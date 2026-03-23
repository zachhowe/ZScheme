namespace ZScript.Compiler.Codegen;

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter(string assemblyName, DiagnosticBag diagnostics, string className = "Program", IReadOnlyList<string>? clrUsings = null, IReadOnlyList<string>? assemblySearchPaths = null, IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null)
{
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths);

    private readonly Dictionary<string, MethodBuilder> _methods = new();
    private readonly Dictionary<string, Type> _userTypes = new();
    private readonly Dictionary<string, Type> _unionCaseTypes = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _unionCasePropertyNames = new();
    private readonly Dictionary<string, MethodBuilder> _unionCaseGetters = new();
    private readonly Dictionary<string, FieldBuilder> _staticFields = new();
    private TypeBuilder? _currentTypeBuilder;
    private ZType? _currentFuncReturnType;
    private int _lambdaId;

    private Type MapToClr(ZType type, IReadOnlyDictionary<string, Type>? typeParamMap = null)
        => IlTypeMapper.MapToClr(type, _userTypes, typeParamMap);

    private Type MapReturnTypeToClr(ZType type)
        => IlTypeMapper.MapReturnTypeToClr(type, _userTypes);

    /// <summary>
    /// Safely resolves a method on a type that may be a generic instantiation containing TypeBuilder args.
    /// </summary>
    private static MethodInfo? SafeGetMethod(Type type, string name, Type[]? paramTypes = null)
    {
        try
        {
            return paramTypes is not null ? type.GetMethod(name, paramTypes) : type.GetMethod(name);
        }
        catch (NotSupportedException) when (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var openType = type.GetGenericTypeDefinition();
            MethodInfo? openMethod;
            if (paramTypes is not null)
            {
                // Match by name and param count since exact type matching won't work on open generics
                openMethod = openType.GetMethods()
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == paramTypes.Length);
            }
            else
            {
                openMethod = openType.GetMethod(name);
            }
            return openMethod is not null ? TypeBuilder.GetMethod(type, openMethod) : null;
        }
    }

    private static MethodInfo? SafeGetMethod(Type type, string name, BindingFlags bindingFlags)
    {
        try
        {
            return type.GetMethod(name, bindingFlags);
        }
        catch (NotSupportedException) when (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var openType = type.GetGenericTypeDefinition();
            var openMethod = openType.GetMethod(name, bindingFlags);
            return openMethod is not null ? TypeBuilder.GetMethod(type, openMethod) : null;
        }
    }

    private static PropertyInfo? SafeGetProperty(Type type, string name)
    {
        try
        {
            return type.GetProperty(name);
        }
        catch (NotSupportedException) when (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            // Can't resolve properties on TypeBuilderInstantiation — caller should handle this
            return null;
        }
    }

    public byte[]? Emit(IrNode node)
    {
        var asmName = new AssemblyName(assemblyName);
        var coreAssembly = Assembly.Load("System.Runtime");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, coreAssembly);
        var moduleBuilder = asmBuilder.DefineDynamicModule(assemblyName);
        var typeBuilder = moduleBuilder.DefineType(
            $"{assemblyName}.{className}",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        _currentTypeBuilder = typeBuilder;

        var mainStatements = new List<IrNode>();

        // Pass 0: define types and functions from imported modules
        // Types must be defined first so the main module can reference them.
        // Functions must be defined before the main module emits function bodies
        // that call stdlib functions.
        var importedModuleTypes = new List<TypeBuilder>();
        if (importedModules is { Count: > 0 })
        {
            // First: define all types from imported modules
            foreach (var (_, defs) in importedModules)
            {
                foreach (var def in defs)
                {
                    if (def is IrNode.RecordDecl or IrNode.UnionDecl)
                        DefineTypeDecl(def, moduleBuilder);
                }
            }

            // Then: emit imported module functions
            foreach (var (moduleClassName, defs) in importedModules)
            {
                var moduleType = moduleBuilder.DefineType(
                    $"{assemblyName}.{moduleClassName}",
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                importedModuleTypes.Add(moduleType);

                foreach (var def in defs)
                {
                    if (def is IrNode.FuncDef func)
                        EmitFuncDef(func, moduleType);
                }
            }
        }

        if (node is IrNode.Seq seq)
        {
            // First pass: define type declarations
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.RecordDecl or IrNode.UnionDecl)
                    DefineTypeDecl(child, moduleBuilder);
            }

            // Second pass: define static fields for top-level Let bindings
            // (must happen before emitting functions that reference them)
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.Let let)
                {
                    var fieldType = MapToClr(let.Value.Type);
                    var fb = typeBuilder.DefineField(let.VarName, fieldType,
                        FieldAttributes.Public | FieldAttributes.Static);
                    _staticFields[let.VarName] = fb;
                }
            }

            // Third pass: emit functions, tracking user-defined main
            MethodBuilder? userMainMethod = null;
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.FuncDef func)
                {
                    EmitFuncDef(func, typeBuilder);
                    if (func.Name == "main")
                        userMainMethod = _methods["main"];
                }
            }

            // Fourth pass: collect top-level statements for static constructor
            foreach (var child in seq.Nodes)
                CollectTopLevel(child, mainStatements);
        }
        else if (node is IrNode.FuncDef singleFunc)
        {
            EmitFuncDef(singleFunc, typeBuilder);
        }
        else
        {
            CollectTopLevel(node, mainStatements);
        }

        // Emit static constructor (.cctor) if there are top-level statements
        if (mainStatements.Count > 0)
        {
            var cctor = typeBuilder.DefineConstructor(
                MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                Type.EmptyTypes);
            var cctorIl = cctor.GetILGenerator();
            var locals = new Dictionary<string, LocalBuilder>();
            foreach (var stmt in mainStatements)
            {
                if (stmt is IrNode.Let let)
                {
                    // Emit value and store in static field
                    EmitNode(let.Value, cctorIl, [], locals);
                    cctorIl.Emit(OpCodes.Stsfld, _staticFields[let.VarName]);
                    // Also set up a local alias for subsequent statements in .cctor
                    var local = cctorIl.DeclareLocal(MapToClr(let.Value.Type));
                    cctorIl.Emit(OpCodes.Ldsfld, _staticFields[let.VarName]);
                    cctorIl.Emit(OpCodes.Stloc, local);
                    locals[let.VarName] = local;
                    // Emit body if non-unit
                    if (let.Body is not IrNode.UnitConst)
                    {
                        EmitNode(let.Body, cctorIl, [], locals);
                        if (let.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                            cctorIl.Emit(OpCodes.Pop);
                    }
                }
                else
                {
                    EmitNode(stmt, cctorIl, [], locals);
                    // Pop return value if non-void
                    if (stmt.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        cctorIl.Emit(OpCodes.Pop);
                }
            }
            cctorIl.Emit(OpCodes.Ret);
        }

        // Emit Main(string[] args) wrapper if user defined a main function
        MethodBuilder? mainMethod = null;
        if (node is IrNode.Seq seq2)
        {
            MethodBuilder? userMain = null;
            foreach (var child in seq2.Nodes)
            {
                if (child is IrNode.FuncDef { Name: "main" })
                {
                    userMain = _methods["main"];
                    break;
                }
            }
            if (userMain is not null)
            {
                mainMethod = typeBuilder.DefineMethod("Main",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(int), [typeof(string[])]);
                var mainIl = mainMethod.GetILGenerator();
                // Convert string[] args to ImmutableList<string>
                var createMethod = typeof(ImmutableList).GetMethods()
                    .First(m => m.Name == "Create"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters() is [{ ParameterType.IsArray: true }])
                    .MakeGenericMethod(typeof(string));
                mainIl.Emit(OpCodes.Ldarg_0);
                mainIl.Emit(OpCodes.Call, createMethod);
                mainIl.Emit(OpCodes.Call, userMain);
                mainIl.Emit(OpCodes.Ret);
                HasEntryPoint = true;
            }
        }

        typeBuilder.CreateType();

        // Finalize imported module classes
        foreach (var moduleType in importedModuleTypes)
            moduleType.CreateType();

        if (mainMethod is not null)
        {
            // Build exe with entry point
            var metadataBuilder = asmBuilder.GenerateMetadata(out var ilStream, out var fieldData);
            int rowNumber = mainMethod.MetadataToken & 0x00FFFFFF;
            var entryPointHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var peBuilder = new ManagedPEBuilder(
                new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage),
                new MetadataRootBuilder(metadataBuilder),
                ilStream,
                entryPoint: entryPointHandle);
            var blobBuilder = new BlobBuilder();
            peBuilder.Serialize(blobBuilder);
            using var ms = new MemoryStream();
            blobBuilder.WriteContentTo(ms);
            return ms.ToArray();
        }
        else
        {
            // Save as dll (no entry point)
            using var ms = new MemoryStream();
            asmBuilder.Save(ms);
            return ms.ToArray();
        }
    }

    private static void CollectTopLevel(IrNode node, List<IrNode> mainStatements)
    {
        switch (node)
        {
            case IrNode.FuncDef:
            case IrNode.RecordDecl:
            case IrNode.UnionDecl:
                break;
            case IrNode.Let let:
                mainStatements.Add(let);
                break;
            case IrNode.ClrCall:
            case IrNode.Call:
                mainStatements.Add(node);
                break;
        }
    }

    private void DefineTypeDecl(IrNode node, ModuleBuilder module)
    {
        switch (node)
        {
            case IrNode.RecordDecl record:
                DefineRecordType(record, module);
                break;
            case IrNode.UnionDecl union:
                DefineUnionType(union, module);
                break;
        }
    }

    private void DefineRecordType(IrNode.RecordDecl record, ModuleBuilder module)
    {
        var typeBuilder = module.DefineType(
            $"{assemblyName}.{record.Name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

        // Register early so self-referential types can resolve
        _userTypes[record.Name] = typeBuilder;

        GenericTypeParameterBuilder[]? genericParams = null;
        Dictionary<string, Type>? typeParamMap = null;
        if (record.TypeParams.Count > 0)
        {
            genericParams = typeBuilder.DefineGenericParameters(record.TypeParams.ToArray());
            typeParamMap = new Dictionary<string, Type>();
            for (int i = 0; i < record.TypeParams.Count; i++)
                typeParamMap[record.TypeParams[i]] = genericParams[i];
        }

        var fieldBuilders = new List<(FieldBuilder Field, PropertyBuilder Prop)>();

        // Define backing fields and properties
        foreach (var field in record.Fields)
        {
            var fieldClrType = MapToClr(field.Type, typeParamMap);

            var fb = typeBuilder.DefineField($"<{field.Name}>k__BackingField", fieldClrType, FieldAttributes.Private | FieldAttributes.InitOnly);

            var pb = typeBuilder.DefineProperty(field.Name, PropertyAttributes.None, fieldClrType, null);
            var getter = typeBuilder.DefineMethod($"get_{field.Name}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                fieldClrType, Type.EmptyTypes);
            var getIl = getter.GetILGenerator();
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, fb);
            getIl.Emit(OpCodes.Ret);
            pb.SetGetMethod(getter);

            fieldBuilders.Add((fb, pb));
        }

        // Define constructor
        var ctorParamTypes = record.Fields.Select(f => MapToClr(f.Type, typeParamMap)).ToArray();
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, ctorParamTypes);

        for (int i = 0; i < record.Fields.Count; i++)
            ctor.DefineParameter(i + 1, ParameterAttributes.None, record.Fields[i].Name);

        var ctorIl = ctor.GetILGenerator();
        // Call base constructor
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        // Set fields
        for (int i = 0; i < fieldBuilders.Count; i++)
        {
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fieldBuilders[i].Field);
        }
        ctorIl.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
    }

    private void DefineUnionType(IrNode.UnionDecl union, ModuleBuilder module)
    {
        // Define abstract base type
        var baseType = module.DefineType(
            $"{assemblyName}.{union.Name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);

        GenericTypeParameterBuilder[]? baseGenericParams = null;
        if (union.TypeParams.Count > 0)
            baseGenericParams = baseType.DefineGenericParameters(union.TypeParams.ToArray());

        // Base constructor
        var baseCtor = baseType.DefineConstructor(
            MethodAttributes.Family, CallingConventions.Standard, Type.EmptyTypes);
        var baseCtorIl = baseCtor.GetILGenerator();
        baseCtorIl.Emit(OpCodes.Ldarg_0);
        baseCtorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        baseCtorIl.Emit(OpCodes.Ret);

        _userTypes[union.Name] = baseType;

        // Define top-level case types
        foreach (var @case in union.Cases)
        {
            var caseType = module.DefineType($"{assemblyName}.{@case.Name}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

            GenericTypeParameterBuilder[]? caseGenericParams = null;
            Dictionary<string, Type>? typeParamMap = null;

            if (union.TypeParams.Count > 0)
            {
                caseGenericParams = caseType.DefineGenericParameters(union.TypeParams.ToArray());
                typeParamMap = new Dictionary<string, Type>();
                for (int i = 0; i < union.TypeParams.Count; i++)
                    typeParamMap[union.TypeParams[i]] = caseGenericParams[i];

                // Set parent to closed base type using case's own generic params
                var closedBaseType = baseType.MakeGenericType(caseGenericParams.Cast<Type>().ToArray());
                caseType.SetParent(closedBaseType);
            }
            else
            {
                caseType.SetParent(baseType);
            }

            var caseFieldBuilders = new List<FieldBuilder>();

            foreach (var field in @case.Fields)
            {
                var fieldClrType = MapToClr(field.Type, typeParamMap);
                var fb = caseType.DefineField($"<{field.Name}>k__BackingField", fieldClrType, FieldAttributes.Private | FieldAttributes.InitOnly);

                var pb = caseType.DefineProperty(field.Name, PropertyAttributes.None, fieldClrType, null);
                var getter = caseType.DefineMethod($"get_{field.Name}",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    fieldClrType, Type.EmptyTypes);
                var getIl = getter.GetILGenerator();
                getIl.Emit(OpCodes.Ldarg_0);
                getIl.Emit(OpCodes.Ldfld, fb);
                getIl.Emit(OpCodes.Ret);
                pb.SetGetMethod(getter);

                // Store getter for later use (avoids reflection on TypeBuilders with generic parents)
                _unionCaseGetters[$"{union.Name}.{@case.Name}.{field.Name}"] = getter;

                caseFieldBuilders.Add(fb);
            }

            // Case constructor
            var caseCtorParams = @case.Fields.Select(f => MapToClr(f.Type, typeParamMap)).ToArray();
            var caseCtor = caseType.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, caseCtorParams);

            for (int i = 0; i < @case.Fields.Count; i++)
                caseCtor.DefineParameter(i + 1, ParameterAttributes.None, @case.Fields[i].Name);

            var caseCtorIl = caseCtor.GetILGenerator();
            caseCtorIl.Emit(OpCodes.Ldarg_0);

            // Call base constructor — for generic types, use TypeBuilder.GetConstructor
            if (caseGenericParams is not null)
            {
                var closedBaseType = baseType.MakeGenericType(caseGenericParams.Cast<Type>().ToArray());
                var closedBaseCtor = TypeBuilder.GetConstructor(closedBaseType, baseCtor);
                caseCtorIl.Emit(OpCodes.Call, closedBaseCtor);
            }
            else
            {
                caseCtorIl.Emit(OpCodes.Call, baseCtor);
            }

            for (int i = 0; i < caseFieldBuilders.Count; i++)
            {
                caseCtorIl.Emit(OpCodes.Ldarg_0);
                caseCtorIl.Emit(OpCodes.Ldarg, i + 1);
                caseCtorIl.Emit(OpCodes.Stfld, caseFieldBuilders[i]);
            }
            caseCtorIl.Emit(OpCodes.Ret);

            caseType.CreateType();
            _unionCaseTypes[$"{union.Name}.{@case.Name}"] = caseType;
            _unionCasePropertyNames[$"{union.Name}.{@case.Name}"] = @case.Fields.Select(f => f.Name).ToList();
        }

        baseType.CreateType();
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeBuilder typeBuilder)
    {
        var paramTypes = func.Params.Select(p => MapToClr(p.Type)).ToArray();

        // For async functions, wrap the return type in Task<T> or Task
        Type returnType;
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                returnType = typeof(System.Threading.Tasks.Task);
            else
                returnType = typeof(System.Threading.Tasks.Task<>)
                    .MakeGenericType(MapToClr(func.ReturnType));
        }
        else
        {
            returnType = MapReturnTypeToClr(func.ReturnType);
        }

        var methodBuilder = typeBuilder.DefineMethod(
            func.Name,
            MethodAttributes.Public | MethodAttributes.Static,
            returnType,
            paramTypes);

        _methods[func.Name] = methodBuilder;

        // Name parameters
        for (int i = 0; i < func.Params.Count; i++)
            methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, func.Params[i].Name);

        var il = methodBuilder.GetILGenerator();
        var locals = new Dictionary<string, LocalBuilder>();

        _currentFuncReturnType = func.ReturnType;
        EmitNode(func.Body, il, func.Params, locals);
        _currentFuncReturnType = null;

        // For async functions, wrap the body result in Task
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            {
                // Body may have left a non-unit value on the stack; pop it
                if (func.Body.Type is not null
                    and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    il.Emit(OpCodes.Pop);

                // Return Task.CompletedTask
                var completedTaskGetter = typeof(System.Threading.Tasks.Task)
                    .GetProperty("CompletedTask")!.GetGetMethod()!;
                il.Emit(OpCodes.Call, completedTaskGetter);
            }
            else
            {
                // Wrap with Task.FromResult<T>(value)
                var innerClrType = MapToClr(func.ReturnType);
                var fromResult = typeof(System.Threading.Tasks.Task)
                    .GetMethod("FromResult")!
                    .MakeGenericMethod(innerClrType);
                il.Emit(OpCodes.Call, fromResult);
            }
        }

        il.Emit(OpCodes.Ret);
    }

    private void EmitAwait(IrNode.Await awaitNode, ILGenerator il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        // Emit the task expression (pushes Task<T> or Task on stack)
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Resolve GetAwaiter() and GetResult() via reflection on the CLR task type
        var taskClrType = MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        var awaiterType = getAwaiterMethod.ReturnType;
        var getResultMethod = awaiterType.GetMethod("GetResult", Type.EmptyTypes)!;

        // Call GetAwaiter() on the Task (reference type)
        il.Emit(OpCodes.Call, getAwaiterMethod);

        // TaskAwaiter is a struct — store in local and load address for instance method call
        var awaiterLocal = il.DeclareLocal(awaiterType);
        il.Emit(OpCodes.Stloc, awaiterLocal);
        il.Emit(OpCodes.Ldloca, awaiterLocal);

        // Call GetResult() — returns T for Task<T>, void for non-generic Task
        il.Emit(OpCodes.Call, getResultMethod);
    }

    private void EmitNode(IrNode node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        switch (node)
        {
            case IrNode.IntConst n:
                il.Emit(OpCodes.Ldc_I4, n.Value);
                break;

            case IrNode.FloatConst n:
                il.Emit(OpCodes.Ldc_R4, n.Value);
                break;

            case IrNode.BoolConst n:
                il.Emit(n.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;

            case IrNode.StringConst n:
                il.Emit(OpCodes.Ldstr, n.Value);
                break;

            case IrNode.UnitConst:
                break;

            case IrNode.Var v:
                EmitLoadVar(v.Name, il, outerParams, locals);
                break;

            case IrNode.BinOp binop:
                EmitNode(binop.Left, il, outerParams, locals);
                EmitNode(binop.Right, il, outerParams, locals);
                EmitBinaryOp(binop.Op, binop.Left.Type, il);
                break;

            case IrNode.UnaryOp unary:
                EmitNode(unary.Operand, il, outerParams, locals);
                EmitUnaryOp(unary.Op, il);
                break;

            case IrNode.If @if:
                var elseLabel = il.DefineLabel();
                var endLabel = il.DefineLabel();
                EmitNode(@if.Condition, il, outerParams, locals);
                il.Emit(OpCodes.Brfalse, elseLabel);
                EmitNode(@if.Then, il, outerParams, locals);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(elseLabel);
                EmitNode(@if.Else, il, outerParams, locals);
                il.MarkLabel(endLabel);
                break;

            case IrNode.Let let:
                var local = il.DeclareLocal(MapToClr(let.Value.Type));
                EmitNode(let.Value, il, outerParams, locals);
                il.Emit(OpCodes.Stloc, local);
                locals[let.VarName] = local;
                EmitNode(let.Body, il, outerParams, locals);
                break;

            case IrNode.ClrCall clrCall:
                EmitClrCall(clrCall, il, outerParams, locals);
                break;

            case IrNode.Call call:
                EmitCall(call, il, outerParams, locals);
                break;

            case IrNode.Match match:
                EmitMatch(match, il, outerParams, locals);
                break;

            case IrNode.TryCatch tryCatch:
                EmitTryCatch(tryCatch, il, outerParams, locals);
                break;

            case IrNode.ClrNew clrNew:
                EmitClrNew(clrNew, il, outerParams, locals);
                break;

            case IrNode.Propagate propagate:
                EmitPropagate(propagate, il, outerParams, locals);
                break;

            case IrNode.Throw @throw:
                EmitNode(@throw.Expr, il, outerParams, locals);
                il.Emit(OpCodes.Throw);
                break;

            case IrNode.MethodCall methodCall:
                EmitMethodCall(methodCall, il, outerParams, locals);
                break;

            case IrNode.ListNew listNew:
                EmitImmutableCollectionNew(listNew.Elements, listNew.Type,
                    typeof(ImmutableList), "Create", il, outerParams, locals);
                break;

            case IrNode.VectorNew vectorNew:
                EmitImmutableCollectionNew(vectorNew.Elements, vectorNew.Type,
                    typeof(ImmutableArray), "Create", il, outerParams, locals);
                break;

            case IrNode.MapNew mapNew:
                EmitMapNew(mapNew, il, outerParams, locals);
                break;

            case IrNode.FuncDef funcDef:
                EmitLambda(funcDef, il, outerParams, locals);
                break;

            case IrNode.RecordNew recordNew:
                EmitRecordNew(recordNew, il, outerParams, locals);
                break;

            case IrNode.FieldGet fieldGet:
                EmitFieldGet(fieldGet, il, outerParams, locals);
                break;

            case IrNode.UnionCaseNew unionCaseNew:
                EmitUnionCaseNew(unionCaseNew, il, outerParams, locals);
                break;

            case IrNode.Await awaitNode:
                EmitAwait(awaitNode, il, outerParams, locals);
                break;

            default:
                diagnostics.Error($"IL emission not implemented for {node.GetType().Name}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0); // push something on the stack
                break;
        }
    }

    private void EmitClrNew(IrNode.ClrNew clrNew, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit arguments
        foreach (var arg in clrNew.Args)
            EmitNode(arg, il, outerParams, locals);

        var type = _clrInterop.FindType(clrNew.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrNew.QualifiedTypeName}' not found", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var argTypes = clrNew.Args.Select(a => MapToClr(a.Type)).ToArray();
        var ctor = type.GetConstructor(argTypes);
        if (ctor is null)
        {
            // Fallback: match by arg count
            ctor = type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == argTypes.Length);
        }

        if (ctor is null)
        {
            diagnostics.Error($"No constructor on '{clrNew.QualifiedTypeName}' matches the given arguments", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        il.Emit(OpCodes.Newobj, ctor);
    }

    private void EmitClrCall(IrNode.ClrCall clrCall, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit arguments
        foreach (var arg in clrCall.Args)
            EmitNode(arg, il, outerParams, locals);

        // Resolve the CLR method — search loaded assemblies since Type.GetType
        // only finds types in the calling assembly or System.Private.CoreLib
        var type = _clrInterop.FindType(clrCall.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrCall.QualifiedTypeName}' not found", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var argTypes = clrCall.Args.Select(a => MapToClr(a.Type)).ToArray();

        MethodInfo? method;
        if (clrCall.GenericArity > 0)
        {
            var generic = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == clrCall.MethodName
                                  && m.IsGenericMethodDefinition
                                  && m.GetGenericArguments().Length == clrCall.GenericArity
                                  && m.GetParameters().Length == argTypes.Length);
            if (generic is not null)
            {
                var typeArgs = InferGenericTypeArgs(generic, argTypes);
                method = generic.MakeGenericMethod(typeArgs);
            }
            else
            {
                method = null;
            }
        }
        else
        {
            method = type.GetMethod(clrCall.MethodName, argTypes);
        }

        if (method is null)
        {
            diagnostics.Error($"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' not found", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        il.Emit(OpCodes.Call, method);
    }

    private static Type[] InferGenericTypeArgs(MethodInfo genericMethod, Type[] argTypes)
    {
        var genericParams = genericMethod.GetGenericArguments();
        var methodParams = genericMethod.GetParameters();
        var result = new Type[genericParams.Length];

        for (int i = 0; i < methodParams.Length && i < argTypes.Length; i++)
        {
            var paramType = methodParams[i].ParameterType;
            if (paramType.IsGenericParameter)
            {
                result[paramType.GenericParameterPosition] = argTypes[i];
            }
        }

        for (int i = 0; i < result.Length; i++)
            result[i] ??= typeof(object);

        return result;
    }

    private void EmitCall(IrNode.Call call, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        if (call.Function is IrNode.Var v)
        {
            // Check if it's a known static method
            if (_methods.TryGetValue(v.Name, out var methodBuilder))
            {
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                il.Emit(OpCodes.Call, methodBuilder);
                return;
            }

            // Check if it's a delegate stored in a local or parameter (lambda/partial)
            if (locals.TryGetValue(v.Name, out var delegateLocal))
            {
                il.Emit(OpCodes.Ldloc, delegateLocal);
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                var invokeMethod = SafeGetMethod(delegateLocal.LocalType, "Invoke")!;
                il.Emit(OpCodes.Callvirt, invokeMethod);
                return;
            }

            // Check parameters for delegate
            for (int i = 0; i < outerParams.Count; i++)
            {
                if (outerParams[i].Name == v.Name && outerParams[i].Type is ZType.ZFuncType)
                {
                    il.Emit(OpCodes.Ldarg, i);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    var delegateType = MapToClr(outerParams[i].Type);
                    var invokeMethod = SafeGetMethod(delegateType, "Invoke")!;
                    il.Emit(OpCodes.Callvirt, invokeMethod);
                    return;
                }
            }

            // Check static fields for delegate (top-level Let bindings)
            if (_staticFields.TryGetValue(v.Name, out var staticField))
            {
                var fieldInvokeMethod = SafeGetMethod(staticField.FieldType, "Invoke");
                if (fieldInvokeMethod is not null)
                {
                    il.Emit(OpCodes.Ldsfld, staticField);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    il.Emit(OpCodes.Callvirt, fieldInvokeMethod);
                    return;
                }
            }

            diagnostics.Error($"Function '{v.Name}' not found for IL emission", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Non-Var function target: emit the function expression, then invoke
        EmitNode(call.Function, il, outerParams, locals);
        foreach (var arg in call.Args)
            EmitNode(arg, il, outerParams, locals);
        var funcType = MapToClr(call.Function.Type);
        var invoke = SafeGetMethod(funcType, "Invoke");
        if (invoke is not null)
        {
            il.Emit(OpCodes.Callvirt, invoke);
            return;
        }

        diagnostics.Error($"IL emission not implemented for Call with {call.Function.GetType().Name} target", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitMatch(IrNode.Match match, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Store scrutinee in a local
        var scrutineeType = MapToClr(match.Scrutinee.Type);
        var scrutineeLocal = il.DeclareLocal(scrutineeType);
        EmitNode(match.Scrutinee, il, outerParams, locals);
        il.Emit(OpCodes.Stloc, scrutineeLocal);

        var endLabel = il.DefineLabel();
        var armLabels = new Label[match.Arms.Count];
        for (int i = 0; i < match.Arms.Count; i++)
            armLabels[i] = il.DefineLabel();

        // Create a "next arm" label for each arm (the label of the following arm, or a fail label)
        var failLabel = il.DefineLabel();

        for (int i = 0; i < match.Arms.Count; i++)
        {
            il.MarkLabel(armLabels[i]);
            var arm = match.Arms[i];
            var nextLabel = i + 1 < match.Arms.Count ? armLabels[i + 1] : failLabel;

            EmitPatternTest(arm.Pattern, scrutineeLocal, match.Scrutinee.Type, nextLabel, il, outerParams, locals);
            EmitNode(arm.Body, il, outerParams, locals);
            il.Emit(OpCodes.Br, endLabel);
        }

        // Fail: throw InvalidOperationException
        il.MarkLabel(failLabel);
        il.Emit(OpCodes.Ldstr, "Non-exhaustive match");
        var exCtor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        il.Emit(OpCodes.Newobj, exCtor);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(endLabel);
    }

    private void EmitPatternTest(IrPattern pattern, LocalBuilder scrutineeLocal, ZType scrutineeType,
        Label failLabel, ILGenerator il, IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                // Always matches — no test needed
                break;

            case IrPattern.Variable v:
                // Bind scrutinee to a new local
                var bindLocal = il.DeclareLocal(scrutineeLocal.LocalType);
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Stloc, bindLocal);
                locals[v.Name] = bindLocal;
                break;

            case IrPattern.Literal { Value: string s }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Ldstr, s);
                var strEquals = typeof(string).GetMethod("Equals", BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)])!;
                il.Emit(OpCodes.Call, strEquals);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: int i }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: bool b }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Constructor c:
                EmitConstructorPatternTest(c, scrutineeLocal, scrutineeType, failLabel, il, outerParams, locals);
                break;
        }
    }

    private void EmitConstructorPatternTest(IrPattern.Constructor ctor, LocalBuilder scrutineeLocal,
        ZType scrutineeType, Label failLabel, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Resolve the CLR type for this constructor case
        var caseType = ResolveConstructorCaseType(ctor.Name, scrutineeType);
        if (caseType is null)
        {
            diagnostics.Error($"Cannot resolve constructor type '{ctor.Name}' for pattern match", SourceSpan.None);
            return;
        }

        // isinst type test
        il.Emit(OpCodes.Ldloc, scrutineeLocal);
        il.Emit(OpCodes.Isinst, caseType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, failLabel);

        // Store the cast result
        var castLocal = il.DeclareLocal(caseType);
        il.Emit(OpCodes.Stloc, castLocal);

        // Extract fields
        if (ctor.Fields.Count > 0)
        {
            // Resolve case key and get property names from stored metadata
            string? caseKey = null;
            if (scrutineeType is ZType.ZNamedType named)
                caseKey = $"{named.Name}.{ctor.Name}";

            List<string> propertyNames;
            if (caseKey is not null && _unionCasePropertyNames.TryGetValue(caseKey, out var storedNames))
            {
                propertyNames = storedNames.ToList();
            }
            else
            {
                // Fallback: try reflection on the open type
                var openCaseType = caseType is TypeBuilder tb ? tb
                    : caseType.IsGenericType && !caseType.IsGenericTypeDefinition
                        ? caseType.GetGenericTypeDefinition()
                        : caseType;
                try
                {
                    propertyNames = openCaseType
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Select(p => p.Name)
                        .ToList();
                }
                catch (NotSupportedException)
                {
                    propertyNames = Enumerable.Range(0, ctor.Fields.Count).Select(_ => "Value").ToList();
                }
            }
            if (propertyNames.Count == 0)
                propertyNames = Enumerable.Range(0, ctor.Fields.Count).Select(_ => "Value").ToList();

            for (int i = 0; i < ctor.Fields.Count; i++)
            {
                var field = ctor.Fields[i];
                if (field is IrPattern.Variable v)
                {
                    var propName = i < propertyNames.Count ? propertyNames[i] : "Value";

                    // Resolve getter — use stored MethodBuilder when available
                    MethodInfo? getter = null;
                    var getterKey = caseKey is not null ? $"{caseKey}.{propName}" : null;
                    if (getterKey is not null && _unionCaseGetters.TryGetValue(getterKey, out var openGetter))
                    {
                        if (caseType.IsGenericType && !caseType.IsGenericTypeDefinition)
                            getter = TypeBuilder.GetMethod(caseType, openGetter);
                        else
                            getter = openGetter;
                    }
                    else
                    {
                        // Fallback for non-union types
                        var prop = caseType.GetProperty(propName);
                        if (prop is not null)
                            getter = prop.GetGetMethod()!;
                    }
                    if (getter is null) continue;

                    var fieldLocal = il.DeclareLocal(getter.ReturnType);
                    il.Emit(OpCodes.Ldloc, castLocal);
                    il.Emit(OpCodes.Callvirt, getter);
                    il.Emit(OpCodes.Stloc, fieldLocal);
                    locals[v.Name] = fieldLocal;
                }
                else if (field is IrPattern.Wildcard)
                {
                    // Ignore
                }
            }
        }
        else
        {
            // No fields to extract — pop the dup'd reference we left on stack
            il.Emit(OpCodes.Pop);
        }
    }

    private Type? ResolveConstructorCaseType(string caseName, ZType scrutineeType)
    {
        if (scrutineeType is ZType.ZNamedType named)
        {
            var caseKey = $"{named.Name}.{caseName}";
            if (_unionCaseTypes.TryGetValue(caseKey, out var caseType))
            {
                // Close generic type if needed
                if (named.TypeArgs.Count > 0 && caseType.IsGenericTypeDefinition)
                {
                    var typeArgs = named.TypeArgs.Select(a => MapToClr(a)).ToArray();
                    return caseType.MakeGenericType(typeArgs);
                }
                return caseType;
            }
        }
        return null;
    }

    private void EmitTryCatch(IrNode.TryCatch node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Extract Ok/Err types from the Result type
        Type okClrType, errClrType, resultClrType;
        if (node.Type is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            okClrType = MapToClr(okT);
            errClrType = MapToClr(errT);
            resultClrType = MapToClr(node.Type);
        }
        else
        {
            diagnostics.Error("TryCatch node type is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Declare a local to hold the result (can't leave values on stack across exception boundaries)
        var resultLocal = il.DeclareLocal(resultClrType);

        // Resolve Ok and Err types from _unionCaseTypes
        if (!_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseType) ||
            !_unionCaseTypes.TryGetValue("Result.Err", out var errCaseType))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Close generic types and get constructors
        Type closedOkType, closedErrType;
        ConstructorInfo okCtor, errCtor;

        if (okCaseType.IsGenericTypeDefinition)
        {
            closedOkType = okCaseType.MakeGenericType(okClrType, errClrType);
            closedErrType = errCaseType.MakeGenericType(okClrType, errClrType);
            var openOkCtor = okCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
            var openErrCtor = errCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
            okCtor = TypeBuilder.GetConstructor(closedOkType, openOkCtor);
            errCtor = TypeBuilder.GetConstructor(closedErrType, openErrCtor);
        }
        else
        {
            closedOkType = okCaseType;
            closedErrType = errCaseType;
            okCtor = okCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
            errCtor = errCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
        }

        // begin try
        il.BeginExceptionBlock();

        // Emit body — evaluates to the "ok" value
        EmitNode(node.Body, il, outerParams, locals);

        // Wrap in Ok
        il.Emit(OpCodes.Newobj, okCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // begin catch (Exception)
        il.BeginCatchBlock(typeof(Exception));

        // Stack has the Exception; get its Message
        var getMessage = typeof(Exception).GetProperty("Message")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, getMessage);

        // Create ErrorInfo(message, None<ErrorInfo>())
        if (_userTypes.TryGetValue("ErrorInfo", out var errorInfoType) &&
            _unionCaseTypes.TryGetValue("Option.None", out var noneCaseType))
        {
            // new None<ErrorInfo>()
            ConstructorInfo noneCtor;
            if (noneCaseType.IsGenericTypeDefinition)
            {
                var closedNoneType = noneCaseType.MakeGenericType(errorInfoType);
                var openNoneCtor = noneCaseType.GetConstructors().First(c => c.GetParameters().Length == 0);
                noneCtor = TypeBuilder.GetConstructor(closedNoneType, openNoneCtor);
            }
            else
            {
                noneCtor = noneCaseType.GetConstructors().First(c => c.GetParameters().Length == 0);
            }
            il.Emit(OpCodes.Newobj, noneCtor);

            // new ErrorInfo(message, noneInstance)
            var errorInfoCtor = errorInfoType.GetConstructors().First(c => c.GetParameters().Length == 2);
            il.Emit(OpCodes.Newobj, errorInfoCtor);
        }
        else
        {
            // Fallback: if ErrorInfo/Option types not available, push null placeholder
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
        }

        // new Err(errorInfo)
        il.Emit(OpCodes.Newobj, errCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // end
        il.EndExceptionBlock();

        // Load the result
        il.Emit(OpCodes.Ldloc, resultLocal);
    }

    private void EmitPropagate(IrNode.Propagate node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit inner expression (should evaluate to a Result value)
        EmitNode(node.Expr, il, outerParams, locals);

        var resultClrType = MapToClr(node.ResultType);
        var tempLocal = il.DeclareLocal(resultClrType);
        il.Emit(OpCodes.Stloc, tempLocal);

        // Resolve the Err type for the inner result
        Type innerOkClrType, innerErrClrType;
        if (node.ResultType is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            innerOkClrType = MapToClr(okT);
            innerErrClrType = MapToClr(errT);
        }
        else
        {
            diagnostics.Error("Propagate expression is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        if (!_unionCaseTypes.TryGetValue("Result.Err", out var errCaseType) ||
            !_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseType))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for Propagate", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        Type closedErrType, closedOkType;
        MethodInfo errPropGetter, okValueGetter;

        if (errCaseType.IsGenericTypeDefinition)
        {
            closedErrType = errCaseType.MakeGenericType(innerOkClrType, innerErrClrType);
            closedOkType = okCaseType.MakeGenericType(innerOkClrType, innerErrClrType);

            var openErrGetter = _unionCaseGetters["Result.Err.error"];
            errPropGetter = TypeBuilder.GetMethod(closedErrType, openErrGetter);

            var openValueGetter = _unionCaseGetters["Result.Ok.value"];
            okValueGetter = TypeBuilder.GetMethod(closedOkType, openValueGetter);
        }
        else
        {
            closedErrType = errCaseType;
            closedOkType = okCaseType;
            errPropGetter = _unionCaseGetters.TryGetValue("Result.Err.error", out var eg) ? eg : errCaseType.GetProperty("error")!.GetGetMethod()!;
            okValueGetter = _unionCaseGetters.TryGetValue("Result.Ok.value", out var og) ? og : okCaseType.GetProperty("value")!.GetGetMethod()!;
        }

        // Test: is it Err?
        var okLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Isinst, closedErrType);
        il.Emit(OpCodes.Brfalse, okLabel);

        // It's Err — extract the error and wrap in the function's return Err type, then early return
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, closedErrType);

        // Get .error property
        il.Emit(OpCodes.Callvirt, errPropGetter);

        // Wrap in the function's return Err type
        if (_currentFuncReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
        {
            var funcOkClr = MapToClr(fOkT);
            var funcErrClr = MapToClr(fErrT);

            ConstructorInfo funcErrCtor;
            if (errCaseType.IsGenericTypeDefinition)
            {
                var funcErrType = errCaseType.MakeGenericType(funcOkClr, funcErrClr);
                var openCtor = errCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
                funcErrCtor = TypeBuilder.GetConstructor(funcErrType, openCtor);
            }
            else
            {
                funcErrCtor = errCaseType.GetConstructors().First(c => c.GetParameters().Length == 1);
            }

            il.Emit(OpCodes.Newobj, funcErrCtor);
        }

        il.Emit(OpCodes.Ret); // Early return

        // Ok path — extract Value
        il.MarkLabel(okLabel);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, closedOkType);
        il.Emit(OpCodes.Callvirt, okValueGetter);
        // Unwrapped value is now on the stack
    }

    private void EmitMethodCall(IrNode.MethodCall node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        var receiverClrType = MapToClr(node.Receiver.Type);

        // For value types, we need the address for instance calls
        var isValueType = receiverClrType.IsValueType;
        LocalBuilder? receiverLocal = null;

        EmitNode(node.Receiver, il, outerParams, locals);

        if (isValueType)
        {
            receiverLocal = il.DeclareLocal(receiverClrType);
            il.Emit(OpCodes.Stloc, receiverLocal);
            il.Emit(OpCodes.Ldloca, receiverLocal);
        }

        if (node.IsProperty)
        {
            var prop = SafeGetProperty(receiverClrType, node.MethodName);
            MethodInfo? getter = null;
            if (prop is not null)
            {
                getter = prop.GetGetMethod()!;
            }
            else
            {
                // Try via SafeGetMethod for get_ accessor on TypeBuilderInstantiation types
                getter = SafeGetMethod(receiverClrType, $"get_{node.MethodName}");
            }
            if (getter is null)
            {
                diagnostics.Error($"Property '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            }
            il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, getter);
            return;
        }

        if (node.IsIndexer)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            var indexer = SafeGetMethod(receiverClrType, "get_Item");
            if (indexer is null)
            {
                diagnostics.Error($"Indexer not found on {receiverClrType}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            }
            il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, indexer);
            return;
        }

        // Regular method call
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var argTypes = node.Args.Select(a => MapToClr(a.Type)).ToArray();
        var method = SafeGetMethod(receiverClrType, node.MethodName, argTypes);
        if (method is null)
        {
            // Fallback: match by name and arg count via SafeGetMethod with BindingFlags
            method = SafeGetMethod(receiverClrType, node.MethodName,
                BindingFlags.Public | BindingFlags.Instance);
            if (method is not null && method.GetParameters().Length != argTypes.Length)
                method = null;
        }
        if (method is null)
        {
            diagnostics.Error($"Method '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }
        il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, method);
    }

    private void EmitImmutableCollectionNew(IReadOnlyList<IrNode> elements, ZType collectionType,
        Type helperClass, string methodName, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Determine element type from the collection's ZType
        Type elementClrType = typeof(object);
        if (collectionType is ZType.ZNamedType { TypeArgs: [var elemT] })
            elementClrType = MapToClr(elemT);

        // Create array and store elements
        il.Emit(OpCodes.Ldc_I4, elements.Count);
        il.Emit(OpCodes.Newarr, elementClrType);

        for (int i = 0; i < elements.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            EmitNode(elements[i], il, outerParams, locals);
            il.Emit(OpCodes.Stelem, elementClrType);
        }

        // Call ImmutableList.Create<T>(T[]) or ImmutableArray.Create<T>(T[])
        var openMethod = helperClass.GetMethods()
            .First(m => m.Name == methodName
                && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType.IsArray: true }]);
        var closedMethod = openMethod.MakeGenericMethod(elementClrType);
        il.Emit(OpCodes.Call, closedMethod);
    }

    private void EmitMapNew(IrNode.MapNew node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Determine key/value types
        Type keyClrType = typeof(object), valueClrType = typeof(object);
        if (node.Type is ZType.ZNamedType { TypeArgs: [var keyT, var valT] })
        {
            keyClrType = MapToClr(keyT);
            valueClrType = MapToClr(valT);
        }

        var kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyClrType, valueClrType);
        var kvpCtor = kvpType.GetConstructor([keyClrType, valueClrType])!;

        // Create array of KeyValuePair<K,V>
        il.Emit(OpCodes.Ldc_I4, node.Entries.Count);
        il.Emit(OpCodes.Newarr, kvpType);

        for (int i = 0; i < node.Entries.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            EmitNode(node.Entries[i].Key, il, outerParams, locals);
            EmitNode(node.Entries[i].Value, il, outerParams, locals);
            il.Emit(OpCodes.Newobj, kvpCtor);
            il.Emit(OpCodes.Stelem, kvpType);
        }

        // Call ImmutableDictionary.CreateRange<K,V>(IEnumerable<KeyValuePair<K,V>>)
        var createRangeMethod = typeof(ImmutableDictionary).GetMethods()
            .First(m => m.Name == "CreateRange"
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 1)
            .MakeGenericMethod(keyClrType, valueClrType);
        il.Emit(OpCodes.Call, createRangeMethod);
    }

    private void EmitLambda(IrNode.FuncDef funcDef, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        var lambdaName = $"__lambda_{_lambdaId++}_{funcDef.Name}";
        var paramNames = funcDef.Params.Select(p => p.Name).ToHashSet();

        // Find free variables captured from the enclosing scope
        var freeVars = FindFreeVars(funcDef.Body, paramNames);

        // Resolve captured variable types from locals/params
        var captures = new List<(string Name, Type ClrType)>();
        foreach (var fv in freeVars)
        {
            if (locals.TryGetValue(fv, out var loc))
                captures.Add((fv, loc.LocalType));
            else
            {
                for (int i = 0; i < outerParams.Count; i++)
                {
                    if (outerParams[i].Name == fv)
                    {
                        captures.Add((fv, MapToClr(outerParams[i].Type)));
                        break;
                    }
                }
            }
        }

        var delegateType = MapToClr(funcDef.Type);

        if (captures.Count == 0)
        {
            // No captures: emit as static method
            EmitFuncDef(funcDef with { Name = lambdaName }, _currentTypeBuilder!);
            var lambdaMethod = _methods[lambdaName];
            var delegateCtor = delegateType.GetConstructors()[0];
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, lambdaMethod);
            il.Emit(OpCodes.Newobj, delegateCtor);
        }
        else
        {
            // Captures: create a closure class with fields for captured variables
            var closureType = _currentTypeBuilder!.DefineNestedType(
                $"<>c__{lambdaName}",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class);

            // Define fields for captured variables
            var captureFields = new List<FieldBuilder>();
            foreach (var (name, clrType) in captures)
            {
                var fb = closureType.DefineField(name, clrType, FieldAttributes.Public);
                captureFields.Add(fb);
            }

            // Define parameterless constructor
            var closureCtor = closureType.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var closureCtorIl = closureCtor.GetILGenerator();
            closureCtorIl.Emit(OpCodes.Ldarg_0);
            closureCtorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            closureCtorIl.Emit(OpCodes.Ret);

            // Define instance method for the lambda body
            var lambdaParamTypes = funcDef.Params.Select(p => MapToClr(p.Type)).ToArray();
            var lambdaReturnType = MapReturnTypeToClr(funcDef.ReturnType);
            var lambdaMethod = closureType.DefineMethod("Invoke",
                MethodAttributes.Public, lambdaReturnType, lambdaParamTypes);

            for (int i = 0; i < funcDef.Params.Count; i++)
                lambdaMethod.DefineParameter(i + 1, ParameterAttributes.None, funcDef.Params[i].Name);

            // Emit lambda body — captured vars come from 'this' fields
            var lambdaIl = lambdaMethod.GetILGenerator();
            var lambdaLocals = new Dictionary<string, LocalBuilder>();

            // Create locals for captured variables, loaded from closure fields
            for (int i = 0; i < captures.Count; i++)
            {
                var captureLocal = lambdaIl.DeclareLocal(captures[i].ClrType);
                lambdaIl.Emit(OpCodes.Ldarg_0); // 'this' (the closure instance)
                lambdaIl.Emit(OpCodes.Ldfld, captureFields[i]);
                lambdaIl.Emit(OpCodes.Stloc, captureLocal);
                lambdaLocals[captures[i].Name] = captureLocal;
            }

            // Params for the lambda body (instance method, so params start at arg 1)
            var instanceParams = funcDef.Params;

            _currentFuncReturnType = funcDef.ReturnType;
            EmitNode(funcDef.Body, lambdaIl, instanceParams, lambdaLocals);
            _currentFuncReturnType = null;
            lambdaIl.Emit(OpCodes.Ret);

            closureType.CreateType();

            // Emit: new closure, set fields, create delegate
            il.Emit(OpCodes.Newobj, closureCtor);
            for (int i = 0; i < captures.Count; i++)
            {
                il.Emit(OpCodes.Dup);
                EmitLoadVar(captures[i].Name, il, outerParams, locals);
                il.Emit(OpCodes.Stfld, captureFields[i]);
            }

            // Create delegate: new Func<...>(closureInstance, lambdaMethod)
            var delegateCtor = delegateType.GetConstructors()[0];
            il.Emit(OpCodes.Ldftn, lambdaMethod);
            il.Emit(OpCodes.Newobj, delegateCtor);
        }
    }

    private static HashSet<string> FindFreeVars(IrNode node, HashSet<string> bound) => node switch
    {
        IrNode.Var v => bound.Contains(v.Name) ? [] : [v.Name],
        IrNode.Let let =>
            Merge(FindFreeVars(let.Value, bound),
                FindFreeVars(let.Body, new HashSet<string>(bound) { let.VarName })),
        IrNode.If @if =>
            Merge(FindFreeVars(@if.Condition, bound),
                Merge(FindFreeVars(@if.Then, bound), FindFreeVars(@if.Else, bound))),
        IrNode.Call call =>
            Merge(FindFreeVars(call.Function, bound),
                call.Args.Aggregate(new HashSet<string>(), (acc, a) => Merge(acc, FindFreeVars(a, bound)))),
        IrNode.BinOp binop =>
            Merge(FindFreeVars(binop.Left, bound), FindFreeVars(binop.Right, bound)),
        IrNode.UnaryOp unary => FindFreeVars(unary.Operand, bound),
        IrNode.FuncDef func =>
            FindFreeVars(func.Body, new HashSet<string>(bound.Concat(func.Params.Select(p => p.Name)))),
        IrNode.Match match =>
            Merge(FindFreeVars(match.Scrutinee, bound),
                match.Arms.Aggregate(new HashSet<string>(), (acc, a) => Merge(acc, FindFreeVars(a.Body, bound)))),
        IrNode.MethodCall mc =>
            Merge(FindFreeVars(mc.Receiver, bound),
                mc.Args.Aggregate(new HashSet<string>(), (acc, a) => Merge(acc, FindFreeVars(a, bound)))),
        _ => []
    };

    private static HashSet<string> Merge(HashSet<string> a, HashSet<string> b)
    {
        var result = new HashSet<string>(a);
        result.UnionWith(b);
        return result;
    }

    private void EmitRecordNew(IrNode.RecordNew node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit field values
        foreach (var (_, value) in node.Fields)
            EmitNode(value, il, outerParams, locals);

        if (_userTypes.TryGetValue(node.TypeName, out var typeBuilder))
        {
            // Get the constructor (matches field order)
            var ctorParams = node.Fields.Select(f => MapToClr(f.Value.Type)).ToArray();
            var ctor = typeBuilder.GetConstructors().FirstOrDefault(c =>
                c.GetParameters().Length == ctorParams.Length);
            if (ctor is not null)
            {
                il.Emit(OpCodes.Newobj, ctor);
                return;
            }
        }

        diagnostics.Error($"Record type '{node.TypeName}' not found for IL emission", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitFieldGet(IrNode.FieldGet node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        EmitNode(node.Record, il, outerParams, locals);
        var recordType = node.Record.Type;
        if (recordType is ZType.ZNamedType named && _userTypes.TryGetValue(named.Name, out var typeBuilder))
        {
            var prop = typeBuilder.GetProperty(node.FieldName);
            if (prop is not null)
            {
                il.Emit(OpCodes.Callvirt, prop.GetGetMethod()!);
                return;
            }
        }

        diagnostics.Error($"Field '{node.FieldName}' not found for IL emission", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitUnionCaseNew(IrNode.UnionCaseNew node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var caseKey = $"{node.UnionName}.{node.CaseName}";
        if (_unionCaseTypes.TryGetValue(caseKey, out var caseType))
        {
            // Close generic type if needed
            if (node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } nt && caseType.IsGenericTypeDefinition)
            {
                var typeArgs = nt.TypeArgs.Select(a => MapToClr(a)).ToArray();
                var closedType = caseType.MakeGenericType(typeArgs);
                var openCtor = caseType.GetConstructors().First(c => c.GetParameters().Length == node.Args.Count);
                var closedCtor = TypeBuilder.GetConstructor(closedType, openCtor);
                il.Emit(OpCodes.Newobj, closedCtor);
                return;
            }

            // Non-generic path
            var ctor = caseType.GetConstructors().FirstOrDefault(c =>
                c.GetParameters().Length == node.Args.Count);
            if (ctor is not null)
            {
                il.Emit(OpCodes.Newobj, ctor);
                return;
            }
        }

        diagnostics.Error($"Union case '{caseKey}' not found for IL emission", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitLoadVar(string name, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Check locals first
        if (locals.TryGetValue(name, out var local))
        {
            il.Emit(OpCodes.Ldloc, local);
            return;
        }

        // Then check parameters
        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
            {
                il.Emit(OpCodes.Ldarg, i);
                return;
            }
        }

        // Then check static fields (top-level Let bindings)
        if (_staticFields.TryGetValue(name, out var field))
        {
            il.Emit(OpCodes.Ldsfld, field);
            return;
        }

        diagnostics.Error($"Variable '{name}' not found for IL emission", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private static void EmitBinaryOp(string op, ZType? leftType, ILGenerator il)
    {
        switch (op)
        {
            case "+" when leftType is ZType.ZPrimitiveType { Kind: PrimitiveKind.String }:
                var concatMethod = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
                il.Emit(OpCodes.Call, concatMethod);
                break;
            case "+": il.Emit(OpCodes.Add); break;
            case "-": il.Emit(OpCodes.Sub); break;
            case "*": il.Emit(OpCodes.Mul); break;
            case "/": il.Emit(OpCodes.Div); break;
            case "%": il.Emit(OpCodes.Rem); break;
            case "=": il.Emit(OpCodes.Ceq); break;
            case "<": il.Emit(OpCodes.Clt); break;
            case ">": il.Emit(OpCodes.Cgt); break;
            case "!=":
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case "<=":
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case ">=":
                il.Emit(OpCodes.Clt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case "and": il.Emit(OpCodes.And); break;
            case "or": il.Emit(OpCodes.Or); break;
        }
    }

    private static void EmitUnaryOp(string op, ILGenerator il)
    {
        switch (op)
        {
            case "not":
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
        }
    }
}

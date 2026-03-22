namespace ZScript.Compiler.Codegen;

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using ZScript.Runtime;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter(string assemblyName, DiagnosticBag diagnostics, string className = "Program", IReadOnlyList<string>? clrUsings = null, IReadOnlyList<string>? assemblySearchPaths = null, IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null)
{
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths);

    private readonly Dictionary<string, MethodBuilder> _methods = new();
    private readonly Dictionary<string, TypeBuilder> _userTypes = new();
    private readonly Dictionary<string, TypeBuilder> _unionCaseTypes = new();
    private readonly Dictionary<string, FieldBuilder> _staticFields = new();
    private TypeBuilder? _currentTypeBuilder;
    private ZType? _currentFuncReturnType;
    private int _lambdaId;

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
                    var fieldType = IlTypeMapper.MapToClr(let.Value.Type);
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
                    var local = cctorIl.DeclareLocal(IlTypeMapper.MapToClr(let.Value.Type));
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
                // Convert string[] args to ZsList<string>
                var zsListType = typeof(ZScript.Runtime.ZsList<string>);
                var fromItemsMethod = zsListType.GetMethod("FromItems", [typeof(ReadOnlySpan<string>)])!;
                mainIl.Emit(OpCodes.Ldarg_0);
                mainIl.Emit(OpCodes.Call, fromItemsMethod);
                mainIl.Emit(OpCodes.Call, userMain);
                mainIl.Emit(OpCodes.Ret);
                HasEntryPoint = true;
            }
        }

        typeBuilder.CreateType();

        // Emit imported module classes as separate types
        if (importedModules is { Count: > 0 })
        {
            foreach (var (moduleClassName, defs) in importedModules)
            {
                var moduleType = moduleBuilder.DefineType(
                    $"{assemblyName}.{moduleClassName}",
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

                foreach (var def in defs)
                {
                    if (def is IrNode.FuncDef func)
                        EmitFuncDef(func, moduleType);
                }

                moduleType.CreateType();
            }
        }

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

        var fieldBuilders = new List<(FieldBuilder Field, PropertyBuilder Prop)>();

        // Define backing fields and properties
        foreach (var field in record.Fields)
        {
            var fieldClrType = IlTypeMapper.MapToClr(field.Type);
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
        var ctorParamTypes = record.Fields.Select(f => IlTypeMapper.MapToClr(f.Type)).ToArray();
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
        _userTypes[record.Name] = typeBuilder;
    }

    private void DefineUnionType(IrNode.UnionDecl union, ModuleBuilder module)
    {
        // Define abstract base type
        var baseType = module.DefineType(
            $"{assemblyName}.{union.Name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);

        // Base constructor
        var baseCtor = baseType.DefineConstructor(
            MethodAttributes.Family, CallingConventions.Standard, Type.EmptyTypes);
        var baseCtorIl = baseCtor.GetILGenerator();
        baseCtorIl.Emit(OpCodes.Ldarg_0);
        baseCtorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        baseCtorIl.Emit(OpCodes.Ret);

        _userTypes[union.Name] = baseType;

        // Define nested case types
        foreach (var @case in union.Cases)
        {
            var caseType = baseType.DefineNestedType(@case.Name,
                TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Sealed,
                baseType);

            var caseFieldBuilders = new List<FieldBuilder>();

            foreach (var field in @case.Fields)
            {
                var fieldClrType = IlTypeMapper.MapToClr(field.Type);
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

                caseFieldBuilders.Add(fb);
            }

            // Case constructor
            var caseCtorParams = @case.Fields.Select(f => IlTypeMapper.MapToClr(f.Type)).ToArray();
            var caseCtor = caseType.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, caseCtorParams);

            for (int i = 0; i < @case.Fields.Count; i++)
                caseCtor.DefineParameter(i + 1, ParameterAttributes.None, @case.Fields[i].Name);

            var caseCtorIl = caseCtor.GetILGenerator();
            caseCtorIl.Emit(OpCodes.Ldarg_0);
            caseCtorIl.Emit(OpCodes.Call, baseCtor);
            for (int i = 0; i < caseFieldBuilders.Count; i++)
            {
                caseCtorIl.Emit(OpCodes.Ldarg_0);
                caseCtorIl.Emit(OpCodes.Ldarg, i + 1);
                caseCtorIl.Emit(OpCodes.Stfld, caseFieldBuilders[i]);
            }
            caseCtorIl.Emit(OpCodes.Ret);

            caseType.CreateType();
            _unionCaseTypes[$"{union.Name}.{@case.Name}"] = caseType;
        }

        baseType.CreateType();
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeBuilder typeBuilder)
    {
        var paramTypes = func.Params.Select(p => IlTypeMapper.MapToClr(p.Type)).ToArray();

        // For async functions, wrap the return type in Task<T> or Task
        Type returnType;
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                returnType = typeof(System.Threading.Tasks.Task);
            else
                returnType = typeof(System.Threading.Tasks.Task<>)
                    .MakeGenericType(IlTypeMapper.MapToClr(func.ReturnType));
        }
        else
        {
            returnType = IlTypeMapper.MapReturnTypeToClr(func.ReturnType);
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
                var innerClrType = IlTypeMapper.MapToClr(func.ReturnType);
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
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
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
                var local = il.DeclareLocal(IlTypeMapper.MapToClr(let.Value.Type));
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

            case IrNode.BuiltinCtorCall ctorCall:
                EmitBuiltinCtorCall(ctorCall, il, outerParams, locals);
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
                EmitCollectionNew(listNew.Elements, listNew.Type, typeof(ZsList), "Of", il, outerParams, locals);
                break;

            case IrNode.VectorNew vectorNew:
                EmitCollectionNew(vectorNew.Elements, vectorNew.Type, typeof(ZsVector), "Of", il, outerParams, locals);
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

        var argTypes = clrNew.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
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

        var argTypes = clrCall.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();

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
                var invokeMethod = delegateLocal.LocalType.GetMethod("Invoke")!;
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
                    var delegateType = IlTypeMapper.MapToClr(outerParams[i].Type);
                    var invokeMethod = delegateType.GetMethod("Invoke")!;
                    il.Emit(OpCodes.Callvirt, invokeMethod);
                    return;
                }
            }

            // Check static fields for delegate (top-level Let bindings)
            if (_staticFields.TryGetValue(v.Name, out var staticField))
            {
                var fieldInvokeMethod = staticField.FieldType.GetMethod("Invoke");
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
        var funcType = IlTypeMapper.MapToClr(call.Function.Type);
        var invoke = funcType.GetMethod("Invoke");
        if (invoke is not null)
        {
            il.Emit(OpCodes.Callvirt, invoke);
            return;
        }

        diagnostics.Error($"IL emission not implemented for Call with {call.Function.GetType().Name} target", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitBuiltinCtorCall(IrNode.BuiltinCtorCall node, ILGenerator il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        if (node.RuntimeTypeName == "ZsError")
        {
            // (Error "msg") -> new ZsError(string)
            foreach (var arg in node.Args)
                EmitNode(arg, il, outerParams, locals);

            var ctor = typeof(ZsError).GetConstructor([typeof(string)])!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        // Ok, Err, Some, None — nested types inside ZsResult<,> or ZsOption<>
        var typeArgs = node.TypeArgs.Select(IlTypeMapper.MapToClr).ToArray();
        var nestedType = ResolveNestedRuntimeType(node.RuntimeTypeName, node.CaseName!, typeArgs);

        if (nestedType is null)
        {
            diagnostics.Error($"Cannot resolve runtime type {node.RuntimeTypeName}.{node.CaseName}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Emit constructor arguments
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        // Find the constructor
        var argTypes = node.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var ctorInfo = nestedType.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == argTypes.Length;
        });

        if (ctorInfo is null)
        {
            diagnostics.Error($"Constructor not found for {nestedType}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        il.Emit(OpCodes.Newobj, ctorInfo);
    }

    private static Type? ResolveNestedRuntimeType(string runtimeTypeName, string caseName, Type[] typeArgs)
    {
        // Map runtime type name to the open generic type
        Type? openParent = runtimeTypeName switch
        {
            "ZsResult" => typeof(ZsResult<,>),
            "ZsOption" => typeof(ZsOption<>),
            _ => null
        };

        if (openParent is null)
            return null;

        // Close the parent generic type
        var closedParent = openParent.MakeGenericType(typeArgs);

        // Get the nested type (Ok, Err, Some, None)
        var nestedType = closedParent.GetNestedType(caseName);
        return nestedType;
    }

    private void EmitMatch(IrNode.Match match, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Store scrutinee in a local
        var scrutineeType = IlTypeMapper.MapToClr(match.Scrutinee.Type);
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
            // Resolve property names: for builtin types use hardcoded names,
            // for user-defined unions get property names from the type
            var propertyNames = ResolvePatternPropertyNames(ctor.Name, caseType, ctor.Fields.Count);

            for (int i = 0; i < ctor.Fields.Count; i++)
            {
                var field = ctor.Fields[i];
                if (field is IrPattern.Variable v)
                {
                    var propName = i < propertyNames.Count ? propertyNames[i] : "Value";
                    var prop = caseType.GetProperty(propName);
                    if (prop is not null)
                    {
                        var getter = prop.GetGetMethod()!;
                        var fieldLocal = il.DeclareLocal(prop.PropertyType);
                        il.Emit(OpCodes.Ldloc, castLocal);
                        il.Emit(OpCodes.Callvirt, getter);
                        il.Emit(OpCodes.Stloc, fieldLocal);
                        locals[v.Name] = fieldLocal;
                    }
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
        switch (scrutineeType)
        {
            case ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] }:
                return ResolveNestedRuntimeType("ZsResult", caseName,
                    [IlTypeMapper.MapToClr(okT), IlTypeMapper.MapToClr(errT)]);

            case ZType.ZNamedType { Name: "Option", TypeArgs: [var t] }:
                return ResolveNestedRuntimeType("ZsOption", caseName,
                    [IlTypeMapper.MapToClr(t)]);

            case ZType.ZNamedType named:
                // User-defined union type
                var caseKey = $"{named.Name}.{caseName}";
                if (_unionCaseTypes.TryGetValue(caseKey, out var caseType))
                    return caseType;
                return null;

            default:
                return null;
        }
    }

    private List<string> ResolvePatternPropertyNames(string caseName, Type caseType, int fieldCount)
    {
        // For builtin types (Ok, Err, Some, None), use hardcoded names
        if (caseName is "Ok" or "Some")
            return ["Value"];
        if (caseName == "Err")
            return ["Error"];
        if (caseName == "None")
            return [];

        // For user-defined unions, get property names from the type's properties
        // (excluding inherited ones from the base type)
        var props = caseType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToList();
        return props.Count > 0 ? props : Enumerable.Range(0, fieldCount).Select(_ => "Value").ToList();
    }

    private void EmitTryCatch(IrNode.TryCatch node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Extract Ok/Err types from the Result type
        Type okClrType, errClrType, resultClrType;
        if (node.Type is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            okClrType = IlTypeMapper.MapToClr(okT);
            errClrType = IlTypeMapper.MapToClr(errT);
            resultClrType = IlTypeMapper.MapToClr(node.Type);
        }
        else
        {
            diagnostics.Error("TryCatch node type is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Declare a local to hold the result (can't leave values on stack across exception boundaries)
        var resultLocal = il.DeclareLocal(resultClrType);

        // Resolve Ok and Err nested types
        var okType = ResolveNestedRuntimeType("ZsResult", "Ok", [okClrType, errClrType]);
        var errType = ResolveNestedRuntimeType("ZsResult", "Err", [okClrType, errClrType]);
        if (okType is null || errType is null)
        {
            diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var okCtor = okType.GetConstructors().First(c => c.GetParameters().Length == 1);
        var errCtor = errType.GetConstructors().First(c => c.GetParameters().Length == 1);

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

        // new ZsError(message)
        var zsErrorCtor = typeof(ZsError).GetConstructor([typeof(string)])!;
        il.Emit(OpCodes.Newobj, zsErrorCtor);

        // new Err(zsError)
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

        var resultClrType = IlTypeMapper.MapToClr(node.ResultType);
        var tempLocal = il.DeclareLocal(resultClrType);
        il.Emit(OpCodes.Stloc, tempLocal);

        // Resolve the Err type for the inner result
        Type innerOkClrType, innerErrClrType;
        if (node.ResultType is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            innerOkClrType = IlTypeMapper.MapToClr(okT);
            innerErrClrType = IlTypeMapper.MapToClr(errT);
        }
        else
        {
            diagnostics.Error("Propagate expression is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var innerErrType = ResolveNestedRuntimeType("ZsResult", "Err", [innerOkClrType, innerErrClrType])!;
        var innerOkType = ResolveNestedRuntimeType("ZsResult", "Ok", [innerOkClrType, innerErrClrType])!;

        // Test: is it Err?
        var okLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Isinst, innerErrType);
        il.Emit(OpCodes.Brfalse, okLabel);

        // It's Err — extract the error and wrap in the function's return Err type, then early return
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, innerErrType);

        // Get .Error property
        var errProp = innerErrType.GetProperty("Error")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, errProp);

        // Wrap in the function's return Err type
        if (_currentFuncReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
        {
            var funcOkClr = IlTypeMapper.MapToClr(fOkT);
            var funcErrClr = IlTypeMapper.MapToClr(fErrT);
            var funcErrType = ResolveNestedRuntimeType("ZsResult", "Err", [funcOkClr, funcErrClr])!;
            var funcErrCtor = funcErrType.GetConstructors().First(c => c.GetParameters().Length == 1);
            il.Emit(OpCodes.Newobj, funcErrCtor);
        }

        il.Emit(OpCodes.Ret); // Early return

        // Ok path — extract Value
        il.MarkLabel(okLabel);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, innerOkType);
        var valueProp = innerOkType.GetProperty("Value")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, valueProp);
        // Unwrapped value is now on the stack
    }

    private void EmitMethodCall(IrNode.MethodCall node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        var receiverClrType = IlTypeMapper.MapToClr(node.Receiver.Type);

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
            var prop = receiverClrType.GetProperty(node.MethodName);
            if (prop is null)
            {
                diagnostics.Error($"Property '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            }
            var getter = prop.GetGetMethod()!;
            il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, getter);
            return;
        }

        if (node.IsIndexer)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            var indexer = receiverClrType.GetMethod("get_Item");
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

        var argTypes = node.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var method = receiverClrType.GetMethod(node.MethodName, argTypes);
        if (method is null)
        {
            // Fallback: match by name and arg count
            method = receiverClrType.GetMethods()
                .FirstOrDefault(m => m.Name == node.MethodName && m.GetParameters().Length == argTypes.Length);
        }
        if (method is null)
        {
            diagnostics.Error($"Method '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }
        il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, method);
    }

    private void EmitCollectionNew(IReadOnlyList<IrNode> elements, ZType collectionType,
        Type helperClass, string methodName, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Determine element type from the collection's ZType
        Type elementClrType = typeof(object);
        if (collectionType is ZType.ZNamedType { TypeArgs: [var elemT] })
            elementClrType = IlTypeMapper.MapToClr(elemT);

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

        // Call HelperClass.Of<T>(params T[])
        var openMethod = helperClass.GetMethod(methodName)!;
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
            keyClrType = IlTypeMapper.MapToClr(keyT);
            valueClrType = IlTypeMapper.MapToClr(valT);
        }

        var tupleType = typeof(ValueTuple<,>).MakeGenericType(keyClrType, valueClrType);
        var tupleCtor = tupleType.GetConstructor([keyClrType, valueClrType])!;

        // Create array of tuples
        il.Emit(OpCodes.Ldc_I4, node.Entries.Count);
        il.Emit(OpCodes.Newarr, tupleType);

        for (int i = 0; i < node.Entries.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            EmitNode(node.Entries[i].Key, il, outerParams, locals);
            EmitNode(node.Entries[i].Value, il, outerParams, locals);
            il.Emit(OpCodes.Newobj, tupleCtor);
            il.Emit(OpCodes.Stelem, tupleType);
        }

        // Call ZsMap.Of<K,V>(params (K,V)[])
        var openMethod = typeof(ZsMap).GetMethod("Of")!;
        var closedMethod = openMethod.MakeGenericMethod(keyClrType, valueClrType);
        il.Emit(OpCodes.Call, closedMethod);
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
                        captures.Add((fv, IlTypeMapper.MapToClr(outerParams[i].Type)));
                        break;
                    }
                }
            }
        }

        var delegateType = IlTypeMapper.MapToClr(funcDef.Type);

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
            var lambdaParamTypes = funcDef.Params.Select(p => IlTypeMapper.MapToClr(p.Type)).ToArray();
            var lambdaReturnType = IlTypeMapper.MapReturnTypeToClr(funcDef.ReturnType);
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
        IrNode.BuiltinCtorCall ctor =>
            ctor.Args.Aggregate(new HashSet<string>(), (acc, a) => Merge(acc, FindFreeVars(a, bound))),
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
            var ctorParams = node.Fields.Select(f => IlTypeMapper.MapToClr(f.Value.Type)).ToArray();
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

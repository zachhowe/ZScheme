namespace ZScript.Compiler.Codegen;

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter(string assemblyName, DiagnosticBag diagnostics, string className = "Program", IReadOnlyList<string>? clrUsings = null, IReadOnlyList<string>? assemblySearchPaths = null, IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null, IReadOnlyList<string>? precompiledAssemblyPaths = null, string? ilNamespace = null)
{
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];
    private readonly string _ilNamespace = ilNamespace ?? assemblyName;
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths);

    private readonly Dictionary<string, MethodInfo> _methods = new();
    private readonly Dictionary<string, Type> _userTypes = new();
    private readonly Dictionary<string, Type> _unionCaseTypes = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _unionCasePropertyNames = new();
    private readonly Dictionary<string, MethodInfo> _unionCaseGetters = new();
    private readonly Dictionary<string, FieldBuilder> _unionCaseFields = new(); // backing fields for pattern match extraction
    private readonly Dictionary<string, FieldBuilder> _staticFields = new();
    private readonly Dictionary<string, ZType.ZFuncType> _genericMethodTypes = new(); // IR func types for generic methods
    private readonly List<TypeBuilder> _deferredTypeCreations = new(); // generic types to CreateType after body emission
    private readonly Dictionary<string, TypeBuilder> _unbaked = new(); // TypeBuilders before CreateType (for MakeGenericType with GPBs)
    private TypeBuilder? _currentTypeBuilder;
    private ZType? _currentFuncReturnType;
    private int _instanceArgOffset; // 0 for static methods, 1 for instance methods
    private Dictionary<string, FieldBuilder>? _currentClassFields;
    private int _lambdaId;
    private int _asyncSmCounter;
    private Dictionary<int, Type>? _currentTypeVarMap;       // ZTypeVar.Id → GenericTypeParameterBuilder
    private Dictionary<string, Type>? _currentTypeParamMap;   // type param name → GenericTypeParameterBuilder

    private IlMoveNextContext? _moveNextCtx;

    private sealed class IlMoveNextContext
    {
        public required TypeBuilder SmType;
        public required FieldBuilder StateField;
        public required FieldBuilder BuilderField;
        public required LocalBuilder StateLocal;
        public required Dictionary<string, FieldBuilder> VarFields;
        public required Dictionary<int, FieldBuilder> AwaiterFields;
        public required List<(string Name, LocalBuilder Local)> AllLocals;
        public required bool IsVoidReturn;
        public int NextAwaitState;
        public Label[]? ResumeLabels;
        public Label ExitLabel;
    }

    private Type MapToClr(ZType type, IReadOnlyDictionary<string, Type>? typeParamMap = null)
        => IlTypeMapper.MapToClr(type, _userTypes, typeParamMap ?? _currentTypeParamMap, _currentTypeVarMap);

    private void LoadPrecompiledAssembly(string path)
    {
        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            diagnostics.Warning($"Failed to load precompiled assembly '{path}': {ex.Message}",
                Diagnostics.SourceSpan.None);
            return;
        }

        // First pass: register all types
        var abstractBases = new Dictionary<Type, string>(); // base type → name
        foreach (var type in asm.GetExportedTypes())
        {
            // Register module classes (ending with "Module") — their static methods
            if (type.IsAbstract && type.IsSealed) // static class
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    _methods[method.Name] = method;
                }
            }

            // Register union base types
            if (type.IsAbstract && !type.IsSealed && !type.IsInterface)
            {
                _userTypes[type.Name] = type;
                abstractBases[type] = type.Name;
            }

            // Register concrete record/union case types (nested types)
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                var caseKey = $"{type.Name}.{nested.Name}";
                _unionCaseTypes[caseKey] = nested;
                _userTypes[nested.Name] = nested;

                // Register property getters for union case fields
                foreach (var prop in nested.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var getter = prop.GetGetMethod();
                    if (getter is not null)
                        _unionCaseGetters[$"{type.Name}.{nested.Name}.{prop.Name}"] = getter;
                }

                // Register property names for union case pattern matching
                var propNames = nested.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name)
                    .ToList();
                if (propNames.Count > 0)
                    _unionCasePropertyNames[caseKey] = propNames;
            }

            // Register non-nested record types
            if (!type.IsAbstract && !type.IsNested && type.GetCustomAttributes(false)
                    .Any(a => a.GetType().Name == "CompilerGeneratedAttribute" || type.GetMethod("<Clone>$") is not null))
            {
                _userTypes[type.Name] = type;
            }
        }

        // Second pass: register top-level union case types (sealed classes inheriting abstract bases)
        foreach (var type in asm.GetExportedTypes())
        {
            if (type.IsSealed && !type.IsAbstract && !type.IsNested
                && type.BaseType is not null
                && abstractBases.TryGetValue(type.BaseType.IsGenericType
                    ? type.BaseType.GetGenericTypeDefinition() : type.BaseType, out var baseName))
            {
                var caseKey = $"{baseName}.{type.Name}";
                if (!_unionCaseTypes.ContainsKey(caseKey))
                {
                    _unionCaseTypes[caseKey] = type;
                    _userTypes[type.Name] = type;

                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var getter = prop.GetGetMethod();
                        if (getter is not null)
                            _unionCaseGetters[$"{baseName}.{type.Name}.{prop.Name}"] = getter;
                    }

                    var propNames = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => p.Name)
                        .ToList();
                    if (propNames.Count > 0)
                        _unionCasePropertyNames[caseKey] = propNames;
                }
            }
        }
    }

    private Type MapReturnTypeToClr(ZType type)
        => IlTypeMapper.MapReturnTypeToClr(type, _userTypes, _currentTypeParamMap, _currentTypeVarMap);

    /// <summary>
    /// Resolves a method on a closed generic type using TypeBuilder.GetMethod.
    /// Falls back gracefully if the type is not a TypeBuilderInstantiation.
    /// </summary>
    private static MethodInfo? ResolveOnClosedGeneric(Type closedType, MethodInfo openMethod)
    {
        try
        {
            return TypeBuilder.GetMethod(closedType, openMethod);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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
            if (openMethod is null) return null;
            return ResolveOnClosedGeneric(type, openMethod) ?? openMethod;
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
            if (openMethod is null) return null;
            return ResolveOnClosedGeneric(type, openMethod) ?? openMethod;
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

    /// <summary>
    /// Resolves a constructor on a closed generic type, handling both TypeBuilder-based
    /// and runtime types from precompiled assemblies.
    /// </summary>
    private static ConstructorInfo ResolveGenericConstructor(Type closedType, ConstructorInfo openCtor)
    {
        var openType = closedType.GetGenericTypeDefinition();
        if (openType is TypeBuilder)
            return TypeBuilder.GetConstructor(closedType, openCtor);
        // For runtime types, get the constructor directly from the closed type
        return closedType.GetConstructors()
            .First(c => c.GetParameters().Length == openCtor.GetParameters().Length);
    }

    /// <summary>
    /// Resolves a method on a closed generic type, handling both TypeBuilder-based
    /// and runtime types from precompiled assemblies.
    /// </summary>
    private static MethodInfo ResolveGenericMethod(Type closedType, MethodInfo openMethod)
    {
        var openType = closedType.GetGenericTypeDefinition();
        if (openType is TypeBuilder)
            return TypeBuilder.GetMethod(closedType, openMethod);
        // For runtime types, get the method directly from the closed type
        return closedType.GetMethod(openMethod.Name, openMethod.GetParameters().Select(p => p.ParameterType).ToArray())
            ?? closedType.GetMethods().First(m => m.Name == openMethod.Name
                && m.GetParameters().Length == openMethod.GetParameters().Length);
    }

    /// <summary>
    /// Safely resolves the constructor of a delegate type that may be a generic instantiation
    /// containing TypeBuilder args (e.g., Func&lt;int, Option&lt;int&gt;&gt; where Option is a TypeBuilder).
    /// </summary>
    private static ConstructorInfo SafeGetDelegateConstructor(Type delegateType)
    {
        try { return delegateType.GetConstructors()[0]; }
        catch (NotSupportedException) when (delegateType.IsGenericType)
        {
            var openCtor = delegateType.GetGenericTypeDefinition().GetConstructors()[0];
            return TypeBuilder.GetConstructor(delegateType, openCtor);
        }
    }

    public byte[]? Emit(IrNode node)
    {
        var asmName = new AssemblyName(assemblyName);
        var coreAssembly = Assembly.Load("System.Runtime");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, coreAssembly);
        var moduleBuilder = asmBuilder.DefineDynamicModule(assemblyName);
        var typeBuilder = moduleBuilder.DefineType(
            $"{_ilNamespace}.{className}",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        _currentTypeBuilder = typeBuilder;

        var mainStatements = new List<IrNode>();

        // Load precompiled assemblies and register their types/methods
        if (precompiledAssemblyPaths is { Count: > 0 })
        {
            foreach (var path in precompiledAssemblyPaths)
                LoadPrecompiledAssembly(path);
        }

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
                    $"{_ilNamespace}.{moduleClassName}",
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                importedModuleTypes.Add(moduleType);

                foreach (var def in defs)
                {
                    if (def is IrNode.FuncDef func)
                        EmitFuncDef(func, moduleType);
                    else if (def is IrNode.ClassDecl classDecl)
                        EmitClassDecl(classDecl, moduleBuilder);
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

            // Third pass: emit functions and class declarations, tracking user-defined main
            MethodInfo? userMainMethod = null;
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.FuncDef func)
                {
                    EmitFuncDef(func, typeBuilder);
                    if (func.Name == "main")
                        userMainMethod = _methods["main"];
                }
                else if (child is IrNode.ClassDecl classDecl)
                {
                    EmitClassDecl(classDecl, moduleBuilder);
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
            MethodInfo? userMain = null;
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

        // Finalize deferred generic types (must happen after all function bodies are emitted
        // so that MakeGenericType(GenericTypeParameterBuilder) works during emission)
        foreach (var deferredType in _deferredTypeCreations)
            deferredType.CreateType();
        _deferredTypeCreations.Clear();

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
            $"{_ilNamespace}.{record.Name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

        // Register early so self-referential types can resolve
        _userTypes[record.Name] = typeBuilder;

        GenericTypeParameterBuilder[]? genericParams = null;
        Dictionary<string, Type>? typeParamMap = null;
        if (record.TypeParams.Count > 0)
        {
            genericParams = typeBuilder.DefineGenericParameters(record.TypeParams.ToArray());
            ApplyGenericConstraints(genericParams, record.TypeParams, record.TypeParamConstraints);
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
            $"{_ilNamespace}.{union.Name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);

        GenericTypeParameterBuilder[]? baseGenericParams = null;
        if (union.TypeParams.Count > 0)
        {
            baseGenericParams = baseType.DefineGenericParameters(union.TypeParams.ToArray());
            ApplyGenericConstraints(baseGenericParams, union.TypeParams, union.TypeParamConstraints);
        }

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
            var caseType = module.DefineType($"{_ilNamespace}.{@case.Name}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

            GenericTypeParameterBuilder[]? caseGenericParams = null;
            Dictionary<string, Type>? typeParamMap = null;

            if (union.TypeParams.Count > 0)
            {
                caseGenericParams = caseType.DefineGenericParameters(union.TypeParams.ToArray());
                ApplyGenericConstraints(caseGenericParams, union.TypeParams, union.TypeParamConstraints);
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

                // Store getter and backing field for later use in pattern matching
                _unionCaseGetters[$"{union.Name}.{@case.Name}.{field.Name}"] = getter;
                _unionCaseFields[$"{union.Name}.{@case.Name}.{field.Name}"] = fb;

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

            // Generate Equals(object) override for structural equality
            EmitUnionCaseEquals(caseType, caseFieldBuilders);

            // Generate GetHashCode() override
            EmitUnionCaseGetHashCode(caseType, caseFieldBuilders, @case.Name);

            // Generate ToString() override
            EmitUnionCaseToString(caseType, caseFieldBuilders, @case.Name);

            var caseKey2 = $"{union.Name}.{@case.Name}";
            _unionCaseTypes[caseKey2] = caseType;
            _unionCasePropertyNames[caseKey2] = @case.Fields.Select(f => f.Name).ToList();
            if (union.TypeParams.Count > 0)
                _unbaked[caseKey2] = caseType; // store before baking for MakeGenericType with GPBs
            caseType.CreateType();
        }

        baseType.CreateType();
    }

    private static void EmitUnionCaseEquals(TypeBuilder caseType, List<FieldBuilder> fields)
    {
        var method = caseType.DefineMethod("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(bool), [typeof(object)]);
        var il = method.GetILGenerator();
        var falseLabel = il.DefineLabel();

        if (fields.Count == 0)
        {
            // Zero-field case: just check type
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Isinst, caseType);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un);
            il.Emit(OpCodes.Ret);
            return;
        }

        // Use runtime helper: compare via reflection to avoid generic type encoding issues
        // CollectionHelpers.UnionCaseEquals(this, obj)
        var helperMethod = typeof(ZScript.Runtime.CollectionHelpers)
            .GetMethod("UnionCaseEquals", BindingFlags.Public | BindingFlags.Static)!;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, helperMethod);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitUnionCaseGetHashCode(TypeBuilder caseType, List<FieldBuilder> fields, string caseName)
    {
        var method = caseType.DefineMethod("GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();

        // Use runtime helper to avoid generic type encoding issues
        var helperMethod = typeof(ZScript.Runtime.CollectionHelpers)
            .GetMethod("UnionCaseGetHashCode", BindingFlags.Public | BindingFlags.Static)!;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, helperMethod);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitUnionCaseToString(TypeBuilder caseType, List<FieldBuilder> fields, string caseName)
    {
        // No custom ToString needed — the default is fine for now
    }

    private static Dictionary<int, string> BuildTypeVarMap(IrNode.FuncDef func)
    {
        if (func.TypeParams is not { Count: > 0 } || func.Type is not ZType.ZFuncType ft)
            return new();
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        var map = new Dictionary<int, string>();
        for (int i = 0; i < freeVars.Count && i < func.TypeParams.Count; i++)
            map[freeVars[i]] = func.TypeParams[i];
        return map;
    }

    private static void ApplyGenericConstraints(
        GenericTypeParameterBuilder[] genericParams,
        IReadOnlyList<string> typeParamNames,
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints)
    {
        if (constraints is not { Count: > 0 }) return;
        for (int i = 0; i < genericParams.Length && i < typeParamNames.Count; i++)
        {
            if (!constraints.TryGetValue(typeParamNames[i], out var kind)) continue;
            var attrs = System.Reflection.GenericParameterAttributes.None;
            if (kind.HasFlag(GenericConstraintKind.Struct) || kind.HasFlag(GenericConstraintKind.Unmanaged))
                attrs |= System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint;
            if (kind.HasFlag(GenericConstraintKind.Class))
                attrs |= System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint;
            if (kind.HasFlag(GenericConstraintKind.New))
                attrs |= System.Reflection.GenericParameterAttributes.DefaultConstructorConstraint;
            // NotNull and Default are C# compiler-level concepts with no IL representation
            if (attrs != System.Reflection.GenericParameterAttributes.None)
                genericParams[i].SetGenericParameterAttributes(attrs);
        }
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeBuilder typeBuilder)
    {
        var isGeneric = func.TypeParams is { Count: > 0 };

        // Save generic context (only reset for generic funcs — non-generic lambdas inherit)
        var savedTypeVarMap = _currentTypeVarMap;
        var savedTypeParamMap = _currentTypeParamMap;

        MethodBuilder methodBuilder;
        if (isGeneric)
        {
            // Define method first (without signature), then add generic params
            methodBuilder = typeBuilder.DefineMethod(
                Sanitize(func.Name),
                MethodAttributes.Public | MethodAttributes.Static);

            var genericParams = methodBuilder.DefineGenericParameters(func.TypeParams!.ToArray());
            ApplyGenericConstraints(genericParams, func.TypeParams!, func.TypeParamConstraints);

            // Build ZTypeVar.Id → GenericTypeParameterBuilder map
            var varNameMap = BuildTypeVarMap(func);
            _currentTypeVarMap = new Dictionary<int, Type>();
            _currentTypeParamMap = new Dictionary<string, Type>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx >= 0)
                {
                    _currentTypeVarMap[varId] = genericParams[idx];
                    _currentTypeParamMap[paramName] = genericParams[idx];
                }
            }

            // Now resolve types with generic params available
            var paramTypes = func.Params.Select(p => MapToClr(p.Type)).ToArray();
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

            methodBuilder.SetReturnType(returnType);
            methodBuilder.SetParameters(paramTypes);
        }
        else
        {
            // Non-generic path: existing behavior
            var paramTypes = func.Params.Select(p => MapToClr(p.Type)).ToArray();
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

            methodBuilder = typeBuilder.DefineMethod(
                Sanitize(func.Name),
                MethodAttributes.Public | MethodAttributes.Static,
                returnType,
                paramTypes);
        }

        _methods[Sanitize(func.Name)] = methodBuilder;
        if (isGeneric && func.Type is ZType.ZFuncType ft2)
            _genericMethodTypes[Sanitize(func.Name)] = ft2;

        // Name parameters
        for (int i = 0; i < func.Params.Count; i++)
            methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, func.Params[i].Name);

        // Branch to async state machine generation if the body contains await
        if (func.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(func.Body))
        {
            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitIlAsyncFuncDef(func, methodBuilder, typeBuilder);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
        }
        else
        {
            var il = methodBuilder.GetILGenerator();
            var locals = new Dictionary<string, LocalBuilder>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitNode(func.Body, il, func.Params, locals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;

            if (func.IsAsync)
            {
                if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                {
                    if (func.Body.Type is not null
                        and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Emit(OpCodes.Pop);
                    var completedTaskGetter = typeof(System.Threading.Tasks.Task)
                        .GetProperty("CompletedTask")!.GetGetMethod()!;
                    il.Emit(OpCodes.Call, completedTaskGetter);
                }
                else
                {
                    var fromResult = typeof(System.Threading.Tasks.Task)
                        .GetMethod("FromResult")!
                        .MakeGenericMethod(MapToClr(func.ReturnType));
                    il.Emit(OpCodes.Call, fromResult);
                }
            }

            il.Emit(OpCodes.Ret);
        }

        // Restore generic context for generic funcs
        if (isGeneric)
        {
            _currentTypeVarMap = savedTypeVarMap;
            _currentTypeParamMap = savedTypeParamMap;
        }
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

    private void EmitClassDecl(IrNode.ClassDecl classDecl, ModuleBuilder moduleBuilder)
    {
        var classFullName = _ilNamespace is not null
            ? $"{_ilNamespace}.{Sanitize(classDecl.Name)}"
            : Sanitize(classDecl.Name);
        var classBuilder = moduleBuilder.DefineType(classFullName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

        // Apply class-level attributes
        EmitCustomAttributes(classDecl.Attributes, classBuilder);

        // Define fields as properties with backing fields
        var fieldBuilders = new List<(FieldBuilder Field, PropertyBuilder Prop)>();
        foreach (var field in classDecl.Fields)
        {
            var fieldType = MapToClr(field.Type);
            var fb = classBuilder.DefineField($"<{Sanitize(field.Name)}>k__BackingField",
                fieldType, FieldAttributes.Private | FieldAttributes.InitOnly);
            var pb = classBuilder.DefineProperty(Sanitize(field.Name), PropertyAttributes.None, fieldType, null);
            var getter = classBuilder.DefineMethod($"get_{Sanitize(field.Name)}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                fieldType, Type.EmptyTypes);
            var getIl = getter.GetILGenerator();
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, fb);
            getIl.Emit(OpCodes.Ret);
            pb.SetGetMethod(getter);
            fieldBuilders.Add((fb, pb));
        }

        // Define constructor
        var ctorParamTypes = classDecl.Fields.Select(f => MapToClr(f.Type)).ToArray();
        var ctor = classBuilder.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, ctorParamTypes);
        for (int i = 0; i < classDecl.Fields.Count; i++)
            ctor.DefineParameter(i + 1, ParameterAttributes.None, Sanitize(classDecl.Fields[i].Name));
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        for (int i = 0; i < fieldBuilders.Count; i++)
        {
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fieldBuilders[i].Field);
        }
        ctorIl.Emit(OpCodes.Ret);

        // Define parameterless constructor for test frameworks
        if (classDecl.Fields.Count > 0)
        {
            var defaultCtor = classBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var dctorIl = defaultCtor.GetILGenerator();
            dctorIl.Emit(OpCodes.Ldarg_0);
            dctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            dctorIl.Emit(OpCodes.Ret);
        }

        // Build field lookup for method bodies
        var classFieldMap = new Dictionary<string, FieldBuilder>();
        for (int i = 0; i < classDecl.Fields.Count; i++)
            classFieldMap[Sanitize(classDecl.Fields[i].Name)] = fieldBuilders[i].Field;

        // Emit methods
        foreach (var method in classDecl.Methods)
        {
            var paramTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();
            var retType = MapReturnTypeToClr(method.ReturnType);
            var mb = classBuilder.DefineMethod(Sanitize(method.Name),
                MethodAttributes.Public, retType, paramTypes);

            for (int i = 0; i < method.Params.Count; i++)
                mb.DefineParameter(i + 1, ParameterAttributes.None, method.Params[i].Name);

            // Apply method-level attributes (e.g., [Fact])
            EmitCustomAttributes(method.Attributes, mb);

            var il = mb.GetILGenerator();
            // Method params: arg0 = this (instance method), arg1.. = params
            // We need to offset by 1 for instance methods
            var methodLocals = new Dictionary<string, LocalBuilder>();
            _currentFuncReturnType = method.ReturnType;
            _instanceArgOffset = 1; // instance methods: arg 0 = this
            _currentClassFields = classFieldMap;
            EmitNode(method.Body, il, method.Params, methodLocals);
            _currentClassFields = null;
            _instanceArgOffset = 0;
            _currentFuncReturnType = null;

            if (method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            {
                // Pop any value left on stack for void methods
                if (method.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    il.Emit(OpCodes.Pop);
            }
            il.Emit(OpCodes.Ret);
        }

        classBuilder.CreateType();
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, TypeBuilder builder)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
        {
            var attrType = _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null) continue;
            var ctorInfo = attrType.GetConstructor(Type.EmptyTypes);
            if (ctorInfo is null) continue;
            builder.SetCustomAttribute(new CustomAttributeBuilder(ctorInfo, []));
        }
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, MethodBuilder builder)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
        {
            var attrType = _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null) continue;
            var ctorInfo = attrType.GetConstructor(Type.EmptyTypes);
            if (ctorInfo is null) continue;
            builder.SetCustomAttribute(new CustomAttributeBuilder(ctorInfo, []));
        }
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
                EmitNode(let.Value, il, outerParams, locals);
                if (let.Value.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                {
                    // Value returns void; nothing on stack to store
                    EmitNode(let.Body, il, outerParams, locals);
                }
                else
                {
                    var local = il.DeclareLocal(MapToClr(let.Value.Type));
                    il.Emit(OpCodes.Stloc, local);
                    locals[let.VarName] = local;

                    // Also save to state machine field if inside MoveNext
                    if (_moveNextCtx != null && _moveNextCtx.VarFields.TryGetValue(let.VarName, out var smField))
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldloc, local);
                        il.Emit(OpCodes.Stfld, smField);
                        _moveNextCtx.AllLocals.Add((let.VarName, local));
                    }

                    EmitNode(let.Body, il, outerParams, locals);
                }
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
                if (_moveNextCtx != null)
                    EmitMoveNextAwait(awaitNode, il, outerParams, locals);
                else
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
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == clrCall.MethodName
                         && m.IsGenericMethodDefinition
                         && m.GetGenericArguments().Length == clrCall.GenericArity
                         && m.GetParameters().Length == argTypes.Length)
                .ToList();

            // Prefer the overload whose parameter types best match the argument types.
            // Score: direct generic param (T) > generic containing T (IEnumerable<T>) > non-generic
            MethodInfo? generic = null;
            if (candidates.Count == 1)
            {
                generic = candidates[0];
            }
            else if (candidates.Count > 1)
            {
                generic = candidates
                    .OrderByDescending(m => ScoreGenericOverload(m, argTypes))
                    .First();
            }

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

    /// <summary>
    /// Scores a generic method overload for how well its parameter types match the argument types.
    /// Higher score = better match. Prefers direct generic params (T) over wrapped (IEnumerable&lt;T&gt;).
    /// </summary>
    private static int ScoreGenericOverload(MethodInfo method, Type[] argTypes)
    {
        int score = 0;
        var methodParams = method.GetParameters();
        for (int i = 0; i < methodParams.Length && i < argTypes.Length; i++)
        {
            var paramType = methodParams[i].ParameterType;
            if (paramType.IsGenericParameter)
            {
                // Direct match: param is T, arg could be anything → best match
                score += 10;
            }
            else if (paramType == argTypes[i])
            {
                // Exact type match
                score += 8;
            }
            else if (paramType.IsAssignableFrom(argTypes[i]))
            {
                score += 5;
            }
        }
        return score;
    }

    private static Type[] InferGenericTypeArgs(MethodInfo genericMethod, Type[] argTypes)
    {
        var genericParams = genericMethod.GetGenericArguments();
        var methodParams = genericMethod.GetParameters();
        var result = new Type[genericParams.Length];

        for (int i = 0; i < methodParams.Length && i < argTypes.Length; i++)
            MatchTypeArgs(methodParams[i].ParameterType, argTypes[i], result);

        for (int i = 0; i < result.Length; i++)
            result[i] ??= typeof(object);

        return result;
    }

    private static void MatchTypeArgs(Type formal, Type actual, Type[] result)
    {
        if (formal.IsGenericParameter)
        {
            result[formal.GenericParameterPosition] = actual;
            return;
        }
        if (formal.IsGenericType && actual.IsGenericType
            && formal.GetGenericTypeDefinition() == actual.GetGenericTypeDefinition())
        {
            var formalArgs = formal.GetGenericArguments();
            var actualArgs = actual.GetGenericArguments();
            for (int j = 0; j < formalArgs.Length && j < actualArgs.Length; j++)
                MatchTypeArgs(formalArgs[j], actualArgs[j], result);
        }
    }

    /// <summary>
    /// Infers type arguments for a call to a generic method using IR type information.
    /// Falls back to reflection-based inference if IR types aren't available.
    /// </summary>
    private Type[] InferTypeArgsForCall(string sanitizedName, MethodInfo genericMethod, IReadOnlyList<IrNode> args)
    {
        var genericArgCount = genericMethod.GetGenericArguments().Length;

        // Use stored IR type to infer from ZType → avoids MethodBuilder.GetParameters() issues
        if (_genericMethodTypes.TryGetValue(sanitizedName, out var funcType))
        {
            var result = new Type[genericArgCount];
            // funcType.Params has ZTypes with ZTypeVar; args have concrete ZTypes
            // Build ZTypeVar.Id → ordered index map (same ordering as ExtractFuncTypeParams)
            var freeVars = Substitution.FreeVars(funcType).OrderBy(id => id).ToList();
            for (int i = 0; i < funcType.Params.Count && i < args.Count; i++)
                MatchZTypeArgs(funcType.Params[i], args[i].Type, freeVars, result);

            for (int i = 0; i < result.Length; i++)
                result[i] ??= typeof(object);
            return result;
        }

        // Fallback: use reflection-based inference
        var argClrTypes = args.Select(a => MapToClr(a.Type)).ToArray();
        return InferGenericTypeArgs(genericMethod, argClrTypes);
    }

    private void MatchZTypeArgs(ZType formal, ZType actual, List<int> freeVarIds, Type[] result)
    {
        if (formal is ZType.ZTypeVar tv)
        {
            var idx = freeVarIds.IndexOf(tv.Id);
            if (idx >= 0 && idx < result.Length)
                result[idx] = MapToClr(actual);
            return;
        }
        if (formal is ZType.ZConstrainedVar cv)
        {
            var idx = freeVarIds.IndexOf(cv.Id);
            if (idx >= 0 && idx < result.Length)
                result[idx] = MapToClr(actual);
            return;
        }
        if (formal is ZType.ZNamedType fn && actual is ZType.ZNamedType an && fn.Name == an.Name)
        {
            for (int i = 0; i < fn.TypeArgs.Count && i < an.TypeArgs.Count; i++)
                MatchZTypeArgs(fn.TypeArgs[i], an.TypeArgs[i], freeVarIds, result);
        }
        if (formal is ZType.ZFuncType ff && actual is ZType.ZFuncType af)
        {
            for (int i = 0; i < ff.Params.Count && i < af.Params.Count; i++)
                MatchZTypeArgs(ff.Params[i], af.Params[i], freeVarIds, result);
            MatchZTypeArgs(ff.Return, af.Return, freeVarIds, result);
        }
    }

    private void EmitCall(IrNode.Call call, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        if (call.Function is IrNode.Var v)
        {
            // Check if it's a known static method
            if (_methods.TryGetValue(Sanitize(v.Name), out var methodInfo))
            {
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);

                if (methodInfo.IsGenericMethodDefinition)
                {
                    var typeArgs = InferTypeArgsForCall(Sanitize(v.Name), methodInfo, call.Args);
                    il.Emit(OpCodes.Call, methodInfo.MakeGenericMethod(typeArgs));
                }
                else
                {
                    il.Emit(OpCodes.Call, methodInfo);
                }
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
                    il.Emit(OpCodes.Ldarg, i + _instanceArgOffset);
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

            // Constructor patterns use isinst+dup+brfalse which leaves a residual value
            // on the stack when branching to the next arm. Pop it here.
            if (i > 0 && match.Arms[i - 1].Pattern is IrPattern.Constructor)
                il.Emit(OpCodes.Pop);

            EmitPatternTest(arm.Pattern, scrutineeLocal, match.Scrutinee.Type, nextLabel, il, outerParams, locals);
            EmitNode(arm.Body, il, outerParams, locals);
            il.Emit(OpCodes.Br, endLabel);
        }

        // Fail: throw InvalidOperationException
        il.MarkLabel(failLabel);
        // Pop residual from last arm's failed constructor pattern test
        if (match.Arms.Count > 0 && match.Arms[^1].Pattern is IrPattern.Constructor)
            il.Emit(OpCodes.Pop);
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

                    // Resolve field/getter for pattern match value extraction
                    var getterKey = caseKey is not null ? $"{caseKey}.{propName}" : null;
                    MethodInfo? getter = null;
                    bool useReflectionHelper = false;

                    if (getterKey is not null && _unionCaseGetters.TryGetValue(getterKey, out var openGetter))
                    {
                        if (caseType.IsGenericType && !caseType.IsGenericTypeDefinition
                            && _currentTypeVarMap is { Count: > 0 })
                        {
                            // PersistedAssemblyBuilder can't encode MemberRef/FieldRef on
                            // TypeBuilder-defined generic types closed with method-level
                            // GenericTypeParameterBuilders. Use runtime reflection helper.
                            useReflectionHelper = true;
                        }
                        else if (caseType.IsGenericType && !caseType.IsGenericTypeDefinition)
                            getter = ResolveGenericMethod(caseType, openGetter);
                        else
                            getter = openGetter;
                    }
                    else
                    {
                        var prop = caseType.GetProperty(propName);
                        if (prop is not null)
                            getter = prop.GetGetMethod()!;
                    }
                    if (getter is null && !useReflectionHelper) continue;

                    if (useReflectionHelper)
                    {
                        // Use runtime CollectionHelpers.GetField(instance, fieldName) -> object
                        // then unbox/cast to the target type
                        var backingFieldName = $"<{propName}>k__BackingField";
                        var helperMethod = typeof(ZScript.Runtime.CollectionHelpers)
                            .GetMethod("GetField", BindingFlags.Public | BindingFlags.Static)!;
                        // Determine the target type from the backing field's generic parameter position
                        Type targetType = typeof(object);
                        var fieldLookupKey = $"{caseKey}.{propName}";
                        if (scrutineeType is ZType.ZNamedType named2
                            && _unionCaseFields.TryGetValue(fieldLookupKey, out var backingFb)
                            && backingFb.FieldType is System.Reflection.Emit.GenericTypeParameterBuilder fieldGpb)
                        {
                            // Map from case-level GPB position to union type arg
                            var gpbPos = fieldGpb.GenericParameterPosition;
                            if (gpbPos < named2.TypeArgs.Count)
                                targetType = MapToClr(named2.TypeArgs[gpbPos]);
                        }
                        else if (scrutineeType is ZType.ZNamedType named3
                            && _unionCasePropertyNames.TryGetValue(caseKey!, out var allPropNames2))
                        {
                            // Fallback: use property index (works when field index == type param position)
                            var fieldIdx2 = allPropNames2.ToList().IndexOf(propName);
                            if (fieldIdx2 >= 0 && fieldIdx2 < named3.TypeArgs.Count)
                                targetType = MapToClr(named3.TypeArgs[fieldIdx2]);
                        }

                        var fieldLocal = il.DeclareLocal(targetType);
                        il.Emit(OpCodes.Ldloc, castLocal);
                        il.Emit(OpCodes.Ldstr, backingFieldName);
                        il.Emit(OpCodes.Call, helperMethod);
                        // Always emit unbox.any for the target type — at runtime, if T0 is a
                        // value type, this unboxes; if it's a reference type, it casts.
                        // (GenericTypeParameterBuilder.IsValueType is false at emit time,
                        // but the actual type may be a value type at runtime.)
                        if (targetType != typeof(object))
                            il.Emit(OpCodes.Unbox_Any, targetType);
                        il.Emit(OpCodes.Stloc, fieldLocal);
                        locals[v.Name] = fieldLocal;
                    }
                    else
                    {
                        var fieldLocal = il.DeclareLocal(getter!.ReturnType);
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
                    // Use unbaked TypeBuilder for MakeGenericType when args contain GenericTypeParameterBuilders
                    // (baked TypeBuilders produce invalid metadata tokens with method-level generic params)
                    var typeForClose = _unbaked.TryGetValue(caseKey, out var unbaked) ? unbaked : caseType;
                    return typeForClose.MakeGenericType(typeArgs);
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
            okCtor = ResolveGenericConstructor(closedOkType, openOkCtor);
            errCtor = ResolveGenericConstructor(closedErrType, openErrCtor);
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
                noneCtor = ResolveGenericConstructor(closedNoneType, openNoneCtor);
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
            errPropGetter = ResolveGenericMethod(closedErrType, openErrGetter);

            var openValueGetter = _unionCaseGetters["Result.Ok.value"];
            okValueGetter = ResolveGenericMethod(closedOkType, openValueGetter);
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
                funcErrCtor = ResolveGenericConstructor(funcErrType, openCtor);
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
            var lambdaMethod = _methods[Sanitize(lambdaName)];
            var delegateCtor = SafeGetDelegateConstructor(delegateType);
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

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 1; // closure Invoke is an instance method
            _currentFuncReturnType = funcDef.ReturnType;
            EmitNode(funcDef.Body, lambdaIl, instanceParams, lambdaLocals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
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
            var delegateCtor = SafeGetDelegateConstructor(delegateType);
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
                // Use unbaked TypeBuilder for proper metadata token generation with GenericTypeParameterBuilders
                var typeForClose = _unbaked.TryGetValue(caseKey, out var unbaked) ? unbaked : caseType;
                if (typeForClose is TypeBuilder)
                {
                    var closedType = typeForClose.MakeGenericType(typeArgs);
                    var openCtor = typeForClose.GetConstructors().First(c => c.GetParameters().Length == node.Args.Count);
                    var closedCtor = TypeBuilder.GetConstructor(closedType, openCtor);
                    il.Emit(OpCodes.Newobj, closedCtor);
                }
                else
                {
                    // Precompiled assembly types: use standard reflection
                    var closedType = caseType.MakeGenericType(typeArgs);
                    var closedCtor = closedType.GetConstructors().First(c => c.GetParameters().Length == node.Args.Count);
                    il.Emit(OpCodes.Newobj, closedCtor);
                }
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

        // Then check parameters (offset by _instanceArgOffset for instance methods)
        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
            {
                il.Emit(OpCodes.Ldarg, i + _instanceArgOffset);
                return;
            }
        }

        // Then check class instance fields
        if (_currentClassFields is not null && _currentClassFields.TryGetValue(name, out var classField))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, classField);
            return;
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

    private static string Sanitize(string name) =>
        name.Replace("-", "_").Replace("/", "_").Replace("?", "_q")
            .Replace(">", "_gt").Replace("|", "_pipe").Replace("^", "");

    // ─── Async State Machine Generation ───────────────────────────────────

    private void EmitIlAsyncFuncDef(IrNode.FuncDef func, MethodBuilder stubMethod, TypeBuilder parentType)
    {
        var info = AsyncStateMachineAnalyzer.Analyze(func);
        var smName = $"<{Sanitize(func.Name)}>d__{_asyncSmCounter++}";

        var isVoid = info.IsVoidReturn;
        Type builderClrType = isVoid
            ? typeof(AsyncTaskMethodBuilder)
            : typeof(AsyncTaskMethodBuilder<>).MakeGenericType(IlTypeMapper.MapToClr(func.ReturnType));

        // --- Define state machine struct ---
        var smType = parentType.DefineNestedType(smName,
            System.Reflection.TypeAttributes.Sealed | System.Reflection.TypeAttributes.NestedPrivate |
            System.Reflection.TypeAttributes.SequentialLayout,
            typeof(ValueType),
            [typeof(IAsyncStateMachine)]);

        smType.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!, []));

        // --- Define fields ---
        var stateField = smType.DefineField("__state", typeof(int), System.Reflection.FieldAttributes.Public);
        var builderField = smType.DefineField("__builder", builderClrType, System.Reflection.FieldAttributes.Public);

        var varFields = new Dictionary<string, FieldBuilder>();
        foreach (var p in func.Params)
        {
            var pField = smType.DefineField(Sanitize(p.Name),
                MapToClr(p.Type), System.Reflection.FieldAttributes.Public);
            varFields[p.Name] = pField;
        }

        foreach (var local in info.HoistedLocals)
        {
            if (!varFields.ContainsKey(local.Name))
            {
                var lField = smType.DefineField($"<{Sanitize(local.Name)}>5__",
                    MapToClr(local.Type), System.Reflection.FieldAttributes.Public);
                varFields[local.Name] = lField;
            }
        }

        var awaiterFields = new Dictionary<int, FieldBuilder>();
        foreach (var ap in info.AwaitPoints)
        {
            var awaiterClrType = GetIlAwaiterClrType(ap);
            var awaiterField = smType.DefineField($"__awaiter{ap.StateNumber}",
                awaiterClrType, System.Reflection.FieldAttributes.Private);
            awaiterFields[ap.StateNumber] = awaiterField;
        }

        // --- Emit MoveNext ---
        EmitIlMoveNextMethod(func, smType, stateField, builderField, builderClrType,
            varFields, awaiterFields, info);

        // --- Emit SetStateMachine ---
        EmitIlSetStateMachineMethod(smType);

        // Create the state machine type
        smType.CreateType();

        // --- Emit stub method body ---
        EmitIlAsyncStubBody(func, stubMethod, smType, stateField, builderField, builderClrType, varFields);

        // --- Add [AsyncStateMachine] attribute ---
        stubMethod.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!, [smType]));
    }

    private static Type GetIlAwaiterClrType(AsyncStateMachineAnalyzer.AwaitPointInfo ap)
    {
        if (ap.ResultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return typeof(TaskAwaiter);
        var innerClr = IlTypeMapper.MapToClr(ap.ResultType);
        return typeof(TaskAwaiter<>).MakeGenericType(innerClr);
    }

    private void EmitIlAsyncStubBody(
        IrNode.FuncDef func,
        MethodBuilder stubMethod,
        Type smType,
        FieldBuilder stateField,
        FieldBuilder builderField,
        Type builderClrType,
        Dictionary<string, FieldBuilder> varFields)
    {
        var il = stubMethod.GetILGenerator();
        var smLocal = il.DeclareLocal(smType);

        // initobj
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Initobj, smType);

        // Copy params
        for (int i = 0; i < func.Params.Count; i++)
        {
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Stfld, varFields[func.Params[i].Name]);
        }

        // sm.__builder = Create()
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Call, builderClrType.GetMethod("Create")!);
        il.Emit(OpCodes.Stfld, builderField);

        // sm.__state = -1
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stfld, stateField);

        // sm.__builder.Start<SM>(ref sm)
        var startMethod = builderClrType.GetMethod("Start")!.MakeGenericMethod(smType);
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldflda, builderField);
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Call, startMethod);

        // return sm.__builder.Task
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldflda, builderField);
        il.Emit(OpCodes.Call, builderClrType.GetProperty("Task")!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitIlMoveNextMethod(
        IrNode.FuncDef func,
        TypeBuilder smType,
        FieldBuilder stateField,
        FieldBuilder builderField,
        Type builderClrType,
        Dictionary<string, FieldBuilder> varFields,
        Dictionary<int, FieldBuilder> awaiterFields,
        AsyncStateMachineAnalyzer.AsyncMethodInfo info)
    {
        var moveNext = smType.DefineMethod("MoveNext",
            System.Reflection.MethodAttributes.Private | System.Reflection.MethodAttributes.Final |
            System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.NewSlot |
            System.Reflection.MethodAttributes.Virtual,
            typeof(void), Type.EmptyTypes);

        // Override IAsyncStateMachine.MoveNext
        smType.DefineMethodOverride(moveNext,
            typeof(IAsyncStateMachine).GetMethod("MoveNext")!);

        var il = moveNext.GetILGenerator();

        // Locals
        var stateLocal = il.DeclareLocal(typeof(int));
        LocalBuilder? resultLocal = null;
        if (!info.IsVoidReturn)
            resultLocal = il.DeclareLocal(MapToClr(func.ReturnType));
        var exLocal = il.DeclareLocal(typeof(Exception));

        // Param locals
        var paramLocals = new Dictionary<string, LocalBuilder>();
        foreach (var p in func.Params)
            paramLocals[p.Name] = il.DeclareLocal(MapToClr(p.Type));

        // Set up context
        var exitLabel = il.DefineLabel();
        _moveNextCtx = new IlMoveNextContext
        {
            SmType = smType,
            StateField = stateField,
            BuilderField = builderField,
            StateLocal = stateLocal,
            VarFields = varFields,
            AwaiterFields = awaiterFields,
            AllLocals = [],
            IsVoidReturn = info.IsVoidReturn,
            NextAwaitState = 0,
            ExitLabel = exitLabel
        };

        foreach (var p in func.Params)
            _moveNextCtx.AllLocals.Add((p.Name, paramLocals[p.Name]));

        // Load __state
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, stateField);
        il.Emit(OpCodes.Stloc, stateLocal);

        // --- Try block ---
        il.BeginExceptionBlock();

        // Jump table
        var resumeLabels = new Label[info.AwaitPoints.Count];
        for (int i = 0; i < info.AwaitPoints.Count; i++)
            resumeLabels[i] = il.DefineLabel();
        _moveNextCtx.ResumeLabels = resumeLabels;

        if (resumeLabels.Length > 0)
        {
            il.Emit(OpCodes.Ldloc, stateLocal);
            il.Emit(OpCodes.Switch, resumeLabels);
        }

        // Load params from fields
        foreach (var p in func.Params)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, varFields[p.Name]);
            il.Emit(OpCodes.Stloc, paramLocals[p.Name]);
        }

        // Emit body
        var bodyLocals = new Dictionary<string, LocalBuilder>(paramLocals);
        EmitNode(func.Body, il, [], bodyLocals);

        // Store result
        if (!info.IsVoidReturn)
            il.Emit(OpCodes.Stloc, resultLocal!);
        else if (func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            il.Emit(OpCodes.Pop);

        // Leave try
        var afterTry = il.DefineLabel();
        il.Emit(OpCodes.Leave, afterTry);

        // --- Catch block ---
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, -2);
        il.Emit(OpCodes.Stfld, stateField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, builderField);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Call, builderClrType.GetMethod("SetException", [typeof(Exception)])!);

        il.Emit(OpCodes.Leave, exitLabel);

        il.EndExceptionBlock();

        // --- After try/catch ---
        il.MarkLabel(afterTry);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, -2);
        il.Emit(OpCodes.Stfld, stateField);

        if (info.IsVoidReturn)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, builderField);
            il.Emit(OpCodes.Call, builderClrType.GetMethod("SetResult", Type.EmptyTypes)!);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, builderField);
            il.Emit(OpCodes.Ldloc, resultLocal!);
            il.Emit(OpCodes.Call, builderClrType.GetMethod("SetResult",
                [IlTypeMapper.MapToClr(func.ReturnType)])!);
        }

        il.MarkLabel(exitLabel);
        il.Emit(OpCodes.Ret);

        _moveNextCtx = null;
    }

    private void EmitMoveNextAwait(IrNode.Await awaitNode, ILGenerator il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        var ctx = _moveNextCtx!;
        var stateNum = ctx.NextAwaitState++;
        var awaiterField = ctx.AwaiterFields[stateNum];
        var resumeLabel = ctx.ResumeLabels![stateNum];
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(awaitNode.Expr.Type);
        var isVoidAwait = resultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

        Type awaiterClrType = isVoidAwait
            ? typeof(TaskAwaiter)
            : typeof(TaskAwaiter<>).MakeGenericType(IlTypeMapper.MapToClr(resultType));

        var awaiterLocal = il.DeclareLocal(awaiterClrType);

        // Emit task expression
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // GetAwaiter()
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        il.Emit(OpCodes.Call, getAwaiterMethod);
        il.Emit(OpCodes.Stloc, awaiterLocal);

        // Check IsCompleted
        var isCompletedGetter = awaiterClrType.GetProperty("IsCompleted")!.GetGetMethod()!;
        var completedLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloca, awaiterLocal);
        il.Emit(OpCodes.Call, isCompletedGetter);
        il.Emit(OpCodes.Brtrue, completedLabel);

        // --- Not completed: suspend ---

        // Set state
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, stateNum);
        il.Emit(OpCodes.Stfld, ctx.StateField);
        il.Emit(OpCodes.Ldc_I4, stateNum);
        il.Emit(OpCodes.Stloc, ctx.StateLocal);

        // Store awaiter to field
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, awaiterLocal);
        il.Emit(OpCodes.Stfld, awaiterField);

        // Save all locals to fields
        foreach (var (name, local) in ctx.AllLocals)
        {
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Stfld, field);
            }
        }

        // AwaitUnsafeOnCompleted
        var awaitUnsafe = ctx.BuilderField.FieldType
            .GetMethod("AwaitUnsafeOnCompleted")!
            .MakeGenericMethod(awaiterClrType, ctx.SmType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, ctx.BuilderField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, awaiterField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, awaitUnsafe);

        // Leave try block
        il.Emit(OpCodes.Leave, ctx.ExitLabel);

        // --- Resume label ---
        il.MarkLabel(resumeLabel);

        // Restore awaiter from field
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, awaiterField);
        il.Emit(OpCodes.Stloc, awaiterLocal);

        // Clear awaiter field
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, awaiterField);
        il.Emit(OpCodes.Initobj, awaiterClrType);

        // Reset state
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stloc, ctx.StateLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stfld, ctx.StateField);

        // Restore locals from fields
        foreach (var (name, local) in ctx.AllLocals)
        {
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Stloc, local);
            }
        }

        // --- Completed label ---
        il.MarkLabel(completedLabel);

        // GetResult()
        var getResultMethod = awaiterClrType.GetMethod("GetResult", Type.EmptyTypes)!;
        il.Emit(OpCodes.Ldloca, awaiterLocal);
        il.Emit(OpCodes.Call, getResultMethod);
    }

    private static void EmitIlSetStateMachineMethod(TypeBuilder smType)
    {
        var setSm = smType.DefineMethod("SetStateMachine",
            System.Reflection.MethodAttributes.Private | System.Reflection.MethodAttributes.Final |
            System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.NewSlot |
            System.Reflection.MethodAttributes.Virtual,
            typeof(void), [typeof(IAsyncStateMachine)]);

        smType.DefineMethodOverride(setSm,
            typeof(IAsyncStateMachine).GetMethod("SetStateMachine")!);

        var il = setSm.GetILGenerator();
        il.Emit(OpCodes.Ret);
    }
}

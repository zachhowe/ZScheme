using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;
using DiagnosticBag = ZScheme.Compiler.Diagnostics.DiagnosticBag;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using AsmMethodSemanticsAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodSemanticsAttributes;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Emits .NET IL using AsmResolver.
/// </summary>
public sealed class IlEmitter(
    string assemblyName,
    DiagnosticBag diagnostics,
    string className,
    IReadOnlyList<string>? clrUsings = null,
    IReadOnlyList<string>? assemblySearchPaths = null,
    IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null,
    IReadOnlyList<string>? precompiledAssemblyPaths = null,
    string? ilNamespace = null,
    bool isModule = false)
{
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths);
    private readonly Dictionary<string, ZType.ZFuncType> _genericMethodTypes = new();
    private readonly string _ilNamespace = ilNamespace ?? assemblyName;

    private readonly Dictionary<string, MethodDefinition> _methods = new();
    private readonly Dictionary<string, IMethodDescriptor> _precompiledMethods = new();
    private readonly Dictionary<string, MethodInfo> _precompiledReflectionMethods = new();
    private readonly Dictionary<string, IFieldDescriptor> _staticFields = new();
    private readonly Dictionary<string, IMethodDescriptor> _unionCaseGetters = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _unionCasePropertyNames = new();
    private readonly Dictionary<string, ITypeDefOrRef> _unionCaseTypes = new();
    private readonly Dictionary<string, ITypeDefOrRef> _userTypes = new();
    private readonly Dictionary<string, TypeSignature> _userTypeSignatures = new();

    private Dictionary<string, FieldDefinition>? _currentClassFields;
    private ZType? _currentFuncReturnType;
    private TypeDefinition? _currentTypeDefinition;
    private readonly Dictionary<string, AsmClassInfo> _asmClassInfos = new();
    private TypeDefinition? _currentBaseTypeDefinition;

    private sealed record AsmClassInfo(
        TypeDefinition TypeDef,
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames);
    private Dictionary<string, TypeSignature>? _currentTypeParamMap;
    private Dictionary<int, TypeSignature>? _currentTypeVarMap;
    private ITypeDefOrRef? _isExternalInitType;
    private int _asyncSmCounter;
    private int _instanceArgOffset;
    private int _lambdaId;
    private int _objectExprId;
    private AsyncMoveNextContext? _moveNextCtx;

    private ModuleDefinition _module = null!;
    private TypeSignature _valueTupleType = null!;
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];

    private TypeSignature MapToClr(ZType type, IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null)
    {
        var result = AsmResolverTypeMapper.MapToClr(type, _module, _valueTupleType, _userTypeSignatures,
            typeParamMap ?? _currentTypeParamMap, _currentTypeVarMap);
        // If the mapper returned Object but the type has a dot-qualified name, try ClrInterop
        if (result == _module.CorLibTypeFactory.Object
            && type is ZType.ZNamedType { Name: var name } && name.Contains('.'))
        {
            var clrType = _clrInterop.FindType(name);
            if (clrType is not null)
                result = _module.DefaultImporter.ImportType(clrType).ToTypeSignature(clrType.IsValueType);
        }
        return result;
    }

    private TypeSignature MapReturnTypeToClr(ZType type)
    {
        return AsmResolverTypeMapper.MapReturnTypeToClr(type, _module, _valueTupleType, _userTypeSignatures,
            _currentTypeParamMap, _currentTypeVarMap);
    }

    private ITypeDefOrRef GetIsExternalInitType()
    {
        if (_isExternalInitType is not null)
            return _isExternalInitType;

        _isExternalInitType = _module.DefaultImporter
            .ImportType(typeof(System.Runtime.CompilerServices.IsExternalInit));
        return _isExternalInitType;
    }

    private MethodDefinition CreateInitSetter(
        string propertyName,
        TypeSignature fieldType,
        FieldDefinition backingField)
    {
        var initReturnType = new CustomModifierTypeSignature(
            GetIsExternalInitType(), true, _module.CorLibTypeFactory.Void);
        var setter = new MethodDefinition($"set_{propertyName}",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig,
            new MethodSignature(CallingConventionAttributes.HasThis, initReturnType, [fieldType]));
        setter.ParameterDefinitions.Add(new ParameterDefinition(1, "value", 0));
        var setBody = new CilMethodBody();
        setter.MethodBody = setBody;
        var setIl = setBody.Instructions;
        setIl.Add(CilOpCodes.Ldarg_0);
        setIl.Add(CilOpCodes.Ldarg_1);
        setIl.Add(CilOpCodes.Stfld, backingField);
        setIl.Add(CilOpCodes.Ret);
        return setter;
    }

    public byte[]? Emit(IrNode node)
    {
        Log.Debug("IlEmitter: emitting assembly {AssemblyName}, usings={UsingCount}, searchPaths={SearchPathCount}, importedModules={ImportedModuleCount}",
            assemblyName, ClrUsings.Count, assemblySearchPaths?.Count ?? 0, importedModules?.Count ?? 0);
        if (assemblySearchPaths is { Count: > 0 })
            foreach (var sp in assemblySearchPaths)
                Log.Debug("IlEmitter: assembly search path: {Path}", sp);

        var sysRuntimeAsm = Assembly.Load("System.Runtime");
        var corLib = new AssemblyReference("System.Runtime", sysRuntimeAsm.GetName().Version!)
        {
            PublicKeyOrToken = sysRuntimeAsm.GetName().GetPublicKeyToken()
        };
        _module = new ModuleDefinition(assemblyName + ".dll", corLib);
        var asmDef = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        asmDef.Modules.Add(_module);

        _valueTupleType = _module.DefaultImporter.ImportType(typeof(ValueTuple)).ToTypeSignature(true);

        var typeAttrs = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed;
        var typeDef = new TypeDefinition(_ilNamespace, className, typeAttrs);
        typeDef.BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
        _module.TopLevelTypes.Add(typeDef);
        _currentTypeDefinition = typeDef;

        var mainStatements = new List<IrNode>();

        // Load precompiled assemblies
        if (precompiledAssemblyPaths is { Count: > 0 })
            foreach (var path in precompiledAssemblyPaths)
                LoadPrecompiledAssembly(path);

        // Pass 0: define types and functions from imported modules
        if (importedModules is { Count: > 0 })
        {
            var moduleState =
                new List<(TypeDefinition ModuleType, List<IrNode.Let> LetBindings, IReadOnlyList<IrNode> Defs)>();

            // Pass 0a: define all types, static fields, and function signatures
            foreach (var (moduleClassName, defs) in importedModules)
            {
                var moduleType = new TypeDefinition(_ilNamespace, moduleClassName,
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                moduleType.BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
                _module.TopLevelTypes.Add(moduleType);

                foreach (var def in defs)
                    if (def is IrNode.RecordDecl or IrNode.UnionDecl or IrNode.InterfaceDecl)
                        DefineTypeDecl(def, moduleType);
                    else if (def is IrNode.ClassDecl classDecl)
                        EmitClassDecl(classDecl);

                var moduleLetBindings = new List<IrNode.Let>();
                foreach (var def in defs)
                    if (def is IrNode.Let let)
                    {
                        var fieldType = MapToClr(let.Value.Type);
                        var fd = new FieldDefinition(let.VarName,
                            FieldAttributes.Public | FieldAttributes.Static,
                            new FieldSignature(fieldType));
                        moduleType.Fields.Add(fd);
                        _staticFields[let.VarName] = fd;
                        moduleLetBindings.Add(let);
                    }

                foreach (var def in defs)
                    if (def is IrNode.FuncDef func)
                        RegisterFuncSignature(func, moduleType);

                moduleState.Add((moduleType, moduleLetBindings, defs));
            }

            // Pass 0b: emit all function bodies and .cctor bodies
            foreach (var (moduleType, moduleLetBindings, defs) in moduleState)
            {
                foreach (var def in defs)
                    if (def is IrNode.FuncDef func)
                        EmitFuncBody(func);

                if (moduleLetBindings.Count > 0)
                {
                    var cctor = new MethodDefinition(".cctor",
                        MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig
                        | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                        MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void));
                    moduleType.Methods.Add(cctor);
                    var body = new CilMethodBody();
                    cctor.MethodBody = body;
                    var il = body.Instructions;
                    var locals = new Dictionary<string, CilLocalVariable>();
                    foreach (var let in moduleLetBindings)
                    {
                        EmitNode(let.Value, il, [], locals);
                        il.Add(CilOpCodes.Stsfld, _staticFields[let.VarName]);
                        var local = new CilLocalVariable(MapToClr(let.Value.Type));
                        body.LocalVariables.Add(local);
                        il.Add(CilOpCodes.Ldsfld, _staticFields[let.VarName]);
                        il.Add(CilOpCodes.Stloc, local);
                        locals[let.VarName] = local;
                        if (let.Body is not IrNode.UnitConst)
                        {
                            EmitNode(let.Body, il, [], locals);
                            if (let.Body.Type is not null
                                and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                                il.Add(CilOpCodes.Pop);
                        }
                    }

                    il.Add(CilOpCodes.Ret);
                }
            }
        }

        if (node is IrNode.Seq seq)
        {
            foreach (var child in seq.Nodes)
                if (child is IrNode.RecordDecl or IrNode.UnionDecl or IrNode.InterfaceDecl)
                    DefineTypeDecl(child, isModule ? typeDef : null);

            foreach (var child in seq.Nodes)
                if (child is IrNode.Let let)
                {
                    var fieldType = MapToClr(let.Value.Type);
                    var fd = new FieldDefinition(let.VarName,
                        FieldAttributes.Public | FieldAttributes.Static,
                        new FieldSignature(fieldType));
                    typeDef.Fields.Add(fd);
                    _staticFields[let.VarName] = fd;
                }

            MethodDefinition? userMainMethod = null;
            foreach (var child in seq.Nodes)
                if (child is IrNode.FuncDef func)
                {
                    EmitFuncDef(func, typeDef);
                    if (func.Name == "main")
                        userMainMethod = _methods[Sanitize("main")];
                }
                else if (child is IrNode.ClassDecl classDecl)
                {
                    EmitClassDecl(classDecl);
                }

            foreach (var child in seq.Nodes)
                CollectTopLevel(child, mainStatements);
        }
        else if (node is IrNode.FuncDef singleFunc)
        {
            EmitFuncDef(singleFunc, typeDef);
        }
        else
        {
            CollectTopLevel(node, mainStatements);
        }

        // Emit static constructor (.cctor)
        if (mainStatements.Count > 0)
        {
            var cctor = new MethodDefinition(".cctor",
                MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void));
            typeDef.Methods.Add(cctor);

            var body = new CilMethodBody();
            cctor.MethodBody = body;
            var il = body.Instructions;
            var locals = new Dictionary<string, CilLocalVariable>();

            foreach (var stmt in mainStatements)
                if (stmt is IrNode.Let let)
                {
                    EmitNode(let.Value, il, [], locals);
                    il.Add(CilOpCodes.Stsfld, _staticFields[let.VarName]);
                    var local = new CilLocalVariable(MapToClr(let.Value.Type));
                    body.LocalVariables.Add(local);
                    il.Add(CilOpCodes.Ldsfld, _staticFields[let.VarName]);
                    il.Add(CilOpCodes.Stloc, local);
                    locals[let.VarName] = local;
                    if (let.Body is not IrNode.UnitConst)
                    {
                        EmitNode(let.Body, il, [], locals);
                        if (let.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                            il.Add(CilOpCodes.Pop);
                    }
                }
                else
                {
                    EmitNode(stmt, il, [], locals);
                    if (stmt.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Add(CilOpCodes.Pop);
                }

            il.Add(CilOpCodes.Ret);
        }

        // Emit Main(string[] args) wrapper
        if (node is IrNode.Seq seq2)
        {
            MethodDefinition? userMain = null;
            foreach (var child in seq2.Nodes)
                if (child is IrNode.FuncDef { Name: "main" })
                {
                    userMain = _methods[Sanitize("main")];
                    break;
                }

            if (userMain is not null)
            {
                var mainMethod = new MethodDefinition("Main",
                    MethodAttributes.Public | MethodAttributes.Static,
                    MethodSignature.CreateStatic(
                        _module.CorLibTypeFactory.Int32,
                        [new SzArrayTypeSignature(_module.CorLibTypeFactory.String)]));
                mainMethod.ParameterDefinitions.Add(new ParameterDefinition(
                    1, "args", 0));
                typeDef.Methods.Add(mainMethod);

                var mainBody = new CilMethodBody();
                mainMethod.MethodBody = mainBody;
                var mainIl = mainBody.Instructions;

                var createMethod = typeof(ImmutableList).GetMethods()
                    .First(m => m.Name == "Create"
                                && m.IsGenericMethodDefinition
                                && m.GetParameters() is [{ ParameterType.IsArray: true }])
                    .MakeGenericMethod(typeof(string));
                mainIl.Add(CilOpCodes.Ldarg_0);
                mainIl.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(createMethod));
                mainIl.Add(CilOpCodes.Call, userMain);
                mainIl.Add(CilOpCodes.Ret);

                HasEntryPoint = true;
                _module.ManagedEntryPointMethod = mainMethod;
            }
        }

        if (diagnostics.HasErrors)
            return null;

        // For library compilation with imported module classes, set a generous maxStack
        // instead of computing it (the IL for pattern matching, async, and nullable types
        // in class methods may have stack calculation issues in AsmResolver)
        if (importedModules?.Any(m => m.Definitions.Any(d => d is IrNode.ClassDecl)) == true)
        {
            void SetMaxStack(TypeDefinition td)
            {
                foreach (var md in td.Methods)
                    if (md.CilMethodBody is { } body)
                    {
                        body.ComputeMaxStackOnBuild = false;
                        body.MaxStack = 16;
                    }

                foreach (var nested in td.NestedTypes)
                    SetMaxStack(nested);
            }

            foreach (var td in _module.TopLevelTypes)
                SetMaxStack(td);
        }

        using var ms = new MemoryStream();
        _module.Write(ms);
        var bytes = ms.ToArray();
        Log.Debug("IlEmitter: emit complete, {ByteCount} bytes", bytes.Length);
        return bytes;
    }

    private void LoadPrecompiledAssembly(string path)
    {
        Log.Debug("IlEmitter: loading precompiled assembly {Path}", path);
        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            diagnostics.Warning($"Failed to load precompiled assembly '{path}': {ex.Message}",
                SourceSpan.None);
            return;
        }

        var abstractBases = new Dictionary<Type, string>();
        foreach (var type in asm.GetExportedTypes())
        {
            if (type.IsAbstract && type.IsSealed) // static class (module class)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static |
                                                       BindingFlags.DeclaredOnly))
                {
                    _precompiledMethods[method.Name] = _module.DefaultImporter.ImportMethod(method);
                    _precompiledReflectionMethods[method.Name] = method;
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static |
                                                      BindingFlags.DeclaredOnly))
                    _staticFields[field.Name] = _module.DefaultImporter.ImportField(field);

                RegisterNestedTypes(type, abstractBases);
            }

            if (type.IsAbstract && !type.IsSealed && !type.IsInterface)
            {
                RegisterUserType(StripBacktickArity(type.Name), ImportTypeWithGenericArity(type));
                abstractBases[type] = StripBacktickArity(type.Name);
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                var strippedTypeName = StripBacktickArity(type.Name);
                var strippedNestedName = StripBacktickArity(nested.Name);
                var caseKey = $"{strippedTypeName}.{strippedNestedName}";
                var importedNested = ImportTypeWithGenericArity(nested);
                _unionCaseTypes[caseKey] = importedNested;
                RegisterUserType(strippedNestedName, importedNested);

                var nestedBase = nested.BaseType;
                if (nestedBase is not null && nestedBase.IsNested
                    && nestedBase.DeclaringType == type)
                {
                    var unionKey = $"{StripBacktickArity(nestedBase.Name)}.{strippedNestedName}";
                    _unionCaseTypes.TryAdd(unionKey, importedNested);
                }

                foreach (var prop in nested.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var getter = prop.GetGetMethod();
                    if (getter is not null)
                    {
                        _unionCaseGetters[$"{strippedTypeName}.{strippedNestedName}.{prop.Name}"] =
                            _module.DefaultImporter.ImportMethod(getter);
                        if (nestedBase is not null && nestedBase.IsNested
                            && nestedBase.DeclaringType == type)
                            _unionCaseGetters.TryAdd($"{StripBacktickArity(nestedBase.Name)}.{strippedNestedName}.{prop.Name}",
                                _module.DefaultImporter.ImportMethod(getter));
                    }
                }

                var propNames = nested.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name).ToList();
                if (propNames.Count > 0)
                {
                    _unionCasePropertyNames[caseKey] = propNames;
                    if (nestedBase is not null && nestedBase.IsNested
                        && nestedBase.DeclaringType == type)
                        _unionCasePropertyNames.TryAdd($"{StripBacktickArity(nestedBase.Name)}.{strippedNestedName}", propNames);
                }
            }

            if (!type.IsAbstract && !type.IsNested && type.GetMethod("<Clone>$") is not null)
                RegisterUserType(StripBacktickArity(type.Name), ImportTypeWithGenericArity(type));
        }

        foreach (var type in asm.GetExportedTypes())
            if (type.IsSealed && !type.IsAbstract && !type.IsNested
                && type.BaseType is not null
                && abstractBases.TryGetValue(type.BaseType.IsGenericType
                    ? type.BaseType.GetGenericTypeDefinition()
                    : type.BaseType, out var baseName))
            {
                var strippedName = StripBacktickArity(type.Name);
                var caseKey = $"{baseName}.{strippedName}";
                if (!_unionCaseTypes.ContainsKey(caseKey))
                {
                    var importedCaseType = ImportTypeWithGenericArity(type);
                    _unionCaseTypes[caseKey] = importedCaseType;
                    RegisterUserType(strippedName, importedCaseType);

                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var getter = prop.GetGetMethod();
                        if (getter is not null)
                            _unionCaseGetters[$"{baseName}.{strippedName}.{prop.Name}"] =
                                _module.DefaultImporter.ImportMethod(getter);
                    }

                    var propNames = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => p.Name).ToList();
                    if (propNames.Count > 0)
                        _unionCasePropertyNames[caseKey] = propNames;
                }
            }
    }

    private void RegisterNestedTypes(Type moduleType, Dictionary<Type, string> abstractBases)
    {
        foreach (var nested in moduleType.GetNestedTypes(BindingFlags.Public))
        {
            var importedType = ImportTypeWithGenericArity(nested);
            var nestedName = StripBacktickArity(nested.Name);

            if (nested.IsAbstract && !nested.IsSealed && !nested.IsInterface)
            {
                RegisterUserType(nestedName, importedType);
                abstractBases[nested] = nestedName;

                foreach (var sibling in moduleType.GetNestedTypes(BindingFlags.Public))
                    if (sibling.IsSealed && !sibling.IsAbstract
                        && sibling.BaseType is not null
                        && (sibling.BaseType.IsGenericType
                            ? sibling.BaseType.GetGenericTypeDefinition() == nested
                            : sibling.BaseType == nested))
                    {
                        var siblingName = StripBacktickArity(sibling.Name);
                        var caseKey = $"{nestedName}.{siblingName}";
                        if (!_unionCaseTypes.ContainsKey(caseKey))
                        {
                            var importedSibling = ImportTypeWithGenericArity(sibling);
                            _unionCaseTypes[caseKey] = importedSibling;
                            RegisterUserType(siblingName, importedSibling);

                            foreach (var prop in sibling.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                var getter = prop.GetGetMethod();
                                if (getter is not null)
                                    _unionCaseGetters[$"{nestedName}.{siblingName}.{prop.Name}"] =
                                        _module.DefaultImporter.ImportMethod(getter);
                            }

                            var propNames = sibling.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .Select(p => p.Name).ToList();
                            if (propNames.Count > 0)
                                _unionCasePropertyNames[caseKey] = propNames;
                        }
                    }
            }

            if (!nested.IsAbstract && nested.IsSealed && nested.GetMethod("<Clone>$") is not null)
                RegisterUserType(nestedName, importedType);

            if (!nested.IsAbstract && nested.IsSealed && nested.GetMethod("<Clone>$") is null)
                RegisterUserType(nestedName, importedType);
        }
    }

    private static void CollectTopLevel(IrNode node, List<IrNode> mainStatements)
    {
        switch (node)
        {
            case IrNode.FuncDef:
            case IrNode.RecordDecl:
            case IrNode.UnionDecl:
            case IrNode.InterfaceDecl:
            case IrNode.ClassDecl:
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

    private void DefineTypeDecl(IrNode node, TypeDefinition? parentType = null)
    {
        switch (node)
        {
            case IrNode.RecordDecl record:
                DefineRecordType(record, parentType);
                break;
            case IrNode.UnionDecl union:
                DefineUnionType(union, parentType);
                break;
            case IrNode.InterfaceDecl iface:
                DefineInterfaceType(iface, parentType);
                break;
        }
    }

    private ITypeDefOrRef? ResolveInterfaceType(string name)
    {
        if (_userTypes.TryGetValue(name, out var userType))
            return userType;

        var clrType = _clrInterop.FindType(name);
        if (clrType is not null)
            return (ITypeDefOrRef)_module.DefaultImporter.ImportType(clrType);

        foreach (var ns in ClrUsings)
        {
            clrType = _clrInterop.FindType(ns + "." + name);
            if (clrType is not null)
                return (ITypeDefOrRef)_module.DefaultImporter.ImportType(clrType);
        }

        return null;
    }

    private void DefineInterfaceType(IrNode.InterfaceDecl iface, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining interface type {InterfaceName}", iface.Name);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;

        var typeDef = new TypeDefinition(ns, Sanitize(iface.Name),
            vis | TypeAttributes.Interface | TypeAttributes.Abstract);

        // Add generic parameters
        foreach (var tp in iface.TypeParams)
        {
            var gp = new GenericParameter(tp);
            typeDef.GenericParameters.Add(gp);
        }

        // Add base interfaces
        foreach (var baseName in iface.BaseInterfaceNames)
        {
            var baseRef = ResolveInterfaceType(baseName);
            if (baseRef is not null)
                typeDef.Interfaces.Add(new InterfaceImplementation(baseRef));
        }

        // Add method signatures
        foreach (var method in iface.Methods)
        {
            var retType = method.ReturnType == ZType.Unit
                ? _module.CorLibTypeFactory.Void
                : MapToClr(method.ReturnType);
            var paramTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();
            var methodDef = new MethodDefinition(Sanitize(method.Name),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig |
                MethodAttributes.NewSlot | MethodAttributes.Abstract,
                MethodSignature.CreateInstance(retType, paramTypes));
            for (var pi = 0; pi < method.Params.Count; pi++)
                methodDef.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(pi + 1), Sanitize(method.Params[pi].Name), 0));
            typeDef.Methods.Add(methodDef);
        }

        EmitCustomAttributes(iface.Attributes, typeDef);

        if (parentType is not null)
            parentType.NestedTypes.Add(typeDef);
        else
            _module.TopLevelTypes.Add(typeDef);

        RegisterUserType(iface.Name, typeDef);
    }

    private void DefineRecordType(IrNode.RecordDecl record, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining record type {RecordName}", record.Name);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
        var typeDef = new TypeDefinition(ns, record.Name,
            vis | TypeAttributes.Class | TypeAttributes.Sealed);
        typeDef.BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();

        if (parentType is not null)
            parentType.NestedTypes.Add(typeDef);
        else
            _module.TopLevelTypes.Add(typeDef);
        RegisterUserType(record.Name, typeDef);

        Dictionary<string, TypeSignature>? typeParamMap = null;
        if (record.TypeParams.Count > 0)
        {
            typeParamMap = new Dictionary<string, TypeSignature>();
            foreach (var tp in record.TypeParams)
            {
                var gp = new GenericParameterSignature(_module, GenericParameterType.Type, typeDef.GenericParameters.Count);
                typeDef.GenericParameters.Add(new GenericParameter(tp));
                typeParamMap[tp] = gp;
            }
        }

        var fieldDefs = new List<(FieldDefinition Field, MethodDefinition Getter)>();

        foreach (var field in record.Fields)
        {
            var fieldClrType = MapToClr(field.Type, typeParamMap);
            var sanitizedName = Sanitize(field.Name);
            var fb = new FieldDefinition($"<{sanitizedName}>k__BackingField",
                FieldAttributes.Private | FieldAttributes.InitOnly,
                new FieldSignature(fieldClrType));
            typeDef.Fields.Add(fb);

            var getter = new MethodDefinition($"get_{sanitizedName}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName
                | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(fieldClrType));
            typeDef.Methods.Add(getter);
            var getBody = new CilMethodBody();
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, fb);
            getIl.Add(CilOpCodes.Ret);

            var prop = new PropertyDefinition(sanitizedName, 0, PropertySignature.CreateInstance(fieldClrType));
            prop.Semantics.Add(new MethodSemantics(getter, AsmMethodSemanticsAttributes.Getter));

            if (field.IsInit)
            {
                var initSetter = CreateInitSetter(sanitizedName, fieldClrType, fb);
                typeDef.Methods.Add(initSetter);
                prop.Semantics.Add(new MethodSemantics(initSetter, AsmMethodSemanticsAttributes.Setter));
            }

            typeDef.Properties.Add(prop);

            fieldDefs.Add((fb, getter));
        }

        // Constructor
        var ctorParams = record.Fields.Select(f => MapToClr(f.Type, typeParamMap)).ToArray();
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, ctorParams));
        for (var i = 0; i < record.Fields.Count; i++)
            ctor.ParameterDefinitions.Add(new ParameterDefinition(
                (ushort)(i + 1), Sanitize(record.Fields[i].Name), 0));
        typeDef.Methods.Add(ctor);

        var ctorBody = new CilMethodBody();
        ctor.MethodBody = ctorBody;
        var ctorIl = ctorBody.Instructions;
        ctorIl.Add(CilOpCodes.Ldarg_0);
        ctorIl.Add(CilOpCodes.Call,
            _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!));
        for (var i = 0; i < fieldDefs.Count; i++)
        {
            ctorIl.Add(CilOpCodes.Ldarg_0);
            ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Stfld, fieldDefs[i].Field);
        }

        ctorIl.Add(CilOpCodes.Ret);

        EmitDeconstruct(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
    }

    private void DefineUnionType(IrNode.UnionDecl union, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining union type {UnionName}", union.Name);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
        var baseType = new TypeDefinition(ns, union.Name,
            vis | TypeAttributes.Class | TypeAttributes.Abstract);
        baseType.BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();

        if (parentType is not null)
            parentType.NestedTypes.Add(baseType);
        else
            _module.TopLevelTypes.Add(baseType);

        if (union.TypeParams.Count > 0)
            foreach (var tp in union.TypeParams)
                baseType.GenericParameters.Add(new GenericParameter(tp));

        // Base constructor
        var baseCtor = new MethodDefinition(".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
        baseType.Methods.Add(baseCtor);
        var baseCtorBody = new CilMethodBody();
        baseCtor.MethodBody = baseCtorBody;
        var baseCtorIl = baseCtorBody.Instructions;
        baseCtorIl.Add(CilOpCodes.Ldarg_0);
        baseCtorIl.Add(CilOpCodes.Call,
            _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!));
        baseCtorIl.Add(CilOpCodes.Ret);

        RegisterUserType(union.Name, baseType);

        // Case types
        foreach (var @case in union.Cases)
        {
            var caseNs = parentType is null ? _ilNamespace : "";
            var caseVis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
            var caseType = new TypeDefinition(caseNs, @case.Name,
                caseVis | TypeAttributes.Class | TypeAttributes.Sealed);
            caseType.BaseType = baseType;

            if (parentType is not null)
                parentType.NestedTypes.Add(caseType);
            else
                _module.TopLevelTypes.Add(caseType);

            Dictionary<string, TypeSignature>? typeParamMap = null;
            if (union.TypeParams.Count > 0)
            {
                typeParamMap = new Dictionary<string, TypeSignature>();
                foreach (var tp in union.TypeParams)
                {
                    var gp = new GenericParameterSignature(_module, GenericParameterType.Type,
                        caseType.GenericParameters.Count);
                    caseType.GenericParameters.Add(new GenericParameter(tp));
                    typeParamMap[tp] = gp;
                }

                // Set parent to closed base type using case's own generic params
                var closedBaseArgs = caseType.GenericParameters
                    .Select((_, i) => (TypeSignature)new GenericParameterSignature(_module, GenericParameterType.Type, i))
                    .ToArray();
                caseType.BaseType = baseType.MakeGenericInstanceType(false, closedBaseArgs).ToTypeDefOrRef();
            }

            var caseFieldDefs = new List<FieldDefinition>();

            foreach (var field in @case.Fields)
            {
                var fieldClrType = MapToClr(field.Type, typeParamMap);
                var sanitizedName = Sanitize(field.Name);
                var fb = new FieldDefinition($"<{sanitizedName}>k__BackingField",
                    FieldAttributes.Private | FieldAttributes.InitOnly,
                    new FieldSignature(fieldClrType));
                caseType.Fields.Add(fb);

                var getter = new MethodDefinition($"get_{sanitizedName}",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName
                    | MethodAttributes.HideBySig,
                    MethodSignature.CreateInstance(fieldClrType));
                caseType.Methods.Add(getter);
                var getBody = new CilMethodBody();
                getter.MethodBody = getBody;
                var getIl = getBody.Instructions;
                getIl.Add(CilOpCodes.Ldarg_0);
                getIl.Add(CilOpCodes.Ldfld, fb);
                getIl.Add(CilOpCodes.Ret);

                var prop = new PropertyDefinition(sanitizedName, 0,
                    PropertySignature.CreateInstance(fieldClrType));
                prop.Semantics.Add(new MethodSemantics(getter, AsmMethodSemanticsAttributes.Getter));

                if (field.IsInit)
                {
                    var initSetter = CreateInitSetter(sanitizedName, fieldClrType, fb);
                    caseType.Methods.Add(initSetter);
                    prop.Semantics.Add(new MethodSemantics(initSetter, AsmMethodSemanticsAttributes.Setter));
                }

                caseType.Properties.Add(prop);

                _unionCaseGetters[$"{union.Name}.{@case.Name}.{sanitizedName}"] = getter;
                caseFieldDefs.Add(fb);
            }

            // Case constructor
            var caseCtorParams = @case.Fields.Select(f => MapToClr(f.Type, typeParamMap)).ToArray();
            var caseCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, caseCtorParams));
            for (var i = 0; i < @case.Fields.Count; i++)
                caseCtor.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(i + 1), Sanitize(@case.Fields[i].Name), 0));
            caseType.Methods.Add(caseCtor);

            var caseCtorBody = new CilMethodBody();
            caseCtor.MethodBody = caseCtorBody;
            var caseCtorIl = caseCtorBody.Instructions;
            caseCtorIl.Add(CilOpCodes.Ldarg_0);

            if (union.TypeParams.Count > 0)
            {
                var closedBaseArgs = caseType.GenericParameters
                    .Select((_, i) => (TypeSignature)new GenericParameterSignature(_module, GenericParameterType.Type, i))
                    .ToArray();
                var closedBaseSig = baseType.MakeGenericInstanceType(false, closedBaseArgs);
                var closedBaseCtor = new MemberReference(closedBaseSig.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
                caseCtorIl.Add(CilOpCodes.Call, closedBaseCtor);
            }
            else
            {
                caseCtorIl.Add(CilOpCodes.Call, baseCtor);
            }

            for (var i = 0; i < caseFieldDefs.Count; i++)
            {
                caseCtorIl.Add(CilOpCodes.Ldarg_0);
                caseCtorIl.Add(CilOpCodes.Ldarg, caseCtor.Parameters[i]);
                caseCtorIl.Add(CilOpCodes.Stfld, caseFieldDefs[i]);
            }

            caseCtorIl.Add(CilOpCodes.Ret);

            // Emit Equals, GetHashCode, and Deconstruct
            EmitUnionCaseEquals(caseType, caseFieldDefs);
            EmitUnionCaseGetHashCode(caseType, caseFieldDefs);
            EmitDeconstruct(caseType, caseFieldDefs);

            var caseKey = $"{union.Name}.{@case.Name}";
            _unionCaseTypes[caseKey] = caseType;
            _unionCasePropertyNames[caseKey] = @case.Fields.Select(f => Sanitize(f.Name)).ToList();
        }
    }

    private void EmitUnionCaseEquals(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Boolean,
                [_module.CorLibTypeFactory.Object]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "obj", 0));
        caseType.Methods.Add(method);

        var body = new CilMethodBody();
        method.MethodBody = body;
        var il = body.Instructions;

        var getType = _module.DefaultImporter.ImportMethod(typeof(object).GetMethod("GetType")!);
        var typeEquality = _module.DefaultImporter.ImportMethod(
            typeof(Type).GetMethod("op_Equality", [typeof(Type), typeof(Type)])!);
        var returnFalse = new CilInstructionLabel();

        // Check: obj != null && this.GetType() == obj.GetType()
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Brfalse, returnFalse);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)getType);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)getType);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)typeEquality);
        il.Add(CilOpCodes.Brfalse, returnFalse);

        if (fields.Count == 0)
        {
            il.Add(CilOpCodes.Ldc_I4_1);
            il.Add(CilOpCodes.Ret);
            var falseLdc0 = new CilInstruction(CilOpCodes.Ldc_I4_0);
            returnFalse.Instruction = falseLdc0;
            il.Add(falseLdc0);
            il.Add(CilOpCodes.Ret);
            return;
        }

        body.InitializeLocals = true;
        var otherLocal = new CilLocalVariable(_module.CorLibTypeFactory.Object);
        body.LocalVariables.Add(otherLocal);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Stloc, otherLocal);

        // Compare each field using object.Equals(object, object)
        var objEquals = _module.DefaultImporter.ImportMethod(
            typeof(object).GetMethod("Equals", [typeof(object), typeof(object)])!);
        foreach (var field in fields)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, field);
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            il.Add(CilOpCodes.Ldloc, otherLocal);
            il.Add(CilOpCodes.Ldfld, field);
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            il.Add(CilOpCodes.Call, (IMethodDefOrRef)objEquals);
            il.Add(CilOpCodes.Brfalse, returnFalse);
        }

        // All fields matched
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Ret);

        // Return false
        var falseLdc = new CilInstruction(CilOpCodes.Ldc_I4_0);
        returnFalse.Instruction = falseLdc;
        il.Add(falseLdc);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitUnionCaseGetHashCode(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition("GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32));
        caseType.Methods.Add(method);

        var body = new CilMethodBody();
        method.MethodBody = body;
        var il = body.Instructions;

        if (fields.Count == 0)
        {
            il.Add(CilOpCodes.Ldstr, caseType.Name ?? "");
            il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(
                typeof(string).GetMethod("GetHashCode", Type.EmptyTypes)!));
            il.Add(CilOpCodes.Ret);
            return;
        }

        body.InitializeLocals = true;
        var hashCodeType = _module.DefaultImporter.ImportType(typeof(HashCode));
        var hashCodeLocal = new CilLocalVariable(hashCodeType.ToTypeSignature(true));
        body.LocalVariables.Add(hashCodeLocal);

        // Initialize HashCode struct
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Initobj, hashCodeType);

        // Add type name
        var addGenericMethod = typeof(HashCode).GetMethods()
            .First(m => m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1);
        var addString = _module.DefaultImporter.ImportMethod(
            addGenericMethod.MakeGenericMethod(typeof(string)));
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Ldstr, caseType.Name ?? "");
        if (addString is MethodSpecification addStringSpec)
            il.Add(CilOpCodes.Call, addStringSpec);
        else
            il.Add(CilOpCodes.Call, (IMethodDefOrRef)addString);

        // Add each field value (boxed to object)
        var addObject = _module.DefaultImporter.ImportMethod(
            addGenericMethod.MakeGenericMethod(typeof(object)));
        foreach (var field in fields)
        {
            il.Add(CilOpCodes.Ldloca, hashCodeLocal);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, field);
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            if (addObject is MethodSpecification addObjSpec)
                il.Add(CilOpCodes.Call, addObjSpec);
            else
                il.Add(CilOpCodes.Call, (IMethodDefOrRef)addObject);
        }

        // Return hash code
        var toHashCode = _module.DefaultImporter.ImportMethod(typeof(HashCode).GetMethod("ToHashCode")!);
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)toHashCode);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitDeconstruct(TypeDefinition type, List<FieldDefinition> fields)
    {
        if (fields.Count == 0) return;

        var outParamTypes = fields
            .Select(f => (TypeSignature)new ByReferenceTypeSignature(f.Signature!.FieldType))
            .ToArray();

        var method = new MethodDefinition("Deconstruct",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, outParamTypes));

        for (var i = 0; i < fields.Count; i++)
            method.ParameterDefinitions.Add(new ParameterDefinition(
                (ushort)(i + 1), $"p{i}", ParameterAttributes.Out));

        type.Methods.Add(method);

        var body = new CilMethodBody();
        method.MethodBody = body;
        var il = body.Instructions;

        for (var i = 0; i < fields.Count; i++)
        {
            il.Add(CilOpCodes.Ldarg, method.Parameters[i]);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, fields[i]);
            il.Add(CilOpCodes.Stobj, fields[i].Signature!.FieldType.ToTypeDefOrRef());
        }

        il.Add(CilOpCodes.Ret);
    }

    private void RegisterFuncSignature(IrNode.FuncDef func, TypeDefinition typeDefinition)
    {
        var isGeneric = func.TypeParams is { Count: > 0 };

        TypeSignature returnType;
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                returnType = _module.DefaultImporter.ImportType(typeof(Task)).ToTypeSignature(false);
            else
            {
                var taskOpen = _module.DefaultImporter.ImportType(typeof(Task<>));
                returnType = taskOpen.ToTypeSignature(false).MakeGenericInstanceType(false, [MapToClr(func.ReturnType)]);
            }
        }
        else
        {
            returnType = MapReturnTypeToClr(func.ReturnType);
        }

        var paramTypes = func.Params.Select(p => MapToClr(p.Type)).ToArray();
        var methodDef = new MethodDefinition(Sanitize(func.Name),
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(returnType, paramTypes));

        if (isGeneric)
        {
            foreach (var tp in func.TypeParams!)
                methodDef.GenericParameters.Add(new GenericParameter(tp));

            // Re-resolve return type and param types with generic params available
            var savedTypeVarMap = _currentTypeVarMap;
            var savedTypeParamMap = _currentTypeParamMap;
            var varNameMap = BuildTypeVarMap(func);
            _currentTypeVarMap = new Dictionary<int, TypeSignature>();
            _currentTypeParamMap = new Dictionary<string, TypeSignature>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx >= 0)
                {
                    var gpSig = new GenericParameterSignature(_module, GenericParameterType.Method, idx);
                    _currentTypeVarMap[varId] = gpSig;
                    _currentTypeParamMap[paramName] = gpSig;
                }
            }

            if (func.IsAsync)
            {
                if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    returnType = _module.DefaultImporter.ImportType(typeof(Task)).ToTypeSignature(false);
                else
                {
                    var taskOpen = _module.DefaultImporter.ImportType(typeof(Task<>));
                    returnType = taskOpen.ToTypeSignature(false).MakeGenericInstanceType(false, [MapToClr(func.ReturnType)]);
                }
            }
            else
            {
                returnType = MapReturnTypeToClr(func.ReturnType);
            }

            paramTypes = func.Params.Select(p => MapToClr(p.Type)).ToArray();
            methodDef.Signature = MethodSignature.CreateStatic(returnType, func.TypeParams!.Count, paramTypes);

            _currentTypeVarMap = savedTypeVarMap;
            _currentTypeParamMap = savedTypeParamMap;
        }

        for (var i = 0; i < func.Params.Count; i++)
        {
            var paramDef = new ParameterDefinition(
                (ushort)(i + 1), SanitizeParam(func.Params[i].Name), 0);
            if (func.Params[i].IsVariadic)
            {
                var paramArrayCtor = _module.DefaultImporter.ImportMethod(
                    typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!);
                paramDef.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)paramArrayCtor));
            }
            methodDef.ParameterDefinitions.Add(paramDef);
        }

        typeDefinition.Methods.Add(methodDef);
        EmitCustomAttributes(func.Attributes, methodDef);
        _methods[Sanitize(func.Name)] = methodDef;
        if (isGeneric && func.Type is ZType.ZFuncType ft2)
            _genericMethodTypes[Sanitize(func.Name)] = ft2;
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeDefinition typeDefinition)
    {
        RegisterFuncSignature(func, typeDefinition);
        EmitFuncBody(func);
    }

    private void EmitFuncBody(IrNode.FuncDef func)
    {
        Log.Debug("IlEmitter: emitting function {FuncName}, IsAsync={IsAsync}, IsGeneric={IsGeneric}",
            func.Name, func.IsAsync, func.TypeParams is { Count: > 0 });
        var isGeneric = func.TypeParams is { Count: > 0 };
        var sanitized = Sanitize(func.Name);
        if (!_methods.TryGetValue(sanitized, out var methodDef))
            return;
        var typeDefinition = (TypeDefinition)methodDef.DeclaringType!;

        var savedTypeVarMap = _currentTypeVarMap;
        var savedTypeParamMap = _currentTypeParamMap;

        if (isGeneric)
        {
            var varNameMap = BuildTypeVarMap(func);
            _currentTypeVarMap = new Dictionary<int, TypeSignature>();
            _currentTypeParamMap = new Dictionary<string, TypeSignature>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx >= 0)
                {
                    var gpSig = new GenericParameterSignature(_module, GenericParameterType.Method, idx);
                    _currentTypeVarMap[varId] = gpSig;
                    _currentTypeParamMap[paramName] = gpSig;
                }
            }
        }

        if (func.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(func.Body))
        {
            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitAsyncFuncDef(func, methodDef, typeDefinition);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
        }
        else
        {
            var body = new CilMethodBody();
            methodDef.MethodBody = body;
            var il = body.Instructions;
            var locals = new Dictionary<string, CilLocalVariable>();

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
                    if (func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Add(CilOpCodes.Pop);
                    var completedTaskGetter = typeof(Task)
                        .GetProperty("CompletedTask")!.GetGetMethod()!;
                    il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(completedTaskGetter));
                }
                else
                {
                    var fromResult = typeof(Task)
                        .GetMethod("FromResult")!
                        .MakeGenericMethod(IlTypeMapper.MapToClr(func.ReturnType));
                    il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(fromResult));
                }
            }

            il.Add(CilOpCodes.Ret);
        }

        if (isGeneric)
        {
            _currentTypeVarMap = savedTypeVarMap;
            _currentTypeParamMap = savedTypeParamMap;
        }
    }

    private static Dictionary<int, string> BuildTypeVarMap(IrNode.FuncDef func)
    {
        if (func.TypeParams is not { Count: > 0 } || func.Type is not ZType.ZFuncType ft)
            return new Dictionary<int, string>();
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        var map = new Dictionary<int, string>();
        for (var i = 0; i < freeVars.Count && i < func.TypeParams.Count; i++)
            map[freeVars[i]] = func.TypeParams[i];
        return map;
    }

    private void EmitNode(IrNode node, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        switch (node)
        {
            case IrNode.IntConst n:
                il.Add(CilOpCodes.Ldc_I4, n.Value);
                break;

            case IrNode.FloatConst n:
                il.Add(CilOpCodes.Ldc_R4, n.Value);
                break;

            case IrNode.BoolConst n:
                il.Add(n.Value ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                break;

            case IrNode.StringConst n:
                il.Add(CilOpCodes.Ldstr, n.Value);
                break;

            case IrNode.UnitConst:
                break;

            case IrNode.NullConst nullConst:
                // null with Unit type means it was used in a void context (e.g., match arm
                // alongside set!) — don't push anything onto the stack
                if (nullConst.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    break;
                if (nullConst.Type is ZType.ZNullableType)
                {
                    var nullableClrType = MapToClr(nullConst.Type);
                    if (nullableClrType.IsValueType)
                    {
                        // Nullable<T> where T is value type — use initobj
                        var nullableLocal = new CilLocalVariable(nullableClrType);
                        il.Owner.LocalVariables.Add(nullableLocal);
                        il.Owner.InitializeLocals = true;
                        il.Add(CilOpCodes.Ldloca, nullableLocal);
                        il.Add(CilOpCodes.Initobj, nullableClrType.ToTypeDefOrRef());
                        il.Add(CilOpCodes.Ldloc, nullableLocal);
                    }
                    else
                    {
                        // Nullable reference type — just ldnull
                        il.Add(CilOpCodes.Ldnull);
                    }
                }
                else
                {
                    il.Add(CilOpCodes.Ldnull);
                }

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
                EmitIf(@if, il, outerParams, locals);
                break;

            case IrNode.Let let:
                EmitLet(let, il, outerParams, locals);
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

            case IrNode.ClrNew clrNew:
                EmitClrNew(clrNew, il, outerParams, locals);
                break;

            case IrNode.Throw @throw:
                EmitNode(@throw.Expr, il, outerParams, locals);
                il.Add(CilOpCodes.Throw);
                break;

            case IrNode.MethodCall methodCall:
                EmitMethodCall(methodCall, il, outerParams, locals);
                break;

            case IrNode.MutableArrayNew mutableArrayNew:
                EmitMutableArrayNew(mutableArrayNew, il, outerParams, locals);
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

            case IrNode.Seq seq:
                for (var i = 0; i < seq.Nodes.Count; i++)
                {
                    EmitNode(seq.Nodes[i], il, outerParams, locals);
                    if (i < seq.Nodes.Count - 1
                        && seq.Nodes[i].Type is not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Add(CilOpCodes.Pop);
                }

                break;

            case IrNode.Await awaitNode:
                if (_moveNextCtx != null)
                    EmitMoveNextAwait(awaitNode, il, outerParams, locals);
                else
                    EmitAwait(awaitNode, il, outerParams, locals);
                break;

            case IrNode.TryCatch tryCatch:
                EmitTryCatch(tryCatch, il, outerParams, locals);
                break;

            case IrNode.WithHandlers withHandlers:
                EmitWithHandlers(withHandlers, il, outerParams, locals);
                break;

            case IrNode.Propagate propagate:
                EmitPropagate(propagate, il, outerParams, locals);
                break;

            case IrNode.SuperMethodCall superCall:
                EmitSuperMethodCall(superCall, il, outerParams, locals);
                break;

            case IrNode.ObjectExpr objectExpr:
                EmitObjectExpr(objectExpr, il, outerParams, locals);
                break;

            case IrNode.SetField setField:
                if (_moveNextCtx?.ThisField is { } setThisF)
                {
                    il.Add(CilOpCodes.Ldarg_0);
                    il.Add(CilOpCodes.Ldfld, setThisF);
                }
                else
                {
                    il.Add(CilOpCodes.Ldarg_0);
                }
                EmitNode(setField.Value, il, outerParams, locals);
                EmitNullableWrapIfNeeded(setField.Value, _currentClassFields![setField.FieldName].Signature!.FieldType, il);
                il.Add(CilOpCodes.Stfld, _currentClassFields![setField.FieldName]);
                break;

            default:
                diagnostics.Error($"AsmResolver IL emission not implemented for {node.GetType().Name}",
                    SourceSpan.None);
                il.Add(CilOpCodes.Ldc_I4_0);
                break;
        }
    }

    /// <summary>
    ///     If the target type is Nullable&lt;T&gt; and the value type is non-nullable T,
    ///     emit a newobj call to wrap the value on the stack into Nullable&lt;T&gt;.
    /// </summary>
    /// <summary>
    ///     If the target type is Nullable&lt;T&gt; and the value type is non-nullable T,
    ///     emit a newobj call to wrap the value on the stack into Nullable&lt;T&gt;.
    ///     For null constants targeting nullable fields, replaces the ldnull with initobj.
    /// </summary>
    private void EmitNullableWrapIfNeeded(IrNode valueNode, TypeSignature targetClrType, CilInstructionCollection il)
    {
        if (targetClrType is not GenericInstanceTypeSignature git)
            return;

        if (git.GenericType.FullName != "System.Nullable`1")
            return;

        // Handle null constant → nullable: replace ldnull with initobj Nullable<T>
        if (valueNode is IrNode.NullConst)
        {
            // The null constant handler may have emitted ldnull (for unresolved type vars)
            // or initobj (for known nullable types). If the last instruction is ldnull, fix it.
            if (il.Count > 0 && il[il.Count - 1].OpCode == CilOpCodes.Ldnull)
            {
                il.RemoveAt(il.Count - 1);
                var nullableLocal = new CilLocalVariable(git);
                il.Owner.LocalVariables.Add(nullableLocal);
                il.Owner.InitializeLocals = true;
                il.Add(CilOpCodes.Ldloca, nullableLocal);
                il.Add(CilOpCodes.Initobj, git.ToTypeDefOrRef());
                il.Add(CilOpCodes.Ldloc, nullableLocal);
            }
            return;
        }

        // Skip if value is already nullable
        if (valueNode.Type is ZType.ZNullableType)
            return;

        // Target is Nullable<T>, value is T — wrap via Nullable<T>(T value) constructor
        var nullableOpenType = typeof(Nullable<>);
        var openCtor = nullableOpenType.GetConstructors()[0];
        var importedCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(openCtor);
        var ctorRef = new MemberReference(git.ToTypeDefOrRef(),
            importedCtor.Name!, importedCtor.Signature as MethodSignature);
        il.Add(CilOpCodes.Newobj, ctorRef);
    }

    private void EmitIf(IrNode.If @if, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var elseLabel = new CilInstructionLabel();
        var endLabel = new CilInstructionLabel();
        var ifIsUnit = @if.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };
        EmitNode(@if.Condition, il, outerParams, locals);
        il.Add(CilOpCodes.Brfalse, elseLabel);
        EmitNode(@if.Then, il, outerParams, locals);
        ReconcileBranchStack(@if.Then.Type, ifIsUnit, il);
        il.Add(CilOpCodes.Br, endLabel);
        elseLabel.Instruction = il.Add(CilOpCodes.Nop);
        EmitNode(@if.Else, il, outerParams, locals);
        ReconcileBranchStack(@if.Else.Type, ifIsUnit, il);
        endLabel.Instruction = il.Add(CilOpCodes.Nop);
    }

    /// <summary>
    /// Ensures consistent stack depth at merge points when branches have different
    /// stack effects (e.g., one branch is void/Unit, the other pushes a value).
    /// </summary>
    private static void ReconcileBranchStack(ZType branchType, bool overallIsUnit, CilInstructionCollection il)
    {
        var branchIsUnit = branchType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };
        if (overallIsUnit && !branchIsUnit)
            il.Add(CilOpCodes.Pop);
        else if (!overallIsUnit && branchIsUnit)
            il.Add(CilOpCodes.Ldnull);
    }

    private void EmitLet(IrNode.Let let, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        EmitNode(let.Value, il, outerParams, locals);
        if (let.Value.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            EmitNode(let.Body, il, outerParams, locals);
        }
        else
        {
            var local = new CilLocalVariable(MapToClr(let.Value.Type));
            il.Owner.LocalVariables.Add(local);
            il.Add(CilOpCodes.Stloc, local);
            locals[let.VarName] = local;

            // Also save to state machine field if we're inside MoveNext
            if (_moveNextCtx != null && _moveNextCtx.VarFields.TryGetValue(let.VarName, out var smField))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldloc, local);
                il.Add(CilOpCodes.Stfld, smField);
                _moveNextCtx.AllLocals.Add((let.VarName, local));
            }

            EmitNode(let.Body, il, outerParams, locals);
        }
    }

    private void EmitClrNew(IrNode.ClrNew clrNew, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        foreach (var arg in clrNew.Args)
            EmitNode(arg, il, outerParams, locals);

        var type = _clrInterop.FindType(clrNew.QualifiedTypeName);

        // If not found, try as a generic type definition by appending arity suffix
        if (type is null && clrNew.TypeArgs.Count > 0)
        {
            type = _clrInterop.FindType($"{clrNew.QualifiedTypeName}`{clrNew.TypeArgs.Count}");
            if (type is not null)
                type = type.MakeGenericType(clrNew.TypeArgs.Select(t => IlTypeMapper.MapToClr(t)).ToArray());
        }
        // Fallback: use inferred type info
        if (type is null && clrNew.Type is ZType.ZNamedType { TypeArgs: { Count: > 0 } typeArgs })
        {
            type = _clrInterop.FindType($"{clrNew.QualifiedTypeName}`{typeArgs.Count}");
            if (type is not null)
                type = type.MakeGenericType(typeArgs.Select(t => IlTypeMapper.MapToClr(t)).ToArray());
        }

        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrNew.QualifiedTypeName}' not found", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var argTypes = clrNew.Args.Select(a => ResolveClrType(a.Type)).ToArray();
        var ctor = type.GetConstructor(argTypes)
                   ?? type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == argTypes.Length);

        if (ctor is null)
        {
            diagnostics.Error($"No constructor on '{clrNew.QualifiedTypeName}' matches the given arguments",
                SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctor));
    }

    private void EmitClrCall(IrNode.ClrCall clrCall, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var type = _clrInterop.FindType(clrCall.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrCall.QualifiedTypeName}' not found", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (clrCall.OutParams is { Count: > 0 })
        {
            EmitOutParamStaticCall(clrCall, type, il, outerParams, locals);
            return;
        }

        var argTypes = clrCall.Args.Select(a => ResolveClrType(a.Type)).ToArray();

        MethodInfo? method;
        MethodInfo? openGeneric = null;
        if (clrCall.GenericArity > 0)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == clrCall.MethodName
                            && m.IsGenericMethodDefinition
                            && m.GetGenericArguments().Length == clrCall.GenericArity
                            && m.GetParameters().Length == argTypes.Length)
                .ToList();

            openGeneric = candidates.Count == 1 ? candidates[0]
                : candidates.Count > 1 ? candidates.OrderByDescending(m => ScoreGenericOverload(m, argTypes)).First()
                : null;

            method = openGeneric is not null
                ? openGeneric.MakeGenericMethod(InferGenericTypeArgs(openGeneric, argTypes))
                : null;
        }
        else
        {
            method = type.GetMethod(clrCall.MethodName, argTypes);

            // Fallback: exact type matching can fail when nullable types are unwrapped
            // (e.g. float? → float) or when assignable types don't match exactly.
            // Search by name + parameter count, then verify assignability.
            if (method is null)
            {
                var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name == clrCall.MethodName && m.GetParameters().Length == argTypes.Length)
                    .ToList();

                if (candidates.Count == 1)
                {
                    method = candidates[0];
                }
                else if (candidates.Count > 1)
                {
                    // Pick the best match: prefer exact matches, then assignable matches
                    method = candidates.FirstOrDefault(m =>
                    {
                        var ps = m.GetParameters();
                        for (var i = 0; i < ps.Length; i++)
                        {
                            if (!ps[i].ParameterType.IsAssignableFrom(argTypes[i])
                                && !(Nullable.GetUnderlyingType(ps[i].ParameterType) == argTypes[i]))
                                return false;
                        }
                        return true;
                    }) ?? candidates[0];
                }
            }
        }

        if (method is null)
        {
            // Fallback: check for static properties
            var prop = type.GetProperty(clrCall.MethodName, BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetGetMethod() is { } getter)
            {
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getter));
                return;
            }

            // Fallback: check for static fields (enum values, static readonly fields)
            var field = type.GetField(clrCall.MethodName, BindingFlags.Public | BindingFlags.Static);
            if (field is not null)
            {
                if (field.IsLiteral)
                {
                    // Enum/const value — emit as integer constant
                    var constVal = field.GetRawConstantValue();
                    if (constVal is int i) il.Add(CilOpCodes.Ldc_I4, i);
                    else if (constVal is long l) il.Add(CilOpCodes.Ldc_I8, l);
                    else il.Add(CilOpCodes.Ldc_I4, Convert.ToInt32(constVal));
                }
                else
                {
                    // Static field — emit ldsfld
                    il.Add(CilOpCodes.Ldsfld,
                        (IFieldDescriptor)_module.DefaultImporter.ImportField(field));
                }
                return;
            }

            diagnostics.Error($"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' not found",
                SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Emit arguments with boxing/nullable wrapping where needed
        var methodParams = method.GetParameters();
        for (var i = 0; i < clrCall.Args.Count; i++)
        {
            EmitNode(clrCall.Args[i], il, outerParams, locals);
            if (i < methodParams.Length)
            {
                var paramType = methodParams[i].ParameterType;
                if (argTypes[i].IsValueType && !paramType.IsValueType)
                    il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argTypes[i]));

                // Wrap T → Nullable<T> when parameter is Nullable<T> and argument is T
                if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Nullable<>)
                    && clrCall.Args[i].Type is not ZType.ZNullableType)
                {
                    var targetSig = _module.DefaultImporter.ImportType(paramType).ToTypeSignature(paramType.IsValueType);
                    EmitNullableWrapIfNeeded(clrCall.Args[i], targetSig, il);
                }
            }
        }

        // When inside a generic context and the CLR call has generic args, use AsmResolver
        // TypeSignatures from the ZScheme type system to preserve type variables as IL generic
        // parameters (instead of erasing them to object via reflection).
        // Use the generic path only when type args contain unresolved type variables.
        // When all type args are concrete (primitives, named types), the reflection path works correctly.
        var hasTypeVarArgs = clrCall.GenericTypeArgs is { Count: > 0 } &&
            clrCall.GenericTypeArgs.Any(t => t is ZType.ZTypeVar or ZType.ZConstrainedVar);
        if (openGeneric is not null && _currentTypeVarMap is { Count: > 0 } && hasTypeVarArgs)
        {
            var openMethodRef = _module.DefaultImporter.ImportMethod(openGeneric);
            var genericArgSigs = clrCall.GenericTypeArgs!
                .Select(t => MapToClr(t))
                .ToArray();
            Log.Debug("EmitClrCall: generic path for {Type}.{Method}, typeArgs=[{TypeArgs}], sigs=[{Sigs}]",
                clrCall.QualifiedTypeName, clrCall.MethodName,
                string.Join(", ", clrCall.GenericTypeArgs!),
                string.Join(", ", genericArgSigs.Select(s => s.ToString())));
            var gim = new MethodSpecification((IMethodDefOrRef)openMethodRef,
                new GenericInstanceMethodSignature(genericArgSigs));
            il.Add(CilOpCodes.Call, gim);
        }
        else
        {
            Log.Debug("EmitClrCall: reflection path for {Type}.{Method}, resolved={ResolvedMethod}",
                clrCall.QualifiedTypeName, clrCall.MethodName, method);
            il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(method));
        }
    }

    private void EmitOutParamStaticCall(IrNode.ClrCall clrCall, Type type,
        CilInstructionCollection il, IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        var outParams = clrCall.OutParams!;

        // Find the method using the full parameter count (including out params)
        var method = FindMethodWithOutParams(type, clrCall.MethodName, clrCall.Args, outParams,
            BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            diagnostics.Error(
                $"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' with out parameters not found",
                SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Allocate locals for each out parameter
        var outLocals = new List<CilLocalVariable>();
        foreach (var op in outParams)
        {
            var outLocal = new CilLocalVariable(MapToClr(op.ElementType));
            il.Owner.LocalVariables.Add(outLocal);
            outLocals.Add(outLocal);
        }

        // Emit arguments interleaved with ldloca for out params
        var outParamSet = outParams.ToDictionary(op => op.OriginalIndex);
        var totalParams = clrCall.Args.Count + outParams.Count;
        var visibleIdx = 0;
        var outIdx = 0;
        for (var i = 0; i < totalParams; i++)
        {
            if (outParamSet.ContainsKey(i))
            {
                il.Add(CilOpCodes.Ldloca, outLocals[outIdx++]);
            }
            else
            {
                EmitNode(clrCall.Args[visibleIdx], il, outerParams, locals);
                var methodParam = method.GetParameters()[i];
                if (methodParam.ParameterType == typeof(object) &&
                    clrCall.Args[visibleIdx].Type is ZType.ZPrimitiveType)
                    il.Add(CilOpCodes.Box,
                        _module.DefaultImporter.ImportType(IlTypeMapper.MapToClr(clrCall.Args[visibleIdx].Type)));
                visibleIdx++;
            }
        }

        // Call the method
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(method));

        // Store the return value, then construct ValueTuple
        var retClrType = MapToClr(ClrInterop.MapClrTypeToZType(method.ReturnType));
        var retLocal = new CilLocalVariable(retClrType);
        il.Owner.LocalVariables.Add(retLocal);
        il.Add(CilOpCodes.Stloc, retLocal);

        il.Add(CilOpCodes.Ldloc, retLocal);
        foreach (var outLocal in outLocals)
            il.Add(CilOpCodes.Ldloc, outLocal);

        var tupleClrType = IlTypeMapper.MapToClr(clrCall.Type);
        var tupleCtor = tupleClrType.GetConstructors().FirstOrDefault();
        if (tupleCtor is not null)
            il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(tupleCtor));
    }

    private static int ScoreGenericOverload(MethodInfo method, Type[] argTypes)
    {
        var score = 0;
        var methodParams = method.GetParameters();
        for (var i = 0; i < methodParams.Length && i < argTypes.Length; i++)
        {
            var paramType = methodParams[i].ParameterType;
            // Prefer array-of-generic (T[]) when arg is also an array — more specific than bare T
            if (paramType.IsArray && paramType.GetElementType()!.IsGenericParameter && argTypes[i].IsArray)
                score += 12;
            else if (paramType.IsGenericParameter) score += 10;
            else if (paramType == argTypes[i]) score += 8;
            else if (paramType.IsAssignableFrom(argTypes[i])) score += 5;
        }

        return score;
    }

    private static Type[] InferGenericTypeArgs(MethodInfo genericMethod, Type[] argTypes)
    {
        var genericParams = genericMethod.GetGenericArguments();
        var methodParams = genericMethod.GetParameters();
        var result = new Type[genericParams.Length];
        for (var i = 0; i < methodParams.Length && i < argTypes.Length; i++)
            MatchTypeArgs(methodParams[i].ParameterType, argTypes[i], result);
        for (var i = 0; i < result.Length; i++)
            result[i] ??= typeof(object);
        return result;
    }

    private static void MatchTypeArgs(Type formal, Type actual, Type[] result)
    {
        if (formal.IsGenericParameter)
        {
            var pos = formal.GenericParameterPosition;
            if (result[pos] is null)
            {
                result[pos] = actual;
            }
            else if (result[pos] != actual)
            {
                // Generic param already bound to a different type. Keep the more general
                // type to support boxing (e.g., ^v bound to Object from map, Int from value arg).
                if (result[pos].IsAssignableFrom(actual) || (!actual.IsValueType && actual.IsAssignableFrom(result[pos])))
                {
                    // Keep existing (it's more general, or they're in a subtype relationship)
                }
                else if (actual.IsAssignableFrom(result[pos]))
                {
                    result[pos] = actual; // New type is more general
                }
                else if (actual == typeof(object) || result[pos] == typeof(object))
                {
                    result[pos] = typeof(object); // Boxing: keep Object
                }
                // else: keep existing (ambiguous, first-match wins)
            }
            return;
        }

        // Handle array-of-generic-param: T[] matching int[] → T = int
        if (formal.IsArray && actual.IsArray)
        {
            MatchTypeArgs(formal.GetElementType()!, actual.GetElementType()!, result);
            return;
        }

        if (formal.IsGenericType && actual.IsGenericType
                                 && formal.GetGenericTypeDefinition() == actual.GetGenericTypeDefinition())
        {
            var formalArgs = formal.GetGenericArguments();
            var actualArgs = actual.GetGenericArguments();
            for (var j = 0; j < formalArgs.Length && j < actualArgs.Length; j++)
                MatchTypeArgs(formalArgs[j], actualArgs[j], result);
            return;
        }

        // Interface-based matching: if formal is a generic interface, check actual's interfaces
        if (formal.IsGenericType && formal.GetGenericTypeDefinition().IsInterface)
        {
            var formalDef = formal.GetGenericTypeDefinition();
            var match = actual.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == formalDef);
            if (match is not null)
            {
                var formalArgs = formal.GetGenericArguments();
                var matchArgs = match.GetGenericArguments();
                for (var j = 0; j < formalArgs.Length && j < matchArgs.Length; j++)
                    MatchTypeArgs(formalArgs[j], matchArgs[j], result);
            }
        }
    }

    private void EmitCall(IrNode.Call call, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        if (call.Function is IrNode.Var v)
        {
            var sanitized = Sanitize(v.Name);

            // Check defined methods
            if (_methods.TryGetValue(sanitized, out var methodDef))
            {
                if (methodDef.GenericParameters.Count > 0)
                {
                    var typeArgs = InferTypeArgsForCall(sanitized, methodDef, call.Args);
                    var gim = new MethodSpecification(methodDef,
                        new GenericInstanceMethodSignature(typeArgs));

                    // Emit arguments with boxing where value types are passed as reference type params
                    var sig = methodDef.Signature!;
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (i < sig.ParameterTypes.Count)
                        {
                            var paramSig = sig.ParameterTypes[i];
                            // Resolve generic parameter signatures to concrete types
                            var resolvedParam = paramSig is GenericParameterSignature gps
                                && gps.Index < typeArgs.Length
                                ? typeArgs[gps.Index]
                                : paramSig;
                            var argClrType = IlTypeMapper.MapToClr(call.Args[i].Type);
                            if (argClrType.IsValueType && !resolvedParam.IsValueType)
                                il.Add(CilOpCodes.Box,
                                    _module.DefaultImporter.ImportType(argClrType));
                        }
                    }

                    il.Add(CilOpCodes.Call, gim);
                }
                else
                {
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    il.Add(CilOpCodes.Call, methodDef);
                }

                return;
            }

            // Check precompiled methods
            if (_precompiledMethods.TryGetValue(sanitized, out var precompiledMethod))
            {
                if (_precompiledReflectionMethods.TryGetValue(sanitized, out var reflectionMethod)
                    && reflectionMethod.IsGenericMethodDefinition)
                {
                    var argClrTypes = call.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
                    var instantiated = reflectionMethod.MakeGenericMethod(
                        InferGenericTypeArgs(reflectionMethod, argClrTypes));

                    // Emit arguments with boxing where value types are passed as reference types
                    var instParams = instantiated.GetParameters();
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (i < instParams.Length && argClrTypes[i].IsValueType
                            && !instParams[i].ParameterType.IsValueType)
                            il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argClrTypes[i]));
                    }

                    var importedGeneric = _module.DefaultImporter.ImportMethod(instantiated);
                    if (importedGeneric is MethodSpecification methodSpec)
                        il.Add(CilOpCodes.Call, methodSpec);
                    else
                        il.Add(CilOpCodes.Call, (IMethodDefOrRef)importedGeneric);
                }
                else
                {
                    // Non-generic precompiled method: emit args with boxing where needed
                    var preParams = reflectionMethod?.GetParameters();
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (preParams is not null && i < preParams.Length)
                        {
                            var argClrType = IlTypeMapper.MapToClr(call.Args[i].Type);
                            if (argClrType.IsValueType && !preParams[i].ParameterType.IsValueType)
                                il.Add(CilOpCodes.Box,
                                    _module.DefaultImporter.ImportType(argClrType));
                        }
                    }
                    il.Add(CilOpCodes.Call, (IMethodDefOrRef)precompiledMethod);
                }

                return;
            }

            // Check locals (delegate invocation)
            if (locals.TryGetValue(v.Name, out var delegateLocal))
            {
                il.Add(CilOpCodes.Ldloc, delegateLocal);
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                EmitDelegateInvoke(call.Function.Type, il);
                return;
            }

            // Check parameters (delegate)
            for (var i = 0; i < outerParams.Count; i++)
                if (outerParams[i].Name == v.Name && outerParams[i].Type is ZType.ZFuncType)
                {
                    var argIndex = i + _instanceArgOffset;
                    var method = (MethodDefinition)il.Owner!.Owner!;
                    il.Add(CilOpCodes.Ldarg, method.Parameters[argIndex]);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(outerParams[i].Type, il);
                    return;
                }

            // Check static fields
            if (_staticFields.TryGetValue(v.Name, out var staticField))
            {
                if (call.Function.Type is ZType.ZFuncType)
                {
                    il.Add(CilOpCodes.Ldsfld, staticField);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(call.Function.Type, il);
                    return;
                }
            }

            diagnostics.Error($"Function '{v.Name}' not found for AsmResolver IL emission", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Non-Var target: emit expression, then invoke
        EmitNode(call.Function, il, outerParams, locals);
        foreach (var arg in call.Args)
            EmitNode(arg, il, outerParams, locals);
        if (call.Function.Type is ZType.ZFuncType)
        {
            EmitDelegateInvoke(call.Function.Type, il);
            return;
        }

        diagnostics.Error($"AsmResolver IL emission not implemented for Call with {call.Function.GetType().Name} target",
            SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private TypeSignature[] InferTypeArgsForCall(string sanitizedName, MethodDefinition genericMethod,
        IReadOnlyList<IrNode> args)
    {
        var genericArgCount = genericMethod.GenericParameters.Count;

        if (_genericMethodTypes.TryGetValue(sanitizedName, out var funcType))
        {
            var result = new TypeSignature[genericArgCount];
            var freeVars = Substitution.FreeVars(funcType).OrderBy(id => id).ToList();
            for (var i = 0; i < funcType.Params.Count && i < args.Count; i++)
            {
                var actualType = args[i].Type;
                // For variadic functions, the formal param is the element type T but the
                // actual arg (after varargs packing) is Mutable-Array[T]. Unwrap it.
                if (funcType.IsVariadic && i == funcType.Params.Count - 1
                    && actualType is ZType.ZNamedType { Name: "Mutable-Array", TypeArgs: [var elemType] })
                    actualType = elemType;
                MatchZTypeArgs(funcType.Params[i], actualType, freeVars, result);
            }
            for (var i = 0; i < result.Length; i++)
                result[i] ??= _module.CorLibTypeFactory.Object;
            return result;
        }

        // Fallback
        var fallback = new TypeSignature[genericArgCount];
        for (var i = 0; i < fallback.Length; i++)
            fallback[i] = _module.CorLibTypeFactory.Object;
        return fallback;
    }

    private void MatchZTypeArgs(ZType formal, ZType actual, List<int> freeVarIds, TypeSignature[] result)
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
            for (var i = 0; i < fn.TypeArgs.Count && i < an.TypeArgs.Count; i++)
                MatchZTypeArgs(fn.TypeArgs[i], an.TypeArgs[i], freeVarIds, result);
        if (formal is ZType.ZFuncType ff && actual is ZType.ZFuncType af)
        {
            for (var i = 0; i < ff.Params.Count && i < af.Params.Count; i++)
                MatchZTypeArgs(ff.Params[i], af.Params[i], freeVarIds, result);
            MatchZTypeArgs(ff.Return, af.Return, freeVarIds, result);
        }
    }

    private void EmitMatch(IrNode.Match match, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var scrutineeType = MapToClr(match.Scrutinee.Type);
        var scrutineeLocal = new CilLocalVariable(scrutineeType);
        il.Owner.LocalVariables.Add(scrutineeLocal);
        EmitNode(match.Scrutinee, il, outerParams, locals);
        il.Add(CilOpCodes.Stloc, scrutineeLocal);

        var endLabel = new CilInstructionLabel();
        var armLabels = new CilInstructionLabel[match.Arms.Count];
        for (var i = 0; i < match.Arms.Count; i++)
            armLabels[i] = new CilInstructionLabel();

        var failLabel = new CilInstructionLabel();
        var matchIsUnit = match.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

        for (var i = 0; i < match.Arms.Count; i++)
        {
            armLabels[i].Instruction = il.Add(CilOpCodes.Nop);
            var arm = match.Arms[i];
            var nextLabel = i + 1 < match.Arms.Count ? armLabels[i + 1] : failLabel;

            if (i > 0 && match.Arms[i - 1].Pattern is IrPattern.Constructor)
                il.Add(CilOpCodes.Pop);

            EmitPatternTest(arm.Pattern, scrutineeLocal, match.Scrutinee.Type, nextLabel, il, outerParams, locals);
            EmitNode(arm.Body, il, outerParams, locals);
            ReconcileBranchStack(arm.Body.Type, matchIsUnit, il);
            il.Add(CilOpCodes.Br, endLabel);
        }

        failLabel.Instruction = il.Add(CilOpCodes.Nop);
        if (match.Arms.Count > 0 && match.Arms[^1].Pattern is IrPattern.Constructor)
            il.Add(CilOpCodes.Pop);
        il.Add(CilOpCodes.Ldstr, "Non-exhaustive match");
        var exCtor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(exCtor));
        il.Add(CilOpCodes.Throw);

        endLabel.Instruction = il.Add(CilOpCodes.Nop);
    }

    private void EmitPatternTest(IrPattern pattern, CilLocalVariable scrutineeLocal, ZType scrutineeType,
        ICilLabel failLabel, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                break;

            case IrPattern.Variable v:
                var bindLocal = new CilLocalVariable(scrutineeLocal.VariableType);
                il.Owner.LocalVariables.Add(bindLocal);
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Stloc, bindLocal);
                locals[v.Name] = bindLocal;
                break;

            case IrPattern.Literal { Value: string s }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Ldstr, s);
                var strEquals = typeof(string).GetMethod("Equals", BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)])!;
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(strEquals));
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: int n }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Ldc_I4, n);
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: bool b }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(b ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Constructor c:
                EmitConstructorPatternTest(c, scrutineeLocal, scrutineeType, failLabel, il, outerParams, locals);
                break;
        }
    }

    private void EmitConstructorPatternTest(IrPattern.Constructor ctor, CilLocalVariable scrutineeLocal,
        ZType scrutineeType, ICilLabel failLabel, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var caseTypeDefOrRef = ResolveConstructorCaseType(ctor.Name, scrutineeType);
        if (caseTypeDefOrRef is null)
        {
            diagnostics.Error($"Cannot resolve constructor type '{ctor.Name}' for pattern match", SourceSpan.None);
            return;
        }

        il.Add(CilOpCodes.Ldloc, scrutineeLocal);
        il.Add(CilOpCodes.Isinst, caseTypeDefOrRef);
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Brfalse, failLabel);

        var caseTypeSig = caseTypeDefOrRef.ToTypeSignature(false);
        var castLocal = new CilLocalVariable(caseTypeSig);
        il.Owner.LocalVariables.Add(castLocal);
        il.Add(CilOpCodes.Stloc, castLocal);

        if (ctor.Fields.Count > 0)
        {
            string? caseKey = null;
            if (scrutineeType is ZType.ZNamedType named)
                caseKey = $"{named.Name}.{ctor.Name}";

            List<string> propertyNames;
            if (caseKey is not null && _unionCasePropertyNames.TryGetValue(caseKey, out var storedNames))
                propertyNames = storedNames.ToList();
            else
                propertyNames = Enumerable.Range(0, ctor.Fields.Count).Select(_ => "Value").ToList();

            for (var i = 0; i < ctor.Fields.Count; i++)
            {
                var field = ctor.Fields[i];
                if (field is IrPattern.Variable v)
                {
                    var propName = i < propertyNames.Count ? propertyNames[i] : "Value";
                    var getterKey = caseKey is not null ? $"{caseKey}.{propName}" : null;

                    if (getterKey is not null && _unionCaseGetters.TryGetValue(getterKey, out var getter))
                    {
                        if (caseTypeSig is GenericInstanceTypeSignature git)
                        {
                            // For generic union cases, create a MemberReference on the closed TypeSpec
                            // keeping the original !0-based signature from the MethodDefinition
                            IMethodDefOrRef resolvedGetter;
                            TypeSignature fieldType;
                            if (getter is MethodDefinition getterDef)
                            {
                                resolvedGetter = new MemberReference(git.ToTypeDefOrRef(),
                                    getterDef.Name!, getterDef.Signature!);
                                // Resolve the local variable type from !0 to the actual type arg
                                var retSig = getterDef.Signature!.ReturnType;
                                fieldType = retSig is GenericParameterSignature gps
                                            && gps.Index < git.TypeArguments.Count
                                    ? git.TypeArguments[gps.Index]
                                    : retSig;
                            }
                            else
                            {
                                // Precompiled getter: create a MemberReference on the closed TypeSpec
                                // so the CLR can resolve the method on the concrete generic instance
                                var importedGetter = (IMethodDefOrRef)getter;
                                resolvedGetter = new MemberReference(git.ToTypeDefOrRef(),
                                    importedGetter.Name!, importedGetter.Signature!);
                                // Resolve the return type: replace generic params with actual type args
                                var retSig = importedGetter.Signature!.ReturnType;
                                fieldType = retSig is GenericParameterSignature gps2
                                            && gps2.Index < git.TypeArguments.Count
                                    ? git.TypeArguments[gps2.Index]
                                    : retSig;
                            }

                            var fieldLocal = new CilLocalVariable(fieldType);
                            il.Owner.LocalVariables.Add(fieldLocal);
                            il.Add(CilOpCodes.Ldloc, castLocal);
                            il.Add(CilOpCodes.Callvirt, resolvedGetter);
                            il.Add(CilOpCodes.Stloc, fieldLocal);
                            locals[v.Name] = fieldLocal;
                        }
                        else
                        {
                            // Non-generic: use getter directly
                            TypeSignature fieldType;
                            if (getter is MethodDefinition getterDef2)
                                fieldType = getterDef2.Signature!.ReturnType;
                            else
                                fieldType = MapToClr(scrutineeType);

                            var fieldLocal = new CilLocalVariable(fieldType);
                            il.Owner.LocalVariables.Add(fieldLocal);
                            il.Add(CilOpCodes.Ldloc, castLocal);
                            il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)getter);
                            il.Add(CilOpCodes.Stloc, fieldLocal);
                            locals[v.Name] = fieldLocal;
                        }
                    }
                }
            }
        }
    }

    private ITypeDefOrRef? ResolveConstructorCaseType(string caseName, ZType scrutineeType)
    {
        if (scrutineeType is ZType.ZNamedType named)
        {
            var caseKey = $"{named.Name}.{caseName}";
            if (_unionCaseTypes.TryGetValue(caseKey, out var caseType))
            {
                Log.Debug("ResolveConstructorCaseType: caseKey={CaseKey}, caseType={CaseType}, typeArgs={TypeArgs}",
                    caseKey, caseType.GetType().Name, named.TypeArgs.Count);
                if (named.TypeArgs.Count > 0)
                {
                    var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                    if (caseType is TypeDefinition td && td.GenericParameters.Count > 0)
                        return td.MakeGenericInstanceType(false, typeArgs).ToTypeDefOrRef();
                    // Imported type reference (precompiled) — create generic instance
                    return new GenericInstanceTypeSignature(caseType, false, typeArgs).ToTypeDefOrRef();
                }

                return caseType;
            }
        }

        return null;
    }

    private void EmitMethodCall(IrNode.MethodCall node, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var receiverClrType = ResolveClrType(node.Receiver.Type);
        var isValueType = receiverClrType.IsValueType;
        CilLocalVariable? receiverLocal = null;

        EmitNode(node.Receiver, il, outerParams, locals);

        if (isValueType)
        {
            receiverLocal = new CilLocalVariable(MapToClr(node.Receiver.Type));
            il.Owner.LocalVariables.Add(receiverLocal);
            il.Add(CilOpCodes.Stloc, receiverLocal);
            il.Add(CilOpCodes.Ldloca, receiverLocal);
        }

        if (node.IsProperty)
        {
            // Arrays: use ldlen for Length property
            if (receiverClrType.IsArray && node.MethodName == "Length")
            {
                il.Add(CilOpCodes.Ldlen);
                il.Add(CilOpCodes.Conv_I4);
                return;
            }

            // Try TypeDefinition first (for types defined in this compilation)
            if (node.Receiver.Type is ZType.ZNamedType named
                && _userTypes.TryGetValue(named.Name, out var typeRef)
                && typeRef is TypeDefinition td)
            {
                var sanitizedMethodName = Sanitize(node.MethodName);
                var asmProp = td.Properties.FirstOrDefault(p => p.Name == sanitizedMethodName);
                var asmGetter = asmProp?.Semantics
                    .FirstOrDefault(s => s.Attributes == AsmMethodSemanticsAttributes.Getter)?.Method;
                if (asmGetter is not null)
                {
                    // For generic types, create a MemberReference on the closed generic instance
                    if (td.GenericParameters.Count > 0 && named.TypeArgs.Count > 0)
                    {
                        var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                        var closedSig = td.MakeGenericInstanceType(false, typeArgs);
                        var getterRef = new MemberReference(closedSig.ToTypeDefOrRef(),
                            asmGetter.Name!, asmGetter.Signature!);
                        il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, getterRef);
                    }
                    else
                    {
                        il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, asmGetter);
                    }

                    return;
                }
            }

            // Resolve using the raw CLR type for proper generic instantiation
            var rawClrType = receiverClrType;
            var ilMappedType = IlTypeMapper.MapToClr(node.Receiver.Type);
            if (ilMappedType != typeof(object))
                rawClrType = ilMappedType;
            var prop = rawClrType.GetProperty(node.MethodName);
            if (prop is null && rawClrType.IsGenericType)
                prop = rawClrType.GetGenericTypeDefinition().GetProperty(node.MethodName);
            if (prop is not null)
            {
                var getter = prop.GetGetMethod()!;
                il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(getter, node.Receiver.Type));
                return;
            }

            diagnostics.Warning($"Property '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (node.IsPropertySet)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            var rawClrType = receiverClrType;
            var ilMappedType = IlTypeMapper.MapToClr(node.Receiver.Type);
            if (ilMappedType != typeof(object))
                rawClrType = ilMappedType;
            var prop = rawClrType.GetProperty(node.MethodName);
            if (prop is null && rawClrType.IsGenericType)
                prop = rawClrType.GetGenericTypeDefinition().GetProperty(node.MethodName);
            if (prop is not null)
            {
                var setter = prop.GetSetMethod()!;
                il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(setter, node.Receiver.Type));
                return;
            }

            diagnostics.Error($"Property setter '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
            return;
        }

        if (node.IsIndexer)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            if (receiverClrType.IsArray)
            {
                var elemType = MapToClr(((ZType.ZNamedType)node.Receiver.Type).TypeArgs[0]);
                il.Add(CilOpCodes.Ldelem, elemType.ToTypeDefOrRef());
                return;
            }

            var indexer = receiverClrType.GetMethod("get_Item");
            if (indexer is not null)
            {
                il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(indexer, node.Receiver.Type));
                return;
            }

            diagnostics.Error($"Indexer not found on {receiverClrType}", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (node.IsIndexerSet)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            EmitNode(node.Args[1], il, outerParams, locals);
            if (receiverClrType.IsArray)
            {
                var elemType = MapToClr(((ZType.ZNamedType)node.Receiver.Type).TypeArgs[0]);
                il.Add(CilOpCodes.Stelem, elemType.ToTypeDefOrRef());
                return;
            }

            var setter = receiverClrType.GetMethod("set_Item")
                         ?? (receiverClrType.IsGenericType
                             ? receiverClrType.GetGenericTypeDefinition().GetMethod("set_Item")
                             : null);
            if (setter is not null)
            {
                il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(setter, node.Receiver.Type));
                return;
            }

            diagnostics.Error($"Indexer setter not found on {receiverClrType}", SourceSpan.None);
            return;
        }

        if (node.OutParams is { Count: > 0 })
        {
            EmitOutParamMethodCall(node, receiverClrType, isValueType, il, outerParams, locals);
            return;
        }

        var argTypes = node.Args.Select(a => ResolveClrType(a.Type)).ToArray();
        MethodInfo? methodInfo;
        try
        {
            methodInfo = receiverClrType.GetMethod(node.MethodName, argTypes)
                         ?? receiverClrType.GetMethod(node.MethodName,
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                             null, argTypes, null);
        }
        catch (AmbiguousMatchException)
        {
            // Fall back to matching by arg count when multiple overloads exist
            methodInfo = receiverClrType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == node.MethodName && m.GetParameters().Length == argTypes.Length);
        }

        // Fallback: match by arg count if exact type match failed
        methodInfo ??= receiverClrType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == node.MethodName && m.GetParameters().Length == argTypes.Length);

        // Emit arguments with boxing/nullable wrapping where needed
        var methodParams = methodInfo?.GetParameters();
        for (var i = 0; i < node.Args.Count; i++)
        {
            EmitNode(node.Args[i], il, outerParams, locals);
            if (methodParams is not null && i < methodParams.Length)
            {
                var paramType = methodParams[i].ParameterType;
                if (argTypes[i].IsValueType && !paramType.IsValueType)
                    il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argTypes[i]));

                // Wrap T → Nullable<T> when parameter is Nullable<T> and argument is T
                if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Nullable<>)
                    && node.Args[i].Type is not ZType.ZNullableType)
                {
                    var targetSig = _module.DefaultImporter.ImportType(paramType).ToTypeSignature(paramType.IsValueType);
                    EmitNullableWrapIfNeeded(node.Args[i], targetSig, il);
                }
            }
        }

        if (methodInfo is not null && methodInfo.GetParameters().Length == argTypes.Length)
        {
            il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                ImportMethodWithGenericDeclaringType(methodInfo, node.Receiver.Type));
            return;
        }

        // Fallback: check for instance properties
        var instanceProp = receiverClrType.GetProperty(node.MethodName,
            BindingFlags.Public | BindingFlags.Instance);
        if (instanceProp?.GetGetMethod() is { } propGetter && node.Args.Count == 0)
        {
            il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                _module.DefaultImporter.ImportMethod(propGetter));
            return;
        }

        diagnostics.Warning($"Property '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitOutParamMethodCall(IrNode.MethodCall node, Type receiverClrType, bool isValueType,
        CilInstructionCollection il, IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        var outParams = node.OutParams!;

        // Resolve the method using the full parameter list (including out params)
        var method = FindMethodWithOutParams(receiverClrType, node.MethodName, node.Args, outParams,
            BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
        {
            diagnostics.Error($"Method '{node.MethodName}' with out parameters not found on {receiverClrType}",
                SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Allocate locals for each out parameter
        var outLocals = new List<CilLocalVariable>();
        foreach (var op in outParams)
        {
            var elemClrType = IlTypeMapper.MapToClr(op.ElementType);
            var outLocal = new CilLocalVariable(MapToClr(op.ElementType));
            il.Owner.LocalVariables.Add(outLocal);
            outLocals.Add(outLocal);
        }

        // Emit arguments interleaved with ldloca for out params
        var outParamSet = outParams.ToDictionary(op => op.OriginalIndex);
        var totalParams = node.Args.Count + outParams.Count;
        var visibleIdx = 0;
        var outIdx = 0;
        for (var i = 0; i < totalParams; i++)
        {
            if (outParamSet.ContainsKey(i))
            {
                il.Add(CilOpCodes.Ldloca, outLocals[outIdx++]);
            }
            else
            {
                EmitNode(node.Args[visibleIdx++], il, outerParams, locals);
                // Box value types if needed for object parameters
                var methodParam = method.GetParameters()[i];
                if (methodParam.ParameterType == typeof(object) && node.Args[visibleIdx - 1].Type is ZType.ZPrimitiveType)
                    il.Add(CilOpCodes.Box, MapToClr(node.Args[visibleIdx - 1].Type).ToTypeDefOrRef());
            }
        }

        // Call the method
        il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
            ImportMethodWithGenericDeclaringType(method, node.Receiver.Type));

        // Store the return value in a local
        var retClrType = MapToClr(ClrInterop.MapClrTypeToZType(method.ReturnType));
        var retLocal = new CilLocalVariable(retClrType);
        il.Owner.LocalVariables.Add(retLocal);
        il.Add(CilOpCodes.Stloc, retLocal);

        // Construct the ValueTuple: load ret, out0, out1, ...
        il.Add(CilOpCodes.Ldloc, retLocal);
        foreach (var outLocal in outLocals)
            il.Add(CilOpCodes.Ldloc, outLocal);

        // Construct the ValueTuple
        var tupleClrType = IlTypeMapper.MapToClr(node.Type);
        var tupleCtor = tupleClrType.GetConstructors().FirstOrDefault();
        if (tupleCtor is not null)
            il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(tupleCtor));
    }

    private MethodInfo? FindMethodWithOutParams(Type type, string methodName, IReadOnlyList<IrNode> visibleArgs,
        IReadOnlyList<ClrInterop.OutParamInfo> outParams, BindingFlags flags)
    {
        var totalParams = visibleArgs.Count + outParams.Count;
        var outIndexSet = new HashSet<int>(outParams.Select(op => op.OriginalIndex));

        return type.GetMethods(flags)
            .Where(m => m.Name == methodName && m.GetParameters().Length == totalParams)
            .FirstOrDefault(m =>
            {
                var parameters = m.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                    if (outIndexSet.Contains(i) && !parameters[i].IsOut)
                        return false;
                return true;
            });
    }

    private void EmitMutableArrayNew(IrNode.MutableArrayNew node, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        var elementSigType = MapToClr(node.ElementType);

        il.Add(CilOpCodes.Ldc_I4, node.Elements.Count);
        il.Add(CilOpCodes.Newarr, elementSigType.ToTypeDefOrRef());

        for (var i = 0; i < node.Elements.Count; i++)
        {
            il.Add(CilOpCodes.Dup);
            il.Add(CilOpCodes.Ldc_I4, i);
            EmitNode(node.Elements[i], il, outerParams, locals);
            // Box value types when element type is object
            if (elementSigType == _module.CorLibTypeFactory.Object)
            {
                var elemClrType = MapToClr(node.Elements[i].Type);
                if (elemClrType.IsValueType)
                    il.Add(CilOpCodes.Box, elemClrType.ToTypeDefOrRef());
            }
            il.Add(CilOpCodes.Stelem, elementSigType.ToTypeDefOrRef());
        }
    }

    private void EmitLambda(IrNode.FuncDef funcDef, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        var lambdaName = $"__lambda_{_lambdaId++}_{funcDef.Name}";
        var paramNames = funcDef.Params.Select(p => p.Name).ToHashSet();
        var freeVars = FindFreeVars(funcDef.Body, paramNames);

        var captures = new List<(string Name, TypeSignature SigType, Type ClrType)>();
        foreach (var fv in freeVars)
            if (locals.TryGetValue(fv, out var loc))
                captures.Add((fv, loc.VariableType,
                    IlTypeMapper.MapToClr(GetVarType(fv, outerParams, locals) ?? ZType.Unit)));
            else
                for (var i = 0; i < outerParams.Count; i++)
                    if (outerParams[i].Name == fv)
                    {
                        captures.Add((fv, MapToClr(outerParams[i].Type),
                            IlTypeMapper.MapToClr(outerParams[i].Type)));
                        break;
                    }

        if (captures.Count == 0)
        {
            EmitFuncDef(funcDef with { Name = lambdaName }, _currentTypeDefinition!);
            var lambdaMethod = _methods[Sanitize(lambdaName)];
            il.Add(CilOpCodes.Ldnull);
            il.Add(CilOpCodes.Ldftn, lambdaMethod);
            il.Add(CilOpCodes.Newobj, ImportDelegateConstructor(funcDef.Type));
        }
        else
        {
            var closureType = new TypeDefinition("", $"<>c__{lambdaName}",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class);
            closureType.BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
            _currentTypeDefinition!.NestedTypes.Add(closureType);

            var captureFields = new List<FieldDefinition>();
            foreach (var (name, sigType, _) in captures)
            {
                var fb = new FieldDefinition(name, FieldAttributes.Public, new FieldSignature(sigType));
                closureType.Fields.Add(fb);
                captureFields.Add(fb);
            }

            var closureCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
            closureType.Methods.Add(closureCtor);
            var closureCtorBody = new CilMethodBody();
            closureCtor.MethodBody = closureCtorBody;
            var closureCtorIl = closureCtorBody.Instructions;
            closureCtorIl.Add(CilOpCodes.Ldarg_0);
            closureCtorIl.Add(CilOpCodes.Call,
                _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!));
            closureCtorIl.Add(CilOpCodes.Ret);

            var lambdaReturnType = MapReturnTypeToClr(funcDef.ReturnType);
            var lambdaParamTypes = funcDef.Params.Select(p => MapToClr(p.Type)).ToArray();
            var lambdaMethod = new MethodDefinition("Invoke",
                MethodAttributes.Public,
                MethodSignature.CreateInstance(lambdaReturnType, lambdaParamTypes));
            for (var i = 0; i < funcDef.Params.Count; i++)
                lambdaMethod.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(i + 1), funcDef.Params[i].Name, 0));
            closureType.Methods.Add(lambdaMethod);

            var lambdaBody = new CilMethodBody();
            lambdaMethod.MethodBody = lambdaBody;
            var lambdaIl = lambdaBody.Instructions;
            var lambdaLocals = new Dictionary<string, CilLocalVariable>();

            for (var i = 0; i < captures.Count; i++)
            {
                var captureLocal = new CilLocalVariable(captures[i].SigType);
                lambdaBody.LocalVariables.Add(captureLocal);
                lambdaIl.Add(CilOpCodes.Ldarg_0);
                lambdaIl.Add(CilOpCodes.Ldfld, captureFields[i]);
                lambdaIl.Add(CilOpCodes.Stloc, captureLocal);
                lambdaLocals[captures[i].Name] = captureLocal;
            }

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 1;
            _currentFuncReturnType = funcDef.ReturnType;
            EmitNode(funcDef.Body, lambdaIl, funcDef.Params, lambdaLocals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
            lambdaIl.Add(CilOpCodes.Ret);

            // Emit closure instantiation
            il.Add(CilOpCodes.Newobj, closureCtor);
            for (var i = 0; i < captures.Count; i++)
            {
                il.Add(CilOpCodes.Dup);
                EmitLoadVar(captures[i].Name, il, outerParams, locals);
                il.Add(CilOpCodes.Stfld, captureFields[i]);
            }

            il.Add(CilOpCodes.Ldftn, lambdaMethod);
            il.Add(CilOpCodes.Newobj, ImportDelegateConstructor(funcDef.Type));
        }
    }

    private void EmitObjectExpr(IrNode.ObjectExpr objectExpr, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        // Capture analysis: collect free vars across all methods
        var allFreeVars = new HashSet<string>();
        foreach (var method in objectExpr.Methods)
        {
            var paramNames = method.Params.Select(p => p.Name).ToHashSet();
            allFreeVars.UnionWith(FindFreeVars(method.Body, paramNames));
        }

        var captures = new List<(string Name, TypeSignature SigType)>();
        foreach (var fv in allFreeVars)
            if (locals.TryGetValue(fv, out var loc))
                captures.Add((fv, loc.VariableType));
            else
            {
                for (var i = 0; i < outerParams.Count; i++)
                    if (outerParams[i].Name == fv)
                    {
                        captures.Add((fv, MapToClr(outerParams[i].Type)));
                        break;
                    }
            }

        // Create anonymous class type
        var objClassName = $"<>__Object_{_objectExprId++}";
        var objType = new TypeDefinition("", objClassName,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class);

        // Resolve base type
        ITypeDefOrRef baseTypeRef = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
        TypeDefinition? baseTypeDef = null;
        var inheritedMethodNames = new HashSet<string>();
        if (objectExpr.BaseClassName is not null &&
            _asmClassInfos.TryGetValue(objectExpr.BaseClassName, out var baseClassInfo))
        {
            baseTypeRef = baseClassInfo.TypeDef;
            baseTypeDef = baseClassInfo.TypeDef;
            inheritedMethodNames = GetAsmInheritedMethodNames(objectExpr.BaseClassName);
        }

        objType.BaseType = baseTypeRef;

        var containerType = _currentTypeDefinition
                            ?? _module.TopLevelTypes.First(t => t.Name == Sanitize(className));
        containerType.NestedTypes.Add(objType);

        // Add interface implementations
        foreach (var ifaceName in objectExpr.InterfaceNames)
        {
            var ifaceRef = ResolveInterfaceType(ifaceName);
            if (ifaceRef is not null)
                objType.Interfaces.Add(new InterfaceImplementation(ifaceRef));
            else
                diagnostics.Error($"Interface '{ifaceName}' not found for object expression", SourceSpan.None);
        }

        // Add capture fields
        var captureFields = new List<FieldDefinition>();
        foreach (var (name, sigType) in captures)
        {
            var fb = new FieldDefinition(name, FieldAttributes.Public, new FieldSignature(sigType));
            objType.Fields.Add(fb);
            captureFields.Add(fb);
        }

        // Emit constructor
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
        objType.Methods.Add(ctor);
        var ctorBody = new CilMethodBody();
        ctor.MethodBody = ctorBody;
        var ctorIl = ctorBody.Instructions;
        ctorIl.Add(CilOpCodes.Ldarg_0);

        // Emit super args if explicit constructor has them
        if (objectExpr.Constructor?.SuperArgs is { Count: > 0 } superArgs)
        {
            var ctorLocals = new Dictionary<string, CilLocalVariable>();
            foreach (var arg in superArgs)
                EmitNode(arg, ctorIl, outerParams, ctorLocals);
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, superArgs.Count);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);
        }
        else
        {
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, 0);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);
        }

        // Emit constructor body expressions if present
        if (objectExpr.Constructor is { BodyExprs: { Count: > 0 } ctorBodyExprs })
        {
            var ctorLocals2 = new Dictionary<string, CilLocalVariable>();
            foreach (var bodyExpr in ctorBodyExprs)
                EmitNode(bodyExpr, ctorIl, outerParams, ctorLocals2);
        }

        ctorIl.Add(CilOpCodes.Ret);

        // Build field map for method bodies (captures accessible via this.field)
        var fieldMap = new Dictionary<string, FieldDefinition>();
        for (var i = 0; i < captures.Count; i++)
            fieldMap[captures[i].Name] = captureFields[i];

        // Also include inherited fields from base class
        if (baseTypeDef is not null)
            AddAsmInheritedFieldsToMap(baseTypeDef, fieldMap);

        // Emit methods
        foreach (var method in objectExpr.Methods)
        {
            var retType = method.ReturnType == ZType.Unit
                ? _module.CorLibTypeFactory.Void
                : MapToClr(method.ReturnType);
            var methodParamTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();

            // Determine method attributes based on whether this overrides a base method
            var isOverride = inheritedMethodNames.Contains(method.Name);
            var methodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                              | MethodAttributes.Final;
            if (!isOverride)
                methodAttrs |= MethodAttributes.NewSlot;

            var mb = new MethodDefinition(Sanitize(method.Name), methodAttrs,
                MethodSignature.CreateInstance(retType, methodParamTypes));
            for (var pi = 0; pi < method.Params.Count; pi++)
                mb.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(pi + 1), method.Params[pi].Name, 0));
            objType.Methods.Add(mb);

            var methodBody = new CilMethodBody();
            mb.MethodBody = methodBody;
            var methodIl = methodBody.Instructions;
            var methodLocals = new Dictionary<string, CilLocalVariable>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            var savedClassFields = _currentClassFields;
            var savedTypeDef = _currentTypeDefinition;
            var savedBaseTypeDef = _currentBaseTypeDefinition;
            _instanceArgOffset = 1;
            _currentFuncReturnType = method.ReturnType;
            _currentClassFields = fieldMap;
            _currentTypeDefinition = objType;
            _currentBaseTypeDefinition = baseTypeDef;

            EmitNode(method.Body, methodIl, method.Params, methodLocals);

            _currentClassFields = savedClassFields;
            _instanceArgOffset = savedOffset;
            _currentFuncReturnType = savedReturnType;
            _currentTypeDefinition = savedTypeDef;
            _currentBaseTypeDefinition = savedBaseTypeDef;

            if (method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                if (method.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    methodIl.Add(CilOpCodes.Pop);
            methodIl.Add(CilOpCodes.Ret);
        }

        // Emit instantiation: Newobj + store captures
        il.Add(CilOpCodes.Newobj, ctor);
        for (var i = 0; i < captures.Count; i++)
        {
            il.Add(CilOpCodes.Dup);
            EmitLoadVar(captures[i].Name, il, outerParams, locals);
            il.Add(CilOpCodes.Stfld, captureFields[i]);
        }
    }

    private static ZType? GetVarType(string name, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        for (var i = 0; i < outerParams.Count; i++)
            if (outerParams[i].Name == name)
                return outerParams[i].Type;
        return null;
    }

    private static HashSet<string> FindFreeVars(IrNode node, HashSet<string> bound)
    {
        return node switch
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
    }

    private static HashSet<string> Merge(HashSet<string> a, HashSet<string> b)
    {
        var result = new HashSet<string>(a);
        result.UnionWith(b);
        return result;
    }

    private void EmitRecordNew(IrNode.RecordNew node, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        foreach (var (_, value) in node.Fields)
            EmitNode(value, il, outerParams, locals);

        if (_userTypes.TryGetValue(node.TypeName, out var typeRef) && typeRef is TypeDefinition td)
        {
            var ctor = td.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                                                                      && m.Parameters.Count == node.Fields.Count);
            if (ctor is not null)
            {
                il.Add(CilOpCodes.Newobj, ctor);
                return;
            }
        }

        diagnostics.Error($"Type '{node.TypeName}' not found or has no matching constructor for AsmResolver IL emission", SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitFieldGet(IrNode.FieldGet node, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        EmitNode(node.Record, il, outerParams, locals);
        var recordType = node.Record.Type;
        if (recordType is ZType.ZNamedType named && _userTypes.TryGetValue(named.Name, out var typeRef))
        {
            if (typeRef is TypeDefinition td)
            {
                var prop = td.Properties.FirstOrDefault(p => p.Name == Sanitize(node.FieldName));
                var getter = prop?.Semantics
                    .FirstOrDefault(s => s.Attributes == AsmMethodSemanticsAttributes.Getter)?.Method;
                if (getter is not null)
                {
                    // For generic types, create a MemberReference on the closed generic instance
                    if (td.GenericParameters.Count > 0 && named.TypeArgs.Count > 0)
                    {
                        var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                        var closedSig = td.MakeGenericInstanceType(false, typeArgs);
                        var getterRef = new MemberReference(closedSig.ToTypeDefOrRef(),
                            getter.Name!, getter.Signature!);
                        il.Add(CilOpCodes.Callvirt, getterRef);
                    }
                    else
                    {
                        il.Add(CilOpCodes.Callvirt, getter);
                    }

                    return;
                }
            }
            else
            {
                // Precompiled type — resolve via reflection
                var clrType = ResolveClrTypeForTypeRef(typeRef);
                if (clrType is not null)
                {
                    var prop = clrType.GetProperty(Sanitize(node.FieldName));
                    if (prop?.GetGetMethod() is not null)
                    {
                        il.Add(CilOpCodes.Callvirt,
                            (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(prop.GetGetMethod()!));
                        return;
                    }
                }
            }
        }

        diagnostics.Error($"Field '{node.FieldName}' not found for AsmResolver IL emission", SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitUnionCaseNew(IrNode.UnionCaseNew node, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var caseKey = $"{node.UnionName}.{node.CaseName}";
        Log.Debug("EmitUnionCaseNew: caseKey={CaseKey}, nodeType={NodeType}", caseKey, node.Type);
        if (_unionCaseTypes.TryGetValue(caseKey, out var caseTypeRef))
        {
            Log.Debug("EmitUnionCaseNew: found caseTypeRef type={RefType}, fullName={FullName}",
                caseTypeRef.GetType().Name, caseTypeRef.FullName);
            if (node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } nt
                && caseTypeRef is TypeDefinition caseTd && caseTd.GenericParameters.Count > 0)
            {
                var typeArgs = nt.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                var closedSig = caseTd.MakeGenericInstanceType(false, typeArgs);

                var openCtor = caseTd.Methods.First(m => m.IsConstructor && !m.IsStatic
                                                                          && m.Parameters.Count == node.Args.Count);
                // Keep open ctor parameter types as !0, !1 etc. — the TypeSpec provides the actual types
                var openCtorParamTypes = openCtor.Parameters
                    .Select(p => p.ParameterType).ToArray();
                var closedCtor = new MemberReference(closedSig.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, openCtorParamTypes));
                il.Add(CilOpCodes.Newobj, closedCtor);
                return;
            }

            // Non-generic or imported non-TypeDefinition
            if (caseTypeRef is TypeDefinition caseTd2)
            {
                var ctor = caseTd2.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                                                                               && m.Parameters.Count == node.Args.Count);
                if (ctor is not null)
                {
                    il.Add(CilOpCodes.Newobj, ctor);
                    return;
                }
            }
            else
            {
                // Precompiled: construct newobj using the imported type reference directly
                if (node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } ntPre)
                {
                    // Generic case: create closed generic instance (e.g., Some<int>)
                    var typeArgs = ntPre.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                    var closedSig = new GenericInstanceTypeSignature(caseTypeRef, false, typeArgs);

                    // Resolve the open constructor via reflection to get correct generic param indices
                    var clrCaseType = ResolveClrTypeForTypeRef(caseTypeRef);
                    if (clrCaseType is not null)
                    {
                        var openCtor = clrCaseType.GetConstructors()
                            .FirstOrDefault(c => c.GetParameters().Length == node.Args.Count);
                        if (openCtor is not null)
                        {
                            var importedCtor =
                                (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(openCtor);
                            var closedCtor = new MemberReference(
                                closedSig.ToTypeDefOrRef(),
                                ".ctor",
                                importedCtor.Signature!);
                            il.Add(CilOpCodes.Newobj, closedCtor);
                            return;
                        }
                    }
                }

                // Non-generic: resolve constructor via reflection
                var clrType = ResolveClrTypeForTypeRef(caseTypeRef);
                if (clrType is not null)
                {
                    var argTypes = node.Args.Select(a => ResolveClrType(a.Type)).ToArray();
                    var ctor = clrType.GetConstructor(argTypes)
                               ?? clrType.GetConstructors()
                                   .FirstOrDefault(c => c.GetParameters().Length == node.Args.Count);
                    if (ctor is not null)
                    {
                        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctor));
                        return;
                    }
                }
            }
        }

        diagnostics.Error($"Union case '{caseKey}' not found for AsmResolver IL emission", SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitAwait(IrNode.Await awaitNode, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        // Emit the task expression (pushes Task<T> or Task on stack)
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Resolve GetAwaiter() and GetResult() via reflection on the CLR task type
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        var awaiterType = getAwaiterMethod.ReturnType;
        var getResultMethod = awaiterType.GetMethod("GetResult", Type.EmptyTypes)!;

        // Call GetAwaiter() on the Task
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getAwaiterMethod));

        // TaskAwaiter is a struct — store in local and load address for instance method call
        var awaiterLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(awaiterType).ToTypeSignature(awaiterType.IsValueType));
        il.Owner.LocalVariables.Add(awaiterLocal);
        il.Add(CilOpCodes.Stloc, awaiterLocal);
        il.Add(CilOpCodes.Ldloca, awaiterLocal);

        // Call GetResult() — returns T for Task<T>, void for non-generic Task
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getResultMethod));
    }

    private void EmitTryCatch(IrNode.TryCatch node, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        // Extract Ok/Err types from the Result type
        if (node.Type is not ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            diagnostics.Error("TryCatch node type is not a Result type", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var resultSigType = MapToClr(node.Type);

        // Declare a local to hold the result
        var resultLocal = new CilLocalVariable(resultSigType);
        il.Owner.LocalVariables.Add(resultLocal);

        // Resolve Ok and Err case types
        if (!_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseTypeRef) ||
            !_unionCaseTypes.TryGetValue("Result.Err", out var errCaseTypeRef))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Resolve constructors for Ok and Err
        IMethodDefOrRef okCtor, errCtor;
        if (okCaseTypeRef is TypeDefinition okTd && okTd.GenericParameters.Count > 0)
        {
            var okTypeArgs = new TypeSignature[] { MapToClr(okT), MapToClr(errT) };
            var closedOkSig = okTd.MakeGenericInstanceType(false, okTypeArgs);

            var errTd = (TypeDefinition)errCaseTypeRef;
            var errTypeArgs = new TypeSignature[] { MapToClr(okT), MapToClr(errT) };
            var closedErrSig = errTd.MakeGenericInstanceType(false, errTypeArgs);

            var openOkCtor = okTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
            var okCtorParamType = ResolveGenericParam(openOkCtor.Parameters[0].ParameterType, okTypeArgs);
            okCtor = new MemberReference(closedOkSig.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [okCtorParamType]));

            var openErrCtor = errTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
            var errCtorParamType = ResolveGenericParam(openErrCtor.Parameters[0].ParameterType, errTypeArgs);
            errCtor = new MemberReference(closedErrSig.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [errCtorParamType]));
        }
        else
        {
            // Precompiled: resolve via reflection
            var okClrType = ResolveClrTypeForTypeRef(okCaseTypeRef);
            var errClrType = ResolveClrTypeForTypeRef(errCaseTypeRef);
            if (okClrType is null || errClrType is null)
            {
                diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
                il.Add(CilOpCodes.Ldc_I4_0);
                return;
            }

            if (okClrType.IsGenericTypeDefinition)
            {
                var okClr = IlTypeMapper.MapToClr(okT);
                var errClr = IlTypeMapper.MapToClr(errT);
                var closedOkClr = okClrType.MakeGenericType(okClr, errClr);
                var closedErrClr = errClrType.MakeGenericType(okClr, errClr);
                var okCtorInfo = closedOkClr.GetConstructors().First(c => c.GetParameters().Length == 1);
                var errCtorInfo = closedErrClr.GetConstructors().First(c => c.GetParameters().Length == 1);
                okCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(okCtorInfo);
                errCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(errCtorInfo);
            }
            else
            {
                var okCtorInfo = okClrType.GetConstructors().First(c => c.GetParameters().Length == 1);
                var errCtorInfo = errClrType.GetConstructors().First(c => c.GetParameters().Length == 1);
                okCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(okCtorInfo);
                errCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(errCtorInfo);
            }
        }

        // Create labels for exception handler boundaries
        var tryStartLabel = new CilInstructionLabel();
        var handlerStartLabel = new CilInstructionLabel();
        var handlerEndLabel = new CilInstructionLabel();

        // Try block
        tryStartLabel.Instruction = il.Add(CilOpCodes.Nop);
        EmitNode(node.Body, il, outerParams, locals);
        il.Add(CilOpCodes.Newobj, okCtor);
        il.Add(CilOpCodes.Stloc, resultLocal);
        il.Add(CilOpCodes.Leave, handlerEndLabel);

        // Catch (Exception) block
        handlerStartLabel.Instruction = il.Add(CilOpCodes.Nop);
        // Stack has the Exception; get its Message
        var getMessage = typeof(Exception).GetProperty("Message")!.GetGetMethod()!;
        il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getMessage));

        // Create ErrorInfo(message, None<ErrorInfo>())
        if (_userTypes.TryGetValue("ErrorInfo", out var errorInfoTypeRef) &&
            _unionCaseTypes.TryGetValue("Option.None", out var noneCaseTypeRef))
        {
            EmitNoneErrorInfo(errorInfoTypeRef, noneCaseTypeRef, il);

            // new ErrorInfo(message, noneInstance)
            EmitNewErrorInfo(errorInfoTypeRef, il);
        }
        else
        {
            // Fallback
            il.Add(CilOpCodes.Pop);
            il.Add(CilOpCodes.Ldnull);
        }

        // new Err(errorInfo)
        il.Add(CilOpCodes.Newobj, errCtor);
        il.Add(CilOpCodes.Stloc, resultLocal);
        il.Add(CilOpCodes.Leave, handlerEndLabel);

        // After handler
        handlerEndLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Register the exception handler
        il.Owner.ExceptionHandlers.Add(new CilExceptionHandler
        {
            HandlerType = CilExceptionHandlerType.Exception,
            TryStart = tryStartLabel,
            TryEnd = handlerStartLabel,
            HandlerStart = handlerStartLabel,
            HandlerEnd = handlerEndLabel,
            ExceptionType = _module.DefaultImporter.ImportType(typeof(Exception)).ToTypeDefOrRef()
        });

        // Load the result
        il.Add(CilOpCodes.Ldloc, resultLocal);
    }

    private void EmitWithHandlers(IrNode.WithHandlers node, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        var resultSigType = MapToClr(node.Type);
        var resultLocal = new CilLocalVariable(resultSigType);
        il.Owner.LocalVariables.Add(resultLocal);

        var endLabel = new CilInstructionLabel();

        // Try block
        var tryStartLabel = new CilInstructionLabel();
        tryStartLabel.Instruction = il.Add(CilOpCodes.Nop);
        EmitNode(node.Body, il, outerParams, locals);
        il.Add(CilOpCodes.Stloc, resultLocal);
        il.Add(CilOpCodes.Leave, endLabel);

        // Emit each catch handler
        var handlerBoundaries = new List<(CilInstructionLabel Start, CilInstructionLabel End, Type ClrType)>();
        foreach (var handler in node.Handlers)
        {
            var exClrType = _clrInterop.FindType(handler.ExceptionTypeName);
            if (exClrType is null)
            {
                diagnostics.Error($"Cannot resolve exception type '{handler.ExceptionTypeName}' for IL emission",
                    SourceSpan.None);
                continue;
            }

            var handlerStart = new CilInstructionLabel();
            var handlerEnd = new CilInstructionLabel();

            // Handler start: exception object is on the stack
            handlerStart.Instruction = il.Add(CilOpCodes.Nop);

            if (handler.BindingVarName != "_")
            {
                // Store exception in a local variable
                var exLocal = new CilLocalVariable(
                    _module.DefaultImporter.ImportType(exClrType).ToTypeSignature(false));
                il.Owner.LocalVariables.Add(exLocal);

                il.Add(CilOpCodes.Stloc, exLocal);

                // Add binding to locals dict
                var hadPrevious = locals.TryGetValue(handler.BindingVarName, out var previousLocal);
                locals[handler.BindingVarName] = exLocal;

                EmitNode(handler.HandlerBody, il, outerParams, locals);

                // Restore locals
                if (hadPrevious)
                    locals[handler.BindingVarName] = previousLocal!;
                else
                    locals.Remove(handler.BindingVarName);
            }
            else
            {
                // Discard the exception from the stack
                il.Add(CilOpCodes.Pop);
                EmitNode(handler.HandlerBody, il, outerParams, locals);
            }

            il.Add(CilOpCodes.Stloc, resultLocal);
            il.Add(CilOpCodes.Leave, endLabel);

            handlerEnd.Instruction = il.Add(CilOpCodes.Nop);
            handlerBoundaries.Add((handlerStart, handlerEnd, exClrType));
        }

        // End label
        endLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Register exception handlers (all share the same try region)
        foreach (var (start, end, clrType) in handlerBoundaries)
        {
            il.Owner.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Exception,
                TryStart = tryStartLabel,
                TryEnd = handlerBoundaries[0].Start,
                HandlerStart = start,
                HandlerEnd = end,
                ExceptionType = _module.DefaultImporter.ImportType(clrType).ToTypeDefOrRef()
            });
        }

        // Load the result
        il.Add(CilOpCodes.Ldloc, resultLocal);
    }

    private void EmitNoneErrorInfo(ITypeDefOrRef errorInfoTypeRef, ITypeDefOrRef noneCaseTypeRef,
        CilInstructionCollection il)
    {
        if (noneCaseTypeRef is TypeDefinition noneTd && noneTd.GenericParameters.Count > 0)
        {
            var closedNoneSig = noneTd.MakeGenericInstanceType(false, [errorInfoTypeRef.ToTypeSignature(false)]);
            var openNoneCtor = noneTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            var noneCtor = new MemberReference(closedNoneSig.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
            il.Add(CilOpCodes.Newobj, noneCtor);
        }
        else
        {
            // Precompiled
            var noneClrType = ResolveClrTypeForTypeRef(noneCaseTypeRef);
            if (noneClrType is not null && noneClrType.IsGenericTypeDefinition)
            {
                var errorInfoClrType = ResolveClrTypeForTypeRef(errorInfoTypeRef);
                if (errorInfoClrType is not null)
                {
                    var closedNone = noneClrType.MakeGenericType(errorInfoClrType);
                    var noneCtor = closedNone.GetConstructors()
                        .FirstOrDefault(c => c.GetParameters().Length == 0);
                    if (noneCtor is not null)
                        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(noneCtor));
                    else
                        il.Add(CilOpCodes.Ldnull);
                }
                else
                {
                    il.Add(CilOpCodes.Ldnull);
                }
            }
            else if (noneClrType is not null)
            {
                var noneCtor = noneClrType.GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Length == 0);
                if (noneCtor is not null)
                    il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(noneCtor));
                else
                    il.Add(CilOpCodes.Ldnull);
            }
            else
            {
                il.Add(CilOpCodes.Ldnull);
            }
        }
    }

    private void EmitNewErrorInfo(ITypeDefOrRef errorInfoTypeRef, CilInstructionCollection il)
    {
        if (errorInfoTypeRef is TypeDefinition errorInfoTd)
        {
            var errorInfoCtor = errorInfoTd.Methods.First(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
            il.Add(CilOpCodes.Newobj, errorInfoCtor);
        }
        else
        {
            var errorInfoClrType = ResolveClrTypeForTypeRef(errorInfoTypeRef);
            if (errorInfoClrType is not null)
            {
                var errorInfoCtor = errorInfoClrType.GetConstructors()
                    .First(c => c.GetParameters().Length == 2);
                il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(errorInfoCtor));
            }
            else
            {
                il.Add(CilOpCodes.Ldnull);
            }
        }
    }

    private void EmitPropagate(IrNode.Propagate node, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        // Emit inner expression (should evaluate to a Result value)
        EmitNode(node.Expr, il, outerParams, locals);

        var resultSigType = MapToClr(node.ResultType);
        var tempLocal = new CilLocalVariable(resultSigType);
        il.Owner.LocalVariables.Add(tempLocal);
        il.Add(CilOpCodes.Stloc, tempLocal);

        // Extract Ok/Err types from the inner Result type
        if (node.ResultType is not ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            diagnostics.Error("Propagate expression is not a Result type", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (!_unionCaseTypes.TryGetValue("Result.Err", out var errCaseTypeRef) ||
            !_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseTypeRef))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for Propagate", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        ITypeDefOrRef closedErrType, closedOkType;
        IMethodDefOrRef errPropGetter, okValueGetter;

        var hasGenericParams = errCaseTypeRef is TypeDefinition errTdCheck && errTdCheck.GenericParameters.Count > 0;
        if (!hasGenericParams)
        {
            var errClr = ResolveClrTypeForTypeRef(errCaseTypeRef);
            hasGenericParams = errClr is not null && errClr.IsGenericTypeDefinition;
        }

        if (hasGenericParams)
        {
            var typeArgs = new TypeSignature[] { MapToClr(okT), MapToClr(errT) };

            if (errCaseTypeRef is TypeDefinition errTd2)
            {
                closedErrType = errTd2.MakeGenericInstanceType(false, typeArgs).ToTypeDefOrRef();
                var okTd = (TypeDefinition)okCaseTypeRef;
                closedOkType = okTd.MakeGenericInstanceType(false, typeArgs).ToTypeDefOrRef();

                // Find getter methods
                var openErrGetter = (IMethodDefOrRef)_unionCaseGetters["Result.Err.Error"];
                TypeSignature errGetterRetType;
                if (openErrGetter is MethodDefinition egd)
                    errGetterRetType = ResolveGenericParam(egd.Signature!.ReturnType, typeArgs);
                else
                    errGetterRetType = MapToClr(errT);
                errPropGetter = new MemberReference(closedErrType, openErrGetter.Name!.Value,
                    MethodSignature.CreateInstance(errGetterRetType));

                var openOkGetter = (IMethodDefOrRef)_unionCaseGetters["Result.Ok.Value"];
                TypeSignature okGetterRetType;
                if (openOkGetter is MethodDefinition ogd)
                    okGetterRetType = ResolveGenericParam(ogd.Signature!.ReturnType, typeArgs);
                else
                    okGetterRetType = MapToClr(okT);
                okValueGetter = new MemberReference(closedOkType, openOkGetter.Name!.Value,
                    MethodSignature.CreateInstance(okGetterRetType));
            }
            else
            {
                // Precompiled
                var errClr = IlTypeMapper.MapToClr(errT);
                var okClr = IlTypeMapper.MapToClr(okT);
                var errClrType = ResolveClrTypeForTypeRef(errCaseTypeRef)!;
                var okClrType = ResolveClrTypeForTypeRef(okCaseTypeRef)!;
                var closedErrClr = errClrType.MakeGenericType(okClr, errClr);
                var closedOkClr = okClrType.MakeGenericType(okClr, errClr);
                closedErrType = _module.DefaultImporter.ImportType(closedErrClr);
                closedOkType = _module.DefaultImporter.ImportType(closedOkClr);

                var errGetterInfo = closedErrClr.GetProperty("Error")!.GetGetMethod()!;
                errPropGetter = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(errGetterInfo);

                var okGetterInfo = closedOkClr.GetProperty("Value")!.GetGetMethod()!;
                okValueGetter = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(okGetterInfo);
            }
        }
        else
        {
            closedErrType = errCaseTypeRef;
            closedOkType = okCaseTypeRef;

            if (_unionCaseGetters.TryGetValue("Result.Err.Error", out var egRef) &&
                _unionCaseGetters.TryGetValue("Result.Ok.Value", out var ogRef))
            {
                errPropGetter = (IMethodDefOrRef)egRef;
                okValueGetter = (IMethodDefOrRef)ogRef;
            }
            else
            {
                diagnostics.Error("Cannot resolve Ok/Err property getters for Propagate", SourceSpan.None);
                il.Add(CilOpCodes.Ldc_I4_0);
                return;
            }
        }

        // Test: is it Err?
        var okLabel = new CilInstructionLabel();
        il.Add(CilOpCodes.Ldloc, tempLocal);
        il.Add(CilOpCodes.Isinst, closedErrType);
        il.Add(CilOpCodes.Brfalse, okLabel);

        // It's Err — extract the error and wrap in the function's return Err type, then early return
        il.Add(CilOpCodes.Ldloc, tempLocal);
        il.Add(CilOpCodes.Castclass, closedErrType);

        // Get .error property
        il.Add(CilOpCodes.Callvirt, errPropGetter);

        // Wrap in the function's return Err type
        if (_currentFuncReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
        {
            if (errCaseTypeRef is TypeDefinition errTdRet && errTdRet.GenericParameters.Count > 0)
            {
                var funcErrTypeArgs = new TypeSignature[] { MapToClr(fOkT), MapToClr(fErrT) };
                var funcErrSig = errTdRet.MakeGenericInstanceType(false, funcErrTypeArgs);
                var openCtor = errTdRet.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                var ctorParamType = ResolveGenericParam(openCtor.Parameters[0].ParameterType, funcErrTypeArgs);
                var funcErrCtor = new MemberReference(funcErrSig.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [ctorParamType]));
                il.Add(CilOpCodes.Newobj, funcErrCtor);
            }
            else
            {
                var errClrType = ResolveClrTypeForTypeRef(errCaseTypeRef);
                if (errClrType is not null)
                {
                    if (errClrType.IsGenericTypeDefinition)
                    {
                        var fOkClr = IlTypeMapper.MapToClr(fOkT);
                        var fErrClr = IlTypeMapper.MapToClr(fErrT);
                        var closedErrClr = errClrType.MakeGenericType(fOkClr, fErrClr);
                        var ctorInfo = closedErrClr.GetConstructors().First(c => c.GetParameters().Length == 1);
                        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctorInfo));
                    }
                    else
                    {
                        var ctorInfo = errClrType.GetConstructors().First(c => c.GetParameters().Length == 1);
                        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctorInfo));
                    }
                }
            }
        }

        il.Add(CilOpCodes.Ret); // Early return

        // Ok path — extract Value
        okLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Ldloc, tempLocal);
        il.Add(CilOpCodes.Castclass, closedOkType);
        il.Add(CilOpCodes.Callvirt, okValueGetter);
        // Unwrapped value is now on the stack
    }

    private void EmitLoadVar(string name, CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        if (locals.TryGetValue(name, out var local))
        {
            il.Add(CilOpCodes.Ldloc, local);
            return;
        }

        for (var i = 0; i < outerParams.Count; i++)
            if (outerParams[i].Name == name)
            {
                var method = (MethodDefinition)il.Owner!.Owner!;
                // In AsmResolver, Parameters collection excludes 'this', so for instance methods
                // (where _instanceArgOffset=1), we still index by i into Parameters.
                il.Add(CilOpCodes.Ldarg, method.Parameters[i]);
                return;
            }

        if (_currentClassFields is not null && _currentClassFields.TryGetValue(name, out var classField))
        {
            if (_moveNextCtx?.ThisField is { } thisF)
            {
                // Inside async state machine: load this.__this then access field
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldfld, thisF);
            }
            else
            {
                il.Add(CilOpCodes.Ldarg_0);
            }
            il.Add(CilOpCodes.Ldfld, classField);
            return;
        }

        if (_staticFields.TryGetValue(name, out var field))
        {
            il.Add(CilOpCodes.Ldsfld, field);
            return;
        }

        diagnostics.Error($"Variable '{name}' not found for AsmResolver IL emission", SourceSpan.None);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitBinaryOp(string op, ZType? leftType, CilInstructionCollection il)
    {
        switch (op)
        {
            case "+" when leftType is ZType.ZPrimitiveType { Kind: PrimitiveKind.String }:
                var concatMethod = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(concatMethod));
                break;
            case "+": il.Add(CilOpCodes.Add); break;
            case "-": il.Add(CilOpCodes.Sub); break;
            case "*": il.Add(CilOpCodes.Mul); break;
            case "/": il.Add(CilOpCodes.Div); break;
            case "%": il.Add(CilOpCodes.Rem); break;
            case "=": il.Add(CilOpCodes.Ceq); break;
            case "<": il.Add(CilOpCodes.Clt); break;
            case ">": il.Add(CilOpCodes.Cgt); break;
            case "!=":
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case "<=":
                il.Add(CilOpCodes.Cgt);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case ">=":
                il.Add(CilOpCodes.Clt);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case "and": il.Add(CilOpCodes.And); break;
            case "or": il.Add(CilOpCodes.Or); break;
        }
    }

    private static void EmitUnaryOp(string op, CilInstructionCollection il)
    {
        switch (op)
        {
            case "not":
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
        }
    }

    /// <summary>
    ///     Imports a delegate constructor with the correct AsmResolver generic type.
    /// </summary>
    private IMethodDefOrRef ImportDelegateConstructor(ZType funcType)
    {
        var clrDelegateType = IlTypeMapper.MapToClr(funcType);
        var ctorInfo = clrDelegateType.GetConstructors()[0];
        var asmDelegateType = MapToClr(funcType);
        if (asmDelegateType is GenericInstanceTypeSignature git)
        {
            return new MemberReference(git.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void,
                    [_module.CorLibTypeFactory.Object,
                    _module.CorLibTypeFactory.IntPtr]));
        }

        return (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(ctorInfo);
    }

    /// <summary>
    ///     Emits a Callvirt to delegate.Invoke() using the AsmResolver-aware type for the delegate.
    /// </summary>
    private void EmitDelegateInvoke(ZType funcType, CilInstructionCollection il)
    {
        var clrDelegateType = IlTypeMapper.MapToClr(funcType);
        var invokeMethod = clrDelegateType.GetMethod("Invoke")!;
        il.Add(CilOpCodes.Callvirt, ImportMethodWithGenericDeclaringType(invokeMethod, funcType));
    }

    /// <summary>
    ///     Imports a reflection MethodInfo, fixing up the declaring type to use the correct
    ///     AsmResolver generic instance when the receiver type has generic parameters.
    /// </summary>
    private IMethodDefOrRef ImportMethodWithGenericDeclaringType(MethodInfo method, ZType receiverType)
    {
        var asmReceiverType = MapToClr(receiverType);
        if (asmReceiverType is GenericInstanceTypeSignature git)
        {
            // AsmResolver's DefaultImporter doesn't normalize method signatures on generic types
            // to use generic parameter references (!0, !1). Unlike Cecil, it preserves concrete types.
            // We must construct the signature manually, replacing declaring type's generic parameters
            // with GenericParameterSignature references.
            var declaringType = method.DeclaringType;
            Type? openDeclaringType = declaringType is { IsGenericType: true }
                ? (declaringType.IsGenericTypeDefinition ? declaringType : declaringType.GetGenericTypeDefinition())
                : null;

            // Get the method on the open type to see the un-instantiated parameter types
            MethodInfo openMethod = method;
            if (declaringType is { IsGenericType: true, IsGenericTypeDefinition: false })
                openMethod = (MethodInfo)MethodBase.GetMethodFromHandle(
                    method.MethodHandle, openDeclaringType!.TypeHandle)!;

            // Map CLR types to AsmResolver TypeSignatures, replacing generic type params with !N
            TypeSignature MapTypeWithGenericParams(Type t)
            {
                if (t.IsGenericParameter && t.DeclaringType == openDeclaringType)
                    return new GenericParameterSignature(_module, GenericParameterType.Type,
                        t.GenericParameterPosition);
                if (t.IsGenericType)
                {
                    var args = t.GetGenericArguments();
                    if (args.Any(a => a.IsGenericParameter || a.ContainsGenericParameters))
                    {
                        var mappedArgs = args.Select(MapTypeWithGenericParams).ToArray();
                        var openClrType = t.IsGenericTypeDefinition
                            ? t
                            : t.GetGenericTypeDefinition();
                        var imported = _module.DefaultImporter.ImportType(openClrType);
                        return imported.ToTypeSignature(openClrType.IsValueType)
                            .MakeGenericInstanceType(openClrType.IsValueType, mappedArgs);
                    }
                }

                if (t.IsArray)
                    return MapTypeWithGenericParams(t.GetElementType()!).MakeSzArrayType();
                if (t.IsByRef)
                    return MapTypeWithGenericParams(t.GetElementType()!).MakeByReferenceType();
                return _module.DefaultImporter.ImportType(t).ToTypeSignature(t.IsValueType);
            }

            var retType = MapTypeWithGenericParams(openMethod.ReturnType);
            var paramTypes = openMethod.GetParameters()
                .Select(p => MapTypeWithGenericParams(p.ParameterType)).ToArray();

            return new MemberReference(git.ToTypeDefOrRef(), method.Name,
                method.IsStatic
                    ? MethodSignature.CreateStatic(retType, paramTypes)
                    : MethodSignature.CreateInstance(retType, paramTypes));
        }

        return (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(method);
    }

    /// <summary>
    ///     Resolves a ZType to a CLR System.Type, checking user-defined types first.
    /// </summary>
    private Type ResolveClrType(ZType type)
    {
        // Unwrap nullable types — resolve the inner type for property/method lookup
        if (type is ZType.ZNullableType nullable)
            return ResolveClrType(nullable.Inner);

        if (type is ZType.ZNamedType named)
        {
            if (_userTypes.TryGetValue(named.Name, out var typeRef))
            {
                var resolved = ResolveClrTypeForTypeRef(typeRef);
                if (resolved is not null)
                    return resolved;
            }

            // Try resolving as a CLR type for fully-qualified names
            if (named.Name.Contains('.'))
            {
                var clrType = _clrInterop.FindType(named.Name);
                if (clrType is not null)
                    return clrType;
            }
        }

        return IlTypeMapper.MapToClr(type);
    }

    /// <summary>
    ///     Resolves an AsmResolver ITypeDefOrRef to a CLR System.Type via reflection.
    /// </summary>
    private static Type? ResolveClrTypeForTypeRef(ITypeDefOrRef typeRef)
    {
        var fullName = typeRef.FullName.Replace('/', '+');
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullName);
            if (type is not null)
                return type;
        }

        // Retry without backtick arity suffixes — ZScheme union types are defined without
        // the backtick convention but ImportTypeWithGenericArity adds it for correct IL metadata
        var stripped = StripBacktickArity(fullName);
        if (stripped != fullName)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(stripped);
                if (type is not null)
                    return type;
            }

        return null;
    }

    /// <summary>
    ///     Strips the backtick arity suffix from a type name (e.g., <c>Some`1</c> → <c>Some</c>).
    ///     Used to convert .NET metadata names to ZScheme logical names for dictionary lookups.
    /// </summary>
    private static string StripBacktickArity(string typeName)
    {
        var idx = typeName.IndexOf('`');
        return idx >= 0 ? typeName[..idx] : typeName;
    }

    /// <summary>
    ///     Imports a type from reflection, ensuring the TypeReference name includes the backtick
    ///     arity suffix (e.g., <c>Some`1</c>) required by CLR metadata for generic types.
    ///     The standard DefaultImporter may produce names without the backtick when the original
    ///     TypeDefinition was created without it.
    /// </summary>
    private ITypeDefOrRef ImportTypeWithGenericArity(Type clrType)
    {
        return _module.DefaultImporter.ImportType(clrType);
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, MethodDefinition target)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
        {
            var attrType = _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null) continue;
            var customAttr = BuildCustomAttribute(attrType, attr);
            if (customAttr is not null)
                target.CustomAttributes.Add(customAttr);
        }
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, TypeDefinition target)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
        {
            var attrType = _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null) continue;
            var customAttr = BuildCustomAttribute(attrType, attr);
            if (customAttr is not null)
                target.CustomAttributes.Add(customAttr);
        }
    }

    private CustomAttribute? BuildCustomAttribute(Type attrType, IrAttribute attr)
    {
        if (attr.PositionalArgs.Count == 0 && attr.NamedArgs.Count == 0)
        {
            var ctorInfo = attrType.GetConstructor(Type.EmptyTypes);
            if (ctorInfo is null) return null;
            var ctorRef = (ICustomAttributeType)_module.DefaultImporter.ImportMethod(ctorInfo);
            return new CustomAttribute(ctorRef);
        }

        var ctor = FindAttributeConstructor(attrType, attr.PositionalArgs);
        if (ctor is null) return null;

        var ctorReference = (ICustomAttributeType)_module.DefaultImporter.ImportMethod(ctor);
        var customAttr = new CustomAttribute(ctorReference);
        var ctorParams = ctor.GetParameters();

        var signature = new CustomAttributeSignature();

        if (ctorParams.Length == 1 && ctorParams[0].ParameterType == typeof(object[]))
        {
            // params object[] — pack all positional args as boxed elements in an array argument
            var objectTypeSig = _module.DefaultImporter.ImportType(typeof(object)).ToTypeSignature(false);
            var arrayTypeSig = objectTypeSig.MakeSzArrayType();

            var elements = new object[attr.PositionalArgs.Count];
            for (var i = 0; i < attr.PositionalArgs.Count; i++)
            {
                var (clrType, value) = ResolveAttributeArgValue(attr.PositionalArgs[i]);
                var elemTypeSig = _module.DefaultImporter.ImportType(clrType).ToTypeSignature(false);
                elements[i] = new BoxedArgument(elemTypeSig, value);
            }

            signature.FixedArguments.Add(new CustomAttributeArgument(arrayTypeSig, elements));
        }
        else
        {
            // Positional args match constructor parameters 1:1
            for (var i = 0; i < attr.PositionalArgs.Count && i < ctorParams.Length; i++)
            {
                var (_, value) = ResolveAttributeArgValue(attr.PositionalArgs[i]);
                var typeSig = _module.DefaultImporter.ImportType(ctorParams[i].ParameterType).ToTypeSignature(false);
                signature.FixedArguments.Add(new CustomAttributeArgument(typeSig, value));
            }
        }

        customAttr.Signature = signature;
        return customAttr;
    }

    private static ConstructorInfo? FindAttributeConstructor(Type attrType, IReadOnlyList<object> positionalArgs)
    {
        var constructors = attrType.GetConstructors();

        // Try exact parameter count match
        foreach (var ctor in constructors)
        {
            var ps = ctor.GetParameters();
            if (ps.Length == positionalArgs.Count)
                return ctor;
        }

        // Try params object[] constructor
        foreach (var ctor in constructors)
        {
            var ps = ctor.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(object[]))
                return ctor;
        }

        return constructors.Length > 0 ? constructors[0] : null;
    }

    private static (Type ClrType, object Value) ResolveAttributeArgValue(object arg)
    {
        return arg switch
        {
            int i => (typeof(int), i),
            long l => (typeof(long), l),
            float f => (typeof(float), f),
            double d => (typeof(double), d),
            string s => (typeof(string), s),
            bool b => (typeof(bool), b),
            _ => (typeof(string), arg.ToString() ?? "")
        };
    }

    private void EmitClassDecl(IrNode.ClassDecl classDecl)
    {
        Log.Debug("IlEmitter: emitting class declaration {ClassName}", classDecl.Name);

        // Resolve base type
        ITypeDefOrRef baseTypeRef = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
        TypeDefinition? baseTypeDef = null;
        var inheritedFields = new List<IrField>();
        var inheritedMethodNames = new HashSet<string>();

        // The parser puts the first name after ':' in BaseClassName (position-based).
        // If it's not a known ZScheme class, it may actually be a CLR interface.
        string? baseClassAsInterface = null;

        if (classDecl.BaseClassName is not null &&
            _asmClassInfos.TryGetValue(classDecl.BaseClassName, out var baseInfo))
        {
            // Known ZScheme class — use as base type
            baseTypeRef = baseInfo.TypeDef;
            baseTypeDef = baseInfo.TypeDef;
            inheritedFields.AddRange(GetAsmInheritedFields(classDecl.BaseClassName));
            inheritedMethodNames = GetAsmInheritedMethodNames(classDecl.BaseClassName);
        }
        else if (classDecl.BaseClassName is not null)
        {
            // Not a ZScheme class — could be a ZScheme interface or a CLR type.
            // Check _userTypes first (ZScheme interfaces are registered there but not in _asmClassInfos)
            if (_userTypes.ContainsKey(classDecl.BaseClassName))
            {
                // ZScheme-defined interface
                baseClassAsInterface = classDecl.BaseClassName;
            }
            else
            {
                // Try resolving as CLR type
                var clrType = _clrInterop.FindType(classDecl.BaseClassName);
                if (clrType is null)
                    foreach (var ns in ClrUsings)
                    {
                        clrType = _clrInterop.FindType(ns + "." + classDecl.BaseClassName);
                        if (clrType is not null) break;
                    }

                if (clrType is not null)
                {
                    if (clrType.IsInterface)
                        baseClassAsInterface = classDecl.BaseClassName;
                    else
                        baseTypeRef = (ITypeDefOrRef)_module.DefaultImporter.ImportType(clrType);
                }
            }
        }

        var typeAttrs = TypeAttributes.Public | TypeAttributes.Class;
        if (!classDecl.IsOpen)
            typeAttrs |= TypeAttributes.Sealed;

        var classType = new TypeDefinition(_ilNamespace, Sanitize(classDecl.Name), typeAttrs);
        classType.BaseType = baseTypeRef;
        _module.TopLevelTypes.Add(classType);
        RegisterUserType(classDecl.Name, classType);

        EmitCustomAttributes(classDecl.Attributes, classType);

        // Add interface implementations and collect interface method names
        var interfaceMethodNames = new HashSet<string>();

        // Handle BaseClassName that was actually a CLR interface
        if (baseClassAsInterface is not null)
        {
            var ifaceRef = ResolveInterfaceType(baseClassAsInterface);
            if (ifaceRef is not null)
                classType.Interfaces.Add(new InterfaceImplementation(ifaceRef));
            CollectInterfaceMethodNames(baseClassAsInterface, interfaceMethodNames);
        }

        foreach (var ifaceName in classDecl.InterfaceNames)
        {
            var ifaceRef = ResolveInterfaceType(ifaceName);
            if (ifaceRef is not null)
                classType.Interfaces.Add(new InterfaceImplementation(ifaceRef));

            // Collect method names from CLR interfaces for Virtual flag marking
            CollectInterfaceMethodNames(ifaceName, interfaceMethodNames);
        }

        // Define own fields as properties with backing fields
        var fieldDefs = new List<(FieldDefinition Field, PropertyDefinition Prop)>();
        foreach (var field in classDecl.Fields)
        {
            var fieldType = MapToClr(field.Type);
            var fieldAttrs = FieldAttributes.Private;
            if (!field.IsMutable)
                fieldAttrs |= FieldAttributes.InitOnly;
            var fb = new FieldDefinition($"<{Sanitize(field.Name)}>k__BackingField",
                fieldAttrs,
                new FieldSignature(fieldType));
            classType.Fields.Add(fb);

            var getterName = $"get_{Sanitize(field.Name)}";
            var isGetterIfaceImpl = interfaceMethodNames.Contains(getterName);
            var getterAttrs = MethodAttributes.Public | MethodAttributes.Virtual
                              | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            if (isGetterIfaceImpl)
                getterAttrs |= MethodAttributes.NewSlot | MethodAttributes.Final;

            var getter = new MethodDefinition(getterName, getterAttrs,
                MethodSignature.CreateInstance(fieldType));
            classType.Methods.Add(getter);
            var getBody = new CilMethodBody();
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, fb);
            getIl.Add(CilOpCodes.Ret);

            var pb = new PropertyDefinition(Sanitize(field.Name), 0, PropertySignature.CreateInstance(fieldType));
            pb.Semantics.Add(new MethodSemantics(getter, AsmMethodSemanticsAttributes.Getter));

            if (field.IsMutable)
            {
                var setterName = $"set_{Sanitize(field.Name)}";
                var isSetterIfaceImpl = interfaceMethodNames.Contains(setterName);
                var setterAttrs = MethodAttributes.Public | MethodAttributes.Virtual
                                  | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
                if (isSetterIfaceImpl)
                    setterAttrs |= MethodAttributes.NewSlot | MethodAttributes.Final;

                var setter = new MethodDefinition(setterName, setterAttrs,
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [fieldType]));
                setter.ParameterDefinitions.Add(new ParameterDefinition(1, "value", 0));
                classType.Methods.Add(setter);
                var setBody = new CilMethodBody();
                setter.MethodBody = setBody;
                var setIl = setBody.Instructions;
                setIl.Add(CilOpCodes.Ldarg_0);
                setIl.Add(CilOpCodes.Ldarg_1);
                setIl.Add(CilOpCodes.Stfld, fb);
                setIl.Add(CilOpCodes.Ret);
                pb.Semantics.Add(new MethodSemantics(setter, AsmMethodSemanticsAttributes.Setter));
            }
            else if (field.IsInit)
            {
                var initSetter = CreateInitSetter(Sanitize(field.Name), fieldType, fb);
                classType.Methods.Add(initSetter);
                pb.Semantics.Add(new MethodSemantics(initSetter, AsmMethodSemanticsAttributes.Setter));
            }

            classType.Properties.Add(pb);
            fieldDefs.Add((fb, pb));
        }

        // Constructor
        if (classDecl.Constructor is { } irCtor)
        {
            // Explicit constructor
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, irCtor.SuperArgs?.Count ?? 0);
            var ctorParamTypes = irCtor.Params.Select(p => MapToClr(p.Type)).ToArray();
            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, ctorParamTypes));
            for (var i = 0; i < irCtor.Params.Count; i++)
                ctor.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(i + 1), Sanitize(irCtor.Params[i].Name), 0));
            classType.Methods.Add(ctor);

            var ctorBody = new CilMethodBody();
            ctor.MethodBody = ctorBody;
            var ctorIl = ctorBody.Instructions;

            // Set up instance context for EmitNode calls within constructor
            var savedCtorOffset = _instanceArgOffset;
            _instanceArgOffset = 1;

            // Call base constructor
            ctorIl.Add(CilOpCodes.Ldarg_0);
            if (irCtor.SuperArgs is not null)
            {
                var ctorLocals = new Dictionary<string, CilLocalVariable>();
                foreach (var arg in irCtor.SuperArgs)
                    EmitNode(arg, ctorIl, irCtor.Params, ctorLocals);
            }
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);

            // Body expressions
            var bodyLocals = new Dictionary<string, CilLocalVariable>();
            foreach (var expr in irCtor.BodyExprs)
            {
                EmitNode(expr, ctorIl, irCtor.Params, bodyLocals);
                if (expr.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    ctorIl.Add(CilOpCodes.Pop);
            }

            // Field assignments from set!
            foreach (var (fieldName, value) in irCtor.FieldSets)
            {
                var fieldIdx = classDecl.Fields.ToList().FindIndex(f => f.Name == fieldName);
                if (fieldIdx >= 0)
                {
                    ctorIl.Add(CilOpCodes.Ldarg_0);
                    EmitNode(value, ctorIl, irCtor.Params, bodyLocals);
                    EmitNullableWrapIfNeeded(value, fieldDefs[fieldIdx].Field.Signature!.FieldType, ctorIl);
                    ctorIl.Add(CilOpCodes.Stfld, fieldDefs[fieldIdx].Field);
                }
            }

            _instanceArgOffset = savedCtorOffset;
            ctorIl.Add(CilOpCodes.Ret);
        }
        else
        {
            // Auto-generated constructor: inherited fields + own fields
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, inheritedFields.Count);
            var allParamTypes = inheritedFields.Select(f => MapToClr(f.Type))
                .Concat(classDecl.Fields.Select(f => MapToClr(f.Type))).ToArray();
            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, allParamTypes));
            var paramIdx = 1;
            foreach (var f in inheritedFields)
                ctor.ParameterDefinitions.Add(new ParameterDefinition((ushort)paramIdx++, Sanitize(f.Name), 0));
            foreach (var f in classDecl.Fields)
                ctor.ParameterDefinitions.Add(new ParameterDefinition((ushort)paramIdx++, Sanitize(f.Name), 0));
            classType.Methods.Add(ctor);

            var ctorBody = new CilMethodBody();
            ctor.MethodBody = ctorBody;
            var ctorIl = ctorBody.Instructions;

            // Call base constructor with inherited field args
            ctorIl.Add(CilOpCodes.Ldarg_0);
            for (var i = 0; i < inheritedFields.Count; i++)
                ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);

            // Store own fields
            for (var i = 0; i < fieldDefs.Count; i++)
            {
                ctorIl.Add(CilOpCodes.Ldarg_0);
                ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[inheritedFields.Count + i]);
                ctorIl.Add(CilOpCodes.Stfld, fieldDefs[i].Field);
            }

            ctorIl.Add(CilOpCodes.Ret);

            // Parameterless constructor for test frameworks
            if (classDecl.Fields.Count > 0 || inheritedFields.Count > 0)
            {
                var defaultCtorBaseRef = ResolveAsmBaseConstructor(baseTypeDef, 0);
                var defaultCtor = new MethodDefinition(".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                    | MethodAttributes.RuntimeSpecialName,
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
                classType.Methods.Add(defaultCtor);
                var defaultCtorBody = new CilMethodBody();
                defaultCtor.MethodBody = defaultCtorBody;
                var defaultCtorIl = defaultCtorBody.Instructions;
                defaultCtorIl.Add(CilOpCodes.Ldarg_0);
                defaultCtorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)defaultCtorBaseRef);
                defaultCtorIl.Add(CilOpCodes.Ret);
            }
        }

        // Build field lookup for method bodies (own fields + inherited)
        var classFieldMap = new Dictionary<string, FieldDefinition>();
        for (var i = 0; i < classDecl.Fields.Count; i++)
            classFieldMap[classDecl.Fields[i].Name] = fieldDefs[i].Field;
        if (baseTypeDef is not null)
            AddAsmInheritedFieldsToMap(baseTypeDef, classFieldMap);

        // Emit methods
        foreach (var method in classDecl.Methods)
        {
            var retType = method.ReturnType == ZType.Unit
                ? _module.CorLibTypeFactory.Void
                : MapToClr(method.ReturnType);
            var methodParamTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();

            var isOverride = inheritedMethodNames.Contains(method.Name);
            var isInterfaceImpl = interfaceMethodNames.Contains(method.Name);
            var methodAttrs = MethodAttributes.Public;
            if (isOverride)
                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
            else if (isInterfaceImpl)
                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot
                                | MethodAttributes.HideBySig | MethodAttributes.Final;
            else if (classDecl.IsOpen)
                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig;

            var mb = new MethodDefinition(Sanitize(method.Name),
                methodAttrs,
                MethodSignature.CreateInstance(retType, methodParamTypes));
            for (var pi = 0; pi < method.Params.Count; pi++)
                mb.ParameterDefinitions.Add(new ParameterDefinition(
                    (ushort)(pi + 1), method.Params[pi].Name, 0));
            classType.Methods.Add(mb);
            EmitCustomAttributes(method.Attributes, mb);

            if (method.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(method.Body))
            {
                // Async class method: create synthetic FuncDef and delegate to async emitter
                var savedOffset = _instanceArgOffset;
                var savedReturnType = _currentFuncReturnType;
                var savedClassFields = _currentClassFields;
                var savedTypeDef = _currentTypeDefinition;
                var savedBaseTypeDef = _currentBaseTypeDefinition;
                _instanceArgOffset = 1;
                _currentFuncReturnType = method.ReturnType;
                _currentClassFields = classFieldMap;
                _currentTypeDefinition = classType;
                _currentBaseTypeDefinition = baseTypeDef;

                var syntheticFunc = new IrNode.FuncDef(
                    method.Name, method.Params, method.ReturnType, method.Body, false);
                EmitAsyncFuncDef(syntheticFunc, mb, classType);

                _currentClassFields = savedClassFields;
                _instanceArgOffset = savedOffset;
                _currentFuncReturnType = savedReturnType;
                _currentTypeDefinition = savedTypeDef;
                _currentBaseTypeDefinition = savedBaseTypeDef;
            }
            else
            {
                var methodBody = new CilMethodBody() { InitializeLocals = true };
                mb.MethodBody = methodBody;
                var methodIl = methodBody.Instructions;
                var methodLocals = new Dictionary<string, CilLocalVariable>();

                var savedOffset = _instanceArgOffset;
                var savedReturnType = _currentFuncReturnType;
                var savedClassFields = _currentClassFields;
                var savedTypeDef = _currentTypeDefinition;
                var savedBaseTypeDef = _currentBaseTypeDefinition;
                _instanceArgOffset = 1;
                _currentFuncReturnType = method.ReturnType;
                _currentClassFields = classFieldMap;
                _currentTypeDefinition = classType;
                _currentBaseTypeDefinition = baseTypeDef;

                EmitNode(method.Body, methodIl, method.Params, methodLocals);

                _currentClassFields = savedClassFields;
                _instanceArgOffset = savedOffset;
                _currentFuncReturnType = savedReturnType;
                _currentTypeDefinition = savedTypeDef;
                _currentBaseTypeDefinition = savedBaseTypeDef;

                if (method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    if (method.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        methodIl.Add(CilOpCodes.Pop);
                methodIl.Add(CilOpCodes.Ret);
            }
        }

        // Store class info for future subclasses
        _asmClassInfos[classDecl.Name] = new AsmClassInfo(
            classType, classDecl.IsOpen, classDecl.BaseClassName,
            classDecl.Fields, classDecl.Methods.Select(m => m.Name).ToList());
    }

    private IMethodDescriptor ResolveAsmBaseConstructor(TypeDefinition? baseTypeDef, int paramCount)
    {
        if (baseTypeDef is not null)
        {
            var baseCtor = baseTypeDef.Methods.FirstOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == paramCount);
            if (baseCtor is not null) return baseCtor;

            var defaultCtor = baseTypeDef.Methods.FirstOrDefault(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            if (defaultCtor is not null) return defaultCtor;
        }

        return _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!);
    }

    private List<IrField> GetAsmInheritedFields(string className)
    {
        var result = new List<IrField>();
        if (_asmClassInfos.TryGetValue(className, out var info))
        {
            if (info.BaseClassName is not null)
                result.AddRange(GetAsmInheritedFields(info.BaseClassName));
            result.AddRange(info.Fields);
        }
        return result;
    }

    private HashSet<string> GetAsmInheritedMethodNames(string className)
    {
        var result = new HashSet<string>();
        if (_asmClassInfos.TryGetValue(className, out var info))
        {
            foreach (var m in GetAsmInheritedMethodNames(info.BaseClassName ?? ""))
                result.Add(m);
            foreach (var m in info.MethodNames)
                result.Add(m);
        }
        return result;
    }

    private void CollectInterfaceMethodNames(string ifaceName, HashSet<string> names)
    {
        // Try CLR reflection first
        var clrType = _clrInterop.FindType(ifaceName);
        if (clrType is null)
        {
            foreach (var ns in ClrUsings)
            {
                clrType = _clrInterop.FindType(ns + "." + ifaceName);
                if (clrType is not null) break;
            }
        }

        if (clrType is not null)
        {
            foreach (var method in clrType.GetMethods())
                names.Add(method.Name);
            // Include methods from inherited interfaces
            foreach (var parentIface in clrType.GetInterfaces())
                foreach (var method in parentIface.GetMethods())
                    names.Add(method.Name);
            return;
        }

        // Fall back to ZScheme-defined interfaces
        if (_userTypes.TryGetValue(ifaceName, out var userType) && userType is TypeDefinition typeDef)
        {
            foreach (var method in typeDef.Methods)
                if (method.Name is not null)
                    names.Add(method.Name.ToString());
        }
    }

    private void AddAsmInheritedFieldsToMap(TypeDefinition baseType, Dictionary<string, FieldDefinition> map)
    {
        // Use tracked class info to get original (unsanitized) field names
        var baseClassName = _asmClassInfos.Values
            .FirstOrDefault(i => i.TypeDef == baseType)?.BaseClassName;

        var info = _asmClassInfos.Values.FirstOrDefault(i => i.TypeDef == baseType);
        if (info is not null)
        {
            // Map original field names to their backing fields
            foreach (var irField in info.Fields)
            {
                var sanitizedName = Sanitize(irField.Name);
                var backingField = baseType.Fields
                    .FirstOrDefault(f => f.Name?.ToString() == $"<{sanitizedName}>k__BackingField");
                if (backingField is not null && !map.ContainsKey(irField.Name))
                    map[irField.Name] = backingField;
            }

            if (info.BaseClassName is not null &&
                _asmClassInfos.TryGetValue(info.BaseClassName, out var parentInfo))
                AddAsmInheritedFieldsToMap(parentInfo.TypeDef, map);
        }
    }

    private void EmitSuperMethodCall(IrNode.SuperMethodCall superCall,
        CilInstructionCollection il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals)
    {
        if (_currentBaseTypeDefinition is null)
        {
            diagnostics.Error("super/ can only be used in a class with a base class", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var baseMethod = _currentBaseTypeDefinition.Methods.FirstOrDefault(m =>
            !m.IsConstructor && m.Name == Sanitize(superCall.MethodName));
        if (baseMethod is null)
        {
            diagnostics.Error($"Base class has no method '{superCall.MethodName}'", SourceSpan.None);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        il.Add(CilOpCodes.Ldarg_0);
        foreach (var arg in superCall.Args)
            EmitNode(arg, il, outerParams, locals);
        il.Add(CilOpCodes.Call, baseMethod);
    }

    /// <summary>
    ///     Helper to resolve a generic parameter signature to a concrete type from type args.
    /// </summary>
    private static TypeSignature ResolveGenericParam(TypeSignature sig, TypeSignature[] typeArgs)
    {
        if (sig is GenericParameterSignature gps && gps.Index < typeArgs.Length)
            return typeArgs[gps.Index];
        return sig;
    }

    private void RegisterUserType(string name, ITypeDefOrRef typeRef)
    {
        _userTypes[name] = typeRef;
        _userTypeSignatures[name] = typeRef.ToTypeSignature(false);
    }

    private static string Sanitize(string name) => NameConverter.SanitizeIdentifier(name);

    private static string SanitizeParam(string name) => NameConverter.SanitizeParameter(name);

    // ─── Async State Machine Generation ───────────────────────────────────

    private void EmitAsyncFuncDef(IrNode.FuncDef func, MethodDefinition stubMethod, TypeDefinition parentType)
    {
        Log.Debug("IlEmitter: emitting async state machine for {FuncName}", func.Name);
        var info = AsyncStateMachineAnalyzer.Analyze(func);
        var smName = $"<{Sanitize(func.Name)}>d__{_asyncSmCounter++}";

        // Determine builder and task types
        var isVoid = info.IsVoidReturn;
        Type builderClrType;
        if (isVoid)
            builderClrType = typeof(AsyncTaskMethodBuilder);
        else
            builderClrType = typeof(AsyncTaskMethodBuilder<>)
                .MakeGenericType(IlTypeMapper.MapToClr(func.ReturnType));

        var builderTypeSig = _module.DefaultImporter.ImportType(builderClrType).ToTypeSignature(builderClrType.IsValueType);

        // --- Define state machine struct ---
        var smType = new TypeDefinition(
            "", smName,
            TypeAttributes.Sealed | TypeAttributes.NestedPrivate | TypeAttributes.SequentialLayout,
            _module.DefaultImporter.ImportType(typeof(ValueType)));
        smType.Interfaces.Add(new InterfaceImplementation(
            _module.DefaultImporter.ImportType(typeof(IAsyncStateMachine))));
        // [CompilerGenerated]
        var compGenCtor = typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!;
        smType.CustomAttributes.Add(new CustomAttribute(
            (ICustomAttributeType)_module.DefaultImporter.ImportMethod(compGenCtor)));
        parentType.NestedTypes.Add(smType);

        // --- Define fields ---
        var stateField = new FieldDefinition("__state", FieldAttributes.Public,
            new FieldSignature(_module.CorLibTypeFactory.Int32));
        smType.Fields.Add(stateField);

        var builderField = new FieldDefinition("__builder", FieldAttributes.Public,
            new FieldSignature(builderTypeSig));
        smType.Fields.Add(builderField);

        // __this field for instance method async state machines
        FieldDefinition? thisField = null;
        if (_instanceArgOffset == 1 && _currentTypeDefinition is not null)
        {
            thisField = new FieldDefinition("__this", FieldAttributes.Public,
                new FieldSignature(_currentTypeDefinition.ToTypeSignature(false)));
            smType.Fields.Add(thisField);
        }

        // Parameter fields
        var varFields = new Dictionary<string, FieldDefinition>();
        foreach (var p in func.Params)
        {
            var pField = new FieldDefinition(Sanitize(p.Name), FieldAttributes.Public,
                new FieldSignature(MapToClr(p.Type)));
            smType.Fields.Add(pField);
            varFields[p.Name] = pField;
        }

        // Hoisted local fields
        foreach (var local in info.HoistedLocals)
            if (!varFields.ContainsKey(local.Name))
            {
                var lField = new FieldDefinition($"<{Sanitize(local.Name)}>5__", FieldAttributes.Public,
                    new FieldSignature(MapToClr(local.Type)));
                smType.Fields.Add(lField);
                varFields[local.Name] = lField;
            }

        // Awaiter fields
        var awaiterFields = new Dictionary<int, FieldDefinition>();
        foreach (var ap in info.AwaitPoints)
        {
            var awaiterClrType = GetAwaiterClrType(ap);
            var awaiterField = new FieldDefinition($"__awaiter{ap.StateNumber}",
                FieldAttributes.Private,
                new FieldSignature(_module.DefaultImporter.ImportType(awaiterClrType).ToTypeSignature(awaiterClrType.IsValueType)));
            smType.Fields.Add(awaiterField);
            awaiterFields[ap.StateNumber] = awaiterField;
        }

        // --- Emit MoveNext method ---
        EmitMoveNextMethod(func, smType, stateField, builderField, builderClrType,
            varFields, awaiterFields, info, thisField);

        // --- Emit SetStateMachine method ---
        EmitSetStateMachineMethod(smType, builderField, builderClrType);

        // --- Emit stub method body ---
        EmitAsyncStubBody(func, stubMethod, smType, stateField, builderField, builderClrType, varFields, thisField);

        // --- Add [AsyncStateMachine] attribute to stub ---
        var asmAttrCtor = typeof(AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!;
        var asmAttr = new CustomAttribute(
            (ICustomAttributeType)_module.DefaultImporter.ImportMethod(asmAttrCtor));
        asmAttr.Signature = new CustomAttributeSignature(
            new CustomAttributeArgument(
                _module.DefaultImporter.ImportType(typeof(Type)).ToTypeSignature(false),
                smType.ToTypeSignature(true)));
        stubMethod.CustomAttributes.Add(asmAttr);
    }

    private static Type GetAwaiterClrType(AsyncStateMachineAnalyzer.AwaitPointInfo ap)
    {
        if (ap.ResultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return typeof(TaskAwaiter);
        var innerClr = IlTypeMapper.MapToClr(ap.ResultType);
        return typeof(TaskAwaiter<>).MakeGenericType(innerClr);
    }

    private void EmitAsyncStubBody(
        IrNode.FuncDef func,
        MethodDefinition stubMethod,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields,
        FieldDefinition? thisField = null)
    {
        var body = new CilMethodBody() { InitializeLocals = true };
        stubMethod.MethodBody = body;
        var il = body.Instructions;

        // Local 0: the state machine struct
        var smLocal = new CilLocalVariable(smType.ToTypeSignature(true));
        body.LocalVariables.Add(smLocal);

        // initobj smType
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Initobj, smType.ToTypeSignature(true).ToTypeDefOrRef());

        // Copy 'this' into __this field for instance method async state machines
        if (thisField is not null)
        {
            il.Add(CilOpCodes.Ldloca, smLocal);
            il.Add(CilOpCodes.Ldarg_0); // this
            il.Add(CilOpCodes.Stfld, thisField);
        }

        // Copy parameters into state machine fields
        for (var i = 0; i < func.Params.Count; i++)
        {
            il.Add(CilOpCodes.Ldloca, smLocal);
            il.Add(CilOpCodes.Ldarg, stubMethod.Parameters[i]);
            il.Add(CilOpCodes.Stfld, varFields[func.Params[i].Name]);
        }

        // sm.__builder = AsyncTaskMethodBuilder<T>.Create()
        var createMethod = _module.DefaultImporter.ImportMethod(builderClrType.GetMethod("Create")!);
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Call, createMethod);
        il.Add(CilOpCodes.Stfld, builderField);

        // sm.__state = -1
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stfld, stateField);

        // sm.__builder.Start<SM>(ref sm)
        var startMethodRef = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(builderClrType.GetMethod("Start")!);
        var startSpec = new MethodSpecification(startMethodRef,
            new GenericInstanceMethodSignature([smType.ToTypeSignature(true)]));

        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Call, startSpec);

        // return sm.__builder.Task
        var taskPropGetter = _module.DefaultImporter.ImportMethod(
            builderClrType.GetProperty("Task")!.GetGetMethod()!);
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Call, taskPropGetter);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitMoveNextMethod(
        IrNode.FuncDef func,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields,
        Dictionary<int, FieldDefinition> awaiterFields,
        AsyncStateMachineAnalyzer.AsyncMethodInfo info,
        FieldDefinition? thisField = null)
    {
        var moveNext = new MethodDefinition("MoveNext",
            MethodAttributes.Private | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void));
        smType.Methods.Add(moveNext);

        // Override IAsyncStateMachine.MoveNext
        var moveNextIntf = _module.DefaultImporter.ImportMethod(
            typeof(IAsyncStateMachine).GetMethod("MoveNext")!);
        smType.MethodImplementations.Add(new MethodImplementation(
            (IMethodDefOrRef)moveNextIntf, moveNext));

        var body = new CilMethodBody() { InitializeLocals = true };
        moveNext.MethodBody = body;
        var il = body.Instructions;

        // Declare locals
        var stateLocal = new CilLocalVariable(_module.CorLibTypeFactory.Int32);
        body.LocalVariables.Add(stateLocal);

        // Local for final result (if non-void)
        CilLocalVariable? resultLocal = null;
        if (!info.IsVoidReturn)
        {
            resultLocal = new CilLocalVariable(MapToClr(func.ReturnType));
            body.LocalVariables.Add(resultLocal);
        }

        // Exception local for catch block
        var exLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(typeof(Exception)).ToTypeSignature(false));
        body.LocalVariables.Add(exLocal);

        // Declare locals for each param (load from fields at resume points)
        var paramLocals = new Dictionary<string, CilLocalVariable>();
        foreach (var p in func.Params)
        {
            var pLocal = new CilLocalVariable(MapToClr(p.Type));
            body.LocalVariables.Add(pLocal);
            paramLocals[p.Name] = pLocal;
        }

        // Set up MoveNext context
        _moveNextCtx = new AsyncMoveNextContext
        {
            SmType = smType,
            StateField = stateField,
            BuilderField = builderField,
            StateLocal = stateLocal,
            VarFields = varFields,
            AwaiterFields = awaiterFields,
            AllLocals = [],
            IsVoidReturn = info.IsVoidReturn,
            ThisField = thisField,
            NextAwaitState = 0
        };

        // Add param locals to the AllLocals tracking
        foreach (var p in func.Params)
            _moveNextCtx.AllLocals.Add((p.Name, paramLocals[p.Name]));

        // Load __state into local
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, stateField);
        il.Add(CilOpCodes.Stloc, stateLocal);

        // --- Try block ---
        var tryStartLabel = new CilInstructionLabel();
        tryStartLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Jump table: create resume labels for each await point
        var resumeLabels = new CilInstructionLabel[info.AwaitPoints.Count];
        for (var i = 0; i < info.AwaitPoints.Count; i++)
            resumeLabels[i] = new CilInstructionLabel();

        // switch (state) { 0: goto resume0, 1: goto resume1, ... }
        if (resumeLabels.Length > 0)
        {
            il.Add(CilOpCodes.Ldloc, stateLocal);
            il.Add(CilOpCodes.Switch, resumeLabels.Cast<ICilLabel>().ToArray());
        }

        // Initial state: load params from fields into locals
        foreach (var p in func.Params)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, varFields[p.Name]);
            il.Add(CilOpCodes.Stloc, paramLocals[p.Name]);
        }

        // Store resume labels and exit label for EmitMoveNextAwait to use
        _moveNextCtx.ResumeLabels = resumeLabels;
        var exitLabel = new CilInstructionLabel();
        _moveNextCtx.ExitLabel = exitLabel;

        // Emit the body using regular EmitNode (outerParams is empty; params come from locals dict)
        var bodyLocals = new Dictionary<string, CilLocalVariable>(paramLocals);
        EmitNode(func.Body, il, [], bodyLocals);

        // Store the result
        if (!info.IsVoidReturn)
            il.Add(CilOpCodes.Stloc, resultLocal!);
        else if (func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            il.Add(CilOpCodes.Pop);

        // Leave try block
        var afterTryLabel = new CilInstructionLabel();
        il.Add(CilOpCodes.Leave, afterTryLabel);

        // --- Catch block ---
        var catchStartLabel = new CilInstructionLabel();
        catchStartLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Stloc, exLocal);

        // __state = -2
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, -2);
        il.Add(CilOpCodes.Stfld, stateField);

        // __builder.SetException(ex)
        var setException = _module.DefaultImporter.ImportMethod(
            builderClrType.GetMethod("SetException", [typeof(Exception)])!);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Ldloc, exLocal);
        il.Add(CilOpCodes.Call, setException);

        il.Add(CilOpCodes.Leave, exitLabel);

        // --- After try/catch ---
        afterTryLabel.Instruction = il.Add(CilOpCodes.Nop);

        // __state = -2
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, -2);
        il.Add(CilOpCodes.Stfld, stateField);

        // __builder.SetResult(result)
        // __builder.SetResult(result)
        if (info.IsVoidReturn)
        {
            var setResult = _module.DefaultImporter.ImportMethod(
                builderClrType.GetMethod("SetResult", Type.EmptyTypes)!);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldflda, builderField);
            il.Add(CilOpCodes.Call, setResult);
        }
        else
        {
            var setResultMethod = builderClrType.GetMethod("SetResult",
                [IlTypeMapper.MapToClr(func.ReturnType)])!;
            var setResult = _module.DefaultImporter.ImportMethod(setResultMethod);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldflda, builderField);
            il.Add(CilOpCodes.Ldloc, resultLocal!);
            il.Add(CilOpCodes.Call, setResult);
        }

        exitLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Ret);

        // Register exception handler
        il.Owner.ExceptionHandlers.Add(new CilExceptionHandler
        {
            HandlerType = CilExceptionHandlerType.Exception,
            TryStart = tryStartLabel,
            TryEnd = catchStartLabel,
            HandlerStart = catchStartLabel,
            HandlerEnd = afterTryLabel,
            ExceptionType = _module.DefaultImporter.ImportType(typeof(Exception)).ToTypeDefOrRef()
        });

        _moveNextCtx = null;
    }

    private void EmitMoveNextAwait(IrNode.Await awaitNode, CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, CilLocalVariable> locals)
    {
        var ctx = _moveNextCtx!;
        var stateNum = ctx.NextAwaitState++;
        var awaiterField = ctx.AwaiterFields[stateNum];
        var resumeLabel = ctx.ResumeLabels![stateNum];
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(awaitNode.Expr.Type);
        var isVoidAwait = resultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

        // Determine awaiter CLR type
        Type awaiterClrType;
        if (isVoidAwait)
            awaiterClrType = typeof(TaskAwaiter);
        else
            awaiterClrType = typeof(TaskAwaiter<>)
                .MakeGenericType(IlTypeMapper.MapToClr(resultType));

        // Declare a local for the awaiter
        var awaiterLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(awaiterClrType).ToTypeSignature(awaiterClrType.IsValueType));
        il.Owner.LocalVariables.Add(awaiterLocal);

        // Emit the task expression
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Call GetAwaiter()
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getAwaiterMethod));
        il.Add(CilOpCodes.Stloc, awaiterLocal);

        // Check IsCompleted
        var isCompletedGetter = awaiterClrType.GetProperty("IsCompleted")!.GetGetMethod()!;
        var completedLabel = new CilInstructionLabel();

        il.Add(CilOpCodes.Ldloca, awaiterLocal);
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(isCompletedGetter));
        il.Add(CilOpCodes.Brtrue, completedLabel);

        // --- Not completed: suspend ---

        // Set state
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, stateNum);
        il.Add(CilOpCodes.Stfld, ctx.StateField);
        il.Add(CilOpCodes.Ldc_I4, stateNum);
        il.Add(CilOpCodes.Stloc, ctx.StateLocal);

        // Store awaiter to field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldloc, awaiterLocal);
        il.Add(CilOpCodes.Stfld, awaiterField);

        // Save all locals to fields
        foreach (var (name, local) in ctx.AllLocals)
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldloc, local);
                il.Add(CilOpCodes.Stfld, field);
            }

        // Call __builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
        var awaitUnsafe = GetAwaitUnsafeOnCompletedRef(awaiterClrType, ctx);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, ctx.BuilderField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, awaiterField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, awaitUnsafe);

        // Leave try block (cannot use ret inside try)
        il.Add(CilOpCodes.Leave, ctx.ExitLabel!);

        // --- Resume label (jump table target) ---
        resumeLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Restore awaiter from field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, awaiterField);
        il.Add(CilOpCodes.Stloc, awaiterLocal);

        // Clear awaiter field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, awaiterField);
        il.Add(CilOpCodes.Initobj,
            _module.DefaultImporter.ImportType(awaiterClrType).ToTypeSignature(awaiterClrType.IsValueType).ToTypeDefOrRef());

        // Reset state to -1
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stloc, ctx.StateLocal);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stfld, ctx.StateField);

        // Restore all locals from fields
        foreach (var (name, local) in ctx.AllLocals)
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldfld, field);
                il.Add(CilOpCodes.Stloc, local);
            }

        // --- Completed label (fast path + resume path converge) ---
        completedLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Call GetResult()
        var getResultMethod = awaiterClrType.GetMethod("GetResult", Type.EmptyTypes)!;
        il.Add(CilOpCodes.Ldloca, awaiterLocal);
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getResultMethod));

        // Result (T or void) is now on the stack
    }

    private IMethodDescriptor GetAwaitUnsafeOnCompletedRef(Type awaiterClrType, AsyncMoveNextContext ctx)
    {
        // Import the AwaitUnsafeOnCompleted method from the builder type
        var builderType = ctx.BuilderField.Signature!.FieldType;

        // Find the open AwaitUnsafeOnCompleted method on the CLR builder type
        Type builderClrType;
        if (ctx.IsVoidReturn)
            builderClrType = typeof(AsyncTaskMethodBuilder);
        else
        {
            // Reconstruct the closed generic builder CLR type
            // The builder field's signature tells us the type args
            if (builderType is GenericInstanceTypeSignature git)
            {
                var innerClrTypes = git.TypeArguments.Select(ta =>
                {
                    // Map back from TypeSignature to CLR Type
                    var fullName = ta.FullName;
                    return Type.GetType(fullName) ?? typeof(object);
                }).ToArray();
                builderClrType = typeof(AsyncTaskMethodBuilder<>).MakeGenericType(innerClrTypes);
            }
            else
                builderClrType = typeof(AsyncTaskMethodBuilder);
        }

        // Use the AsmResolver approach:
        // Import the open generic method, then create a MethodSpecification
        var openAwaitMethod = builderClrType.GetMethods()
            .First(m => m.Name == "AwaitUnsafeOnCompleted" && m.IsGenericMethodDefinition);
        var importedMethod = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(openAwaitMethod);

        // If the builder is a generic instance, we need to reference the method on the closed type
        if (builderType is GenericInstanceTypeSignature gitSig)
        {
            // Create a MemberReference on the closed generic builder type
            var openMethod = typeof(AsyncTaskMethodBuilder<>).GetMethods()
                .First(m => m.Name == "AwaitUnsafeOnCompleted" && m.IsGenericMethodDefinition);
            var openParams = openMethod.GetParameters();

            var sig = MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Void,
                [new GenericParameterSignature(_module, GenericParameterType.Method, 0).MakeByReferenceType(),
                new GenericParameterSignature(_module, GenericParameterType.Method, 1).MakeByReferenceType()]);
            sig.GenericParameterCount = 2;
            var memberRef = new MemberReference(
                gitSig.ToTypeDefOrRef(),
                "AwaitUnsafeOnCompleted",
                sig);
            importedMethod = memberRef;
        }

        var awaiterSig = _module.DefaultImporter.ImportType(awaiterClrType).ToTypeSignature(awaiterClrType.IsValueType);
        var smSig = ctx.SmType.ToTypeSignature(true); // state machines are always value types

        return new MethodSpecification(importedMethod,
            new GenericInstanceMethodSignature([awaiterSig, smSig]));
    }

    private void EmitSetStateMachineMethod(
        TypeDefinition smType,
        FieldDefinition builderField,
        Type builderClrType)
    {
        var setSmMethod = new MethodDefinition("SetStateMachine",
            MethodAttributes.Private | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Void,
                [_module.DefaultImporter.ImportType(typeof(IAsyncStateMachine)).ToTypeSignature(false)]));
        setSmMethod.ParameterDefinitions.Add(new ParameterDefinition(1, "stateMachine", 0));
        smType.Methods.Add(setSmMethod);

        // Override IAsyncStateMachine.SetStateMachine
        var setSmIntf = _module.DefaultImporter.ImportMethod(
            typeof(IAsyncStateMachine).GetMethod("SetStateMachine")!);
        smType.MethodImplementations.Add(new MethodImplementation(
            (IMethodDefOrRef)setSmIntf, setSmMethod));

        var body = new CilMethodBody();
        setSmMethod.MethodBody = body;
        body.Instructions.Add(CilOpCodes.Ret);
    }

    private sealed class AsyncMoveNextContext
    {
        public required List<(string Name, CilLocalVariable Local)> AllLocals; // all locals to save/restore
        public required Dictionary<int, FieldDefinition> AwaiterFields; // state number -> awaiter field
        public required FieldDefinition BuilderField;
        public CilInstructionLabel? ExitLabel; // label after try/catch for suspension return
        public required bool IsVoidReturn;
        public int NextAwaitState;
        public CilInstructionLabel[]? ResumeLabels;
        public required TypeDefinition SmType;
        public required FieldDefinition StateField;
        public required CilLocalVariable StateLocal;
        public required Dictionary<string, FieldDefinition> VarFields; // params + locals -> fields
        public FieldDefinition? ThisField; // __this field for instance method async state machines
    }
}

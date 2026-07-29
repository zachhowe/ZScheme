using System.Reflection;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;
using DiagnosticBag = ZScheme.Compiler.Diagnostics.DiagnosticBag;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Emits .NET IL using AsmResolver.
/// </summary>
public sealed partial class IlEmitter(
    string assemblyName,
    DiagnosticBag diagnostics,
    string className,
    IReadOnlyList<string>? clrUsings = null,
    IReadOnlyList<string>? assemblySearchPaths = null,
    IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null,
    IReadOnlyList<string>? precompiledAssemblyPaths = null,
    string? ilNamespace = null,
    bool isModule = false,
    TypeAliasRegistry? typeAliases = null,
    // rawTypeName -> emitted name for renamed types exported by precompiled modules this
    // compilation consumes (loaded from their metadata). After precompiled assemblies are
    // imported under their baked names, AliasPrecompiledTypeRenames adds raw-source-name
    // aliases into the type registries so consumer references resolve.
    IReadOnlyDictionary<string, string>? precompiledTypeRenames = null,
    Pipeline.CoverageOptions? coverage = null
)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<IlEmitter>();

    private readonly Pipeline.CoverageOptions? _coverage = coverage;

    private readonly Dictionary<string, AsmClassInfo> _asmClassInfos = new();
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths, typeAliases);
    private readonly Dictionary<string, ZType.ZFuncType> _genericMethodTypes = new();
    private readonly string _ilNamespace = ilNamespace ?? assemblyName;

    private readonly Dictionary<string, MethodDefinition> _methods = new();
    private readonly Dictionary<string, IMethodDescriptor> _precompiledMethods = new();
    private readonly Dictionary<string, MethodInfo> _precompiledReflectionMethods = new();
    private readonly Dictionary<string, IFieldDescriptor> _staticFields = new();

    internal readonly TypeAliasRegistry _typeAliases = typeAliases ?? new TypeAliasRegistry();

    private readonly Dictionary<string, IMethodDescriptor> _unionCaseGetters = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _unionCasePropertyNames = new();

    private readonly Dictionary<string, ITypeDefOrRef> _unionCaseTypes = new();

    // Reflection `System.Type` parallel to `_userTypes`, populated only when a user type
    // originates from a precompiled assembly (i.e. we have a real loaded Type in hand).
    // Routed into `IlTypeMapper.MapToClr` so reflection-time generic instantiations against
    // imported union/record/struct types resolve to the actual closed type instead of
    // silently falling back to `System.Object` (which corrupts pattern-match decision trees).
    private readonly Dictionary<string, Type> _userReflectionTypes = new();
    private readonly Dictionary<string, ITypeDefOrRef> _userTypes = new();
    private readonly Dictionary<string, TypeSignature> _userTypeSignatures = new();

    // The async state-machine generator lives in IlAsyncEmitter; it is created lazily
    // (a primary-constructor field initializer cannot reference `this`) and holds a
    // back-reference to this emitter for callbacks (EmitNode, type mapping, etc.).
    private IlAsyncEmitter? _async;
    private IlAsyncEmitter Async => _async ??= new IlAsyncEmitter(this);

    private ITypeDefOrRef? _isExternalInitType;
    private int _lambdaId;

    internal ModuleDefinition _module = null!;
    private TypeSignature _valueTupleType = null!;
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];

    /// <summary>
    ///     Reflection-based type mapping for AsmResolver-internal MakeGenericType / MakeGenericMethod
    ///     operations. Threads the compilation's <see cref="TypeAliasRegistry" /> so user-declared
    ///     and stdlib-declared aliases resolve correctly, and the precompiled-assembly user-type
    ///     dictionary so imported unions/records/structs resolve to their real closed generic
    ///     instead of falling back to System.Object.
    /// </summary>
    internal Type MapToReflectionClr(ZType type)
    {
        return IlTypeMapper.MapToClr(
            type,
            _userReflectionTypes,
            diagnostics: diagnostics,
            typeAliases: _typeAliases,
            clrInterop: _clrInterop
        );
    }

    internal TypeSignature MapToClr(
        ZType type,
        EmitContext ctx,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null
    )
    {
        var result = AsmResolverTypeMapper.MapToClr(
            type,
            _module,
            _valueTupleType,
            _userTypeSignatures,
            typeParamMap ?? ctx.CurrentTypeParamMap,
            ctx.CurrentTypeVarMap,
            _typeAliases,
            _clrInterop
        );
        // If the mapper returned Object but the type has a dot-qualified name, try ClrInterop
        if (
            result == _module.CorLibTypeFactory.Object
            && type is ZType.ZNamedType { Name: var name }
            && name.Contains('.')
        )
        {
            var clrType = _clrInterop.FindType(name);
            if (clrType is not null)
            {
                Log.Debug(
                    "IlEmitter.MapToClr: CLR interop fallback for {TypeName} -> {ClrType}",
                    name,
                    clrType
                );
                result = _module
                    .DefaultImporter.ImportType(clrType)
                    .ToTypeSignature(clrType.IsValueType);
            }
        }

        return result;
    }

    private TypeSignature MapReturnTypeToClr(ZType type, EmitContext ctx)
    {
        return AsmResolverTypeMapper.MapReturnTypeToClr(
            type,
            _module,
            _valueTupleType,
            _userTypeSignatures,
            ctx.CurrentTypeParamMap,
            ctx.CurrentTypeVarMap,
            _typeAliases,
            _clrInterop
        );
    }

    private ITypeDefOrRef GetIsExternalInitType()
    {
        if (_isExternalInitType is not null)
            return _isExternalInitType;

        _isExternalInitType = _module.DefaultImporter.ImportType(typeof(IsExternalInit));
        return _isExternalInitType;
    }

    private MethodDefinition CreateInitSetter(
        TypeDefinition declaringType,
        string propertyName,
        TypeSignature fieldType,
        FieldDefinition backingField,
        bool isValueType = false
    )
    {
        var initReturnType = new CustomModifierTypeSignature(
            GetIsExternalInitType(),
            true,
            _module.CorLibTypeFactory.Void
        );
        // Structs cannot have virtual instance methods; record-class setters are virtual.
        var attrs =
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        if (!isValueType)
            attrs |= MethodAttributes.Virtual;
        var setter = new MethodDefinition(
            $"set_{propertyName}",
            attrs,
            new MethodSignature(CallingConventionAttributes.HasThis, initReturnType, [fieldType])
        );
        setter.ParameterDefinitions.Add(new ParameterDefinition(1, "value", 0));
        var setBody = new CilMethodBody { InitializeLocals = true };
        setter.MethodBody = setBody;
        var setIl = setBody.Instructions;
        setIl.Add(CilOpCodes.Ldarg_0);
        setIl.Add(CilOpCodes.Ldarg_1);
        setIl.Add(CilOpCodes.Stfld, ResolveSelfField(declaringType, backingField));
        setIl.Add(CilOpCodes.Ret);
        return setter;
    }

    /// <summary>
    ///     Returns a closed self-instantiation of <paramref name="typeDef" /> (the type
    ///     applied to its own generic parameters), or <c>null</c> if the type is non-generic.
    ///     Used as the declaring-type token for IL member references emitted inside the
    ///     type's own methods.
    /// </summary>
    /// <remarks>
    ///     When a type is generic, IL that references its fields or methods from within
    ///     the type's own body must use a declaring-type token that carries generic
    ///     arguments (e.g. <c>Some&lt;!0&gt;</c>), not the bare open type (<c>Some</c>).
    ///     <c>this</c> on an instance method of <c>Some&lt;T&gt;</c> is typed as
    ///     <c>Some&lt;!0&gt;</c>; passing a bare <see cref="FieldDefinition" /> or
    ///     <see cref="MethodDefinition" /> to <c>stfld</c>/<c>ldfld</c>/<c>call</c> resolves
    ///     the declaring type as the open <c>Some</c>, which <c>ilverify</c> rejects with
    ///     a StackUnexpected error. For non-generic types the bare definition is already
    ///     the correct token, so callers can use <c>null</c> to mean "no rewrite needed".
    ///     The <c>isValueType</c> passed to <see cref="TypeDefinitionExtensions.MakeGenericInstanceType" />
    ///     is read from <paramref name="typeDef" /> so that struct records emit a ValueType
    ///     signature and record classes emit a Class signature.
    /// </remarks>
    private GenericInstanceTypeSignature? MakeSelfGenericInstance(TypeDefinition typeDef)
    {
        if (typeDef.GenericParameters.Count == 0)
            return null;
        var genArgs = typeDef
            .GenericParameters.Select(
                TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i)
            )
            .ToArray();
        return typeDef.MakeGenericInstanceType(typeDef.IsValueType, genArgs);
    }

    /// <summary>
    ///     Resolves an <see cref="IFieldDescriptor" /> for <paramref name="field" /> that is
    ///     safe to use as the operand of <c>ldfld</c>/<c>stfld</c> inside members of
    ///     <paramref name="declaringType" />. If the declaring type is generic, this
    ///     returns a <see cref="MemberReference" /> anchored on the closed self-instantiation
    ///     (<c>DeclaringType&lt;!0, !1, ...&gt;</c>) so the emitted IL passes verification.
    ///     For non-generic types the bare <see cref="FieldDefinition" /> is returned unchanged.
    /// </summary>
    private IFieldDescriptor ResolveSelfField(TypeDefinition declaringType, FieldDefinition field)
    {
        var closed = MakeSelfGenericInstance(declaringType);
        return closed is null
            ? field
            : new MemberReference(closed.ToTypeDefOrRef(), field.Name!, field.Signature!);
    }

    /// <summary>
    ///     Recursively rewrites <em>method</em>-kind generic parameter references
    ///     (<c>!!i</c>) to <em>type</em>-kind references (<c>!i</c>) of the same index.
    ///     When a closure captured inside a generic method is lifted into a generic
    ///     nested type, the type's fields and <c>Invoke</c> signature may only reference
    ///     the <em>type's</em> own generic parameters — referencing the enclosing
    ///     method's parameters (which is what <see cref="MapToClr" /> emits in a generic
    ///     method body) produces IL that <c>ilverify</c> rejects with StackUnexpected /
    ///     DelegateCtor errors and that the JIT refuses with <c>InvalidProgramException</c>.
    /// </summary>
    private TypeSignature MethodGpToTypeGp(TypeSignature sig)
    {
        switch (sig)
        {
            case GenericParameterSignature { ParameterType: GenericParameterType.Method } gps:
                return new GenericParameterSignature(_module, GenericParameterType.Type, gps.Index);
            case GenericInstanceTypeSignature git:
            {
                var args = git.TypeArguments.Select(MethodGpToTypeGp).ToArray();
                return new GenericInstanceTypeSignature(git.GenericType, git.IsValueType, args);
            }
            case SzArrayTypeSignature sz:
                return MethodGpToTypeGp(sz.BaseType).MakeSzArrayType();
            case ByReferenceTypeSignature br:
                return MethodGpToTypeGp(br.BaseType).MakeByReferenceType();
            case PointerTypeSignature ptr:
                return MethodGpToTypeGp(ptr.BaseType).MakePointerType();
            default:
                return sig;
        }
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
            diagnostics.Warning(
                $"Failed to load precompiled assembly '{path}': {ex.Message}",
                SourceSpan.None
            );
            return;
        }

        Log.Debug(
            "IlEmitter: precompiled assembly loaded, {TypeCount} exported types",
            asm.GetExportedTypes().Length
        );

        var abstractBases = new Dictionary<Type, string>();
        foreach (var type in asm.GetExportedTypes())
        {
            if (type is { IsAbstract: true, IsSealed: true }) // static class (module class)
            {
                foreach (
                    var method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly
                    )
                )
                {
                    var imported = _module.DefaultImporter.ImportMethod(method);
                    _precompiledMethods[method.Name] = imported;
                    _precompiledReflectionMethods[method.Name] = method;
                    // Qualified key for overload-resolved call sites that need to
                    // route past the bare-name last-write-wins overwrite.
                    var qualified = $"{type.Name}.{method.Name}";
                    _precompiledMethods[qualified] = imported;
                    _precompiledReflectionMethods[qualified] = method;
                }

                foreach (
                    var field in type.GetFields(
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly
                    )
                )
                    _staticFields[field.Name] = _module.DefaultImporter.ImportField(field);

                RegisterNestedTypes(type, abstractBases);
                var methodCount = type.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly
                ).Length;
                var fieldCount = type.GetFields(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly
                ).Length;
                Log.Debug(
                    "IlEmitter: precompiled module class {TypeName}: {MethodCount} methods, {FieldCount} fields",
                    type.Name,
                    methodCount,
                    fieldCount
                );
            }

            if (type.IsAbstract && !type.IsSealed && !type.IsInterface)
            {
                RegisterUserType(
                    StripBacktickArity(type.Name),
                    ImportTypeWithGenericArity(type),
                    reflectionType: type
                );
                abstractBases[type] = StripBacktickArity(type.Name);
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                var strippedTypeName = StripBacktickArity(type.Name);
                var strippedNestedName = StripBacktickArity(nested.Name);
                var caseKey = $"{strippedTypeName}.{strippedNestedName}";
                var importedNested = ImportTypeWithGenericArity(nested);
                _unionCaseTypes[caseKey] = importedNested;
                RegisterUserType(strippedNestedName, importedNested, reflectionType: nested);

                // A union case's base is the union's (also-nested) abstract base, so it keys
                // additionally under "{union}.{case}". A standalone record/struct nested in the
                // module class has an external base (object/ValueType); it must key under its
                // own name "{rec}.{rec}", mirroring the source path's RegisterSingleCasePattern.
                // Records are emitted nested in module mode, so this is the only registration
                // that lets `(match v [(Rec a b) ...])` resolve a *precompiled-imported* record
                // — without it the pattern reported "Cannot resolve constructor type".
                var nestedBase = nested.BaseType;
                var isUnionCase =
                    nestedBase is not null
                    && nestedBase.IsNested
                    && nestedBase.DeclaringType == type;
                var secondaryKey = isUnionCase
                    ? $"{StripBacktickArity(nestedBase!.Name)}.{strippedNestedName}"
                    : $"{strippedNestedName}.{strippedNestedName}";
                _unionCaseTypes.TryAdd(secondaryKey, importedNested);

                foreach (
                    var prop in nested.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                )
                {
                    var getter = prop.GetGetMethod();
                    if (getter is null)
                        continue;
                    _unionCaseGetters[$"{caseKey}.{prop.Name}"] =
                        _module.DefaultImporter.ImportMethod(getter);
                    _unionCaseGetters.TryAdd(
                        $"{secondaryKey}.{prop.Name}",
                        _module.DefaultImporter.ImportMethod(getter)
                    );
                }

                var propNames = nested
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name)
                    .ToList();
                if (propNames.Count <= 0)
                    continue;
                _unionCasePropertyNames[caseKey] = propNames;
                _unionCasePropertyNames.TryAdd(secondaryKey, propNames);
            }

            if (type is { IsAbstract: false, IsNested: false, IsSealed: false })
                RegisterUserType(
                    StripBacktickArity(type.Name),
                    ImportTypeWithGenericArity(type),
                    reflectionType: type
                );
        }

        foreach (var type in asm.GetExportedTypes())
            if (
                type is { IsSealed: true, IsAbstract: false, IsNested: false, BaseType: not null }
                && abstractBases.TryGetValue(
                    type.BaseType.IsGenericType
                        ? type.BaseType.GetGenericTypeDefinition()
                        : type.BaseType,
                    out var baseName
                )
            )
            {
                var strippedName = StripBacktickArity(type.Name);
                var caseKey = $"{baseName}.{strippedName}";
                if (_unionCaseTypes.ContainsKey(caseKey))
                    continue;
                var importedCaseType = ImportTypeWithGenericArity(type);
                _unionCaseTypes[caseKey] = importedCaseType;
                RegisterUserType(strippedName, importedCaseType, reflectionType: type);

                foreach (
                    var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                )
                {
                    var getter = prop.GetGetMethod();
                    if (getter is not null)
                        _unionCaseGetters[$"{baseName}.{strippedName}.{prop.Name}"] =
                            _module.DefaultImporter.ImportMethod(getter);
                }

                var propNames = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name)
                    .ToList();
                if (propNames.Count > 0)
                    _unionCasePropertyNames[caseKey] = propNames;
            }
    }

    private void RegisterNestedTypes(Type moduleType, Dictionary<Type, string> abstractBases)
    {
        foreach (var nested in moduleType.GetNestedTypes(BindingFlags.Public))
        {
            var importedType = ImportTypeWithGenericArity(nested);
            var nestedName = StripBacktickArity(nested.Name);

            switch (nested.IsAbstract)
            {
                case true when nested is { IsSealed: false, IsInterface: false }:
                {
                    RegisterUserType(nestedName, importedType, reflectionType: nested);
                    abstractBases[nested] = nestedName;

                    foreach (var sibling in moduleType.GetNestedTypes(BindingFlags.Public))
                        if (
                            sibling is { IsSealed: true, IsAbstract: false, BaseType: not null }
                            && (
                                sibling.BaseType.IsGenericType
                                    ? sibling.BaseType.GetGenericTypeDefinition() == nested
                                    : sibling.BaseType == nested
                            )
                        )
                        {
                            var siblingName = StripBacktickArity(sibling.Name);
                            var caseKey = $"{nestedName}.{siblingName}";
                            if (_unionCaseTypes.ContainsKey(caseKey))
                                continue;
                            var importedSibling = ImportTypeWithGenericArity(sibling);
                            _unionCaseTypes[caseKey] = importedSibling;
                            RegisterUserType(siblingName, importedSibling, reflectionType: sibling);

                            foreach (
                                var prop in sibling.GetProperties(
                                    BindingFlags.Public | BindingFlags.Instance
                                )
                            )
                            {
                                var getter = prop.GetGetMethod();
                                if (getter is not null)
                                    _unionCaseGetters[$"{nestedName}.{siblingName}.{prop.Name}"] =
                                        _module.DefaultImporter.ImportMethod(getter);
                            }

                            var propNames = sibling
                                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .Select(p => p.Name)
                                .ToList();
                            if (propNames.Count > 0)
                                _unionCasePropertyNames[caseKey] = propNames;
                        }

                    break;
                }
                case false when nested.IsSealed && nested.GetMethod("<Clone>$") is not null:
                    RegisterUserType(nestedName, importedType, reflectionType: nested);
                    break;
            }

            if (!nested.IsAbstract && nested.IsSealed && nested.GetMethod("<Clone>$") is null)
                RegisterUserType(nestedName, importedType, reflectionType: nested);
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
            case IrNode.TypeAliasDecl:
                break;
            case IrNode.Let let:
                mainStatements.Add(let);
                break;
            case IrNode.ClrCall:
            case IrNode.Call:
            // A bare top-level `use` or `with-handlers` runs for effect in the .cctor like any
            // other top-level statement; EmitNode gives it the usual try/finally or protected
            // region. Without these cases it matches nothing and the whole form is silently
            // dropped from the output.
            case IrNode.Use:
            case IrNode.WithHandlers:
                mainStatements.Add(node);
                break;
        }
    }

    private MethodDefinition RegisterFuncSignature(
        IrNode.FuncDef func,
        TypeDefinition typeDefinition,
        EmitContext ctx
    )
    {
        var isGeneric = func.TypeParams is { Count: > 0 };

        TypeSignature returnType;
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            {
                returnType = _module
                    .DefaultImporter.ImportType(typeof(Task))
                    .ToTypeSignature(false);
            }
            else
            {
                var taskOpen = _module.DefaultImporter.ImportType(typeof(Task<>));
                returnType = taskOpen
                    .ToTypeSignature(false)
                    .MakeGenericInstanceType(false, [MapToClr(func.ReturnType, ctx)]);
            }
        }
        else
        {
            returnType = MapReturnTypeToClr(func.ReturnType, ctx);
        }

        var emittedName = Emitted(func.EmitName, func.Name);
        var paramTypes = func.Params.Select(p => MapToClr(p.Type, ctx)).ToArray();
        var methodDef = new MethodDefinition(
            emittedName,
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(returnType, paramTypes)
        );

        if (isGeneric)
        {
            foreach (var tp in func.TypeParams!)
                methodDef.GenericParameters.Add(new GenericParameter(tp));

            // Re-resolve return type and param types with generic params available
            var varNameMap = BuildTypeVarMap(func);
            var typeVarMap = new Dictionary<int, TypeSignature>();
            var typeParamMap = new Dictionary<string, TypeSignature>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx < 0)
                    continue;
                var gpSig = new GenericParameterSignature(
                    _module,
                    GenericParameterType.Method,
                    idx
                );
                typeVarMap[varId] = gpSig;
                typeParamMap[paramName] = gpSig;
            }

            var sigCtx = ctx with
            {
                CurrentTypeVarMap = typeVarMap,
                CurrentTypeParamMap = typeParamMap,
            };

            if (func.IsAsync)
            {
                if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                {
                    returnType = _module
                        .DefaultImporter.ImportType(typeof(Task))
                        .ToTypeSignature(false);
                }
                else
                {
                    var taskOpen = _module.DefaultImporter.ImportType(typeof(Task<>));
                    returnType = taskOpen
                        .ToTypeSignature(false)
                        .MakeGenericInstanceType(false, [MapToClr(func.ReturnType, sigCtx)]);
                }
            }
            else
            {
                returnType = MapReturnTypeToClr(func.ReturnType, sigCtx);
            }

            paramTypes = func.Params.Select(p => MapToClr(p.Type, sigCtx)).ToArray();
            methodDef.Signature = MethodSignature.CreateStatic(
                returnType,
                func.TypeParams!.Count,
                paramTypes
            );
        }

        for (var i = 0; i < func.Params.Count; i++)
        {
            var paramDef = new ParameterDefinition(
                (ushort)(i + 1),
                SanitizeParam(func.Params[i].Name),
                0
            );
            if (func.Params[i].IsVariadic)
            {
                var paramArrayCtor = _module.DefaultImporter.ImportMethod(
                    typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!
                );
                paramDef.CustomAttributes.Add(
                    new CustomAttribute((ICustomAttributeType)paramArrayCtor)
                );
            }

            methodDef.ParameterDefinitions.Add(paramDef);
        }

        typeDefinition.Methods.Add(methodDef);
        EmitCustomAttributes(func.Attributes, methodDef);
        var bareKey = emittedName;
        var qualifiedKey = $"{typeDefinition.Name}.{bareKey}";
        _methods[bareKey] = methodDef;
        // Also key by "ClassName.FuncName" so overload-resolved call sites that
        // know the originating module can fetch the correct method even when
        // another imported module has overwritten the bare-name entry above.
        _methods[qualifiedKey] = methodDef;
        Log.Debug(
            "RegisterFuncSignature: registered '{BareKey}' and '{QualifiedKey}' for function {FuncName}",
            bareKey,
            qualifiedKey,
            func.Name
        );
        if (isGeneric && func.Type is ZType.ZFuncType ft2)
        {
            _genericMethodTypes[emittedName] = ft2;
            _genericMethodTypes[$"{typeDefinition.Name}.{emittedName}"] = ft2;
        }

        return methodDef;
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

    /// <summary>
    ///     Ensures consistent stack depth at merge points when branches have different
    ///     stack effects (e.g., one branch is void/Unit, the other pushes a value).
    /// </summary>
    private static void ReconcileBranchStack(
        ZType branchType,
        bool overallIsUnit,
        CilInstructionCollection il
    )
    {
        var branchIsUnit = branchType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };
        switch (overallIsUnit)
        {
            case true when !branchIsUnit:
                il.Add(CilOpCodes.Pop);
                break;
            case false when branchIsUnit:
                il.Add(CilOpCodes.Ldnull);
                break;
        }
    }

    private static int ScoreGenericOverload(MethodInfo method, Type[] argTypes)
    {
        var score = 0;
        var methodParams = method.GetParameters();
        for (var i = 0; i < methodParams.Length && i < argTypes.Length; i++)
        {
            var paramType = methodParams[i].ParameterType;
            // Prefer array-of-generic (T[]) when arg is also an array — more specific than bare T
            if (
                paramType.IsArray
                && paramType.GetElementType()!.IsGenericParameter
                && argTypes[i].IsArray
            )
                score += 12;
            else if (paramType.IsGenericParameter)
                score += 10;
            else if (paramType == argTypes[i])
                score += 8;
            else if (paramType.IsAssignableFrom(argTypes[i]))
                score += 5;
        }

        return score;
    }

    private static Type[] InferGenericTypeArgs(
        MethodInfo genericMethod,
        Type[] argTypes,
        Type? returnType = null
    )
    {
        var genericParams = genericMethod.GetGenericArguments();
        var methodParams = genericMethod.GetParameters();
        var result = new Type[genericParams.Length];
        for (var i = 0; i < methodParams.Length && i < argTypes.Length; i++)
            MatchTypeArgs(methodParams[i].ParameterType, argTypes[i], result);
        // Also match the return type to infer type args not present in parameters
        if (returnType is not null)
            MatchTypeArgs(genericMethod.ReturnType, returnType, result);
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
                if (
                    result[pos].IsAssignableFrom(actual)
                    || (!actual.IsValueType && actual.IsAssignableFrom(result[pos]))
                )
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

        switch (formal.IsGenericType)
        {
            case true
                when actual.IsGenericType
                    && formal.GetGenericTypeDefinition() == actual.GetGenericTypeDefinition():
            {
                var formalArgs = formal.GetGenericArguments();
                var actualArgs = actual.GetGenericArguments();
                for (var j = 0; j < formalArgs.Length && j < actualArgs.Length; j++)
                    MatchTypeArgs(formalArgs[j], actualArgs[j], result);
                return;
            }
            // Interface-based matching: if formal is a generic interface, check actual's interfaces
            case true when formal.GetGenericTypeDefinition().IsInterface:
            {
                var formalDef = formal.GetGenericTypeDefinition();
                var match = actual
                    .GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType && i.GetGenericTypeDefinition() == formalDef
                    );
                if (match is not null)
                {
                    var formalArgs = formal.GetGenericArguments();
                    var matchArgs = match.GetGenericArguments();
                    for (var j = 0; j < formalArgs.Length && j < matchArgs.Length; j++)
                        MatchTypeArgs(formalArgs[j], matchArgs[j], result);
                }

                break;
            }
        }
    }

    private TypeSignature[] InferTypeArgsForCall(
        string sanitizedName,
        MethodDefinition genericMethod,
        IReadOnlyList<IrNode> args,
        EmitContext ctx,
        ZType? callReturnType = null
    )
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
                // actual arg (after varargs packing) is Clr-Array[T]. Unwrap it.
                if (
                    funcType.IsVariadic
                    && i == funcType.Params.Count - 1
                    && actualType is ZType.ZNamedType { TypeArgs: [var elemType] } named
                    && _typeAliases.IsArrayName(named.Name)
                )
                    actualType = elemType;
                MatchZTypeArgs(funcType.Params[i], actualType, freeVars, result, ctx);
            }

            // Also match the return type to infer type args not present in parameters
            if (callReturnType is not null)
                MatchZTypeArgs(funcType.Return, callReturnType, freeVars, result, ctx);

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

    private void MatchZTypeArgs(
        ZType formal,
        ZType actual,
        List<int> freeVarIds,
        TypeSignature[] result,
        EmitContext ctx
    )
    {
        switch (formal)
        {
            case ZType.ZTypeVar tv:
            {
                var idx = freeVarIds.IndexOf(tv.Id);
                if (idx >= 0 && idx < result.Length)
                    AssignGenericArgPreferringReference(result, idx, MapToClr(actual, ctx));
                return;
            }
            case ZType.ZConstrainedVar cv:
            {
                var idx = freeVarIds.IndexOf(cv.Id);
                if (idx >= 0 && idx < result.Length)
                    AssignGenericArgPreferringReference(result, idx, MapToClr(actual, ctx));
                return;
            }
            case ZType.ZNamedType fn when actual is ZType.ZNamedType an && fn.Name == an.Name:
            {
                for (var i = 0; i < fn.TypeArgs.Count && i < an.TypeArgs.Count; i++)
                    MatchZTypeArgs(fn.TypeArgs[i], an.TypeArgs[i], freeVarIds, result, ctx);
                break;
            }
            case ZType.ZFuncType ff when actual is ZType.ZFuncType af:
            {
                for (var i = 0; i < ff.Params.Count && i < af.Params.Count; i++)
                    MatchZTypeArgs(ff.Params[i], af.Params[i], freeVarIds, result, ctx);
                MatchZTypeArgs(ff.Return, af.Return, freeVarIds, result, ctx);
                break;
            }
        }
    }

    /// <summary>
    ///     Assigns a candidate type to a generic-arg slot, preferring a previously-bound
    ///     reference type (e.g. Object) over a value type. This mirrors the widening the
    ///     Unifier performs during inference — when the same ^v gets matched by both a
    ///     Dictionary&lt;_, Object&gt; receiver and a value-type value arg, we must keep
    ///     Object so IL emission chooses the correct generic instantiation and boxes
    ///     the value-type arg.
    /// </summary>
    private void AssignGenericArgPreferringReference(
        TypeSignature[] result,
        int idx,
        TypeSignature candidate
    )
    {
        if (result[idx] is null)
        {
            result[idx] = candidate;
            return;
        }

        if (result[idx].FullName == candidate.FullName)
            return;

        var objectSig = _module.CorLibTypeFactory.Object;
        if (result[idx].FullName == objectSig.FullName)
            return; // keep Object
        if (candidate.FullName == objectSig.FullName)
        {
            result[idx] = candidate; // widen to Object
            return;
        }

        // If the existing binding is a reference type and the candidate is a value type,
        // keep the existing one (the reference type is the more general CLR type here).
        if (!result[idx].IsValueType && candidate.IsValueType)
            return;
        if (result[idx].IsValueType && !candidate.IsValueType)
        {
            result[idx] = candidate;
            return;
        }

        // Otherwise, last-write-wins (preserves existing behavior for unrelated mismatches).
        result[idx] = candidate;
    }

    private ITypeDefOrRef? ResolveConstructorCaseType(
        string caseName,
        ZType scrutineeType,
        EmitContext ctx,
        string? resolvedUnion = null
    )
    {
        if (scrutineeType is not ZType.ZNamedType named)
            return null;
        // Prefer the union PatternResolver resolved — it applies the case→union fallback the
        // scrutinee's own type name alone would miss (e.g. a bare type-variable scrutinee).
        // Falls back to the scrutinee name, so well-typed union matches are unchanged.
        var caseKey = $"{resolvedUnion ?? named.Name}.{caseName}";
        if (!_unionCaseTypes.TryGetValue(caseKey, out var caseType))
            return null;
        Log.Debug(
            "ResolveConstructorCaseType: caseKey={CaseKey}, caseType={CaseType}, typeArgs={TypeArgs}",
            caseKey,
            caseType.GetType().Name,
            named.TypeArgs.Count
        );
        if (named.TypeArgs.Count <= 0)
            return caseType;
        var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta, ctx)).ToArray();
        if (caseType is TypeDefinition { GenericParameters.Count: > 0 } td)
            return td.MakeGenericInstanceType(td.IsValueType, typeArgs).ToTypeDefOrRef();
        // Imported type reference (precompiled) — create generic instance
        return new GenericInstanceTypeSignature(caseType, false, typeArgs).ToTypeDefOrRef();
    }

    private static MethodInfo? FindMethodWithOutParams(
        Type type,
        string methodName,
        IReadOnlyList<IrNode> visibleArgs,
        IReadOnlyList<ClrInterop.OutParamInfo> outParams,
        BindingFlags flags
    )
    {
        var totalParams = visibleArgs.Count + outParams.Count;
        var outIndexSet = new HashSet<int>(outParams.Select(op => op.OriginalIndex));

        // Interfaces don't surface base-interface members via reflection, so include
        // the inherited methods (e.g. IDictionary<,>.TryGetValue on IHeaderDictionary).
        var candidates = type.GetMethods(flags).AsEnumerable();
        if (type.IsInterface)
            candidates = candidates.Concat(
                type.GetInterfaces().SelectMany(i => i.GetMethods(flags))
            );

        return candidates
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

    private static ZType? GetVarType(
        string name,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        foreach (var t in outerParams)
            if (t.Name == name)
                return t.Type;

        return null;
    }

    private static HashSet<string> FindFreeVars(IrNode node, HashSet<string> bound)
    {
        return node switch
        {
            IrNode.Var v => bound.Contains(v.Name) ? [] : [v.Name],
            IrNode.Let let => Merge(
                FindFreeVars(let.Value, bound),
                FindFreeVars(let.Body, [.. bound, let.VarName])
            ),
            IrNode.Use use => Merge(
                FindFreeVars(use.Value, bound),
                FindFreeVars(use.Body, [.. bound, use.VarName])
            ),
            IrNode.If @if => Merge(
                FindFreeVars(@if.Condition, bound),
                Merge(FindFreeVars(@if.Then, bound), FindFreeVars(@if.Else, bound))
            ),
            IrNode.Call call => Merge(
                FindFreeVars(call.Function, bound),
                call.Args.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a, bound))
                )
            ),
            IrNode.BinOp binop => Merge(
                FindFreeVars(binop.Left, bound),
                FindFreeVars(binop.Right, bound)
            ),
            IrNode.UnaryOp unary => FindFreeVars(unary.Operand, bound),
            IrNode.FuncDef func => FindFreeVars(
                func.Body,
                [.. bound.Concat(func.Params.Select(p => p.Name))]
            ),
            IrNode.Match match => Merge(
                FindFreeVars(match.Scrutinee, bound),
                match.Arms.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a.Body, bound))
                )
            ),
            IrNode.MethodCall mc => Merge(
                FindFreeVars(mc.Receiver, bound),
                mc.Args.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a, bound))
                )
            ),
            IrNode.UnionCaseNew ucn => ucn.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.ClrNew cn => cn.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.ClrCall cc => cc.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.TupleNew tn => tn.Elements.Aggregate(
                new HashSet<string>(),
                (acc, e) => Merge(acc, FindFreeVars(e, bound))
            ),
            IrNode.RecordNew rn => rn.Fields.Aggregate(
                new HashSet<string>(),
                (acc, f) => Merge(acc, FindFreeVars(f.Value, bound))
            ),
            IrNode.RecordWith rw => Merge(
                FindFreeVars(rw.Record, bound),
                rw.Updates.Aggregate(
                    new HashSet<string>(),
                    (acc, u) => Merge(acc, FindFreeVars(u.Value, bound))
                )
            ),
            IrNode.MutableArrayNew man => man.Elements.Aggregate(
                new HashSet<string>(),
                (acc, e) => Merge(acc, FindFreeVars(e, bound))
            ),
            IrNode.Seq seq => seq.Nodes.Aggregate(
                new HashSet<string>(),
                (acc, n) => Merge(acc, FindFreeVars(n, bound))
            ),
            IrNode.Throw th => FindFreeVars(th.Expr, bound),
            IrNode.WithHandlers wh => Merge(
                FindFreeVars(wh.Body, bound),
                wh.Handlers.Aggregate(
                    new HashSet<string>(),
                    (acc, h) =>
                        Merge(acc, FindFreeVars(h.HandlerBody, [.. bound, h.BindingVarName]))
                )
            ),
            IrNode.Await aw => FindFreeVars(aw.Expr, bound),
            IrNode.SetField sf => FindFreeVars(sf.Value, bound),
            IrNode.FieldGet fg => FindFreeVars(fg.Record, bound),
            IrNode.SuperMethodCall smc => smc.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.TcoJump tj => tj.NewArgs.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.Closure cl => cl.CapturedValues.Aggregate(
                new HashSet<string>(),
                (acc, v) => Merge(acc, FindFreeVars(v, bound))
            ),
            _ => [],
        };
    }

    private static HashSet<string> Merge(HashSet<string> a, HashSet<string> b)
    {
        var result = new HashSet<string>(a);
        result.UnionWith(b);
        return result;
    }

    /// <summary>
    ///     Returns true if <paramref name="node" /> contains a <see cref="IrNode.SetField" />
    ///     that writes to any class field name in <paramref name="classFields" />. Var reads
    ///     are not scanned here — FindFreeVars already surfaces those as free vars of the
    ///     enclosing lambda, where they're checked against class fields directly.
    /// </summary>
    private static bool BodyContainsClassFieldSet(
        IrNode node,
        IReadOnlyDictionary<string, FieldDefinition> classFields
    )
    {
        return node switch
        {
            IrNode.SetField sf => classFields.ContainsKey(sf.FieldName)
                || BodyContainsClassFieldSet(sf.Value, classFields),
            IrNode.Let let => BodyContainsClassFieldSet(let.Value, classFields)
                || BodyContainsClassFieldSet(let.Body, classFields),
            IrNode.Use use => BodyContainsClassFieldSet(use.Value, classFields)
                || BodyContainsClassFieldSet(use.Body, classFields),
            IrNode.FuncDef func => BodyContainsClassFieldSet(func.Body, classFields),
            IrNode.WithHandlers wh => BodyContainsClassFieldSet(wh.Body, classFields)
                || wh.Handlers.Any(h => BodyContainsClassFieldSet(h.HandlerBody, classFields)),
            IrNode.Match match => BodyContainsClassFieldSet(match.Scrutinee, classFields)
                || match.Arms.Any(a => BodyContainsClassFieldSet(a.Body, classFields)),
            IrNode.If @if => BodyContainsClassFieldSet(@if.Condition, classFields)
                || BodyContainsClassFieldSet(@if.Then, classFields)
                || BodyContainsClassFieldSet(@if.Else, classFields),
            IrNode.Call call => BodyContainsClassFieldSet(call.Function, classFields)
                || call.Args.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.BinOp bin => BodyContainsClassFieldSet(bin.Left, classFields)
                || BodyContainsClassFieldSet(bin.Right, classFields),
            IrNode.UnaryOp un => BodyContainsClassFieldSet(un.Operand, classFields),
            IrNode.MethodCall mc => BodyContainsClassFieldSet(mc.Receiver, classFields)
                || mc.Args.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.ClrCall cc => cc.Args.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.ClrNew cn => cn.Args.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.UnionCaseNew ucn => ucn.Args.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.TupleNew tn => tn.Elements.Any(e => BodyContainsClassFieldSet(e, classFields)),
            IrNode.RecordNew rn => rn.Fields.Any(f =>
                BodyContainsClassFieldSet(f.Value, classFields)
            ),
            IrNode.RecordWith rw => BodyContainsClassFieldSet(rw.Record, classFields)
                || rw.Updates.Any(u => BodyContainsClassFieldSet(u.Value, classFields)),
            IrNode.MutableArrayNew man => man.Elements.Any(e =>
                BodyContainsClassFieldSet(e, classFields)
            ),
            IrNode.Seq seq => seq.Nodes.Any(n => BodyContainsClassFieldSet(n, classFields)),
            IrNode.Throw th => BodyContainsClassFieldSet(th.Expr, classFields),
            IrNode.Await aw => BodyContainsClassFieldSet(aw.Expr, classFields),
            IrNode.FieldGet fg => BodyContainsClassFieldSet(fg.Record, classFields),
            IrNode.SuperMethodCall smc => smc.Args.Any(a =>
                BodyContainsClassFieldSet(a, classFields)
            ),
            IrNode.TcoJump tj => tj.NewArgs.Any(a => BodyContainsClassFieldSet(a, classFields)),
            IrNode.Closure cl => cl.CapturedValues.Any(v =>
                BodyContainsClassFieldSet(v, classFields)
            ),
            _ => false,
        };
    }

    /// <summary>
    ///     Emits the enclosing class instance onto the stack. Normally <c>ldarg.0</c>
    ///     (the current instance method's <c>this</c>), but redirected to the saved
    ///     local when we're inside a lambda that captured the class instance, or to
    ///     <c>this.__this</c> when inside an async state machine MoveNext.
    /// </summary>
    private void EmitLoadClassThis(CilInstructionCollection il, EmitContext ctx)
    {
        if (ctx.CurrentClassThisLocal is { } thisLocal)
        {
            il.Add(CilOpCodes.Ldloc, thisLocal);
            return;
        }

        if (ctx.MoveNextCtx?.ThisField is { } thisF)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, thisF);
            return;
        }

        il.Add(CilOpCodes.Ldarg_0);
    }

    /// <summary>
    ///     Imports a delegate constructor with the correct AsmResolver generic type.
    /// </summary>
    private IMethodDefOrRef ImportDelegateConstructor(ZType funcType, EmitContext ctx)
    {
        var clrDelegateType = MapToReflectionClr(funcType);
        var ctorInfo = clrDelegateType.GetConstructors()[0];
        var asmDelegateType = MapToClr(funcType, ctx);
        if (asmDelegateType is GenericInstanceTypeSignature git)
            return new MemberReference(
                git.ToTypeDefOrRef(),
                ".ctor",
                MethodSignature.CreateInstance(
                    _module.CorLibTypeFactory.Void,
                    [_module.CorLibTypeFactory.Object, _module.CorLibTypeFactory.IntPtr]
                )
            );

        return (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(ctorInfo);
    }

    /// <summary>
    ///     Imports a reflection MethodInfo, fixing up the declaring type to use the correct
    ///     AsmResolver generic instance when the receiver type has generic parameters.
    /// </summary>
    private IMethodDefOrRef ImportMethodWithGenericDeclaringType(
        MethodInfo method,
        ZType receiverType,
        EmitContext ctx
    )
    {
        var asmReceiverType = MapToClr(receiverType, ctx);
        if (asmReceiverType is not GenericInstanceTypeSignature git)
            return (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(method);

        // AsmResolver's DefaultImporter doesn't normalize method signatures on generic types
        // to use generic parameter references (!0, !1). Unlike Cecil, it preserves concrete types.
        // We must construct the signature manually, replacing declaring type's generic parameters
        // with GenericParameterSignature references.
        var declaringType = method.DeclaringType;
        var openDeclaringType = declaringType is { IsGenericType: true }
            ? declaringType.IsGenericTypeDefinition
                ? declaringType
                : declaringType.GetGenericTypeDefinition()
            : null;

        // Get the method on the open type to see the un-instantiated parameter types
        var openMethod = method;
        if (declaringType is { IsGenericType: true, IsGenericTypeDefinition: false })
            openMethod = (MethodInfo)
                MethodBase.GetMethodFromHandle(method.MethodHandle, openDeclaringType!.TypeHandle)!;

        var retType = MapTypeWithGenericParams(openMethod.ReturnType);
        var paramTypes = openMethod
            .GetParameters()
            .Select(p => MapTypeWithGenericParams(p.ParameterType))
            .ToArray();

        return new MemberReference(
            git.ToTypeDefOrRef(),
            method.Name,
            method.IsStatic
                ? MethodSignature.CreateStatic(retType, paramTypes)
                : MethodSignature.CreateInstance(retType, paramTypes)
        );

        // Map CLR types to AsmResolver TypeSignatures, replacing generic type params with !N
        TypeSignature MapTypeWithGenericParams(Type t)
        {
            if (t.IsGenericParameter && t.DeclaringType == openDeclaringType)
                return new GenericParameterSignature(
                    _module,
                    GenericParameterType.Type,
                    t.GenericParameterPosition
                );
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Any(a => a.IsGenericParameter || a.ContainsGenericParameters))
                {
                    var mappedArgs = args.Select(MapTypeWithGenericParams).ToArray();
                    var openClrType = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
                    var imported = _module.DefaultImporter.ImportType(openClrType);
                    return imported
                        .ToTypeSignature(openClrType.IsValueType)
                        .MakeGenericInstanceType(openClrType.IsValueType, mappedArgs);
                }
            }

            if (t.IsArray)
                return MapTypeWithGenericParams(t.GetElementType()!).MakeSzArrayType();
            if (t.IsByRef)
                return MapTypeWithGenericParams(t.GetElementType()!).MakeByReferenceType();
            return _module.DefaultImporter.ImportType(t).ToTypeSignature(t.IsValueType);
        }
    }

    /// <summary>
    ///     Imports a property/indexer accessor that was resolved by walking base interfaces
    ///     (so its declaring type may differ from the call's receiver type). When the accessor
    ///     lives on a closed generic interface — e.g. <c>get_Count</c> on
    ///     <c>ICollection&lt;ServiceDescriptor&gt;</c>, inherited by <c>IServiceCollection</c> —
    ///     it must be imported against that closed-generic declaring type via
    ///     <see cref="ImportClosedGenericMethod" />; a plain DefaultImporter.ImportMethod would
    ///     strip the type-arg substitution and emit a malformed reference. For an accessor on a
    ///     non-generic base interface a plain import is correct.
    /// </summary>
    private IMethodDefOrRef ImportAccessor(MethodInfo accessor) =>
        accessor.DeclaringType is { IsGenericType: true }
            ? ImportClosedGenericMethod(accessor.DeclaringType, accessor.Name)
            : (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(accessor);

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

    private static ConstructorInfo? FindAttributeConstructor(
        Type attrType,
        IReadOnlyList<object> positionalArgs
    )
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

    private List<IrField> GetAsmInheritedFields(string otherClassName)
    {
        var result = new List<IrField>();
        if (_asmClassInfos.TryGetValue(otherClassName, out var info))
        {
            if (info.BaseClassName is not null)
                result.AddRange(GetAsmInheritedFields(info.BaseClassName));
            result.AddRange(info.Fields);
        }

        return result;
    }

    private HashSet<string> GetAsmInheritedMethodNames(string otherClassName)
    {
        var result = new HashSet<string>();
        if (_asmClassInfos.TryGetValue(otherClassName, out var info))
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
            foreach (var ns in ClrUsings)
            {
                clrType = _clrInterop.FindType(ns + "." + ifaceName);
                if (clrType is not null)
                    break;
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
        if (
            !_userTypes.TryGetValue(ifaceName, out var userType)
            || userType is not TypeDefinition typeDef
        )
            return;

        foreach (var method in typeDef.Methods)
            if (method.Name is not null)
                names.Add(method.Name.ToString());
    }

    private void AddAsmInheritedFieldsToMap(
        TypeDefinition baseType,
        Dictionary<string, FieldDefinition> map
    )
    {
        var info = _asmClassInfos.Values.FirstOrDefault(i => i.TypeDef == baseType);
        if (info is null)
            return;

        // Map original field names to their backing fields
        foreach (var irField in info.Fields)
        {
            var sanitizedName = Sanitize(irField.Name);
            var backingField = baseType.Fields.FirstOrDefault(f =>
                f.Name?.ToString() == $"<{sanitizedName}>k__BackingField"
            );
            if (backingField is not null)
                map.TryAdd(irField.Name, backingField);
        }

        if (
            info.BaseClassName is not null
            && _asmClassInfos.TryGetValue(info.BaseClassName, out var parentInfo)
        )
            AddAsmInheritedFieldsToMap(parentInfo.TypeDef, map);
    }

    /// <summary>
    ///     Recursively resolve generic parameter signatures to concrete types from type args.
    ///     Handles simple cases like <c>!0</c> and nested cases like <c>SList&lt;!0&gt;</c>.
    /// </summary>
    private static TypeSignature ResolveGenericParam(
        TypeSignature sig,
        IList<TypeSignature> typeArgs
    )
    {
        switch (sig)
        {
            case GenericParameterSignature gps when gps.Index < typeArgs.Count:
                return typeArgs[gps.Index];
            case GenericInstanceTypeSignature nested:
            {
                var resolvedArgs = new TypeSignature[nested.TypeArguments.Count];
                var changed = false;
                for (var i = 0; i < nested.TypeArguments.Count; i++)
                {
                    resolvedArgs[i] = ResolveGenericParam(nested.TypeArguments[i], typeArgs);
                    if (resolvedArgs[i] != nested.TypeArguments[i])
                        changed = true;
                }

                return changed
                    ? new GenericInstanceTypeSignature(
                        nested.GenericType,
                        nested.IsValueType,
                        resolvedArgs
                    )
                    : sig;
            }
            default:
                return sig;
        }
    }

    private void RegisterUserType(
        string name,
        ITypeDefOrRef typeRef,
        bool isValueType = false,
        Type? reflectionType = null
    )
    {
        _userTypes[name] = typeRef;
        // The bool flag distinguishes ELEMENT_TYPE_VALUETYPE (struct) from ELEMENT_TYPE_CLASS
        // in the type signature. Mismatch here causes TypeLoadException at runtime.
        var asValueType = isValueType || (typeRef is TypeDefinition td && td.IsValueType);
        _userTypeSignatures[name] = typeRef.ToTypeSignature(asValueType);
        if (reflectionType is not null)
            _userReflectionTypes[name] =
                reflectionType.IsGenericType && !reflectionType.IsGenericTypeDefinition
                    ? reflectionType.GetGenericTypeDefinition()
                    : reflectionType;
    }

    // After precompiled assemblies are imported (which registers their types under the
    // names baked into the DLL), add raw-source-name aliases for any type a precompiled
    // module renamed on a collision (see EmitNameResolver), so a consumer that references
    // it by its original source name still resolves to the imported type. Non-renamed types
    // need no aliasing — their baked name already equals the source name the consumer uses.
    private void AliasPrecompiledTypeRenames()
    {
        if (precompiledTypeRenames is not { Count: > 0 })
            return;

        // baked (emitted) name -> raw source name, for the renamed types only.
        var emitToRaw = new Dictionary<string, string>();
        foreach (var (raw, emit) in precompiledTypeRenames)
            if (raw != emit)
                emitToRaw[emit] = raw;

        if (emitToRaw.Count == 0)
            return;

        // Bare-name registries (records, classes, interfaces, union base + case types).
        foreach (var (emit, raw) in emitToRaw)
        {
            if (_userTypes.TryGetValue(emit, out var td))
                _userTypes.TryAdd(raw, td);
            if (_userTypeSignatures.TryGetValue(emit, out var sig))
                _userTypeSignatures.TryAdd(raw, sig);
            if (_userReflectionTypes.TryGetValue(emit, out var rt))
                _userReflectionTypes.TryAdd(raw, rt);
        }

        // Composite "<union>.<case>" registries: rewrite each name through emitToRaw.
        AliasCompositeKeys(_unionCaseTypes, emitToRaw);
        AliasCompositeKeys(_unionCaseGetters, emitToRaw);
        AliasCompositeKeys(_unionCasePropertyNames, emitToRaw);
    }

    private static void AliasCompositeKeys<TValue>(
        Dictionary<string, TValue> map,
        IReadOnlyDictionary<string, string> emitToRaw
    )
    {
        foreach (var (key, value) in map.ToList())
        {
            var dot = key.IndexOf('.');
            if (dot < 0)
                continue;
            var left = key[..dot];
            var right = key[(dot + 1)..];
            var rawLeft = emitToRaw.GetValueOrDefault(left, left);
            var rawRight = emitToRaw.GetValueOrDefault(right, right);
            if (rawLeft == left && rawRight == right)
                continue; // neither component renamed
            map.TryAdd($"{rawLeft}.{rawRight}", value);
        }
    }

    internal static string Sanitize(string name)
    {
        return NameConverter.SanitizeIdentifier(name);
    }

    // The emitted member name for a module-level definition/reference: the
    // disambiguated EmitName stamped by EmitNameResolver when the sanitized name
    // collided, else the plain sanitization of the original name. (IL needs no
    // keyword escaping, unlike the C# backend.)
    private static string Emitted(string? emitName, string rawName)
    {
        return emitName ?? Sanitize(rawName);
    }

    private static string SanitizeParam(string name)
    {
        return NameConverter.SanitizeParameter(name);
    }

    // ─── Async State Machine Generation ───────────────────────────────────

    /// <summary>
    ///     Imports a method from a closed generic declaring type, correctly substituting
    ///     the type's generic parameters into the method signature. Works around AsmResolver's
    ///     DefaultImporter.ImportMethod which strips type-arg substitution for static methods
    ///     and property accessors on closed generic instances, producing malformed references
    ///     like 'AsyncTaskMethodBuilder`1 AsyncTaskMethodBuilder`1&lt;Bool&gt;::Create()' with
    ///     an open-generic return type. Instead, we import the method from the open generic
    ///     definition and construct a MemberReference on the closed instance.
    /// </summary>
    internal IMethodDefOrRef ImportClosedGenericMethod(Type closedGenericType, string methodName)
    {
        var openGenericType = closedGenericType.GetGenericTypeDefinition();
        // Prefer the method on the OPEN generic so we can translate signatures through
        // the open type's generic parameters. Property getters (`get_Task`) aren't
        // returned by GetMethods() under the property name, so we also probe properties.
        var openMethod =
            openGenericType.GetMethods().FirstOrDefault(m => m.Name == methodName)
            ?? openGenericType.GetProperty(methodName)?.GetGetMethod()
            ?? throw new InvalidOperationException(
                $"Method '{methodName}' not found on {openGenericType}"
            );

        // Build a signature using GenericParameterSignature(Type, i) for the declaring-
        // type's generic params (so `!0`/`!1` references survive serialization). The
        // declaring type side of the MemberReference is a closed instance, which closes
        // those `!i` references at each call site.
        var returnTypeSig = TranslateOpenGenericTypeToSignature(openMethod.ReturnType);
        var paramSigs = openMethod
            .GetParameters()
            .Select(p => TranslateOpenGenericTypeToSignature(p.ParameterType))
            .ToArray();

        var sig = openMethod.IsStatic
            ? MethodSignature.CreateStatic(returnTypeSig, paramSigs)
            : MethodSignature.CreateInstance(returnTypeSig, paramSigs);

        if (openMethod.IsGenericMethodDefinition)
        {
            sig.GenericParameterCount = openMethod.GetGenericArguments().Length;
            // Mark the signature as generic so the serialized IL uses `GENERIC` calling
            // convention. Without this, ilverify/CLR resolves the reference as a
            // non-generic method and fails to find its closed form.
            sig.Attributes |= CallingConventionAttributes.Generic;
        }

        // Build the closed declaring-type signature.
        var closedTypeArgs = closedGenericType
            .GetGenericArguments()
            .Select(a => _module.DefaultImporter.ImportType(a).ToTypeSignature(a.IsValueType))
            .ToArray();
        var closedDeclaringSig = new GenericInstanceTypeSignature(
            _module.DefaultImporter.ImportType(openGenericType),
            openGenericType.IsValueType,
            closedTypeArgs
        );

        // Use the reflected method's actual name (may be `get_<Prop>` for property accessors
        // even if the caller passed the property name).
        return new MemberReference(closedDeclaringSig.ToTypeDefOrRef(), openMethod.Name, sig);
    }

    /// <summary>
    ///     Translates a reflection Type that may contain the open generic type's
    ///     parameters (e.g. the `TResult` on `AsyncTaskMethodBuilder&lt;TResult&gt;`) to a
    ///     signature whose `!0`/`!1` references are unresolved, ready to be closed when
    ///     the surrounding MemberReference's declaring-type instance substitutes them.
    /// </summary>
    private TypeSignature TranslateOpenGenericTypeToSignature(Type clrType)
    {
        if (clrType == typeof(void))
            return _module.CorLibTypeFactory.Void;

        if (clrType.IsGenericParameter)
            return new GenericParameterSignature(
                _module,
                clrType.DeclaringMethod is not null
                    ? GenericParameterType.Method
                    : GenericParameterType.Type,
                clrType.GenericParameterPosition
            );

        if (clrType.IsByRef)
            return TranslateOpenGenericTypeToSignature(clrType.GetElementType()!)
                .MakeByReferenceType();

        if (clrType.IsArray)
            return TranslateOpenGenericTypeToSignature(clrType.GetElementType()!).MakeSzArrayType();

        // When a method on the open generic returns its own declaring type (e.g.
        // `Create()` on `AsyncTaskMethodBuilder<TResult>` returning
        // `AsyncTaskMethodBuilder<TResult>`), reflection reports `ReturnType` as the
        // *type definition* (`IsGenericTypeDefinition == true`) rather than a
        // constructed instance — so `IsConstructedGenericType` is false. Still treat
        // it as a generic instance closed over the type's own generic parameters so
        // the serialized signature says `AsyncTaskMethodBuilder`1<!0>` rather than
        // the bare open definition.
        if (clrType.IsGenericType)
        {
            var openDef = clrType.IsGenericTypeDefinition
                ? clrType
                : clrType.GetGenericTypeDefinition();
            var args = clrType
                .GetGenericArguments()
                .Select(TranslateOpenGenericTypeToSignature)
                .ToArray();
            return new GenericInstanceTypeSignature(
                _module.DefaultImporter.ImportType(openDef),
                openDef.IsValueType,
                args
            );
        }

        return _module.DefaultImporter.ImportType(clrType).ToTypeSignature(clrType.IsValueType);
    }

    private sealed record AsmClassInfo(
        TypeDefinition TypeDef,
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames
    );

    internal sealed class AsyncMoveNextContext
    {
        public required List<(string Name, CilLocalVariable Local)> AllLocals; // all locals to save/restore
        public required Dictionary<int, FieldDefinition> AwaiterFields; // state number -> awaiter field

        // Per-await enclosing with-handlers chain (outermost first).
        // Used by cascading dispatch: an await whose chain is non-empty has its
        // resume label emitted *inside* one or more nested try regions, so the
        // outer dispatch must route via trampolines rather than branching
        // directly into the protected region (CIL forbids branch-into-try).
        // Each enclosing try-region node is an IrNode.WithHandlers (try/catch) or an
        // IrNode.Use (try/finally); both place a trampoline before their TryStart.
        public IReadOnlyList<IReadOnlyList<IrNode>>? AwaitTryChains;
        public required FieldDefinition BuilderField;
        public CilInstructionLabel? ExitLabel; // label after try/catch for suspension return
        public required bool IsVoidReturn;
        public int NextAwaitState;
        public CilInstructionLabel[]? ResumeLabels;
        public required TypeDefinition SmType;
        public required FieldDefinition StateField;
        public required CilLocalVariable StateLocal;
        public FieldDefinition? ThisField; // __this field for instance method async state machines

        // Trampoline label placed in the parent scope, immediately before each
        // try region's (with-handlers or use) TryStart. Outer dispatch jumps here;
        // execution then falls through into the inner try, where another dispatch
        // routes to the actual resume label.
        public Dictionary<IrNode, CilInstructionLabel>? TrampolineLabels;
        public required Dictionary<string, FieldDefinition> VarFields; // params + locals -> fields
    }

    // Immutable snapshot of the emission context ("what am I emitting right now"),
    // threaded explicitly through EmitNode and its helpers as a parameter. Each nested
    // emission derives a child snapshot with `ctx with { ... }` and passes it down, so
    // context is never ambient mutable state — a whole class of "works alone, breaks when
    // nested" bugs becomes structurally impossible.
    //
    // Monotonic name generators (`_asyncSmCounter`, `_lambdaId`) deliberately stay out of
    // this record: they are emitter-wide counters, not per-emission context.
    internal sealed record EmitContext
    {
        // class / type context
        public TypeDefinition? CurrentTypeDefinition { get; init; }
        public TypeDefinition? CurrentBaseTypeDefinition { get; init; }
        public Dictionary<string, FieldDefinition>? CurrentClassFields { get; init; }
        public Dictionary<string, MethodDefinition>? CurrentClassMethods { get; init; }

        // When a lambda inside a class instance method references class fields, the lambda
        // is emitted as a closure method where ldarg.0 is the closure — not the enclosing
        // class's `this`. In that case we capture `this` into a closure field and load it
        // into a local at method entry. Setting this redirects class-field emission from
        // `ldarg.0; ldfld` to `ldloc thisLocal; ldfld`.
        public CilLocalVariable? CurrentClassThisLocal { get; init; }

        // function context
        public int InstanceArgOffset { get; init; } // 0 = static, 1 = instance

        // generic context
        public Dictionary<string, TypeSignature>? CurrentTypeParamMap { get; init; }
        public Dictionary<int, TypeSignature>? CurrentTypeVarMap { get; init; }

        // async state-machine context
        public AsyncMoveNextContext? MoveNextCtx { get; init; }

        public static readonly EmitContext Empty = new();
    }
}

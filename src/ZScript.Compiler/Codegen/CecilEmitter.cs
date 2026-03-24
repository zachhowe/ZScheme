namespace ZScript.Compiler.Codegen;

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

/// <summary>
/// Emits .NET IL using Mono.Cecil.
/// </summary>
public sealed class CecilEmitter(
    string assemblyName,
    DiagnosticBag diagnostics,
    string className = "Program",
    IReadOnlyList<string>? clrUsings = null,
    IReadOnlyList<string>? assemblySearchPaths = null,
    IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null,
    IReadOnlyList<string>? precompiledAssemblyPaths = null,
    string? ilNamespace = null)
{
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];
    private readonly string _ilNamespace = ilNamespace ?? assemblyName;
    private readonly ClrInterop _clrInterop = new(diagnostics, assemblySearchPaths);

    private ModuleDefinition _module = null!;

    private readonly Dictionary<string, MethodDefinition> _methods = new();
    private readonly Dictionary<string, MethodReference> _precompiledMethods = new();
    private readonly Dictionary<string, System.Reflection.MethodInfo> _precompiledReflectionMethods = new();
    private readonly Dictionary<string, TypeReference> _userTypes = new();
    private readonly Dictionary<string, TypeReference> _unionCaseTypes = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _unionCasePropertyNames = new();
    private readonly Dictionary<string, MethodReference> _unionCaseGetters = new();
    private readonly Dictionary<string, FieldDefinition> _staticFields = new();
    private readonly Dictionary<string, ZType.ZFuncType> _genericMethodTypes = new();
    private TypeDefinition? _currentTypeDefinition;
    private ZType? _currentFuncReturnType;
    private int _instanceArgOffset;
    private Dictionary<string, FieldDefinition>? _currentClassFields;
    private int _lambdaId;
    private int _asyncSmCounter;
    private Dictionary<int, TypeReference>? _currentTypeVarMap;
    private Dictionary<string, TypeReference>? _currentTypeParamMap;

    // When non-null, we are emitting inside a MoveNext method and variable access
    // is redirected to state machine fields.
    private AsyncMoveNextContext? _moveNextCtx;

    private sealed class AsyncMoveNextContext
    {
        public required TypeDefinition SmType;
        public required FieldDefinition StateField;
        public required FieldDefinition BuilderField;
        public required VariableDefinition StateLocal;
        public required Dictionary<string, FieldDefinition> VarFields; // params + locals -> fields
        public required Dictionary<int, FieldDefinition> AwaiterFields; // state number -> awaiter field
        public required List<(string Name, VariableDefinition Local)> AllLocals; // all locals to save/restore
        public required bool IsVoidReturn;
        public int NextAwaitState;
        public Instruction[]? ResumeLabels;
        public Instruction? ExitLabel; // label after try/catch for suspension return
    }

    private TypeReference MapToClr(ZType type, IReadOnlyDictionary<string, TypeReference>? typeParamMap = null)
        => CecilTypeMapper.MapToClr(type, _module, _userTypes, typeParamMap ?? _currentTypeParamMap, _currentTypeVarMap);

    private TypeReference MapReturnTypeToClr(ZType type)
        => CecilTypeMapper.MapReturnTypeToClr(type, _module, _userTypes, _currentTypeParamMap, _currentTypeVarMap);

    public byte[]? Emit(IrNode node)
    {
        var asmName = new AssemblyNameDefinition(assemblyName, new Version(1, 0, 0, 0));
        var assemblyDef = AssemblyDefinition.CreateAssembly(asmName, assemblyName,
            ModuleKind.Dll);
        _module = assemblyDef.MainModule;

        var typeAttrs = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed;
        var typeDef = new TypeDefinition(_ilNamespace, className, typeAttrs, _module.TypeSystem.Object);
        _module.Types.Add(typeDef);
        _currentTypeDefinition = typeDef;

        var mainStatements = new List<IrNode>();

        // Load precompiled assemblies and register their types/methods
        if (precompiledAssemblyPaths is { Count: > 0 })
        {
            // Register assembly directories so Cecil can resolve cross-assembly references
            if (_module.AssemblyResolver is DefaultAssemblyResolver resolver)
            {
                foreach (var path in precompiledAssemblyPaths)
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (dir is not null)
                        resolver.AddSearchDirectory(dir);
                }
            }

            foreach (var path in precompiledAssemblyPaths)
                LoadPrecompiledAssembly(path);
        }

        // Pass 0: define types and functions from imported modules
        if (importedModules is { Count: > 0 })
        {
            foreach (var (_, defs) in importedModules)
            {
                foreach (var def in defs)
                {
                    if (def is IrNode.RecordDecl or IrNode.UnionDecl)
                        DefineTypeDecl(def);
                }
            }

            foreach (var (moduleClassName, defs) in importedModules)
            {
                var moduleType = new TypeDefinition(_ilNamespace, moduleClassName,
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                    _module.TypeSystem.Object);
                _module.Types.Add(moduleType);

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
                    DefineTypeDecl(child);
            }

            // Second pass: define static fields for top-level Let bindings
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.Let let)
                {
                    var fieldType = MapToClr(let.Value.Type);
                    var fd = new FieldDefinition(let.VarName, FieldAttributes.Public | FieldAttributes.Static, fieldType);
                    typeDef.Fields.Add(fd);
                    _staticFields[let.VarName] = fd;
                }
            }

            // Third pass: emit functions and class declarations
            MethodDefinition? userMainMethod = null;
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.FuncDef func)
                {
                    EmitFuncDef(func, typeDef);
                    if (func.Name == "main")
                        userMainMethod = _methods["main"];
                }
                else if (child is IrNode.ClassDecl classDecl)
                {
                    EmitClassDecl(classDecl);
                }
            }

            // Fourth pass: collect top-level statements
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
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                _module.TypeSystem.Void);
            typeDef.Methods.Add(cctor);

            var il = cctor.Body.GetILProcessor();
            var locals = new Dictionary<string, VariableDefinition>();

            foreach (var stmt in mainStatements)
            {
                if (stmt is IrNode.Let let)
                {
                    EmitNode(let.Value, il, [], locals);
                    il.Append(il.Create(OpCodes.Stsfld, _staticFields[let.VarName]));
                    var local = new VariableDefinition(MapToClr(let.Value.Type));
                    cctor.Body.Variables.Add(local);
                    il.Append(il.Create(OpCodes.Ldsfld, _staticFields[let.VarName]));
                    il.Append(il.Create(OpCodes.Stloc, local));
                    locals[let.VarName] = local;
                    if (let.Body is not IrNode.UnitConst)
                    {
                        EmitNode(let.Body, il, [], locals);
                        if (let.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                            il.Append(il.Create(OpCodes.Pop));
                    }
                }
                else
                {
                    EmitNode(stmt, il, [], locals);
                    if (stmt.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Append(il.Create(OpCodes.Pop));
                }
            }
            il.Append(il.Create(OpCodes.Ret));
        }

        // Emit Main(string[] args) wrapper
        if (node is IrNode.Seq seq2)
        {
            MethodDefinition? userMain = null;
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
                var mainMethod = new MethodDefinition("Main",
                    MethodAttributes.Public | MethodAttributes.Static,
                    _module.TypeSystem.Int32);
                mainMethod.Parameters.Add(new ParameterDefinition("args",
                    Mono.Cecil.ParameterAttributes.None, new ArrayType(_module.TypeSystem.String)));
                typeDef.Methods.Add(mainMethod);

                var mainIl = mainMethod.Body.GetILProcessor();

                // ImmutableList.Create<string>(args)
                var createMethod = typeof(ImmutableList).GetMethods()
                    .First(m => m.Name == "Create"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters() is [{ ParameterType.IsArray: true }])
                    .MakeGenericMethod(typeof(string));
                mainIl.Append(mainIl.Create(OpCodes.Ldarg_0));
                mainIl.Append(mainIl.Create(OpCodes.Call, _module.ImportReference(createMethod)));
                mainIl.Append(mainIl.Create(OpCodes.Call, userMain));
                mainIl.Append(mainIl.Create(OpCodes.Ret));

                HasEntryPoint = true;
                assemblyDef.EntryPoint = mainMethod;
                _module.Kind = ModuleKind.Console;
            }
        }

        if (diagnostics.HasErrors)
            return null;

        using var ms = new MemoryStream();
        assemblyDef.Write(ms);
        return ms.ToArray();
    }

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
                SourceSpan.None);
            return;
        }

        var abstractBases = new Dictionary<Type, string>();
        foreach (var type in asm.GetExportedTypes())
        {
            if (type.IsAbstract && type.IsSealed) // static class
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    _precompiledMethods[method.Name] = _module.ImportReference(method);
                    _precompiledReflectionMethods[method.Name] = method;
                }
            }

            if (type.IsAbstract && !type.IsSealed && !type.IsInterface)
            {
                _userTypes[type.Name] = _module.ImportReference(type);
                abstractBases[type] = type.Name;
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                var caseKey = $"{type.Name}.{nested.Name}";
                _unionCaseTypes[caseKey] = _module.ImportReference(nested);
                _userTypes[nested.Name] = _module.ImportReference(nested);

                foreach (var prop in nested.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var getter = prop.GetGetMethod();
                    if (getter is not null)
                        _unionCaseGetters[$"{type.Name}.{nested.Name}.{prop.Name}"] = _module.ImportReference(getter);
                }

                var propNames = nested.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name).ToList();
                if (propNames.Count > 0)
                    _unionCasePropertyNames[caseKey] = propNames;
            }

            if (!type.IsAbstract && !type.IsNested && type.GetMethod("<Clone>$") is not null)
            {
                _userTypes[type.Name] = _module.ImportReference(type);
            }
        }

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
                    _unionCaseTypes[caseKey] = _module.ImportReference(type);
                    _userTypes[type.Name] = _module.ImportReference(type);

                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var getter = prop.GetGetMethod();
                        if (getter is not null)
                            _unionCaseGetters[$"{baseName}.{type.Name}.{prop.Name}"] = _module.ImportReference(getter);
                    }

                    var propNames = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => p.Name).ToList();
                    if (propNames.Count > 0)
                        _unionCasePropertyNames[caseKey] = propNames;
                }
            }
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

    private void DefineTypeDecl(IrNode node)
    {
        switch (node)
        {
            case IrNode.RecordDecl record:
                DefineRecordType(record);
                break;
            case IrNode.UnionDecl union:
                DefineUnionType(union);
                break;
        }
    }

    private void DefineRecordType(IrNode.RecordDecl record)
    {
        var typeDef = new TypeDefinition(_ilNamespace, record.Name,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            _module.TypeSystem.Object);
        _module.Types.Add(typeDef);
        _userTypes[record.Name] = typeDef;

        Dictionary<string, TypeReference>? typeParamMap = null;
        if (record.TypeParams.Count > 0)
        {
            typeParamMap = new Dictionary<string, TypeReference>();
            foreach (var tp in record.TypeParams)
            {
                var gp = new GenericParameter(tp, typeDef);
                typeDef.GenericParameters.Add(gp);
                typeParamMap[tp] = gp;
            }
        }

        var fieldDefs = new List<(FieldDefinition Field, MethodDefinition Getter)>();

        foreach (var field in record.Fields)
        {
            var fieldClrType = MapToClr(field.Type, typeParamMap);
            var fb = new FieldDefinition($"<{field.Name}>k__BackingField", FieldAttributes.Private | FieldAttributes.InitOnly, fieldClrType);
            typeDef.Fields.Add(fb);

            var getter = new MethodDefinition($"get_{field.Name}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                fieldClrType);
            typeDef.Methods.Add(getter);
            var getIl = getter.Body.GetILProcessor();
            getIl.Append(getIl.Create(OpCodes.Ldarg_0));
            getIl.Append(getIl.Create(OpCodes.Ldfld, fb));
            getIl.Append(getIl.Create(OpCodes.Ret));

            var prop = new PropertyDefinition(field.Name, Mono.Cecil.PropertyAttributes.None, fieldClrType);
            prop.GetMethod = getter;
            typeDef.Properties.Add(prop);

            fieldDefs.Add((fb, getter));
        }

        // Constructor
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _module.TypeSystem.Void);
        for (int i = 0; i < record.Fields.Count; i++)
        {
            var fieldClrType = MapToClr(record.Fields[i].Type, typeParamMap);
            ctor.Parameters.Add(new ParameterDefinition(record.Fields[i].Name,
                Mono.Cecil.ParameterAttributes.None, fieldClrType));
        }
        typeDef.Methods.Add(ctor);

        var ctorIl = ctor.Body.GetILProcessor();
        ctorIl.Append(ctorIl.Create(OpCodes.Ldarg_0));
        ctorIl.Append(ctorIl.Create(OpCodes.Call,
            _module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        for (int i = 0; i < fieldDefs.Count; i++)
        {
            ctorIl.Append(ctorIl.Create(OpCodes.Ldarg_0));
            ctorIl.Append(ctorIl.Create(OpCodes.Ldarg, i + 1));
            ctorIl.Append(ctorIl.Create(OpCodes.Stfld, fieldDefs[i].Field));
        }
        ctorIl.Append(ctorIl.Create(OpCodes.Ret));
    }

    private void DefineUnionType(IrNode.UnionDecl union)
    {
        // Abstract base type
        var baseType = new TypeDefinition(_ilNamespace, union.Name,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract,
            _module.TypeSystem.Object);
        _module.Types.Add(baseType);

        if (union.TypeParams.Count > 0)
        {
            foreach (var tp in union.TypeParams)
            {
                var gp = new GenericParameter(tp, baseType);
                baseType.GenericParameters.Add(gp);
            }
        }

        // Base constructor
        var baseCtor = new MethodDefinition(".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _module.TypeSystem.Void);
        baseType.Methods.Add(baseCtor);
        var baseCtorIl = baseCtor.Body.GetILProcessor();
        baseCtorIl.Append(baseCtorIl.Create(OpCodes.Ldarg_0));
        baseCtorIl.Append(baseCtorIl.Create(OpCodes.Call,
            _module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        baseCtorIl.Append(baseCtorIl.Create(OpCodes.Ret));

        _userTypes[union.Name] = baseType;

        // Case types
        foreach (var @case in union.Cases)
        {
            var caseType = new TypeDefinition(_ilNamespace, @case.Name,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                baseType);
            _module.Types.Add(caseType);

            Dictionary<string, TypeReference>? typeParamMap = null;
            if (union.TypeParams.Count > 0)
            {
                typeParamMap = new Dictionary<string, TypeReference>();
                foreach (var tp in union.TypeParams)
                {
                    var gp = new GenericParameter(tp, caseType);
                    caseType.GenericParameters.Add(gp);
                    typeParamMap[tp] = gp;
                }

                // Set parent to closed base type using case's own generic params
                var closedBase = new GenericInstanceType(baseType);
                foreach (var gp in caseType.GenericParameters)
                    closedBase.GenericArguments.Add(gp);
                caseType.BaseType = closedBase;
            }

            var caseFieldDefs = new List<FieldDefinition>();

            foreach (var field in @case.Fields)
            {
                var fieldClrType = MapToClr(field.Type, typeParamMap);
                var fb = new FieldDefinition($"<{field.Name}>k__BackingField",
                    FieldAttributes.Private | FieldAttributes.InitOnly, fieldClrType);
                caseType.Fields.Add(fb);

                var getter = new MethodDefinition($"get_{field.Name}",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    fieldClrType);
                caseType.Methods.Add(getter);
                var getIl = getter.Body.GetILProcessor();
                getIl.Append(getIl.Create(OpCodes.Ldarg_0));
                getIl.Append(getIl.Create(OpCodes.Ldfld, fb));
                getIl.Append(getIl.Create(OpCodes.Ret));

                var prop = new PropertyDefinition(field.Name, Mono.Cecil.PropertyAttributes.None, fieldClrType);
                prop.GetMethod = getter;
                caseType.Properties.Add(prop);

                _unionCaseGetters[$"{union.Name}.{@case.Name}.{field.Name}"] = getter;
                caseFieldDefs.Add(fb);
            }

            // Case constructor
            var caseCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                _module.TypeSystem.Void);
            for (int i = 0; i < @case.Fields.Count; i++)
            {
                var fieldClrType = MapToClr(@case.Fields[i].Type, typeParamMap);
                caseCtor.Parameters.Add(new ParameterDefinition(@case.Fields[i].Name,
                    Mono.Cecil.ParameterAttributes.None, fieldClrType));
            }
            caseType.Methods.Add(caseCtor);

            var caseCtorIl = caseCtor.Body.GetILProcessor();
            caseCtorIl.Append(caseCtorIl.Create(OpCodes.Ldarg_0));

            // Call base constructor
            if (union.TypeParams.Count > 0)
            {
                var closedBase = new GenericInstanceType(baseType);
                foreach (var gp in caseType.GenericParameters)
                    closedBase.GenericArguments.Add(gp);
                var closedBaseCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedBase)
                {
                    HasThis = true
                };
                caseCtorIl.Append(caseCtorIl.Create(OpCodes.Call, closedBaseCtor));
            }
            else
            {
                caseCtorIl.Append(caseCtorIl.Create(OpCodes.Call, baseCtor));
            }

            for (int i = 0; i < caseFieldDefs.Count; i++)
            {
                caseCtorIl.Append(caseCtorIl.Create(OpCodes.Ldarg_0));
                caseCtorIl.Append(caseCtorIl.Create(OpCodes.Ldarg, i + 1));
                caseCtorIl.Append(caseCtorIl.Create(OpCodes.Stfld, caseFieldDefs[i]));
            }
            caseCtorIl.Append(caseCtorIl.Create(OpCodes.Ret));

            // Emit Equals override using runtime helper
            EmitUnionCaseEquals(caseType, caseFieldDefs);
            EmitUnionCaseGetHashCode(caseType, caseFieldDefs);

            var caseKey = $"{union.Name}.{@case.Name}";
            _unionCaseTypes[caseKey] = caseType;
            _unionCasePropertyNames[caseKey] = @case.Fields.Select(f => f.Name).ToList();
        }
    }

    private void EmitUnionCaseEquals(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _module.TypeSystem.Boolean);
        method.Parameters.Add(new ParameterDefinition("obj",
            Mono.Cecil.ParameterAttributes.None, _module.TypeSystem.Object));
        caseType.Methods.Add(method);

        var il = method.Body.GetILProcessor();
        var getType = _module.ImportReference(typeof(object).GetMethod("GetType")!);
        var typeEquality = _module.ImportReference(typeof(Type).GetMethod("op_Equality", [typeof(Type), typeof(Type)])!);
        var returnFalse = il.Create(OpCodes.Ldc_I4_0);

        // Check: obj != null && this.GetType() == obj.GetType()
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Brfalse, returnFalse));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, getType));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Call, typeEquality));
        il.Append(il.Create(OpCodes.Brfalse, returnFalse));

        if (fields.Count == 0)
        {
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
            il.Append(returnFalse);
            il.Append(il.Create(OpCodes.Ret));
            return;
        }

        method.Body.InitLocals = true;
        var otherLocal = new VariableDefinition(_module.TypeSystem.Object);
        method.Body.Variables.Add(otherLocal);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Stloc, otherLocal));

        // Compare each field using object.Equals(object, object)
        var objEquals = _module.ImportReference(
            typeof(object).GetMethod("Equals", [typeof(object), typeof(object)])!);
        foreach (var field in fields)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, field));
            il.Append(il.Create(OpCodes.Box, field.FieldType));
            il.Append(il.Create(OpCodes.Ldloc, otherLocal));
            il.Append(il.Create(OpCodes.Ldfld, field));
            il.Append(il.Create(OpCodes.Box, field.FieldType));
            il.Append(il.Create(OpCodes.Call, objEquals));
            il.Append(il.Create(OpCodes.Brfalse, returnFalse));
        }

        // All fields matched
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));

        // Return false
        il.Append(returnFalse);
        il.Append(il.Create(OpCodes.Ret));
    }

    private void EmitUnionCaseGetHashCode(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition("GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _module.TypeSystem.Int32);
        caseType.Methods.Add(method);

        var il = method.Body.GetILProcessor();

        if (fields.Count == 0)
        {
            // Zero-field case: just hash the type name
            il.Append(il.Create(OpCodes.Ldstr, caseType.Name));
            il.Append(il.Create(OpCodes.Callvirt,
                _module.ImportReference(typeof(string).GetMethod("GetHashCode", Type.EmptyTypes)!)));
            il.Append(il.Create(OpCodes.Ret));
            return;
        }

        method.Body.InitLocals = true;
        var hashCodeType = _module.ImportReference(typeof(HashCode));
        var hashCodeLocal = new VariableDefinition(hashCodeType);
        method.Body.Variables.Add(hashCodeLocal);

        // Initialize HashCode struct
        il.Append(il.Create(OpCodes.Ldloca, hashCodeLocal));
        il.Append(il.Create(OpCodes.Initobj, hashCodeType));

        // Add type name
        var addGenericMethod = typeof(HashCode).GetMethods()
            .First(m => m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1);
        var addString = _module.ImportReference(addGenericMethod.MakeGenericMethod(typeof(string)));
        il.Append(il.Create(OpCodes.Ldloca, hashCodeLocal));
        il.Append(il.Create(OpCodes.Ldstr, caseType.Name));
        il.Append(il.Create(OpCodes.Call, addString));

        // Add each field value (boxed to object)
        var addObject = _module.ImportReference(addGenericMethod.MakeGenericMethod(typeof(object)));
        foreach (var field in fields)
        {
            il.Append(il.Create(OpCodes.Ldloca, hashCodeLocal));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, field));
            il.Append(il.Create(OpCodes.Box, field.FieldType));
            il.Append(il.Create(OpCodes.Call, addObject));
        }

        // Return hash code
        var toHashCode = _module.ImportReference(typeof(HashCode).GetMethod("ToHashCode")!);
        il.Append(il.Create(OpCodes.Ldloca, hashCodeLocal));
        il.Append(il.Create(OpCodes.Call, toHashCode));
        il.Append(il.Create(OpCodes.Ret));
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeDefinition typeDefinition)
    {
        var isGeneric = func.TypeParams is { Count: > 0 };

        var savedTypeVarMap = _currentTypeVarMap;
        var savedTypeParamMap = _currentTypeParamMap;

        TypeReference returnType;
        if (func.IsAsync)
        {
            if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                returnType = _module.ImportReference(typeof(System.Threading.Tasks.Task));
            else
            {
                var taskOpen = _module.ImportReference(typeof(System.Threading.Tasks.Task<>));
                var git = new GenericInstanceType(taskOpen);
                git.GenericArguments.Add(MapToClr(func.ReturnType));
                returnType = git;
            }
        }
        else
        {
            returnType = MapReturnTypeToClr(func.ReturnType);
        }

        var methodDef = new MethodDefinition(Sanitize(func.Name),
            MethodAttributes.Public | MethodAttributes.Static,
            returnType);

        if (isGeneric)
        {
            foreach (var tp in func.TypeParams!)
            {
                var gp = new GenericParameter(tp, methodDef);
                methodDef.GenericParameters.Add(gp);
            }

            var varNameMap = BuildTypeVarMap(func);
            _currentTypeVarMap = new Dictionary<int, TypeReference>();
            _currentTypeParamMap = new Dictionary<string, TypeReference>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx >= 0)
                {
                    _currentTypeVarMap[varId] = methodDef.GenericParameters[idx];
                    _currentTypeParamMap[paramName] = methodDef.GenericParameters[idx];
                }
            }

            // Re-resolve return type with generic params available
            if (func.IsAsync)
            {
                if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    returnType = _module.ImportReference(typeof(System.Threading.Tasks.Task));
                else
                {
                    var taskOpen = _module.ImportReference(typeof(System.Threading.Tasks.Task<>));
                    var git = new GenericInstanceType(taskOpen);
                    git.GenericArguments.Add(MapToClr(func.ReturnType));
                    returnType = git;
                }
            }
            else
            {
                returnType = MapReturnTypeToClr(func.ReturnType);
            }
            methodDef.ReturnType = returnType;
        }

        foreach (var p in func.Params)
        {
            methodDef.Parameters.Add(new ParameterDefinition(p.Name,
                Mono.Cecil.ParameterAttributes.None, MapToClr(p.Type)));
        }

        typeDefinition.Methods.Add(methodDef);
        EmitCustomAttributes(func.Attributes, methodDef);
        _methods[Sanitize(func.Name)] = methodDef;
        if (isGeneric && func.Type is ZType.ZFuncType ft2)
            _genericMethodTypes[Sanitize(func.Name)] = ft2;

        // Branch to async state machine generation if the body contains await
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
            var il = methodDef.Body.GetILProcessor();
            var locals = new Dictionary<string, VariableDefinition>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitNode(func.Body, il, func.Params, locals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;

            if (func.IsAsync)
            {
                // Async without await: wrap result in Task
                if (func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                {
                    if (func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Append(il.Create(OpCodes.Pop));
                    var completedTaskGetter = typeof(System.Threading.Tasks.Task)
                        .GetProperty("CompletedTask")!.GetGetMethod()!;
                    il.Append(il.Create(OpCodes.Call, _module.ImportReference(completedTaskGetter)));
                }
                else
                {
                    var fromResult = typeof(System.Threading.Tasks.Task)
                        .GetMethod("FromResult")!
                        .MakeGenericMethod(IlTypeMapper.MapToClr(func.ReturnType));
                    il.Append(il.Create(OpCodes.Call, _module.ImportReference(fromResult)));
                }
            }

            il.Append(il.Create(OpCodes.Ret));
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
            return new();
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        var map = new Dictionary<int, string>();
        for (int i = 0; i < freeVars.Count && i < func.TypeParams.Count; i++)
            map[freeVars[i]] = func.TypeParams[i];
        return map;
    }

    private void EmitNode(IrNode node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        switch (node)
        {
            case IrNode.IntConst n:
                il.Append(il.Create(OpCodes.Ldc_I4, n.Value));
                break;

            case IrNode.FloatConst n:
                il.Append(il.Create(OpCodes.Ldc_R4, n.Value));
                break;

            case IrNode.BoolConst n:
                il.Append(il.Create(n.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                break;

            case IrNode.StringConst n:
                il.Append(il.Create(OpCodes.Ldstr, n.Value));
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
                il.Append(il.Create(OpCodes.Throw));
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

            case IrNode.Seq seq:
                for (int i = 0; i < seq.Nodes.Count; i++)
                {
                    EmitNode(seq.Nodes[i], il, outerParams, locals);
                    // Pop intermediate results, keep the last
                    if (i < seq.Nodes.Count - 1
                        && seq.Nodes[i].Type is not null
                        and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                        il.Append(il.Create(OpCodes.Pop));
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

            case IrNode.Propagate propagate:
                EmitPropagate(propagate, il, outerParams, locals);
                break;

            default:
                diagnostics.Error($"Cecil IL emission not implemented for {node.GetType().Name}", SourceSpan.None);
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                break;
        }
    }

    private void EmitIf(IrNode.If @if, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        var elseTarget = il.Create(OpCodes.Nop);
        var endTarget = il.Create(OpCodes.Nop);
        EmitNode(@if.Condition, il, outerParams, locals);
        il.Append(il.Create(OpCodes.Brfalse, elseTarget));
        EmitNode(@if.Then, il, outerParams, locals);
        il.Append(il.Create(OpCodes.Br, endTarget));
        il.Append(elseTarget);
        EmitNode(@if.Else, il, outerParams, locals);
        il.Append(endTarget);
    }

    private void EmitLet(IrNode.Let let, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        EmitNode(let.Value, il, outerParams, locals);
        if (let.Value.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            EmitNode(let.Body, il, outerParams, locals);
        }
        else
        {
            var local = new VariableDefinition(MapToClr(let.Value.Type));
            il.Body.Method.Body.Variables.Add(local);
            il.Append(il.Create(OpCodes.Stloc, local));
            locals[let.VarName] = local;

            // Also save to state machine field if we're inside MoveNext
            if (_moveNextCtx != null && _moveNextCtx.VarFields.TryGetValue(let.VarName, out var field))
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldloc, local));
                il.Append(il.Create(OpCodes.Stfld, field));
                _moveNextCtx.AllLocals.Add((let.VarName, local));
            }

            EmitNode(let.Body, il, outerParams, locals);
        }
    }

    private void EmitClrNew(IrNode.ClrNew clrNew, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        foreach (var arg in clrNew.Args)
            EmitNode(arg, il, outerParams, locals);

        var type = _clrInterop.FindType(clrNew.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrNew.QualifiedTypeName}' not found", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        var argTypes = clrNew.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var ctor = type.GetConstructor(argTypes)
            ?? type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == argTypes.Length);

        if (ctor is null)
        {
            diagnostics.Error($"No constructor on '{clrNew.QualifiedTypeName}' matches the given arguments", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(ctor)));
    }

    private void EmitClrCall(IrNode.ClrCall clrCall, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        foreach (var arg in clrCall.Args)
            EmitNode(arg, il, outerParams, locals);

        var type = _clrInterop.FindType(clrCall.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrCall.QualifiedTypeName}' not found", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        var argTypes = clrCall.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();

        System.Reflection.MethodInfo? method;
        if (clrCall.GenericArity > 0)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == clrCall.MethodName
                         && m.IsGenericMethodDefinition
                         && m.GetGenericArguments().Length == clrCall.GenericArity
                         && m.GetParameters().Length == argTypes.Length)
                .ToList();

            System.Reflection.MethodInfo? generic = candidates.Count == 1 ? candidates[0]
                : candidates.Count > 1 ? candidates.OrderByDescending(m => ScoreGenericOverload(m, argTypes)).First()
                : null;

            method = generic is not null
                ? generic.MakeGenericMethod(InferGenericTypeArgs(generic, argTypes))
                : null;
        }
        else
        {
            method = type.GetMethod(clrCall.MethodName, argTypes);
        }

        if (method is null)
        {
            diagnostics.Error($"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' not found", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        il.Append(il.Create(OpCodes.Call, _module.ImportReference(method)));
    }

    private static int ScoreGenericOverload(System.Reflection.MethodInfo method, Type[] argTypes)
    {
        int score = 0;
        var methodParams = method.GetParameters();
        for (int i = 0; i < methodParams.Length && i < argTypes.Length; i++)
        {
            var paramType = methodParams[i].ParameterType;
            if (paramType.IsGenericParameter) score += 10;
            else if (paramType == argTypes[i]) score += 8;
            else if (paramType.IsAssignableFrom(argTypes[i])) score += 5;
        }
        return score;
    }

    private static Type[] InferGenericTypeArgs(System.Reflection.MethodInfo genericMethod, Type[] argTypes)
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

    private void EmitCall(IrNode.Call call, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        if (call.Function is IrNode.Var v)
        {
            var sanitized = Sanitize(v.Name);

            // Check defined methods
            if (_methods.TryGetValue(sanitized, out var methodDef))
            {
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);

                if (methodDef.HasGenericParameters)
                {
                    var typeArgs = InferCecilTypeArgsForCall(sanitized, methodDef, call.Args);
                    var gim = new GenericInstanceMethod(methodDef);
                    foreach (var ta in typeArgs)
                        gim.GenericArguments.Add(ta);
                    il.Append(il.Create(OpCodes.Call, gim));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Call, methodDef));
                }
                return;
            }

            // Check precompiled methods
            if (_precompiledMethods.TryGetValue(sanitized, out var precompiledMethod))
            {
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);

                if (_precompiledReflectionMethods.TryGetValue(sanitized, out var reflectionMethod)
                    && reflectionMethod.IsGenericMethodDefinition)
                {
                    var argTypes = call.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
                    var instantiated = reflectionMethod.MakeGenericMethod(
                        InferGenericTypeArgs(reflectionMethod, argTypes));
                    il.Append(il.Create(OpCodes.Call, _module.ImportReference(instantiated)));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Call, precompiledMethod));
                }
                return;
            }

            // Check locals (delegate invocation)
            if (locals.TryGetValue(v.Name, out var delegateLocal))
            {
                il.Append(il.Create(OpCodes.Ldloc, delegateLocal));
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                EmitDelegateInvoke(call.Function.Type, il);
                return;
            }

            // Check parameters (delegate)
            for (int i = 0; i < outerParams.Count; i++)
            {
                if (outerParams[i].Name == v.Name && outerParams[i].Type is ZType.ZFuncType)
                {
                    il.Append(il.Create(OpCodes.Ldarg, i + _instanceArgOffset));
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(outerParams[i].Type, il);
                    return;
                }
            }

            // Check static fields
            if (_staticFields.TryGetValue(v.Name, out var staticField))
            {
                var fieldType = IlTypeMapper.MapToClr(call.Function.Type);
                if (call.Function.Type is ZType.ZFuncType)
                {
                    il.Append(il.Create(OpCodes.Ldsfld, staticField));
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(call.Function.Type, il);
                    return;
                }
            }

            diagnostics.Error($"Function '{v.Name}' not found for Cecil IL emission", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
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

        diagnostics.Error($"Cecil IL emission not implemented for Call with {call.Function.GetType().Name} target", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private TypeReference[] InferCecilTypeArgsForCall(string sanitizedName, MethodDefinition genericMethod, IReadOnlyList<IrNode> args)
    {
        var genericArgCount = genericMethod.GenericParameters.Count;

        if (_genericMethodTypes.TryGetValue(sanitizedName, out var funcType))
        {
            var result = new TypeReference[genericArgCount];
            var freeVars = Substitution.FreeVars(funcType).OrderBy(id => id).ToList();
            for (int i = 0; i < funcType.Params.Count && i < args.Count; i++)
                MatchZTypeArgs(funcType.Params[i], args[i].Type, freeVars, result);
            for (int i = 0; i < result.Length; i++)
                result[i] ??= _module.TypeSystem.Object;
            return result;
        }

        // Fallback
        var argClrTypes = args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var reflectionMethod = genericMethod.Module.Assembly.MainModule.LookupToken(genericMethod.MetadataToken.ToInt32()) as MethodDefinition;
        // Simple fallback: map arg types
        var fallback = new TypeReference[genericArgCount];
        for (int i = 0; i < fallback.Length; i++)
            fallback[i] = _module.TypeSystem.Object;
        return fallback;
    }

    private void MatchZTypeArgs(ZType formal, ZType actual, List<int> freeVarIds, TypeReference[] result)
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

    private void EmitMatch(IrNode.Match match, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        var scrutineeType = MapToClr(match.Scrutinee.Type);
        var scrutineeLocal = new VariableDefinition(scrutineeType);
        il.Body.Method.Body.Variables.Add(scrutineeLocal);
        EmitNode(match.Scrutinee, il, outerParams, locals);
        il.Append(il.Create(OpCodes.Stloc, scrutineeLocal));

        var endTarget = il.Create(OpCodes.Nop);
        var armTargets = new Instruction[match.Arms.Count];
        for (int i = 0; i < match.Arms.Count; i++)
            armTargets[i] = il.Create(OpCodes.Nop);

        var failTarget = il.Create(OpCodes.Nop);

        for (int i = 0; i < match.Arms.Count; i++)
        {
            il.Append(armTargets[i]);
            var arm = match.Arms[i];
            var nextTarget = i + 1 < match.Arms.Count ? armTargets[i + 1] : failTarget;

            if (i > 0 && match.Arms[i - 1].Pattern is IrPattern.Constructor)
                il.Append(il.Create(OpCodes.Pop));

            EmitPatternTest(arm.Pattern, scrutineeLocal, match.Scrutinee.Type, nextTarget, il, outerParams, locals);
            EmitNode(arm.Body, il, outerParams, locals);
            il.Append(il.Create(OpCodes.Br, endTarget));
        }

        il.Append(failTarget);
        if (match.Arms.Count > 0 && match.Arms[^1].Pattern is IrPattern.Constructor)
            il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldstr, "Non-exhaustive match"));
        var exCtor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(exCtor)));
        il.Append(il.Create(OpCodes.Throw));

        il.Append(endTarget);
    }

    private void EmitPatternTest(IrPattern pattern, VariableDefinition scrutineeLocal, ZType scrutineeType,
        Instruction failTarget, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                break;

            case IrPattern.Variable v:
                var bindLocal = new VariableDefinition(scrutineeLocal.VariableType);
                il.Body.Method.Body.Variables.Add(bindLocal);
                il.Append(il.Create(OpCodes.Ldloc, scrutineeLocal));
                il.Append(il.Create(OpCodes.Stloc, bindLocal));
                locals[v.Name] = bindLocal;
                break;

            case IrPattern.Literal { Value: string s }:
                il.Append(il.Create(OpCodes.Ldloc, scrutineeLocal));
                il.Append(il.Create(OpCodes.Ldstr, s));
                var strEquals = typeof(string).GetMethod("Equals", BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)])!;
                il.Append(il.Create(OpCodes.Call, _module.ImportReference(strEquals)));
                il.Append(il.Create(OpCodes.Brfalse, failTarget));
                break;

            case IrPattern.Literal { Value: int n }:
                il.Append(il.Create(OpCodes.Ldloc, scrutineeLocal));
                il.Append(il.Create(OpCodes.Ldc_I4, n));
                il.Append(il.Create(OpCodes.Ceq));
                il.Append(il.Create(OpCodes.Brfalse, failTarget));
                break;

            case IrPattern.Literal { Value: bool b }:
                il.Append(il.Create(OpCodes.Ldloc, scrutineeLocal));
                il.Append(il.Create(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ceq));
                il.Append(il.Create(OpCodes.Brfalse, failTarget));
                break;

            case IrPattern.Constructor c:
                EmitConstructorPatternTest(c, scrutineeLocal, scrutineeType, failTarget, il, outerParams, locals);
                break;
        }
    }

    private void EmitConstructorPatternTest(IrPattern.Constructor ctor, VariableDefinition scrutineeLocal,
        ZType scrutineeType, Instruction failTarget, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        var caseType = ResolveConstructorCaseType(ctor.Name, scrutineeType);
        if (caseType is null)
        {
            diagnostics.Error($"Cannot resolve constructor type '{ctor.Name}' for pattern match", SourceSpan.None);
            return;
        }

        il.Append(il.Create(OpCodes.Ldloc, scrutineeLocal));
        il.Append(il.Create(OpCodes.Isinst, caseType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, failTarget));

        var castLocal = new VariableDefinition(caseType);
        il.Body.Method.Body.Variables.Add(castLocal);
        il.Append(il.Create(OpCodes.Stloc, castLocal));

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

            for (int i = 0; i < ctor.Fields.Count; i++)
            {
                var field = ctor.Fields[i];
                if (field is IrPattern.Variable v)
                {
                    var propName = i < propertyNames.Count ? propertyNames[i] : "Value";
                    var getterKey = caseKey is not null ? $"{caseKey}.{propName}" : null;

                    if (getterKey is not null && _unionCaseGetters.TryGetValue(getterKey, out var getter))
                    {
                        // Fix up getter for closed generic types (e.g., Some<int>.value instead of Some<T0>.value)
                        var resolvedGetter = getter;
                        if (caseType is GenericInstanceType git)
                        {
                            resolvedGetter = new MethodReference(getter.Name, getter.ReturnType, git)
                            {
                                HasThis = getter.HasThis,
                                ExplicitThis = getter.ExplicitThis,
                                CallingConvention = getter.CallingConvention
                            };
                            foreach (var param in getter.Parameters)
                                resolvedGetter.Parameters.Add(new ParameterDefinition(param.ParameterType));
                        }

                        // Resolve the field type using the pattern's actual type info
                        var fieldType = resolvedGetter.ReturnType;
                        if (fieldType is GenericParameter gp && caseType is GenericInstanceType git2)
                        {
                            var idx = gp.Position;
                            if (idx < git2.GenericArguments.Count)
                                fieldType = git2.GenericArguments[idx];
                        }

                        var fieldLocal = new VariableDefinition(fieldType);
                        il.Body.Method.Body.Variables.Add(fieldLocal);
                        il.Append(il.Create(OpCodes.Ldloc, castLocal));
                        il.Append(il.Create(OpCodes.Callvirt, resolvedGetter));
                        il.Append(il.Create(OpCodes.Stloc, fieldLocal));
                        locals[v.Name] = fieldLocal;
                    }
                }
            }
        }
    }

    private TypeReference? ResolveConstructorCaseType(string caseName, ZType scrutineeType)
    {
        if (scrutineeType is ZType.ZNamedType named)
        {
            var caseKey = $"{named.Name}.{caseName}";
            if (_unionCaseTypes.TryGetValue(caseKey, out var caseType))
            {
                if (named.TypeArgs.Count > 0 && caseType.HasGenericParameters)
                {
                    var git = new GenericInstanceType(caseType);
                    foreach (var ta in named.TypeArgs)
                        git.GenericArguments.Add(MapToClr(ta));
                    return git;
                }
                return caseType;
            }
        }
        return null;
    }

    private void EmitMethodCall(IrNode.MethodCall node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        var receiverClrType = ResolveClrType(node.Receiver.Type);
        var isValueType = receiverClrType.IsValueType;
        VariableDefinition? receiverLocal = null;

        EmitNode(node.Receiver, il, outerParams, locals);

        if (isValueType)
        {
            receiverLocal = new VariableDefinition(MapToClr(node.Receiver.Type));
            il.Body.Method.Body.Variables.Add(receiverLocal);
            il.Append(il.Create(OpCodes.Stloc, receiverLocal));
            il.Append(il.Create(OpCodes.Ldloca, receiverLocal));
        }

        if (node.IsProperty)
        {
            // Try Cecil TypeDefinition first (for types defined in this compilation)
            if (node.Receiver.Type is ZType.ZNamedType named
                && _userTypes.TryGetValue(named.Name, out var typeRef)
                && typeRef is TypeDefinition td)
            {
                var cecilProp = td.Properties.FirstOrDefault(p => p.Name == node.MethodName);
                if (cecilProp?.GetMethod is not null)
                {
                    il.Append(il.Create(isValueType ? OpCodes.Call : OpCodes.Callvirt, cecilProp.GetMethod));
                    return;
                }
            }

            // Resolve using the raw (non-generic-erased) CLR type for proper generic instantiation
            var rawClrType = IlTypeMapper.MapToClr(node.Receiver.Type);
            var prop = rawClrType.GetProperty(node.MethodName);
            if (prop is null && rawClrType.IsGenericType)
            {
                // Try on the open generic definition
                prop = rawClrType.GetGenericTypeDefinition().GetProperty(node.MethodName);
            }
            if (prop is not null)
            {
                var getter = prop.GetGetMethod()!;
                il.Append(il.Create(isValueType ? OpCodes.Call : OpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(getter, node.Receiver.Type)));
                return;
            }
            diagnostics.Error($"Property '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        if (node.IsIndexer)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            var indexer = receiverClrType.GetMethod("get_Item");
            if (indexer is not null)
            {
                il.Append(il.Create(isValueType ? OpCodes.Call : OpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(indexer, node.Receiver.Type)));
                return;
            }
            diagnostics.Error($"Indexer not found on {receiverClrType}", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var argTypes = node.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var method = receiverClrType.GetMethod(node.MethodName, argTypes)
            ?? receiverClrType.GetMethod(node.MethodName, BindingFlags.Public | BindingFlags.Instance);
        if (method is not null && method.GetParameters().Length == argTypes.Length)
        {
            il.Append(il.Create(isValueType ? OpCodes.Call : OpCodes.Callvirt,
                ImportMethodWithGenericDeclaringType(method, node.Receiver.Type)));
            return;
        }
        diagnostics.Error($"Method '{node.MethodName}' not found on {receiverClrType}", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private void EmitImmutableCollectionNew(IReadOnlyList<IrNode> elements, ZType collectionType,
        Type helperClass, string methodName, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        // Use Cecil-aware type mapper to preserve generic parameters (e.g., T0 instead of object)
        TypeReference elementCecilType = _module.TypeSystem.Object;
        if (collectionType is ZType.ZNamedType { TypeArgs: [var elemT] })
            elementCecilType = MapToClr(elemT);

        il.Append(il.Create(OpCodes.Ldc_I4, elements.Count));
        il.Append(il.Create(OpCodes.Newarr, elementCecilType));

        for (int i = 0; i < elements.Count; i++)
        {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, i));
            EmitNode(elements[i], il, outerParams, locals);
            il.Append(il.Create(OpCodes.Stelem_Any, elementCecilType));
        }

        var openMethod = helperClass.GetMethods()
            .First(m => m.Name == methodName
                && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType.IsArray: true }]);
        var openMethodRef = _module.ImportReference(openMethod);
        var gim = new GenericInstanceMethod(openMethodRef);
        gim.GenericArguments.Add(elementCecilType);
        il.Append(il.Create(OpCodes.Call, gim));
    }

    private void EmitMapNew(IrNode.MapNew node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        // Use Cecil-aware type mapper to preserve generic parameters
        TypeReference keyCecilType = _module.TypeSystem.Object, valueCecilType = _module.TypeSystem.Object;
        Type keyClrType = typeof(object), valueClrType = typeof(object);
        if (node.Type is ZType.ZNamedType { TypeArgs: [var keyT, var valT] })
        {
            keyCecilType = MapToClr(keyT);
            valueCecilType = MapToClr(valT);
            keyClrType = IlTypeMapper.MapToClr(keyT);
            valueClrType = IlTypeMapper.MapToClr(valT);
        }

        var kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyClrType, valueClrType);
        var kvpCtor = kvpType.GetConstructor([keyClrType, valueClrType])!;
        var kvpCecilType = _module.ImportReference(kvpType);

        il.Append(il.Create(OpCodes.Ldc_I4, node.Entries.Count));
        il.Append(il.Create(OpCodes.Newarr, kvpCecilType));

        for (int i = 0; i < node.Entries.Count; i++)
        {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, i));
            EmitNode(node.Entries[i].Key, il, outerParams, locals);
            EmitNode(node.Entries[i].Value, il, outerParams, locals);
            il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(kvpCtor)));
            il.Append(il.Create(OpCodes.Stelem_Any, kvpCecilType));
        }

        var createRangeOpenMethod = typeof(ImmutableDictionary).GetMethods()
            .First(m => m.Name == "CreateRange"
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 1);
        var createRangeRef = _module.ImportReference(createRangeOpenMethod);
        var gim = new GenericInstanceMethod(createRangeRef);
        gim.GenericArguments.Add(keyCecilType);
        gim.GenericArguments.Add(valueCecilType);
        il.Append(il.Create(OpCodes.Call, gim));
    }

    private void EmitLambda(IrNode.FuncDef funcDef, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        var lambdaName = $"__lambda_{_lambdaId++}_{funcDef.Name}";
        var paramNames = funcDef.Params.Select(p => p.Name).ToHashSet();
        var freeVars = FindFreeVars(funcDef.Body, paramNames);

        var captures = new List<(string Name, TypeReference CecilType, Type ClrType)>();
        foreach (var fv in freeVars)
        {
            if (locals.TryGetValue(fv, out var loc))
                captures.Add((fv, loc.VariableType, IlTypeMapper.MapToClr(GetVarType(fv, outerParams, locals) ?? ZType.Unit)));
            else
            {
                for (int i = 0; i < outerParams.Count; i++)
                {
                    if (outerParams[i].Name == fv)
                    {
                        captures.Add((fv, MapToClr(outerParams[i].Type),
                            IlTypeMapper.MapToClr(outerParams[i].Type)));
                        break;
                    }
                }
            }
        }

        var delegateClrType = IlTypeMapper.MapToClr(funcDef.Type);

        if (captures.Count == 0)
        {
            EmitFuncDef(funcDef with { Name = lambdaName }, _currentTypeDefinition!);
            var lambdaMethod = _methods[Sanitize(lambdaName)];
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldftn, lambdaMethod));
            il.Append(il.Create(OpCodes.Newobj, ImportDelegateConstructor(funcDef.Type)));
        }
        else
        {
            var closureType = new TypeDefinition("", $"<>c__{lambdaName}",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class,
                _module.TypeSystem.Object);
            _currentTypeDefinition!.NestedTypes.Add(closureType);

            var captureFields = new List<FieldDefinition>();
            foreach (var (name, cecilType, _) in captures)
            {
                var fb = new FieldDefinition(name, FieldAttributes.Public, cecilType);
                closureType.Fields.Add(fb);
                captureFields.Add(fb);
            }

            var closureCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                _module.TypeSystem.Void);
            closureType.Methods.Add(closureCtor);
            var closureCtorIl = closureCtor.Body.GetILProcessor();
            closureCtorIl.Append(closureCtorIl.Create(OpCodes.Ldarg_0));
            closureCtorIl.Append(closureCtorIl.Create(OpCodes.Call,
                _module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            closureCtorIl.Append(closureCtorIl.Create(OpCodes.Ret));

            var lambdaReturnType = MapReturnTypeToClr(funcDef.ReturnType);
            var lambdaMethod = new MethodDefinition("Invoke",
                MethodAttributes.Public, lambdaReturnType);
            foreach (var p in funcDef.Params)
                lambdaMethod.Parameters.Add(new ParameterDefinition(p.Name,
                    Mono.Cecil.ParameterAttributes.None, MapToClr(p.Type)));
            closureType.Methods.Add(lambdaMethod);

            var lambdaIl = lambdaMethod.Body.GetILProcessor();
            var lambdaLocals = new Dictionary<string, VariableDefinition>();

            for (int i = 0; i < captures.Count; i++)
            {
                var captureLocal = new VariableDefinition(captures[i].CecilType);
                lambdaMethod.Body.Variables.Add(captureLocal);
                lambdaIl.Append(lambdaIl.Create(OpCodes.Ldarg_0));
                lambdaIl.Append(lambdaIl.Create(OpCodes.Ldfld, captureFields[i]));
                lambdaIl.Append(lambdaIl.Create(OpCodes.Stloc, captureLocal));
                lambdaLocals[captures[i].Name] = captureLocal;
            }

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 1;
            _currentFuncReturnType = funcDef.ReturnType;
            EmitNode(funcDef.Body, lambdaIl, funcDef.Params, lambdaLocals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
            lambdaIl.Append(lambdaIl.Create(OpCodes.Ret));

            // Emit closure instantiation
            il.Append(il.Create(OpCodes.Newobj, closureCtor));
            for (int i = 0; i < captures.Count; i++)
            {
                il.Append(il.Create(OpCodes.Dup));
                EmitLoadVar(captures[i].Name, il, outerParams, locals);
                il.Append(il.Create(OpCodes.Stfld, captureFields[i]));
            }

            il.Append(il.Create(OpCodes.Ldftn, lambdaMethod));
            il.Append(il.Create(OpCodes.Newobj, ImportDelegateConstructor(funcDef.Type)));
        }
    }

    private ZType? GetVarType(string name, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
                return outerParams[i].Type;
        }
        return null;
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

    private void EmitRecordNew(IrNode.RecordNew node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        foreach (var (_, value) in node.Fields)
            EmitNode(value, il, outerParams, locals);

        if (_userTypes.TryGetValue(node.TypeName, out var typeRef) && typeRef is TypeDefinition td)
        {
            var ctor = td.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                && m.Parameters.Count == node.Fields.Count);
            if (ctor is not null)
            {
                il.Append(il.Create(OpCodes.Newobj, ctor));
                return;
            }
        }

        diagnostics.Error($"Record type '{node.TypeName}' not found for Cecil IL emission", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private void EmitFieldGet(IrNode.FieldGet node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        EmitNode(node.Record, il, outerParams, locals);
        var recordType = node.Record.Type;
        if (recordType is ZType.ZNamedType named && _userTypes.TryGetValue(named.Name, out var typeRef))
        {
            if (typeRef is TypeDefinition td)
            {
                var prop = td.Properties.FirstOrDefault(p => p.Name == node.FieldName);
                if (prop?.GetMethod is not null)
                {
                    il.Append(il.Create(OpCodes.Callvirt, prop.GetMethod));
                    return;
                }
            }
            else
            {
                // Precompiled type — resolve via reflection
                var clrType = ResolveClrTypeForTypeRef(typeRef);
                if (clrType is not null)
                {
                    var prop = clrType.GetProperty(node.FieldName);
                    if (prop?.GetGetMethod() is not null)
                    {
                        il.Append(il.Create(OpCodes.Callvirt, _module.ImportReference(prop.GetGetMethod()!)));
                        return;
                    }
                }
            }
        }

        diagnostics.Error($"Field '{node.FieldName}' not found for Cecil IL emission", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private void EmitUnionCaseNew(IrNode.UnionCaseNew node, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var caseKey = $"{node.UnionName}.{node.CaseName}";
        if (_unionCaseTypes.TryGetValue(caseKey, out var caseTypeRef))
        {
            if (node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } nt && caseTypeRef.HasGenericParameters)
            {
                var git = new GenericInstanceType(caseTypeRef);
                foreach (var ta in nt.TypeArgs)
                    git.GenericArguments.Add(MapToClr(ta));

                // Find the constructor on the open type and make a reference on the closed type
                if (caseTypeRef is TypeDefinition caseTd)
                {
                    var openCtor = caseTd.Methods.First(m => m.IsConstructor && !m.IsStatic
                        && m.Parameters.Count == node.Args.Count);
                    var closedCtor = new MethodReference(".ctor", _module.TypeSystem.Void, git)
                    {
                        HasThis = true
                    };
                    foreach (var p in openCtor.Parameters)
                        closedCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                    il.Append(il.Create(OpCodes.Newobj, closedCtor));
                }
                else
                {
                    // Precompiled: resolve via TypeDefinition from the imported TypeReference
                    var resolved = caseTypeRef.Resolve();
                    if (resolved is not null)
                    {
                        var openCtor = resolved.Methods.First(m => m.IsConstructor && !m.IsStatic
                            && m.Parameters.Count == node.Args.Count);
                        var closedCtor = new MethodReference(".ctor", _module.TypeSystem.Void, git)
                        {
                            HasThis = true
                        };
                        foreach (var p in openCtor.Parameters)
                            closedCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                        il.Append(il.Create(OpCodes.Newobj, closedCtor));
                    }
                    else
                    {
                        diagnostics.Error($"Cannot resolve precompiled union case type for '{caseKey}'", SourceSpan.None);
                        il.Append(il.Create(OpCodes.Ldc_I4_0));
                    }
                }
                return;
            }

            // Non-generic
            if (caseTypeRef is TypeDefinition caseTd2)
            {
                var ctor = caseTd2.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                    && m.Parameters.Count == node.Args.Count);
                if (ctor is not null)
                {
                    il.Append(il.Create(OpCodes.Newobj, ctor));
                    return;
                }
            }
            else
            {
                // Non-generic precompiled
                var resolved = caseTypeRef.Resolve();
                if (resolved is not null)
                {
                    var ctor = resolved.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                        && m.Parameters.Count == node.Args.Count);
                    if (ctor is not null)
                    {
                        il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(ctor)));
                        return;
                    }
                }
            }
        }

        diagnostics.Error($"Union case '{caseKey}' not found for Cecil IL emission", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private void EmitLoadVar(string name, ILProcessor il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, VariableDefinition> locals)
    {
        if (locals.TryGetValue(name, out var local))
        {
            il.Append(il.Create(OpCodes.Ldloc, local));
            return;
        }

        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
            {
                il.Append(il.Create(OpCodes.Ldarg, i + _instanceArgOffset));
                return;
            }
        }

        if (_currentClassFields is not null && _currentClassFields.TryGetValue(name, out var classField))
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, classField));
            return;
        }

        if (_staticFields.TryGetValue(name, out var field))
        {
            il.Append(il.Create(OpCodes.Ldsfld, field));
            return;
        }

        diagnostics.Error($"Variable '{name}' not found for Cecil IL emission", SourceSpan.None);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
    }

    private void EmitBinaryOp(string op, ZType? leftType, ILProcessor il)
    {
        switch (op)
        {
            case "+" when leftType is ZType.ZPrimitiveType { Kind: PrimitiveKind.String }:
                var concatMethod = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
                il.Append(il.Create(OpCodes.Call, _module.ImportReference(concatMethod)));
                break;
            case "+": il.Append(il.Create(OpCodes.Add)); break;
            case "-": il.Append(il.Create(OpCodes.Sub)); break;
            case "*": il.Append(il.Create(OpCodes.Mul)); break;
            case "/": il.Append(il.Create(OpCodes.Div)); break;
            case "%": il.Append(il.Create(OpCodes.Rem)); break;
            case "=": il.Append(il.Create(OpCodes.Ceq)); break;
            case "<": il.Append(il.Create(OpCodes.Clt)); break;
            case ">": il.Append(il.Create(OpCodes.Cgt)); break;
            case "!=":
                il.Append(il.Create(OpCodes.Ceq));
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ceq));
                break;
            case "<=":
                il.Append(il.Create(OpCodes.Cgt));
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ceq));
                break;
            case ">=":
                il.Append(il.Create(OpCodes.Clt));
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ceq));
                break;
            case "and": il.Append(il.Create(OpCodes.And)); break;
            case "or": il.Append(il.Create(OpCodes.Or)); break;
        }
    }

    private static void EmitUnaryOp(string op, ILProcessor il)
    {
        switch (op)
        {
            case "not":
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ceq));
                break;
        }
    }

    private void EmitAwait(IrNode.Await awaitNode, ILProcessor il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, VariableDefinition> locals)
    {
        // Emit the task expression (pushes Task<T> or Task on stack)
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Resolve GetAwaiter() and GetResult() via reflection on the CLR task type
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        var awaiterType = getAwaiterMethod.ReturnType;
        var getResultMethod = awaiterType.GetMethod("GetResult", Type.EmptyTypes)!;

        // Call GetAwaiter() on the Task
        il.Append(il.Create(OpCodes.Call, _module.ImportReference(getAwaiterMethod)));

        // TaskAwaiter is a struct — store in local and load address for instance method call
        var awaiterLocal = new VariableDefinition(_module.ImportReference(awaiterType));
        il.Body.Method.Body.Variables.Add(awaiterLocal);
        il.Append(il.Create(OpCodes.Stloc, awaiterLocal));
        il.Append(il.Create(OpCodes.Ldloca, awaiterLocal));

        // Call GetResult() — returns T for Task<T>, void for non-generic Task
        il.Append(il.Create(OpCodes.Call, _module.ImportReference(getResultMethod)));
    }

    private void EmitTryCatch(IrNode.TryCatch node, ILProcessor il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, VariableDefinition> locals)
    {
        // Extract Ok/Err types from the Result type
        if (node.Type is not ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            diagnostics.Error("TryCatch node type is not a Result type", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        var resultClrTypeRef = MapToClr(node.Type);

        // Declare a local to hold the result
        var resultLocal = new VariableDefinition(resultClrTypeRef);
        il.Body.Method.Body.Variables.Add(resultLocal);

        // Resolve Ok and Err case types
        if (!_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseTypeRef) ||
            !_unionCaseTypes.TryGetValue("Result.Err", out var errCaseTypeRef))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        // Resolve constructors for Ok and Err
        MethodReference okCtor, errCtor;
        if (okCaseTypeRef is TypeDefinition okTd && okTd.HasGenericParameters)
        {
            var closedOk = new GenericInstanceType(okCaseTypeRef);
            closedOk.GenericArguments.Add(MapToClr(okT));
            closedOk.GenericArguments.Add(MapToClr(errT));

            var closedErr = new GenericInstanceType(errCaseTypeRef);
            closedErr.GenericArguments.Add(MapToClr(okT));
            closedErr.GenericArguments.Add(MapToClr(errT));

            var openOkCtor = okTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
            okCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedOk) { HasThis = true };
            foreach (var p in openOkCtor.Parameters)
                okCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

            var errTd = (TypeDefinition)errCaseTypeRef;
            var openErrCtor = errTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
            errCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedErr) { HasThis = true };
            foreach (var p in openErrCtor.Parameters)
                errCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        }
        else
        {
            // Precompiled: resolve via Cecil TypeDefinition
            var okResolved = okCaseTypeRef.Resolve();
            var errResolved = errCaseTypeRef.Resolve();
            if (okResolved is null || errResolved is null)
            {
                diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                return;
            }

            if (okResolved.HasGenericParameters)
            {
                var closedOk = new GenericInstanceType(okCaseTypeRef);
                closedOk.GenericArguments.Add(MapToClr(okT));
                closedOk.GenericArguments.Add(MapToClr(errT));

                var closedErr = new GenericInstanceType(errCaseTypeRef);
                closedErr.GenericArguments.Add(MapToClr(okT));
                closedErr.GenericArguments.Add(MapToClr(errT));

                var openOkCtor = okResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                okCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedOk) { HasThis = true };
                foreach (var p in openOkCtor.Parameters)
                    okCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

                var openErrCtor = errResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                errCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedErr) { HasThis = true };
                foreach (var p in openErrCtor.Parameters)
                    errCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            }
            else
            {
                var openOkCtor = okResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                var openErrCtor = errResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                okCtor = _module.ImportReference(openOkCtor);
                errCtor = _module.ImportReference(openErrCtor);
            }
        }

        // Create marker instructions for exception handler boundaries
        var tryStart = il.Create(OpCodes.Nop);
        var handlerStart = il.Create(OpCodes.Nop);
        var handlerEnd = il.Create(OpCodes.Nop);

        // Try block
        il.Append(tryStart);
        EmitNode(node.Body, il, outerParams, locals);
        il.Append(il.Create(OpCodes.Newobj, okCtor));
        il.Append(il.Create(OpCodes.Stloc, resultLocal));
        il.Append(il.Create(OpCodes.Leave, handlerEnd));

        // Catch (Exception) block
        il.Append(handlerStart);
        // Stack has the Exception; get its Message
        var getMessage = typeof(Exception).GetProperty("Message")!.GetGetMethod()!;
        il.Append(il.Create(OpCodes.Callvirt, _module.ImportReference(getMessage)));

        // Create ErrorInfo(message, None<ErrorInfo>())
        if (_userTypes.TryGetValue("ErrorInfo", out var errorInfoTypeRef) &&
            _unionCaseTypes.TryGetValue("Option.None", out var noneCaseTypeRef))
        {
            if (noneCaseTypeRef is TypeDefinition noneTd && noneTd.HasGenericParameters)
            {
                var closedNone = new GenericInstanceType(noneCaseTypeRef);
                closedNone.GenericArguments.Add(errorInfoTypeRef);
                var openNoneCtor = noneTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
                var noneCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedNone) { HasThis = true };
                il.Append(il.Create(OpCodes.Newobj, noneCtor));
            }
            else
            {
                // Precompiled: resolve the None type via its TypeDefinition
                var noneResolved = noneCaseTypeRef.Resolve();
                if (noneResolved is not null && noneResolved.HasGenericParameters)
                {
                    var closedNone = new GenericInstanceType(noneCaseTypeRef);
                    closedNone.GenericArguments.Add(errorInfoTypeRef);
                    var openNoneCtor = noneResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
                    var noneCtor = new MethodReference(".ctor", _module.TypeSystem.Void, closedNone) { HasThis = true };
                    il.Append(il.Create(OpCodes.Newobj, noneCtor));
                }
                else if (noneResolved is not null)
                {
                    var noneCtor = noneResolved.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
                    if (noneCtor is not null)
                        il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(noneCtor)));
                    else
                        il.Append(il.Create(OpCodes.Ldnull));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Ldnull));
                }
            }

            // new ErrorInfo(message, noneInstance)
            if (errorInfoTypeRef is TypeDefinition errorInfoTd)
            {
                var errorInfoCtor = errorInfoTd.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
                il.Append(il.Create(OpCodes.Newobj, errorInfoCtor));
            }
            else
            {
                var errorInfoResolved = errorInfoTypeRef.Resolve();
                if (errorInfoResolved is not null)
                {
                    var errorInfoCtor2 = errorInfoResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2);
                    il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(errorInfoCtor2)));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Ldnull));
                }
            }
        }
        else
        {
            // Fallback
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ldnull));
        }

        // new Err(errorInfo)
        il.Append(il.Create(OpCodes.Newobj, errCtor));
        il.Append(il.Create(OpCodes.Stloc, resultLocal));
        il.Append(il.Create(OpCodes.Leave, handlerEnd));

        // After handler
        il.Append(handlerEnd);

        // Register the exception handler
        il.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = handlerEnd,
            CatchType = _module.ImportReference(typeof(Exception))
        });

        // Load the result
        il.Append(il.Create(OpCodes.Ldloc, resultLocal));
    }

    private void EmitPropagate(IrNode.Propagate node, ILProcessor il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, VariableDefinition> locals)
    {
        // Emit inner expression (should evaluate to a Result value)
        EmitNode(node.Expr, il, outerParams, locals);

        var resultClrTypeRef = MapToClr(node.ResultType);
        var tempLocal = new VariableDefinition(resultClrTypeRef);
        il.Body.Method.Body.Variables.Add(tempLocal);
        il.Append(il.Create(OpCodes.Stloc, tempLocal));

        // Extract Ok/Err types from the inner Result type
        if (node.ResultType is not ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            diagnostics.Error("Propagate expression is not a Result type", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        if (!_unionCaseTypes.TryGetValue("Result.Err", out var errCaseTypeRef) ||
            !_unionCaseTypes.TryGetValue("Result.Ok", out var okCaseTypeRef))
        {
            diagnostics.Error("Cannot resolve Ok/Err types for Propagate", SourceSpan.None);
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            return;
        }

        TypeReference closedErrType, closedOkType;
        MethodReference errPropGetter, okValueGetter;

        // Check if we have generic parameters (works for both TypeDefinition and precompiled TypeReference)
        var errResolved = errCaseTypeRef is TypeDefinition errTd2 ? errTd2 : errCaseTypeRef.Resolve();
        var hasGenericParams = errResolved?.HasGenericParameters ?? false;

        if (hasGenericParams)
        {
            closedErrType = new GenericInstanceType(errCaseTypeRef);
            ((GenericInstanceType)closedErrType).GenericArguments.Add(MapToClr(okT));
            ((GenericInstanceType)closedErrType).GenericArguments.Add(MapToClr(errT));

            closedOkType = new GenericInstanceType(okCaseTypeRef);
            ((GenericInstanceType)closedOkType).GenericArguments.Add(MapToClr(okT));
            ((GenericInstanceType)closedOkType).GenericArguments.Add(MapToClr(errT));

            // Find getter methods from the registered getters
            var openErrGetter = _unionCaseGetters["Result.Err.error"];
            errPropGetter = new MethodReference(openErrGetter.Name, openErrGetter.ReturnType, closedErrType)
                { HasThis = true };

            var openOkGetter = _unionCaseGetters["Result.Ok.value"];
            okValueGetter = new MethodReference(openOkGetter.Name, openOkGetter.ReturnType, closedOkType)
                { HasThis = true };
        }
        else
        {
            closedErrType = errCaseTypeRef;
            closedOkType = okCaseTypeRef;

            if (_unionCaseGetters.TryGetValue("Result.Err.error", out var egRef) &&
                _unionCaseGetters.TryGetValue("Result.Ok.value", out var ogRef))
            {
                errPropGetter = egRef;
                okValueGetter = ogRef;
            }
            else
            {
                diagnostics.Error("Cannot resolve Ok/Err property getters for Propagate", SourceSpan.None);
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                return;
            }
        }

        // Test: is it Err?
        var okLabel = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Ldloc, tempLocal));
        il.Append(il.Create(OpCodes.Isinst, closedErrType));
        il.Append(il.Create(OpCodes.Brfalse, okLabel));

        // It's Err — extract the error and wrap in the function's return Err type, then early return
        il.Append(il.Create(OpCodes.Ldloc, tempLocal));
        il.Append(il.Create(OpCodes.Castclass, closedErrType));

        // Get .error property
        il.Append(il.Create(OpCodes.Callvirt, errPropGetter));

        // Wrap in the function's return Err type
        if (_currentFuncReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
        {
            var funcErrResolved = errResolved;
            if (funcErrResolved is not null && funcErrResolved.HasGenericParameters)
            {
                var funcErrType = new GenericInstanceType(errCaseTypeRef);
                funcErrType.GenericArguments.Add(MapToClr(fOkT));
                funcErrType.GenericArguments.Add(MapToClr(fErrT));

                var openCtor = funcErrResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                var funcErrCtor = new MethodReference(".ctor", _module.TypeSystem.Void, funcErrType) { HasThis = true };
                foreach (var p in openCtor.Parameters)
                    funcErrCtor.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                il.Append(il.Create(OpCodes.Newobj, funcErrCtor));
            }
            else if (funcErrResolved is not null)
            {
                var openCtor = funcErrResolved.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
                il.Append(il.Create(OpCodes.Newobj, _module.ImportReference(openCtor)));
            }
        }

        il.Append(il.Create(OpCodes.Ret)); // Early return

        // Ok path — extract Value
        il.Append(okLabel);
        il.Append(il.Create(OpCodes.Ldloc, tempLocal));
        il.Append(il.Create(OpCodes.Castclass, closedOkType));
        il.Append(il.Create(OpCodes.Callvirt, okValueGetter));
        // Unwrapped value is now on the stack
    }

    private static string Sanitize(string name) =>
        name.Replace("-", "_").Replace("/", "_").Replace("?", "_q")
            .Replace(">", "_gt").Replace("|", "_pipe").Replace("^", "");

    // ─── Async State Machine Generation ───────────────────────────────────

    private void EmitAsyncFuncDef(IrNode.FuncDef func, MethodDefinition stubMethod, TypeDefinition parentType)
    {
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

        var builderTypeRef = _module.ImportReference(builderClrType);

        // --- Define state machine struct ---
        var smType = new TypeDefinition(
            "", smName,
            TypeAttributes.Sealed | TypeAttributes.NestedPrivate | TypeAttributes.SequentialLayout,
            _module.ImportReference(typeof(ValueType)));
        smType.Interfaces.Add(new InterfaceImplementation(
            _module.ImportReference(typeof(IAsyncStateMachine))));
        // [CompilerGenerated]
        var compGenCtor = _module.ImportReference(
            typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!);
        smType.CustomAttributes.Add(new CustomAttribute(compGenCtor));
        parentType.NestedTypes.Add(smType);

        // --- Define fields ---
        var stateField = new FieldDefinition("__state", FieldAttributes.Public, _module.TypeSystem.Int32);
        smType.Fields.Add(stateField);

        var builderField = new FieldDefinition("__builder", FieldAttributes.Public, builderTypeRef);
        smType.Fields.Add(builderField);

        // Parameter fields
        var varFields = new Dictionary<string, FieldDefinition>();
        foreach (var p in func.Params)
        {
            var pField = new FieldDefinition(Sanitize(p.Name), FieldAttributes.Public, MapToClr(p.Type));
            smType.Fields.Add(pField);
            varFields[p.Name] = pField;
        }

        // Hoisted local fields
        foreach (var local in info.HoistedLocals)
        {
            if (!varFields.ContainsKey(local.Name))
            {
                var lField = new FieldDefinition($"<{Sanitize(local.Name)}>5__", FieldAttributes.Public,
                    MapToClr(local.Type));
                smType.Fields.Add(lField);
                varFields[local.Name] = lField;
            }
        }

        // Awaiter fields
        var awaiterFields = new Dictionary<int, FieldDefinition>();
        foreach (var ap in info.AwaitPoints)
        {
            var awaiterClrType = GetAwaiterClrType(ap);
            var awaiterField = new FieldDefinition($"__awaiter{ap.StateNumber}",
                FieldAttributes.Private, _module.ImportReference(awaiterClrType));
            smType.Fields.Add(awaiterField);
            awaiterFields[ap.StateNumber] = awaiterField;
        }

        // --- Emit MoveNext method ---
        EmitMoveNextMethod(func, smType, stateField, builderField, builderClrType,
            varFields, awaiterFields, info);

        // --- Emit SetStateMachine method ---
        EmitSetStateMachineMethod(smType, builderField, builderClrType);

        // --- Emit stub method body ---
        EmitAsyncStubBody(func, stubMethod, smType, stateField, builderField, builderClrType, varFields);

        // --- Add [AsyncStateMachine] attribute to stub ---
        var asmAttrCtor = _module.ImportReference(
            typeof(AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!);
        var asmAttr = new CustomAttribute(asmAttrCtor);
        asmAttr.ConstructorArguments.Add(new CustomAttributeArgument(
            _module.ImportReference(typeof(Type)), (TypeReference)smType));
        stubMethod.CustomAttributes.Add(asmAttr);
    }

    private static Type GetAwaiterClrType(AsyncStateMachineAnalyzer.AwaitPointInfo ap)
    {
        if (ap.ResultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return typeof(System.Runtime.CompilerServices.TaskAwaiter);
        var innerClr = IlTypeMapper.MapToClr(ap.ResultType);
        return typeof(System.Runtime.CompilerServices.TaskAwaiter<>).MakeGenericType(innerClr);
    }

    private void EmitAsyncStubBody(
        IrNode.FuncDef func,
        MethodDefinition stubMethod,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields)
    {
        var il = stubMethod.Body.GetILProcessor();
        stubMethod.Body.InitLocals = true;

        // Local 0: the state machine struct
        var smLocal = new VariableDefinition(smType);
        stubMethod.Body.Variables.Add(smLocal);

        // initobj smType
        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Initobj, smType));

        // Copy parameters into state machine fields
        for (int i = 0; i < func.Params.Count; i++)
        {
            il.Append(il.Create(OpCodes.Ldloca, smLocal));
            il.Append(il.Create(OpCodes.Ldarg, i));
            il.Append(il.Create(OpCodes.Stfld, varFields[func.Params[i].Name]));
        }

        // sm.__builder = AsyncTaskMethodBuilder<T>.Create()
        var createMethod = _module.ImportReference(builderClrType.GetMethod("Create")!);
        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Call, createMethod));
        il.Append(il.Create(OpCodes.Stfld, builderField));

        // sm.__state = -1
        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Stfld, stateField));

        // sm.__builder.Start<SM>(ref sm)
        var startMethodRef = _module.ImportReference(builderClrType.GetMethod("Start")!);
        var startGeneric = new GenericInstanceMethod(startMethodRef);
        startGeneric.GenericArguments.Add(smType);

        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Ldflda, builderField));
        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Call, startGeneric));

        // return sm.__builder.Task
        var taskPropGetter = _module.ImportReference(builderClrType.GetProperty("Task")!.GetGetMethod()!);
        il.Append(il.Create(OpCodes.Ldloca, smLocal));
        il.Append(il.Create(OpCodes.Ldflda, builderField));
        il.Append(il.Create(OpCodes.Call, taskPropGetter));
        il.Append(il.Create(OpCodes.Ret));
    }

    private void EmitMoveNextMethod(
        IrNode.FuncDef func,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields,
        Dictionary<int, FieldDefinition> awaiterFields,
        AsyncStateMachineAnalyzer.AsyncMethodInfo info)
    {
        var moveNext = new MethodDefinition("MoveNext",
            MethodAttributes.Private | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            _module.TypeSystem.Void);
        smType.Methods.Add(moveNext);
        moveNext.Body.InitLocals = true;

        // Override IAsyncStateMachine.MoveNext
        var iasm = _module.ImportReference(typeof(IAsyncStateMachine));
        var moveNextIntf = _module.ImportReference(typeof(IAsyncStateMachine).GetMethod("MoveNext")!);
        moveNext.Overrides.Add(moveNextIntf);

        var il = moveNext.Body.GetILProcessor();

        // Declare locals
        var stateLocal = new VariableDefinition(_module.TypeSystem.Int32);
        moveNext.Body.Variables.Add(stateLocal);

        // Local for final result (if non-void)
        VariableDefinition? resultLocal = null;
        if (!info.IsVoidReturn)
        {
            resultLocal = new VariableDefinition(MapToClr(func.ReturnType));
            moveNext.Body.Variables.Add(resultLocal);
        }

        // Exception local for catch block
        var exLocal = new VariableDefinition(_module.ImportReference(typeof(Exception)));
        moveNext.Body.Variables.Add(exLocal);

        // Declare locals for each param (load from fields at resume points)
        var paramLocals = new Dictionary<string, VariableDefinition>();
        foreach (var p in func.Params)
        {
            var pLocal = new VariableDefinition(MapToClr(p.Type));
            moveNext.Body.Variables.Add(pLocal);
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
            NextAwaitState = 0
        };

        // Add param locals to the AllLocals tracking
        foreach (var p in func.Params)
            _moveNextCtx.AllLocals.Add((p.Name, paramLocals[p.Name]));

        // Load __state into local
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, stateField));
        il.Append(il.Create(OpCodes.Stloc, stateLocal));

        // --- Try block ---
        var tryStart = il.Create(OpCodes.Nop);
        il.Append(tryStart);

        // Jump table: create resume labels for each await point
        var resumeLabels = new Instruction[info.AwaitPoints.Count];
        for (int i = 0; i < info.AwaitPoints.Count; i++)
            resumeLabels[i] = il.Create(OpCodes.Nop);

        // switch (state) { 0: goto resume0, 1: goto resume1, ... }
        if (resumeLabels.Length > 0)
        {
            il.Append(il.Create(OpCodes.Ldloc, stateLocal));
            il.Append(il.Create(OpCodes.Switch, resumeLabels));
        }

        // Initial state: load params from fields into locals
        foreach (var p in func.Params)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, varFields[p.Name]));
            il.Append(il.Create(OpCodes.Stloc, paramLocals[p.Name]));
        }

        // Store resume labels and exit label for EmitMoveNextAwait to use
        _moveNextCtx.ResumeLabels = resumeLabels;
        var exitLabel = il.Create(OpCodes.Nop);
        _moveNextCtx.ExitLabel = exitLabel;

        // Emit the body using regular EmitNode (outerParams is empty; params come from locals dict)
        var bodyLocals = new Dictionary<string, VariableDefinition>(paramLocals);
        EmitNode(func.Body, il, [], bodyLocals);

        // Store the result
        if (!info.IsVoidReturn)
            il.Append(il.Create(OpCodes.Stloc, resultLocal!));
        else if (func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            il.Append(il.Create(OpCodes.Pop));

        // Leave try block
        var afterTry = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Leave, afterTry));

        // --- Catch block ---
        var catchStart = il.Create(OpCodes.Nop);
        il.Append(catchStart);
        il.Append(il.Create(OpCodes.Stloc, exLocal));

        // __state = -2
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, -2));
        il.Append(il.Create(OpCodes.Stfld, stateField));

        // __builder.SetException(ex)
        var setException = _module.ImportReference(
            builderClrType.GetMethod("SetException", [typeof(Exception)])!);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldflda, builderField));
        il.Append(il.Create(OpCodes.Ldloc, exLocal));
        il.Append(il.Create(OpCodes.Call, setException));

        il.Append(il.Create(OpCodes.Leave, exitLabel));

        // --- After try/catch ---
        il.Append(afterTry);

        // __state = -2
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, -2));
        il.Append(il.Create(OpCodes.Stfld, stateField));

        // __builder.SetResult(result)
        if (info.IsVoidReturn)
        {
            var setResult = _module.ImportReference(builderClrType.GetMethod("SetResult", Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldflda, builderField));
            il.Append(il.Create(OpCodes.Call, setResult));
        }
        else
        {
            var setResultMethod = builderClrType.GetMethod("SetResult",
                [IlTypeMapper.MapToClr(func.ReturnType)])!;
            var setResult = _module.ImportReference(setResultMethod);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldflda, builderField));
            il.Append(il.Create(OpCodes.Ldloc, resultLocal!));
            il.Append(il.Create(OpCodes.Call, setResult));
        }

        il.Append(exitLabel);
        il.Append(il.Create(OpCodes.Ret));

        // Register exception handler
        var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = afterTry,
            CatchType = _module.ImportReference(typeof(Exception))
        };
        moveNext.Body.ExceptionHandlers.Add(handler);

        _moveNextCtx = null;
    }

    private void EmitMoveNextAwait(IrNode.Await awaitNode, ILProcessor il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, VariableDefinition> locals)
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
            awaiterClrType = typeof(System.Runtime.CompilerServices.TaskAwaiter);
        else
            awaiterClrType = typeof(System.Runtime.CompilerServices.TaskAwaiter<>)
                .MakeGenericType(IlTypeMapper.MapToClr(resultType));

        // Declare a local for the awaiter
        var awaiterLocal = new VariableDefinition(_module.ImportReference(awaiterClrType));
        il.Body.Method.Body.Variables.Add(awaiterLocal);

        // Emit the task expression
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Call GetAwaiter()
        var taskClrType = IlTypeMapper.MapToClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        il.Append(il.Create(OpCodes.Call, _module.ImportReference(getAwaiterMethod)));
        il.Append(il.Create(OpCodes.Stloc, awaiterLocal));

        // Check IsCompleted
        var isCompletedGetter = awaiterClrType.GetProperty("IsCompleted")!.GetGetMethod()!;
        var completedLabel = il.Create(OpCodes.Nop);

        il.Append(il.Create(OpCodes.Ldloca, awaiterLocal));
        il.Append(il.Create(OpCodes.Call, _module.ImportReference(isCompletedGetter)));
        il.Append(il.Create(OpCodes.Brtrue, completedLabel));

        // --- Not completed: suspend ---

        // Set state
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4, stateNum));
        il.Append(il.Create(OpCodes.Stfld, ctx.StateField));
        il.Append(il.Create(OpCodes.Ldc_I4, stateNum));
        il.Append(il.Create(OpCodes.Stloc, ctx.StateLocal));

        // Store awaiter to field
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, awaiterLocal));
        il.Append(il.Create(OpCodes.Stfld, awaiterField));

        // Save all locals to fields
        foreach (var (name, local) in ctx.AllLocals)
        {
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldloc, local));
                il.Append(il.Create(OpCodes.Stfld, field));
            }
        }

        // Call __builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
        var awaitUnsafe = GetAwaitUnsafeOnCompletedRef(awaiterClrType, ctx);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldflda, ctx.BuilderField));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldflda, awaiterField));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, awaitUnsafe));

        // Leave try block (cannot use ret inside try)
        il.Append(il.Create(OpCodes.Leave, ctx.ExitLabel!));

        // --- Resume label (jump table target) ---
        il.Append(resumeLabel);

        // Restore awaiter from field
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, awaiterField));
        il.Append(il.Create(OpCodes.Stloc, awaiterLocal));

        // Clear awaiter field
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldflda, awaiterField));
        il.Append(il.Create(OpCodes.Initobj, _module.ImportReference(awaiterClrType)));

        // Reset state to -1
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Stloc, ctx.StateLocal));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Stfld, ctx.StateField));

        // Restore all locals from fields
        foreach (var (name, local) in ctx.AllLocals)
        {
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, field));
                il.Append(il.Create(OpCodes.Stloc, local));
            }
        }

        // --- Completed label (fast path + resume path converge) ---
        il.Append(completedLabel);

        // Call GetResult()
        var getResultMethod = awaiterClrType.GetMethod("GetResult", Type.EmptyTypes)!;
        il.Append(il.Create(OpCodes.Ldloca, awaiterLocal));
        il.Append(il.Create(OpCodes.Call, _module.ImportReference(getResultMethod)));

        // Result (T or void) is now on the stack
    }

    private MethodReference GetAwaitUnsafeOnCompletedRef(Type awaiterClrType, AsyncMoveNextContext ctx)
    {
        // Get the open generic method: AsyncTaskMethodBuilder<T>.AwaitUnsafeOnCompleted<TAwaiter, TSM>
        var builderType = ctx.BuilderField.FieldType;
        var openMethods = ctx.BuilderField.FieldType.Resolve().Methods;
        var awaitMethod = openMethods.FirstOrDefault(m => m.Name == "AwaitUnsafeOnCompleted");
        if (awaitMethod == null)
            throw new InvalidOperationException("AwaitUnsafeOnCompleted not found on builder type");

        var awaitMethodRef = _module.ImportReference(awaitMethod);

        // If the builder is generic (AsyncTaskMethodBuilder<T>), we need to resolve
        // AwaitUnsafeOnCompleted on the closed generic builder type
        if (builderType is GenericInstanceType git)
        {
            awaitMethodRef = new MethodReference(awaitMethod.Name, awaitMethod.ReturnType, git)
            {
                HasThis = awaitMethod.HasThis,
                ExplicitThis = awaitMethod.ExplicitThis,
                CallingConvention = awaitMethod.CallingConvention
            };
            foreach (var p in awaitMethod.Parameters)
                awaitMethodRef.Parameters.Add(new ParameterDefinition(p.ParameterType));
            foreach (var gp in awaitMethod.GenericParameters)
                awaitMethodRef.GenericParameters.Add(new GenericParameter(gp.Name, awaitMethodRef));
        }

        // Make the generic instance method with concrete type args
        var genericAwait = new GenericInstanceMethod(awaitMethodRef);
        genericAwait.GenericArguments.Add(_module.ImportReference(awaiterClrType));
        genericAwait.GenericArguments.Add(ctx.SmType);

        return genericAwait;
    }

    private void EmitSetStateMachineMethod(
        TypeDefinition smType,
        FieldDefinition builderField,
        Type builderClrType)
    {
        var setSmMethod = new MethodDefinition("SetStateMachine",
            MethodAttributes.Private | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            _module.TypeSystem.Void);
        setSmMethod.Parameters.Add(new ParameterDefinition("stateMachine",
            Mono.Cecil.ParameterAttributes.None,
            _module.ImportReference(typeof(IAsyncStateMachine))));
        smType.Methods.Add(setSmMethod);

        // Override IAsyncStateMachine.SetStateMachine
        var setSmIntf = _module.ImportReference(
            typeof(IAsyncStateMachine).GetMethod("SetStateMachine")!);
        setSmMethod.Overrides.Add(setSmIntf);

        var il = setSmMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ret));
    }

    /// <summary>
    /// Imports a delegate constructor with the correct Cecil generic type (e.g., Func&lt;T0,T1&gt; not Func&lt;object,object&gt;).
    /// </summary>
    private MethodReference ImportDelegateConstructor(ZType funcType)
    {
        var clrDelegateType = IlTypeMapper.MapToClr(funcType);
        var ctorInfo = clrDelegateType.GetConstructors()[0];
        var ctorRef = _module.ImportReference(ctorInfo);
        var cecilDelegateType = MapToClr(funcType);
        if (cecilDelegateType is GenericInstanceType git)
        {
            var memberRef = new MethodReference(".ctor", _module.TypeSystem.Void, git)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };
            memberRef.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object));
            memberRef.Parameters.Add(new ParameterDefinition(_module.TypeSystem.IntPtr));
            return memberRef;
        }
        return ctorRef;
    }

    /// <summary>
    /// Emits a Callvirt to delegate.Invoke() using the Cecil-aware type for the delegate,
    /// ensuring generic parameters are preserved (e.g., Func&lt;T0,T1&gt; instead of Func&lt;object,object&gt;).
    /// </summary>
    private void EmitDelegateInvoke(ZType funcType, ILProcessor il)
    {
        var cecilDelegateType = MapToClr(funcType);
        // Use IlTypeMapper to get the open Func<> type for reflection, then fix up with Cecil generics
        var clrDelegateType = IlTypeMapper.MapToClr(funcType);
        var invokeMethod = clrDelegateType.GetMethod("Invoke")!;
        il.Append(il.Create(OpCodes.Callvirt, ImportMethodWithGenericDeclaringType(invokeMethod, funcType)));
    }

    /// <summary>
    /// Imports a reflection MethodInfo, fixing up the declaring type to use the correct
    /// Cecil generic instance when the receiver type has generic parameters.
    /// </summary>
    private MethodReference ImportMethodWithGenericDeclaringType(System.Reflection.MethodInfo method, ZType receiverType)
    {
        var methodRef = _module.ImportReference(method);
        var cecilReceiverType = MapToClr(receiverType);
        if (cecilReceiverType is GenericInstanceType git)
        {
            var memberRef = new MethodReference(methodRef.Name, methodRef.ReturnType, git)
            {
                HasThis = methodRef.HasThis,
                ExplicitThis = methodRef.ExplicitThis,
                CallingConvention = methodRef.CallingConvention
            };
            foreach (var param in methodRef.Parameters)
                memberRef.Parameters.Add(new ParameterDefinition(param.ParameterType));
            return memberRef;
        }
        return methodRef;
    }

    /// <summary>
    /// Resolves a ZType to a CLR System.Type, checking user-defined types first.
    /// </summary>
    private Type ResolveClrType(ZType type)
    {
        if (type is ZType.ZNamedType named && _userTypes.TryGetValue(named.Name, out var typeRef))
        {
            var resolved = ResolveClrTypeForTypeRef(typeRef);
            if (resolved is not null)
                return resolved;
        }
        return IlTypeMapper.MapToClr(type);
    }

    /// <summary>
    /// Resolves a Cecil TypeReference to a CLR System.Type via reflection.
    /// </summary>
    private Type? ResolveClrTypeForTypeRef(TypeReference typeRef)
    {
        var fullName = typeRef.FullName;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullName);
            if (type is not null)
                return type;
        }
        return null;
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, Mono.Cecil.ICustomAttributeProvider target)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
        {
            var attrType = _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null) continue;
            var ctorInfo = attrType.GetConstructor(Type.EmptyTypes);
            if (ctorInfo is null) continue;
            var ctorRef = _module.ImportReference(ctorInfo);
            target.CustomAttributes.Add(new CustomAttribute(ctorRef));
        }
    }

    private void EmitClassDecl(IrNode.ClassDecl classDecl)
    {
        var classType = new TypeDefinition(_ilNamespace, Sanitize(classDecl.Name),
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            _module.TypeSystem.Object);
        _module.Types.Add(classType);

        EmitCustomAttributes(classDecl.Attributes, classType);

        // Define fields as properties with backing fields
        var fieldDefs = new List<(FieldDefinition Field, PropertyDefinition Prop)>();
        foreach (var field in classDecl.Fields)
        {
            var fieldType = MapToClr(field.Type);
            var fb = new FieldDefinition($"<{Sanitize(field.Name)}>k__BackingField",
                FieldAttributes.Private | FieldAttributes.InitOnly, fieldType);
            classType.Fields.Add(fb);

            var pb = new PropertyDefinition(Sanitize(field.Name), Mono.Cecil.PropertyAttributes.None, fieldType);
            var getter = new MethodDefinition($"get_{Sanitize(field.Name)}",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                fieldType);
            var gil = getter.Body.GetILProcessor();
            gil.Append(gil.Create(OpCodes.Ldarg_0));
            gil.Append(gil.Create(OpCodes.Ldfld, fb));
            gil.Append(gil.Create(OpCodes.Ret));
            classType.Methods.Add(getter);
            pb.GetMethod = getter;
            classType.Properties.Add(pb);
            fieldDefs.Add((fb, pb));
        }

        // Constructor with parameters
        var objCtorRef = _module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _module.TypeSystem.Void);
        for (int i = 0; i < classDecl.Fields.Count; i++)
            ctor.Parameters.Add(new ParameterDefinition(Sanitize(classDecl.Fields[i].Name),
                Mono.Cecil.ParameterAttributes.None, MapToClr(classDecl.Fields[i].Type)));
        var cil = ctor.Body.GetILProcessor();
        cil.Append(cil.Create(OpCodes.Ldarg_0));
        cil.Append(cil.Create(OpCodes.Call, objCtorRef));
        for (int i = 0; i < fieldDefs.Count; i++)
        {
            cil.Append(cil.Create(OpCodes.Ldarg_0));
            cil.Append(cil.Create(OpCodes.Ldarg, i + 1));
            cil.Append(cil.Create(OpCodes.Stfld, fieldDefs[i].Field));
        }
        cil.Append(cil.Create(OpCodes.Ret));
        classType.Methods.Add(ctor);

        // Parameterless constructor for test frameworks
        if (classDecl.Fields.Count > 0)
        {
            var defaultCtor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                _module.TypeSystem.Void);
            var dil = defaultCtor.Body.GetILProcessor();
            dil.Append(dil.Create(OpCodes.Ldarg_0));
            dil.Append(dil.Create(OpCodes.Call, objCtorRef));
            dil.Append(dil.Create(OpCodes.Ret));
            classType.Methods.Add(defaultCtor);
        }

        // Build field lookup for method bodies
        var classFieldMap = new Dictionary<string, FieldDefinition>();
        for (int i = 0; i < classDecl.Fields.Count; i++)
            classFieldMap[Sanitize(classDecl.Fields[i].Name)] = fieldDefs[i].Field;

        // Emit methods
        foreach (var method in classDecl.Methods)
        {
            var retType = method.ReturnType == ZType.Unit
                ? (TypeReference)_module.TypeSystem.Void
                : MapToClr(method.ReturnType);
            var mb = new MethodDefinition(Sanitize(method.Name),
                MethodAttributes.Public, retType);
            foreach (var p in method.Params)
                mb.Parameters.Add(new ParameterDefinition(p.Name,
                    Mono.Cecil.ParameterAttributes.None, MapToClr(p.Type)));
            classType.Methods.Add(mb);
            EmitCustomAttributes(method.Attributes, mb);

            var mil = mb.Body.GetILProcessor();
            var methodLocals = new Dictionary<string, VariableDefinition>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            var savedClassFields = _currentClassFields;
            var savedTypeDef = _currentTypeDefinition;
            _instanceArgOffset = 1;
            _currentFuncReturnType = method.ReturnType;
            _currentClassFields = classFieldMap;
            _currentTypeDefinition = classType;

            EmitNode(method.Body, mil, method.Params, methodLocals);

            _currentClassFields = savedClassFields;
            _instanceArgOffset = savedOffset;
            _currentFuncReturnType = savedReturnType;
            _currentTypeDefinition = savedTypeDef;

            if (method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            {
                if (method.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    mil.Append(mil.Create(OpCodes.Pop));
            }
            mil.Append(mil.Create(OpCodes.Ret));
        }
    }
}

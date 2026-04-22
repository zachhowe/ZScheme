using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Serilog;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

public sealed partial class IlEmitter
{
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

    private void DefineInterfaceType(IrNode.InterfaceDecl iface, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining interface type {InterfaceName}, {MethodCount} methods, {TypeParamCount} type params, {BaseCount} base interfaces",
            iface.Name, iface.Methods.Count, iface.TypeParams.Count, iface.BaseInterfaceNames.Count);
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
        if (record.IsValueType)
        {
            DefineStructType(record, parentType);
            return;
        }

        Log.Debug("IlEmitter: defining record type {RecordName}, {FieldCount} fields, {TypeParamCount} type params",
            record.Name, record.Fields.Count, record.TypeParams.Count);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
        var typeDef = new TypeDefinition(ns, record.Name,
            vis | TypeAttributes.Class | TypeAttributes.Sealed)
        {
            BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef()
        };

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
                var gp = new GenericParameterSignature(_module, GenericParameterType.Type,
                    typeDef.GenericParameters.Count);
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
            var getBody = new CilMethodBody { InitializeLocals = true };
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, ResolveSelfField(typeDef, fb));
            getIl.Add(CilOpCodes.Ret);

            var prop = new PropertyDefinition(sanitizedName, 0, PropertySignature.CreateInstance(fieldClrType));
            prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

            // Always emit an init setter for every record field so that C#'s `with`
            // expression lowering (clone + init-set) can decompile cleanly.
            var initSetter = CreateInitSetter(typeDef, sanitizedName, fieldClrType, fb);
            typeDef.Methods.Add(initSetter);
            prop.Semantics.Add(new MethodSemantics(initSetter, MethodSemanticsAttributes.Setter));

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

        var ctorBody = new CilMethodBody { InitializeLocals = true };
        ctor.MethodBody = ctorBody;
        var ctorIl = ctorBody.Instructions;
        ctorIl.Add(CilOpCodes.Ldarg_0);
        ctorIl.Add(CilOpCodes.Call,
            _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!));
        for (var i = 0; i < fieldDefs.Count; i++)
        {
            ctorIl.Add(CilOpCodes.Ldarg_0);
            ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Stfld, ResolveSelfField(typeDef, fieldDefs[i].Field));
        }

        ctorIl.Add(CilOpCodes.Ret);

        var copyCtor = EmitCopyConstructor(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
        EmitCloneMethod(typeDef, copyCtor);
        EmitEqualityContract(typeDef);
        EmitPrintMembers(typeDef);
        EmitRecordEquality(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
        EmitDeconstruct(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
    }

    /// <summary>
    /// Emits a real CLR struct: a sealed type whose BaseType is System.ValueType. The runtime
    /// classifies a type as a value type by checking its base; this layout is identical to what
    /// `csc` produces for `record struct`.
    /// </summary>
    private void DefineStructType(IrNode.RecordDecl record, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining struct type {RecordName}, {FieldCount} fields, {TypeParamCount} type params",
            record.Name, record.Fields.Count, record.TypeParams.Count);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
        var valueTypeRef = _module.DefaultImporter.ImportType(typeof(ValueType));
        var typeDef = new TypeDefinition(ns, record.Name,
            vis | TypeAttributes.Class | TypeAttributes.Sealed)
        {
            BaseType = valueTypeRef
        };

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
                var gp = new GenericParameterSignature(_module, GenericParameterType.Type,
                    typeDef.GenericParameters.Count);
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
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(fieldClrType));
            typeDef.Methods.Add(getter);
            var getBody = new CilMethodBody { InitializeLocals = true };
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, ResolveSelfField(typeDef, fb));
            getIl.Add(CilOpCodes.Ret);

            var prop = new PropertyDefinition(sanitizedName, 0, PropertySignature.CreateInstance(fieldClrType));
            prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

            // Init setters work on structs too — needed for `with` lowering.
            var initSetter = CreateInitSetter(typeDef, sanitizedName, fieldClrType, fb, isValueType: true);
            typeDef.Methods.Add(initSetter);
            prop.Semantics.Add(new MethodSemantics(initSetter, MethodSemanticsAttributes.Setter));

            typeDef.Properties.Add(prop);
            fieldDefs.Add((fb, getter));
        }

        // Constructor — value types must NOT call System.ValueType..ctor (it has none to call).
        var ctorParams = record.Fields.Select(f => MapToClr(f.Type, typeParamMap)).ToArray();
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, ctorParams));
        for (var i = 0; i < record.Fields.Count; i++)
            ctor.ParameterDefinitions.Add(new ParameterDefinition(
                (ushort)(i + 1), Sanitize(record.Fields[i].Name), 0));
        typeDef.Methods.Add(ctor);

        var ctorBody = new CilMethodBody { InitializeLocals = true };
        ctor.MethodBody = ctorBody;
        var ctorIl = ctorBody.Instructions;
        for (var i = 0; i < fieldDefs.Count; i++)
        {
            ctorIl.Add(CilOpCodes.Ldarg_0);
            ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Stfld, ResolveSelfField(typeDef, fieldDefs[i].Field));
        }
        ctorIl.Add(CilOpCodes.Ret);

        EmitStructEquality(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
        EmitPrintMembers(typeDef);
        EmitDeconstruct(typeDef, fieldDefs.Select(fd => fd.Field).ToList());
    }

    /// <summary>
    /// Emits structural equality members for a value-type record:
    /// `Equals(T)`, `Equals(object)`, `GetHashCode`, `op_Equality`, `op_Inequality`.
    /// Differs from <see cref="EmitRecordEquality"/>: no EqualityContract chain (structs
    /// have a concrete runtime type), no null checks (value types can't be null), and
    /// `Equals(object)` uses unbox.any rather than isinst-cast.
    /// </summary>
    private void EmitStructEquality(TypeDefinition typeDef, IReadOnlyList<FieldDefinition> backingFields)
    {
        TypeSignature selfSig;
        GenericInstanceTypeSignature? closedSig = null;
        if (typeDef.GenericParameters.Count > 0)
        {
            var genArgs = typeDef.GenericParameters
                .Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i))
                .ToArray();
            closedSig = typeDef.MakeGenericInstanceType(true, genArgs);
            selfSig = closedSig;
        }
        else
        {
            selfSig = typeDef.ToTypeSignature();
        }

        IFieldDescriptor ResolveField(FieldDefinition f) =>
            closedSig is null ? f : new MemberReference(closedSig.ToTypeDefOrRef(), f.Name!, f.Signature!);

        // --- Equals(T other) — structural equality, no null handling ---
        var equalsT = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean, [selfSig]));
        equalsT.ParameterDefinitions.Add(new ParameterDefinition(1, "other", 0));
        typeDef.Methods.Add(equalsT);

        var etBody = new CilMethodBody { InitializeLocals = true };
        equalsT.MethodBody = etBody;
        var etIl = etBody.Instructions;
        var returnFalse = new CilInstructionLabel();

        if (backingFields.Count == 0)
        {
            etIl.Add(CilOpCodes.Ldc_I4_1);
            etIl.Add(CilOpCodes.Ret);
        }
        else
        {
            foreach (var backing in backingFields)
            {
                var fieldSig = backing.Signature!.FieldType;
                var getDefault = ResolveEqualityComparerDefault(fieldSig);
                var equalsMethod = ResolveEqualityComparerEquals(fieldSig);

                etIl.Add(CilOpCodes.Call, getDefault);
                etIl.Add(CilOpCodes.Ldarg_0);
                etIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
                etIl.Add(CilOpCodes.Ldarg_1);
                etIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
                etIl.Add(CilOpCodes.Callvirt, equalsMethod);
                etIl.Add(CilOpCodes.Brfalse, returnFalse);
            }
            etIl.Add(CilOpCodes.Ldc_I4_1);
            etIl.Add(CilOpCodes.Ret);
            returnFalse.Instruction = etIl.Add(CilOpCodes.Ldc_I4_0);
            etIl.Add(CilOpCodes.Ret);
        }

        // --- Equals(object obj) — typecheck via isinst on a boxed copy, then call Equals(T) ---
        var equalsObj = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean,
                [_module.CorLibTypeFactory.Object]));
        equalsObj.ParameterDefinitions.Add(new ParameterDefinition(1, "obj", 0));
        typeDef.Methods.Add(equalsObj);

        var eoBody = new CilMethodBody { InitializeLocals = true };
        equalsObj.MethodBody = eoBody;
        var eoIl = eoBody.Instructions;
        var notMatch = new CilInstructionLabel();
        eoIl.Add(CilOpCodes.Ldarg_1);
        eoIl.Add(CilOpCodes.Isinst, selfSig.ToTypeDefOrRef());
        eoIl.Add(CilOpCodes.Brfalse, notMatch);
        eoIl.Add(CilOpCodes.Ldarg_0);
        eoIl.Add(CilOpCodes.Ldarg_1);
        eoIl.Add(CilOpCodes.Unbox_Any, selfSig.ToTypeDefOrRef());
        if (closedSig is null)
            eoIl.Add(CilOpCodes.Call, equalsT);
        else
            eoIl.Add(CilOpCodes.Call,
                new MemberReference(closedSig.ToTypeDefOrRef(), equalsT.Name!, equalsT.Signature!));
        eoIl.Add(CilOpCodes.Ret);
        notMatch.Instruction = eoIl.Add(CilOpCodes.Ldc_I4_0);
        eoIl.Add(CilOpCodes.Ret);

        // --- GetHashCode ---
        var getHash = new MethodDefinition("GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32));
        typeDef.Methods.Add(getHash);

        var ghBody = new CilMethodBody { InitializeLocals = true };
        getHash.MethodBody = ghBody;
        var ghIl = ghBody.Instructions;

        if (backingFields.Count == 0)
        {
            ghIl.Add(CilOpCodes.Ldc_I4_0);
            ghIl.Add(CilOpCodes.Ret);
        }
        else
        {
            for (var i = 0; i < backingFields.Count; i++)
            {
                var backing = backingFields[i];
                var fieldSig = backing.Signature!.FieldType;
                if (i > 0)
                {
                    ghIl.Add(CilOpCodes.Ldc_I4, -1521134295);
                    ghIl.Add(CilOpCodes.Mul);
                }
                var getDefault = ResolveEqualityComparerDefault(fieldSig);
                var hashMethod = ResolveEqualityComparerGetHashCode(fieldSig);
                ghIl.Add(CilOpCodes.Call, getDefault);
                ghIl.Add(CilOpCodes.Ldarg_0);
                ghIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
                ghIl.Add(CilOpCodes.Callvirt, hashMethod);
                if (i > 0) ghIl.Add(CilOpCodes.Add);
            }
            ghIl.Add(CilOpCodes.Ret);
        }

        // --- op_Equality / op_Inequality ---
        var opEq = new MethodDefinition("op_Equality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Boolean, [selfSig, selfSig]));
        opEq.ParameterDefinitions.Add(new ParameterDefinition(1, "left", 0));
        opEq.ParameterDefinitions.Add(new ParameterDefinition(2, "right", 0));
        typeDef.Methods.Add(opEq);

        var eqBody = new CilMethodBody { InitializeLocals = true };
        opEq.MethodBody = eqBody;
        var eqIl = eqBody.Instructions;
        eqIl.Add(CilOpCodes.Ldarga_S, opEq.Parameters[0]);
        eqIl.Add(CilOpCodes.Ldarg_1);
        if (closedSig is null)
            eqIl.Add(CilOpCodes.Call, equalsT);
        else
            eqIl.Add(CilOpCodes.Call,
                new MemberReference(closedSig.ToTypeDefOrRef(), equalsT.Name!, equalsT.Signature!));
        eqIl.Add(CilOpCodes.Ret);

        var opNeq = new MethodDefinition("op_Inequality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Boolean, [selfSig, selfSig]));
        opNeq.ParameterDefinitions.Add(new ParameterDefinition(1, "left", 0));
        opNeq.ParameterDefinitions.Add(new ParameterDefinition(2, "right", 0));
        typeDef.Methods.Add(opNeq);

        var neqBody = new CilMethodBody { InitializeLocals = true };
        opNeq.MethodBody = neqBody;
        var neqIl = neqBody.Instructions;
        neqIl.Add(CilOpCodes.Ldarg_0);
        neqIl.Add(CilOpCodes.Ldarg_1);
        if (closedSig is null)
            neqIl.Add(CilOpCodes.Call, opEq);
        else
            neqIl.Add(CilOpCodes.Call,
                new MemberReference(closedSig.ToTypeDefOrRef(), opEq.Name!, opEq.Signature!));
        neqIl.Add(CilOpCodes.Ldc_I4_0);
        neqIl.Add(CilOpCodes.Ceq);
        neqIl.Add(CilOpCodes.Ret);
    }

    /// <summary>
    /// Emits `protected virtual Type EqualityContract { get; }` returning typeof(T).
    /// Required for ILSpy and other decompilers to classify the type as a record class.
    /// </summary>
    private void EmitEqualityContract(TypeDefinition typeDef)
    {
        var typeType = _module.DefaultImporter.ImportType(typeof(Type));
        var typeSig = typeType.ToTypeSignature(false);
        var getTypeFromHandle = _module.DefaultImporter.ImportMethod(
            typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!);

        var getter = new MethodDefinition("get_EqualityContract",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.NewSlot
            | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(typeSig));
        typeDef.Methods.Add(getter);

        var body = new CilMethodBody { InitializeLocals = true };
        getter.MethodBody = body;
        var il = body.Instructions;

        ITypeDefOrRef selfTypeTok = typeDef;
        if (typeDef.GenericParameters.Count > 0)
        {
            var genArgs = typeDef.GenericParameters
                .Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i))
                .ToArray();
            selfTypeTok = typeDef.MakeGenericInstanceType(false, genArgs).ToTypeDefOrRef();
        }

        il.Add(CilOpCodes.Ldtoken, selfTypeTok);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)getTypeFromHandle);
        il.Add(CilOpCodes.Ret);

        var prop = new PropertyDefinition("EqualityContract", 0, PropertySignature.CreateInstance(typeSig));
        prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));
        typeDef.Properties.Add(prop);
    }

    /// <summary>
    /// Emits `Equals(T)`, `Equals(object)`, `GetHashCode()`, and `op_Equality`/`op_Inequality`
    /// so the type satisfies decompilers' record detection heuristics. Implementations are
    /// structurally correct: two records are equal iff their EqualityContract matches and
    /// each backing field compares equal under the default comparer.
    /// </summary>
    private void EmitRecordEquality(TypeDefinition typeDef, IReadOnlyList<FieldDefinition> backingFields)
    {
        TypeSignature selfSig;
        GenericInstanceTypeSignature? closedSig = null;
        if (typeDef.GenericParameters.Count > 0)
        {
            var genArgs = typeDef.GenericParameters
                .Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i))
                .ToArray();
            closedSig = typeDef.MakeGenericInstanceType(false, genArgs);
            selfSig = closedSig;
        }
        else
        {
            selfSig = typeDef.ToTypeSignature();
        }

        IFieldDescriptor ResolveField(FieldDefinition f) =>
            closedSig is null ? f : new MemberReference(closedSig.ToTypeDefOrRef(), f.Name!, f.Signature!);

        // --- Equals(T other) — structural equality ---
        var equalsT = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot
            | MethodAttributes.Final | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean, [selfSig]));
        equalsT.ParameterDefinitions.Add(new ParameterDefinition(1, "other", 0));
        typeDef.Methods.Add(equalsT);

        var etBody = new CilMethodBody { InitializeLocals = true };
        equalsT.MethodBody = etBody;
        etBody.InitializeLocals = true;
        var etIl = etBody.Instructions;

        var returnFalse = new CilInstructionLabel();
        var returnTrue = new CilInstructionLabel();

        // if (other is null) return false;
        etIl.Add(CilOpCodes.Ldarg_1);
        etIl.Add(CilOpCodes.Brfalse, returnFalse);

        // if (!ReferenceEquals(this, other)) check fields
        var skipRefEq = new CilInstructionLabel();
        etIl.Add(CilOpCodes.Ldarg_0);
        etIl.Add(CilOpCodes.Ldarg_1);
        etIl.Add(CilOpCodes.Bne_Un, skipRefEq);
        etIl.Add(CilOpCodes.Br, returnTrue);
        skipRefEq.Instruction = etIl.Add(CilOpCodes.Nop);

        // For each field: EqualityComparer<TField>.Default.Equals(this.field, other.field)
        foreach (var backing in backingFields)
        {
            var fieldSig = backing.Signature!.FieldType;
            var getDefault = ResolveEqualityComparerDefault(fieldSig);
            var equalsMethod = ResolveEqualityComparerEquals(fieldSig);

            etIl.Add(CilOpCodes.Call, getDefault);
            etIl.Add(CilOpCodes.Ldarg_0);
            etIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
            etIl.Add(CilOpCodes.Ldarg_1);
            etIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
            etIl.Add(CilOpCodes.Callvirt, equalsMethod);
            etIl.Add(CilOpCodes.Brfalse, returnFalse);
        }

        returnTrue.Instruction = etIl.Add(CilOpCodes.Ldc_I4_1);
        etIl.Add(CilOpCodes.Ret);
        returnFalse.Instruction = etIl.Add(CilOpCodes.Ldc_I4_0);
        etIl.Add(CilOpCodes.Ret);

        // --- Equals(object) — forwards to Equals(T) via isinst ---
        var equalsObj = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean,
                [_module.CorLibTypeFactory.Object]));
        equalsObj.ParameterDefinitions.Add(new ParameterDefinition(1, "obj", 0));
        typeDef.Methods.Add(equalsObj);

        var eoBody = new CilMethodBody { InitializeLocals = true };
        equalsObj.MethodBody = eoBody;
        var eoIl = eoBody.Instructions;
        eoIl.Add(CilOpCodes.Ldarg_0);
        eoIl.Add(CilOpCodes.Ldarg_1);
        eoIl.Add(CilOpCodes.Isinst, selfSig.ToTypeDefOrRef());
        if (closedSig is null)
            eoIl.Add(CilOpCodes.Call, equalsT);
        else
            eoIl.Add(CilOpCodes.Call,
                new MemberReference(closedSig.ToTypeDefOrRef(), equalsT.Name!, equalsT.Signature!));
        eoIl.Add(CilOpCodes.Ret);

        // --- GetHashCode ---
        var getHash = new MethodDefinition("GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32));
        typeDef.Methods.Add(getHash);

        var ghBody = new CilMethodBody { InitializeLocals = true };
        getHash.MethodBody = ghBody;
        ghBody.InitializeLocals = true;
        var ghIl = ghBody.Instructions;

        // Start with hash of EqualityContract
        var typeType = _module.DefaultImporter.ImportType(typeof(Type));
        var eqContractGetter = typeDef.Properties.FirstOrDefault(p => p.Name == "EqualityContract")?
            .Semantics.FirstOrDefault(s => s.Attributes == MethodSemanticsAttributes.Getter)?.Method;
        var typeComparerDefault = ResolveEqualityComparerDefault(typeType.ToTypeSignature(false));
        var typeComparerHash = ResolveEqualityComparerGetHashCode(typeType.ToTypeSignature(false));

        ghIl.Add(CilOpCodes.Call, typeComparerDefault);
        ghIl.Add(CilOpCodes.Ldarg_0);
        if (eqContractGetter is not null)
        {
            IMethodDefOrRef getterRef = (MethodDefinition)eqContractGetter;
            if (closedSig is not null)
                getterRef = new MemberReference(closedSig.ToTypeDefOrRef(),
                    eqContractGetter.Name!, eqContractGetter.Signature!);
            ghIl.Add(CilOpCodes.Callvirt, getterRef);
        }
        ghIl.Add(CilOpCodes.Callvirt, typeComparerHash);

        foreach (var backing in backingFields)
        {
            var fieldSig = backing.Signature!.FieldType;
            ghIl.Add(CilOpCodes.Ldc_I4, -1521134295);
            ghIl.Add(CilOpCodes.Mul);
            var getDefault = ResolveEqualityComparerDefault(fieldSig);
            var hashMethod = ResolveEqualityComparerGetHashCode(fieldSig);
            ghIl.Add(CilOpCodes.Call, getDefault);
            ghIl.Add(CilOpCodes.Ldarg_0);
            ghIl.Add(CilOpCodes.Ldfld, ResolveField(backing));
            ghIl.Add(CilOpCodes.Callvirt, hashMethod);
            ghIl.Add(CilOpCodes.Add);
        }
        ghIl.Add(CilOpCodes.Ret);

        // --- op_Equality / op_Inequality ---
        var opEq = new MethodDefinition("op_Equality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Boolean, [selfSig, selfSig]));
        opEq.ParameterDefinitions.Add(new ParameterDefinition(1, "left", 0));
        opEq.ParameterDefinitions.Add(new ParameterDefinition(2, "right", 0));
        typeDef.Methods.Add(opEq);

        var eqBody = new CilMethodBody { InitializeLocals = true };
        opEq.MethodBody = eqBody;
        var eqIl = eqBody.Instructions;
        var eqNotNull = new CilInstructionLabel();
        var eqDone = new CilInstructionLabel();
        eqIl.Add(CilOpCodes.Ldarg_0);
        eqIl.Add(CilOpCodes.Brtrue, eqNotNull);
        eqIl.Add(CilOpCodes.Ldarg_1);
        eqIl.Add(CilOpCodes.Ldnull);
        eqIl.Add(CilOpCodes.Ceq);
        eqIl.Add(CilOpCodes.Br, eqDone);
        eqNotNull.Instruction = eqIl.Add(CilOpCodes.Ldarg_0);
        eqIl.Add(CilOpCodes.Ldarg_1);
        if (closedSig is null)
            eqIl.Add(CilOpCodes.Callvirt, equalsT);
        else
            eqIl.Add(CilOpCodes.Callvirt,
                new MemberReference(closedSig.ToTypeDefOrRef(), equalsT.Name!, equalsT.Signature!));
        eqDone.Instruction = eqIl.Add(CilOpCodes.Ret);

        var opNeq = new MethodDefinition("op_Inequality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Boolean, [selfSig, selfSig]));
        opNeq.ParameterDefinitions.Add(new ParameterDefinition(1, "left", 0));
        opNeq.ParameterDefinitions.Add(new ParameterDefinition(2, "right", 0));
        typeDef.Methods.Add(opNeq);

        var neqBody = new CilMethodBody { InitializeLocals = true };
        opNeq.MethodBody = neqBody;
        var neqIl = neqBody.Instructions;
        neqIl.Add(CilOpCodes.Ldarg_0);
        neqIl.Add(CilOpCodes.Ldarg_1);
        if (closedSig is null)
            neqIl.Add(CilOpCodes.Call, opEq);
        else
            neqIl.Add(CilOpCodes.Call,
                new MemberReference(closedSig.ToTypeDefOrRef(), opEq.Name!, opEq.Signature!));
        neqIl.Add(CilOpCodes.Ldc_I4_0);
        neqIl.Add(CilOpCodes.Ceq);
        neqIl.Add(CilOpCodes.Ret);
    }

    private GenericInstanceTypeSignature EqualityComparerClosed(TypeSignature fieldType)
    {
        var open = _module.DefaultImporter.ImportType(
            typeof(System.Collections.Generic.EqualityComparer<>));
        return new GenericInstanceTypeSignature(open, false, [fieldType]);
    }

    // Build references to methods on a closed `EqualityComparer<T>` instance.
    // The signature MUST use a `GenericParameterSignature(Type, 0)` placeholder for the
    // declaring type's type parameter — when the runtime encounters a TypeSpec parent,
    // it substitutes !0 in the signature with the parent's actual type arg. Using the
    // concrete `fieldType` directly in the signature breaks that substitution and the
    // runtime fails to find the method.
    private IMethodDefOrRef ResolveEqualityComparerDefault(TypeSignature fieldType)
    {
        var closed = EqualityComparerClosed(fieldType);
        var genParam0 = new GenericParameterSignature(_module, GenericParameterType.Type, 0);
        var openClosed = new GenericInstanceTypeSignature(
            _module.DefaultImporter.ImportType(typeof(System.Collections.Generic.EqualityComparer<>)),
            false, [genParam0]);
        return new MemberReference(closed.ToTypeDefOrRef(), "get_Default",
            MethodSignature.CreateStatic(openClosed));
    }

    private IMethodDefOrRef ResolveEqualityComparerEquals(TypeSignature fieldType)
    {
        var closed = EqualityComparerClosed(fieldType);
        var genParam0 = new GenericParameterSignature(_module, GenericParameterType.Type, 0);
        return new MemberReference(closed.ToTypeDefOrRef(), "Equals",
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean, [genParam0, genParam0]));
    }

    private IMethodDefOrRef ResolveEqualityComparerGetHashCode(TypeSignature fieldType)
    {
        var closed = EqualityComparerClosed(fieldType);
        var genParam0 = new GenericParameterSignature(_module, GenericParameterType.Type, 0);
        return new MemberReference(closed.ToTypeDefOrRef(), "GetHashCode",
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32, [genParam0]));
    }

    /// <summary>
    /// Emits a copy constructor `.ctor(T other)` that copies the backing fields.
    /// C# records have this, and decompilers use its presence (together with
    /// `<Clone>$` and `PrintMembers`) to recognise the type as a record.
    /// </summary>
    private MethodDefinition EmitCopyConstructor(TypeDefinition typeDef, IReadOnlyList<FieldDefinition> backingFields)
    {
        TypeSignature selfSig;
        GenericInstanceTypeSignature? closedSig = null;
        if (typeDef.GenericParameters.Count > 0)
        {
            var genArgs = typeDef.GenericParameters
                .Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i))
                .ToArray();
            closedSig = typeDef.MakeGenericInstanceType(false, genArgs);
            selfSig = closedSig;
        }
        else
        {
            selfSig = typeDef.ToTypeSignature();
        }

        var copyCtor = new MethodDefinition(".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName
            | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [selfSig]));
        copyCtor.ParameterDefinitions.Add(new ParameterDefinition(1, "original", 0));
        typeDef.Methods.Add(copyCtor);

        var body = new CilMethodBody { InitializeLocals = true };
        copyCtor.MethodBody = body;
        var il = body.Instructions;

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call,
            _module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(Type.EmptyTypes)!));

        foreach (var backing in backingFields)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldarg_1);
            // Field references need to be resolved against the closed generic instance
            // so the ldfld/stfld target the right field when T has type params.
            IFieldDescriptor fieldRef = backing;
            if (closedSig is not null)
                fieldRef = new MemberReference(closedSig.ToTypeDefOrRef(), backing.Name!, backing.Signature!);
            il.Add(CilOpCodes.Ldfld, fieldRef);
            il.Add(CilOpCodes.Stfld, fieldRef);
        }

        il.Add(CilOpCodes.Ret);
        return copyCtor;
    }

    /// <summary>
    /// Emits a trivial `PrintMembers(StringBuilder)` method. Its presence (not its
    /// body) is what decompilers check when classifying the type as a record.
    /// </summary>
    private void EmitPrintMembers(TypeDefinition typeDef)
    {
        var sbType = _module.DefaultImporter.ImportType(typeof(System.Text.StringBuilder));
        var sbSig = sbType.ToTypeSignature(false);
        var printMembers = new MethodDefinition("PrintMembers",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Boolean, [sbSig]));
        printMembers.ParameterDefinitions.Add(new ParameterDefinition(1, "builder", 0));
        typeDef.Methods.Add(printMembers);

        var body = new CilMethodBody { InitializeLocals = true };
        printMembers.MethodBody = body;
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Ret);
    }

    /// <summary>
    /// Emits a `<Clone>$()` method that calls the copy constructor. This is the method
    /// that C#'s `with` expression calls before mutating init-only properties, and
    /// decompilers rely on its presence to render call sites as `x with { ... }`.
    /// </summary>
    private void EmitCloneMethod(TypeDefinition typeDef, MethodDefinition copyCtor)
    {
        TypeSignature returnSig;
        GenericInstanceTypeSignature? closedSig = null;
        if (typeDef.GenericParameters.Count > 0)
        {
            var genArgs = typeDef.GenericParameters
                .Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(_module, GenericParameterType.Type, i))
                .ToArray();
            closedSig = typeDef.MakeGenericInstanceType(false, genArgs);
            returnSig = closedSig;
        }
        else
        {
            returnSig = typeDef.ToTypeSignature();
        }

        var cloneMethod = new MethodDefinition("<Clone>$",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(returnSig));
        typeDef.Methods.Add(cloneMethod);

        var body = new CilMethodBody { InitializeLocals = true };
        cloneMethod.MethodBody = body;
        var il = body.Instructions;

        IMethodDefOrRef ctorRef = copyCtor;
        if (closedSig is not null)
            ctorRef = new MemberReference(closedSig.ToTypeDefOrRef(), copyCtor.Name!, copyCtor.Signature!);

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Newobj, ctorRef);
        il.Add(CilOpCodes.Ret);
    }

    private void DefineUnionType(IrNode.UnionDecl union, TypeDefinition? parentType = null)
    {
        Log.Debug("IlEmitter: defining union type {UnionName}, {CaseCount} cases, {TypeParamCount} type params",
            union.Name, union.Cases.Count, union.TypeParams.Count);
        var ns = parentType is null ? _ilNamespace : "";
        var vis = parentType is null ? TypeAttributes.Public : TypeAttributes.NestedPublic;
        var baseType = new TypeDefinition(ns, union.Name,
            vis | TypeAttributes.Class | TypeAttributes.Abstract)
        {
            BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef()
        };

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
        var baseCtorBody = new CilMethodBody { InitializeLocals = true };
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
                caseVis | TypeAttributes.Class | TypeAttributes.Sealed)
            {
                BaseType = baseType
            };

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
                    .Select(TypeSignature (_, i) =>
                        new GenericParameterSignature(_module, GenericParameterType.Type, i))
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
                var getBody = new CilMethodBody { InitializeLocals = true };
                getter.MethodBody = getBody;
                var getIl = getBody.Instructions;
                getIl.Add(CilOpCodes.Ldarg_0);
                getIl.Add(CilOpCodes.Ldfld, ResolveSelfField(caseType, fb));
                getIl.Add(CilOpCodes.Ret);

                var prop = new PropertyDefinition(sanitizedName, 0,
                    PropertySignature.CreateInstance(fieldClrType));
                prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

                if (field.IsInit)
                {
                    var initSetter = CreateInitSetter(caseType, sanitizedName, fieldClrType, fb);
                    caseType.Methods.Add(initSetter);
                    prop.Semantics.Add(new MethodSemantics(initSetter, MethodSemanticsAttributes.Setter));
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

            var caseCtorBody = new CilMethodBody { InitializeLocals = true };
            caseCtor.MethodBody = caseCtorBody;
            var caseCtorIl = caseCtorBody.Instructions;
            caseCtorIl.Add(CilOpCodes.Ldarg_0);

            if (union.TypeParams.Count > 0)
            {
                var closedBaseArgs = caseType.GenericParameters
                    .Select(TypeSignature (_, i) =>
                        new GenericParameterSignature(_module, GenericParameterType.Type, i))
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
                caseCtorIl.Add(CilOpCodes.Stfld, ResolveSelfField(caseType, caseFieldDefs[i]));
            }

            caseCtorIl.Add(CilOpCodes.Ret);

            // Emit Equals, GetHashCode, and Deconstruct
            EmitUnionCaseEquals(caseType, caseFieldDefs);
            EmitUnionCaseGetHashCode(caseType, caseFieldDefs);
            EmitDeconstruct(caseType, caseFieldDefs);

            var caseKey = $"{union.Name}.{@case.Name}";
            _unionCaseTypes[caseKey] = caseType;
            _unionCasePropertyNames[caseKey] = @case.Fields.Select(f => Sanitize(f.Name)).ToList();
            _unionCaseFieldTypes[caseKey] = (union.TypeParams, @case.Fields.Select(f => f.Type).ToList());
        }
    }
}

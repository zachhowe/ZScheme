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
            var getBody = new CilMethodBody();
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, fb);
            getIl.Add(CilOpCodes.Ret);

            var prop = new PropertyDefinition(sanitizedName, 0, PropertySignature.CreateInstance(fieldClrType));
            prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

            if (field.IsInit)
            {
                var initSetter = CreateInitSetter(sanitizedName, fieldClrType, fb);
                typeDef.Methods.Add(initSetter);
                prop.Semantics.Add(new MethodSemantics(initSetter, MethodSemanticsAttributes.Setter));
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
                var getBody = new CilMethodBody();
                getter.MethodBody = getBody;
                var getIl = getBody.Instructions;
                getIl.Add(CilOpCodes.Ldarg_0);
                getIl.Add(CilOpCodes.Ldfld, fb);
                getIl.Add(CilOpCodes.Ret);

                var prop = new PropertyDefinition(sanitizedName, 0,
                    PropertySignature.CreateInstance(fieldClrType));
                prop.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

                if (field.IsInit)
                {
                    var initSetter = CreateInitSetter(sanitizedName, fieldClrType, fb);
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

            var caseCtorBody = new CilMethodBody();
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
}

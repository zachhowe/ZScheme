using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to AsmResolver <see cref="TypeSignature" /> instances. A thin facade over
///     the shared <see cref="TypeMapperCore" /> traversal, parameterised with an
///     <see cref="AsmResolverTypeFactory" /> so it stays behaviourally in lockstep with
///     <see cref="IlTypeMapper" />.
/// </summary>
public static class AsmResolverTypeMapper
{
    public static TypeSignature MapReturnTypeToClr(
        ZType type,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
    {
        return type == ZType.Unit
            ? module.CorLibTypeFactory.Void
            : MapToClr(
                type,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            );
    }

    public static TypeSignature MapToClr(
        ZType type,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
    {
        return TypeMapperCore.Map(
            type,
            new AsmResolverTypeFactory(module, unitType),
            userTypes,
            typeParamMap,
            typeVarMap,
            typeAliases,
            clrInterop
        );
    }
}

/// <summary>
///     <see cref="ITypeFactory{T}" /> that constructs AsmResolver <see cref="TypeSignature" />
///     instances against a target <see cref="ModuleDefinition" />.
/// </summary>
internal sealed class AsmResolverTypeFactory(ModuleDefinition module, TypeSignature unitType)
    : ITypeFactory<TypeSignature>
{
    public TypeSignature Object => module.CorLibTypeFactory.Object;

    public TypeSignature Primitive(PrimitiveKind kind)
    {
        return kind switch
        {
            PrimitiveKind.Int => module.CorLibTypeFactory.Int32,
            PrimitiveKind.Long => module.CorLibTypeFactory.Int64,
            PrimitiveKind.Float => module.CorLibTypeFactory.Single,
            PrimitiveKind.Double => module.CorLibTypeFactory.Double,
            PrimitiveKind.Byte => module.CorLibTypeFactory.Byte,
            PrimitiveKind.Char => module.CorLibTypeFactory.Char,
            PrimitiveKind.Bool => module.CorLibTypeFactory.Boolean,
            PrimitiveKind.String => module.CorLibTypeFactory.String,
            PrimitiveKind.Unit => unitType,
            _ => module.CorLibTypeFactory.Object,
        };
    }

    public bool IsValueType(TypeSignature t)
    {
        return t.IsValueType;
    }

    public bool IsGenericDefinition(TypeSignature t)
    {
        // A user type carrying generic parameters is stored as a TypeDefinition-backed signature,
        // which we can introspect directly. For anything else (e.g. an imported TypeReference whose
        // arity we can't see cheaply) assume it is closeable — preserving the historical AsmResolver
        // behaviour of closing whenever type arguments are present.
        if (t is TypeDefOrRefSignature { Type: TypeDefinition td })
            return td.GenericParameters.Count > 0;
        return true;
    }

    public TypeSignature MakeArray(TypeSignature element)
    {
        return new SzArrayTypeSignature(element);
    }

    public TypeSignature FromClrType(Type clrType, bool corLibAware)
    {
        var imported = corLibAware
            ? ImportTypeCorLibAware(clrType)
            : module.DefaultImporter.ImportType(clrType);
        return imported.ToTypeSignature(clrType.IsValueType);
    }

    public TypeSignature CloseClrGeneric(Type openClrType, TypeSignature[] args)
    {
        return ImportTypeCorLibAware(openClrType)
            .ToTypeSignature(openClrType.IsValueType)
            .MakeGenericInstanceType(openClrType.IsValueType, args);
    }

    public TypeSignature CloseMappedGeneric(TypeSignature openMapped, TypeSignature[] args)
    {
        return openMapped
            .ToTypeDefOrRef()
            .ToTypeSignature(openMapped.IsValueType)
            .MakeGenericInstanceType(openMapped.IsValueType, args);
    }

    public void Warn(string message)
    {
        // No diagnostics surface on the IL backend; the reflection backend reports these.
    }

    /// <summary>
    ///     Imports a CLR type, routing corlib types (Func, Action, Task, etc.) through the module's
    ///     configured corlib scope instead of System.Private.CoreLib.
    /// </summary>
    private ITypeDefOrRef ImportTypeCorLibAware(Type clrType)
    {
        var imported = module.DefaultImporter.ImportType(clrType);
        var asmName = clrType.Assembly.GetName().Name;
        // Only reroute types that are actually forwarded through System.Runtime (the corlib scope).
        // Types in System.Collections.Generic (List<T>, Dictionary<K,V>, etc.) are forwarded
        // through System.Collections, not System.Runtime, so they must keep their original scope.
        // Types in System.Collections.Concurrent are forwarded through
        // System.Collections.Concurrent, not System.Runtime, so they must also keep their scope.
        // Exception: KeyValuePair<,> is in System.Collections.Generic but forwarded through
        // System.Runtime, so it must be rerouted.
        if (
            asmName is "System.Private.CoreLib" or "mscorlib"
            && clrType.Namespace is not "System.Collections.Concurrent"
            && (
                clrType.Namespace is not "System.Collections.Generic"
                || clrType.Name.StartsWith("KeyValuePair")
            )
            && imported is TypeReference tr
        )
            tr.Scope = module.CorLibTypeFactory.CorLibScope;
        return imported;
    }
}

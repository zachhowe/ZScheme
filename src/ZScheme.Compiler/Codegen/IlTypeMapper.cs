using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to CLR <see cref="Type" /> instances. A thin facade over the shared
///     <see cref="TypeMapperCore" /> traversal, parameterised with a <see cref="ReflectionTypeFactory" />
///     so it stays behaviourally in lockstep with <see cref="AsmResolverTypeMapper" />.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapToClr(
        ZType type,
        DiagnosticBag? diagnostics = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
    {
        return TypeMapperCore.Map(
            type,
            new ReflectionTypeFactory(diagnostics),
            null,
            null,
            null,
            typeAliases,
            clrInterop
        );
    }

    public static Type MapToClr(
        ZType type,
        IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null,
        IReadOnlyDictionary<int, Type>? typeVarMap = null,
        DiagnosticBag? diagnostics = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
    {
        return TypeMapperCore.Map(
            type,
            new ReflectionTypeFactory(diagnostics),
            userTypes,
            typeParamMap,
            typeVarMap,
            typeAliases,
            clrInterop
        );
    }
}

/// <summary>
///     <see cref="ITypeFactory{T}" /> that constructs reflection <see cref="Type" /> instances.
///     The <c>corLibAware</c> flag is irrelevant for reflection (there is no module scope to route
///     through), so it is ignored.
/// </summary>
internal sealed class ReflectionTypeFactory(DiagnosticBag? diagnostics) : ITypeFactory<Type>
{
    public Type Object => typeof(object);

    public Type Primitive(PrimitiveKind kind)
    {
        return kind switch
        {
            PrimitiveKind.Int => typeof(int),
            PrimitiveKind.Long => typeof(long),
            PrimitiveKind.Float => typeof(float),
            PrimitiveKind.Double => typeof(double),
            PrimitiveKind.Byte => typeof(byte),
            PrimitiveKind.Char => typeof(char),
            PrimitiveKind.Bool => typeof(bool),
            PrimitiveKind.String => typeof(string),
            PrimitiveKind.Unit => typeof(ValueTuple),
            PrimitiveKind.Symbol => typeof(Runtime.ZSymbol),
            _ => typeof(object),
        };
    }

    public bool IsValueType(Type t)
    {
        return t.IsValueType;
    }

    public bool IsGenericDefinition(Type t)
    {
        return t.IsGenericTypeDefinition;
    }

    public Type MakeArray(Type element)
    {
        return element.MakeArrayType();
    }

    public Type FromClrType(Type clrType, bool corLibAware)
    {
        return clrType;
    }

    public Type CloseClrGeneric(Type openClrType, Type[] args)
    {
        return openClrType.MakeGenericType(args);
    }

    public Type CloseMappedGeneric(Type openMapped, Type[] args)
    {
        return openMapped.MakeGenericType(args);
    }

    public void Warn(string message)
    {
        diagnostics?.Warning(message, SourceSpan.None);
    }
}

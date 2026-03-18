namespace ZScript.Compiler.Codegen;

using ZScript.Compiler.Types;

/// <summary>
/// Maps ZScript types to CLR System.Type instances.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapToClr(ZType type) => type switch
    {
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => typeof(int),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => typeof(long),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => typeof(float),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => typeof(double),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => typeof(byte),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => typeof(char),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => typeof(bool),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => typeof(string),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => typeof(ZScript.Runtime.ZsUnit),
        ZType.ZFuncType ft => MakeFuncType(ft),
        _ => typeof(object)
    };

    private static Type MakeFuncType(ZType.ZFuncType ft)
    {
        var types = ft.Params.Select(MapToClr).Append(MapToClr(ft.Return)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => typeof(object) // fallback
        };
    }
}

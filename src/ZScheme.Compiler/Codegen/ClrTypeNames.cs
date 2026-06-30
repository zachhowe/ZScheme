namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Shared string conversions between C#-style type names and .NET reflection
///     type names. Used by both type mappers (<see cref="IlTypeMapper" /> and
///     <see cref="AsmResolverTypeMapper" />) so the two backends munge names identically.
/// </summary>
public static class ClrTypeNames
{
    /// <summary>
    ///     Convert a C#-style generic type name to its .NET reflection form, e.g.
    ///     <c>System.Func&lt;int,int&gt;</c> → <c>System.Func`2[System.Int32,System.Int32]</c>.
    ///     Non-generic names (e.g. <c>System.Action</c>) pass through unchanged.
    /// </summary>
    public static string ConvertToReflectionTypeName(string typeName)
    {
        if (!typeName.Contains('<'))
            return typeName;

        // Extract the base name and type arguments
        var openAngle = typeName.IndexOf('<');
        var closeAngle = typeName.LastIndexOf('>');
        if (openAngle >= closeAngle)
            return typeName;

        var baseName = typeName[..openAngle];
        var typeArgsStr = typeName[(openAngle + 1)..closeAngle];

        // Extract the arity from the base name (e.g., Func`2) or infer from type args
        var backtick = baseName.LastIndexOf('`');
        var arity = typeArgsStr.Split(',').Length;

        string reflectedBase;
        if (backtick > 0)
            reflectedBase = baseName[..backtick];
        else
            reflectedBase = $"{baseName}`{arity}";

        // Convert each type argument
        var reflectedArgs = typeArgsStr.Split(',').Select(ConvertTypeArg).ToArray();

        return $"{reflectedBase}[{string.Join(",", reflectedArgs)}]";
    }

    /// <summary>
    ///     Normalize a single type argument to a fully-qualified reflection name,
    ///     mapping primitive aliases (<c>int</c>, <c>string</c>, …) to their
    ///     <c>System.*</c> equivalents and passing anything else through as-is.
    /// </summary>
    public static string ConvertTypeArg(string arg)
    {
        arg = arg.Trim();
        return arg switch
        {
            "int" or "Int32" => "System.Int32",
            "long" or "Int64" => "System.Int64",
            "short" or "Int16" => "System.Int16",
            "byte" or "Byte" or "uint" or "UInt32" => "System.UInt32",
            "ushort" or "UInt16" => "System.UInt16",
            "sbyte" or "SByte" => "System.SByte",
            "float" or "Single" => "System.Single",
            "double" or "Double" => "System.Double",
            "bool" or "Boolean" => "System.Boolean",
            "string" or "String" => "System.String",
            "char" or "Char" => "System.Char",
            "unit" or "Unit" => "System.Object",
            _ => arg, // Pass through as-is (assumed to be fully qualified)
        };
    }
}

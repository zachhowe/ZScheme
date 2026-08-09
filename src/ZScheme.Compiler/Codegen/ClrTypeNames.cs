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

        // Split the type arguments at top-level commas only — commas nested inside
        // an inner generic's angle brackets belong to that inner type.
        var typeArgs = SplitTopLevel(typeArgsStr);

        // Extract the arity from the base name (e.g., Func`2) or infer from type args
        var backtick = baseName.LastIndexOf('`');

        string reflectedBase;
        if (backtick > 0)
            reflectedBase = baseName[..backtick];
        else
            reflectedBase = $"{baseName}`{typeArgs.Count}";

        // Convert each type argument, recursing so nested generics get their own
        // `N[...] reflection form before the simple-name fallback.
        var reflectedArgs = typeArgs
            .Select(arg =>
                arg.Contains('<') ? ConvertToReflectionTypeName(arg.Trim()) : ConvertTypeArg(arg)
            )
            .ToArray();

        return $"{reflectedBase}[{string.Join(",", reflectedArgs)}]";
    }

    /// <summary>
    ///     Render a reflection <see cref="Type" /> as a C#-style type name, e.g.
    ///     <c>System.Func`2[[System.String, …],[System.Object, …]]</c> →
    ///     <c>System.Func&lt;System.String, System.Object&gt;</c>.
    ///     <see cref="Type.FullName" /> is unusable wherever the name is emitted as source:
    ///     for a constructed generic it produces the assembly-qualified reflection spelling,
    ///     which is not valid C#. This is the inverse of
    ///     <see cref="ConvertToReflectionTypeName" />.
    /// </summary>
    public static string ToCSharpTypeName(Type type)
    {
        if (type.IsByRef || type.IsPointer)
            return ToCSharpTypeName(type.GetElementType()!);

        if (type.IsArray)
        {
            var commas = new string(',', type.GetArrayRank() - 1);
            return $"{ToCSharpTypeName(type.GetElementType()!)}[{commas}]";
        }

        // An open type parameter has no qualified spelling; its bare name is what a
        // generic C# signature refers to it by.
        if (type.IsGenericParameter)
            return type.Name;

        var name = QualifiedName(type);
        if (!type.IsGenericType)
            return name;

        var args = type.GetGenericArguments().Select(ToCSharpTypeName);
        return $"{name}<{string.Join(", ", args)}>";
    }

    /// <summary>
    ///     The type's C# name without type arguments: namespace-qualified, backtick arity
    ///     stripped, and nested types joined with <c>.</c> rather than reflection's <c>+</c>.
    ///     A type nested inside a <em>generic</em> type is spelled with all of the arguments on
    ///     the innermost name rather than split across the two — no delegate type has that
    ///     shape, and every caller here is naming a delegate.
    /// </summary>
    private static string QualifiedName(Type type)
    {
        var name = type.Name;
        var backtick = name.IndexOf('`');
        if (backtick > 0)
            name = name[..backtick];

        if (type.IsNested && type.DeclaringType is { } declaring)
            return $"{QualifiedName(declaring)}.{name}";

        return string.IsNullOrEmpty(type.Namespace) ? name : $"{type.Namespace}.{name}";
    }

    /// <summary>
    ///     Split a type-argument list on commas at angle-bracket depth 0, so nested
    ///     generic arguments (e.g. <c>System.Func&lt;int,int&gt;</c>) are kept intact.
    /// </summary>
    private static List<string> SplitTopLevel(string typeArgsStr)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < typeArgsStr.Length; i++)
        {
            switch (typeArgsStr[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(typeArgsStr[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(typeArgsStr[start..]);
        return parts;
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
            "byte" or "Byte" => "System.Byte",
            "uint" or "UInt32" => "System.UInt32",
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

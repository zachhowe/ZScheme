namespace ZScheme.Compiler.Syntax;

/// <summary>
///     The spelling of a type-derived member binding — a record/struct field, a class field
///     or method, an interface method. The accessor for field <c>status-code</c> of record
///     <c>HttpResponse</c> is the single identifier <c>HttpResponse-status-code</c>.
///     <para>
///         The separator used to be <c>/</c>. That spelling is deprecated but still resolves,
///         via <see cref="TryModernizeLegacyName" /> — see <c>DiagnosticCodes.DeprecatedAccessorSyntax</c>.
///     </para>
/// </summary>
public static class AccessorNaming
{
    /// <summary>Separator between the type name and the member name.</summary>
    public const char Separator = '-';

    /// <summary>Separator of the deprecated spelling.</summary>
    public const char LegacySeparator = '/';

    /// <summary>Builds the accessor binding name for <paramref name="memberName" /> on
    ///     <paramref name="typeName" />.</summary>
    public static string Accessor(string typeName, string memberName) =>
        $"{typeName}{Separator}{memberName}";

    /// <summary>
    ///     Rewrites a deprecated <c>Type/member</c> spelling to <c>Type-member</c>, or returns
    ///     <c>null</c> when <paramref name="name" /> carries no separator. Splits at the
    ///     <em>last</em> <c>/</c>: a type name never contains one, so the split is unambiguous
    ///     — which is exactly why the modern name can never be split back apart the same way
    ///     (type names very much do contain <c>-</c>).
    /// </summary>
    public static string? TryModernizeLegacyName(string name)
    {
        var idx = name.LastIndexOf(LegacySeparator);
        if (idx <= 0 || idx == name.Length - 1)
            return null;

        return string.Concat(name.AsSpan(0, idx), Separator.ToString(), name.AsSpan(idx + 1));
    }
}

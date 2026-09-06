using System.Collections.Frozen;

namespace ZScheme.Compiler.Syntax;

/// <summary>
///     The deprecated spellings of special-form heads, and the modern head each maps to.
///     <c>(export …)</c> is now <c>(provide …)</c>, and the type declarations shed their
///     <c>define-</c> prefix — <c>(define-record …)</c> is <c>(record …)</c>, and likewise
///     for struct, union, class and interface.
///     <para>
///         The old heads still build, via <see cref="TryModernize" /> — see
///         <c>DiagnosticCodes.DeprecatedKeyword</c>. <c>define</c>, <c>define-async</c>,
///         <c>define-syntax</c> and <c>define-type-alias</c> are unchanged.
///     </para>
/// </summary>
public static class KeywordAliases
{
    private static readonly FrozenDictionary<string, string> _legacyToModern = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["export"] = "provide",
        ["define-record"] = "record",
        ["define-struct"] = "struct",
        ["define-union"] = "union",
        ["define-class"] = "class",
        ["define-interface"] = "interface",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The deprecated heads, for tooling that still highlights them as keywords.</summary>
    public static IReadOnlyCollection<string> LegacyHeads => _legacyToModern.Keys;

    /// <summary>The heads that replaced them.</summary>
    public static IReadOnlyCollection<string> ModernHeads => _legacyToModern.Values;

    /// <summary>
    ///     The modern spelling of <paramref name="head" />, or <c>null</c> when it is not a
    ///     deprecated head. Callers that dispatch on a head should normalize through this
    ///     first, so only the modern spellings need a case.
    /// </summary>
    public static string? TryModernize(string head) =>
        _legacyToModern.TryGetValue(head, out var modern) ? modern : null;
}

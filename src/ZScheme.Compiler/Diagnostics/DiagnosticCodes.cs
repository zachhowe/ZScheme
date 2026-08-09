namespace ZScheme.Compiler.Diagnostics;

/// <summary>
///     Stable codes for diagnostics that tooling (the LSP's quick fixes) keys off.
///     Each constant documents the convention for <see cref="Diagnostic.Data" />.
///     Codes are assigned only where tooling needs them — most diagnostics remain
///     message-only.
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>Undefined variable. Data: <c>[0]</c> = the undefined name.</summary>
    public const string UndefinedVariable = "ZS0001";

    /// <summary>Non-exhaustive match over a union. Data: one entry per missing case,
    ///     formatted <c>"CaseName/Arity"</c> (e.g. <c>"Some/1"</c>).</summary>
    public const string NonExhaustiveMatch = "ZS0002";

    /// <summary>Unused <c>let</c>/<c>use</c> binding. Data: <c>[0]</c> = the binding
    ///     name. Rendered by LSP clients with the <c>Unnecessary</c> tag (greyed out).</summary>
    public const string UnusedBinding = "ZS0003";

    /// <summary>
    ///     A fully-qualified CLR type name — in a type position, or leading an
    ///     <c>import-clr</c> member path — whose namespace the same file already declares with
    ///     <c>(import-clr Ns …)</c>, so the short name would resolve to the identical type.
    ///     Data: <c>[0]</c> = the short name, <c>[1]</c> = the redundant
    ///     namespace prefix. The span covers only the <c>Ns.</c> characters, so the quick fix
    ///     is a plain deletion; clients render it with the <c>Unnecessary</c> tag.
    ///     <para>
    ///         LSP-only — emitted by <c>Analysis/RedundantTypeQualifierAnalyzer.cs</c>, never by
    ///         the compiler, so CLI builds stay quiet about a purely stylistic choice.
    ///     </para>
    /// </summary>
    public const string RedundantTypeQualifier = "ZS0004";
}

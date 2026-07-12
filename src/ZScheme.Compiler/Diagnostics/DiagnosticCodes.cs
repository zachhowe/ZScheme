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
}

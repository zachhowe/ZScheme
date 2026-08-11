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
    ///         Opt-in — emitted by <c>Analysis/RedundantTypeQualifierAnalyzer.cs</c>, which no
    ///         compile path runs. Only the language server and <c>zs lint</c> ask for it, so
    ///         <c>zs build</c> stays quiet about a purely stylistic choice.
    ///     </para>
    /// </summary>
    public const string RedundantTypeQualifier = "ZS0004";

    /// <summary>
    ///     A self-recursive function or class/object method that <c>Ir/TailCallLowering</c>
    ///     will not turn into a <c>TcoJump</c> loop, so the recursion consumes stack. Data:
    ///     <c>[0]</c> = the function or method name, <c>[1]</c> = why it is not looped — one of
    ///     <c>"not-tail"</c>, <c>"barrier"</c>, <c>"not-top-level"</c>, <c>"virtual"</c> (a
    ///     method of an <c>#:open</c> class, whose self-call has to dispatch to any subclass
    ///     override). Silenced for one definition or method by <c>#:recursive</c>, and for a
    ///     whole compilation by <c>CompilerOptions.WarnUnloopedRecursion</c>.
    ///     <para>
    ///         Silence means the function will be marked <c>IsTcoLoop</c> — <em>not</em> that
    ///         every recursive path is bounded: a function with one tail arm and one non-tail
    ///         arm is looped and stays quiet. Mutual recursion is never looped and never
    ///         reported, matching the pass's own self-call-only scope.
    ///     </para>
    /// </summary>
    public const string NonLoopedSelfRecursion = "ZS0005";
}

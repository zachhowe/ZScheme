namespace ZScheme.Compiler.Syntax;

/// <summary>
///     One macro rewrite recorded during expansion: <see cref="Macro" />'s rule
///     <see cref="RuleIndex" /> rewrote <see cref="Redex" /> into <see cref="Expansion" />.
///     <see cref="FormBefore" /> / <see cref="FormAfter" /> are snapshots of the whole
///     top-level form containing the redex, with left siblings already expanded and right
///     siblings still unexpanded (the progressive view a stepper shows).
///     <see cref="PathFromRoot" /> addresses the redex/expansion by child indices from the
///     snapshot root; it is valid for both snapshots. Do not locate the redex by reference:
///     template substitution can insert the same <see cref="SExpr" /> instance into
///     multiple holes.
/// </summary>
public sealed record MacroStep(
    int Index,
    MacroDefinition Macro,
    int RuleIndex,
    int Depth,
    SExpr Redex,
    SExpr Expansion,
    SExpr FormBefore,
    SExpr FormAfter,
    IReadOnlyList<int> PathFromRoot,
    int TopLevelFormIndex
);

/// <summary>
///     Receives macro-expansion events from <see cref="MacroExpander" />. Passing no observer
///     (the default) skips all snapshot bookkeeping, so ordinary compilation is unaffected.
/// </summary>
public interface IMacroExpansionObserver
{
    void OnStep(MacroStep step);

    void OnDepthLimitExceeded(SExpr expr, int depth) { }
}

/// <summary>Collecting observer used by the macro debugger and tests.</summary>
public sealed class MacroExpansionTrace : IMacroExpansionObserver
{
    private readonly List<MacroStep> _steps = [];

    public IReadOnlyList<MacroStep> Steps => _steps;
    public bool DepthLimitHit { get; private set; }

    public void OnStep(MacroStep step)
    {
        _steps.Add(step);
    }

    public void OnDepthLimitExceeded(SExpr expr, int depth)
    {
        DepthLimitHit = true;
    }
}

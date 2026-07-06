using ZScheme.Compiler.Syntax;

namespace ZScheme.MacroDebugger.ViewModels;

/// <summary>
///     Presentation of one <see cref="MacroStep" />: pretty-printed before/after snapshots of
///     the enclosing top-level form with the redex/result span marked, plus header metadata.
///     Printing is deferred until a pane actually shows the step.
/// </summary>
public sealed class StepViewModel(MacroStep step, int total)
{
    public const int PrintWidth = 80;

    private readonly Lazy<SExprPrinter.Result> _after = new(() =>
        SExprPrinter.Print(step.FormAfter, step.PathFromRoot, PrintWidth)
    );

    private readonly Lazy<SExprPrinter.Result> _before = new(() =>
        SExprPrinter.Print(step.FormBefore, step.PathFromRoot, PrintWidth)
    );

    public MacroStep Step => step;

    public string Header =>
        $"Step {step.Index + 1} of {total}: {step.Macro.Name} "
        + $"(rule {step.RuleIndex + 1} of {step.Macro.Rules.Count}, depth {step.Depth})";

    public string RuleText =>
        step.RuleIndex >= 0 && step.RuleIndex < step.Macro.Rules.Count
            ? MacroRulePrinter.Print(step.Macro.Rules[step.RuleIndex])
            : "";

    public string BeforeText => _before.Value.Text;
    public string AfterText => _after.Value.Text;
    public SExprPrinter.TextSpan? BeforeHighlight => _before.Value.MarkedSpan;
    public SExprPrinter.TextSpan? AfterHighlight => _after.Value.MarkedSpan;
}

using ZScheme.Compiler.Syntax;

namespace ZScheme.MacroDebugger.ViewModels;

/// <summary>
///     Presentation of one <see cref="MacroStep" />: before/after full-file views composed
///     from the shared <see cref="DocumentContext" /> (expanded context above, raw context
///     below) around the pretty-printed enclosing form, with the redex/result span marked.
///     <c>Focus</c> is the current form's span within the full text; the highlight span is
///     in full-file coordinates. Printing is deferred until a pane actually shows the step.
/// </summary>
public sealed class StepViewModel(MacroStep step, int total, DocumentContext context)
{
    public const int PrintWidth = 80;

    private readonly Lazy<PaneContent> _after = new(() => Compose(step.FormAfter, step, context));

    private readonly Lazy<PaneContent> _before = new(() =>
        Compose(step.FormBefore, step, context)
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
    public SExprPrinter.TextSpan? BeforeFocus => _before.Value.Focus;
    public SExprPrinter.TextSpan? AfterFocus => _after.Value.Focus;
    public SExprPrinter.TextSpan? BeforeHighlight => _before.Value.Highlight;
    public SExprPrinter.TextSpan? AfterHighlight => _after.Value.Highlight;

    private static PaneContent Compose(SExpr form, MacroStep step, DocumentContext context)
    {
        var printed = SExprPrinter.Print(form, step.PathFromRoot, PrintWidth);
        var prefix = context.PrefixFor(step.TopLevelFormIndex);
        var suffix = context.SuffixFor(step.TopLevelFormIndex);

        var focusStart = prefix.Length == 0 ? 0 : prefix.Length + DocumentContext.FormSeparator.Length;
        var text =
            (prefix.Length == 0 ? "" : prefix + DocumentContext.FormSeparator)
            + printed.Text
            + (suffix.Length == 0 ? "" : DocumentContext.FormSeparator + suffix);

        var focus = new SExprPrinter.TextSpan(focusStart, printed.Text.Length);
        SExprPrinter.TextSpan? highlight = printed.MarkedSpan is { } marked
            ? new SExprPrinter.TextSpan(focusStart + marked.Start, marked.Length)
            : null;

        return new PaneContent(text, focus, highlight);
    }

    private readonly record struct PaneContent(
        string Text,
        SExprPrinter.TextSpan Focus,
        SExprPrinter.TextSpan? Highlight
    );
}

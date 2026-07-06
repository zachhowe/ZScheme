using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.MacroDebugger.Services;
using ZScheme.MacroDebugger.ViewModels;

namespace ZScheme.MacroDebugger.Tests;

public class MainWindowViewModelTests
{
    private static ExpansionResult Expand(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var parser = new SExprParser(lexer.Tokenize(), diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var trace = new MacroExpansionTrace();
        var expander = new MacroExpander(diag, trace);
        var expanded = expander.ExpandAll(sexprs, new MacroEnvironment());

        return new ExpansionResult(
            "test.zs",
            trace.Steps,
            sexprs,
            expanded,
            trace.ExpandedTopLevelForms,
            diag,
            true
        );
    }

    private const string PipeSource = """
        (define-syntax |>
          (syntax-rules ()
            [(|> x) x]
            [(|> x (f args ...) rest ...) (|> (f x args ...) rest ...)]
            [(|> x f rest ...) (|> (f x) rest ...)]))
        (|> 5 (add 1) (mul 2))
        """;

    [Fact]
    public void Apply_WithSteps_StartsAtFirstStep()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand(PipeSource));

        Assert.Equal(3, vm.Steps.Count);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.NotNull(vm.CurrentStep);
        Assert.StartsWith("Step 1 of 3: |>", vm.Header);
        Assert.Equal("3 macro expansion steps", vm.StatusText);
        Assert.False(vm.HasDiagnostics);
    }

    [Fact]
    public void Navigation_ClampsAtBounds()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand(PipeSource));

        vm.CurrentIndex = 99;
        Assert.Equal(2, vm.CurrentIndex);
        vm.CurrentIndex = -5;
        Assert.Equal(0, vm.CurrentIndex);
    }

    [Fact]
    public void CommandCanExecute_TracksPosition()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand(PipeSource));

        // At first step
        Assert.False(vm.PrevCommand.CanExecute(null));
        Assert.False(vm.FirstCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.True(vm.LastCommand.CanExecute(null));

        vm.LastCommand.Execute(null);
        Assert.Equal(2, vm.CurrentIndex);
        Assert.True(vm.PrevCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.False(vm.LastCommand.CanExecute(null));

        vm.PrevCommand.Execute(null);
        Assert.Equal(1, vm.CurrentIndex);
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.True(vm.PrevCommand.CanExecute(null));
    }

    [Fact]
    public void ZeroSteps_ShowsFinalProgramFallback()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand("(+ 1 2)"));

        Assert.Empty(vm.Steps);
        Assert.Equal(-1, vm.CurrentIndex);
        Assert.Null(vm.CurrentStep);
        Assert.Equal("No macro expansion steps", vm.Header);
        Assert.Equal("No macro expansion steps in this file", vm.StatusText);
        Assert.Equal("(+ 1 2)", vm.BeforeText);
        Assert.Equal("(+ 1 2)", vm.AfterText);
        Assert.Null(vm.BeforeHighlight);
        Assert.Null(vm.AfterHighlight);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.False(vm.PrevCommand.CanExecute(null));
    }

    [Fact]
    public void StepViewModel_ExposesHighlightedSnapshots()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(
            Expand(
                """
                (define-syntax my-if
                  (syntax-rules ()
                    [(my-if c t e) (if c t e)]))
                (define (f) (my-if #t 1 2))
                """
            )
        );

        var step = Assert.Single(vm.Steps);
        Assert.Equal("(my-if c t e) => (if c t e)", step.RuleText);

        // Full-file view: the define-syntax form stays visible above the focused form
        Assert.StartsWith("(define-syntax my-if", step.BeforeText);
        Assert.StartsWith("(define-syntax my-if", step.AfterText);

        Assert.NotNull(step.BeforeFocus);
        var beforeFocus = step.BeforeFocus.Value;
        Assert.Equal(
            "(define (f) (my-if #t 1 2))",
            step.BeforeText.Substring(beforeFocus.Start, beforeFocus.Length)
        );

        Assert.NotNull(step.AfterFocus);
        var afterFocus = step.AfterFocus.Value;
        Assert.Equal(
            "(define (f) (if #t 1 2))",
            step.AfterText.Substring(afterFocus.Start, afterFocus.Length)
        );

        Assert.NotNull(step.BeforeHighlight);
        var before = step.BeforeHighlight.Value;
        Assert.Equal("(my-if #t 1 2)", step.BeforeText.Substring(before.Start, before.Length));
        Assert.True(before.Start >= beforeFocus.Start);
        Assert.True(before.Start + before.Length <= beforeFocus.Start + beforeFocus.Length);

        Assert.NotNull(step.AfterHighlight);
        var after = step.AfterHighlight.Value;
        Assert.Equal("(if #t 1 2)", step.AfterText.Substring(after.Start, after.Length));
        Assert.True(after.Start >= afterFocus.Start);
        Assert.True(after.Start + after.Length <= afterFocus.Start + afterFocus.Length);
    }

    [Fact]
    public void FullFileView_AboveIsExpanded_BelowIsRaw()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(
            Expand(
                """
                (define-syntax my-if
                  (syntax-rules ()
                    [(my-if c t e) (if c t e)]))
                (define (f) (my-if #t 1 2))
                (define (g) (my-if #f 3 4))
                """
            )
        );

        Assert.Equal(2, vm.Steps.Count);

        // At the first step, the second form is below: shown raw (unexpanded)
        var first = vm.Steps[0];
        Assert.Contains("(define (g) (my-if #f 3 4))", first.BeforeText);

        // At the second step, the first form is above: shown expanded, its raw call gone
        var second = vm.Steps[1];
        Assert.Contains("(define (f) (if #t 1 2))", second.BeforeText);
        Assert.DoesNotContain("(define (f) (my-if #t 1 2))", second.BeforeText);
    }

    [Fact]
    public void DefineSyntax_StaysVisibleInBothPanesAtEveryStep()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand(PipeSource));

        foreach (var step in vm.Steps)
        {
            Assert.Contains("define-syntax", step.BeforeText);
            Assert.Contains("define-syntax", step.AfterText);
        }
    }

    [Fact]
    public void EmptyExpansion_ContributesNothingToContext()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(
            Expand(
                """
                (define-syntax vanish
                  (syntax-rules ()
                    [(vanish) (begin)]))
                (vanish)
                (define (f) (vanish))
                """
            )
        );

        // Second site: the top-level (vanish) above expanded to nothing — no empty slot
        var last = vm.Steps[^1];
        Assert.DoesNotContain("\n\n\n", last.BeforeText);
        Assert.DoesNotContain("(vanish)\n\n(define", last.BeforeText);
    }

    [Fact]
    public void Sites_OneEntryPerDepthZeroStep()
    {
        var vm = new MainWindowViewModel();
        // One source-level call that cascades through 3 rewrites → a single site
        vm.Apply(Expand(PipeSource));
        var site = Assert.Single(vm.Sites);
        Assert.Equal(0, site.FirstStepIndex);
        Assert.Same(site, vm.SelectedSite);

        // Two separate source-level calls → two sites
        vm.Apply(
            Expand(
                """
                (define-syntax my-if
                  (syntax-rules ()
                    [(my-if c t e) (if c t e)]))
                (define (f) (my-if #t 1 2))
                (define (g) (my-if #f 3 4))
                """
            )
        );
        Assert.Equal(2, vm.Sites.Count);
        Assert.Equal(0, vm.Sites[0].FirstStepIndex);
        Assert.Equal(1, vm.Sites[1].FirstStepIndex);
    }

    [Fact]
    public void SiteLabel_UsesMacroNameAndLine()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand(PipeSource));

        Assert.Equal("1. |> — line 6", Assert.Single(vm.Sites).Label);
    }

    [Fact]
    public void SelectingSite_JumpsToFirstStep()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(
            Expand(
                """
                (define-syntax my-if
                  (syntax-rules ()
                    [(my-if c t e) (if c t e)]))
                (define (f) (my-if #t 1 2))
                (define (g) (my-if #f 3 4))
                """
            )
        );

        vm.SelectedSite = vm.Sites[1];
        Assert.Equal(vm.Sites[1].FirstStepIndex, vm.CurrentIndex);

        vm.SelectedSite = vm.Sites[0];
        Assert.Equal(0, vm.CurrentIndex);
    }

    [Fact]
    public void Stepping_SyncsSelectedSite_WithoutJumpingBack()
    {
        var vm = new MainWindowViewModel();
        // PipeSource: single site, steps 1..2 are its depth-1+ cascade
        vm.Apply(Expand(PipeSource));

        vm.NextCommand.Execute(null);
        // Still inside the same site, and the sync must not snap back to its first step
        Assert.Equal(1, vm.CurrentIndex);
        Assert.Same(vm.Sites[0], vm.SelectedSite);

        // Crossing into another file's second site updates the selection
        vm.Apply(
            Expand(
                """
                (define-syntax my-if
                  (syntax-rules ()
                    [(my-if c t e) (if c t e)]))
                (define (f) (my-if #t 1 2))
                (define (g) (my-if #f 3 4))
                """
            )
        );
        Assert.Same(vm.Sites[0], vm.SelectedSite);
        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.CurrentIndex);
        Assert.Same(vm.Sites[1], vm.SelectedSite);
        vm.PrevCommand.Execute(null);
        Assert.Same(vm.Sites[0], vm.SelectedSite);
    }

    [Fact]
    public void ZeroSteps_HasNoSites()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(Expand("(+ 1 2)"));

        Assert.Empty(vm.Sites);
        Assert.False(vm.HasSites);
        Assert.Null(vm.SelectedSite);
    }

    [Fact]
    public void Apply_WithDiagnostics_SurfacesThem()
    {
        var diag = new DiagnosticBag();
        diag.Error("Something failed", SourceSpan.None);
        var vm = new MainWindowViewModel();
        vm.Apply(new ExpansionResult("bad.zs", [], null, null, null, diag, false));

        Assert.True(vm.HasDiagnostics);
        Assert.Contains("Something failed", vm.DiagnosticsText);
        Assert.Equal("Compilation failed before macro expansion", vm.StatusText);
        Assert.Equal("", vm.BeforeText);
        Assert.Equal("", vm.AfterText);
    }

    [Fact]
    public void WindowTitle_TracksFile()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("ZScheme Macro Stepper", vm.WindowTitle);
        vm.Apply(Expand("(+ 1 2)"));
        Assert.Equal("ZScheme Macro Stepper — test.zs", vm.WindowTitle);
    }
}

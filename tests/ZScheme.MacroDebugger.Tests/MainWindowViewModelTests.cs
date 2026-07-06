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

        return new ExpansionResult("test.zs", trace.Steps, sexprs, expanded, diag, true);
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
        Assert.Equal("(define (f) (my-if #t 1 2))", step.BeforeText);
        Assert.Equal("(define (f) (if #t 1 2))", step.AfterText);
        Assert.Equal("(my-if c t e) => (if c t e)", step.RuleText);

        Assert.NotNull(step.BeforeHighlight);
        var before = step.BeforeHighlight.Value;
        Assert.Equal("(my-if #t 1 2)", step.BeforeText.Substring(before.Start, before.Length));

        Assert.NotNull(step.AfterHighlight);
        var after = step.AfterHighlight.Value;
        Assert.Equal("(if #t 1 2)", step.AfterText.Substring(after.Start, after.Length));
    }

    [Fact]
    public void Apply_WithDiagnostics_SurfacesThem()
    {
        var diag = new DiagnosticBag();
        diag.Error("Something failed", SourceSpan.None);
        var vm = new MainWindowViewModel();
        vm.Apply(new ExpansionResult("bad.zs", [], null, null, diag, false));

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

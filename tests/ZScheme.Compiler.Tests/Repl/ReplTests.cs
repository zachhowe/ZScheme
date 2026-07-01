using Xunit;

namespace ZScheme.Compiler.Tests.Repl;

public class ReplTests
{
    private readonly MockReplConsole _console = new();

    private Compiler.Repl.Repl CreateRepl()
    {
        return new Compiler.Repl.Repl(_console);
    }

    // --- Run loop behavior ---

    [Fact]
    public void Run_DisplaysWelcomeMessage()
    {
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains("ZScheme REPL (type :quit to exit)", _console.WrittenLines);
    }

    [Fact]
    public void Run_DisplaysPrompt()
    {
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains("zs> ", _console.WrittenText);
    }

    [Fact]
    public void Run_QuitCommand_ExitsLoop()
    {
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        // Should have displayed exactly one prompt before exiting
        Assert.Single(_console.WrittenText, t => t == "zs> ");
    }

    [Fact]
    public void Run_QCommand_ExitsLoop()
    {
        _console.Inputs.Enqueue(":q");
        CreateRepl().Run();

        Assert.Single(_console.WrittenText, t => t == "zs> ");
    }

    [Fact]
    public void Run_NullInput_ExitsLoop()
    {
        // Queue is empty, so ReadLine returns null
        CreateRepl().Run();

        Assert.Single(_console.WrittenText, t => t == "zs> ");
    }

    [Fact]
    public void Run_EmptyInput_ContinuesLoop()
    {
        _console.Inputs.Enqueue("");
        _console.Inputs.Enqueue("   ");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        // Three prompts: empty, whitespace, then :quit
        Assert.Equal(3, _console.WrittenText.Count(t => t == "zs> "));
    }

    [Fact]
    public void Run_EnvCommand_PrintsNotImplemented()
    {
        _console.Inputs.Enqueue(":env");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains("(environment listing not yet implemented)", _console.WrittenLines);
    }

    // --- Evaluate via Run ---

    [Fact]
    public void Run_IntegerLiteral_PrintsValue()
    {
        _console.Inputs.Enqueue("42");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("42"));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_ArithmeticExpression_PrintsResultValue()
    {
        _console.Inputs.Enqueue("(+ 2 2)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("4"));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_StringLiteral_PrintsQuotedString()
    {
        _console.Inputs.Enqueue("\"hello\"");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("\"hello\""));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_BoolLiteralTrue_PrintsHashT()
    {
        _console.Inputs.Enqueue("#t");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("#t"));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_BoolLiteralFalse_PrintsHashF()
    {
        _console.Inputs.Enqueue("#f");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("#f"));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_DefineFunction_PrintsDefinedName()
    {
        _console.Inputs.Enqueue("(define (f x) x)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("defined f"));
    }

    [Fact]
    public void Run_DefineValue_PrintsDefinedNameAndValue()
    {
        _console.Inputs.Enqueue("(define x 42)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("defined x"));
        Assert.Contains(_console.WrittenLines, l => l.Contains("42"));
    }

    [Fact]
    public void Run_InvalidSyntax_PrintsError()
    {
        _console.Inputs.Enqueue("(");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.NotEmpty(_console.ErrorLines);
    }

    [Fact]
    public void Run_PersistentEnv_RemembersDefinitions()
    {
        _console.Inputs.Enqueue("(define x 42)");
        _console.Inputs.Enqueue("x");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        // Defining x prints both the name and the value, then referencing x
        // prints the value again. We expect at least two occurrences of "42"
        // across the output (one from defining, one from referencing).
        var fortyTwoLines = _console.WrittenLines.Count(l => l.Contains("42"));
        Assert.True(
            fortyTwoLines >= 2,
            $"Expected at least 2 lines containing '42', got {fortyTwoLines}. Lines: [{string.Join(", ", _console.WrittenLines)}]"
        );
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_PersistentEnv_CanCallDefinedFunction()
    {
        _console.Inputs.Enqueue("(define (double x) (+ x x))");
        _console.Inputs.Enqueue("(double 5)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("defined double"));
        Assert.Contains(_console.WrittenLines, l => l.Contains("10"));
        Assert.Empty(_console.ErrorLines);
    }

    [Fact]
    public void Run_UndefinedVariable_PrintsError()
    {
        _console.Inputs.Enqueue("(foo 1)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.ErrorLines, l => l.Contains("Undefined variable"));
    }

    [Fact]
    public void Run_UndefinedVariable_DoesNotPrintTypeVariable()
    {
        _console.Inputs.Enqueue("(foo 1)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        // Should show error, not a raw type variable like ": ?"
        Assert.DoesNotContain(_console.WrittenLines, l => l.Contains(": ?"));
    }
}

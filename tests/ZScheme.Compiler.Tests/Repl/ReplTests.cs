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
    public void Run_IntegerLiteral_PrintsIntType()
    {
        _console.Inputs.Enqueue("42");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains(": Int"));
    }

    [Fact]
    public void Run_StringLiteral_PrintsStringType()
    {
        _console.Inputs.Enqueue("\"hello\"");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains(": String"));
    }

    [Fact]
    public void Run_BoolLiteral_PrintsBoolType()
    {
        _console.Inputs.Enqueue("#t");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains(": Bool"));
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
    public void Run_DefineValue_PrintsDefinedName()
    {
        _console.Inputs.Enqueue("(define x 42)");
        _console.Inputs.Enqueue(":quit");
        CreateRepl().Run();

        Assert.Contains(_console.WrittenLines, l => l.Contains("defined x"));
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

        // Both expressions should succeed with Int type
        var intTypeLines = _console.WrittenLines.Where(l => l.Contains(": Int")).ToList();
        Assert.Equal(2, intTypeLines.Count);
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
        Assert.Contains(_console.WrittenLines, l => l.Contains(": Int"));
        Assert.Empty(_console.ErrorLines);
    }
}

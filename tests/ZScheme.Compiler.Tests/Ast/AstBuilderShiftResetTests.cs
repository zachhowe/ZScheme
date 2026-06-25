using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Ast;

public class AstBuilderShiftResetTests
{
    private static (AstNode.Program Program, DiagnosticBag Diagnostics) BuildWithDiagnostics(
        string source
    )
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);
        return (program, diag);
    }

    private static AstNode.Program Build(string source)
    {
        var (program, diag) = BuildWithDiagnostics(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return program;
    }

    [Fact]
    public void Reset_WrapsBodyExpression()
    {
        var prog = Build("(reset 1)");
        var reset = Assert.IsType<AstNode.Reset>(prog.TopLevelForms[0]);
        var lit = Assert.IsType<AstNode.IntLit>(reset.Body);
        Assert.Equal(1, lit.Value);
    }

    [Fact]
    public void Shift_BindsContinuationName()
    {
        var prog = Build("(reset (shift k 7))");
        var reset = Assert.IsType<AstNode.Reset>(prog.TopLevelForms[0]);
        var shift = Assert.IsType<AstNode.Shift>(reset.Body);
        Assert.Equal("k", shift.ContName);
    }

    [Fact]
    public void Reset_RequiresAtLeastOneBody()
    {
        // (reset) — no body (or tag) — is now an arity error after tagged-arity overload.
        var (_, diag) = BuildWithDiagnostics("(reset)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'reset'"));
    }

    [Fact]
    public void Reset_RejectsTooManyArguments()
    {
        // (reset a b c) is now (reset tag body extra) — arity error.
        var (_, diag) = BuildWithDiagnostics("(reset 1 2 3)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void Shift_RequiresContinuationNameAndBody()
    {
        // Bare (shift k) has no body and is now invalid under both the 3-arg and 4-arg shapes.
        var (_, diag) = BuildWithDiagnostics("(shift k)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'shift'"));
    }

    [Fact]
    public void Shift_RejectsNonSymbolContinuationName()
    {
        var (_, diag) = BuildWithDiagnostics("(shift 5 body)");
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("'shift' continuation name must be an identifier")
        );
    }

    [Fact]
    public void ShiftAndResetRemainOrdinaryIdentifiersInValuePosition()
    {
        // (let ([reset 3]) reset) — `reset` as a binding name in non-head position must still parse
        // as an ordinary identifier. Same regression check for `shift`.
        var prog = Build("(let ([reset 3]) reset)");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("reset", let.VarName);
        var name = Assert.IsType<AstNode.Name>(let.Body);
        Assert.Equal("reset", name.Value);

        var prog2 = Build("(let ([shift 4]) shift)");
        var let2 = Assert.IsType<AstNode.Let>(prog2.TopLevelForms[0]);
        Assert.Equal("shift", let2.VarName);
    }
}

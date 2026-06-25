using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Ast;

public class AstBuilderControlPromptTests
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
    public void Prompt_BuildsPromptNode()
    {
        var prog = Build("(prompt 5)");
        var p = Assert.IsType<AstNode.Prompt>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.IntLit>(p.Body);
    }

    [Fact]
    public void PromptAt_BuildsPromptAtNode()
    {
        var prog = Build("(prompt t 5)");
        var p = Assert.IsType<AstNode.PromptAt>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.Name>(p.Tag);
        Assert.IsType<AstNode.IntLit>(p.Body);
    }

    [Fact]
    public void Control_BuildsControlNode()
    {
        var prog = Build("(prompt (control k 7))");
        var p = Assert.IsType<AstNode.Prompt>(prog.TopLevelForms[0]);
        var c = Assert.IsType<AstNode.Control>(p.Body);
        Assert.Equal("k", c.ContName);
    }

    [Fact]
    public void ControlAt_BuildsControlAtNode()
    {
        var prog = Build("(prompt t (control t k 7))");
        var p = Assert.IsType<AstNode.PromptAt>(prog.TopLevelForms[0]);
        var c = Assert.IsType<AstNode.ControlAt>(p.Body);
        Assert.IsType<AstNode.Name>(c.Tag);
        Assert.Equal("k", c.ContName);
    }

    [Fact]
    public void CallComp_BuildsCallCompNode()
    {
        var prog = Build("(prompt (call/comp f))");
        var p = Assert.IsType<AstNode.Prompt>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.CallComp>(p.Body);
    }

    [Fact]
    public void CallCompAt_PutsTagAfterFunction()
    {
        // Surface syntax is (call/comp f tag) — tag is the second positional arg, matching Racket.
        var prog = Build("(prompt t (call/comp f t))");
        var p = Assert.IsType<AstNode.PromptAt>(prog.TopLevelForms[0]);
        var c = Assert.IsType<AstNode.CallCompAt>(p.Body);
        Assert.IsType<AstNode.Name>(c.Tag);
        Assert.IsType<AstNode.Name>(c.Function);
    }

    [Fact]
    public void MakePromptTag_BuildsNode()
    {
        var prog = Build("(make-prompt-tag)");
        Assert.IsType<AstNode.MakePromptTag>(prog.TopLevelForms[0]);
    }

    [Fact]
    public void MakePromptTag_RejectsArguments()
    {
        var (_, diag) = BuildWithDiagnostics("(make-prompt-tag 1)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void Control_RejectsNonSymbolContName()
    {
        var (_, diag) = BuildWithDiagnostics("(prompt (control 5 7))");
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("'control' continuation name must be an identifier")
        );
    }

    [Fact]
    public void Prompt_RejectsTooManyArguments()
    {
        var (_, diag) = BuildWithDiagnostics("(prompt 1 2 3)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void PromptControlMakePromptTagRemainOrdinaryIdentifiersInValuePosition()
    {
        // Ensure the new keywords don't accidentally collide with let bindings.
        var prog = Build("(let ([prompt 1]) prompt)");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("prompt", let.VarName);

        var prog2 = Build("(let ([control 2]) control)");
        var let2 = Assert.IsType<AstNode.Let>(prog2.TopLevelForms[0]);
        Assert.Equal("control", let2.VarName);
    }

    [Fact]
    public void ResetAt_BuildsResetAtNode()
    {
        var prog = Build("(reset t 5)");
        var r = Assert.IsType<AstNode.ResetAt>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.Name>(r.Tag);
        Assert.IsType<AstNode.IntLit>(r.Body);
    }

    [Fact]
    public void ShiftAt_BuildsShiftAtNode()
    {
        var prog = Build("(reset t (shift t k 7))");
        var r = Assert.IsType<AstNode.ResetAt>(prog.TopLevelForms[0]);
        var s = Assert.IsType<AstNode.ShiftAt>(r.Body);
        Assert.Equal("k", s.ContName);
    }
}

namespace ZScript.Compiler.Tests.Types;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;
using Xunit;

public class ExhaustivenessTests
{
    private static DiagnosticBag CheckMatch(string source, Action<ExhaustivenessChecker>? setup = null)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        var env = TypeEnv.CreateRoot();
        var checker = new ExhaustivenessChecker(diag, env);
        setup?.Invoke(checker);

        foreach (var form in program.TopLevelForms)
        {
            if (form is AstNode.Match match)
                checker.Check(match, null);
        }

        return diag;
    }

    [Fact]
    public void WildcardIsExhaustive()
    {
        var diag = CheckMatch("(match x [_ 0])");
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void VariableIsExhaustive()
    {
        var diag = CheckMatch("(match x [y (+ y 1)])");
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void LiteralsWithoutWildcard_Warns()
    {
        var diag = CheckMatch("(match x [1 \"one\"] [2 \"two\"])");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("exhaustive"));
    }

    [Fact]
    public void UnionMissingCase_ReportsError()
    {
        var diag = new DiagnosticBag();
        var env = TypeEnv.CreateRoot();
        var checker = new ExhaustivenessChecker(diag, env);
        checker.RegisterUnion("Shape", ["Circle", "Rect"]);

        // Build a match with only one case
        var match = new AstNode.Match(
            new AstNode.Name("s", SourceSpan.None),
            [
                new MatchArm(
                    new Pattern.Constructor("Circle", [new Pattern.Variable("r", SourceSpan.None)], SourceSpan.None),
                    new AstNode.IntLit(1, SourceSpan.None),
                    SourceSpan.None)
            ],
            SourceSpan.None);

        checker.Check(match, "Shape");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Rect"));
    }

    [Fact]
    public void UnionAllCasesCovered_NoError()
    {
        var diag = new DiagnosticBag();
        var env = TypeEnv.CreateRoot();
        var checker = new ExhaustivenessChecker(diag, env);
        checker.RegisterUnion("Bool", ["True", "False"]);

        var match = new AstNode.Match(
            new AstNode.Name("b", SourceSpan.None),
            [
                new MatchArm(
                    new Pattern.Constructor("True", [], SourceSpan.None),
                    new AstNode.IntLit(1, SourceSpan.None), SourceSpan.None),
                new MatchArm(
                    new Pattern.Constructor("False", [], SourceSpan.None),
                    new AstNode.IntLit(0, SourceSpan.None), SourceSpan.None)
            ],
            SourceSpan.None);

        checker.Check(match, "Bool");
        Assert.False(diag.HasErrors);
    }
}

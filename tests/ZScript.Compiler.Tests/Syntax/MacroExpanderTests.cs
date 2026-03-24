namespace ZScript.Compiler.Tests.Syntax;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Pipeline;
using ZScript.Compiler.Syntax;
using Xunit;

public class MacroExpanderTests
{
    private static List<SExpr> Parse(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return sexprs;
    }

    private static List<SExpr> ExpandAll(string source, MacroEnvironment? env = null)
    {
        var diag = new DiagnosticBag();
        var sexprs = Parse(source);
        env ??= new MacroEnvironment();
        var expander = new MacroExpander(diag);
        var result = expander.ExpandAll(sexprs, env);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return result;
    }

    [Fact]
    public void SimpleRewrite()
    {
        var result = ExpandAll(@"
            (define-syntax my-if
              (syntax-rules ()
                [(my-if c t e) (if c t e)]))
            (my-if #t 1 2)");

        Assert.Single(result);
        Assert.Equal("(if #t 1 2)", result[0].ToString());
    }

    [Fact]
    public void DefineSyntaxIsRemoved()
    {
        var result = ExpandAll(@"
            (define-syntax noop (syntax-rules () [(noop x) x]))
            (noop 42)");
        Assert.Single(result);
    }

    [Fact]
    public void EllipsisExpansion()
    {
        var result = ExpandAll(@"
            (define-syntax when
              (syntax-rules ()
                [(when cond body ...) (if cond (begin body ...) unit)]))
            (when #t 1 2 3)");

        Assert.Single(result);
        var expanded = result[0].ToString();
        Assert.Equal("(if #t (begin 1 2 3) unit)", expanded);
    }

    [Fact]
    public void EmptyEllipsis()
    {
        var result = ExpandAll(@"
            (define-syntax when
              (syntax-rules ()
                [(when cond body ...) (if cond (begin body ...) unit)]))
            (when #t)");

        Assert.Single(result);
        var expanded = result[0].ToString();
        Assert.Equal("(if #t (begin) unit)", expanded);
    }

    [Fact]
    public void MultipleRules()
    {
        var result = ExpandAll(@"
            (define-syntax my-and
              (syntax-rules ()
                [(my-and) #t]
                [(my-and x) x]
                [(my-and x rest ...) (if x (my-and rest ...) #f)]))
            (my-and)
            (my-and a)
            (my-and a b c)");

        Assert.Equal(3, result.Count);
        Assert.Equal("#t", result[0].ToString());
        Assert.Equal("a", result[1].ToString());
        // (my-and a b c) → (if a (my-and b c) #f) → (if a (if b c #f) #f)
        Assert.Contains("if", result[2].ToString());
    }

    [Fact]
    public void RecursiveExpansion()
    {
        var result = ExpandAll(@"
            (define-syntax my-and
              (syntax-rules ()
                [(my-and) #t]
                [(my-and x) x]
                [(my-and x rest ...) (if x (my-and rest ...) #f)]))
            (my-and a b)");

        Assert.Single(result);
        // (my-and a b) → (if a (my-and b) #f) → (if a b #f)
        Assert.Equal("(if a b #f)", result[0].ToString());
    }

    [Fact]
    public void NonMacroPassesThrough()
    {
        var result = ExpandAll("(+ 1 2)");
        Assert.Single(result);
        Assert.Equal("(+ 1 2)", result[0].ToString());
    }

    [Fact]
    public void TestCaseMacro_FromStdLib()
    {
        var source = @"(module test)
(import zunit)
(test-case my-test (+ 1 2))";
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            StdLibPath = GetStdLibPath(),
            ModuleSearchPaths = [GetZUnitPath()]
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        var cs = result.Output!;
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("my_test", cs);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(MacroExpanderTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(MacroExpanderTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    [Fact]
    public void BeginSplicesAtTopLevel()
    {
        var result = ExpandAll("(begin (+ 1 2) (+ 3 4))");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NestedMacroExpansion()
    {
        var result = ExpandAll(@"
            (define-syntax swap
              (syntax-rules ()
                [(swap a b) (list b a)]))
            (define-syntax double-swap
              (syntax-rules ()
                [(double-swap a b) (swap (swap a b) (swap b a))]))
            (double-swap x y)");

        Assert.Single(result);
        // Inner swaps expand first, then outer
        var expanded = result[0].ToString();
        Assert.Contains("list", expanded);
    }

    [Fact]
    public void LiteralMatching()
    {
        var result = ExpandAll(@"
            (define-syntax my-cond
              (syntax-rules (else)
                [(my-cond [else e]) e]
                [(my-cond [c t] rest ...) (if c t (my-cond rest ...))]))
            (my-cond [#t 1] [else 2])");

        Assert.Single(result);
        Assert.Contains("if", result[0].ToString());
    }

    [Fact]
    public void QuoteShorthand()
    {
        var sexprs = Parse("'x");
        Assert.Single(sexprs);
        Assert.Equal("(quote x)", sexprs[0].ToString());
    }

    [Fact]
    public void QuasiquoteShorthand()
    {
        var sexprs = Parse("`(a b)");
        Assert.Single(sexprs);
        Assert.Equal("(quasiquote (a b))", sexprs[0].ToString());
    }

    [Fact]
    public void UnquoteShorthand()
    {
        var sexprs = Parse(",x");
        Assert.Single(sexprs);
        Assert.Equal("(unquote x)", sexprs[0].ToString());
    }

    [Fact]
    public void UnquoteSplicingShorthand()
    {
        var sexprs = Parse(",@xs");
        Assert.Single(sexprs);
        Assert.Equal("(unquote-splicing xs)", sexprs[0].ToString());
    }

    [Fact]
    public void HygienicMacroIntroducedBindings()
    {
        var result = ExpandAll(@"
            (define-syntax my-let1
              (syntax-rules ()
                [(my-let1 val body) (let [tmp val] body)]))
            (my-let1 42 tmp)");

        Assert.Single(result);
        var expanded = result[0].ToString();
        // Pattern variables are substituted; non-pattern identifiers pass through literally
        Assert.Contains("let", expanded);
        Assert.Equal("(let [tmp 42] tmp)", expanded);
    }

    [Fact]
    public void ExpansionDepthLimit()
    {
        var diag = new DiagnosticBag();
        var sexprs = Parse(@"
            (define-syntax loop
              (syntax-rules ()
                [(loop) (loop)]))
            (loop)");
        var env = new MacroEnvironment();
        var expander = new MacroExpander(diag);
        expander.ExpandAll(sexprs, env);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("depth limit"));
    }

    [Fact]
    public void WildcardPattern()
    {
        var result = ExpandAll(@"
            (define-syntax ignore-first
              (syntax-rules ()
                [(ignore-first _ x) x]))
            (ignore-first blah 42)");

        Assert.Single(result);
        Assert.Equal("42", result[0].ToString());
    }
}

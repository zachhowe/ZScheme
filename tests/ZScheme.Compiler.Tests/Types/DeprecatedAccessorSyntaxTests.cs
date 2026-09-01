using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     The deprecated <c>Type/member</c> accessor spelling still resolves, and says so
///     (ZS0006). See <see cref="AccessorNaming" />.
/// </summary>
public class DeprecatedAccessorSyntaxTests
{
    private static (AstNode.Program program, TypeEnv env, DiagnosticBag diag) Infer(
        string source,
        bool warn = true
    )
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(source, "test.zs", diag).Tokenize();
        var sexprs = new SExprParser(tokens, diag).ParseAll();
        var program = new AstBuilder(diag).BuildProgram(sexprs);

        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diag) { WarnDeprecatedAccessorSyntax = warn };
        inferer.Infer(program, env);
        inferer.Resolve(program);

        return (program, env, diag);
    }

    private static Diagnostic[] AccessorWarnings(DiagnosticBag diag) =>
        diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.DeprecatedAccessorSyntax).ToArray();

    // ---- The modern spelling is the one that gets bound ----

    [Theory]
    [InlineData("(define-record Point [x : Int] [y : Int])", "Point-x", "Point/x")]
    [InlineData("(define-struct Point [x : Int] [y : Int])", "Point-x", "Point/x")]
    public void Declaration_BindsHyphenatedAccessor_NotSlashed(
        string source,
        string modern,
        string legacy
    )
    {
        var (_, env, diag) = Infer(source);
        Assert.False(diag.HasErrors);
        Assert.NotNull(env.Lookup(modern));
        Assert.Null(env.Lookup(legacy));
    }

    [Fact]
    public void ClassDecl_BindsHyphenatedFieldAndMethodAccessors()
    {
        var (_, env, diag) = Infer(
            @"
(define-class Counter
  [count : Int]
  (define (Bump) : Int (+ count 1)))"
        );
        Assert.False(diag.HasErrors);
        Assert.NotNull(env.Lookup("Counter-count"));
        Assert.NotNull(env.Lookup("Counter-Bump"));
        Assert.Null(env.Lookup("Counter/count"));
    }

    [Fact]
    public void InterfaceDecl_BindsHyphenatedMethodAccessor()
    {
        var (_, env, diag) = Infer("(define-interface IGreeter (Greet [] : String))");
        Assert.False(diag.HasErrors);
        Assert.NotNull(env.Lookup("IGreeter-Greet"));
        Assert.Null(env.Lookup("IGreeter/Greet"));
    }

    // ---- The old spelling still resolves, and warns ----

    [Fact]
    public void LegacyAccessor_StillResolves_AndWarns()
    {
        var (_, _, diag) = Infer(
            @"
(define-record Point [x : Int] [y : Int])
(define (get [p : Point]) : Int (Point/x p))"
        );
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var warning = Assert.Single(AccessorWarnings(diag));
        Assert.Equal(["Point/x", "Point-x"], warning.Data);
        Assert.Contains("Point-x", warning.Message);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void LegacyAccessor_WarningSpansOnlyTheAccessorAtom()
    {
        const string source = """
            (define-record Point [x : Int] [y : Int])
            (define (get [p : Point]) : Int (Point/x p))
            """;
        var (_, _, diag) = Infer(source);

        var warning = Assert.Single(AccessorWarnings(diag));
        var lines = source.Split('\n');
        var atom = lines[warning.Span.Line - 1]
            .Substring(warning.Span.Column - 1, warning.Span.Length);
        Assert.Equal("Point/x", atom);
    }

    [Fact]
    public void LegacyAccessor_WarnsOncePerOccurrence()
    {
        var (_, _, diag) = Infer(
            @"
(define-record Point [x : Int] [y : Int])
(define (sum [p : Point]) : Int (+ (Point/x p) (Point/y p)))"
        );
        Assert.False(diag.HasErrors);
        Assert.Equal(2, AccessorWarnings(diag).Length);
    }

    [Fact]
    public void LegacyClassMemberAccess_StillResolves_AndWarns()
    {
        var (_, _, diag) = Infer(
            @"
(define-class Counter
  [count : Int]
  (define (Bump) : Int (+ count 1)))
(define (read [c : Counter]) : Int (+ (Counter/count c) (Counter/Bump c)))"
        );
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(2, AccessorWarnings(diag).Length);
    }

    // A record whose *name* contains the '-' separator: the member name can only come from
    // the accessor registry, never from splitting the string (which would yield "v-a").
    [Fact]
    public void HyphenatedTypeName_ResolvesWholeMemberName()
    {
        var (_, env, diag) = Infer("(define-struct s-v [a : Int])");
        Assert.False(diag.HasErrors);
        Assert.NotNull(env.Lookup("s-v-a"));

        var (_, _, legacyDiag) = Infer(
            @"
(define-struct s-v [a : Int])
(define (get [s : s-v]) : Int (s-v/a s))"
        );
        Assert.False(legacyDiag.HasErrors, string.Join("\n", legacyDiag.Diagnostics));
        var warning = Assert.Single(AccessorWarnings(legacyDiag));
        Assert.Equal(["s-v/a", "s-v-a"], warning.Data);
    }

    // ---- The warning is suppressible; resolution is not affected ----

    [Fact]
    public void WarnDeprecatedAccessorSyntaxFalse_SilencesWarning_ButStillResolves()
    {
        var (_, _, diag) = Infer(
            @"
(define-record Point [x : Int] [y : Int])
(define (get [p : Point]) : Int (Point/x p))",
            false
        );
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Empty(AccessorWarnings(diag));
    }

    // ---- The fallback does not swallow genuine mistakes ----

    [Fact]
    public void UnknownSlashName_WithNoMatchingAccessor_StillReportsUndefined()
    {
        var (_, _, diag) = Infer("(define (get) : Int (Nope/missing 1))");
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.UndefinedVariable && d.Message.Contains("Nope/missing")
        );
        Assert.Empty(AccessorWarnings(diag));
    }

    // `foo-bar` exists but is an ordinary function, not an accessor for a type named `foo`,
    // so `foo/bar` must not be silently redirected to it.
    [Fact]
    public void UnknownSlashName_MatchingPlainFunction_IsNotTreatedAsAccessor()
    {
        var (_, _, diag) = Infer(
            @"
(define (foo-bar [n : Int]) : Int n)
(define (use) : Int (foo/bar 1))"
        );
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.UndefinedVariable && d.Message.Contains("foo/bar")
        );
        Assert.Empty(AccessorWarnings(diag));
    }

    // ---- AccessorNaming itself ----

    [Theory]
    [InlineData("Point/x", "Point-x")]
    [InlineData("s-v/a", "s-v-a")]
    [InlineData("HttpResponse/status-code", "HttpResponse-status-code")]
    [InlineData("stdlib/geom/Point/x", "stdlib/geom/Point-x")] // splits at the last '/'
    public void TryModernizeLegacyName_SplitsAtLastSlash(string legacy, string expected) =>
        Assert.Equal(expected, AccessorNaming.TryModernizeLegacyName(legacy));

    [Theory]
    [InlineData("Point-x")]
    [InlineData("plain")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    public void TryModernizeLegacyName_ReturnsNullWhenNotApplicable(string name) =>
        Assert.Null(AccessorNaming.TryModernizeLegacyName(name));
}

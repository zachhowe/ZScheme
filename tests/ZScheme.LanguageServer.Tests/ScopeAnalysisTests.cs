using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class ScopeAnalysisTests
{
    /// <summary>Occurrences of "xx": 1 = param binding, 2 = let binding,
    ///     3 = use in the let value (bound by the param), 4 = use in the let body
    ///     (bound by the let).</summary>
    private const string Shadow = """
        (module test)
        (define (f [xx : Int]) : Int
          (let ([xx (* xx 2)])
            (+ xx 1)))
        """;

    private static (DocumentState State, string Src) Analyze(string src)
    {
        var (svc, uri) = LspTestSession.Open(src);
        return (svc.GetDocument(uri)!, src);
    }

    private static void AssertOccurrences(
        IReadOnlyList<SourceSpan>? occurrences,
        string src,
        string token,
        params int[] expectedTokenOccurrences
    )
    {
        Assert.NotNull(occurrences);
        var actual = occurrences!
            .Select(s => (s.Line, s.Column))
            .OrderBy(p => p.Line)
            .ThenBy(p => p.Column)
            .ToList();
        var expected = expectedTokenOccurrences
            .Select(n => LspTestSession.Locate(src, token, n))
            .OrderBy(p => p.Line)
            .ThenBy(p => p.Col)
            .Select(p => (p.Line, p.Col))
            .ToList();
        Assert.Equal(expected, actual);
    }

    /// <summary>Occurrences of "gg": 1 = letrec binding, 2 = the recursive self-reference in
    ///     its own value, 3 = the use in the body. A `let` binder would not see occurrence 2 —
    ///     its value is outside the new scope — which is exactly what letrec changes.</summary>
    private const string Recursive = """
        (module test)
        (define (f [n : Int]) : Int
          (letrec ([gg (lambda ([k : Int]) : Int (if (= k 0) 0 (gg (- k 1))))])
            (gg n)))
        """;

    /// <summary>Occurrences of "pp": 1 = binding, 2 = reference from the sibling's value,
    ///     3 = the body use. A sibling's value is in scope for the whole group.</summary>
    private const string Mutual = """
        (module test)
        (define (f [n : Int]) : Int
          (letrec ([pp (lambda ([k : Int]) : Int (if (= k 0) 0 (qq (- k 1))))]
                   [qq (lambda ([k : Int]) : Int (pp (- k 1)))])
            (pp n)))
        """;

    [Fact]
    public void LocalOccurrences_FromLetrecBinding_IncludesItsOwnValue()
    {
        var (state, src) = Analyze(Recursive);
        var (line, col) = LspTestSession.Locate(src, "gg", 1);

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "gg", 1, 2, 3);
    }

    [Fact]
    public void LocalOccurrences_FromLetrecSelfReference_ResolvesToItsBinder()
    {
        var (state, src) = Analyze(Recursive);
        var (line, col) = LspTestSession.Locate(src, "gg", 2);

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "gg", 1, 2, 3);
    }

    [Fact]
    public void LocalOccurrences_FromLetrecBinding_IncludesSiblingValueUses()
    {
        var (state, src) = Analyze(Mutual);
        var (line, col) = LspTestSession.Locate(src, "pp", 1);

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "pp", 1, 2, 3);
    }

    [Fact]
    public void LocalOccurrences_FromLetBindingSite_IncludesBinderAndBodyUses()
    {
        var (state, src) = Analyze(Shadow);
        var (line, col) = LspTestSession.Locate(src, "xx", 2);

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "xx", 2, 4);
    }

    [Fact]
    public void LocalOccurrences_FromShadowedParam_ExcludesInnerScope()
    {
        var (state, src) = Analyze(Shadow);
        var (line, col) = LspTestSession.Locate(src, "xx", 1); // the parameter

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        // The let's value is outside the let's scope, so occurrence 3 belongs
        // to the parameter; occurrence 4 is shadowed.
        AssertOccurrences(occurrences, src, "xx", 1, 3);
    }

    [Fact]
    public void LocalOccurrences_FromUseSite_ResolvesToInnermostBinder()
    {
        var (state, src) = Analyze(Shadow);
        var (line, col) = LspTestSession.Locate(src, "xx", 4); // use in the let body

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "xx", 2, 4);
    }

    [Fact]
    public void LocalOccurrences_OnTopLevelName_ReturnsNull()
    {
        var (state, src) = Analyze(Shadow);
        var (line, col) = LspTestSession.Locate(src, "f");

        Assert.Null(ScopeAnalysis.LocalOccurrences(state.Ast!, line, col));
    }

    [Fact]
    public void LocalOccurrences_PatternVariable_ConfinedToItsArm()
    {
        var src = """
            (module test)
            (define (g [o : (Option Int)]) : Int
              (match o
                [(Some vv) (+ vv 1)]
                [None 0]))
            """;
        var (state, _) = Analyze(src);
        var (line, col) = LspTestSession.Locate(src, "vv", 1); // the pattern variable

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        AssertOccurrences(occurrences, src, "vv", 1, 2);
    }

    [Fact]
    public void OccurrencesBoundLocally_CoversAllBindersOfTheName()
    {
        var (state, src) = Analyze(Shadow);

        var spans = ScopeAnalysis.OccurrencesBoundLocally(state.Ast!, "xx");

        // Param binding + let binding + both uses: every "xx" is locally bound.
        Assert.Equal(4, spans.Count);
    }

    [Fact]
    public void BindingsInScopeAt_RespectsFormExtents()
    {
        var (state, src) = Analyze(Shadow);

        // Inside the let body the binding is visible.
        var (line, col) = LspTestSession.Locate(src, "(+ xx 1)");
        var inScope = ScopeAnalysis.BindingsInScopeAt(state.Ast!, src, line, col + 1);
        Assert.Contains(inScope, b => b.Name == "xx");

        // At the very top of the file nothing local is visible.
        var atTop = ScopeAnalysis.BindingsInScopeAt(state.Ast!, src, 1, 1);
        Assert.Empty(atTop);
    }

    [Fact]
    public void LocalOccurrences_LetStar_BindingsResolveIndividually()
    {
        var src = """
            (module test)
            (define (h) : Int
              (let* ([aa 1] [bb (+ aa 1)])
                (+ aa bb)))
            """;
        var (state, _) = Analyze(src);
        var (line, col) = LspTestSession.Locate(src, "aa", 1); // first binding

        var occurrences = ScopeAnalysis.LocalOccurrences(state.Ast!, line, col);

        // Binding + use in bb's value + use in the body.
        AssertOccurrences(occurrences, src, "aa", 1, 2, 3);
    }
}

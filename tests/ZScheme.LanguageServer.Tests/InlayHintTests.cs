using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public sealed class InlayHintTests
{
    private static readonly Range FullDocument = new(new Position(0, 0), new Position(10000, 0));

    private static IReadOnlyList<InlayHint> Hints(string src, Range? range = null)
    {
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        return InlayHintHandler.Collect(state, range ?? FullDocument);
    }

    private static IEnumerable<string> Labels(IEnumerable<InlayHint> hints) =>
        hints.Select(h => h.Label.String ?? "");

    [Fact]
    public void Inlay_DefineValue_ShowsInferredType()
    {
        var src = """
            (module test)
            (define answer 42)
            """;
        var hints = Hints(src);

        var hint = Assert.Single(hints);
        Assert.Equal(": Int", hint.Label.String);
        Assert.Equal(InlayHintKind.Type, hint.Kind);
        // Positioned at the end of "answer" on line 2 (0-based line 1).
        Assert.Equal(1, hint.Position.Line);
    }

    [Fact]
    public void Inlay_UnannotatedLet_ShowsType()
    {
        var src = """
            (module test)
            (define (f [y : Int]) : Int (let ([x 42]) x))
            """;
        var hints = Hints(src);

        Assert.Contains(Labels(hints), l => l == ": Int");
    }

    [Fact]
    public void Inlay_AnnotatedLet_NoHint()
    {
        var src = """
            (module test)
            (define (f [y : Int]) : Int (let ([x : Int 42]) x))
            """;
        var hints = Hints(src);

        // Param, return, and let binding are all annotated → no hints.
        Assert.Empty(hints);
    }

    [Fact]
    public void Inlay_UnannotatedLambdaParam_AddsHint()
    {
        var unannotated = """
            (module test)
            (define (f) : Int ((lambda (x) (+ x 1)) 5))
            """;
        var annotated = """
            (module test)
            (define (f) : Int ((lambda ([x : Int]) (+ x 1)) 5))
            """;

        // The only difference is the parameter annotation, so the unannotated form has
        // exactly one extra hint: the inferred parameter type.
        Assert.Equal(Hints(annotated).Count + 1, Hints(unannotated).Count);
        Assert.Contains(Labels(Hints(unannotated)), l => l == ": Int");
    }

    [Fact]
    public void Inlay_UnannotatedReturnType_ShowsHint()
    {
        var src = """
            (module test)
            (define (add [a : Int] [b : Int]) (+ a b))
            """;
        var hints = Hints(src);

        // Params are annotated; only the inferred return type is hinted.
        var hint = Assert.Single(hints);
        Assert.Equal(": Int", hint.Label.String);
    }

    [Fact]
    public void Inlay_RangeGating_ExcludesOtherLines()
    {
        var src = """
            (module test)
            (define first 1)
            (define second 2)
            """;
        // Restrict to line 2 (0-based line 1) only.
        var onlyLine2 = new Range(new Position(1, 0), new Position(1, 100));
        var hints = Hints(src, onlyLine2);

        var hint = Assert.Single(hints);
        Assert.Equal(1, hint.Position.Line);
    }

    [Fact]
    public void Inlay_GenericBinding_RendersTypeVariables()
    {
        var src = """
            (module test)
            (define poly (lambda (x) x))
            """;
        var labels = Labels(Hints(src)).ToList();

        Assert.Contains(labels, l => l.Contains("^a"));
        Assert.DoesNotContain(labels, l => l.Contains("?"));
        Assert.DoesNotContain(labels, l => l.Contains("t0"));
    }

    [Fact]
    public void Inlay_CallSite_ShowsParameterNames()
    {
        var src = """
            (module test)
            (define (scale [factor : Int] [amount : Int]) : Int (* factor amount))
            (define (run) : Int (scale 2 3))
            """;
        var hints = Hints(src);

        var factor = Assert.Single(hints, h => h.Label.String == "factor:");
        Assert.Equal(InlayHintKind.Parameter, factor.Kind);
        Assert.Single(hints, h => h.Label.String == "amount:");
    }

    [Fact]
    public void Inlay_CallSite_SuppressesArgAlreadyNamedLikeParam()
    {
        var src = """
            (module test)
            (define (scale [factor : Int] [amount : Int]) : Int (* factor amount))
            (define (run [factor : Int]) : Int (scale factor 3))
            """;
        var hints = Hints(src);

        // Passing a variable named exactly like the parameter needs no hint.
        Assert.DoesNotContain(hints, h => h.Label.String == "factor:");
        Assert.Single(hints, h => h.Label.String == "amount:");
    }

    [Fact]
    public void Inlay_CallSite_UnknownCallee_EmitsNoNameHints()
    {
        var src = """
            (module test)
            (define (run) : Int (+ 1 2))
            """;
        var hints = Hints(src);

        Assert.DoesNotContain(hints, h => h.Kind == InlayHintKind.Parameter);
    }
}

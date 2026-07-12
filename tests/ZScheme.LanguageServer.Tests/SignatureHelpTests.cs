using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class SignatureHelpTests
{
    private static SignatureHelp? Help(string src, int line, int col)
    {
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        return SignatureHelpHandler.ResolveSignatureHelp(state, svc.Index, line, col);
    }

    [Fact]
    public void Signature_OverloadedCallee_ShowsAllSignatures()
    {
        const string LibA = """
            (module liba)
            (define (combine [a : Int]) : Int a)
            (export combine)
            """;
        const string LibB = """
            (module libb)
            (define (combine [a : Int] [b : Int]) : Int (+ a b))
            (export combine)
            """;
        const string App = """
            (module app)
            (import xpkg/liba)
            (import xpkg/libb)
            (define (run) : Int (combine 1 2))
            """;

        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string>
            {
                ["liba.zs"] = LibA,
                ["libb.zs"] = LibB,
                ["app.zs"] = App,
            }
        );
        ws.Open("liba.zs");
        ws.Open("libb.zs");
        var appState = ws.Open("app.zs");
        var (line, col) = ws.Locate("app.zs", "combine", 1); // the call site (only occurrence)

        var help = SignatureHelpHandler.ResolveSignatureHelp(
            appState,
            ws.Service.Index,
            line,
            col + 8 // move the cursor inside the call, past "combine "
        );

        Assert.NotNull(help);
        // Both overloads are offered; the two-argument one matches the call arity.
        Assert.Equal(2, help!.Signatures.Count());
        var active = help.Signatures.ElementAt(help.ActiveSignature!.Value);
        Assert.Equal(2, active.Parameters!.Count());
    }

    [Fact]
    public void Signature_InsideCall_ShowsParametersActiveZero()
    {
        var src = """
            (module test)
            (define (add [a : Int] [b : Int]) : Int (+ a b))
            (define (run) : Int (add 10 20))
            """;
        var (line, col) = LspTestSession.Locate(src, "10");
        var help = Help(src, line, col);

        Assert.NotNull(help);
        var sig = Assert.Single(help!.Signatures);
        Assert.Equal(2, sig.Parameters!.Count());
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void Signature_AfterFirstArg_ActiveParameterAdvances()
    {
        var src = """
            (module test)
            (define (add [a : Int] [b : Int]) : Int (+ a b))
            (define (run) : Int (add 10 20))
            """;
        var (line, col) = LspTestSession.Locate(src, "20");
        var help = Help(src, line, col);

        Assert.NotNull(help);
        Assert.Equal(1, help!.ActiveParameter);
    }

    [Fact]
    public void Signature_NestedCall_ResolvesInnerCallee()
    {
        var src = """
            (module test)
            (define (inc [x : Int]) : Int (+ x 1))
            (define (add [a : Int] [b : Int]) : Int (+ a b))
            (define (run) : Int (add (inc 5) 2))
            """;
        var (line, col) = LspTestSession.Locate(src, "5");
        var help = Help(src, line, col);

        Assert.NotNull(help);
        // Inner call is (inc 5) → one parameter, not add's two.
        var sig = Assert.Single(help!.Signatures);
        Assert.Single(sig.Parameters!);
    }

    [Fact]
    public void Signature_Variadic_LastParameterMarkedAndActiveClamped()
    {
        var src = """
            (module test)
            (define (sum-all [first : Int] [rest : Int ...]) : Int first)
            (define (run) : Int (sum-all 1 2 3 4))
            """;
        var (line, col) = LspTestSession.Locate(src, "4");
        var help = Help(src, line, col);

        Assert.NotNull(help);
        var sig = Assert.Single(help!.Signatures);
        Assert.EndsWith("...", sig.Parameters!.Last().Label.Label);
        // Four args against two parameters (last variadic) → clamped to the last parameter.
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void Signature_NotInsideCall_ReturnsNull()
    {
        var src = """
            (module test)
            (define answer 42)
            """;
        // Cursor on the value binding, not inside any application.
        var (line, col) = LspTestSession.Locate(src, "answer");
        var help = Help(src, line, col);

        Assert.Null(help);
    }

    [Fact]
    public void Signature_CalleeNotAName_ReturnsNull()
    {
        var src = """
            (module test)
            (define (run) : Int ((lambda (x) x) 5))
            """;
        // The callee in ((lambda ...) 5) is a lambda, not a named function.
        var (line, col) = LspTestSession.Locate(src, "5");
        var help = Help(src, line, col);

        Assert.Null(help);
    }
}

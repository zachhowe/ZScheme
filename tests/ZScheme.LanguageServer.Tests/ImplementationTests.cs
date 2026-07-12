using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class ImplementationTests
{
    private const string Lib = """
        (module lib)
        (define-interface ISpeaker
          (Speak [] : String))
        (export ISpeaker)
        """;

    private const string Impls = """
        (module impls)
        (import ipkg/lib)
        (define-class Dog : ISpeaker
          (define (Speak) : String "woof"))
        (define-class Cat : ISpeaker
          (define (Speak) : String "meow"))
        """;

    private static TempPackageWorkspace NewWorkspace()
    {
        return new TempPackageWorkspace(
            "ipkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["impls.zs"] = Impls }
        );
    }

    [Fact]
    public void CursorOnInterfaceDeclarationName_FindsClassesAcrossFiles()
    {
        using var ws = NewWorkspace();
        var libState = ws.Open("lib.zs");
        ws.Open("impls.zs");

        var (line, col) = ws.Locate("lib.zs", "ISpeaker"); // the declaration name
        var impls = ImplementationHandler.Resolve(libState, ws.Service.Index, line, col);

        Assert.Equal(2, impls.Count);
        Assert.Contains(impls, d => d.BareName == "Dog");
        Assert.Contains(impls, d => d.BareName == "Cat");
        Assert.All(impls, d => Assert.Equal(ws.PathOf("impls.zs"), d.File));
    }

    [Fact]
    public void CursorOnInterfaceUsage_FindsClasses()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        var implsState = ws.Open("impls.zs");

        // `ISpeaker` in (define-class Dog : ISpeaker ...)
        var (line, col) = ws.Locate("impls.zs", "ISpeaker", 2);
        var impls = ImplementationHandler.Resolve(implsState, ws.Service.Index, line, col);

        Assert.Equal(2, impls.Count);
    }

    [Fact]
    public void InterfaceExtension_IncludesTransitiveImplementors()
    {
        var source = """
            (define-interface IBase
              (Base [] : Int))
            (define-interface IDerived : IBase
              (Derived [] : Int))
            (define-class Impl : IDerived
              (define (Base) : Int 1)
              (define (Derived) : Int 2))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(source, "IBase");
        var impls = ImplementationHandler.Resolve(state, service.Index, line, col);

        Assert.Contains(impls, d => d.BareName == "IDerived" && d.Kind == SymbolKind.Interface);
        Assert.Contains(impls, d => d.BareName == "Impl" && d.Kind == SymbolKind.Class);
    }

    [Fact]
    public void NonInterfaceName_ReturnsEmpty()
    {
        var source = "(define x 1)";
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(source, "x");
        Assert.Empty(ImplementationHandler.Resolve(state, service.Index, line, col));
    }
}

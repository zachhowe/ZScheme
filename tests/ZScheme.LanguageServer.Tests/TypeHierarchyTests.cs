using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class TypeHierarchyTests
{
    private const string Src = """
        (module test)
        (define-interface IBase (Ping [] : Int))
        (define-interface IExtended : IBase (Pong [] : Int))
        (define-class Impl : IExtended
          (define (Ping) : Int 1)
          (define (Pong) : Int 2))
        """;

    private static (DocumentState State, AnalysisService Svc, string Uri) Open()
    {
        var (svc, uri) = LspTestSession.Open(Src, testName: "TypeHierarchy");
        return (svc.GetDocument(uri)!, svc, uri);
    }

    [Fact]
    public void Prepare_OnInterface_ReturnsItem()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "IBase", 1);

        var item = TypeHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri));

        Assert.NotNull(item);
        Assert.Equal("IBase", item!.Name);
    }

    [Fact]
    public void Prepare_OnFunction_ReturnsNull()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "Ping", 1);

        Assert.Null(TypeHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri)));
    }

    [Fact]
    public void Subtypes_AreDirectOnly()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "IBase", 1);
        var item = TypeHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri))!;

        var subtypes = TypeHierarchyHandler.Subtypes(svc.Index, item);

        // One level: IExtended extends IBase; Impl (transitive) is not listed here.
        var sub = Assert.Single(subtypes);
        Assert.Equal("IExtended", sub.Name);
    }

    [Fact]
    public void Supertypes_ComeFromDeclaredBaseList()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "Impl", 1);
        var item = TypeHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri))!;

        var supertypes = TypeHierarchyHandler.Supertypes(svc.Index, item);

        var super = Assert.Single(supertypes);
        Assert.Equal("IExtended", super.Name);
    }
}

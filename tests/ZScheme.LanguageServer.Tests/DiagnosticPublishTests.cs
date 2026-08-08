using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class DiagnosticPublishTests
{
    private static DocumentState StateWith(DiagnosticBag bag)
    {
        return new DocumentState(
            "file:///test.zs",
            1,
            "",
            null,
            bag,
            [],
            new Dictionary<string, SymbolInfo>(),
            new Dictionary<string, ZScheme.Compiler.Ast.AstNode.TypeAliasDecl>()
        );
    }

    [Fact]
    public void UnusedBindingCode_GetsUnnecessaryTag()
    {
        var bag = new DiagnosticBag();
        bag.Warning(
            "Unused binding 'x'",
            new SourceSpan("test.zs", 1, 7, 1),
            DiagnosticCodes.UnusedBinding,
            ["x"]
        );

        var published = TextDocumentSyncHandler.ConvertDiagnostics(
            DocumentUri.Parse("file:///test.zs"),
            StateWith(bag)
        );

        var diagnostic = Assert.Single(published);
        Assert.NotNull(diagnostic.Tags);
        Assert.Contains(DiagnosticTag.Unnecessary, diagnostic.Tags!);
    }

    [Fact]
    public void RedundantTypeQualifier_IsPublishedAsAGreyedOutHint()
    {
        var bag = new DiagnosticBag();
        bag.Hint(
            "'System.Text.StringBuilder' can be written as 'StringBuilder'",
            new SourceSpan("test.zs", 3, 19, "System.Text.".Length),
            DiagnosticCodes.RedundantTypeQualifier,
            ["StringBuilder", "System.Text"]
        );

        var published = TextDocumentSyncHandler.ConvertDiagnostics(
            DocumentUri.Parse("file:///test.zs"),
            StateWith(bag)
        );

        var diagnostic = Assert.Single(published);
        Assert.Equal(
            OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity.Hint,
            diagnostic.Severity
        );
        Assert.Contains(DiagnosticTag.Unnecessary, diagnostic.Tags!);
        Assert.Equal(DiagnosticCodes.RedundantTypeQualifier, diagnostic.Code!.Value.String);
    }

    [Fact]
    public void OtherCodes_GetNoTags()
    {
        var bag = new DiagnosticBag();
        bag.Error(
            "Undefined variable 'y'",
            new SourceSpan("test.zs", 1, 1, 1),
            DiagnosticCodes.UndefinedVariable,
            ["y"]
        );

        var published = TextDocumentSyncHandler.ConvertDiagnostics(
            DocumentUri.Parse("file:///test.zs"),
            StateWith(bag)
        );

        Assert.Null(Assert.Single(published).Tags);
    }

    [Fact]
    public void RelatedInfo_IsForwarded()
    {
        var bag = new DiagnosticBag();
        bag.Warning(
            "Non-exhaustive match",
            new SourceSpan("/abs/test.zs", 2, 3, 5),
            DiagnosticCodes.NonExhaustiveMatch,
            ["None/0"],
            [
                new DiagnosticRelatedInfo(
                    new SourceSpan("/abs/test.zs", 3, 5, 8),
                    "existing arm here"
                ),
            ]
        );

        var published = TextDocumentSyncHandler.ConvertDiagnostics(
            DocumentUri.Parse("file:///abs/test.zs"),
            StateWith(bag)
        );

        var diagnostic = Assert.Single(published);
        var related = Assert.Single(diagnostic.RelatedInformation!);
        Assert.Equal("existing arm here", related.Message);
        Assert.Equal(2, related.Location.Range.Start.Line);
        Assert.Equal(4, related.Location.Range.Start.Character);
        Assert.EndsWith("test.zs", related.Location.Uri.GetFileSystemPath());
    }

    [Fact]
    public void RelatedInfoWithNoFile_FallsBackToDocumentUri()
    {
        var bag = new DiagnosticBag();
        bag.Warning(
            "w",
            new SourceSpan("", 1, 1, 1),
            related: [new DiagnosticRelatedInfo(new SourceSpan("", 1, 1, 1), "here")]
        );

        var uri = DocumentUri.Parse("file:///doc.zs");
        var published = TextDocumentSyncHandler.ConvertDiagnostics(uri, StateWith(bag));

        var related = Assert.Single(Assert.Single(published).RelatedInformation!);
        Assert.Equal(uri, related.Location.Uri);
    }

    [Fact]
    public void NonExhaustiveMatch_CarriesExistingArmRelatedInfo()
    {
        var source = """
            (define-union Opt
              (Some [v : Int])
              (None))
            (define (f [o : Opt]) : Int
              (match o
                [(Some v) v]))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var published = TextDocumentSyncHandler.ConvertDiagnostics(DocumentUri.Parse(uri), state);

        var zs0002 = published.Single(d => d.Code?.String == DiagnosticCodes.NonExhaustiveMatch);
        var related = Assert.Single(zs0002.RelatedInformation!);
        Assert.Equal("existing arm here", related.Message);
    }
}

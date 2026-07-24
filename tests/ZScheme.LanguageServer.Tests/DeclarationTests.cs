using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     <c>textDocument/declaration</c> shares <see cref="DefinitionHandler" />'s resolver —
///     ZScheme has no declaration/definition split. These drive the handler itself, since
///     the bug being fixed was that the request was never served at all (the server
///     answered <c>Method not found</c>).
/// </summary>
public sealed class DeclarationTests
{
    private const string Src = """
        (module test)
        (define (square [x : Int]) : Int (* x x))
        (define (twice [n : Int]) : Int (square n))
        """;

    private static async Task<Location?> DeclareAsync(int line, int col)
    {
        var (service, uri) = LspTestSession.Open(Src, testName: nameof(DeclarationTests));
        var handler = new DeclarationHandler(service);

        var result = await handler.Handle(
            new DeclarationParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Position = new Position(line - 1, col - 1),
            },
            CancellationToken.None
        );

        return result?.SingleOrDefault()?.Location;
    }

    [Fact]
    public async Task Declaration_OnCallToTopLevelFunction_ResolvesToDefinition()
    {
        var (line, col) = LspTestSession.Locate(Src, "square", occurrence: 2);

        var location = await DeclareAsync(line, col);

        Assert.NotNull(location);
        Assert.Equal(1, location.Range.Start.Line); // 0-based: (define (square ...
        Assert.Equal(9, location.Range.Start.Character);
    }

    [Fact]
    public async Task Declaration_OnParameter_ResolvesToParameterName()
    {
        var location = await DeclareAsync(2, 37); // the "x" in (* x x)

        Assert.NotNull(location);
        Assert.Equal(1, location.Range.Start.Line);
        Assert.Equal(17, location.Range.Start.Character); // 0-based column of [x : Int]'s x
    }

    [Fact]
    public async Task Declaration_OnLiteral_ResolvesToNothing()
    {
        var location = await DeclareAsync(1, 2); // the "module" keyword head

        Assert.Null(location);
    }
}

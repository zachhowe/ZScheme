using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Syntax;
using ZScheme.LanguageServer.Analysis;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>Makes the module names in <c>(import …)</c> forms clickable, resolving
///     them to files with the same search-path/package/alias setup the compiler uses.</summary>
public sealed class DocumentLinkHandler(AnalysisService analysisService)
    : DocumentLinkHandlerBase
{
    protected override DocumentLinkRegistrationOptions CreateRegistrationOptions(
        DocumentLinkCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentLinkRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            ResolveProvider = false,
        };
    }

    public override Task<DocumentLinkContainer?> Handle(
        DocumentLinkParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<DocumentLinkContainer?>(null);

        var documentPath = request.TextDocument.Uri.GetFileSystemPath();
        var links = Compute(state, m => analysisService.ResolveModulePath(documentPath, m));
        return Task.FromResult<DocumentLinkContainer?>(new DocumentLinkContainer(links));
    }

    public override Task<DocumentLink> Handle(
        DocumentLink request,
        CancellationToken cancellationToken
    )
    {
        // ResolveProvider = false: links are fully populated up front.
        return Task.FromResult(request);
    }

    /// <summary>Test seam: one link per resolvable import, ranged over the module name.</summary>
    public static IReadOnlyList<DocumentLink> Compute(
        DocumentState state,
        Func<string, string?> resolveModule
    )
    {
        if (state.Ast is null)
            return [];

        var tokens = LexicalStructure.Tokens(state.Source);
        var links = new List<DocumentLink>();
        foreach (var import in Imports(state.Ast))
        {
            var path = resolveModule(import.ModuleName);
            if (path is null || !File.Exists(path))
                continue;

            if (LinkRange(state.Source, tokens, import.Span) is not { } range)
                continue;

            links.Add(
                new DocumentLink
                {
                    Range = range,
                    Target = DocumentUri.FromFileSystemPath(path),
                    Tooltip = path,
                }
            );
        }

        return links;
    }

    private static IEnumerable<AstNode.Import> Imports(AstNode node)
    {
        switch (node)
        {
            case AstNode.Import import:
                yield return import;
                break;
            case AstNode.Program program:
                foreach (var form in program.TopLevelForms)
                foreach (var found in Imports(form))
                    yield return found;
                break;
            case AstNode.ModuleDecl module:
                foreach (var form in module.Body)
                foreach (var found in Imports(form))
                    yield return found;
                break;
        }
    }

    /// <summary>
    ///     The range covering just the module name. A multi-import's per-name spans
    ///     are already tight; a single import's span covers the whole
    ///     <c>(import foo)</c> form, so the name is recovered as the second token
    ///     after the open paren.
    /// </summary>
    private static Range? LinkRange(
        string source,
        IReadOnlyList<Token> tokens,
        Compiler.Diagnostics.SourceSpan span
    )
    {
        var offset = SourceText.OffsetAt(source, span.Line - 1, span.Column - 1);
        if (offset >= source.Length || source[offset] != '(')
            return TextDocumentSyncHandler.SpanToRange(span);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.LParen
                || token.Span.Line != span.Line
                || token.Span.Column != span.Column)
                continue;

            // tokens[i + 1] is the `import` keyword; the module name follows.
            var name = tokens
                .Skip(i + 2)
                .FirstOrDefault(t => t.Kind is not TokenKind.Comment);
            return name is { Kind: TokenKind.Symbol }
                ? TextDocumentSyncHandler.SpanToRange(name.Span)
                : null;
        }

        return null;
    }
}

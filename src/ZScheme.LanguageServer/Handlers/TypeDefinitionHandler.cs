using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;
using ZScheme.LanguageServer.Analysis;
using SymbolKind = ZScheme.LanguageServer.Analysis.SymbolKind;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>Jump from a value to the declaration of its inferred type — the
///     <c>define-record</c> / <c>define-union</c> / <c>define-class</c> /
///     <c>define-interface</c> / <c>define-type-alias</c> form.</summary>
public sealed class TypeDefinitionHandler(AnalysisService analysisService)
    : TypeDefinitionHandlerBase
{
    protected override TypeDefinitionRegistrationOptions CreateRegistrationOptions(
        TypeDefinitionCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new TypeDefinitionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationOrLocationLinks?> Handle(
        TypeDefinitionParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var spans = Resolve(
            state,
            analysisService.Index,
            request.Position.Line + 1,
            request.Position.Character + 1
        );
        if (spans.Count == 0)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var locations = spans.Select(span => new LocationOrLocationLink(
            new Location
            {
                Uri = DefinitionHandler.SpanUri(span, request.TextDocument.Uri),
                Range = TextDocumentSyncHandler.SpanToRange(span),
            }
        ));
        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(locations));
    }

    /// <summary>Test seam: declaration span(s) of the inferred type of the node at the
    ///     1-based (line, col). Same-file declarations win; otherwise all type-kind
    ///     index matches are returned (multiple only when the bare name is ambiguous
    ///     across packages).</summary>
    public static IReadOnlyList<SourceSpan> Resolve(
        DocumentState state,
        WorkspaceIndex? index,
        int line,
        int col
    )
    {
        if (state.Ast is null)
            return [];

        var node = AstNavigation.FindNodeAt(state.Ast, line, col);
        if (TypeConstructorName(node?.ResolvedType) is not { } typeName)
            return [];

        if (
            state.NameToDefinition.TryGetValue(typeName, out var sameFile)
            && IsTypeKind(sameFile.Kind)
        )
            return [sameFile.DefinitionSpan];

        var hits = index?.ResolveDefinition(null, typeName) ?? [];
        return [.. hits.Where(h => IsTypeKind(h.Kind)).Select(h => h.Span)];
    }

    /// <summary>The named constructor of an inferred type, unwrapped through
    ///     polymorphism and nullability: <c>(Option Int)</c> → <c>Option</c>.</summary>
    private static string? TypeConstructorName(ZType? type)
    {
        while (true)
            switch (type)
            {
                case ZType.ZForAllType forAll:
                    type = forAll.Body;
                    break;
                case ZType.ZNullableType nullable:
                    type = nullable.Inner;
                    break;
                case ZType.ZNamedType named:
                    var name = named.Name;
                    return name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('.')) + 1)..];
                default:
                    return null;
            }
    }

    private static bool IsTypeKind(SymbolKind kind)
    {
        return kind
            is SymbolKind.Record
                or SymbolKind.Union
                or SymbolKind.Class
                or SymbolKind.Interface
                or SymbolKind.TypeAlias;
    }
}

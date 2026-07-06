using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class HoverHandler(AnalysisService analysisService) : HoverHandlerBase
{
    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state?.Ast is null)
            return Task.FromResult<Hover?>(null);

        var line = request.Position.Line + 1; // LSP 0-based → compiler 1-based
        var col = request.Position.Character + 1;

        var result = ResolveHover(state, line, col);
        if (result is null)
            return Task.FromResult<Hover?>(null);

        return Task.FromResult<Hover?>(
            new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(
                    new MarkupContent { Kind = MarkupKind.Markdown, Value = result.Value.Markdown }
                ),
                Range = TextDocumentSyncHandler.SpanToRange(result.Value.Node.Span),
            }
        );
    }

    /// <summary>
    ///     Test seam: resolve hover info for a 1-based (line, col) position against a parsed
    ///     <see cref="DocumentState" />. Returns the matched node and formatted markdown, or
    ///     null if no hover is available.
    /// </summary>
    public static (AstNode Node, string Markdown)? ResolveHover(
        DocumentState state,
        int line,
        int col
    )
    {
        if (state.Ast is null)
            return null;
        var node = FindNodeAt(state.Ast, line, col);
        if (node is null)
            return null;
        var info = FormatHoverInfo(node, state);
        if (info is null)
            return null;
        return (node, info);
    }

    private static string? FormatHoverInfo(AstNode node, DocumentState state)
    {
        var typePart = node.ResolvedType is not null ? node.ResolvedType.ToString() : null;

        return node switch
        {
            AstNode.Define def => typePart is not null
                ? $"```zscheme\n(define {def.FnName}) : {typePart}\n```"
                : null,
            AstNode.DefineAsync def => typePart is not null
                ? $"```zscheme\n(define-async {def.FnName}) : {typePart}\n```"
                : null,
            AstNode.DefineValue def => typePart is not null
                ? $"```zscheme\n{def.VarName} : {typePart}\n```"
                : null,
            AstNode.Name name => FormatNameHover(name, state),
            AstNode.RecordDecl rec =>
                $"```zscheme\n({(rec.IsValueType ? "define-struct" : "define-record")} {rec.RecordName})\n```",
            AstNode.UnionDecl union => $"```zscheme\n(define-union {union.UnionName})\n```",
            AstNode.ClassDecl cls => $"```zscheme\n(define-class {cls.ClassName})\n```",
            AstNode.InterfaceDecl iface =>
                $"```zscheme\n(define-interface {iface.InterfaceName})\n```",
            AstNode.TypeAliasDecl alias => FormatTypeAliasHover(alias),
            _ => typePart is not null ? $"```zscheme\n{typePart}\n```" : null,
        };
    }

    private static string FormatTypeAliasHover(AstNode.TypeAliasDecl alias)
    {
        var head =
            alias.TypeParams.Count == 0
                ? alias.AliasName
                : $"({alias.AliasName} {string.Join(' ', alias.TypeParams)})";

        string mapping;
        if (alias.IsArray)
        {
            // The :array form requires exactly one type param (validated in AstBuilder).
            var elem = alias.TypeParams.Count == 1 ? alias.TypeParams[0] : "^a";
            mapping = $"{elem}[]";
        }
        else if (alias.TypeParams.Count == 0)
        {
            mapping = alias.ClrTarget;
        }
        else
        {
            mapping = $"{alias.ClrTarget}<{string.Join(", ", alias.TypeParams)}>";
        }

        var assemblySuffix = alias.AssemblyHint is not null
            ? $" :from \"{alias.AssemblyHint}\""
            : "";

        return $"```zscheme\n(define-type-alias {head}) → {mapping}{assemblySuffix}\n```";
    }

    private static string? FormatNameHover(AstNode.Name name, DocumentState state)
    {
        // Type aliases live in their own namespace and don't have a value-level ResolvedType,
        // so check the alias table first regardless of whether the Name has a resolved type.
        if (state.TypeAliases.TryGetValue(name.Value, out var alias))
            return FormatTypeAliasHover(alias);

        var type = name.ResolvedType;
        if (type is not null)
            return $"```zscheme\n{name.Value} : {type}\n```";

        // Try to look up from symbol table
        if (
            state.NameToDefinition.TryGetValue(name.Value, out var sym)
            && sym.ResolvedType is not null
        )
            return $"```zscheme\n{name.Value} : {sym.ResolvedType}\n```";

        return null;
    }

    internal static AstNode? FindNodeAt(AstNode node, int line, int col)
    {
        return AstNavigation.FindNodeAt(node, line, col);
    }
}

namespace ZScheme.LanguageServer.Handlers;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

public sealed class HoverHandler(AnalysisService analysisService) : HoverHandlerBase
{
    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg"))
        };

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state?.Ast is null)
            return Task.FromResult<Hover?>(null);

        var line = request.Position.Line + 1;   // LSP 0-based → compiler 1-based
        var col = request.Position.Character + 1;

        var node = FindNodeAt(state.Ast, line, col);
        if (node is null)
            return Task.FromResult<Hover?>(null);

        var info = FormatHoverInfo(node, state);
        if (info is null)
            return Task.FromResult<Hover?>(null);

        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(
                new MarkupContent { Kind = MarkupKind.Markdown, Value = info }),
            Range = TextDocumentSyncHandler.SpanToRange(node.Span)
        });
    }

    private static string? FormatHoverInfo(AstNode node, DocumentState state)
    {
        var typePart = node.ResolvedType is not null ? node.ResolvedType.ToString() : null;

        return node switch
        {
            AstNode.Define def =>
                typePart is not null ? $"```zscheme\n(define {def.FnName}) : {typePart}\n```" : null,
            AstNode.DefineAsync def =>
                typePart is not null ? $"```zscheme\n(define-async {def.FnName}) : {typePart}\n```" : null,
            AstNode.DefineValue def =>
                typePart is not null ? $"```zscheme\n{def.VarName} : {typePart}\n```" : null,
            AstNode.Name name =>
                FormatNameHover(name, state),
            AstNode.RecordDecl rec =>
                $"```zscheme\n(record {rec.RecordName})\n```",
            AstNode.UnionDecl union =>
                $"```zscheme\n(union {union.UnionName})\n```",
            AstNode.ClassDecl cls =>
                $"```zscheme\n(class {cls.ClassName})\n```",
            AstNode.InterfaceDecl iface =>
                $"```zscheme\n(interface {iface.InterfaceName})\n```",
            _ => typePart is not null ? $"```zscheme\n{typePart}\n```" : null
        };
    }

    private static string? FormatNameHover(AstNode.Name name, DocumentState state)
    {
        var type = name.ResolvedType;
        if (type is not null)
            return $"```zscheme\n{name.Value} : {type}\n```";

        // Try to look up from symbol table
        if (state.NameToDefinition.TryGetValue(name.Value, out var sym) && sym.ResolvedType is not null)
            return $"```zscheme\n{name.Value} : {sym.ResolvedType}\n```";

        return null;
    }

    internal static AstNode? FindNodeAt(AstNode node, int line, int col)
    {
        // Find the most specific (deepest) node whose span contains the position
        AstNode? best = null;

        if (SpanContains(node.Span, line, col))
            best = node;

        foreach (var child in GetChildren(node))
        {
            var found = FindNodeAt(child, line, col);
            if (found is not null)
                best = found;
        }

        return best;
    }

    private static bool SpanContains(SourceSpan span, int line, int col) =>
        span.Line == line && col >= span.Column && col < span.Column + span.Length;

    private static IEnumerable<AstNode> GetChildren(AstNode node) => node switch
    {
        AstNode.Program p => p.TopLevelForms,
        AstNode.Define d => d.Params.Select(p => (AstNode)new AstNode.Name(p.Name, p.Span)).Append(d.Body),
        AstNode.DefineAsync d => d.Params.Select(p => (AstNode)new AstNode.Name(p.Name, p.Span)).Append(d.Body),
        AstNode.DefineValue d => [d.Value],
        AstNode.Let l => [l.Value, l.Body],
        AstNode.If i => [i.Condition, i.Then, i.Else],
        AstNode.Lambda l => l.Params.Select(p => (AstNode)new AstNode.Name(p.Name, p.Span)).Concat([l.Body]),
        AstNode.Apply a => new[] { a.Function }.Concat(a.Args),
        AstNode.Match m => new[] { m.Scrutinee }.Concat(m.Arms.Select(a => a.Body)),
        AstNode.Pipe p => new[] { p.Initial }.Concat(p.Steps),
        AstNode.ModuleDecl m => m.Body,
        AstNode.Try t => [t.Body],
        AstNode.Catch c => [c.Body],
        AstNode.Propagate p => [p.Expr],
        AstNode.Raise r => [r.Expr],
        AstNode.Await a => [a.Expr],
        AstNode.Partial p => new[] { p.Function }.Concat(p.Args),
        _ => []
    };
}

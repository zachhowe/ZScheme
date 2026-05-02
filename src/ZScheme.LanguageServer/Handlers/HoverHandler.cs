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
        ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg"))
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

        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(
                new MarkupContent { Kind = MarkupKind.Markdown, Value = result.Value.Markdown }),
            Range = TextDocumentSyncHandler.SpanToRange(result.Value.Node.Span)
        });
    }

    /// <summary>
    ///     Test seam: resolve hover info for a 1-based (line, col) position against a parsed
    ///     <see cref="DocumentState"/>. Returns the matched node and formatted markdown, or
    ///     null if no hover is available.
    /// </summary>
    public static (AstNode Node, string Markdown)? ResolveHover(DocumentState state, int line, int col)
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

    private static bool SpanContains(SourceSpan span, int line, int col)
    {
        return span.Line == line && col >= span.Column && col < span.Column + span.Length;
    }

    private static IEnumerable<AstNode> GetChildren(AstNode node)
    {
        return node switch
        {
            AstNode.Program p => p.TopLevelForms,
            AstNode.Define d => DefineNameNode(d.FnName, d.NameSpan, d.ResolvedType)
                .Concat(ParamNames(d.Params))
                .Append(d.Body),
            AstNode.DefineAsync d => DefineNameNode(d.FnName, d.NameSpan, d.ResolvedType)
                .Concat(ParamNames(d.Params))
                .Append(d.Body),
            AstNode.DefineValue d => DefineNameNode(d.VarName, d.NameSpan, d.ResolvedType)
                .Append(d.Value),
            AstNode.Let l => [l.Value, l.Body],
            AstNode.If i => [i.Condition, i.Then, i.Else],
            AstNode.Lambda l => ParamNames(l.Params).Append(l.Body),
            AstNode.Apply a => new[] { a.Function }.Concat(a.Args),
            AstNode.Match m => new[] { m.Scrutinee }.Concat(m.Arms.Select(a => a.Body)),
            AstNode.ModuleDecl m => m.Body,
            AstNode.Raise r => [r.Expr],
            AstNode.Await a => [a.Expr],
            AstNode.Partial p => new[] { p.Function }.Concat(p.Args),
            AstNode.With w => new[] { w.Record }.Concat(w.Updates.Select(u => u.Value)),
            AstNode.WithHandlers wh => new[] { wh.Body }.Concat(wh.Handlers.Select(h => h.HandlerBody)),
            AstNode.SetField sf => [sf.Value],
            AstNode.TupleNew tn => tn.Elements,
            AstNode.ClrNew cn => cn.Args,
            AstNode.SuperMethodCall smc => smc.Args,
            AstNode.ObjectExpr oe => ObjectExprChildren(oe),
            AstNode.ClassDecl cd => ClassDeclChildren(cd),
            _ => []
        };
    }

    private static IEnumerable<AstNode> ParamNames(IReadOnlyList<Param> params_)
    {
        // Synthesize a Name node per parameter so the walker can resolve the cursor to
        // a parameter binding. Carry the inferred type so hover can format it.
        return params_.Select(p => (AstNode)new AstNode.Name(p.Name, p.Span) { ResolvedType = p.ResolvedType });
    }

    private static IEnumerable<AstNode> DefineNameNode(string name, SourceSpan nameSpan, ZType? type)
    {
        // Synthesize a Name node for the function/value name itself so the cursor on
        // the name resolves precisely — the outer Define span is single-line and
        // doesn't reach the name on multi-line forms.
        if (nameSpan.Length == 0)
            return [];
        return [new AstNode.Name(name, nameSpan) { ResolvedType = type }];
    }

    private static IEnumerable<AstNode> MethodChildren(ObjectMethod method)
    {
        return ParamNames(method.Params).Append(method.Body);
    }

    private static IEnumerable<AstNode> ConstructorChildren(ConstructorDecl ctor)
    {
        var children = ParamNames(ctor.Params).ToList();
        if (ctor.SuperArgs is not null)
            children.AddRange(ctor.SuperArgs);
        children.AddRange(ctor.FieldSets.Select(f => f.Value));
        children.AddRange(ctor.BodyExprs);
        return children;
    }

    private static IEnumerable<AstNode> ObjectExprChildren(AstNode.ObjectExpr oe)
    {
        var children = oe.Methods.SelectMany(MethodChildren).ToList();
        if (oe.Constructor is not null)
            children.AddRange(ConstructorChildren(oe.Constructor));
        return children;
    }

    private static IEnumerable<AstNode> ClassDeclChildren(AstNode.ClassDecl cd)
    {
        var children = cd.Methods.SelectMany(MethodChildren).ToList();
        if (cd.Constructor is not null)
            children.AddRange(ConstructorChildren(cd.Constructor));
        return children;
    }
}

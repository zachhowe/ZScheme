using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;
using ZScheme.LanguageServer.Analysis;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Renders Hindley-Milner–inferred types inline: on <c>define</c>/<c>let</c>/<c>use</c>
///     value bindings, on function/lambda parameters, and as function return types — only
///     where the type isn't already written in the source. Walks the real typed AST (not
///     the synthesized Name nodes used for navigation), since those carry neither the
///     binding's <c>TypeAnnotation</c> nor, for <c>let</c>/<c>use</c>, a usable name span.
/// </summary>
public sealed class InlayHintHandler(AnalysisService analysisService) : InlayHintsHandlerBase
{
    protected override InlayHintRegistrationOptions CreateRegistrationOptions(
        InlayHintClientCapabilities capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new InlayHintRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            ResolveProvider = false,
        };
    }

    public override Task<InlayHintContainer?> Handle(
        InlayHintParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<InlayHintContainer?>(null);

        var hints = Collect(state, request.Range);
        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    // No resolve work needed — every hint is fully populated up front.
    public override Task<InlayHint> Handle(InlayHint request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    /// <summary>
    ///     Test seam: the inferred-type inlay hints within <paramref name="visible" />.
    /// </summary>
    public static IReadOnlyList<InlayHint> Collect(DocumentState state, Range visible)
    {
        var hints = new List<InlayHint>();
        if (state.Ast is not null)
            Walk(state.Ast, visible, hints);
        return hints;
    }

    private static void Walk(AstNode node, Range visible, List<InlayHint> hints)
    {
        switch (node)
        {
            // (define answer 42) — value binding; there is no annotation grammar, so hint
            // whenever a type is known.
            case AstNode.DefineValue dv when dv.NameSpan.Length > 0:
                Emit(hints, visible, EndOf(dv.NameSpan), dv.Value.ResolvedType ?? dv.ResolvedType);
                break;

            // (let [x 42] …) — the binding's type is the value's inferred type. No name span,
            // so render just before the value expression.
            case AstNode.Let l when l.TypeAnnotation is null:
                Emit(hints, visible, StartOf(l.Value.Span), l.Value.ResolvedType, false, true);
                break;
            case AstNode.Use u when u.TypeAnnotation is null:
                Emit(hints, visible, StartOf(u.Value.Span), u.Value.ResolvedType, false, true);
                break;

            case AstNode.Lambda lam:
                EmitParams(hints, visible, lam.Params);
                if (lam.ReturnTypeAnnotation is null)
                    EmitReturn(hints, visible, lam.Body, lam.ResolvedType);
                break;
            case AstNode.Define d:
                EmitParams(hints, visible, d.Params);
                if (d.ReturnTypeAnnotation is null)
                    EmitReturn(hints, visible, d.Body, d.ResolvedType);
                break;
            case AstNode.DefineAsync d:
                EmitParams(hints, visible, d.Params);
                if (d.ReturnTypeAnnotation is null)
                    EmitReturn(hints, visible, d.Body, d.ResolvedType);
                break;
        }

        foreach (var child in AstNavigation.Children(node))
            Walk(child, visible, hints);
    }

    private static void EmitParams(List<InlayHint> hints, Range visible, IReadOnlyList<Param> parameters)
    {
        foreach (var p in parameters)
            if (p.TypeAnnotation is null && p.Span.Length > 0)
                Emit(hints, visible, EndOf(p.Span), p.ResolvedType);
    }

    private static void EmitReturn(List<InlayHint> hints, Range visible, AstNode body, ZType? funcType)
    {
        Emit(hints, visible, StartOf(body.Span), UnwrapFunc(funcType)?.Return, false, true);
    }

    private static void Emit(
        List<InlayHint> hints,
        Range visible,
        Position at,
        ZType? type,
        bool padLeft = true,
        bool padRight = false
    )
    {
        if (type is null)
            return;
        if (at.Line < visible.Start.Line || at.Line > visible.End.Line)
            return;
        hints.Add(
            new InlayHint
            {
                Position = at,
                Label = new StringOrInlayHintLabelParts($": {type}"),
                Kind = InlayHintKind.Type,
                PaddingLeft = padLeft,
                PaddingRight = padRight,
            }
        );
    }

    private static ZType.ZFuncType? UnwrapFunc(ZType? type)
    {
        return type switch
        {
            ZType.ZFuncType f => f,
            ZType.ZForAllType { Body: ZType.ZFuncType f } => f,
            _ => null,
        };
    }

    private static Position StartOf(SourceSpan span)
    {
        return TextDocumentSyncHandler.SpanToRange(span).Start;
    }

    private static Position EndOf(SourceSpan span)
    {
        return TextDocumentSyncHandler.SpanToRange(span).End;
    }
}

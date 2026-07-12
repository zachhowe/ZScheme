using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Types;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Parameter hints while typing a call <c>(foo …)</c>. Finds the enclosing
///     application, resolves the callee's <see cref="ZType.ZFuncType" /> (all overloads
///     when the name is overloaded), and tracks which argument the cursor is on.
///     Parameter labels are <c>name : Type</c> when the declared names are recoverable
///     (same-file AST or the index's <c>ParamNames</c> facet — <see cref="ZType.ZFuncType" />
///     itself carries no names, see <see cref="ParamNameResolver" />), else types only.
/// </summary>
public sealed class SignatureHelpHandler(AnalysisService analysisService)
    : SignatureHelpHandlerBase
{
    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            TriggerCharacters = new Container<string>("(", " "),
            RetriggerCharacters = new Container<string>(" "),
        };
    }

    public override Task<SignatureHelp?> Handle(
        SignatureHelpParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<SignatureHelp?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        return Task.FromResult(
            ResolveSignatureHelp(state, analysisService.Index, line, col)
        );
    }

    /// <summary>
    ///     Test seam: signature help for the call enclosing the 1-based (line, col) cursor,
    ///     or null if the cursor is not inside a call to a resolvable function.
    /// </summary>
    public static SignatureHelp? ResolveSignatureHelp(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col
    )
    {
        if (state.Ast is null)
            return null;

        var apply = AstNavigation.FindEnclosingApply(state.Ast, line, col);
        if (apply?.Function is not AstNode.Name fn)
            return null;

        var candidates = ResolveFuncTypes(fn, state);
        if (candidates.Count == 0)
            return null;

        var signatures = candidates
            .Select(c =>
                BuildSignature(
                    fn.Value,
                    c.Func,
                    ParamNameResolver.ForDeclaredArity(
                        c.QualifiedName,
                        fn.Value,
                        c.Func.Params.Count,
                        state,
                        index
                    )?.Names
                )
            )
            .ToList();

        var funcTypes = candidates.Select(c => c.Func).ToList();

        // Prefer the overload whose arity matches the call site.
        var activeSignature = funcTypes.FindIndex(ft => ArityMatches(ft, apply.Args.Count));
        if (activeSignature < 0)
            activeSignature = 0;

        var activeParameter = ClampActive(
            ActiveParameter(apply, line, col),
            funcTypes[activeSignature]
        );

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(signatures),
            ActiveSignature = activeSignature,
            ActiveParameter = activeParameter,
        };
    }

    private static List<(ZType.ZFuncType Func, string? QualifiedName)> ResolveFuncTypes(
        AstNode.Name fn,
        DocumentState state
    )
    {
        var result = new List<(ZType.ZFuncType, string?)>();

        // Overload set (imported functions sharing a bare name): one signature per candidate.
        if (fn.OverloadCandidates is { Candidates.Count: > 0 } set)
        {
            foreach (var candidate in set.Candidates)
                if (UnwrapFunc(candidate.Type) is { } ft)
                    result.Add((ft, candidate.QualifiedName));
            if (result.Count > 0)
                return result;
        }

        // Single callee: the call-site name's inferred type, else the local definition's.
        var single = UnwrapFunc(fn.ResolvedType);
        if (single is null && state.NameToDefinition.TryGetValue(fn.Value, out var sym))
            single = UnwrapFunc(sym.ResolvedType);
        if (single is not null)
            result.Add((single, fn.ResolvedQualifiedName));

        return result;
    }

    private static SignatureInformation BuildSignature(
        string name,
        ZType.ZFuncType ft,
        IReadOnlyList<string>? paramNames
    )
    {
        var parameters = new List<ParameterInformation>();
        var labels = new List<string>();
        for (var i = 0; i < ft.Params.Count; i++)
        {
            var label = ft.Params[i].ToString() ?? "?";
            if (paramNames is not null && i < paramNames.Count)
                label = $"{paramNames[i]} : {label}";
            if (i == ft.Params.Count - 1 && ft.IsVariadic)
                label += "...";
            labels.Add(label);
            parameters.Add(new ParameterInformation { Label = new ParameterInformationLabel(label) });
        }

        var signatureLabel =
            labels.Count == 0
                ? $"({name}) : {ft.Return}"
                : $"({name} {string.Join(" ", labels)}) : {ft.Return}";

        return new SignatureInformation
        {
            Label = signatureLabel,
            Parameters = new Container<ParameterInformation>(parameters),
        };
    }

    // Index of the argument the cursor is currently on: the count of arguments whose span
    // ends strictly before the cursor. Robust for multi-line and partially-typed calls.
    private static int ActiveParameter(AstNode.Apply apply, int line, int col)
    {
        var cursorLine = line - 1;
        var cursorChar = col - 1;
        var index = 0;
        foreach (var arg in apply.Args)
        {
            var end = TextDocumentSyncHandler.SpanToRange(arg.Span).End;
            var pastArg =
                cursorLine > end.Line || (cursorLine == end.Line && cursorChar > end.Character);
            if (pastArg)
                index++;
            else
                break;
        }

        return index;
    }

    private static int ClampActive(int active, ZType.ZFuncType ft)
    {
        if (ft.Params.Count == 0)
            return 0;
        if (ft.IsVariadic && active >= ft.Params.Count)
            return ft.Params.Count - 1;
        return Math.Min(active, ft.Params.Count - 1);
    }

    private static bool ArityMatches(ZType.ZFuncType ft, int argCount)
    {
        return ft.IsVariadic ? argCount >= ft.Params.Count - 1 : ft.Params.Count == argCount;
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
}

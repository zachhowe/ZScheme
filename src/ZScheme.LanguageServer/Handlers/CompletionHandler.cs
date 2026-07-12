using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

using AnalysisSymbolKind = Analysis.SymbolKind;

public sealed class CompletionHandler(AnalysisService analysisService) : CompletionHandlerBase
{
    private static readonly string[] Keywords =
    [
        "define",
        "define-async",
        "define-syntax",
        "let",
        "let*",
        "use",
        "use*",
        "if",
        "lambda",
        "match",
        "define-record",
        "define-struct",
        "define-union",
        "define-class",
        "define-interface",
        "object",
        "module",
        "namespace",
        "import",
        "export",
        "import-clr",
        "raise",
        "await",
        "begin",
        "new",
        "typeof",
        "list",
        "vector",
        "partial",
        "and",
        "or",
        "not",
        "syntax-rules",
        "values",
        "true",
        "false",
        "#t",
        "#f",
        "null",
    ];

    private static readonly string[] BuiltinTypes =
    [
        "Int",
        "Float",
        "Bool",
        "String",
        "Unit",
        "List",
        "Vector",
        "Hash",
        "Option",
        "Result",
        "Fn",
        "Task",
    ];

    private static readonly string[] ValueConstructors = ["Some", "None", "Ok", "Err", "Error"];

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            TriggerCharacters = new Container<string>("("),
            ResolveProvider = false,
        };
    }

    public override Task<CompletionList> Handle(
        CompletionParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);

        // Filter server-side by the partial identifier before the cursor. An empty
        // prefix (fresh position) matches everything.
        var prefix = state is null ? "" : ExtractPrefix(state.Source, request.Position);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        // In a type position (after ':', inside a type expression, after new/typeof)
        // only type names make sense; everywhere else, type-only names like the
        // builtin primitives are noise.
        var isTypePosition =
            state is not null
            && TypePosition.IsTypePosition(LexicalStructure.Tokens(state.Source), line, col);

        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!isTypePosition)
            foreach (var kw in Keywords)
                if (Matches(prefix, kw) && seen.Add(kw))
                    items.Add(
                        new CompletionItem
                        {
                            Label = kw,
                            Kind = CompletionItemKind.Keyword,
                            Detail = "keyword",
                        }
                    );

        if (isTypePosition)
            foreach (var t in BuiltinTypes)
                if (Matches(prefix, t) && seen.Add(t))
                    items.Add(
                        new CompletionItem
                        {
                            Label = t,
                            Kind = CompletionItemKind.Class,
                            Detail = "type",
                        }
                    );

        if (!isTypePosition)
            foreach (var vc in ValueConstructors)
                if (Matches(prefix, vc) && seen.Add(vc))
                    items.Add(
                        new CompletionItem
                        {
                            Label = vc,
                            Kind = CompletionItemKind.EnumMember,
                            Detail = "constructor",
                        }
                    );

        // Top-level symbols from the current document. Locals (parameters, let/use
        // bindings) are offered separately, scope-filtered.
        if (state is not null)
            foreach (var symbol in state.Symbols)
            {
                if (symbol.IsLocal)
                    continue;
                if (isTypePosition && !IsTypeKind(symbol.Kind))
                    continue;
                if (Matches(prefix, symbol.Name) && seen.Add(symbol.Name))
                    items.Add(
                        new CompletionItem
                        {
                            Label = symbol.Name,
                            Kind = MapCompletionKind(symbol.Kind),
                            Detail = symbol.ResolvedType?.ToString(),
                        }
                    );
            }

        // Locals visible at the cursor, innermost shadow winning.
        if (state?.Ast is not null && !isTypePosition)
            foreach (var binding in ScopeAnalysis.BindingsInScopeAt(
                state.Ast,
                state.Source,
                line,
                col
            ))
                if (Matches(prefix, binding.Name) && seen.Add(binding.Name))
                    items.Add(
                        new CompletionItem
                        {
                            Label = binding.Name,
                            Kind = MapCompletionKind(binding.Kind),
                            Detail = binding.Type?.ToString(),
                        }
                    );

        // Cross-file symbols from the workspace index. Current-file entries are skipped
        // (already covered above); sorted after same-file symbols.
        var currentFile = RequestFilePath(request);
        foreach (var def in analysisService.Index.CompletionCandidates(prefix))
        {
            if (
                currentFile is not null
                && string.Equals(def.File, currentFile, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (isTypePosition && !IsTypeKind(def.Kind))
                continue;
            if (!seen.Add(def.BareName))
                continue;

            items.Add(
                new CompletionItem
                {
                    Label = def.BareName,
                    Kind = MapCompletionKind(def.Kind),
                    Detail = def.ContainerModule,
                    SortText = "z" + def.BareName,
                }
            );
        }

        return Task.FromResult(new CompletionList(items, false));
    }

    private static bool IsTypeKind(AnalysisSymbolKind kind)
    {
        return kind
            is AnalysisSymbolKind.Record
                or AnalysisSymbolKind.Union
                or AnalysisSymbolKind.Class
                or AnalysisSymbolKind.Interface
                or AnalysisSymbolKind.TypeAlias;
    }

    /// <summary>The partial identifier immediately before the cursor.</summary>
    internal static string ExtractPrefix(string source, Position position)
    {
        var offset = SourceText.OffsetAt(source, position.Line, position.Character);
        return SourceText.IdentifierPrefixAt(source, offset);
    }

    private static bool Matches(string prefix, string label)
    {
        return prefix.Length == 0
            || label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RequestFilePath(CompletionParams request)
    {
        try
        {
            var path = request.TextDocument.Uri.GetFileSystemPath();
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    public override Task<CompletionItem> Handle(
        CompletionItem request,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(request);
    }

    private static CompletionItemKind MapCompletionKind(AnalysisSymbolKind kind)
    {
        return kind switch
        {
            AnalysisSymbolKind.Function => CompletionItemKind.Function,
            AnalysisSymbolKind.Variable => CompletionItemKind.Variable,
            AnalysisSymbolKind.Parameter => CompletionItemKind.Variable,
            AnalysisSymbolKind.Record => CompletionItemKind.Struct,
            AnalysisSymbolKind.Union => CompletionItemKind.Enum,
            AnalysisSymbolKind.UnionCase => CompletionItemKind.EnumMember,
            AnalysisSymbolKind.Class => CompletionItemKind.Class,
            AnalysisSymbolKind.Interface => CompletionItemKind.Interface,
            AnalysisSymbolKind.Module => CompletionItemKind.Module,
            _ => CompletionItemKind.Text,
        };
    }
}

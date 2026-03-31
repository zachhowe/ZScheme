namespace ZScript.LanguageServer.Handlers;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScript.LanguageServer.Analysis;
using AnalysisSymbolKind = ZScript.LanguageServer.Analysis.SymbolKind;

public sealed class CompletionHandler(AnalysisService analysisService) : CompletionHandlerBase
{
    private static readonly string[] Keywords =
    [
        "define", "define-async", "define-syntax", "let", "let*", "if", "fn",
        "match", "record", "union", "class", "interface", "object",
        "module", "namespace", "import", "export", "import-clr",
        "try", "catch", "raise", "await", "begin", "new",
        "list", "vector", "partial",
        "and", "or", "not", "syntax-rules",
        "true", "false", "#t", "#f"
    ];

    private static readonly string[] BuiltinTypes =
    [
        "Int", "Float", "Bool", "String", "Unit",
        "List", "Vector", "Map", "Option", "Result", "Fn", "Task"
    ];

    private static readonly string[] ValueConstructors =
    [
        "Some", "None", "Ok", "Err", "Error"
    ];

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscript"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")),
            TriggerCharacters = new Container<string>("("),
            ResolveProvider = false
        };

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();

        // Keywords
        foreach (var kw in Keywords)
        {
            items.Add(new CompletionItem
            {
                Label = kw,
                Kind = CompletionItemKind.Keyword,
                Detail = "keyword"
            });
        }

        // Built-in types
        foreach (var t in BuiltinTypes)
        {
            items.Add(new CompletionItem
            {
                Label = t,
                Kind = CompletionItemKind.Class,
                Detail = "type"
            });
        }

        // Value constructors
        foreach (var vc in ValueConstructors)
        {
            items.Add(new CompletionItem
            {
                Label = vc,
                Kind = CompletionItemKind.EnumMember,
                Detail = "constructor"
            });
        }

        // Symbols from the current document
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is not null)
        {
            foreach (var symbol in state.Symbols)
            {
                if (symbol.Kind is AnalysisSymbolKind.Parameter)
                    continue;

                items.Add(new CompletionItem
                {
                    Label = symbol.Name,
                    Kind = MapCompletionKind(symbol.Kind),
                    Detail = symbol.ResolvedType?.ToString()
                });
            }
        }

        return Task.FromResult(new CompletionList(items, isIncomplete: false));
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken) =>
        Task.FromResult(request);

    private static CompletionItemKind MapCompletionKind(AnalysisSymbolKind kind) => kind switch
    {
        AnalysisSymbolKind.Function => CompletionItemKind.Function,
        AnalysisSymbolKind.Variable => CompletionItemKind.Variable,
        AnalysisSymbolKind.Record => CompletionItemKind.Struct,
        AnalysisSymbolKind.Union => CompletionItemKind.Enum,
        AnalysisSymbolKind.UnionCase => CompletionItemKind.EnumMember,
        AnalysisSymbolKind.Class => CompletionItemKind.Class,
        AnalysisSymbolKind.Interface => CompletionItemKind.Interface,
        AnalysisSymbolKind.Module => CompletionItemKind.Module,
        _ => CompletionItemKind.Text
    };
}

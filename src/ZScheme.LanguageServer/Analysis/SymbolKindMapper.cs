using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>Maps the language server's <see cref="SymbolKind" /> to the LSP protocol
///     symbol kind used by document-symbol and workspace-symbol responses.</summary>
public static class SymbolKindMapper
{
    public static LspSymbolKind ToLsp(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Function => LspSymbolKind.Function,
            SymbolKind.Variable => LspSymbolKind.Variable,
            SymbolKind.Record => LspSymbolKind.Struct,
            SymbolKind.Union => LspSymbolKind.Enum,
            SymbolKind.UnionCase => LspSymbolKind.EnumMember,
            SymbolKind.Class => LspSymbolKind.Class,
            SymbolKind.Interface => LspSymbolKind.Interface,
            SymbolKind.Module => LspSymbolKind.Module,
            SymbolKind.Parameter => LspSymbolKind.Variable,
            SymbolKind.TypeAlias => LspSymbolKind.Interface,
            _ => LspSymbolKind.Variable,
        };
    }
}

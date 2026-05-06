using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

public sealed record SymbolInfo(
    string Name,
    ZType? ResolvedType,
    SourceSpan DefinitionSpan,
    SymbolKind Kind);

public enum SymbolKind
{
    Function,
    Variable,
    Record,
    Union,
    Class,
    Interface,
    Module,
    Parameter,
    UnionCase,
    TypeAlias
}

public sealed record DocumentState(
    string Uri,
    int Version,
    string Source,
    AstNode.Program? Ast,
    DiagnosticBag Diagnostics,
    IReadOnlyList<SymbolInfo> Symbols,
    IReadOnlyDictionary<string, SymbolInfo> NameToDefinition,
    IReadOnlyDictionary<string, AstNode.TypeAliasDecl> TypeAliases);

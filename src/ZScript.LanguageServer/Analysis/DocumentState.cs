namespace ZScript.LanguageServer.Analysis;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Types;

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
    UnionCase
}

public sealed record DocumentState(
    string Uri,
    int Version,
    string Source,
    AstNode.Program? Ast,
    DiagnosticBag Diagnostics,
    IReadOnlyList<SymbolInfo> Symbols,
    IReadOnlyDictionary<string, SymbolInfo> NameToDefinition);

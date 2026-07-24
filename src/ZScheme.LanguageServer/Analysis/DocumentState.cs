using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

/// <summary><c>IsLocal</c> marks parameters and <c>let</c>/<c>use</c> bindings, whose
///     visibility is scope-bounded — completion offers them via
///     <see cref="ScopeAnalysis.BindingsInScopeAt" /> instead of file-wide.</summary>
public sealed record SymbolInfo(
    string Name,
    ZType? ResolvedType,
    SourceSpan DefinitionSpan,
    SymbolKind Kind,
    bool IsLocal = false
);

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
    TypeAlias,

    /// <summary>An <c>(import-clr [alias Type/Method])</c> binding. Its "definition" is
    ///     the alias declaration — the CLR member it forwards to has no ZScheme source.</summary>
    ClrAlias,
}

public sealed record DocumentState(
    string Uri,
    int Version,
    string Source,
    AstNode.Program? Ast,
    DiagnosticBag Diagnostics,
    IReadOnlyList<SymbolInfo> Symbols,
    IReadOnlyDictionary<string, SymbolInfo> NameToDefinition,
    IReadOnlyDictionary<string, AstNode.TypeAliasDecl> TypeAliases
);

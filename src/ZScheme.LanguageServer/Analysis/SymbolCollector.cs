using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

public sealed class SymbolCollector
{
    private readonly Dictionary<string, SymbolInfo> _nameToDefinition = new();
    private readonly List<SymbolInfo> _symbols = [];
    private readonly Dictionary<string, AstNode.TypeAliasDecl> _typeAliases = new();

    public IReadOnlyList<SymbolInfo> Symbols => _symbols;
    public IReadOnlyDictionary<string, SymbolInfo> NameToDefinition => _nameToDefinition;
    public IReadOnlyDictionary<string, AstNode.TypeAliasDecl> TypeAliases => _typeAliases;

    public void Collect(AstNode.Program program)
    {
        foreach (var form in program.TopLevelForms)
            CollectNode(form);
    }

    private void CollectNode(AstNode node)
    {
        switch (node)
        {
            case AstNode.Define def:
                AddSymbol(
                    def.FnName,
                    def.ResolvedType,
                    PreferNameSpan(def.NameSpan, def.Span),
                    SymbolKind.Function
                );
                foreach (var p in def.Params)
                    AddSymbol(
                        p.Name,
                        p.TypeAnnotation,
                        PreferNameSpan(p.NameSpan, p.Span),
                        SymbolKind.Parameter,
                        isLocal: true
                    );
                CollectNode(def.Body);
                break;

            case AstNode.DefineAsync def:
                AddSymbol(
                    def.FnName,
                    def.ResolvedType,
                    PreferNameSpan(def.NameSpan, def.Span),
                    SymbolKind.Function
                );
                foreach (var p in def.Params)
                    AddSymbol(
                        p.Name,
                        p.TypeAnnotation,
                        PreferNameSpan(p.NameSpan, p.Span),
                        SymbolKind.Parameter,
                        isLocal: true
                    );
                CollectNode(def.Body);
                break;

            case AstNode.DefineValue def:
                AddSymbol(
                    def.VarName,
                    def.ResolvedType,
                    PreferNameSpan(def.NameSpan, def.Span),
                    SymbolKind.Variable
                );
                CollectNode(def.Value);
                break;

            case AstNode.RecordDecl rec:
                AddSymbol(
                    rec.RecordName,
                    rec.ResolvedType,
                    PreferNameSpan(rec.NameSpan, rec.Span),
                    SymbolKind.Record
                );
                break;

            case AstNode.UnionDecl union:
                AddSymbol(
                    union.UnionName,
                    union.ResolvedType,
                    PreferNameSpan(union.NameSpan, union.Span),
                    SymbolKind.Union
                );
                foreach (var c in union.Cases)
                    AddSymbol(c.Name, null, PreferNameSpan(c.NameSpan, c.Span), SymbolKind.UnionCase);
                break;

            case AstNode.ClassDecl cls:
                AddSymbol(
                    cls.ClassName,
                    cls.ResolvedType,
                    PreferNameSpan(cls.NameSpan, cls.Span),
                    SymbolKind.Class
                );
                break;

            case AstNode.InterfaceDecl iface:
                AddSymbol(
                    iface.InterfaceName,
                    iface.ResolvedType,
                    PreferNameSpan(iface.NameSpan, iface.Span),
                    SymbolKind.Interface
                );
                break;

            case AstNode.TypeAliasDecl alias:
                AddSymbol(
                    alias.AliasName,
                    null,
                    PreferNameSpan(alias.NameSpan, alias.Span),
                    SymbolKind.TypeAlias
                );
                _typeAliases.TryAdd(alias.AliasName, alias);
                break;

            case AstNode.ImportClr importClr:
                foreach (var import in importClr.Imports)
                    AddSymbol(
                        import.Alias,
                        import.TypeAnnotation,
                        PreferNameSpan(import.AliasSpan, import.Span),
                        SymbolKind.ClrAlias
                    );
                break;

            case AstNode.ModuleDecl mod:
                AddSymbol(mod.ModuleName, null, mod.Span, SymbolKind.Module);
                foreach (var bodyNode in mod.Body)
                    CollectNode(bodyNode);
                break;

            case AstNode.Let let:
                AddSymbol(
                    let.VarName,
                    let.ResolvedType,
                    PreferNameSpan(let.NameSpan, let.Span),
                    SymbolKind.Variable,
                    isLocal: true
                );
                CollectNode(let.Value);
                CollectNode(let.Body);
                break;

            case AstNode.Use use:
                AddSymbol(
                    use.VarName,
                    use.ResolvedType,
                    PreferNameSpan(use.NameSpan, use.Span),
                    SymbolKind.Variable,
                    isLocal: true
                );
                CollectNode(use.Value);
                CollectNode(use.Body);
                break;

            case AstNode.Lambda lam:
                foreach (var p in lam.Params)
                    AddSymbol(
                        p.Name,
                        p.TypeAnnotation,
                        PreferNameSpan(p.NameSpan, p.Span),
                        SymbolKind.Parameter,
                        isLocal: true
                    );
                CollectNode(lam.Body);
                break;

            case AstNode.If ifNode:
                CollectNode(ifNode.Condition);
                CollectNode(ifNode.Then);
                CollectNode(ifNode.Else);
                break;

            case AstNode.Apply app:
                CollectNode(app.Function);
                foreach (var arg in app.Args)
                    CollectNode(arg);
                break;

            case AstNode.Match match:
                CollectNode(match.Scrutinee);
                foreach (var arm in match.Arms)
                    CollectNode(arm.Body);
                break;

            case AstNode.Raise raise:
                CollectNode(raise.Expr);
                break;

            case AstNode.Await awaitNode:
                CollectNode(awaitNode.Expr);
                break;

            case AstNode.Partial partial:
                CollectNode(partial.Function);
                foreach (var arg in partial.Args)
                    CollectNode(arg);
                break;

            case AstNode.SetField sf:
                CollectNode(sf.Value);
                break;
        }
    }

    private static SourceSpan PreferNameSpan(SourceSpan nameSpan, SourceSpan formSpan)
    {
        return nameSpan.Length > 0 ? nameSpan : formSpan;
    }

    private void AddSymbol(
        string name,
        ZType? type,
        SourceSpan span,
        SymbolKind kind,
        bool isLocal = false
    )
    {
        var symbol = new SymbolInfo(name, type, span, kind, isLocal);
        _symbols.Add(symbol);
        // Only track file-scope definitions here: this map is keyed by bare name for the
        // whole file, which cannot represent a local (two functions may bind the same
        // name, and an inner binding may shadow an outer one). Locals are resolved
        // scope-aware via ScopeAnalysis instead; they remain in Symbols.
        if (!isLocal)
            _nameToDefinition.TryAdd(name, symbol);
    }
}

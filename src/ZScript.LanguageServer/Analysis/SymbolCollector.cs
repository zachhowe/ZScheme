namespace ZScript.LanguageServer.Analysis;

using ZScript.Compiler.Ast;

public sealed class SymbolCollector
{
    private readonly List<SymbolInfo> _symbols = [];
    private readonly Dictionary<string, SymbolInfo> _nameToDefinition = new();

    public IReadOnlyList<SymbolInfo> Symbols => _symbols;
    public IReadOnlyDictionary<string, SymbolInfo> NameToDefinition => _nameToDefinition;

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
                AddSymbol(def.FnName, def.ResolvedType, def.Span, SymbolKind.Function);
                foreach (var p in def.Params)
                    AddSymbol(p.Name, p.TypeAnnotation, p.Span, SymbolKind.Parameter);
                CollectNode(def.Body);
                break;

            case AstNode.DefineAsync def:
                AddSymbol(def.FnName, def.ResolvedType, def.Span, SymbolKind.Function);
                foreach (var p in def.Params)
                    AddSymbol(p.Name, p.TypeAnnotation, p.Span, SymbolKind.Parameter);
                CollectNode(def.Body);
                break;

            case AstNode.DefineValue def:
                AddSymbol(def.VarName, def.ResolvedType, def.Span, SymbolKind.Variable);
                CollectNode(def.Value);
                break;

            case AstNode.RecordDecl rec:
                AddSymbol(rec.RecordName, rec.ResolvedType, rec.Span, SymbolKind.Record);
                break;

            case AstNode.UnionDecl union:
                AddSymbol(union.UnionName, union.ResolvedType, union.Span, SymbolKind.Union);
                foreach (var c in union.Cases)
                    AddSymbol(c.Name, null, c.Span, SymbolKind.UnionCase);
                break;

            case AstNode.ClassDecl cls:
                AddSymbol(cls.ClassName, cls.ResolvedType, cls.Span, SymbolKind.Class);
                break;

            case AstNode.InterfaceDecl iface:
                AddSymbol(iface.InterfaceName, iface.ResolvedType, iface.Span, SymbolKind.Interface);
                break;

            case AstNode.ModuleDecl mod:
                AddSymbol(mod.ModuleName, null, mod.Span, SymbolKind.Module);
                foreach (var bodyNode in mod.Body)
                    CollectNode(bodyNode);
                break;

            case AstNode.Let let:
                AddSymbol(let.VarName, let.ResolvedType, let.Span, SymbolKind.Variable);
                CollectNode(let.Value);
                CollectNode(let.Body);
                break;

            case AstNode.Lambda lam:
                foreach (var p in lam.Params)
                    AddSymbol(p.Name, p.TypeAnnotation, p.Span, SymbolKind.Parameter);
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

            case AstNode.Pipe pipe:
                CollectNode(pipe.Initial);
                foreach (var step in pipe.Steps)
                    CollectNode(step);
                break;

            case AstNode.Try tryNode:
                CollectNode(tryNode.Body);
                break;

            case AstNode.Catch catchNode:
                CollectNode(catchNode.Body);
                break;

            case AstNode.Propagate prop:
                CollectNode(prop.Expr);
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
        }
    }

    private void AddSymbol(string name, Compiler.Types.ZType? type, Compiler.Diagnostics.SourceSpan span, SymbolKind kind)
    {
        var symbol = new SymbolInfo(name, type, span, kind);
        _symbols.Add(symbol);
        // Only track top-level-ish definitions for go-to-definition (not parameters)
        if (kind is not SymbolKind.Parameter)
            _nameToDefinition.TryAdd(name, symbol);
    }
}

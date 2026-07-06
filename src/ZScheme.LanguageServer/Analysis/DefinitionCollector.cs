using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Harvests the <em>top-level</em> definitions of a file's typed AST into
///     <see cref="IndexedDefinition" />s for the workspace index. Unlike
///     <see cref="SymbolCollector" /> (which recurses into bodies to power hover and
///     same-file go-to-definition, and therefore also surfaces local <c>let</c>
///     bindings), this stays at module scope: only names another file could import.
///     Each definition is tagged with the package-qualified key
///     <c>"{primaryModule}/{name}"</c> so a use-site's <c>ResolvedQualifiedName</c>
///     resolves to it.
/// </summary>
internal static class DefinitionCollector
{
    public static List<IndexedDefinition> Collect(AstNode.Program program, string? primaryModule)
    {
        var defs = new List<IndexedDefinition>();
        foreach (var form in program.TopLevelForms)
            CollectForm(form, primaryModule, defs);
        return defs;
    }

    private static void CollectForm(
        AstNode form,
        string? primaryModule,
        List<IndexedDefinition> defs
    )
    {
        switch (form)
        {
            case AstNode.ModuleDecl mod:
                // A file's real top-level defs may be nested one level under (module ...).
                foreach (var bodyForm in mod.Body)
                    CollectForm(bodyForm, primaryModule, defs);
                break;

            case AstNode.Define def:
                Add(defs, def.FnName, PreferNameSpan(def.NameSpan, def.Span), SymbolKind.Function, primaryModule);
                break;

            case AstNode.DefineAsync def:
                Add(defs, def.FnName, PreferNameSpan(def.NameSpan, def.Span), SymbolKind.Function, primaryModule);
                break;

            case AstNode.DefineValue def:
                Add(defs, def.VarName, PreferNameSpan(def.NameSpan, def.Span), SymbolKind.Variable, primaryModule);
                break;

            case AstNode.RecordDecl rec:
                Add(defs, rec.RecordName, rec.Span, SymbolKind.Record, primaryModule);
                break;

            case AstNode.UnionDecl union:
                Add(defs, union.UnionName, union.Span, SymbolKind.Union, primaryModule);
                foreach (var c in union.Cases)
                    Add(defs, c.Name, c.Span, SymbolKind.UnionCase, primaryModule);
                break;

            case AstNode.ClassDecl cls:
                Add(defs, cls.ClassName, cls.Span, SymbolKind.Class, primaryModule);
                break;

            case AstNode.InterfaceDecl iface:
                Add(defs, iface.InterfaceName, iface.Span, SymbolKind.Interface, primaryModule);
                break;

            case AstNode.TypeAliasDecl alias:
                Add(
                    defs,
                    alias.AliasName,
                    PreferNameSpan(alias.NameSpan, alias.Span),
                    SymbolKind.TypeAlias,
                    primaryModule
                );
                break;
        }
    }

    private static void Add(
        List<IndexedDefinition> defs,
        string name,
        SourceSpan span,
        SymbolKind kind,
        string? primaryModule
    )
    {
        var key = primaryModule is not null ? $"{primaryModule}/{name}" : name;
        defs.Add(new IndexedDefinition(key, name, span, kind, primaryModule));
    }

    private static SourceSpan PreferNameSpan(SourceSpan nameSpan, SourceSpan formSpan)
    {
        return nameSpan.Length > 0 ? nameSpan : formSpan;
    }
}

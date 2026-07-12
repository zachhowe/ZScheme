using ZScheme.Compiler.Ast;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Collects every <see cref="AstNode.Name" /> occurrence in a file's typed AST as
///     an <see cref="IndexedReference" />, carrying the use-site's
///     <c>ResolvedQualifiedName</c> when the type inferer resolved one (imported /
///     overloaded functions). Occurrences include the synthesized definition-name node,
///     so a symbol's declaration site appears among its references (the references
///     handler filters it out unless the client asks to include the declaration).
///     Each occurrence is tagged with the qualified key of its enclosing top-level
///     definition (for call-hierarchy derivation); module-scope occurrences get null.
/// </summary>
internal static class ReferenceCollector
{
    public static List<IndexedReference> Collect(AstNode.Program program, string? primaryModule)
    {
        var refs = new List<IndexedReference>();
        foreach (var form in TopLevelForms(program))
        {
            var container = ContainerKey(form, primaryModule);
            foreach (var name in AstNavigation.AllNames(form))
                refs.Add(
                    new IndexedReference(
                        name.Value,
                        name.ResolvedQualifiedName,
                        name.Span,
                        container
                    )
                );
        }

        return refs;
    }

    private static IEnumerable<AstNode> TopLevelForms(AstNode.Program program)
    {
        foreach (var form in program.TopLevelForms)
            if (form is AstNode.ModuleDecl mod)
                foreach (var bodyForm in mod.Body)
                    yield return bodyForm;
            else
                yield return form;
    }

    /// <summary>The qualified key of the definition this top-level form declares —
    ///     names inside it are "contained by" that definition. Mirrors
    ///     <see cref="DefinitionCollector" />'s key format.</summary>
    private static string? ContainerKey(AstNode form, string? primaryModule)
    {
        var name = form switch
        {
            AstNode.Define d => d.FnName,
            AstNode.DefineAsync d => d.FnName,
            AstNode.DefineValue d => d.VarName,
            AstNode.ClassDecl c => c.ClassName,
            AstNode.RecordDecl r => r.RecordName,
            AstNode.UnionDecl u => u.UnionName,
            AstNode.InterfaceDecl i => i.InterfaceName,
            _ => null,
        };
        if (name is null)
            return null;
        return primaryModule is not null ? $"{primaryModule}/{name}" : name;
    }
}

using ZScheme.Compiler.Ast;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Collects every name occurrence in a file's typed AST as an
///     <see cref="IndexedReference" />, carrying the use-site's
///     <c>ResolvedQualifiedName</c> when the type inferer resolved one (imported /
///     overloaded functions). Occurrences include the synthesized definition-name node,
///     so a symbol's declaration site appears among its references (the references
///     handler filters it out unless the client asks to include the declaration).
///     Each occurrence is tagged with the qualified key of its enclosing top-level
///     definition (for call-hierarchy derivation); module-scope occurrences get null.
///     <para>
///         Type names written in type positions (annotations, nested type applications,
///         class/interface base lists, handler exception types) never become
///         <see cref="AstNode.Name" /> nodes — the compiler records them on
///         <see cref="AstNode.Program.TypeNameUses" /> instead — so they are emitted as
///         <see cref="IndexedReference.IsTypeUse" /> entries with no qualified key, and
///         cross-file matches for them go through
///         <see cref="WorkspaceIndex.FindTypeReferences" />.
///     </para>
/// </summary>
internal static class ReferenceCollector
{
    public static List<IndexedReference> Collect(AstNode.Program program, string? primaryModule)
    {
        var forms = TopLevelForms(program).ToList();
        var refs = new List<IndexedReference>();
        foreach (var form in forms)
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

        refs.AddRange(TypeUseReferences(forms, program.TypeNameUses, primaryModule));
        return refs;
    }

    /// <summary>
    ///     The file's written type names as <see cref="IndexedReference" />s with
    ///     <see cref="IndexedReference.IsTypeUse" /> set. Container attribution uses the
    ///     enclosing top-level form, as for names: every type use sits inside exactly one
    ///     form, and since form spans are single-line that form is the latest one that
    ///     starts at or before the use (a use cannot lie past the next form's start).
    /// </summary>
    private static IEnumerable<IndexedReference> TypeUseReferences(
        IReadOnlyList<AstNode> forms,
        IReadOnlyList<TypeNameUse> uses,
        string? primaryModule
    )
    {
        var ordered = forms
            .Select(form =>
                (
                    Start: (form.Span.Line, form.Span.Column),
                    Container: ContainerKey(form, primaryModule)
                )
            )
            .OrderBy(f => f.Start)
            .ToList();

        foreach (var use in uses)
        {
            if (use.Span.Length == 0)
                continue;
            var container = ordered
                .LastOrDefault(f =>
                    f.Start.Line < use.Span.Line
                    || (f.Start.Line == use.Span.Line && f.Start.Column <= use.Span.Column)
                )
                .Container;
            yield return new IndexedReference(use.Name, null, use.Span, container, IsTypeUse: true);
        }
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

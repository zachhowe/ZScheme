using ZScheme.Compiler.Ast;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Collects every <see cref="AstNode.Name" /> occurrence in a file's typed AST as
///     an <see cref="IndexedReference" />, carrying the use-site's
///     <c>ResolvedQualifiedName</c> when the type inferer resolved one (imported /
///     overloaded functions). Occurrences include the synthesized definition-name node,
///     so a symbol's declaration site appears among its references (the references
///     handler filters it out unless the client asks to include the declaration).
/// </summary>
internal static class ReferenceCollector
{
    public static List<IndexedReference> Collect(AstNode.Program program)
    {
        var refs = new List<IndexedReference>();
        foreach (var name in AstNavigation.AllNames(program))
            refs.Add(new IndexedReference(name.Value, name.ResolvedQualifiedName, name.Span));
        return refs;
    }
}

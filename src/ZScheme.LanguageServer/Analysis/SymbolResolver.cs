using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>The definition a cursor position resolves to, canonicalized so it can drive
///     both go-to-definition (the span) and find-references (the key + file).</summary>
public readonly record struct ResolvedSymbol(
    string BareName,
    string? QualifiedKey,
    SourceSpan DefinitionSpan
);

/// <summary>
///     Resolves the <see cref="AstNode.Name" /> under a 1-based (line, col) cursor to
///     its canonical definition, consulting the current document first (fast path,
///     same file) and then the workspace index for cross-file / cross-package symbols.
///     Shared by <c>DefinitionHandler</c> and <c>ReferencesHandler</c>.
/// </summary>
public static class SymbolResolver
{
    public static ResolvedSymbol? Resolve(
        DocumentState state,
        WorkspaceIndex? index,
        int line,
        int col
    )
    {
        if (state.Ast is null)
            return null;
        if (AstNavigation.FindNodeAt(state.Ast, line, col) is not AstNode.Name name)
            return null;

        var bare = name.Value;
        var qualified = name.ResolvedQualifiedName;

        // Same-file definition. Its span already carries this file's path, so it drives
        // the go-to URI directly. Recover the qualified key (from the use-site, else the
        // index's record of this file's definition) so find-references can match uses in
        // other files.
        if (state.NameToDefinition.TryGetValue(bare, out var local))
        {
            var span = local.DefinitionSpan;
            var key = qualified ?? index?.DefinitionInFile(span.File, bare)?.QualifiedKey;
            return new ResolvedSymbol(bare, key, span);
        }

        // Cross-file: resolve via the workspace index.
        if (index is null)
            return null;

        var best = PickBest(index.ResolveDefinition(qualified, bare), qualified);
        return best is null ? null : new ResolvedSymbol(bare, best.QualifiedKey, best.Span);
    }

    private static IndexedDefinition? PickBest(
        IReadOnlyList<IndexedDefinition> defs,
        string? qualified
    )
    {
        if (defs.Count == 0)
            return null;

        // An exact qualified-key match is unambiguous (imported functions).
        if (qualified is not null)
        {
            var exact = defs.FirstOrDefault(d => d.QualifiedKey == qualified);
            if (exact is not null)
                return exact;
        }

        // Otherwise only accept a unique bare-name hit — a name defined in exactly one
        // place across the workspace. Ambiguous names are left unresolved rather than
        // jumping somewhere arbitrary.
        return defs.Count == 1 ? defs[0] : null;
    }
}

using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Navigation for type names written in type positions (parameter/return
///     annotations, nested type applications like <c>(Option Int)</c>, class/interface
///     base lists, <c>new</c>/<c>typeof</c> arguments, handler exception types). Those
///     names never become <see cref="AstNode.Name" /> nodes — annotations compile into
///     <see cref="ZScheme.Compiler.Types.ZType" />, which carries no span — so the
///     compiler records every written occurrence on
///     <see cref="AstNode.Program.TypeNameUses" /> and this class serves cursor
///     resolution over that list. Because type uses are never value names, this path and
///     <see cref="SymbolResolver" /> can never point at the same span; handlers consult
///     it after the locals check and only fall through to <see cref="SymbolResolver" />
///     when it declines.
///     <para>
///         Ambiguity rule: a type name declared in the current file resolves to that
///     declaration (mirroring <see cref="SymbolResolver" />, whose same-file entry also
///     wins); otherwise the name must be defined in exactly one place across the
///     workspace index — <see cref="SymbolResolver.PickBest" /> semantics, filtered to
///     type kinds, since a same-named function is not a type candidate. Ambiguous names
///     are declined rather than guessed: type uses carry no qualified key, so a wrong
///     choice would navigate to the wrong file's declaration.
///     </para>
/// </summary>
public static class TypeNavigation
{
    /// <summary>
    ///     The type-name occurrence containing the 1-based (line, col) cursor, or null.
    ///     Occurrence spans are single-line and disjoint, so a linear scan decides it.
    /// </summary>
    public static TypeNameUse? UseAt(AstNode.Program program, int line, int col)
    {
        foreach (var use in program.TypeNameUses)
        {
            if (
                use.Span.Length > 0
                && use.Span.Line == line
                && col >= use.Span.Column
                && col < use.Span.Column + use.Span.Length
            )
                return use;
        }
        return null;
    }

    /// <summary>
    ///     The definition a type-name use at the 1-based (line, col) cursor refers to, or
    ///     null when the cursor is not on a type use, the name has no type definition, or
    ///     the name is ambiguous across the workspace.
    /// </summary>
    public static ResolvedSymbol? Resolve(
        DocumentState state,
        WorkspaceIndex? index,
        int line,
        int col
    )
    {
        if (state.Ast is null)
            return null;
        if (UseAt(state.Ast, line, col) is not { } use)
            return null;

        var bare = use.Name;

        // Same-file type declaration wins — use and declaration live in one file, so no
        // cross-file guess is involved. A same-named non-type (function, case) is not a
        // candidate: fall through to the index, which sees the type declaration.
        if (state.NameToDefinition.TryGetValue(bare, out var sameFile) && IsTypeKind(sameFile.Kind))
        {
            var span = sameFile.DefinitionSpan;
            var key = index?.DefinitionInFile(span.File, bare)?.QualifiedKey;
            return new ResolvedSymbol(bare, key, span);
        }

        // Cross-file: unique-bare-name guard over type definitions only (SymbolResolver.
        // PickBest semantics — several candidates, no qualified key to break the tie,
        // means decline).
        if (index is null)
            return null;
        var candidates = index
            .ResolveDefinition(null, bare)
            .Where(d => IsTypeKind(d.Kind))
            .ToList();
        return candidates.Count == 1
            ? new ResolvedSymbol(bare, candidates[0].QualifiedKey, candidates[0].Span)
            : null;
    }

    /// <summary>
    ///     Every occurrence of a resolved type target across the workspace: the
    ///     <see cref="AstNode.Name" /> uses (constructor calls, pattern case names) from
    ///     <see cref="WorkspaceIndex.FindReferences" />, plus the type-annotation uses
    ///     from <see cref="WorkspaceIndex.FindTypeReferences" />. Same-file type uses
    ///     arrive through both paths (the caller deduplicates); cross-file type uses ride
    ///     the bare name alone, so they count only when the name has exactly one type
    ///     definition in the workspace — otherwise they could belong to a same-named
    ///     declaration in another file, and guessing is worse than missing them.
    /// </summary>
    public static IEnumerable<SourceSpan> TypeReferenceSpans(
        WorkspaceIndex index,
        ResolvedSymbol target
    )
    {
        foreach (
            var reference in index.FindReferences(
                target.QualifiedKey,
                target.BareName,
                target.DefinitionSpan.File
            )
        )
            yield return reference.Span;

        var unique =
            index.ResolveDefinition(null, target.BareName).Count(def => IsTypeKind(def.Kind)) == 1;
        foreach (var typeUse in index.FindTypeReferences(target.BareName))
            if (
                unique
                || string.Equals(
                    typeUse.File,
                    target.DefinitionSpan.File,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                yield return typeUse.Span;
    }

    /// <summary>
    ///     True when the resolved target denotes a type declaration — record, union,
    ///     class, interface or type alias. Such a target's reference set includes
    ///     type-annotation uses in addition to <c>Name</c> uses (constructor calls,
    ///     pattern case names), whether the cursor started on the annotation, the
    ///     declaration, or a constructor call. The definition is matched by span, not
    ///     by name, so a same-named function in the same file cannot flip the answer.
    /// </summary>
    public static bool IsTypeTarget(
        DocumentState state,
        WorkspaceIndex? index,
        ResolvedSymbol target
    )
    {
        if (index is not null)
        {
            var def = index
                .DefinitionsInFile(target.DefinitionSpan.File)
                .FirstOrDefault(d =>
                    d.BareName == target.BareName && d.Span == target.DefinitionSpan
                );
            if (def is not null)
                return IsTypeKind(def.Kind);
        }

        return state.NameToDefinition.TryGetValue(target.BareName, out var local)
            && local.DefinitionSpan == target.DefinitionSpan
            && IsTypeKind(local.Kind);
    }

    /// <summary>
    ///     The symbol kinds that denote a type declaration — what a type position can
    ///     name. Union cases are excluded: they are value constructors, reached through
    ///     their <see cref="AstNode.Name" /> occurrences, not type positions.
    /// </summary>
    public static bool IsTypeKind(SymbolKind kind)
    {
        return kind
            is SymbolKind.Record
                or SymbolKind.Union
                or SymbolKind.Class
                or SymbolKind.Interface
                or SymbolKind.TypeAlias;
    }
}

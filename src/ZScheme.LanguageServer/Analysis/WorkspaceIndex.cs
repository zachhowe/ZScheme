using ZScheme.Compiler.Diagnostics;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     A definition harvested from some file's typed AST, tagged with the
///     package-qualified key that a use-site's <c>ResolvedQualifiedName</c> matches.
/// </summary>
/// <param name="QualifiedKey">
///     <c>"{module}/{name}"</c> (e.g. <c>"stdlib/option/some"</c>) when the file
///     belongs to a package; otherwise just the bare name. Matches the format the type
///     inferer writes into <see cref="ZScheme.Compiler.Ast.AstNode.Name.ResolvedQualifiedName" />.
/// </param>
public sealed record IndexedDefinition(
    string QualifiedKey,
    string BareName,
    SourceSpan Span,
    SymbolKind Kind,
    string? ContainerModule
)
{
    public string File => Span.File;
}

/// <summary>A single <c>Name</c> occurrence in some file.</summary>
/// <param name="QualifiedKey">
///     The use-site's resolved qualified name (imported/overloaded functions), or null
///     for uses that resolve locally / to non-function symbols.
/// </param>
public sealed record IndexedReference(string BareName, string? QualifiedKey, SourceSpan Span)
{
    public string File => Span.File;
}

/// <summary>
///     Workspace-wide symbol index. Definitions and references are stored per source
///     file so a single file can be re-indexed in place on edit without touching the
///     rest. Serves cross-file go-to-definition, find-references, and workspace symbol
///     search. Thread-safe: the initial disk scan runs on a background task while
///     editor edits update files on the request thread.
/// </summary>
public sealed class WorkspaceIndex
{
    private readonly object _lock = new();

    private readonly Dictionary<string, FileSlice> _files = new(StringComparer.OrdinalIgnoreCase);

    // Aggregate lookups, kept in sync incrementally with _files.
    private readonly Dictionary<string, List<IndexedDefinition>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IndexedDefinition>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IndexedReference>> _refsByName = new(
        StringComparer.Ordinal
    );

    private sealed record FileSlice(
        IReadOnlyList<IndexedDefinition> Definitions,
        IReadOnlyList<IndexedReference> References
    );

    /// <summary>Replaces the index slice for a single file.</summary>
    public void UpdateFile(
        string file,
        IReadOnlyList<IndexedDefinition> definitions,
        IReadOnlyList<IndexedReference> references
    )
    {
        lock (_lock)
        {
            RemoveFileLocked(file);
            _files[file] = new FileSlice(definitions, references);

            foreach (var def in definitions)
            {
                Add(_byKey, def.QualifiedKey, def);
                Add(_byName, def.BareName, def);
            }

            foreach (var reference in references)
                Add(_refsByName, reference.BareName, reference);
        }
    }

    public void RemoveFile(string file)
    {
        lock (_lock)
        {
            RemoveFileLocked(file);
        }
    }

    public bool Contains(string file)
    {
        lock (_lock)
        {
            return _files.ContainsKey(file);
        }
    }

    /// <summary>
    ///     Resolves the definition(s) a use-site refers to. Prefers an exact
    ///     qualified-key match (unambiguous — imported functions); otherwise falls back
    ///     to bare-name matches across the workspace.
    /// </summary>
    public IReadOnlyList<IndexedDefinition> ResolveDefinition(string? qualifiedKey, string bareName)
    {
        lock (_lock)
        {
            if (
                qualifiedKey is not null
                && _byKey.TryGetValue(qualifiedKey, out var byKey)
                && byKey.Count > 0
            )
                return [.. byKey];

            return _byName.TryGetValue(bareName, out var byName) ? [.. byName] : [];
        }
    }

    /// <summary>Returns the definition of <paramref name="bareName" /> declared in
    ///     <paramref name="file" />, if any (used to recover a local symbol's qualified key).</summary>
    public IndexedDefinition? DefinitionInFile(string file, string bareName)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(file, out var slice))
                return null;
            return slice.Definitions.FirstOrDefault(d => d.BareName == bareName);
        }
    }

    /// <summary>
    ///     All references to the symbol identified by (<paramref name="qualifiedKey" />,
    ///     <paramref name="bareName" />) defined in <paramref name="definingFile" />.
    ///     Cross-file references are matched by qualified key (functions); same-file
    ///     references are matched by bare name regardless of key (covers intra-file uses
    ///     that resolve locally and symbols the inferer does not tag, e.g. records).
    /// </summary>
    public IReadOnlyList<IndexedReference> FindReferences(
        string? qualifiedKey,
        string bareName,
        string? definingFile
    )
    {
        lock (_lock)
        {
            if (!_refsByName.TryGetValue(bareName, out var candidates))
                return [];

            return
            [
                .. candidates.Where(r =>
                    (qualifiedKey is not null && r.QualifiedKey == qualifiedKey)
                    || (
                        definingFile is not null
                        && string.Equals(r.File, definingFile, StringComparison.OrdinalIgnoreCase)
                    )
                ),
            ];
        }
    }

    /// <summary>Fuzzy (case-insensitive subsequence) search over all definitions for
    ///     <c>workspace/symbol</c>. An empty query returns everything.</summary>
    public IReadOnlyList<IndexedDefinition> SearchSymbols(string query)
    {
        lock (_lock)
        {
            var all = _files.Values.SelectMany(s => s.Definitions);
            if (string.IsNullOrWhiteSpace(query))
                return [.. all];
            return [.. all.Where(d => IsSubsequence(query, d.BareName))];
        }
    }

    private void RemoveFileLocked(string file)
    {
        if (!_files.Remove(file, out var slice))
            return;

        foreach (var def in slice.Definitions)
        {
            Remove(_byKey, def.QualifiedKey, def);
            Remove(_byName, def.BareName, def);
        }

        foreach (var reference in slice.References)
            Remove(_refsByName, reference.BareName, reference);
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(value);
    }

    private static void Remove<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
            return;
        list.Remove(value);
        if (list.Count == 0)
            map.Remove(key);
    }

    /// <summary>Case-insensitive subsequence match (VS Code Ctrl+T style).</summary>
    private static bool IsSubsequence(string query, string candidate)
    {
        var qi = 0;
        foreach (var c in candidate)
        {
            if (qi >= query.Length)
                break;
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length;
    }
}

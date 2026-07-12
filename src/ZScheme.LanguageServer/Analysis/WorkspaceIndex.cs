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
/// <param name="ImplementedInterfaces">
///     For <see cref="SymbolKind.Class" />: the bare names the class declares after
///     <c>:</c> — its interfaces plus the base-class candidate (the AST cannot
///     distinguish them; see <c>DefinitionCollector</c>). For
///     <see cref="SymbolKind.Interface" />: the bare names of the base interfaces it
///     extends. Null for every other kind.
/// </param>
/// <param name="ParamNames">
///     For <see cref="SymbolKind.Function" />: the declared parameter names, in order
///     (with <paramref name="IsVariadic" /> marking a trailing rest-parameter). Powers
///     call-site parameter-name inlay hints and named signature-help labels for
///     imported functions — <c>ZFuncType</c> itself carries no names. Null elsewhere.
/// </param>
public sealed record IndexedDefinition(
    string QualifiedKey,
    string BareName,
    SourceSpan Span,
    SymbolKind Kind,
    string? ContainerModule,
    IReadOnlyList<string>? ImplementedInterfaces = null,
    IReadOnlyList<string>? ParamNames = null,
    bool IsVariadic = false
)
{
    public string File => Span.File;
}

/// <summary>A single <c>Name</c> occurrence in some file.</summary>
/// <param name="QualifiedKey">
///     The use-site's resolved qualified name (imported/overloaded functions), or null
///     for uses that resolve locally / to non-function symbols.
/// </param>
/// <param name="ContainingDefinition">
///     The qualified key of the top-level definition whose form encloses this
///     occurrence (class methods attribute to the class), or null for module-scope
///     expressions. Powers call-hierarchy derivation — the compiler records no call
///     graph, so caller→callee is reconstructed from references grouped by container.
/// </param>
public sealed record IndexedReference(
    string BareName,
    string? QualifiedKey,
    SourceSpan Span,
    string? ContainingDefinition = null
)
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
    private readonly Dictionary<string, List<IndexedDefinition>> _implsByInterface = new(
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
                foreach (var iface in def.ImplementedInterfaces ?? [])
                    Add(_implsByInterface, iface, def);
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

    /// <summary>Snapshot of every indexed file path (used to re-index a package's files
    ///     when its manifest changes).</summary>
    public IReadOnlyList<string> IndexedFiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _files.Keys];
            }
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

    /// <summary>Snapshot of the top-level definitions indexed for
    ///     <paramref name="file" /> (used by code lens).</summary>
    public IReadOnlyList<IndexedDefinition> DefinitionsInFile(string file)
    {
        lock (_lock)
        {
            return _files.TryGetValue(file, out var slice) ? [.. slice.Definitions] : [];
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

    /// <summary>
    ///     Definitions whose bare name starts with <paramref name="prefix" />
    ///     (case-insensitive) for completion — one entry per distinct bare name (first
    ///     definition wins), capped at <paramref name="limit" />. An empty prefix returns
    ///     up to <paramref name="limit" /> distinct names.
    /// </summary>
    public IReadOnlyList<IndexedDefinition> CompletionCandidates(string prefix, int limit = 500)
    {
        lock (_lock)
        {
            var results = new List<IndexedDefinition>();
            foreach (var (name, defs) in _byName)
            {
                if (results.Count >= limit)
                    break;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (defs.Count > 0)
                    results.Add(defs[0]);
            }

            return results;
        }
    }

    /// <summary>
    ///     Everything that implements the interface (or extends the class) named
    ///     <paramref name="bareName" />: direct implementors/subclasses, plus —
    ///     transitively through extending interfaces and subclasses — theirs too.
    /// </summary>
    public IReadOnlyList<IndexedDefinition> FindImplementations(string bareName)
    {
        lock (_lock)
        {
            var results = new List<IndexedDefinition>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { bareName };
            var pending = new Queue<string>();
            pending.Enqueue(bareName);

            while (pending.Count > 0)
            {
                if (!_implsByInterface.TryGetValue(pending.Dequeue(), out var implementors))
                    continue;
                foreach (var def in implementors)
                {
                    results.Add(def);
                    if (visited.Add(def.BareName))
                        pending.Enqueue(def.BareName);
                }
            }

            return results;
        }
    }

    /// <summary>
    ///     Callers of the symbol (<paramref name="qualifiedKey" />, <paramref name="bareName" />)
    ///     defined at <paramref name="definitionSpan" />: its references grouped by their
    ///     enclosing top-level definition, resolved back to that caller's
    ///     <see cref="IndexedDefinition" />. The declaration-site occurrence and
    ///     module-scope references (null container) are dropped — the latter have no
    ///     caller item to hang a hierarchy node on.
    /// </summary>
    public IReadOnlyList<(IndexedDefinition Caller, IReadOnlyList<SourceSpan> FromSpans)> IncomingCalls(
        string? qualifiedKey,
        string bareName,
        string? definingFile,
        SourceSpan definitionSpan
    )
    {
        lock (_lock)
        {
            if (!_refsByName.TryGetValue(bareName, out var candidates))
                return [];

            var byCaller = new Dictionary<string, List<SourceSpan>>(StringComparer.Ordinal);
            foreach (var r in candidates)
            {
                var matches =
                    (qualifiedKey is not null && r.QualifiedKey == qualifiedKey)
                    || (
                        definingFile is not null
                        && string.Equals(r.File, definingFile, StringComparison.OrdinalIgnoreCase)
                    );
                if (!matches || r.ContainingDefinition is null || r.Span == definitionSpan)
                    continue;
                if (!byCaller.TryGetValue(r.ContainingDefinition, out var spans))
                    byCaller[r.ContainingDefinition] = spans = [];
                spans.Add(r.Span);
            }

            var result = new List<(IndexedDefinition, IReadOnlyList<SourceSpan>)>();
            foreach (var (containerKey, spans) in byCaller)
                if (_byKey.TryGetValue(containerKey, out var defs) && defs.Count > 0)
                    result.Add((defs[0], spans));
            return result;
        }
    }

    /// <summary>
    ///     Callees of the definition with <paramref name="qualifiedKey" /> in
    ///     <paramref name="file" />: references contained in it, resolved to their
    ///     definitions and filtered to callable kinds (functions, plus record/union-case
    ///     constructors — those are calls in this language). Ambiguous names (several
    ///     workspace definitions, none in this file) are skipped rather than guessed.
    /// </summary>
    public IReadOnlyList<(IndexedDefinition Target, IReadOnlyList<SourceSpan> FromSpans)> OutgoingCalls(
        string qualifiedKey,
        string file,
        SourceSpan definitionSpan
    )
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(file, out var slice))
                return [];

            var byTarget = new Dictionary<string, (IndexedDefinition Def, List<SourceSpan> Spans)>(
                StringComparer.Ordinal
            );
            foreach (var r in slice.References)
            {
                if (r.ContainingDefinition != qualifiedKey || r.Span == definitionSpan)
                    continue;
                var target = ResolveTargetLocked(r.QualifiedKey, r.BareName, file);
                if (target is null || !IsCallable(target.Kind))
                    continue;
                if (!byTarget.TryGetValue(target.QualifiedKey, out var entry))
                    byTarget[target.QualifiedKey] = entry = (target, []);
                entry.Spans.Add(r.Span);
            }

            return [.. byTarget.Values.Select(e => (e.Def, (IReadOnlyList<SourceSpan>)e.Spans))];
        }
    }

    /// <summary>Direct implementors/subclasses only — one hierarchy level per expansion
    ///     (unlike <see cref="FindImplementations" />, which is transitive).</summary>
    public IReadOnlyList<IndexedDefinition> DirectImplementations(string bareName)
    {
        lock (_lock)
        {
            return _implsByInterface.TryGetValue(bareName, out var impls) ? [.. impls] : [];
        }
    }

    /// <summary>The unique definition of <paramref name="bareName" />, or null when the
    ///     name is undefined or defined in several places (used to resolve supertype
    ///     names — guessing would build a wrong hierarchy).</summary>
    public IndexedDefinition? UniqueDefinition(string bareName)
    {
        lock (_lock)
        {
            return _byName.TryGetValue(bareName, out var defs) && defs.Count == 1
                ? defs[0]
                : null;
        }
    }

    private IndexedDefinition? ResolveTargetLocked(string? qualifiedKey, string bareName, string file)
    {
        if (
            qualifiedKey is not null
            && _byKey.TryGetValue(qualifiedKey, out var byKey)
            && byKey.Count > 0
        )
            return byKey[0];

        if (!_byName.TryGetValue(bareName, out var byName) || byName.Count == 0)
            return null;
        if (byName.Count == 1)
            return byName[0];
        // Several definitions share the bare name: an unresolved use most plausibly
        // targets this file's own definition; otherwise skip rather than guess.
        return byName.FirstOrDefault(d =>
            string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool IsCallable(SymbolKind kind)
    {
        return kind is SymbolKind.Function or SymbolKind.UnionCase or SymbolKind.Record
            or SymbolKind.Class;
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
            foreach (var iface in def.ImplementedInterfaces ?? [])
                Remove(_implsByInterface, iface, def);
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

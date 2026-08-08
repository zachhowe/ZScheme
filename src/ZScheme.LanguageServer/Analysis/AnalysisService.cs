using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

public sealed class AnalysisService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AnalysisService>();

    /// <summary>How long a single document's analysis may block the caller. Comfortably
    ///     under the timeouts editors apply to LSP requests (Zed cancels at 120s).</summary>
    internal static readonly TimeSpan AnalysisBudget = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, DocumentState> _documents = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingAnalysis = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly WorkspaceIndex _index = new();
    private readonly WorkspaceExclusions _exclusions = new();
    private int _workspaceScanStarted;

    private readonly object _reindexLock = new();
    private readonly HashSet<string> _reindexQueue = new(StringComparer.OrdinalIgnoreCase);
    private int _reindexGeneration;

    /// <summary>Workspace-wide symbol index backing cross-file definition, references,
    ///     and workspace symbol search.</summary>
    public WorkspaceIndex Index => _index;

    public DocumentState? GetDocument(string uri)
    {
        return _documents.TryGetValue(uri, out var state) ? state : null;
    }

    /// <summary>
    ///     Kicks off a one-time background scan that compiles and indexes every
    ///     <c>.zs</c> file under the given workspace roots, so cross-file navigation
    ///     works into files the user has not opened. Idempotent; safe to call once at
    ///     server startup. Open editor buffers (indexed on open/edit) always take
    ///     precedence over their on-disk copy.
    /// </summary>
    public Task InitializeWorkspaceAsync(
        IEnumerable<string> roots,
        IWorkspaceScanReporter? reporter = null
    )
    {
        if (Interlocked.Exchange(ref _workspaceScanStarted, 1) != 0)
            return Task.CompletedTask;

        var rootList = roots
            .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rootList.Count == 0)
            return Task.CompletedTask;

        _exclusions.AddRoots(rootList);
        return Task.Run(() => ScanWorkspace(rootList, reporter));
    }

    /// <summary>
    ///     Scans additional roots after startup (<c>didChangeWorkspaceFolders</c>).
    ///     Unlike <see cref="InitializeWorkspaceAsync" /> this is not one-shot — each
    ///     call scans its roots; already-indexed files are simply re-indexed.
    /// </summary>
    public Task ScanAdditionalRootsAsync(
        IEnumerable<string> roots,
        IWorkspaceScanReporter? reporter = null
    )
    {
        var rootList = roots
            .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rootList.Count == 0)
            return Task.CompletedTask;

        _exclusions.AddRoots(rootList);
        return Task.Run(() => ScanWorkspace(rootList, reporter));
    }

    /// <summary>Removes every indexed file under <paramref name="root" /> from the
    ///     workspace index (a workspace folder was removed).</summary>
    public void PurgeRoot(string root)
    {
        _exclusions.RemoveRoot(root);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        foreach (var file in _index.IndexedFiles)
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _index.RemoveFile(file);
    }

    public async Task<DocumentState> AnalyzeAsync(string uri, string source, int version)
    {
        // Cancel any pending analysis for this document
        if (_pendingAnalysis.TryRemove(uri, out var previousCts))
            await previousCts.CancelAsync();

        var cts = new CancellationTokenSource();
        _pendingAnalysis[uri] = cts;

        try
        {
            // Debounce: wait 300ms before analyzing
            await Task.Delay(300, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return _documents.TryGetValue(uri, out var existing)
                ? existing
                : new DocumentState(
                    uri,
                    version,
                    source,
                    null,
                    new DiagnosticBag(),
                    [],
                    new Dictionary<string, SymbolInfo>(),
                    new Dictionary<string, AstNode.TypeAliasDecl>()
                );
        }
        finally
        {
            _pendingAnalysis.TryRemove(uri, out _);
        }

        return AnalyzeGuarded(uri, source, version);
    }

    public DocumentState AnalyzeImmediate(string uri, string source, int version)
    {
        return AnalyzeGuarded(uri, source, version);
    }

    /// <summary>
    ///     Runs <see cref="RunAnalysis" /> so that neither a crash nor a pathologically
    ///     slow compile can take the document down with it.
    ///     <para>
    ///         The document is registered <em>before</em> analysis starts. Previously a
    ///         throwing or non-returning analysis meant <see cref="GetDocument" /> kept
    ///         returning null, so every navigation request answered "no result" instantly,
    ///         forever, with nothing logged — the failure was completely invisible.
    ///     </para>
    ///     <para>
    ///         <c>Compilation.Compile</c> takes no cancellation token, so what is bounded
    ///         here is the <em>wait</em>, not the work: on expiry we return the current
    ///         (last-good or placeholder) state and let the orphaned task publish its
    ///         result if it ever finishes.
    ///     </para>
    /// </summary>
    private DocumentState AnalyzeGuarded(string uri, string source, int version)
    {
        var placeholder = _documents.TryGetValue(uri, out var previous)
            ? previous with
            {
                Version = version,
                Source = source,
            }
            : EmptyState(uri, source, version);
        _documents[uri] = placeholder;

        var analysis = Task.Run(() => RunAnalysis(uri, source, version));

        bool finished;
        try
        {
            // VSTHRD002: blocking is the point — callers need a DocumentState back. It is
            // safe and bounded here: the wait is on a thread-pool task with no
            // synchronization context, and it always gives up after AnalysisBudget.
#pragma warning disable VSTHRD002
            finished = analysis.Wait(AnalysisBudget);
#pragma warning restore VSTHRD002
        }
        catch (AggregateException error)
        {
            // Wait rethrows whatever RunAnalysis threw. Without this the exception would
            // escape the didOpen handler, no diagnostics would ever be published, and the
            // document would keep serving the empty placeholder — silently.
            var crashed = Failed(uri, source, version, DescribeFailure(error));
            _documents[uri] = crashed;
            return crashed;
        }

        if (finished)
        {
            // Already completed, so this does not block.
#pragma warning disable VSTHRD002
            var state = analysis.IsCompletedSuccessfully ? analysis.Result : null;
#pragma warning restore VSTHRD002
            if (state is not null)
            {
                _documents[uri] = state;
                return state;
            }

            var failure = Failed(uri, source, version, DescribeFailure(analysis.Exception));
            _documents[uri] = failure;
            return failure;
        }

        Log.Warning(
            "Analysis of {Uri} exceeded {Budget}s; serving the last known state while it finishes",
            uri,
            AnalysisBudget.TotalSeconds
        );

        // Adopt the result whenever it lands, so a slow first compile still converges.
        _ = analysis.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    _documents[uri] = t.Result;
                else
                    Log.Error(t.Exception, "Analysis of {Uri} failed", uri);
            },
            TaskScheduler.Default
        );

        var timedOut = Failed(
            uri,
            source,
            version,
            $"ZScheme analysis is taking longer than {AnalysisBudget.TotalSeconds:0}s; "
                + "results for this file are stale until it completes."
        );
        _documents[uri] = timedOut;
        return timedOut;
    }

    private static string DescribeFailure(AggregateException? error)
    {
        var inner = error?.GetBaseException();
        Log.Error(inner, "ZScheme analysis failed");
        return inner is null
            ? "ZScheme analysis failed."
            : $"ZScheme analysis failed: {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>A state carrying a single diagnostic explaining why analysis produced
    ///     nothing, so the editor shows a reason instead of silently offering no
    ///     navigation.</summary>
    private static DocumentState Failed(string uri, string source, int version, string message)
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.Error(message, new SourceSpan(UriToFilePath(uri), 1, 1, 1));
        return new DocumentState(
            uri,
            version,
            source,
            null,
            diagnostics,
            [],
            new Dictionary<string, SymbolInfo>(),
            new Dictionary<string, AstNode.TypeAliasDecl>()
        );
    }

    private static DocumentState EmptyState(string uri, string source, int version)
    {
        return new DocumentState(
            uri,
            version,
            source,
            null,
            new DiagnosticBag(),
            [],
            new Dictionary<string, SymbolInfo>(),
            new Dictionary<string, AstNode.TypeAliasDecl>()
        );
    }

    public void RemoveDocument(string uri)
    {
        _documents.TryRemove(uri, out _);
        if (_pendingAnalysis.TryRemove(uri, out var cts))
            cts.Cancel();
    }

    /// <summary>
    ///     Re-compiles <paramref name="fileName" /> from its on-disk contents and refreshes
    ///     its slice of the workspace index. No-ops when the file has an open editor buffer
    ///     (the buffer is always the freshest view). Removes the slice when the file no
    ///     longer exists on disk. When the on-disk source fails to compile, the previous
    ///     slice is kept (last-good, matching the open-document path).
    /// </summary>
    public void ReindexFromDisk(string fileName)
    {
        var full = CanonicalPath(Path.GetFullPath(fileName));
        if (
            _exclusions.IsExcluded(full, isDirectory: false)
            || !full.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
            || HasOpenDocument(full)
        )
            return;

        if (!File.Exists(full))
        {
            _index.RemoveFile(full);
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch
        {
            return;
        }

        try
        {
            var (program, _, _) = CompileFile(full, text);
            if (program is not null)
                IndexFile(full, program);
        }
        catch
        {
            // Best-effort: a file that fails to compile in isolation keeps its old slice.
        }
    }

    /// <summary>Removes <paramref name="fileName" />'s slice from the workspace index
    ///     (deleted files).</summary>
    public void RemoveFromIndex(string fileName)
    {
        _index.RemoveFile(Path.GetFullPath(fileName));
    }

    /// <summary>
    ///     Coalescing entry point for file-watcher events. Queues the file and drains the
    ///     queue after a quiet period, so an event storm (branch switch, <c>git pull</c>)
    ///     triggers one re-index per unique file rather than one compile per event. The
    ///     returned task completes when the batch containing this file has been drained;
    ///     superseded (re-debounced) calls complete early, their paths drained by the
    ///     latest caller's task.
    /// </summary>
    public Task QueueReindexAsync(string fileName)
    {
        var full = Path.GetFullPath(fileName);
        if (_exclusions.IsExcluded(full, isDirectory: false))
            return Task.CompletedTask;

        int generation;
        lock (_reindexLock)
        {
            _reindexQueue.Add(full);
            generation = ++_reindexGeneration;
        }

        return Task.Run(async () =>
        {
            await Task.Delay(500);

            string[] batch;
            lock (_reindexLock)
            {
                // Superseded by a newer event: its drain covers the shared queue.
                if (generation != _reindexGeneration)
                    return;
                batch = [.. _reindexQueue];
                _reindexQueue.Clear();
            }

            foreach (var file in batch)
                try
                {
                    ReindexFromDisk(file);
                }
                catch
                {
                    // Best-effort, same stance as ScanWorkspace.
                }
        });
    }

    private bool HasOpenDocument(string fullPath)
    {
        foreach (var uri in _documents.Keys)
        {
            string docPath;
            try
            {
                docPath = Path.GetFullPath(UriToFilePath(uri));
            }
            catch
            {
                continue;
            }

            if (string.Equals(docPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private DocumentState RunAnalysis(string uri, string source, int version)
    {
        var fileName = UriToFilePath(uri);

        if (fileName.EndsWith(".zspkg", StringComparison.OrdinalIgnoreCase))
            return AnalyzeManifest(uri, source, version, fileName);

        var (program, diagnostics, canonicalizer) = CompileFile(fileName, source);

        // Refresh this file's slice of the workspace index from the fresh AST — an open
        // editor buffer is always the freshest view of the file.
        if (program is not null)
            IndexFile(fileName, program);

        // Editor-only suggestions, layered on the compiler's diagnostics. Token-based, so they
        // do not need the AST and stay useful on the last-good path below. Only the open
        // document gets them — background indexing calls CompileFile directly.
        if (canonicalizer is not null)
            new RedundantTypeQualifierAnalyzer(diagnostics).Analyze(
                source,
                fileName,
                canonicalizer
            );

        // Last-good fallback: when the current source fails before type inference (transient
        // parse errors during typing), reuse the previous typed AST + symbols so hover, go-to,
        // and completion keep working. Fresh diagnostics still surface.
        if (
            program is null
            && _documents.TryGetValue(uri, out var previous)
            && previous.Ast is not null
        )
            return new DocumentState(
                uri,
                version,
                source,
                previous.Ast,
                diagnostics,
                previous.Symbols,
                previous.NameToDefinition,
                previous.TypeAliases
            );

        return MakeState(uri, version, source, program, diagnostics);
    }

    /// <summary>
    ///     Type-checks a single file with full package-aware context (the same setup the
    ///     active document uses), returning its typed AST, diagnostics, and the canonicalizer
    ///     stage 4 built for it (null when compilation failed before then). Shared by the
    ///     active-document path and background workspace indexing.
    /// </summary>
    private (
        AstNode.Program? Program,
        DiagnosticBag Diagnostics,
        TypeNameCanonicalizer? Canonicalizer
    ) CompileFile(string fileName, string source)
    {
        var env = DiscoverPackages(fileName);
        var assemblySearchPaths = ResolveNuGetAssemblyPaths(env.NuGetDeps);
        assemblySearchPaths.AddRange(ResolveFrameworkAssemblyPaths(env.Frameworks));
        AddDistinctPaths(assemblySearchPaths, env.AssemblySearchPaths);
        var primaryModuleName = DerivePrimaryModuleName(fileName);

        var options = new CompilerOptions
        {
            StopAfterTypeInference = true,
            AllowsImplicitModuleName = true,
            PackagePaths = env.PackagePaths,
            ModuleAliases = env.ModuleAliases,
            ModuleSearchPaths = env.ExtraSearchPaths,
            AssemblySearchPaths = assemblySearchPaths,
            PrimaryModuleName = primaryModuleName,
        };

        var compilation = new Compilation(options);
        compilation.Compile(source, fileName);
        return (compilation.TypedProgram, compilation.GetDiagnostics(), compilation.Canonicalizer);
    }

    /// <summary>
    ///     Resolves a logical module name (as written in an <c>(import …)</c>) to the
    ///     file it denotes, using the same search-path/package/alias setup the compiler
    ///     uses when compiling <paramref name="documentPath" /> (mirrors
    ///     <c>Compilation.CreateModuleResolver</c>). Null when unresolvable. Used by
    ///     document links.
    /// </summary>
    public string? ResolveModulePath(string documentPath, string moduleName)
    {
        try
        {
            var env = DiscoverPackages(documentPath);
            var resolver = new ModuleResolver(new DiagnosticBag());

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(documentPath));
            if (sourceDir is not null)
                resolver.AddSearchPath(sourceDir);
            foreach (var path in env.ExtraSearchPaths)
                resolver.AddSearchPath(path);
            foreach (var (name, path) in env.PackagePaths)
            {
                resolver.AddPackagePath(name, path);
                if (name == "stdlib")
                    resolver.AddSearchPath(path);
            }

            foreach (var (alias, qualified) in env.ModuleAliases)
                resolver.AddModuleAlias(alias, qualified);

            return resolver.Resolve(moduleName, SourceSpan.None)?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Harvests <paramref name="fileName" />'s top-level definitions and name
    ///     references into the workspace index.</summary>
    private void IndexFile(string fileName, AstNode.Program program)
    {
        var primaryModule = DerivePrimaryModuleName(fileName);
        var definitions = DefinitionCollector.Collect(program, primaryModule);
        var references = ReferenceCollector.Collect(program, primaryModule);
        _index.UpdateFile(fileName, definitions, references);
    }

    /// <summary>
    ///     Spells an on-disk path the way a client-supplied one is spelled. Paths that arrive as LSP
    ///     URIs go through <see cref="DocumentUri.GetFileSystemPath" />, which lower-cases the
    ///     Windows drive letter; a raw directory walk preserves it. Routing walked paths through the
    ///     same conversion keeps one spelling in the index and in every <c>SourceSpan.File</c> handed
    ///     back, so a scanned file and an opened one compare equal without relying on the index's
    ///     case-insensitive keys.
    /// </summary>
    private static string CanonicalPath(string path)
    {
        return DocumentUri.FromFileSystemPath(path).GetFileSystemPath();
    }

    private void ScanWorkspace(IReadOnlyList<string> roots, IWorkspaceScanReporter? reporter)
    {
        // Materialize the work list up front so progress can report a total. The walk
        // prunes ignored/generated directories (see WorkspaceExclusions) so trees like
        // the fuzzer's fuzz-runs/ never reach the index.
        var pending = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var full in _exclusions.EnumerateSourceFiles(root))
                {
                    var canonical = CanonicalPath(full);
                    if (!_index.Contains(canonical))
                        pending.Add(canonical);
                }
            }
            catch (Exception ex)
            {
                // Unreadable root: skip. Logged because this silently truncates the work list,
                // which otherwise looks identical to "the root legitimately had no sources".
                Log.Debug(ex, "ScanWorkspace: enumeration of root {Root} failed", root);
            }
        }

        reporter?.Begin(pending.Count);
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var full = pending[i];
                // Reported before the read, so the report says "the exclusion rules kept
                // this file" and nothing more. Both skips below leave the file out of the
                // index without leaving it out of the report — that difference is the only
                // way an observer can tell a filtered file from an unreadable one.
                reporter?.Report(i + 1, pending.Count, full);

                string text;
                try
                {
                    text = File.ReadAllText(full);
                }
                catch (Exception ex)
                {
                    // A transient sharing lock (AV scanner, another writer) lands here and drops
                    // the file from the index for the rest of the session.
                    Log.Debug(ex, "ScanWorkspace: could not read {File}", full);
                    continue;
                }

                try
                {
                    var (program, _, _) = CompileFile(full, text);
                    if (program is not null)
                        IndexFile(full, program);
                }
                catch (Exception ex)
                {
                    // Best-effort: a file that fails to compile in isolation is skipped.
                    Log.Debug(ex, "ScanWorkspace: could not index {File}", full);
                }
            }
        }
        finally
        {
            reporter?.End();
        }
    }

    private static DocumentState AnalyzeManifest(
        string uri,
        string source,
        int version,
        string fileName
    )
    {
        var diagnostics = new DiagnosticBag();
        new ManifestParser(diagnostics).Parse(source, fileName);
        return MakeState(uri, version, source, null, diagnostics);
    }

    private static DocumentState MakeState(
        string uri,
        int version,
        string source,
        AstNode.Program? program,
        DiagnosticBag diagnostics
    )
    {
        IReadOnlyList<SymbolInfo> symbols = [];
        IReadOnlyDictionary<string, SymbolInfo> nameToDefinition =
            new Dictionary<string, SymbolInfo>();
        IReadOnlyDictionary<string, AstNode.TypeAliasDecl> typeAliases =
            new Dictionary<string, AstNode.TypeAliasDecl>();

        if (program is not null)
        {
            var collector = new SymbolCollector();
            collector.Collect(program);
            symbols = collector.Symbols;
            nameToDefinition = collector.NameToDefinition;
            typeAliases = collector.TypeAliases;
        }

        return new DocumentState(
            uri,
            version,
            source,
            program,
            diagnostics,
            symbols,
            nameToDefinition,
            typeAliases
        );
    }

    private static string UriToFilePath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        return uri;
    }

    /// <summary>
    ///     Walks up from the file's directory looking for a sibling <c>packages/</c> directory
    ///     containing subdirectories with <c>package.zspkg</c> manifests, then layers on the
    ///     owning package's own manifest. Returns the package paths, module aliases (for
    ///     <c>default-module</c>), extra search paths, assembly search paths, and NuGet
    ///     dependencies needed to type-check the file. When the file lives inside a package's
    ///     declared test directory, that package's <c>test-dependencies</c> are also resolved
    ///     so unqualified imports like <c>(import zunit)</c> succeed.
    /// </summary>
    private static DiscoveredEnvironment DiscoverPackages(string filePath)
    {
        var paths = new Dictionary<string, string>();
        var aliases = new Dictionary<string, string>();
        var extraSearchPaths = new List<string>();
        var assemblyPaths = new List<string>();
        var nuget = new List<NuGetDependency>();
        var seenNuGet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new List<FrameworkDependency>();
        var seenFramework = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fullFilePath = Path.GetFullPath(filePath);
        var (ownerManifest, ownerDir, isTestFile) = FindOwningPackage(fullFilePath);

        var dir = Path.GetDirectoryName(fullFilePath);
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(packagesDir))
                {
                    var manifestPath = Path.Combine(sub, "package.zspkg");
                    if (!File.Exists(manifestPath))
                        continue;

                    var diag = new DiagnosticBag();
                    var parser = new ManifestParser(diag);
                    var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
                    if (manifest?.ImportPrefix is null || diag.HasErrors)
                        continue;

                    RegisterManifest(
                        manifest,
                        sub,
                        paths,
                        aliases,
                        nuget,
                        seenNuGet,
                        frameworks,
                        seenFramework
                    );
                }

                if (paths.Count > 0)
                    break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        if (ownerManifest is not null && ownerDir is not null)
        {
            ApplyOwnerContext(
                ownerManifest,
                ownerDir,
                paths,
                aliases,
                extraSearchPaths,
                assemblyPaths,
                nuget,
                seenNuGet,
                frameworks,
                seenFramework
            );

            if (isTestFile)
                ApplyTestContext(
                    ownerManifest,
                    ownerDir,
                    paths,
                    aliases,
                    extraSearchPaths,
                    assemblyPaths,
                    nuget,
                    seenNuGet,
                    frameworks,
                    seenFramework
                );
        }

        return new DiscoveredEnvironment(
            paths,
            aliases,
            extraSearchPaths,
            assemblyPaths,
            nuget,
            frameworks
        );
    }

    /// <summary>
    ///     Walks up from <paramref name="fullFilePath" /> looking for the nearest
    ///     <c>package.zspkg</c>. Returns the parsed manifest, its directory, and whether the
    ///     file lives under <c>Sources.Test</c> of that package.
    /// </summary>
    private static (
        PackageManifest? Manifest,
        string? PackageDir,
        bool IsTestFile
    ) FindOwningPackage(string fullFilePath)
    {
        var dir = Path.GetDirectoryName(fullFilePath);
        while (dir is not null)
        {
            var manifestPath = Path.Combine(dir, "package.zspkg");
            if (File.Exists(manifestPath))
            {
                var diag = new DiagnosticBag();
                var manifest = new ManifestParser(diag).Parse(
                    File.ReadAllText(manifestPath),
                    manifestPath
                );
                if (manifest is null || diag.HasErrors)
                    return (null, null, false);

                var isTest = false;
                if (manifest.Sources?.Test is { } testRel)
                {
                    var testDir = Path.GetFullPath(Path.Combine(dir, testRel));
                    isTest = IsPathUnder(fullFilePath, testDir);
                }

                return (manifest, dir, isTest);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return (null, null, false);
    }

    /// <summary>
    ///     When the file lives under a package's main source directory, returns the
    ///     package-qualified module name (e.g. <c>"stdlib/list"</c>) that
    ///     <see cref="ZScheme.Compiler.Package.LibraryCompiler" /> would use when compiling
    ///     it. Setting this as <see cref="CompilerOptions.PrimaryModuleName" /> ensures
    ///     locally-defined functions register under the same qualified prefix that the
    ///     prelude self-import sees, preventing duplicate overload candidates (e.g.
    ///     <c>list/list</c> vs. <c>stdlib/list/list</c>) when editing prelude modules
    ///     such as <c>packages/stdlib/src/list.zs</c>. Returns <c>null</c> for files
    ///     outside any package or under a package's test directory.
    /// </summary>
    private static string? DerivePrimaryModuleName(string filePath)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        var (manifest, packageDir, isTestFile) = FindOwningPackage(fullFilePath);
        if (manifest?.ImportPrefix is null || packageDir is null || isTestFile)
            return null;

        var sourceDirRel = manifest.Sources?.Main;
        var sourceDir = sourceDirRel is not null
            ? Path.GetFullPath(Path.Combine(packageDir, sourceDirRel))
            : packageDir;
        if (!IsPathUnder(fullFilePath, sourceDir))
            return null;

        var relativePath = Path.GetRelativePath(sourceDir, fullFilePath);
        var modulePart = Path.ChangeExtension(relativePath, null)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return $"{manifest.ImportPrefix}/{modulePart}";
    }

    private static bool IsPathUnder(string filePath, string ancestorDir)
    {
        var normalized =
            Path.GetFullPath(ancestorDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return filePath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterManifest(
        PackageManifest manifest,
        string packageDir,
        Dictionary<string, string> paths,
        Dictionary<string, string> aliases,
        List<NuGetDependency> nuget,
        HashSet<string> seenNuGet,
        List<FrameworkDependency> frameworks,
        HashSet<string> seenFramework
    )
    {
        if (manifest.ImportPrefix is null)
            return;

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(packageDir, manifest.Sources.Main))
            : packageDir;

        paths[manifest.ImportPrefix] = sourceDir;

        if (manifest.DefaultModule is { } defMod)
            aliases.TryAdd(manifest.ImportPrefix, $"{manifest.ImportPrefix}/{defMod}");

        foreach (var dep in manifest.Dependencies.NuGet)
            if (seenNuGet.Add($"{dep.PackageId}|{dep.Version}"))
                nuget.Add(dep);

        foreach (var fw in manifest.Dependencies.Frameworks)
            if (seenFramework.Add(fw.Id))
                frameworks.Add(fw);
    }

    /// <summary>
    ///     Merges the file's own package into the resolution maps: its import prefix (so
    ///     intra-package imports like <c>(import mypkg/lib/helper)</c> resolve), and the
    ///     transitive closure of its main dependencies plus its <c>(build (main (ref …)))</c>
    ///     paths — resolved through the same <see cref="PackageOptionsBuilder" /> every real
    ///     compile uses, so <c>import-clr</c> types from a manifest-referenced build output
    ///     resolve while editing exactly as they do on the command line. The sibling
    ///     <c>packages/</c> walk-up alone cannot see either: a package outside such a
    ///     directory registers nothing, and ref paths are not part of that scan.
    ///     Resolution diagnostics are discarded — the LSP is best-effort and should not
    ///     surface our own setup errors as user-facing diagnostics.
    /// </summary>
    private static void ApplyOwnerContext(
        PackageManifest ownerManifest,
        string ownerDir,
        Dictionary<string, string> paths,
        Dictionary<string, string> aliases,
        List<string> extraSearchPaths,
        List<string> assemblyPaths,
        List<NuGetDependency> nuget,
        HashSet<string> seenNuGet,
        List<FrameworkDependency> frameworks,
        HashSet<string> seenFramework
    )
    {
        RegisterManifest(
            ownerManifest,
            ownerDir,
            paths,
            aliases,
            nuget,
            seenNuGet,
            frameworks,
            seenFramework
        );

        // NuGet resolution can reach the network, and a package whose feed is unreachable
        // would otherwise take its ref paths and module search paths down with it — an
        // editor that stops resolving CLR types the moment you go offline. Retry without
        // it so the offline-resolvable half of the manifest still lands.
        var inputs =
            TryResolvePackage(ownerDir, ownerManifest, resolveNuGetDependencies: true)
            ?? TryResolvePackage(ownerDir, ownerManifest, resolveNuGetDependencies: false);

        if (inputs is null)
            return;

        AddDistinctPaths(assemblyPaths, inputs.AssemblySearchPaths);
        AddDistinctPaths(extraSearchPaths, inputs.ModuleSearchPaths);

        // The walk-up scan ran first and is the more specific answer for a file inside a
        // `packages/` layout, so it wins on conflicts.
        foreach (var (prefix, path) in inputs.PackagePaths)
            paths.TryAdd(prefix, path);
        foreach (var (prefix, alias) in inputs.ModuleAliases)
            aliases.TryAdd(prefix, alias);
    }

    /// <summary>
    ///     <see cref="PackageOptionsBuilder.Resolve" /> with every failure — thrown or
    ///     reported — flattened to <c>null</c>. Each attempt gets its own diagnostic bag:
    ///     the builder bails early whenever the bag it was handed already has errors, so a
    ///     shared bag would make a retry fail before it started.
    /// </summary>
    private static ResolvedPackageInputs? TryResolvePackage(
        string manifestDir,
        PackageManifest manifest,
        bool resolveNuGetDependencies
    )
    {
        try
        {
            return PackageOptionsBuilder.Resolve(
                manifestDir,
                manifest,
                new DiagnosticBag(),
                resolveNuGetDependencies
            );
        }
        catch (Exception ex)
        {
            Log.Debug(
                "AnalysisService: package resolution failed for {ManifestDir}: {Error}",
                manifestDir,
                ex.Message
            );
            return null;
        }
    }

    /// <summary>
    ///     Resolves <paramref name="ownerManifest" />'s test dependencies and merges them into
    ///     the resolution maps. Mirrors what <c>PackageTester</c> does when compiling test
    ///     files. Resolution diagnostics are discarded — the LSP is best-effort and should
    ///     not surface our own setup errors as user-facing diagnostics.
    /// </summary>
    private static void ApplyTestContext(
        PackageManifest ownerManifest,
        string ownerDir,
        Dictionary<string, string> paths,
        Dictionary<string, string> aliases,
        List<string> extraSearchPaths,
        List<string> assemblyPaths,
        List<NuGetDependency> nuget,
        HashSet<string> seenNuGet,
        List<FrameworkDependency> frameworks,
        HashSet<string> seenFramework
    )
    {
        if (ownerManifest.Sources?.Test is { } testRel)
        {
            var testDir = Path.GetFullPath(Path.Combine(ownerDir, testRel));
            if (Directory.Exists(testDir))
                extraSearchPaths.Add(testDir);
        }

        // Test-only ref paths, mirroring PackageTester's test compilation.
        if (ownerManifest.Build.Test is { } testBuild)
            AddDistinctPaths(
                assemblyPaths,
                testBuild.RefPaths.Select(r => Path.GetFullPath(Path.Combine(ownerDir, r)))
            );

        foreach (var dep in ownerManifest.TestDependencies.NuGet)
            if (seenNuGet.Add($"{dep.PackageId}|{dep.Version}"))
                nuget.Add(dep);

        foreach (var fw in ownerManifest.TestDependencies.Frameworks)
            if (seenFramework.Add(fw.Id))
                frameworks.Add(fw);

        if (ownerManifest.TestDependencies.ZScheme.Count == 0)
            return;

        var sink = new DiagnosticBag();
        List<string> depPaths;
        try
        {
            var resolver = new ZSchemeDependencyResolver(sink, ownerDir);
            depPaths = resolver.Resolve(ownerManifest.TestDependencies.ZScheme);
        }
        catch
        {
            return;
        }

        foreach (var depPath in depPaths)
        {
            var manifestPath = Path.Combine(depPath, "package.zspkg");
            if (!File.Exists(manifestPath))
                continue;

            var depDiag = new DiagnosticBag();
            var depManifest = new ManifestParser(depDiag).Parse(
                File.ReadAllText(manifestPath),
                manifestPath
            );
            if (depManifest?.ImportPrefix is null || depDiag.HasErrors)
                continue;

            RegisterManifest(
                depManifest,
                depPath,
                paths,
                aliases,
                nuget,
                seenNuGet,
                frameworks,
                seenFramework
            );
        }
    }

    /// <summary>
    ///     Resolves <paramref name="deps" /> via <see cref="NuGetResolver" /> and returns the
    ///     resulting DLL directory in a single-element list (or an empty list on failure / no
    ///     deps). Resolution diagnostics are discarded — they would otherwise pollute user-facing
    ///     diagnostics for problems they did not cause.
    /// </summary>
    private static List<string> ResolveNuGetAssemblyPaths(IReadOnlyList<NuGetDependency> deps)
    {
        if (deps.Count == 0)
            return [];

        var sink = new DiagnosticBag();
        var resolver = new NuGetResolver(sink);
        try
        {
            var dir = resolver.Resolve(deps);
            return dir is not null ? [dir] : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Resolves declared <c>(framework …)</c> shared-framework references (e.g.
    ///     <c>Microsoft.AspNetCore.App</c>) to their reference-assembly directories via
    ///     <see cref="FrameworkResolver" />, so <c>import-clr :from</c> hints that point at
    ///     framework assemblies (such as <c>Microsoft.Extensions.Hosting.Abstractions</c>)
    ///     can be resolved while editing. Mirrors what <c>PackageTester</c> does for real
    ///     compiles. Resolution diagnostics are discarded — the LSP is best-effort and should
    ///     not surface our own setup errors (e.g. a missing runtime) as user-facing diagnostics.
    /// </summary>
    private static List<string> ResolveFrameworkAssemblyPaths(
        IReadOnlyList<FrameworkDependency> frameworks
    )
    {
        if (frameworks.Count == 0)
            return [];

        var sink = new DiagnosticBag();
        try
        {
            return [.. FrameworkResolver.Resolve(frameworks, sink)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Appends <paramref name="additions" /> to <paramref name="target" />, skipping
    ///     entries already present (case-insensitive, treating values as file-system paths).
    ///     Mirrors <c>PackageOptionsBuilder.AddDistinct</c>, which is internal to the compiler.
    /// </summary>
    private static void AddDistinctPaths(List<string> target, IEnumerable<string> additions)
    {
        var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        foreach (var item in additions)
            if (seen.Add(item))
                target.Add(item);
    }

    private sealed record DiscoveredEnvironment(
        Dictionary<string, string> PackagePaths,
        Dictionary<string, string> ModuleAliases,
        List<string> ExtraSearchPaths,
        List<string> AssemblySearchPaths,
        List<NuGetDependency> NuGetDeps,
        List<FrameworkDependency> Frameworks
    );
}

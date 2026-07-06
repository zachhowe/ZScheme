using System.Collections.Concurrent;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.LanguageServer.Analysis;

public sealed class AnalysisService
{
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingAnalysis = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly WorkspaceIndex _index = new();
    private int _workspaceScanStarted;

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
    public void InitializeWorkspace(IEnumerable<string> roots)
    {
        if (Interlocked.Exchange(ref _workspaceScanStarted, 1) != 0)
            return;

        var rootList = roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rootList.Count == 0)
            return;

        _ = Task.Run(() => ScanWorkspace(rootList));
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

        var state = RunAnalysis(uri, source, version);
        _documents[uri] = state;
        return state;
    }

    public DocumentState AnalyzeImmediate(string uri, string source, int version)
    {
        var state = RunAnalysis(uri, source, version);
        _documents[uri] = state;
        return state;
    }

    public void RemoveDocument(string uri)
    {
        _documents.TryRemove(uri, out _);
        if (_pendingAnalysis.TryRemove(uri, out var cts))
            cts.Cancel();
    }

    private DocumentState RunAnalysis(string uri, string source, int version)
    {
        var fileName = UriToFilePath(uri);

        if (fileName.EndsWith(".zspkg", StringComparison.OrdinalIgnoreCase))
            return AnalyzeManifest(uri, source, version, fileName);

        var (program, diagnostics) = CompileFile(fileName, source);

        // Refresh this file's slice of the workspace index from the fresh AST — an open
        // editor buffer is always the freshest view of the file.
        if (program is not null)
            IndexFile(fileName, program);

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
    ///     active document uses), returning its typed AST and diagnostics. Shared by the
    ///     active-document path and background workspace indexing.
    /// </summary>
    private (AstNode.Program? Program, DiagnosticBag Diagnostics) CompileFile(
        string fileName,
        string source
    )
    {
        var env = DiscoverPackages(fileName);
        var assemblySearchPaths = ResolveNuGetAssemblyPaths(env.NuGetDeps);
        assemblySearchPaths.AddRange(ResolveFrameworkAssemblyPaths(env.Frameworks));
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
        return (compilation.TypedProgram, compilation.GetDiagnostics());
    }

    /// <summary>Harvests <paramref name="fileName" />'s top-level definitions and name
    ///     references into the workspace index.</summary>
    private void IndexFile(string fileName, AstNode.Program program)
    {
        var primaryModule = DerivePrimaryModuleName(fileName);
        var definitions = DefinitionCollector.Collect(program, primaryModule);
        var references = ReferenceCollector.Collect(program);
        _index.UpdateFile(fileName, definitions, references);
    }

    private void ScanWorkspace(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.zs", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var full = Path.GetFullPath(file);
                if (IsIndexExcluded(full) || _index.Contains(full))
                    continue;

                string text;
                try
                {
                    text = File.ReadAllText(full);
                }
                catch
                {
                    continue;
                }

                try
                {
                    var (program, _) = CompileFile(full, text);
                    if (program is not null)
                        IndexFile(full, program);
                }
                catch
                {
                    // Best-effort: a file that fails to compile in isolation is skipped.
                }
            }
        }
    }

    private static bool IsIndexExcluded(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase);
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
    ///     containing subdirectories with <c>package.zspkg</c> manifests. Returns the package
    ///     paths, module aliases (for <c>default-module</c>), extra search paths, and NuGet
    ///     dependencies needed to type-check the file. When the file lives inside a package's
    ///     declared test directory, that package's <c>test-dependencies</c> are also resolved
    ///     so unqualified imports like <c>(import zunit)</c> succeed.
    /// </summary>
    private static DiscoveredEnvironment DiscoverPackages(string filePath)
    {
        var paths = new Dictionary<string, string>();
        var aliases = new Dictionary<string, string>();
        var extraSearchPaths = new List<string>();
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

        if (isTestFile && ownerManifest is not null && ownerDir is not null)
            ApplyTestContext(
                ownerManifest,
                ownerDir,
                paths,
                aliases,
                extraSearchPaths,
                nuget,
                seenNuGet,
                frameworks,
                seenFramework
            );

        return new DiscoveredEnvironment(paths, aliases, extraSearchPaths, nuget, frameworks);
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

    private sealed record DiscoveredEnvironment(
        Dictionary<string, string> PackagePaths,
        Dictionary<string, string> ModuleAliases,
        List<string> ExtraSearchPaths,
        List<NuGetDependency> NuGetDeps,
        List<FrameworkDependency> Frameworks
    );
}

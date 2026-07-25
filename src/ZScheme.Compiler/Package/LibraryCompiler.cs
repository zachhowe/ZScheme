using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Package;

public sealed record LibraryCompilationResult(
    byte[] AssemblyBytes,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    IReadOnlyList<string> PrecompiledDependencyPaths
);

public sealed record LibraryCSharpResult(
    string CsOutput,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    IReadOnlyList<string> PrecompiledDependencyPaths
);

public sealed class LibraryCompiler(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LibraryCompiler>();

    private readonly HashSet<string> _precompiledAssemblyPaths = [];

    public LibraryCSharpResult? CompileToCSharp(
        string packageDir,
        PackageManifest manifest,
        CompilerOptions options
    )
    {
        var compiledModules = CompileModules(packageDir, manifest, options);
        if (compiledModules is null)
            return null;

        var (allIrDefs, clrNamespaces, precompiledAssemblyPaths) = BuildEmitInputs(compiledModules);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> emitRenames;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> typeEmitRenames;
        (allIrDefs, emitRenames, typeEmitRenames) = ResolveEmitNames(allIrDefs, compiledModules);
        compiledModules = ApplyEmittedNames(compiledModules, emitRenames, typeEmitRenames);

        var precompiledModuleMap = compiledModules
            .Values.Where(m => m.PrecompiledAssemblyPath is not null)
            .SelectMany(m =>
                m.ExportedNames.Select(name =>
                    (name, className: NameConverter.ClassNameFromModuleName(m.Name))
                )
            )
            .GroupBy(x => x.name)
            .ToDictionary(g => g.Key, g => g.First().className);

        var precompiledTypeRenames = BuildPrecompiledTypeRenames(compiledModules.Values);

        var emptyIr = new IrNode.Seq([]) { Type = ZType.Unit };
        var ns = manifest.Build.Main?.Namespace ?? options.Namespace;
        var aliasRegistry = BuildAliasRegistry(compiledModules);
        var emitter = new CSharpEmitter(
            diagnostics,
            ns,
            "LibraryInit",
            clrNamespaces,
            allIrDefs,
            precompiledModuleMap,
            false,
            false,
            aliasRegistry,
            precompiledTypeRenames: precompiledTypeRenames
        );
        var csOutput = emitter.Emit(emptyIr);

        if (diagnostics.HasErrors)
            return null;

        Log.Debug(
            "LibraryCompiler: emitted {Length} chars of C# for {ModuleCount} modules",
            csOutput.Length,
            compiledModules.Count
        );

        return new LibraryCSharpResult(csOutput, compiledModules, precompiledAssemblyPaths);
    }

    public LibraryCompilationResult? Compile(
        string packageDir,
        PackageManifest manifest,
        CompilerOptions options
    )
    {
        var librarySw = Stopwatch.StartNew();

        var compiledModules = CompileModules(packageDir, manifest, options);
        if (compiledModules is null)
            return null;

        var (allIrDefs, clrNamespaces, precompiledAssemblyPaths) = BuildEmitInputs(compiledModules);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> emitRenames;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> typeEmitRenames;
        (allIrDefs, emitRenames, typeEmitRenames) = ResolveEmitNames(allIrDefs, compiledModules);
        compiledModules = ApplyEmittedNames(compiledModules, emitRenames, typeEmitRenames);

        // Use IL emitter with an empty main program, putting all module code as imported modules
        var assemblyName = manifest.Name;
        var emptyIr = new IrNode.Seq([]) { Type = ZType.Unit };
        var aliasRegistry = BuildAliasRegistry(compiledModules);
        var precompiledTypeRenames = BuildPrecompiledTypeRenames(compiledModules.Values);
        var emitter = new IlEmitter(
            assemblyName,
            diagnostics,
            "LibraryInit",
            clrNamespaces,
            options.AssemblySearchPaths,
            allIrDefs,
            precompiledAssemblyPaths,
            manifest.Build.Main?.Namespace,
            typeAliases: aliasRegistry,
            precompiledTypeRenames: precompiledTypeRenames
        );
        var bytes = emitter.Emit(emptyIr);
        if (bytes is null || diagnostics.HasErrors)
            return null;

        Log.Debug(
            "LibraryCompiler: emitted {ByteCount} bytes for {ModuleCount} modules in {ElapsedMs}ms",
            bytes.Length,
            compiledModules.Count,
            librarySw.ElapsedMilliseconds
        );

        return new LibraryCompilationResult(bytes, compiledModules, precompiledAssemblyPaths);
    }

    /// <summary>
    ///     Walks every compiled module's IR, collecting <see cref="IrNode.TypeAliasDecl" />
    ///     entries into a fresh <see cref="TypeAliasRegistry" /> so the package emitter has
    ///     alias-aware type mapping when emitting cross-module CLR signatures.
    /// </summary>
    private static TypeAliasRegistry BuildAliasRegistry(
        IReadOnlyDictionary<string, CompiledModule> compiledModules
    )
    {
        var reg = new TypeAliasRegistry();
        foreach (var (_, mod) in compiledModules)
        {
            var defs = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
            foreach (var def in defs)
                CollectAliases(def, reg);
        }

        return reg;
    }

    private static void CollectAliases(IrNode node, TypeAliasRegistry reg)
    {
        switch (node)
        {
            case IrNode.Seq seq:
                foreach (var child in seq.Nodes)
                    CollectAliases(child, reg);
                break;
            case IrNode.Let let:
                CollectAliases(let.Body, reg);
                break;
            case IrNode.Use use:
                CollectAliases(use.Body, reg);
                break;
            case IrNode.TypeAliasDecl alias:
                reg.TryAdd(
                    new TypeAliasInfo(
                        alias.Name,
                        alias.TypeParams,
                        alias.ClrTarget,
                        alias.AssemblyHint,
                        alias.IsArray ? TypeAliasKind.SzArray : TypeAliasKind.GenericClrType,
                        SourceSpan.None
                    ),
                    out _
                );
                break;
        }
    }

    /// <summary>
    ///     Runs <see cref="EmitNameResolver" /> over the package's module definitions so
    ///     colliding emitted identifiers are disambiguated identically for both backends.
    ///     Returns the rewritten definitions and the per-module-class rename map (used to
    ///     persist exported renames into module metadata).
    /// </summary>
    private (
        List<(string ClassName, IReadOnlyList<IrNode> Definitions)> Defs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Renames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeRenames
    ) ResolveEmitNames(
        List<(string ClassName, IReadOnlyList<IrNode> Definitions)> allIrDefs,
        IReadOnlyDictionary<string, CompiledModule> compiledModules
    )
    {
        var precompiledRenames = compiledModules
            .Values.Where(m => m.PrecompiledAssemblyPath is not null && m.EmittedNames is not null)
            .GroupBy(m => m.Name)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g.First().EmittedNames!
            );
        var emptyIr = new IrNode.Seq([]) { Type = ZType.Unit };
        var resolved = EmitNameResolver.Resolve(
            "LibraryInit",
            emptyIr,
            allIrDefs,
            precompiledRenames
        );
        var defs = resolved.ImportedModules.Select(m => (m.ClassName, m.Definitions)).ToList();
        return (defs, resolved.ModuleRenames, resolved.ModuleTypeRenames);
    }

    /// Attaches each module's exported-symbol renames (keyed by module-class name in
    /// <paramref name="renames" />) to its <see cref="CompiledModule.EmittedNames" /> so
    /// they are persisted into the module's metadata and a consumer can reference the
    /// precompiled symbol by the name actually baked into the DLL. Only exported renames
    /// are kept — internal helpers are never referenced across the boundary.
    private static Dictionary<string, CompiledModule> ApplyEmittedNames(
        IReadOnlyDictionary<string, CompiledModule> compiledModules,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> renames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> typeRenames
    )
    {
        var result = new Dictionary<string, CompiledModule>(compiledModules.Count);
        foreach (var (name, mod) in compiledModules)
        {
            var className = NameConverter.ClassNameFromModuleName(name);
            var updated = mod;

            if (
                renames.TryGetValue(className, out var modRenames)
                && modRenames
                    .Where(kv => mod.ExportedNames.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
                    is { Count: > 0 } exported
            )
                updated = updated with { EmittedNames = exported };

            // Type renames are filtered by the same exported-name set that builds the
            // consumer's precompiledModuleMap, so exactly the cross-module-referenceable
            // renamed types are persisted.
            if (
                typeRenames.TryGetValue(className, out var modTypeRenames)
                && modTypeRenames
                    .Where(kv => mod.ExportedNames.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
                    is { Count: > 0 } exportedTypes
            )
                updated = updated with { TypeEmittedNames = exportedTypes };

            result[name] = updated;
        }

        return result;
    }

    // Flattens the type renames exported by consumed precompiled modules into a single
    // rawTypeName -> emittedName map for the emitters (last writer wins, matching the global
    // keying of precompiledModuleMap). Empty when no consumed module renamed a type.
    private static Dictionary<string, string> BuildPrecompiledTypeRenames(
        IEnumerable<CompiledModule> compiledModules
    )
    {
        var map = new Dictionary<string, string>();
        foreach (var m in compiledModules)
            if (m.PrecompiledAssemblyPath is not null && m.TypeEmittedNames is { } te)
                foreach (var (raw, emitted) in te)
                    map[raw] = emitted; // last writer wins
        return map;
    }

    private (
        List<(string ClassName, IReadOnlyList<IrNode> Definitions)> AllIrDefs,
        List<string> ClrNamespaces,
        List<string> PrecompiledAssemblyPaths
    ) BuildEmitInputs(IReadOnlyDictionary<string, CompiledModule> compiledModules)
    {
        var allIrDefs = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>();
        foreach (var (name, mod) in compiledModules)
        {
            var defs = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
            if (defs.Count > 0)
                allIrDefs.Add((NameConverter.ClassNameFromModuleName(name), defs));
        }

        var clrNamespaces = compiledModules
            .Values.SelectMany(m => m.ExportedClrNamespaces)
            .Distinct()
            .ToList();

        var precompiledAssemblyPaths = _precompiledAssemblyPaths.ToList();
        return (allIrDefs, clrNamespaces, precompiledAssemblyPaths);
    }

    private Dictionary<string, CompiledModule>? CompileModules(
        string packageDir,
        PackageManifest manifest,
        CompilerOptions options
    )
    {
        // Discover .zs files: use sources.main subdir if specified, else package root
        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(packageDir, manifest.Sources.Main))
            : packageDir;
        var zsFiles = Directory.GetFiles(sourceDir, "*.zs", SearchOption.AllDirectories);
        if (zsFiles.Length == 0)
        {
            diagnostics.Error(
                $"No .zs files found in source directory: {sourceDir}",
                SourceSpan.None
            );
            return null;
        }

        Log.Debug(
            "LibraryCompiler: {FileCount} .zs files in {SourceDir}",
            zsFiles.Length,
            sourceDir
        );

        // Build module name → source mapping
        // If the package has an import-prefix, qualify module names (e.g., "option" → "stdlib/option")
        var packagePrefix = manifest.ImportPrefix;
        var moduleSources = new Dictionary<string, (string Path, string Source)>();

        // A bare intra-package import ("helper") and its prefixed spelling ("mypkg/helper") name
        // the same file. Alias the bare form to the prefixed one so both the dependency graph
        // below and each module's sub-compilation settle on a single name; left unaliased, the
        // bare form falls through to the resolver's search paths, finds that same file, and
        // compiles it a second time under a second name.
        var localAliases = new Dictionary<string, string>();
        foreach (var file in zsFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var modulePart = Path.ChangeExtension(relativePath, null)
                .Replace(Path.DirectorySeparatorChar, '/');
            var qualifiedName = packagePrefix is not null
                ? $"{packagePrefix}/{modulePart}"
                : modulePart;
            moduleSources[qualifiedName] = (file, File.ReadAllText(file));
            if (packagePrefix is not null)
                localAliases[modulePart] = qualifiedName;
        }

        // Caller-supplied aliases name this package's *dependencies*, and already win over the
        // search paths inside a sub-compilation; keep them winning over the local ones too.
        var moduleAliases = new Dictionary<string, string>(localAliases);
        foreach (var (alias, qualified) in options.ModuleAliases)
            moduleAliases[alias] = qualified;

        // Build dependency graph across all modules
        var graph = new ModuleGraph(diagnostics);
        var resolver = new ModuleResolver(diagnostics);
        resolver.AddSearchPath(sourceDir);

        // Register the source dir as a package path for this package's prefix
        if (packagePrefix is not null)
            resolver.AddPackagePath(packagePrefix, sourceDir);
        foreach (var (name, path) in options.PackagePaths)
        {
            resolver.AddPackagePath(name, path);
            if (name == "stdlib")
                resolver.AddSearchPath(path);
        }

        foreach (var (alias, qualified) in moduleAliases)
            resolver.AddModuleAlias(alias, qualified);

        foreach (var (moduleName, (path, source)) in moduleSources)
        {
            graph.AddModule(moduleName);
            ScanDependencies(moduleName, source, path, graph, resolver, moduleSources);
        }

        if (diagnostics.HasErrors)
            return null;

        var order = graph.TopologicalSort();
        if (order is null)
            return null;

        if (order.Count > 0)
            Log.Debug("LibraryCompiler: module order: {Order}", string.Join(" -> ", order));

        // Compile modules in topological order
        var compiledModules = new Dictionary<string, CompiledModule>();
        var compilingModules = new HashSet<string>();
        var failedModules = new HashSet<string>();

        foreach (var moduleName in order)
        {
            if (!moduleSources.ContainsKey(moduleName))
                continue; // External dependency, skip

            var compiled = CompileModule(
                moduleName,
                moduleSources,
                compiledModules,
                compilingModules,
                failedModules,
                resolver,
                options,
                moduleAliases,
                sourceDir,
                packagePrefix
            );
            if (compiled is null)
                return null;

            // Record the package's build namespace so consuming projects can emit
            // fully-qualified references to this module's generated class. Also keep it
            // in ExportedClrNamespaces (existing behavior relied on by other consumers).
            if (manifest.Build.Main?.Namespace is { } ns)
            {
                compiled = compiled with { BuildNamespace = ns };
                if (!compiled.ExportedClrNamespaces.Contains(ns))
                    compiled = compiled with
                    {
                        ExportedClrNamespaces = compiled.ExportedClrNamespaces.Append(ns).ToList(),
                    };
            }

            compiledModules[moduleName] = compiled;
        }

        if (diagnostics.HasErrors)
            return null;

        return compiledModules;
    }

    private CompiledModule? CompileModule(
        string moduleName,
        Dictionary<string, (string Path, string Source)> moduleSources,
        Dictionary<string, CompiledModule> compiledModules,
        HashSet<string> compilingModules,
        HashSet<string> failedModules,
        ModuleResolver resolver,
        CompilerOptions options,
        IReadOnlyDictionary<string, string> moduleAliases,
        string sourceDir,
        string? packagePrefix
    )
    {
        if (compiledModules.TryGetValue(moduleName, out var cached))
        {
            Log.Debug(
                "LibraryCompiler: module {ModuleName} already compiled (cache hit)",
                moduleName
            );
            return cached;
        }

        if (failedModules.Contains(moduleName))
        {
            Log.Debug(
                "LibraryCompiler: module {ModuleName} previously failed, short-circuiting",
                moduleName
            );
            return null;
        }

        if (!compilingModules.Add(moduleName))
        {
            diagnostics.Error(
                $"Circular module dependency involving '{moduleName}'",
                SourceSpan.None
            );
            return null;
        }

        CompiledModule? Fail()
        {
            failedModules.Add(moduleName);
            return null;
        }

        try
        {
            if (!moduleSources.TryGetValue(moduleName, out var entry))
            {
                diagnostics.Error($"Module '{moduleName}' not found in package", SourceSpan.None);
                return Fail();
            }

            var (filePath, source) = entry;
            var moduleSw = Stopwatch.StartNew();
            Log.Debug(
                "LibraryCompiler: compiling module {ModuleName} from {FilePath}",
                moduleName,
                filePath
            );

            // Keep this package's own prefix for intra-package resolution, plus all
            // external dependency package paths (e.g. stdlib) so CompileModule can
            // resolve imports from packages that haven't been precompiled yet.
            var subPackagePathsForCompile = new Dictionary<string, string>();
            if (packagePrefix is not null)
                subPackagePathsForCompile[packagePrefix] = sourceDir;
            foreach (var (name, path) in options.PackagePaths)
                if (name != packagePrefix)
                    subPackagePathsForCompile[name] = path;

            var subOptions = new CompilerOptions
            {
                AssemblySearchPaths = options.AssemblySearchPaths,
                PackagePaths = subPackagePathsForCompile,
                ModuleAliases = new Dictionary<string, string>(moduleAliases),
                PrecompiledPackagePaths = options.PrecompiledPackagePaths,
                PrimaryModuleName = moduleName,
            };
            var compilation = new Compilation(subOptions);

            // Inject already-compiled sibling modules
            foreach (var (depName, depMod) in compiledModules)
                compilation.InjectModule(depName, depMod);
            Log.Debug(
                "LibraryCompiler: injected {DepCount} compiled dependencies into {ModuleName}",
                compiledModules.Count,
                moduleName
            );

            var compResult = compilation.CompileAsModule(moduleName, source, filePath);

            // Collect precompiled assembly paths from dependencies (e.g. stdlib)
            foreach (var path in compilation.GetPrecompiledAssemblyPaths())
                _precompiledAssemblyPaths.Add(path);

            // Extract transitive dependencies cached by the sub-compilation so they are
            // included in the package assembly (their IR definitions must be available
            // when the IL emitter resolves types referenced across module boundaries).
            foreach (var (depName, depMod) in compilation.GetCachedModules())
                if (depName != moduleName && !compiledModules.ContainsKey(depName))
                    compiledModules[depName] = depMod;

            Log.Debug(
                "LibraryCompiler: module {ModuleName} compiled in {ElapsedMs}ms, success={Success}",
                moduleName,
                moduleSw.ElapsedMilliseconds,
                compResult is not null
            );
            if (compResult is null)
            {
                diagnostics.AddRange(compilation.GetDiagnostics());
                return Fail();
            }

            return compResult;
        }
        finally
        {
            compilingModules.Remove(moduleName);
        }
    }

    private static void ScanDependencies(
        string moduleName,
        string source,
        string filePath,
        ModuleGraph graph,
        ModuleResolver resolver,
        Dictionary<string, (string Path, string Source)> localModules,
        HashSet<string>? scanned = null
    )
    {
        scanned ??= [];
        if (!scanned.Add(moduleName))
            return;

        Log.Debug("LibraryCompiler: scanning dependencies for {ModuleName}", moduleName);

        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, diag);
        var tokens = lexer.Tokenize();
        if (diag.HasErrors)
            return;

        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        if (diag.HasErrors)
            return;

        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        foreach (
            var import in program
                .TopLevelForms.SelectMany(f =>
                    f is AstNode.ModuleDecl m ? new[] { f }.Concat(m.Body) : [f]
                )
                .OfType<AstNode.Import>()
        )
        {
            // Canonicalize through the alias table before the name reaches the graph, so a
            // sibling imported bare ("helper") and one imported prefixed ("mypkg/helper") land on
            // one node. Two spellings are two nodes, which compiles the file twice and registers
            // every name it exports twice in the overload set.
            var depName = resolver.ResolveAlias(import.ModuleName);

            // Only track intra-package dependencies; the sub-compilation resolves the rest.
            if (!localModules.TryGetValue(depName, out var depEntry))
                continue;

            Log.Debug("LibraryCompiler: {ModuleName} depends on {Dependency}", moduleName, depName);
            graph.AddModule(depName);
            graph.AddDependency(moduleName, depName, import.Span);

            ScanDependencies(
                depName,
                depEntry.Source,
                depEntry.Path,
                graph,
                resolver,
                localModules,
                scanned
            );
        }
    }
}

using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Package;

public sealed record LibraryCompilationResult(
    byte[] AssemblyBytes,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    IReadOnlyList<string> PrecompiledDependencyPaths
);

/// <summary>
///     One module's emitted C# as its own source file, ready to write into a generated
///     project. <see cref="RelativePath" /> mirrors the module's place in the source
///     tree (see <see cref="LibraryCompiler.RelativePathForModule" />), so the generated
///     project reads like the package it came from.
/// </summary>
public sealed record LibraryCsFile(string RelativePath, string Source);

public sealed record LibraryCSharpResult(
    /// <summary>
    ///     Every module in one file. <see cref="Files" /> is the same emission split per
    ///     module; the two are not substring-related, because each file repeats the header.
    /// </summary>
    string CsOutput,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    IReadOnlyList<string> PrecompiledDependencyPaths,
    /// <summary>See <see cref="CSharpEmitter.ClrTypeAssemblies" />.</summary>
    IReadOnlyDictionary<string, string> ClrTypeAssemblies,
    /// <summary>One file per module class. See <see cref="CsOutput" />.</summary>
    IReadOnlyList<LibraryCsFile> Files
);

public sealed class LibraryCompiler(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LibraryCompiler>();

    private readonly HashSet<string> _precompiledAssemblyPaths = [];

    /// <summary>Cached for <see cref="IsUsableAsPath" />, which runs per module.</summary>
    private static readonly char[] InvalidPathChars = Path.GetInvalidFileNameChars();

    /// <summary>
    ///     Union of every per-module sub-compilation's alias registry. Each sub-compilation
    ///     starts from the built-ins (<c>Task</c>, <c>Seq</c>, <c>Clr-Array</c>, <c>ValueTuple</c>)
    ///     and seeds the prelude's aliases (notably <c>Mutable-Vector</c>, which backs variadic
    ///     rest parameters), none of which appear as <see cref="IrNode.TypeAliasDecl" /> in the
    ///     package's own IR. Emitting from a registry built only out of that IR loses them and
    ///     the backends fall back to spelling the raw ZScheme name into the output.
    /// </summary>
    private readonly TypeAliasRegistry _packageAliases = new();

    /// <param name="externalModules">
    ///     Modules another project in the same solution emits, injected so this package's sources
    ///     compile against them without a copy of their code landing here. Each carries the build
    ///     namespace of the project that owns it, which is what references to it are qualified
    ///     with. This is the same mechanism the test project uses to reference the main one.
    /// </param>
    public LibraryCSharpResult? CompileToCSharp(
        string packageDir,
        PackageManifest manifest,
        CompilerOptions options,
        IReadOnlyDictionary<string, CompiledModule>? externalModules = null
    )
    {
        var compiledModules = CompileModules(packageDir, manifest, options, externalModules);
        if (compiledModules is null)
            return null;

        var (allIrDefs, clrNamespaces, precompiledAssemblyPaths) = BuildEmitInputs(compiledModules);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> emitRenames;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> typeEmitRenames;
        (allIrDefs, emitRenames, typeEmitRenames) = ResolveEmitNames(allIrDefs, compiledModules);
        compiledModules = ApplyEmittedNames(compiledModules, emitRenames, typeEmitRenames);

        // IsExternallyEmitted, not PrecompiledAssemblyPath: a module emitted by a sibling project
        // has no assembly to point at yet, but references to it must be qualified exactly the same
        // way as ones into a prebuilt package.
        var precompiledModuleMap = compiledModules
            .Values.Where(m => m.IsExternallyEmitted)
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
        var externalModuleInfos = compiledModules
            .Values.Where(m => m.EmitAsExternalReference)
            .Select(m =>
            {
                var className = NameConverter.ClassNameFromModuleName(m.Name);
                return new ExternalModuleInfo(
                    className,
                    m.BuildNamespace is { Length: > 0 } bns ? $"{bns}.{className}" : className,
                    m.AllIrDefinitions ?? m.ExportedIrDefinitions
                );
            })
            .ToList();

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
            precompiledModuleNamespaces: compiledModules
                .Values.Where(m => m.IsExternallyEmitted && m.BuildNamespace is { Length: > 0 })
                .ToDictionary(m => m.Name, m => m.BuildNamespace!),
            precompiledTypeRenames: precompiledTypeRenames,
            externalModules: externalModuleInfos
        );
        var emitted = emitter.EmitUnits(emptyIr);
        var csOutput = emitted.ToSingleFile();

        if (diagnostics.HasErrors)
            return null;

        var files = SplitIntoFiles(emitted, ModuleNamesByClass(compiledModules), manifest);

        Log.Debug(
            "LibraryCompiler: emitted {Length} chars of C# across {FileCount} files for {ModuleCount} modules",
            csOutput.Length,
            files.Count,
            compiledModules.Count
        );

        return new LibraryCSharpResult(
            csOutput,
            compiledModules,
            precompiledAssemblyPaths,
            emitter.ClrTypeAssemblies,
            files
        );
    }

    /// <summary>
    ///     Turns one emission into a source file per module class. The main unit is dropped:
    ///     a package library emits with an empty main IR, so it never has content.
    /// </summary>
    /// <param name="moduleNamesByClass">See <see cref="ModuleNamesByClass" />.</param>
    private static List<LibraryCsFile> SplitIntoFiles(
        CSharpEmitUnits emitted,
        IReadOnlyDictionary<string, string> moduleNamesByClass,
        PackageManifest manifest
    )
    {
        var files = new List<LibraryCsFile>(emitted.Units.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in emitted.Units)
        {
            if (unit.ModuleClassName is not { } className || string.IsNullOrWhiteSpace(unit.Body))
                continue;

            // Every unit's class came from a compiled module, so the lookup cannot miss.
            var moduleName = moduleNamesByClass[className];
            files.Add(
                new LibraryCsFile(
                    RelativePathForModule(moduleName, className, manifest.ImportPrefix, taken),
                    emitted.ToFile(unit)
                )
            );
        }

        return files;
    }

    /// <summary>
    ///     The path a module's emitted C# is written to, relative to the project directory.
    ///     Mirrors the source tree: the package's own <c>import-prefix</c> is stripped, so
    ///     <c>stdlib/mutable/vector</c> becomes <c>mutable/vector.cs</c> in stdlib's own
    ///     project, while a dependency inlined from source keeps its prefix as a folder
    ///     (<c>stdlib/list.cs</c> inside the http project).
    /// </summary>
    /// <param name="taken">
    ///     Paths already claimed, compared case-insensitively so two modules differing only
    ///     in case do not collide on a case-insensitive filesystem. Added to as it goes.
    /// </param>
    internal static string RelativePathForModule(
        string moduleName,
        string className,
        string? importPrefix,
        HashSet<string> taken
    )
    {
        var path = moduleName;
        if (
            !string.IsNullOrEmpty(importPrefix)
            && path.StartsWith(importPrefix + '/', StringComparison.Ordinal)
        )
            path = path[(importPrefix.Length + 1)..];

        var candidate = IsUsableAsPath(path) ? path + ".cs" : className + ".cs";
        if (taken.Add(candidate))
            return candidate;

        // A module name that already collides on its class name is a duplicate-definition
        // error in the emitted C# regardless of how the files are laid out; the suffix is
        // here so an injected or aliased module with an exotic name cannot silently
        // overwrite another module's file.
        candidate = className + ".cs";
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{className}_{n}.cs";
        return candidate;
    }

    /// <summary>
    ///     Whether a module name is safe to use as a relative file path: no escaping the
    ///     project directory, nothing the filesystem rejects, and not under a <c>bin</c> or
    ///     <c>obj</c> root or a dot-prefixed directory. The generated csproj names every
    ///     source explicitly, so the SDK's <c>DefaultItemExcludes</c> (which cover exactly
    ///     those paths) no longer decide what compiles; the fallback stays so a module's
    ///     file never sits inside the project's own build output or a hidden directory.
    /// </summary>
    private static bool IsUsableAsPath(string moduleName)
    {
        if (moduleName.Length == 0 || Path.IsPathRooted(moduleName))
            return false;

        var segments = moduleName.Split('/');
        if (
            segments[0].Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("obj", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        return segments.All(s => s.Length > 0 && s[0] != '.' && s.IndexOfAny(InvalidPathChars) < 0);
    }

    /// <summary>
    ///     Compiles a library package from its manifest path, resolving the manifest's
    ///     ZScheme dependency closure, shared frameworks, and <c>(ref ...)</c> paths first.
    ///     Prefer this over <see cref="Compile(string, PackageManifest, CompilerOptions)" />,
    ///     which expects <see cref="CompilerOptions.PackagePaths" /> to already be populated
    ///     — without it the package's own modules compile but every prelude module fails to
    ///     resolve.
    /// </summary>
    /// <param name="overrides">
    ///     Caller preferences. Its search paths are probed ahead of anything the manifest
    ///     implies, so an in-process host's live output directory wins over a manifest
    ///     <c>(ref ...)</c> naming a possibly-stale build directory.
    /// </param>
    /// <param name="resolveNuGetDependencies">
    ///     See <see cref="PackageOptionsBuilder.Resolve" />. In-process hosts should pass
    ///     <c>false</c>.
    /// </param>
    public LibraryCompilationResult? CompileFromManifest(
        string manifestPath,
        CompilerOptions? overrides = null,
        bool resolveNuGetDependencies = true
    )
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            diagnostics.Error($"Manifest not found: {fullPath}", SourceSpan.None);
            return null;
        }

        var packageDir = Path.GetDirectoryName(fullPath)!;
        var manifest = new ManifestParser(diagnostics).Parse(File.ReadAllText(fullPath), fullPath);
        if (manifest is null || diagnostics.HasErrors)
            return null;

        var options = PackageOptionsBuilder.BuildForPackage(
            packageDir,
            manifest,
            diagnostics,
            overrides,
            resolveNuGetDependencies
        );
        if (options is null)
            return null;

        return Compile(packageDir, manifest, options);
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
    ///     Builds the registry the package emitter uses for alias-aware type mapping: the
    ///     aliases every sub-compilation saw (built-ins + prelude, see
    ///     <see cref="_packageAliases" />), plus every <see cref="IrNode.TypeAliasDecl" /> in
    ///     the package's own module IR — which also covers modules injected by a caller rather
    ///     than compiled here.
    /// </summary>
    private TypeAliasRegistry BuildAliasRegistry(
        IReadOnlyDictionary<string, CompiledModule> compiledModules
    )
    {
        var reg = new TypeAliasRegistry();
        reg.MergeFrom(_packageAliases);
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
            .Values.Where(m => m.IsExternallyEmitted && m.EmittedNames is not null)
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
            if (m.IsExternallyEmitted && m.TypeEmittedNames is { } te)
                foreach (var (raw, emitted) in te)
                    map[raw] = emitted; // last writer wins
        return map;
    }

    /// <summary>
    ///     The module name behind each emitted class name, for laying the C# project out
    ///     as one file per module. Built forward from the module names, never by reversing
    ///     <see cref="NameConverter.ClassNameFromModuleName" /> — that mapping is lossy (each
    ///     segment is capitalised, '-' disappears, '/' becomes '_'), so <c>base/mod</c> and
    ///     <c>base/Mod</c>, or <c>base-mod</c> and <c>baseMod</c>, share a class name and
    ///     neither can be recovered from it; the first module keeps the entry.
    /// </summary>
    private static Dictionary<string, string> ModuleNamesByClass(
        IReadOnlyDictionary<string, CompiledModule> compiledModules
    )
    {
        var moduleNamesByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in compiledModules.Keys)
            moduleNamesByClass.TryAdd(NameConverter.ClassNameFromModuleName(name), name);
        return moduleNamesByClass;
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
            // A module that lives in a referenced assembly is not emitted again here. Its
            // metadata still carries type declarations — that is how a consumer type-checks
            // against it — and emitting those would redeclare Option and friends in this
            // assembly, beside the reference to the ones that already exist.
            if (mod.IsExternallyEmitted)
                continue;

            var className = NameConverter.ClassNameFromModuleName(name);
            var defs = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
            if (defs.Count > 0)
                allIrDefs.Add((className, defs));
        }

        // Only inlined modules contribute using directives; a referenced module's members are
        // reached through its build namespace instead, and pulling its CLR namespaces in here
        // is what would make an ambiguous short type name resolve differently than it did when
        // that module was built.
        var clrNamespaces = compiledModules
            .Values.Where(m => !m.IsExternallyEmitted)
            .SelectMany(m => m.ExportedClrNamespaces)
            .Distinct()
            .ToList();

        var precompiledAssemblyPaths = _precompiledAssemblyPaths.ToList();

        // Always-on runtime support assembly, riding the precompiled list exactly as in
        // Compilation.CompileEmit: it is not a compiled module, but emitted code references
        // its ZSymbol type, and consumers of this list are the ones that emit a <Reference>
        // or copy the DLL next to the output.
        var runtimeAssemblyPath = typeof(Runtime.ZSymbol).Assembly.Location;
        if (
            !string.IsNullOrEmpty(runtimeAssemblyPath)
            && !precompiledAssemblyPaths.Contains(runtimeAssemblyPath)
        )
            precompiledAssemblyPaths.Add(runtimeAssemblyPath);

        return (allIrDefs, clrNamespaces, precompiledAssemblyPaths);
    }

    private Dictionary<string, CompiledModule>? CompileModules(
        string packageDir,
        PackageManifest manifest,
        CompilerOptions options,
        IReadOnlyDictionary<string, CompiledModule>? externalModules = null
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

        // Compile modules in topological order. Modules another project emits are seeded in
        // first, so every sub-compilation below is handed them the way it is handed an
        // already-compiled sibling — they are injected, never compiled, and BuildEmitInputs
        // leaves them out of what this project emits.
        var compiledModules = new Dictionary<string, CompiledModule>();
        if (externalModules is not null)
            foreach (var (name, mod) in externalModules)
                compiledModules[name] = mod;
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
                // Carried through so a caller that disables the prelude actually gets it
                // disabled. Without these two, the sub-compilation silently reverted to the
                // defaults and the option looked ignored.
                DisablePrelude = options.DisablePrelude,
                PreludeModules = options.PreludeModules,
                // Same reason: the module path now runs the ZS0005 analyzer, so the manifest's
                // (warn-unlooped-recursion "false") has to reach the sub-compilation to mean
                // anything.
                WarnUnloopedRecursion = options.WarnUnloopedRecursion,
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

            // Fold this module's alias view (built-ins + prelude + its own declarations) into
            // the package-wide registry the final emit uses.
            _packageAliases.MergeFrom(compilation.TypeAliases);

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
            // Carry the sub-compilation's diagnostics out on success too, not just on failure:
            // a module that compiles cleanly can still have produced warnings (ZS0005, an export
            // that names nothing), and dropping them is what made package builds silent.
            diagnostics.AddRange(compilation.GetDiagnostics());
            if (compResult is null)
                return Fail();

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

        foreach (var (importName, importSpan) in ImportScanner.Scan(source, filePath))
        {
            // Canonicalize through the alias table before the name reaches the graph, so a
            // sibling imported bare ("helper") and one imported prefixed ("mypkg/helper") land on
            // one node. Two spellings are two nodes, which compiles the file twice and registers
            // every name it exports twice in the overload set.
            var depName = resolver.ResolveAlias(importName);

            // Only track intra-package dependencies; the sub-compilation resolves the rest.
            if (!localModules.TryGetValue(depName, out var depEntry))
                continue;

            Log.Debug("LibraryCompiler: {ModuleName} depends on {Dependency}", moduleName, depName);
            graph.AddModule(depName);
            graph.AddDependency(moduleName, depName, importSpan);

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

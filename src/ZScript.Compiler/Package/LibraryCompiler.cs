using System.Diagnostics;
using Serilog;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Pipeline;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Package;

public sealed record LibraryCompilationResult(
    byte[] AssemblyBytes,
    IReadOnlyDictionary<string, CompiledModule> Modules);

public sealed class LibraryCompiler(DiagnosticBag diagnostics)
{
    public LibraryCompilationResult? Compile(
        string packageDir, PackageManifest manifest, CompilerOptions options)
    {
        var librarySw = Stopwatch.StartNew();

        // Discover .zs files: use sources.main subdir if specified, else package root
        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(packageDir, manifest.Sources.Main))
            : packageDir;
        var zsFiles = Directory.GetFiles(sourceDir, "*.zs", SearchOption.TopDirectoryOnly);
        if (zsFiles.Length == 0)
        {
            diagnostics.Error($"No .zs files found in source directory: {sourceDir}", SourceSpan.None);
            return null;
        }

        Log.Debug("LibraryCompiler: {FileCount} .zs files in {SourceDir}", zsFiles.Length, sourceDir);

        // Build module name → source mapping
        // If the package has an import-prefix, qualify module names (e.g., "option" → "stdlib/option")
        var packagePrefix = manifest.ImportPrefix;
        var moduleSources = new Dictionary<string, (string Path, string Source)>();
        foreach (var file in zsFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var qualifiedName = packagePrefix is not null ? $"{packagePrefix}/{fileName}" : fileName;
            moduleSources[qualifiedName] = (file, File.ReadAllText(file));
        }

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

        foreach (var moduleName in order)
        {
            if (!moduleSources.ContainsKey(moduleName))
                continue; // External dependency, skip

            var compiled = CompileModule(moduleName, moduleSources, compiledModules,
                compilingModules, resolver, options, sourceDir, packagePrefix);
            if (compiled is null)
                return null;

            // Add the package namespace to the module's CLR namespaces so consuming
            // projects can resolve precompiled module class references
            if (manifest.Build.Namespace is { } ns
                && !compiled.ExportedClrNamespaces.Contains(ns))
                compiled = compiled with
                {
                    ExportedClrNamespaces = compiled.ExportedClrNamespaces.Append(ns).ToList()
                };

            compiledModules[moduleName] = compiled;
        }

        if (diagnostics.HasErrors)
            return null;

        // Emit all modules into one assembly using IlEmitter
        var assemblyName = manifest.Name;
        var allIrDefs = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>();
        foreach (var (name, mod) in compiledModules)
            if (mod.ExportedIrDefinitions.Count > 0)
                allIrDefs.Add((ModuleNameToClassName(name), mod.ExportedIrDefinitions));

        // Collect all CLR namespaces
        var clrNamespaces = compiledModules.Values
            .SelectMany(m => m.ExportedClrNamespaces)
            .Distinct()
            .ToList();

        // Use IlEmitter with an empty main program, putting all module code as imported modules
        var emitter = new IlEmitter(assemblyName, diagnostics, "LibraryInit",
            clrNamespaces, options.AssemblySearchPaths, allIrDefs,
            ilNamespace: manifest.Build.Namespace);
        var emptyIr = new IrNode.Seq([]) { Type = ZType.Unit };
        var bytes = emitter.Emit(emptyIr);
        if (bytes is null || diagnostics.HasErrors)
            return null;

        Log.Debug("LibraryCompiler: emitted {ByteCount} bytes for {ModuleCount} modules in {ElapsedMs}ms",
            bytes.Length, compiledModules.Count, librarySw.ElapsedMilliseconds);

        return new LibraryCompilationResult(bytes, compiledModules);
    }

    private CompiledModule? CompileModule(string moduleName,
        Dictionary<string, (string Path, string Source)> moduleSources,
        Dictionary<string, CompiledModule> compiledModules,
        HashSet<string> compilingModules,
        ModuleResolver resolver,
        CompilerOptions options,
        string sourceDir,
        string? packagePrefix)
    {
        if (compiledModules.TryGetValue(moduleName, out var cached))
        {
            Log.Debug("LibraryCompiler: module {ModuleName} already compiled (cache hit)", moduleName);
            return cached;
        }

        if (!compilingModules.Add(moduleName))
        {
            diagnostics.Error($"Circular module dependency involving '{moduleName}'", SourceSpan.None);
            return null;
        }

        if (!moduleSources.TryGetValue(moduleName, out var entry))
        {
            diagnostics.Error($"Module '{moduleName}' not found in package", SourceSpan.None);
            return null;
        }

        var (filePath, source) = entry;
        var moduleSw = Stopwatch.StartNew();
        Log.Debug("LibraryCompiler: compiling module {ModuleName} from {FilePath}", moduleName, filePath);

        // Use a sub-compilation to compile this module
        var subPackagePaths = new Dictionary<string, string>(options.PackagePaths);
        if (packagePrefix is not null)
            subPackagePaths.TryAdd(packagePrefix, sourceDir);
        var subOptions = new CompilerOptions
        {
            DisablePrelude = true,
            AssemblySearchPaths = options.AssemblySearchPaths,
            PackagePaths = subPackagePaths,
            ModuleAliases = new Dictionary<string, string>(options.ModuleAliases),
            UsePackageCache = false
        };
        var compilation = new Compilation(subOptions);

        // Inject already-compiled sibling modules
        foreach (var (depName, depMod) in compiledModules)
            compilation.InjectModule(depName, depMod);
        Log.Debug("LibraryCompiler: injected {DepCount} compiled dependencies into {ModuleName}", compiledModules.Count, moduleName);

        var result = compilation.Compile(source, filePath);
        if (!result.Success && result.Diagnostics.HasErrors)
        {
            diagnostics.AddRange(result.Diagnostics);
            return null;
        }

        // The compilation result won't directly give us the module — we need to get it
        // from the compilation's module cache. Let's use a different approach:
        // compile as a module and extract the CompiledModule.
        var compResult = compilation.CompileAsModule(moduleName, source, filePath);
        compilingModules.Remove(moduleName);
        Log.Debug("LibraryCompiler: module {ModuleName} compiled in {ElapsedMs}ms, success={Success}",
            moduleName, moduleSw.ElapsedMilliseconds, compResult is not null);
        if (compResult is null)
        {
            diagnostics.AddRange(compilation.GetDiagnostics());
            return null;
        }

        return compResult;
    }

    private static void ScanDependencies(string moduleName, string source, string filePath,
        ModuleGraph graph, ModuleResolver resolver,
        Dictionary<string, (string Path, string Source)> localModules,
        HashSet<string>? scanned = null)
    {
        scanned ??= [];
        if (!scanned.Add(moduleName))
            return;

        Log.Debug("LibraryCompiler: scanning dependencies for {ModuleName}", moduleName);

        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, diag);
        var tokens = lexer.Tokenize();
        if (diag.HasErrors) return;

        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        if (diag.HasErrors) return;

        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        foreach (var import in program.TopLevelForms
                     .SelectMany(f => f is AstNode.ModuleDecl m
                         ? new[] { f }.Concat(m.Body)
                         : [f])
                     .OfType<AstNode.Import>())
        {
            // Only track intra-package dependencies
            if (!localModules.ContainsKey(import.ModuleName))
                continue;

            Log.Debug("LibraryCompiler: {ModuleName} depends on {Dependency}", moduleName, import.ModuleName);
            graph.AddModule(import.ModuleName);
            graph.AddDependency(moduleName, import.ModuleName, import.Span);

            if (localModules.TryGetValue(import.ModuleName, out var depEntry))
                ScanDependencies(import.ModuleName, depEntry.Source, depEntry.Path, graph, resolver, localModules,
                    scanned);
        }
    }

    private static string ModuleNameToClassName(string moduleName)
    {
        return string.Concat(
            moduleName.Split('/', '-')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Module";
    }
}

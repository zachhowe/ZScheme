using System.Reflection.Emit;
using System.Reflection;
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

        // Build module name → source mapping
        var moduleSources = new Dictionary<string, (string Path, string Source)>();
        foreach (var file in zsFiles)
        {
            var moduleName = Path.GetFileNameWithoutExtension(file);
            moduleSources[moduleName] = (file, File.ReadAllText(file));
        }

        // Build dependency graph across all modules
        var graph = new ModuleGraph(diagnostics);
        var resolver = new ModuleResolver(diagnostics);
        resolver.AddSearchPath(sourceDir);
        if (options.StdLibPath is not null)
            resolver.AddSearchPath(options.StdLibPath);

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

        // Compile modules in topological order
        var compiledModules = new Dictionary<string, CompiledModule>();
        var compilingModules = new HashSet<string>();

        foreach (var moduleName in order)
        {
            if (!moduleSources.ContainsKey(moduleName))
                continue; // External dependency, skip

            var compiled = CompileModule(moduleName, moduleSources, compiledModules,
                compilingModules, resolver, options);
            if (compiled is null)
                return null;

            // Add the package namespace to the module's CLR namespaces so consuming
            // projects can resolve precompiled module class references
            if (manifest.Build.Namespace is { } ns
                && !compiled.ExportedClrNamespaces.Contains(ns))
            {
                compiled = compiled with
                {
                    ExportedClrNamespaces = compiled.ExportedClrNamespaces.Append(ns).ToList()
                };
            }

            compiledModules[moduleName] = compiled;
        }

        if (diagnostics.HasErrors)
            return null;

        // Emit all modules into one assembly using CecilEmitter
        var assemblyName = manifest.Name;
        var allIrDefs = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>();
        foreach (var (name, mod) in compiledModules)
        {
            if (mod.ExportedIrDefinitions.Count > 0)
                allIrDefs.Add((ModuleNameToClassName(name), mod.ExportedIrDefinitions));
        }

        // Collect all CLR namespaces
        var clrNamespaces = compiledModules.Values
            .SelectMany(m => m.ExportedClrNamespaces)
            .Distinct()
            .ToList();

        // Use CecilEmitter with an empty main program, putting all module code as imported modules
        var emitter = new CecilEmitter(assemblyName, diagnostics, "LibraryInit",
            clrNamespaces, options.AssemblySearchPaths, allIrDefs,
            ilNamespace: manifest.Build.Namespace);
        var emptyIr = new IrNode.Seq([]) { Type = ZType.Unit };
        var bytes = emitter.Emit(emptyIr);
        if (bytes is null || diagnostics.HasErrors)
            return null;

        return new LibraryCompilationResult(bytes, compiledModules);
    }

    private CompiledModule? CompileModule(string moduleName,
        Dictionary<string, (string Path, string Source)> moduleSources,
        Dictionary<string, CompiledModule> compiledModules,
        HashSet<string> compilingModules,
        ModuleResolver resolver,
        CompilerOptions options)
    {
        if (compiledModules.TryGetValue(moduleName, out var cached))
            return cached;

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

        // Use a sub-compilation to compile this module
        var subOptions = new CompilerOptions
        {
            DisablePrelude = true,
            StdLibPath = options.StdLibPath,
            AssemblySearchPaths = options.AssemblySearchPaths,
            UsePackageCache = false,
        };
        var compilation = new Compilation(subOptions);

        // Inject already-compiled sibling modules
        foreach (var (depName, depMod) in compiledModules)
            compilation.InjectModule(depName, depMod);

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
                ? new AstNode[] { f }.Concat(m.Body)
                : [f])
            .OfType<AstNode.Import>())
        {
            // Only track intra-package dependencies
            if (!localModules.ContainsKey(import.ModuleName))
                continue;

            graph.AddModule(import.ModuleName);
            graph.AddDependency(moduleName, import.ModuleName);

            if (localModules.TryGetValue(import.ModuleName, out var depEntry))
                ScanDependencies(import.ModuleName, depEntry.Source, depEntry.Path, graph, resolver, localModules, scanned);
        }
    }

    private static string ModuleNameToClassName(string moduleName) =>
        string.Concat(
            moduleName.Split('/', '-')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Module";
}

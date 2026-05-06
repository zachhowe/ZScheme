using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation(CompilerOptions? options = null)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Compilation>();

    private readonly HashSet<string> _compilingModules = [];
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Dictionary<string, CompiledModule> _moduleCache = new();
    private readonly CompilerOptions _options = options ?? new CompilerOptions();

    private readonly PackageCacheManager _packageCache = new();

    /// <summary>
    ///     The typed AST produced after stage 4 (type inference). Populated even when
    ///     <see cref="CompilerOptions.StopAfterTypeInference"/> is set and codegen is skipped.
    ///     Null until <see cref="Compile"/> reaches stage 4 successfully.
    /// </summary>
    public AstNode.Program? TypedProgram { get; private set; }

    private static IEnumerable<AstNode> AllTopLevelForms(AstNode.Program program)
    {
        return program.TopLevelForms.SelectMany(f => f is AstNode.ModuleDecl m
            ? new[] { f }.Concat(m.Body)
            : [f]);
    }

    public CompilationResult Compile(string source, string fileName = "input.zs")
    {
        Log.Debug("Compiling {FileName}", fileName);
        var compilationSw = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();

        // Stage 1: Lex
        var lexer = new Lexer(source, fileName, _diagnostics);
        var tokens = lexer.Tokenize();
        Log.Debug("Stage 1 Lex: {TokenCount} tokens in {ElapsedMs}ms", tokens.Count, sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.LexerFailure(_diagnostics);

        // Stage 2: Parse S-expressions
        sw.Restart();
        var parser = new SExprParser(tokens, _diagnostics);
        var sexprs = parser.ParseAll();
        Log.Debug("Stage 2 Parse: {SExprCount} s-expressions in {ElapsedMs}ms", sexprs.Count, sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.SExprParserFailure(_diagnostics);

        // Pre-parse to discover imports (before macro expansion)
        var preDiag = new DiagnosticBag();
        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(sexprs);

        // Resolve module imports early so macros from dependencies are available
        var preImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();
        var compiledModules = new List<CompiledModule>();

        // Check if this is a prelude module (prelude modules should not auto-import prelude)
        var preModuleDecl = AllTopLevelForms(preProgram).OfType<AstNode.ModuleDecl>().FirstOrDefault();
        var isPreludeModule = preModuleDecl is not null && _options.PreludeModules.Contains(preModuleDecl.ModuleName);
        var userImportNames = new HashSet<string>(preImports.Select(i => i.ModuleName));
        Log.Debug("Pre-parse: {ImportCount} imports, isPreludeModule={IsPrelude}", preImports.Count, isPreludeModule);

        var resolver = CreateModuleResolver(fileName);
        Log.Debug("Compilation: resolver created for {FileName}, packagePaths={PackagePathCount}",
            fileName, _options.PackagePaths.Count);

        // Load explicitly specified precompiled packages
        var (explicitPrecompiled, precompiledAliases) = LoadExplicitPrecompiledPackages();
        Log.Debug("Precompiled packages: {Count} loaded", explicitPrecompiled.Count);
        foreach (var mod in explicitPrecompiled)
            if (_moduleCache.TryAdd(mod.Name, mod))
                compiledModules.Add(mod);
        if (explicitPrecompiled.Count > 0)
            Log.Debug("Compilation: injected precompiled modules: [{ModuleNames}]",
                string.Join(", ", explicitPrecompiled.Select(m => m.Name)));

        // Register module aliases from precompiled packages (e.g., "zunit" → "zunit/zunit")
        foreach (var (alias, qualified) in precompiledAliases)
            resolver.AddModuleAlias(alias, qualified);

        // Load stdlib modules from package cache (skip when PackagePaths provides stdlib source)
        if (!_options.PackagePaths.ContainsKey("stdlib"))
        {
            var cachedPrelude = TryLoadPrecompiledModules("zscheme-stdlib");
            if (cachedPrelude is not null)
            {
                Log.Debug("Package cache hit: {ModuleCount} stdlib modules", cachedPrelude.Count);
                foreach (var mod in cachedPrelude)
                    if (_moduleCache.TryAdd(mod.Name, mod))
                        // Auto-import prelude modules (unless this is a module or prelude is disabled)
                        if (!_options.DisablePrelude && !isPreludeModule
                                                     && _options.PreludeModules.Contains(mod.Name)
                                                     && !userImportNames.Contains(mod.Name))
                            compiledModules.Add(mod);
            }
            else
            {
                // Try auto-install from source
                var anchorDir = Path.GetDirectoryName(Path.GetFullPath(fileName))
                                ?? Directory.GetCurrentDirectory();
                var autoInstalled = PackageAutoInstaller.TryAutoInstall(
                    "zscheme-stdlib", anchorDir, _diagnostics);
                if (autoInstalled is not null)
                {
                    cachedPrelude = LoadModulesFromPackage(autoInstalled);
                    if (cachedPrelude is not null)
                    {
                        Log.Debug("Package auto-install: {ModuleCount} stdlib modules", cachedPrelude.Count);
                        foreach (var mod in cachedPrelude)
                            if (_moduleCache.TryAdd(mod.Name, mod))
                                if (!_options.DisablePrelude && !isPreludeModule
                                                             && _options.PreludeModules.Contains(mod.Name)
                                                             && !userImportNames.Contains(mod.Name))
                                    compiledModules.Add(mod);
                    }
                }

                if (cachedPrelude is null)
                {
                    _diagnostics.Error(
                        "Package 'zscheme-stdlib' is not installed and could not be auto-installed. Run 'zs install' to install required packages.",
                        SourceSpan.None);
                    return new CompilationResult.DependencyResolutionFailure(_diagnostics);
                }
            }
        }

        // Compile prelude modules before user code (unless disabled or this is a prelude module itself)
        if (!_options.DisablePrelude && !isPreludeModule)
        {
            // Use a silent resolver to probe which prelude modules are available
            var probeDiag = new DiagnosticBag();
            var probeResolver = new ModuleResolver(probeDiag);
            foreach (var (name, path) in _options.PackagePaths)
            {
                probeResolver.AddPackagePath(name, path);
                if (name == "stdlib")
                    probeResolver.AddSearchPath(path);
            }

            foreach (var preludeName in _options.PreludeModules)
            {
                if (userImportNames.Contains(preludeName))
                    continue;
                if (_moduleCache.TryGetValue(preludeName, out var cached))
                {
                    if (!compiledModules.Contains(cached))
                        compiledModules.Add(cached);
                    continue;
                }

                // Probe whether the module exists before compiling (skip silently if not found)
                var probed = probeResolver.Resolve(preludeName, SourceSpan.None);
                if (probed is null)
                {
                    Log.Debug("Compilation: prelude module {PreludeName} not found, skipping", preludeName);
                    continue;
                }
                Log.Debug("Compilation: prelude module {PreludeName} found at {Path}", preludeName, probed.Value.Path);

                // Scan dependencies so transitive prelude deps are compiled first
                var preludeGraph = new ModuleGraph(_diagnostics);
                preludeGraph.AddModule(preludeName);
                ScanDependencies(preludeName, probed.Value.Source, probed.Value.Path, preludeGraph, probeResolver);

                var preludeOrder = preludeGraph.TopologicalSort();
                if (preludeOrder is null) continue;
                if (preludeOrder.Count > 0)
                    Log.Debug("Compilation: prelude dependency order for {PreludeName}: {Order}",
                        preludeName, string.Join(" -> ", preludeOrder));

                foreach (var depName in preludeOrder)
                {
                    if (_moduleCache.ContainsKey(depName)) continue;
                    var depCompiled = CompileModule(depName, resolver, SourceSpan.None);
                    if (depCompiled is null) continue;
                    _moduleCache[depName] = depCompiled;
                }

                if (_moduleCache.TryGetValue(preludeName, out var preludeMod))
                    compiledModules.Add(preludeMod);
            }
        }

        if (preImports.Count > 0)
        {
            // Add cached modules for explicit imports directly
            foreach (var import in preImports)
            {
                var importName = resolver.ResolveAlias(import.ModuleName);
                if (_moduleCache.TryGetValue(importName, out var cached)
                    && !compiledModules.Contains(cached))
                    compiledModules.Add(cached);
            }

            var graph = new ModuleGraph(_diagnostics);
            var importSpans = new Dictionary<string, SourceSpan>();

            foreach (var import in preImports)
            {
                var importName = resolver.ResolveAlias(import.ModuleName);
                importSpans.TryAdd(importName, import.Span);
                // Skip resolving modules already in cache (e.g., precompiled stdlib modules)
                if (_moduleCache.ContainsKey(importName))
                    continue;

                graph.AddModule(importName);
                var resolved = resolver.Resolve(import.ModuleName, import.Span);
                if (resolved is null)
                    continue;

                ScanDependencies(importName, resolved.Value.Source, resolved.Value.Path, graph, resolver);
            }

            if (_diagnostics.HasErrors)
                return new CompilationResult.DependencyResolutionFailure(_diagnostics);

            var order = graph.TopologicalSort();
            if (order is null)
                return new CompilationResult.DependencyResolutionFailure(_diagnostics);

            if (order.Count > 0)
                Log.Debug("Module compilation order: {Order}", string.Join(" -> ", order));

            foreach (var moduleName in order)
            {
                if (_moduleCache.ContainsKey(moduleName))
                    continue;

                var compiled = CompileModule(moduleName, resolver,
                    importSpans.GetValueOrDefault(moduleName, SourceSpan.None));
                if (compiled is null)
                    return new CompilationResult.DependencyResolutionFailure(_diagnostics);

                _moduleCache[moduleName] = compiled;
            }

            // Include all compiled modules (direct imports + transitive deps)
            foreach (var mod in _moduleCache.Values)
                if (!compiledModules.Contains(mod))
                    compiledModules.Add(mod);
        }

        // Stage 2.5: Macro expansion — seed with macros from imported modules
        sw.Restart();
        var macroEnv = MacroEnvironment.Default();
        foreach (var mod in compiledModules)
            foreach (var (name, macroDef) in mod.ExportedMacros)
                macroEnv.Define(name, macroDef);
        var importedMacroCount = compiledModules.Sum(m => m.ExportedMacros.Count);
        Log.Debug("Compilation: seeding macro env from {ModuleCount} modules, {MacroCount} total macros",
            compiledModules.Count, importedMacroCount);
        var expander = new MacroExpander(_diagnostics);
        sexprs = expander.ExpandAll(sexprs, macroEnv);
        Log.Debug("Stage 2.5 Macro expansion: {MacroCount} macros, {SExprCount} s-expressions in {ElapsedMs}ms",
            importedMacroCount, sexprs.Count, sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.MacroExpanderFailure(_diagnostics);

        // Stage 3: Build AST
        sw.Restart();
        var astBuilder = new AstBuilder(_diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        Log.Debug("Stage 3 AST: {FormCount} top-level forms in {ElapsedMs}ms", program.TopLevelForms.Count,
            sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.AstBuilderFailure(_diagnostics);

        // Extract namespace directive (if present) — source overrides options
        var nsDecls = AllTopLevelForms(program).OfType<AstNode.NamespaceDecl>().ToList();
        if (nsDecls.Count > 1)
            _diagnostics.Warning("Multiple namespace declarations; using the first one", nsDecls[1].Span);
        if (nsDecls.Count > 0)
            _options.Namespace = nsDecls[0].NsName;

        // Extract module name (if present) — convert to PascalCase class name
        var moduleDecls = AllTopLevelForms(program).OfType<AstNode.ModuleDecl>().ToList();

        // Require module declaration unless AllowsImplicitModuleName is set
        if (moduleDecls.Count == 0)
        {
            if (_options.AllowsImplicitModuleName)
            {
                // REPL / unit test mode: silently use a default class name
            }
            else
            {
                var firstDefine = program.TopLevelForms.FirstOrDefault(f => f is AstNode.Define or AstNode.DefineValue);
                if (firstDefine is not null)
                {
                    _diagnostics.Error("Files with top-level definitions require a (module ...) declaration",
                        firstDefine.Span);
                    return new CompilationResult.MissingModuleDeclFailure(_diagnostics);
                }

                var firstForm = program.TopLevelForms.FirstOrDefault();
                _diagnostics.Error("Files require a (module ...) declaration",
                    firstForm?.Span ?? SourceSpan.None);
                return new CompilationResult.MissingModuleNameFailure(_diagnostics);
            }
        }

        var className = moduleDecls.Count > 0
            ? NameConverter.ClassNameFromModuleName(moduleDecls[0].ModuleName)
            : "UnnamedModule";

        // Stage 4: Type inference — inject imported types first
        sw.Restart();
        var env = TypeEnv.CreateRoot();

        foreach (var mod in compiledModules)
            foreach (var (name, type) in mod.ExportedTypes)
                env.Define(name, type);
        var injectedTypeCount = compiledModules.Sum(m => m.ExportedTypes.Count);
        Log.Debug("Compilation: injected {TypeCount} types from {ModuleCount} modules into type environment",
            injectedTypeCount, compiledModules.Count);

        var inferer = new TypeInferer(_diagnostics, _options.AssemblySearchPaths);

        // Inject class interface info from imported modules for cross-module subtyping
        foreach (var mod in compiledModules)
            if (mod.ExportedClassInterfaces is not null)
                inferer.RegisterClassInterfaces(mod.ExportedClassInterfaces);

        var classIfaceCount = compiledModules.Count(m => m.ExportedClassInterfaces is { Count: > 0 });
        if (classIfaceCount > 0)
            Log.Debug("Compilation: registered class interfaces from {Count} modules", classIfaceCount);

        inferer.Infer(program, env);
        inferer.Resolve(program);
        Log.Debug("Stage 4 Type inference: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        TypedProgram = program;
        if (_diagnostics.HasErrors)
            return new CompilationResult.TypeInfererFailure(_diagnostics);

        if (_options.StopAfterTypeInference)
        {
            Log.Debug("Compilation: stopping after type inference (LSP analysis mode)");
            return new CompilationResult.TypeAnalysisResult(_diagnostics);
        }

        // Stage 5: Lower to IR — inject imported CLR bindings first
        sw.Restart();
        var lowering = new IrLowering(_diagnostics, inferer.OutParamsByAlias);

        foreach (var mod in compiledModules)
        {
            foreach (var (alias, (typeName, methodName, genericArity, kind, constraints)) in mod.ExportedClrImports)
                lowering.RegisterClrImport(alias, typeName, methodName, genericArity, kind, constraints);
            if (mod.ExportedUnionCtors is not null)
                foreach (var (caseName, unionName) in mod.ExportedUnionCtors)
                    lowering.RegisterUnionCtor(caseName, unionName);
            if (mod.ExportedRecordCtors is not null)
                foreach (var (recordName, fieldNames) in mod.ExportedRecordCtors)
                    lowering.RegisterRecordCtor(recordName, fieldNames);
        }

        var clrImportCount = compiledModules.Sum(m => m.ExportedClrImports.Count);
        var unionCtorCount = compiledModules.Sum(m => m.ExportedUnionCtors?.Count ?? 0);
        var recordCtorCount = compiledModules.Sum(m => m.ExportedRecordCtors?.Count ?? 0);
        Log.Debug("Compilation: IR lowering injected {ClrImports} CLR imports, {UnionCtors} union ctors, {RecordCtors} record ctors",
            clrImportCount, unionCtorCount, recordCtorCount);

        var ir = lowering.Lower(program);
        Log.Debug("Stage 5 IR lowering: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.IrLoweringFailure(_diagnostics);

        // Build imported module info for emitters — source-compiled modules (both backends)
        // Use AllIrDefinitions when available so internal helpers are included in IL emission
        var sourceImportedModules = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is null && mod.ExportedIrDefinitions.Count > 0)
            .Select(mod => (NameConverter.ClassNameFromModuleName(mod.Name),
                mod.AllIrDefinitions ?? mod.ExportedIrDefinitions))
            .ToList();

        // For C# backend: source-compiled modules only — precompiled types are
        // referenced from the DLL via using directives (no re-emission needed)
        var csImportedModules = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>(sourceImportedModules);

        // Precompiled assemblies — referenced externally instead of inlining IR
        var precompiledAssemblyPaths = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is not null)
            .Select(mod => mod.PrecompiledAssemblyPath!)
            .Distinct()
            .ToList();

        // Build func-to-module-class map for precompiled modules (emitters need qualified names)
        var precompiledModuleMap = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is not null)
            .SelectMany(mod =>
                mod.ExportedNames.Select(name => (name, className: NameConverter.ClassNameFromModuleName(mod.Name))))
            .GroupBy(x => x.name)
            .ToDictionary(g => g.Key, g => g.First().className);

        // Collect CLR namespace imports from lowering and compiled modules
        var clrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in compiledModules)
            clrNamespaces.AddRange(mod.ExportedClrNamespaces);
        clrNamespaces = clrNamespaces.Distinct().ToList();

        // Stage 6: Code generation
        sw.Restart();
        if (_options.OutputMode == OutputMode.CSharp)
        {
            Log.Debug("Compilation: constructing CSharpEmitter, namespace={Namespace}, className={ClassName}, usings={UsingCount}, importedModules={ImportedModuleCount}, precompiledMap={PrecompiledMapCount}",
                _options.Namespace, className, clrNamespaces.Count, csImportedModules.Count, precompiledModuleMap.Count);
            var emitter = new CSharpEmitter(_diagnostics, _options.Namespace, className, clrNamespaces,
                csImportedModules, precompiledModuleMap,
                moduleDecls.Count > 0,
                _options.SuppressVersionPreamble);
            var csCode = emitter.Emit(ir);
            Log.Debug("Stage 6 C# emit: {OutputLength} chars in {ElapsedMs}ms", csCode.Length, sw.ElapsedMilliseconds);
            Log.Debug("Compilation of {FileName} completed in {ElapsedMs}ms", fileName,
                compilationSw.ElapsedMilliseconds);
            return new CompilationResult.CSharpOutputResult(_diagnostics, csCode, precompiledAssemblyPaths);
        }

        // IL backend
        Log.Debug("Compilation: constructing IlEmitter, namespace={Namespace}, className={ClassName}, usings={UsingCount}, importedModules={ImportedModuleCount}, precompiled={PrecompiledCount}",
            _options.Namespace, className, clrNamespaces.Count, sourceImportedModules.Count, precompiledAssemblyPaths.Count);
        var ilEmitter = new IlEmitter(_options.Namespace, _diagnostics, className, clrNamespaces,
            _options.AssemblySearchPaths, sourceImportedModules, precompiledAssemblyPaths,
            isModule: moduleDecls.Count > 0);
        var bytes = ilEmitter.Emit(ir);
        var hasEntryPoint = ilEmitter.HasEntryPoint;

        Log.Debug("Stage 6 IL emit: {OutputBytes} bytes in {ElapsedMs}ms", bytes?.Length ?? 0, sw.ElapsedMilliseconds);
        if (bytes is null || _diagnostics.HasErrors)
            return new CompilationResult.IlOutputFailure(_diagnostics);
        Log.Debug("Compilation of {FileName} completed in {ElapsedMs}ms", fileName, compilationSw.ElapsedMilliseconds);
        return new CompilationResult.IlOutputResult(_diagnostics, bytes, precompiledAssemblyPaths)
        {
            IsExecutable = hasEntryPoint
        };
    }

    /// <summary>
    ///     Injects a pre-compiled module into this compilation's cache so it's available
    ///     to subsequent compilations without recompiling from source.
    /// </summary>
    public void InjectModule(string name, CompiledModule module)
    {
        _moduleCache[name] = module;
    }

    public DiagnosticBag GetDiagnostics()
    {
        return _diagnostics;
    }

    /// <summary>
    ///     Returns distinct precompiled assembly paths from all cached modules.
    /// </summary>
    public IReadOnlyList<string> GetPrecompiledAssemblyPaths()
    {
        return _moduleCache.Values
            .Where(mod => mod.PrecompiledAssemblyPath is not null)
            .Select(mod => mod.PrecompiledAssemblyPath!)
            .Distinct()
            .ToList();
    }

    private void CopyDiagnostics(DiagnosticBag source)
    {
        _diagnostics.AddRange(source);
    }
}

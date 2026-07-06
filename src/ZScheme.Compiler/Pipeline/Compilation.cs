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
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation(CompilerOptions? options = null)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Compilation>();

    private readonly HashSet<string> _compilingModules = [];
    private readonly DiagnosticBag _diagnostics = new();
    private readonly HashSet<string> _failedModules = [];
    private readonly Dictionary<string, CompiledModule> _moduleCache = new();
    private readonly CompilerOptions _options = options ?? new CompilerOptions();

    private readonly PackageCacheManager _packageCache = new(
        ZSchemePaths.GetPackageCacheRoot(options?.CacheDirectory)
    );

    /// <summary>
    ///     Compilation-wide registry of type aliases declared via `(define-type-alias ...)`.
    ///     Populated during IR collection (after all modules have been lowered) and consulted
    ///     by codegen to map ZScheme named types to CLR types.
    ///     Also contains compiler-built-in aliases (Task, ValueTuple) registered at construction.
    /// </summary>
    public TypeAliasRegistry TypeAliases { get; } = CreateDefaultRegistry();

    /// <summary>
    ///     The typed AST produced after stage 4 (type inference). Populated even when
    ///     <see cref="CompilerOptions.StopAfterTypeInference" /> is set and codegen is skipped.
    ///     Null until <see cref="Compile" /> reaches stage 4 successfully.
    /// </summary>
    public AstNode.Program? TypedProgram { get; private set; }

    /// <summary>
    ///     The main file's s-expressions after stages 1-2 (lex/parse), before macro expansion.
    ///     Null until <see cref="Compile" /> parses successfully.
    /// </summary>
    public List<SExpr>? RawSExprs { get; private set; }

    /// <summary>
    ///     The main file's s-expressions after stage 2.5 (macro expansion). Assigned even when
    ///     expansion reported errors (e.g. the depth limit), so a debugger can show the partial
    ///     result. Null until <see cref="Compile" /> reaches stage 2.5.
    /// </summary>
    public List<SExpr>? ExpandedSExprs { get; private set; }

    private static TypeAliasRegistry CreateDefaultRegistry()
    {
        var registry = new TypeAliasRegistry();
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "Task",
                [],
                "System.Threading.Tasks.Task`1",
                "System.Threading.Tasks",
                TypeAliasKind.GenericClrType,
                default
            )
        );
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "System.Threading.Tasks.Task",
                [],
                "System.Threading.Tasks.Task`1",
                "System.Threading.Tasks",
                TypeAliasKind.GenericClrType,
                default
            )
        );
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "ValueTuple",
                [],
                "System.ValueTuple",
                "System.Private.CoreLib",
                TypeAliasKind.GenericClrType,
                default
            )
        );
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "Clr-Array",
                ["^a"],
                "",
                "System.Private.CoreLib",
                TypeAliasKind.SzArray,
                default
            )
        );
        // Seq is the honest ZScheme name for IEnumerable<T>. CLR collection members such as
        // Dictionary.Keys/.Values return enumerable view types (KeyCollection, ICollection<T>,
        // IEnumerable<T>) rather than concrete lists; binding those imports to (Seq ^a) keeps the
        // declared type truthful so the import-clr validator passes them.
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "Seq",
                ["^a"],
                "System.Collections.Generic.IEnumerable",
                "System.Private.CoreLib",
                TypeAliasKind.GenericClrType,
                default
            )
        );
        return registry;
    }

    private static IEnumerable<AstNode> AllTopLevelForms(AstNode.Program program)
    {
        return program.TopLevelForms.SelectMany(f =>
            f is AstNode.ModuleDecl m ? new[] { f }.Concat(m.Body) : [f]
        );
    }

    public CompilationResult Compile(string source, string fileName = "input.zs")
    {
        Log.Debug("Compiling {FileName}", fileName);
        var compilationSw = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();

        // Stages 1-2: Lex and Parse
        var (tokens, sexprs, hasLexErrors) = CompileLexAndParse(source, fileName, sw);
        if (hasLexErrors)
            return new CompilationResult.LexerFailure(_diagnostics);
        if (_diagnostics.HasErrors)
            return new CompilationResult.SExprParserFailure(_diagnostics);
        RawSExprs = sexprs;

        // Pre-parse: discover imports before macro expansion
        var (preProgram, preImports, isPreludeModule, userImportNames) =
            CompilePreParseAndDiscoverImports(sexprs, new HashSet<string>(_options.PreludeModules));

        var resolver = CreateModuleResolver(fileName);
        Log.Debug(
            "Compilation: resolver created for {FileName}, packagePaths={PackagePathCount}",
            fileName,
            _options.PackagePaths.Count
        );

        var moduleAliases = new Dictionary<string, string>();
        var compiledModules = new List<CompiledModule>();

        // Load precompiled packages and stdlib
        CompileLoadModules(
            moduleAliases,
            compiledModules,
            isPreludeModule,
            userImportNames,
            _options.DisablePrelude,
            new HashSet<string>(_options.PreludeModules),
            _options.PackagePaths,
            fileName
        );

        // Register module aliases from precompiled packages
        foreach (var (alias, qualified) in moduleAliases)
            resolver.AddModuleAlias(alias, qualified);

        // Compile prelude modules
        CompilePreludeModules(
            compiledModules,
            resolver,
            isPreludeModule,
            _options.DisablePrelude,
            new HashSet<string>(_options.PreludeModules),
            userImportNames
        );

        if (_diagnostics.HasErrors)
            return new CompilationResult.DependencyResolutionFailure(_diagnostics);

        // Resolve and compile user imports
        var (_, compiledModules2, importErrors) = CompileResolveAndCompileImports(
            preImports,
            compiledModules,
            resolver
        );
        if (importErrors)
            return new CompilationResult.DependencyResolutionFailure(_diagnostics);

        compiledModules = compiledModules2;

        // Stage 2.5: Macro expansion
        sw.Restart();
        var (expandedSexprs, macroErrors) = CompileExpandMacros(sexprs, compiledModules, sw);
        ExpandedSExprs = expandedSexprs;
        if (macroErrors)
            return new CompilationResult.MacroExpanderFailure(_diagnostics);
        sexprs = expandedSexprs;

        if (_options.StopAfterMacroExpansion)
        {
            Log.Debug("Compilation: stopping after macro expansion (macro debugger mode)");
            return new CompilationResult.MacroExpansionResult(_diagnostics);
        }

        // Stage 3: Build AST
        sw.Restart();
        var (program, className, hasModuleDecl, astFailure) = CompileBuildAst(
            sexprs,
            "UnnamedModule",
            sw
        );
        if (astFailure is not null)
            return astFailure;

        // Pre-pass: collect type aliases from this module's AST and from all imported modules'
        // IR so the registry is populated before type inference (TypeInferer needs alias-aware
        // CLR mapping for `(new T<...>)` validation).
        CollectTypeAliasesFromAst(program!);
        foreach (var mod in compiledModules)
        {
            var modIr = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
            foreach (var def in modIr)
                CollectTypeAliases(def);
        }

        // Stage 4: Type inference
        sw.Restart();
        var (inferer, typeInferenceErrors) = CompileTypeInference(
            program!,
            compiledModules,
            _options.PrimaryModuleName,
            hasModuleDecl,
            sw
        );
        if (typeInferenceErrors)
            return new CompilationResult.TypeInfererFailure(_diagnostics);

        // Stage 4.5: Validate the entry point (`main`) signature. Runs before the
        // StopAfterTypeInference early-return so the LSP surfaces these diagnostics too.
        new EntryPointValidator(_diagnostics, TypeAliases).Validate(program!);
        if (_diagnostics.HasErrors)
            return new CompilationResult.EntryPointValidationFailure(_diagnostics);

        if (_options.StopAfterTypeInference)
        {
            Log.Debug("Compilation: stopping after type inference (LSP analysis mode)");
            return new CompilationResult.TypeAnalysisResult(_diagnostics);
        }

        // Stage 5: Lower to IR
        sw.Restart();
        var (ir, lowering, loweringErrors) = CompileLowerToIr(
            program!,
            compiledModules,
            inferer.OutParamsByAlias,
            sw
        );
        if (loweringErrors)
            return new CompilationResult.IrLoweringFailure(_diagnostics);

        // Stage 6: Emit code
        var result = CompileEmit(
            ir,
            lowering,
            compiledModules,
            className,
            hasModuleDecl,
            _options.SuppressVersionPreamble
        );

        Log.Debug(
            "Compilation of {FileName} completed in {ElapsedMs}ms",
            fileName,
            compilationSw.ElapsedMilliseconds
        );
        return result;
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
        return _moduleCache
            .Values.Where(mod => mod.PrecompiledAssemblyPath is not null)
            .Select(mod => mod.PrecompiledAssemblyPath!)
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     Returns all modules cached in this compilation (including transitive dependencies
    ///     that were compiled as part of resolving imports for the primary module).
    /// </summary>
    public IReadOnlyDictionary<string, CompiledModule> GetCachedModules()
    {
        return new Dictionary<string, CompiledModule>(_moduleCache);
    }

    private void CopyDiagnostics(DiagnosticBag source)
    {
        _diagnostics.AddRange(source);
    }

    #region Pipeline Stage Methods

    /// <summary>
    ///     Stages 1-2: Lex source into tokens, then parse into s-expressions.
    /// </summary>
    private (List<Token> Tokens, List<SExpr> SExprs, bool HasLexErrors) CompileLexAndParse(
        string source,
        string fileName,
        Stopwatch sw
    )
    {
        // Stage 1: Lex
        var lexer = new Lexer(source, fileName, _diagnostics);
        var tokens = lexer.Tokenize();
        Log.Debug(
            "Stage 1 Lex: {TokenCount} tokens in {ElapsedMs}ms",
            tokens.Count,
            sw.ElapsedMilliseconds
        );
        if (_diagnostics.HasErrors)
            return (tokens, [], true);

        // Stage 2: Parse S-expressions
        sw.Restart();
        var parser = new SExprParser(tokens, _diagnostics);
        var sexprs = parser.ParseAll();
        Log.Debug(
            "Stage 2 Parse: {SExprCount} s-expressions in {ElapsedMs}ms",
            sexprs.Count,
            sw.ElapsedMilliseconds
        );
        if (_diagnostics.HasErrors)
            return (tokens, sexprs, false);

        return (tokens, sexprs, false);
    }

    /// <summary>
    ///     Pre-parse s-expressions to discover import directives before macro expansion.
    /// </summary>
    private (
        AstNode.Program Program,
        List<AstNode.Import> Imports,
        bool IsPreludeModule,
        HashSet<string> UserImportNames
    ) CompilePreParseAndDiscoverImports(List<SExpr> sexprs, HashSet<string> preludeModules)
    {
        var preDiag = new DiagnosticBag();
        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(sexprs);

        var preImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();

        // Check if this is a prelude module (prelude modules should not auto-import prelude)
        var preModuleDecl = AllTopLevelForms(preProgram)
            .OfType<AstNode.ModuleDecl>()
            .FirstOrDefault();
        var isPreludeModule =
            preModuleDecl is not null && preludeModules.Contains(preModuleDecl.ModuleName);
        var userImportNames = new HashSet<string>(preImports.Select(i => i.ModuleName));
        Log.Debug(
            "Pre-parse: {ImportCount} imports, isPreludeModule={IsPrelude}",
            preImports.Count,
            isPreludeModule
        );

        return (preProgram, preImports, isPreludeModule, userImportNames);
    }

    /// <summary>
    ///     Loads precompiled packages and stdlib modules into the module cache.
    /// </summary>
    private void CompileLoadModules(
        Dictionary<string, string> moduleAliases,
        List<CompiledModule> compiledModules,
        bool isPreludeModule,
        HashSet<string> userImportNames,
        bool disablePrelude,
        HashSet<string> preludeModules,
        IReadOnlyDictionary<string, string> packagePaths,
        string sourceFileName
    )
    {
        // Load explicitly specified precompiled packages
        var (explicitPrecompiled, precompiledAliases) = LoadExplicitPrecompiledPackages();
        Log.Debug("Precompiled packages: {Count} loaded", explicitPrecompiled.Count);
        foreach (var mod in explicitPrecompiled)
            if (_moduleCache.TryAdd(mod.Name, mod))
                compiledModules.Add(mod);
        if (explicitPrecompiled.Count > 0)
            Log.Debug(
                "Compilation: injected precompiled modules: [{ModuleNames}]",
                string.Join(", ", explicitPrecompiled.Select(m => m.Name))
            );

        // Register module aliases from precompiled packages (e.g., "zunit" → "zunit/zunit")
        foreach (var (alias, qualified) in precompiledAliases)
            moduleAliases[alias] = qualified;

        // Load stdlib modules from package cache (skip when PackagePaths provides stdlib source)
        if (!packagePaths.ContainsKey("stdlib"))
        {
            var cachedModules = TryLoadPrecompiledModules("zscheme-stdlib");
            if (cachedModules is not null)
            {
                Log.Debug("Package cache hit: {ModuleCount} stdlib modules", cachedModules.Count);
                foreach (var mod in cachedModules)
                    if (_moduleCache.TryAdd(mod.Name, mod))
                    {
                        // Always add precompiled modules so their assembly paths are
                        // collected for the IL emitter (precompiledAssemblyPaths).
                        if (mod.PrecompiledAssemblyPath is not null)
                            compiledModules.Add(mod);
                        // Also add to compiledModules for prelude modules so they
                        // are available during type inference / IR lowering.
                        else if (
                            !disablePrelude
                            && !isPreludeModule
                            && preludeModules.Contains(mod.Name)
                            && !userImportNames.Contains(mod.Name)
                        )
                            compiledModules.Add(mod);
                    }
            }
            else
            {
                // Try auto-install from source
                var anchorDir =
                    Path.GetDirectoryName(Path.GetFullPath(sourceFileName))
                    ?? Directory.GetCurrentDirectory();
                var autoInstalled = PackageAutoInstaller.TryAutoInstall(
                    "zscheme-stdlib",
                    anchorDir,
                    _diagnostics,
                    _options.CacheDirectory
                );
                if (autoInstalled is not null)
                {
                    cachedModules = LoadModulesFromPackage(autoInstalled);
                    if (cachedModules is not null)
                    {
                        Log.Debug(
                            "Package auto-install: {ModuleCount} stdlib modules",
                            cachedModules.Count
                        );
                        foreach (var mod in cachedModules)
                            if (_moduleCache.TryAdd(mod.Name, mod))
                            {
                                // Always add precompiled modules so their assembly paths are
                                // collected for the IL emitter (precompiledAssemblyPaths).
                                if (mod.PrecompiledAssemblyPath is not null)
                                    compiledModules.Add(mod);
                                // Also add to compiledModules for prelude modules so they
                                // are available during type inference / IR lowering.
                                else if (
                                    !disablePrelude
                                    && !isPreludeModule
                                    && preludeModules.Contains(mod.Name)
                                    && !userImportNames.Contains(mod.Name)
                                )
                                    compiledModules.Add(mod);
                            }
                    }
                }

                if (cachedModules is null)
                    _diagnostics.Error(
                        "Package 'zscheme-stdlib' is not installed and could not be auto-installed. Run 'zs install' to install required packages.",
                        SourceSpan.None
                    );
            }
        }
    }

    /// <summary>
    ///     Compiles prelude modules before user code (unless disabled or this is a prelude module itself).
    /// </summary>
    private void CompilePreludeModules(
        List<CompiledModule> compiledModules,
        ModuleResolver resolver,
        bool isPreludeModule,
        bool disablePrelude,
        HashSet<string> preludeModules,
        HashSet<string> userImportNames
    )
    {
        if (disablePrelude || isPreludeModule)
            return;

        // Use a silent resolver to probe which prelude modules are available
        var probeDiag = new DiagnosticBag();
        var probeResolver = new ModuleResolver(probeDiag);
        foreach (var (name, path) in _options.PackagePaths)
        {
            probeResolver.AddPackagePath(name, path);
            if (name == "stdlib")
                probeResolver.AddSearchPath(path);
        }

        foreach (var preludeName in preludeModules)
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
                Log.Debug(
                    "Compilation: prelude module {PreludeName} not found, skipping",
                    preludeName
                );
                continue;
            }

            Log.Debug(
                "Compilation: prelude module {PreludeName} found at {Path}",
                preludeName,
                probed.Value.Path
            );

            // Scan dependencies so transitive prelude deps are compiled first
            var preludeGraph = new ModuleGraph(_diagnostics);
            preludeGraph.AddModule(preludeName);
            ScanDependencies(
                preludeName,
                probed.Value.Source,
                probed.Value.Path,
                preludeGraph,
                probeResolver
            );

            var preludeOrder = preludeGraph.TopologicalSort();
            if (preludeOrder is null)
                continue;
            if (preludeOrder.Count > 0)
                Log.Debug(
                    "Compilation: prelude dependency order for {PreludeName}: {Order}",
                    preludeName,
                    string.Join(" -> ", preludeOrder)
                );

            foreach (var depName in preludeOrder)
            {
                if (_moduleCache.ContainsKey(depName))
                    continue;
                var depCompiled = CompileModule(depName, resolver, SourceSpan.None);
                if (depCompiled is null)
                    continue;
                _moduleCache[depName] = depCompiled;
            }

            if (_moduleCache.TryGetValue(preludeName, out var preludeMod))
                compiledModules.Add(preludeMod);
        }
    }

    /// <summary>
    ///     Resolves user import directives, builds dependency graph, and compiles all imported modules.
    /// </summary>
    private (
        List<AstNode.Import> Imports,
        List<CompiledModule> CompiledModules,
        bool HasErrors
    ) CompileResolveAndCompileImports(
        List<AstNode.Import> preImports,
        List<CompiledModule> compiledModules,
        ModuleResolver resolver
    )
    {
        if (preImports.Count <= 0)
            return (preImports, compiledModules, false);

        // Add cached modules for explicit imports directly
        foreach (var import in preImports)
        {
            var importName = resolver.ResolveAlias(import.ModuleName);
            if (
                _moduleCache.TryGetValue(importName, out var cached)
                && !compiledModules.Contains(cached)
            )
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

            ScanDependencies(
                importName,
                resolved.Value.Source,
                resolved.Value.Path,
                graph,
                resolver
            );
        }

        if (_diagnostics.HasErrors)
            return (preImports, compiledModules, true);

        var order = graph.TopologicalSort();
        if (order is null)
            return (preImports, compiledModules, true);

        if (order.Count > 0)
            Log.Debug("Module compilation order: {Order}", string.Join(" -> ", order));

        foreach (var moduleName in order)
        {
            if (_moduleCache.ContainsKey(moduleName))
                continue;

            var compiled = CompileModule(
                moduleName,
                resolver,
                importSpans.GetValueOrDefault(moduleName, SourceSpan.None)
            );
            if (compiled is null)
                return (preImports, compiledModules, true);

            _moduleCache[moduleName] = compiled;
        }

        // Include all compiled modules (direct imports + transitive deps)
        foreach (var mod in _moduleCache.Values)
            if (!compiledModules.Contains(mod))
                compiledModules.Add(mod);

        return (preImports, compiledModules, false);
    }

    /// <summary>
    ///     Stage 2.5: Expand macros from the macro environment seeded with imported module macros.
    /// </summary>
    private (List<SExpr> SExprs, bool HasErrors) CompileExpandMacros(
        List<SExpr> sexprs,
        List<CompiledModule> compiledModules,
        Stopwatch sw
    )
    {
        var macroEnv = MacroEnvironment.Default();
        foreach (var mod in compiledModules)
        foreach (var (name, macroDef) in mod.ExportedMacros)
            macroEnv.Define(name, macroDef);
        var importedMacroCount = compiledModules.Sum(m => m.ExportedMacros.Count);
        Log.Debug(
            "Compilation: seeding macro env from {ModuleCount} modules, {MacroCount} total macros",
            compiledModules.Count,
            importedMacroCount
        );
        var expander = new MacroExpander(_diagnostics, _options.MacroObserver);
        sexprs = expander.ExpandAll(sexprs, macroEnv);
        Log.Debug(
            "Stage 2.5 Macro expansion: {MacroCount} macros, {SExprCount} s-expressions in {ElapsedMs}ms",
            importedMacroCount,
            sexprs.Count,
            sw.ElapsedMilliseconds
        );
        return (sexprs, _diagnostics.HasErrors);
    }

    /// <summary>
    ///     Stage 3: Build typed AST from s-expressions, extract namespace and module declarations.
    /// </summary>
    private (
        AstNode.Program? Program,
        string ClassName,
        bool HasModuleDecl,
        CompilationResult? Failure
    ) CompileBuildAst(List<SExpr> sexprs, string defaultClassName, Stopwatch sw)
    {
        var astBuilder = new AstBuilder(_diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        Log.Debug(
            "Stage 3 AST: {FormCount} top-level forms in {ElapsedMs}ms",
            program.TopLevelForms.Count,
            sw.ElapsedMilliseconds
        );
        if (_diagnostics.HasErrors)
            return (
                program,
                defaultClassName,
                false,
                new CompilationResult.AstBuilderFailure(_diagnostics)
            );

        // Extract namespace directive (if present) — source overrides options
        var nsDecls = AllTopLevelForms(program).OfType<AstNode.NamespaceDecl>().ToList();
        if (nsDecls.Count > 1)
            _diagnostics.Warning(
                "Multiple namespace declarations; using the first one",
                nsDecls[1].Span
            );
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
                var firstDefine = program.TopLevelForms.FirstOrDefault(f =>
                    f is AstNode.Define or AstNode.DefineValue
                );
                if (firstDefine is not null)
                {
                    _diagnostics.Error(
                        "Files with top-level definitions require a (module ...) declaration",
                        firstDefine.Span
                    );
                    return (
                        program,
                        defaultClassName,
                        false,
                        new CompilationResult.MissingModuleDeclFailure(_diagnostics)
                    );
                }

                var firstForm = program.TopLevelForms.FirstOrDefault();
                _diagnostics.Error(
                    "Files require a (module ...) declaration",
                    firstForm?.Span ?? SourceSpan.None
                );
                return (
                    program,
                    defaultClassName,
                    false,
                    new CompilationResult.MissingModuleNameFailure(_diagnostics)
                );
            }
        }

        var className =
            moduleDecls.Count > 0
                ? NameConverter.ClassNameFromModuleName(moduleDecls[0].ModuleName)
                : defaultClassName;

        return (program, className, moduleDecls.Count > 0, null);
    }

    /// <summary>
    ///     Stage 4: Type inference — inject imported types, infer types, and resolve.
    /// </summary>
    private (TypeInferer Inferer, bool HasErrors) CompileTypeInference(
        AstNode.Program program,
        List<CompiledModule> compiledModules,
        string? primaryModuleName,
        bool hasModuleDecl,
        Stopwatch sw
    )
    {
        var env = TypeEnv.CreateRoot();

        foreach (var mod in compiledModules)
        foreach (var (name, type) in mod.ExportedTypes)
            env.DefineImportedBinding(mod.Name, name, type);
        var injectedTypeCount = compiledModules.Sum(m => m.ExportedTypes.Count);
        Log.Debug(
            "Compilation: injected {TypeCount} types from {ModuleCount} modules into type environment",
            injectedTypeCount,
            compiledModules.Count
        );

        // Collect CLR namespaces from all compiled modules for short-type-name resolution
        var clrNamespaces = compiledModules
            .SelectMany(m => m.ExportedClrNamespaces)
            .Distinct()
            .ToList();

        var inferer = new TypeInferer(
            _diagnostics,
            _options.AssemblySearchPaths,
            TypeAliases,
            clrNamespaces.Count > 0 ? clrNamespaces : null
        )
        {
            // Prefer the externally-supplied package-qualified name so locals registered as
            // overload candidates use the same qualified prefix that prelude self-imports
            // produce (e.g. "stdlib/vector/..." not "vector/..."). Falls back to the file's
            // own module declaration for standalone compilations.
            CurrentModuleName =
                primaryModuleName
                ?? (
                    hasModuleDecl
                        ? program
                            .TopLevelForms.OfType<AstNode.ModuleDecl>()
                            .FirstOrDefault()
                            ?.ModuleName
                        : null
                ),
        };

        // Inject class interface info from imported modules for cross-module subtyping
        foreach (var mod in compiledModules)
            if (mod.ExportedClassInterfaces is not null)
                inferer.RegisterClassInterfaces(mod.ExportedClassInterfaces);

        var classIfaceCount = compiledModules.Count(m =>
            m.ExportedClassInterfaces is { Count: > 0 }
        );
        if (classIfaceCount > 0)
            Log.Debug(
                "Compilation: registered class interfaces from {Count} modules",
                classIfaceCount
            );

        inferer.Infer(program, env);
        inferer.Resolve(program);
        Log.Debug("Stage 4 Type inference: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        TypedProgram = program;

        return (inferer, _diagnostics.HasErrors);
    }

    /// <summary>
    ///     Stage 5: Lower typed AST to IR — inject imported CLR bindings and lower.
    /// </summary>
    private (IrNode Ir, IrLowering Lowering, bool HasErrors) CompileLowerToIr(
        AstNode.Program program,
        List<CompiledModule> compiledModules,
        IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> outParamsByAlias,
        Stopwatch sw
    )
    {
        var lowering = new IrLowering(
            _diagnostics,
            outParamsByAlias,
            TypeAliases,
            _options.AssemblySearchPaths
        );

        foreach (var mod in compiledModules)
        {
            foreach (
                var (
                    alias,
                    (typeName, methodName, genericArity, kind, constraints)
                ) in mod.ExportedClrImports
            )
                lowering.RegisterClrImport(
                    alias,
                    typeName,
                    methodName,
                    genericArity,
                    kind,
                    constraints
                );
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
        Log.Debug(
            "Compilation: IR lowering injected {ClrImports} CLR imports, {UnionCtors} union ctors, {RecordCtors} record ctors",
            clrImportCount,
            unionCtorCount,
            recordCtorCount
        );

        var ir = lowering.Lower(program);
        Log.Debug("Stage 5 IR lowering: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);

        return (ir, lowering, _diagnostics.HasErrors);
    }

    /// <summary>
    ///     Stage 6: Emit code (C# source or IL bytes) from the IR.
    /// </summary>
    private CompilationResult CompileEmit(
        IrNode ir,
        IrLowering lowering,
        List<CompiledModule> compiledModules,
        string className,
        bool isModule,
        bool suppressVersionPreamble
    )
    {
        // Collect type aliases from this module's IR plus all imported modules' IR
        // so the compilation-wide TypeAliases registry sees every alias before codegen.
        CollectTypeAliases(ir);
        foreach (var mod in compiledModules)
        {
            var modIr = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
            foreach (var def in modIr)
                CollectTypeAliases(def);
        }

        Log.Debug("Compilation: collected {AliasCount} type aliases", TypeAliases.All.Count());

        // Build imported module info for emitters — source-compiled modules (both backends)
        // Use AllIrDefinitions when available so internal helpers are included in IL emission
        var sourceImportedModules = compiledModules
            .Where(mod =>
                mod.PrecompiledAssemblyPath is null
                && (mod.AllIrDefinitions?.Count > 0 == true || mod.ExportedIrDefinitions.Count > 0)
            )
            .Select(mod =>
                (
                    NameConverter.ClassNameFromModuleName(mod.Name),
                    mod.AllIrDefinitions ?? mod.ExportedIrDefinitions
                )
            )
            .ToList();

        // Disambiguate colliding emitted identifiers (e.g. `this-function` vs
        // `ThisFunction`) consistently for both backends: stamps EmitName on module-level
        // defs/refs and alpha-renames colliding locals. Precompiled references are stamped
        // from each module's persisted rename map so they match the names in the DLL.
        var precompiledRenames = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is not null && mod.EmittedNames is not null)
            .GroupBy(mod => mod.Name)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g.First().EmittedNames!
            );
        var resolved = Ir.EmitNameResolver.Resolve(
            className,
            ir,
            sourceImportedModules,
            precompiledRenames
        );
        ir = resolved.CurrentIr;
        sourceImportedModules = resolved
            .ImportedModules.Select(m => (m.ClassName, m.Definitions))
            .ToList();

        // For C# backend: source-compiled modules only — precompiled types are
        // referenced from the DLL via using directives (no re-emission needed)
        var csImportedModules = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>(
            sourceImportedModules
        );

        Log.Debug(
            "CompileEmit: sourceImportedModules has {Count} modules: [{Names}]",
            sourceImportedModules.Count,
            string.Join(", ", sourceImportedModules.Select(m => m.Item1))
        );

        // Precompiled assemblies — referenced externally instead of inlining IR
        var precompiledAssemblyPaths = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is not null)
            .Select(mod => mod.PrecompiledAssemblyPath!)
            .Distinct()
            .ToList();

        // Always-on runtime support assembly (ZScheme.Runtime, the analogue of FSharp.Core).
        // Not a compiled module — it ships the ZSymbol type that emitted code references. Riding
        // it on the precompiled-assembly list gives both backends their reference for free: the
        // C# project generator emits a <Reference>, and the IL path copies it next to the output.
        var runtimeAssemblyPath = typeof(Runtime.ZSymbol).Assembly.Location;
        if (
            !string.IsNullOrEmpty(runtimeAssemblyPath)
            && !precompiledAssemblyPaths.Contains(runtimeAssemblyPath)
        )
            precompiledAssemblyPaths.Add(runtimeAssemblyPath);

        // Build func-to-module-class map for precompiled modules (emitters need qualified names).
        // The class name is qualified with the module's build namespace so the generated C#
        // resolves the precompiled type from a different namespace (e.g.
        // ZScheme.StdLib.Stdlib_OptionModule) without leaking `using` directives.
        var precompiledModuleMap = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is not null)
            .SelectMany(mod =>
            {
                var className = NameConverter.ClassNameFromModuleName(mod.Name);
                var qualified = mod.BuildNamespace is { Length: > 0 } bns
                    ? $"{bns}.{className}"
                    : className;
                return mod.ExportedNames.Select(name => (name, className: qualified));
            })
            .GroupBy(x => x.name)
            .ToDictionary(g => g.Key, g => g.First().className);

        // Flattened type renames exported by consumed precompiled modules (rawTypeName ->
        // emitted name), so references to a renamed precompiled type resolve to the name
        // baked into the DLL. Last writer wins, matching precompiledModuleMap's keying.
        var precompiledTypeRenames = compiledModules
            .Where(mod =>
                mod.PrecompiledAssemblyPath is not null && mod.TypeEmittedNames is not null
            )
            .SelectMany(mod => mod.TypeEmittedNames!)
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);

        // Maps precompiled module name -> its build namespace, for overload-resolved
        // references that route through the module name directly (CSharpEmitter.EmitVar).
        var precompiledModuleNamespaces = compiledModules
            .Where(mod =>
                mod.PrecompiledAssemblyPath is not null && mod.BuildNamespace is { Length: > 0 }
            )
            .GroupBy(mod => mod.Name)
            .ToDictionary(g => g.Key, g => g.First().BuildNamespace!);

        // Collect CLR namespace imports from lowering and source-imported modules
        // (not from precompiled modules, whose build namespaces should not leak
        // into the user's C# output as `using` directives).
        var clrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in compiledModules)
            if (mod.PrecompiledAssemblyPath is null)
                clrNamespaces.AddRange(mod.ExportedClrNamespaces);
        clrNamespaces = clrNamespaces.Distinct().ToList();

        if (_options.OutputMode == OutputMode.CSharp)
        {
            Log.Debug(
                "Compilation: constructing CSharpEmitter, namespace={Namespace}, className={ClassName}, usings={UsingCount}, importedModules={ImportedModuleCount}, precompiledMap={PrecompiledMapCount}",
                _options.Namespace,
                className,
                clrNamespaces.Count,
                csImportedModules.Count,
                precompiledModuleMap.Count
            );
            var emitter = new CSharpEmitter(
                _diagnostics,
                _options.Namespace,
                className,
                clrNamespaces,
                csImportedModules,
                precompiledModuleMap,
                isModule,
                suppressVersionPreamble,
                TypeAliases,
                precompiledModuleNamespaces,
                precompiledTypeRenames
            );
            var csCode = emitter.Emit(ir);
            Log.Debug("Stage 6 C# emit: {OutputLength} chars", csCode.Length);
            return new CompilationResult.CSharpOutputResult(
                _diagnostics,
                csCode,
                precompiledAssemblyPaths
            )
            {
                IsExecutable = emitter.HasEntryPoint,
            };
        }

        // IL backend
        // IL requires stack depth 0 at try-block entry. Hoist any with-handlers nested inside
        // compound expressions (binops, calls, etc.) up into let bindings. Applied to both
        // the main IR and each imported module's definitions. Awaits inside async state
        // machines have the same stack-depth requirement at suspension points.
        var hoister = new WithHandlersHoister();
        var awaitHoister = new AwaitHoister();
        var hoistedSourceImportedModules = sourceImportedModules
            .Select(m =>
                (
                    ClassName: m.Item1,
                    Definitions: (IReadOnlyList<IrNode>)
                        m.Item2.Select(hoister.Hoist).Select(awaitHoister.Hoist).ToList()
                )
            )
            .ToList();

        Log.Debug(
            "Compilation: constructing IlEmitter, namespace={Namespace}, className={ClassName}, usings={UsingCount}, importedModules={ImportedModuleCount}, precompiled={PrecompiledCount}",
            _options.Namespace,
            className,
            clrNamespaces.Count,
            hoistedSourceImportedModules.Count,
            precompiledAssemblyPaths.Count
        );
        var ilEmitter = new IlEmitter(
            _options.Namespace,
            _diagnostics,
            className,
            clrNamespaces,
            _options.AssemblySearchPaths,
            hoistedSourceImportedModules,
            precompiledAssemblyPaths,
            isModule: isModule,
            typeAliases: TypeAliases,
            precompiledTypeRenames: precompiledTypeRenames,
            coverage: _options.Coverage
        );
        var bytes = ilEmitter.Emit(ir);
        var hasEntryPoint = ilEmitter.HasEntryPoint;

        Log.Debug("Stage 6 IL emit: {OutputBytes} bytes", bytes?.Length ?? 0);
        if (bytes is null || _diagnostics.HasErrors)
            return new CompilationResult.IlOutputFailure(_diagnostics);
        return new CompilationResult.IlOutputResult(_diagnostics, bytes, precompiledAssemblyPaths)
        {
            IsExecutable = hasEntryPoint,
            FrameworkReferences = _options.FrameworkReferences,
        };
    }

    #endregion
}

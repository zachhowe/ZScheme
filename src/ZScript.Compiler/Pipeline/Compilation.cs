using System.Diagnostics;
using Serilog;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Cache;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Pipeline;

public sealed class Compilation(CompilerOptions? options = null)
{
    private readonly HashSet<string> _compilingModules = [];
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Dictionary<string, CompiledModule> _moduleCache = new();
    private readonly CompilerOptions _options = options ?? new CompilerOptions();

    private readonly PackageCacheManager? _packageCache = (options ?? new CompilerOptions()).UsePackageCache
        ? new PackageCacheManager()
        : null;

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
            return new CompilationResult.SExprParserFailure( _diagnostics);

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

        var resolver = CreateResolver(fileName);

        // Load explicitly specified precompiled packages
        var (explicitPrecompiled, precompiledAliases) = LoadExplicitPrecompiledPackages();
        Log.Debug("Precompiled packages: {Count} loaded", explicitPrecompiled.Count);
        foreach (var mod in explicitPrecompiled)
            if (!_moduleCache.ContainsKey(mod.Name))
            {
                _moduleCache[mod.Name] = mod;
                compiledModules.Add(mod);
            }

        // Register module aliases from precompiled packages (e.g., "zunit" → "zunit/zunit")
        foreach (var (alias, qualified) in precompiledAliases)
            resolver.AddModuleAlias(alias, qualified);

        // Load stdlib modules from package cache into _moduleCache (for import resolution).
        // Skip cache when --stdlib explicitly specifies a source path.
        if (_packageCache is not null && !_options.PackagePaths.ContainsKey("stdlib"))
        {
            var cachedPrelude = TryLoadPrecompiledModules("zscript-stdlib", "0.1.0");
            if (cachedPrelude is not null)
            {
                Log.Debug("Package cache hit: {ModuleCount} stdlib modules", cachedPrelude.Count);
                foreach (var mod in cachedPrelude)
                    if (!_moduleCache.ContainsKey(mod.Name))
                    {
                        _moduleCache[mod.Name] = mod;
                        // Auto-import prelude modules (unless this is a module or prelude is disabled)
                        if (!_options.DisablePrelude && !isPreludeModule
                                                     && _options.PreludeModules.Contains(mod.Name)
                                                     && !userImportNames.Contains(mod.Name))
                            compiledModules.Add(mod);
                    }
            }
            else
            {
                Log.Debug("Package cache miss for zscript-stdlib");
            }
        }

        // Compile prelude modules before user code (unless disabled or this is a prelude module itself)
        if (!_options.DisablePrelude && !isPreludeModule)
        {
            // Use a silent resolver for probing prelude modules — only search stdlib paths
            var silentDiag = new DiagnosticBag();
            var silentResolver = new ModuleResolver(silentDiag);
            if (_options.PackagePaths.TryGetValue("stdlib", out var silentStdlibDir))
            {
                silentResolver.AddPackagePath("stdlib", silentStdlibDir);
                silentResolver.AddSearchPath(silentStdlibDir);
            }

            // Also check the default stdlib location relative to compiler
            var compilerDir = Path.GetDirectoryName(typeof(Compilation).Assembly.Location);
            if (compilerDir is not null)
            {
                silentResolver.AddPackagePath("stdlib", Path.Combine(compilerDir, "stdlib"));
                silentResolver.AddSearchPath(Path.Combine(compilerDir, "stdlib"));
            }

            foreach (var preludeName in _options.PreludeModules)
            {
                if (userImportNames.Contains(preludeName))
                    continue; // User explicitly imports it
                if (_moduleCache.ContainsKey(preludeName))
                {
                    var cached = _moduleCache[preludeName];
                    if (!compiledModules.Contains(cached))
                        compiledModules.Add(cached);
                    continue;
                }

                var preludeResolved = silentResolver.Resolve(preludeName, SourceSpan.None);
                if (preludeResolved is null)
                    continue; // Prelude module not found — skip silently

                // Scan dependencies of this prelude module
                var preludeGraph = new ModuleGraph(_diagnostics);
                preludeGraph.AddModule(preludeName);
                ScanDependencies(preludeName, preludeResolved.Value.Source, preludeResolved.Value.Path, preludeGraph,
                    silentResolver);

                var preludeOrder = preludeGraph.TopologicalSort();
                if (preludeOrder is null) continue;

                // Use a prelude-specific resolver that only searches stdlib paths
                var preludeResolver = new ModuleResolver(_diagnostics);
                if (_options.PackagePaths.TryGetValue("stdlib", out var preludeStdlibDir))
                {
                    preludeResolver.AddPackagePath("stdlib", preludeStdlibDir);
                    preludeResolver.AddSearchPath(preludeStdlibDir);
                }

                if (compilerDir is not null)
                {
                    preludeResolver.AddPackagePath("stdlib", Path.Combine(compilerDir, "stdlib"));
                    preludeResolver.AddSearchPath(Path.Combine(compilerDir, "stdlib"));
                }

                foreach (var depName in preludeOrder)
                {
                    if (_moduleCache.ContainsKey(depName)) continue;
                    var compiled = CompileModule(depName, preludeResolver, SourceSpan.None);
                    if (compiled is null) continue;
                    _moduleCache[depName] = compiled;
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
        Log.Debug("Stage 3 AST: {FormCount} top-level forms in {ElapsedMs}ms", program.TopLevelForms.Count, sw.ElapsedMilliseconds);
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
            ? ClassNameCreator.ClassNameFromModuleName(moduleDecls[0].ModuleName)
            : "UnnamedModule";

        // Imports already resolved above
        var imports = AllTopLevelForms(program).OfType<AstNode.Import>().ToList();

        // Stage 4: Type inference — inject imported types first
        sw.Restart();
        var env = TypeEnv.CreateRoot();

        foreach (var mod in compiledModules)
        foreach (var (name, type) in mod.ExportedTypes)
            env.Define(name, type);

        var inferer = new TypeInferer(_diagnostics, _options.AssemblySearchPaths);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        Log.Debug("Stage 4 Type inference: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.TypeInfererFailure(_diagnostics);

        // Stage 5: Lower to IR — inject imported CLR bindings first
        sw.Restart();
        var lowering = new IrLowering(_diagnostics);

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

        var ir = lowering.Lower(program);
        Log.Debug("Stage 5 IR lowering: completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        if (_diagnostics.HasErrors)
            return new CompilationResult.IrLoweringFailure(_diagnostics);

        // Build imported module info for emitters — source-compiled modules (both backends)
        // Use AllIrDefinitions when available so internal helpers are included in IL emission
        var sourceImportedModules = compiledModules
            .Where(mod => mod.PrecompiledAssemblyPath is null && mod.ExportedIrDefinitions.Count > 0)
            .Select(mod => (ClassNameCreator.ClassNameFromModuleName(mod.Name),
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
            .SelectMany(mod => mod.ExportedNames.Select(name => (name, className: ClassNameCreator.ClassNameFromModuleName(mod.Name))))
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
            var emitter = new CSharpEmitter(_diagnostics, _options.Namespace, className, clrNamespaces,
                csImportedModules, precompiledAssemblyPaths, precompiledModuleMap,
                isModule: moduleDecls.Count > 0);
            var csCode = emitter.Emit(ir);
            Log.Debug("Stage 6 C# emit: {OutputLength} chars in {ElapsedMs}ms", csCode.Length, sw.ElapsedMilliseconds);
            Log.Debug("Compilation of {FileName} completed in {ElapsedMs}ms", fileName, compilationSw.ElapsedMilliseconds);
            return new CompilationResult.CSharpOutputResult(_diagnostics, csCode, precompiledAssemblyPaths);
        }

        // IL backend
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

    private ModuleResolver CreateResolver(string importingFilePath)
    {
        var resolver = new ModuleResolver(_diagnostics);

        // 1. Directory of the importing source file
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(importingFilePath));
        if (sourceDir is not null)
            resolver.AddSearchPath(sourceDir);

        // 2. Module search paths from package manifest / options
        foreach (var path in _options.ModuleSearchPaths)
            resolver.AddSearchPath(path);

        // 3. Register explicit package paths
        foreach (var (name, path) in _options.PackagePaths)
        {
            resolver.AddPackagePath(name, path);
            if (name == "stdlib")
                resolver.AddSearchPath(path);
        }

        // 4. Default: stdlib/ relative to the compiler executable
        var exeDir = Path.GetDirectoryName(typeof(Compilation).Assembly.Location);
        if (exeDir is not null)
        {
            resolver.AddPackagePath("stdlib", Path.Combine(exeDir, "stdlib"));
            resolver.AddSearchPath(Path.Combine(exeDir, "stdlib"));
        }

        // 5. Register module aliases (e.g., "zunit" → "zunit/zunit")
        foreach (var (alias, qualified) in _options.ModuleAliases)
            resolver.AddModuleAlias(alias, qualified);

        return resolver;
    }

    private static void ScanDependencies(string moduleName,
        string source,
        string filePath,
        ModuleGraph graph,
        ModuleResolver resolver,
        HashSet<string>? scanned = null)
    {
        scanned ??= new HashSet<string>();
        if (!scanned.Add(moduleName))
            return;

        // Quick parse to find import directives
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, diag);
        var tokens = lexer.Tokenize();
        if (diag.HasErrors) return;

        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        if (diag.HasErrors) return;

        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        foreach (var import in AllTopLevelForms(program).OfType<AstNode.Import>())
        {
            graph.AddModule(import.ModuleName);
            graph.AddDependency(moduleName, import.ModuleName, import.Span);

            var depResolved = resolver.Resolve(import.ModuleName, import.Span);
            if (depResolved is not null)
                ScanDependencies(import.ModuleName, depResolved.Value.Source, depResolved.Value.Path, graph, resolver,
                    scanned);
        }
    }

    private CompiledModule? CompileModule(string moduleName, ModuleResolver resolver, SourceSpan importSpan)
    {
        if (_moduleCache.TryGetValue(moduleName, out var cached))
        {
            Log.Debug("Module {ModuleName}: cache hit", moduleName);
            return cached;
        }

        if (!_compilingModules.Add(moduleName))
        {
            _diagnostics.Error($"Circular module dependency involving '{moduleName}'", importSpan);
            return null;
        }

        Log.Debug("Module {ModuleName}: compiling from source", moduleName);
        var moduleSw = Stopwatch.StartNew();

        var resolved = resolver.Resolve(moduleName, importSpan);
        if (resolved is null)
            return null;

        var (filePath, source) = resolved.Value;

        // Lex
        var modDiag = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, modDiag);
        var tokens = lexer.Tokenize();
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Parse
        var parser = new SExprParser(tokens, modDiag);
        var sexprs = parser.ParseAll();
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Pre-parse to find imports before macro expansion (macros may depend on imported macros)
        // Use a throwaway DiagnosticBag — define-syntax forms with brackets cause harmless errors
        var preDiag = new DiagnosticBag();
        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(sexprs);

        var transImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();
        var transModules = new List<CompiledModule>();

        foreach (var import in transImports)
        {
            var transMod = CompileModule(import.ModuleName, resolver, import.Span);
            if (transMod is null)
                return null;
            _moduleCache[import.ModuleName] = transMod;
            transModules.Add(transMod);
        }

        // Macro expansion — seed with macros from dependencies
        var modMacroEnv = MacroEnvironment.Default();
        foreach (var mod in transModules)
        foreach (var (name, macroDef) in mod.ExportedMacros)
            modMacroEnv.Define(name, macroDef);
        var modExpander = new MacroExpander(modDiag);
        sexprs = modExpander.ExpandAll(sexprs, modMacroEnv);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Build AST
        var astBuilder = new AstBuilder(modDiag);
        var program = astBuilder.BuildProgram(sexprs);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Type inference — inject transitive dependency types
        var env = TypeEnv.CreateRoot();
        foreach (var mod in transModules)
        foreach (var (name, type) in mod.ExportedTypes)
            env.Define(name, type);

        var inferer = new TypeInferer(modDiag, _options.AssemblySearchPaths);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Lower to IR — inject transitive CLR bindings
        var lowering = new IrLowering(modDiag);
        foreach (var mod in transModules)
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

        var ir = lowering.Lower(program);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Extract export declarations
        var exportDecls = AllTopLevelForms(program).OfType<AstNode.Export>().ToList();
        var exportedNameSpans = new Dictionary<string, SourceSpan>();
        foreach (var export in exportDecls)
        foreach (var name in export.Names)
            exportedNameSpans.TryAdd(name, export.Span);
        var exportedNames = exportedNameSpans.Keys.ToHashSet();

        // Build exported types — generalize type-parameter-like named types
        var exportedTypes = new Dictionary<string, ZType>();
        foreach (var name in exportedNames)
        {
            var type = env.Lookup(name);
            if (type is not null)
            {
                var resolvedType = inferer.Substitution.Apply(type);
                exportedTypes[name] = GeneralizeForExport(resolvedType);
            }
            else
            {
                _diagnostics.Warning($"Module '{moduleName}' exports '{name}' but it is not defined", exportedNameSpans[name]);
            }
        }

        // Build exported CLR imports (filter to exported names)
        var exportedClrImports =
            new Dictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
                IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)>();
        foreach (var (alias, clrInfo) in lowering.ClrImports)
            if (exportedNames.Contains(alias))
                exportedClrImports[alias] = clrInfo;

        // Build exported union/record constructors
        var exportedUnionCtors = new Dictionary<string, string>();
        foreach (var (caseName, unionName) in lowering.UnionCtors)
            if (exportedNames.Contains(caseName))
                exportedUnionCtors[caseName] = unionName;
        var exportedRecordCtors = new Dictionary<string, List<string>>();
        foreach (var (recordName, fieldNames) in lowering.RecordCtors)
            if (exportedNames.Contains(recordName))
                exportedRecordCtors[recordName] = fieldNames;

        // Auto-export record field accessors (RecordName/fieldName) when the record is exported
        foreach (var (recordName, fieldNames) in exportedRecordCtors)
        foreach (var fieldName in fieldNames)
        {
            var accessorName = $"{recordName}/{fieldName}";
            exportedNames.Add(accessorName);
            var type = env.Lookup(accessorName);
            if (type is not null)
                exportedTypes[accessorName] = GeneralizeForExport(inferer.Substitution.Apply(type));
        }

        // Build exported IR definitions (filter to exported names)
        var exportedIrDefs = new List<IrNode>();
        CollectExportedIrDefs(ir, exportedNames, exportedIrDefs);

        // Build all IR definitions (for library compilation, which needs internal helpers too)
        var allIrDefs = new List<IrNode>();
        CollectAllIrDefs(ir, allIrDefs);

        _compilingModules.Remove(moduleName);

        // Collect CLR namespace imports from this module and its transitive deps
        var exportedClrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in transModules)
            exportedClrNamespaces.AddRange(mod.ExportedClrNamespaces);
        exportedClrNamespaces = exportedClrNamespaces.Distinct().ToList();

        // Build exported macros (filter to exported names + all user-defined macros)
        var exportedMacros = new Dictionary<string, MacroDefinition>();
        foreach (var (name, macroDef) in modMacroEnv.OwnMacros)
            if (exportedNames.Contains(name))
                exportedMacros[name] = macroDef;

        Log.Debug("Module {ModuleName}: compiled in {ElapsedMs}ms ({ExportCount} exports)",
            moduleName, moduleSw.ElapsedMilliseconds, exportedNames.Count);

        return new CompiledModule(
            moduleName,
            filePath,
            exportedNames,
            exportedTypes,
            exportedClrImports,
            exportedIrDefs,
            exportedClrNamespaces,
            exportedMacros,
            exportedUnionCtors,
            exportedRecordCtors,
            AllIrDefinitions: allIrDefs
        );
    }

    private static void CollectExportedIrDefs(IrNode node, HashSet<string> exportedNames, List<IrNode> result)
    {
        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                CollectExportedIrDefs(child, exportedNames, result);
        else if (node is IrNode.FuncDef funcDef && exportedNames.Contains(funcDef.Name))
            result.Add(funcDef);
        else if (node is IrNode.Let let)
        {
            if (exportedNames.Contains(let.VarName))
                result.Add(let);
            // Always recurse into Let.Body — exported definitions can be nested
            // inside non-exported Let bindings (e.g. module-level defines)
            CollectExportedIrDefs(let.Body, exportedNames, result);
        }
        else if (node is IrNode.UnionDecl unionDecl && exportedNames.Contains(unionDecl.Name))
            result.Add(unionDecl);
        else if (node is IrNode.RecordDecl recordDecl && exportedNames.Contains(recordDecl.Name))
            result.Add(recordDecl);
    }

    private static void CollectAllIrDefs(IrNode node, List<IrNode> result)
    {
        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                CollectAllIrDefs(child, result);
        else if (node is IrNode.FuncDef or IrNode.UnionDecl or IrNode.RecordDecl)
            result.Add(node);
        else if (node is IrNode.Let let)
        {
            result.Add(let);
            // Recurse into Let.Body to find definitions nested inside
            // module-level Let bindings (e.g. functions defined after a top-level define)
            CollectAllIrDefs(let.Body, result);
        }
    }

    /// <summary>
    ///     Converts named types that look like type parameters (single lowercase letters)
    ///     into proper ForAll-wrapped type variables for cross-module use.
    ///     e.g. Fn(a, a) → ForAll([1000], Fn(tv1000, tv1000))
    /// </summary>
    private static ZType GeneralizeForExport(ZType type)
    {
        var typeParamNames = new HashSet<string>();
        CollectTypeParamNames(type, typeParamNames);

        if (typeParamNames.Count == 0)
            return type;

        var nextId = 1000;
        var mapping = new Dictionary<string, int>();
        foreach (var name in typeParamNames.OrderBy(n => n))
            mapping[name] = nextId++;

        var replaced = ReplaceTypeParamNames(type, mapping);
        return new ZType.ZForAllType(mapping.Values.ToList(), replaced);
    }

    private static void CollectTypeParamNames(ZType type, HashSet<string> names)
    {
        switch (type)
        {
            case ZType.ZNamedType { TypeArgs.Count: 0 } nt when IsTypeParamName(nt.Name):
                names.Add(nt.Name);
                break;
            case ZType.ZFuncType ft:
                foreach (var p in ft.Params) CollectTypeParamNames(p, names);
                CollectTypeParamNames(ft.Return, names);
                break;
            case ZType.ZNamedType nt:
                foreach (var a in nt.TypeArgs) CollectTypeParamNames(a, names);
                break;
            case ZType.ZForAllType fa:
                CollectTypeParamNames(fa.Body, names);
                break;
        }
    }

    private static bool IsTypeParamName(string name)
    {
        return name.Length == 1 && char.IsLower(name[0]);
    }

    private static ZType ReplaceTypeParamNames(ZType type, Dictionary<string, int> mapping)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt when mapping.TryGetValue(nt.Name, out var id) =>
                new ZType.ZTypeVar(id),
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => ReplaceTypeParamNames(p, mapping)).ToList(),
                    ReplaceTypeParamNames(ft.Return, mapping)),
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(a => ReplaceTypeParamNames(a, mapping)).ToList()),
            ZType.ZForAllType fa =>
                new ZType.ZForAllType(fa.BoundVars, ReplaceTypeParamNames(fa.Body, mapping)),
            _ => type
        };
    }

    /// <summary>
    ///     Attempts to load modules from a precompiled package in the cache.
    ///     Returns CompiledModule records with type declarations from metadata
    ///     and PrecompiledAssemblyPath set. Function IR lives in the .dll.
    /// </summary>
    private List<CompiledModule>? TryLoadPrecompiledModules(string packageName, string version)
    {
        if (_packageCache is null)
            return null;

        var package = _packageCache.TryLoad(packageName, version);
        if (package is null)
            return null;

        var result = new List<CompiledModule>();
        foreach (var (moduleName, info) in package.Modules)
        {
            // Use type declarations from metadata (if available) instead of empty list
            var irDefs = info.TypeDeclarations ?? [];

            var compiled = new CompiledModule(
                info.Name,
                package.AssemblyPath,
                info.ExportedNames,
                info.ExportedTypes,
                info.ExportedClrImports,
                irDefs,
                info.ExportedClrNamespaces,
                info.ExportedMacros ?? new Dictionary<string, MacroDefinition>(),
                info.ExportedUnionCtors,
                info.ExportedRecordCtors,
                package.AssemblyPath // PrecompiledAssemblyPath
            );
            result.Add(compiled);
        }

        return result;
    }

    /// <summary>
    ///     Tries to load precompiled modules from explicit .dll paths in compiler options.
    /// </summary>
    private (List<CompiledModule> Modules, Dictionary<string, string> Aliases) LoadExplicitPrecompiledPackages()
    {
        var result = new List<CompiledModule>();
        var aliases = new Dictionary<string, string>();
        foreach (var dllPath in _options.PrecompiledPackagePaths)
        {
            if (!File.Exists(dllPath))
                continue;

            var metadataPath = Path.ChangeExtension(dllPath, ".metadata.json");
            if (!File.Exists(metadataPath))
                continue;

            var json = File.ReadAllText(metadataPath);
            var package = MetadataSerializer.Deserialize(json, dllPath);
            if (package is null)
                continue;

            // Register module alias from package metadata (e.g., "zunit" → "zunit/zunit")
            if (package.ImportPrefix is not null && package.DefaultModule is not null)
                aliases[package.ImportPrefix] = $"{package.ImportPrefix}/{package.DefaultModule}";

            foreach (var (moduleName, info) in package.Modules)
            {
                var irDefs = info.TypeDeclarations ?? [];

                var compiled = new CompiledModule(
                    info.Name,
                    package.AssemblyPath,
                    info.ExportedNames,
                    info.ExportedTypes,
                    info.ExportedClrImports,
                    irDefs,
                    info.ExportedClrNamespaces,
                    info.ExportedMacros ?? new Dictionary<string, MacroDefinition>(),
                    info.ExportedUnionCtors,
                    info.ExportedRecordCtors,
                    package.AssemblyPath
                );
                result.Add(compiled);
            }
        }

        return (result, aliases);
    }

    /// <summary>
    ///     Injects a pre-compiled module into this compilation's cache so it's available
    ///     to subsequent compilations without recompiling from source.
    /// </summary>
    public void InjectModule(string name, CompiledModule module)
    {
        _moduleCache[name] = module;
    }

    /// <summary>
    ///     Compiles a single module from source and returns the CompiledModule.
    ///     Used by LibraryCompiler for building library packages.
    /// </summary>
    public CompiledModule? CompileAsModule(string moduleName, string source, string filePath)
    {
        var resolver = CreateResolver(filePath);
        // First inject the source so the resolver can find it
        // Actually, since this is standalone source, we compile directly
        var modDiag = new DiagnosticBag();

        // Lex
        var lexer = new Lexer(source, filePath, modDiag);
        var tokens = lexer.Tokenize();
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Parse
        var parser = new SExprParser(tokens, modDiag);
        var sexprs = parser.ParseAll();
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Pre-parse for imports
        var preDiag = new DiagnosticBag();
        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(sexprs);

        var transImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();
        var transModules = new List<CompiledModule>();

        foreach (var import in transImports)
        {
            var importName = resolver.ResolveAlias(import.ModuleName);
            if (_moduleCache.TryGetValue(importName, out var existing))
            {
                transModules.Add(existing);
                continue;
            }

            var transMod = CompileModule(importName, resolver, import.Span);
            if (transMod is null) return null;
            _moduleCache[importName] = transMod;
            transModules.Add(transMod);
        }

        // Macro expansion
        var modMacroEnv = MacroEnvironment.Default();
        foreach (var mod in transModules)
        foreach (var (name, macroDef) in mod.ExportedMacros)
            modMacroEnv.Define(name, macroDef);
        var modExpander = new MacroExpander(modDiag);
        sexprs = modExpander.ExpandAll(sexprs, modMacroEnv);
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Build AST
        var astBuilder = new AstBuilder(modDiag);
        var program = astBuilder.BuildProgram(sexprs);
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Type inference
        var env = TypeEnv.CreateRoot();
        foreach (var mod in transModules)
        foreach (var (name, type) in mod.ExportedTypes)
            env.Define(name, type);

        var inferer = new TypeInferer(modDiag, _options.AssemblySearchPaths);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Lower to IR
        var lowering = new IrLowering(modDiag);
        foreach (var mod in transModules)
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

        var ir = lowering.Lower(program);
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Extract exports
        var exportDecls = AllTopLevelForms(program).OfType<AstNode.Export>().ToList();
        var exportedNames = new HashSet<string>();
        foreach (var export in exportDecls)
        foreach (var n in export.Names)
            exportedNames.Add(n);

        var exportedTypes = new Dictionary<string, ZType>();
        foreach (var n in exportedNames)
        {
            var type = env.Lookup(n);
            if (type is not null)
                exportedTypes[n] = GeneralizeForExport(inferer.Substitution.Apply(type));
        }

        var exportedClrImports =
            new Dictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
                IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)>();
        foreach (var (alias, clrInfo) in lowering.ClrImports)
            if (exportedNames.Contains(alias))
                exportedClrImports[alias] = clrInfo;

        var exportedUnionCtors = new Dictionary<string, string>();
        foreach (var (caseName, unionName) in lowering.UnionCtors)
            if (exportedNames.Contains(caseName))
                exportedUnionCtors[caseName] = unionName;

        var exportedRecordCtors = new Dictionary<string, List<string>>();
        foreach (var (recordName, fieldNames) in lowering.RecordCtors)
            if (exportedNames.Contains(recordName))
                exportedRecordCtors[recordName] = fieldNames;

        // Auto-export record field accessors (RecordName/fieldName) when the record is exported
        foreach (var (recordName, fieldNames) in exportedRecordCtors)
        foreach (var fieldName in fieldNames)
        {
            var accessorName = $"{recordName}/{fieldName}";
            exportedNames.Add(accessorName);
            var type = env.Lookup(accessorName);
            if (type is not null)
                exportedTypes[accessorName] = GeneralizeForExport(inferer.Substitution.Apply(type));
        }

        var exportedIrDefs = new List<IrNode>();
        CollectExportedIrDefs(ir, exportedNames, exportedIrDefs);

        var allIrDefs = new List<IrNode>();
        CollectAllIrDefs(ir, allIrDefs);

        var exportedClrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in transModules)
            exportedClrNamespaces.AddRange(mod.ExportedClrNamespaces);
        exportedClrNamespaces = exportedClrNamespaces.Distinct().ToList();

        var exportedMacros = new Dictionary<string, MacroDefinition>();
        foreach (var (name, macroDef) in modMacroEnv.OwnMacros)
            if (exportedNames.Contains(name))
                exportedMacros[name] = macroDef;

        return new CompiledModule(
            moduleName, filePath, exportedNames, exportedTypes, exportedClrImports,
            exportedIrDefs, exportedClrNamespaces, exportedMacros,
            exportedUnionCtors, exportedRecordCtors,
            AllIrDefinitions: allIrDefs);
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

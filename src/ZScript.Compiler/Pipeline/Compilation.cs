namespace ZScript.Compiler.Pipeline;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed class Compilation(CompilerOptions? options = null)
{
    private readonly CompilerOptions _options = options ?? new CompilerOptions();
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Dictionary<string, CompiledModule> _moduleCache = new();
    private readonly HashSet<string> _compilingModules = [];

    private static IEnumerable<AstNode> AllTopLevelForms(AstNode.Program program) =>
        program.TopLevelForms.SelectMany(f => f is AstNode.ModuleDecl m
            ? new AstNode[] { f }.Concat(m.Body)
            : [f]);

    public CompilationResult Compile(string source, string fileName = "input.zs")
    {
        // Stage 1: Lex
        var lexer = new Lexer(source, fileName, _diagnostics);
        var tokens = lexer.Tokenize();
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 2: Parse S-expressions
        var parser = new SExprParser(tokens, _diagnostics);
        var sexprs = parser.ParseAll();
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Pre-parse to discover imports (before macro expansion)
        var preDiag = new DiagnosticBag();
        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(sexprs);

        // Resolve module imports early so macros from dependencies are available
        var preImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();
        var compiledModules = new List<CompiledModule>();

        if (preImports.Count > 0)
        {
            var resolver = CreateResolver(fileName);
            var graph = new ModuleGraph(_diagnostics);

            foreach (var import in preImports)
            {
                graph.AddModule(import.ModuleName);
                var resolved = resolver.Resolve(import.ModuleName);
                if (resolved is null)
                    continue;

                ScanDependencies(import.ModuleName, resolved.Value.Source, resolved.Value.Path, graph, resolver);
            }

            if (_diagnostics.HasErrors)
                return new CompilationResult(null, _diagnostics);

            var order = graph.TopologicalSort();
            if (order is null)
                return new CompilationResult(null, _diagnostics);

            foreach (var moduleName in order)
            {
                if (_moduleCache.ContainsKey(moduleName))
                    continue;

                var compiled = CompileModule(moduleName, resolver);
                if (compiled is null)
                    return new CompilationResult(null, _diagnostics);

                _moduleCache[moduleName] = compiled;
            }

            foreach (var import in preImports)
            {
                if (_moduleCache.TryGetValue(import.ModuleName, out var mod))
                    compiledModules.Add(mod);
            }
        }

        // Stage 2.5: Macro expansion — seed with macros from imported modules
        var macroEnv = MacroEnvironment.Default();
        foreach (var mod in compiledModules)
        {
            foreach (var (name, macroDef) in mod.ExportedMacros)
                macroEnv.Define(name, macroDef);
        }
        var expander = new MacroExpander(_diagnostics);
        sexprs = expander.ExpandAll(sexprs, macroEnv);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 3: Build AST
        var astBuilder = new AstBuilder(_diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Extract namespace directive (if present) — source overrides options
        var nsDecls = AllTopLevelForms(program).OfType<AstNode.NamespaceDecl>().ToList();
        if (nsDecls.Count > 1)
            _diagnostics.Warning("Multiple namespace declarations; using the first one", nsDecls[1].Span);
        if (nsDecls.Count > 0)
            _options.Namespace = nsDecls[0].NsName;

        // Extract module name (if present) — convert to PascalCase class name
        var moduleDecls = AllTopLevelForms(program).OfType<AstNode.ModuleDecl>().ToList();
        if (moduleDecls.Count > 1)
            _diagnostics.Warning("Multiple module declarations; using the first one", moduleDecls[1].Span);
        var className = moduleDecls.Count > 0
            ? ModuleNameToClassName(moduleDecls[0].ModuleName)
            : "Program";

        // Imports already resolved above
        var imports = AllTopLevelForms(program).OfType<AstNode.Import>().ToList();

        // Stage 4: Type inference — inject imported types first
        var env = TypeEnv.CreateRoot();

        foreach (var mod in compiledModules)
        {
            foreach (var (name, type) in mod.ExportedTypes)
                env.Define(name, type);
        }

        var inferer = new TypeInferer(_diagnostics, _options.AssemblySearchPaths);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 5: Lower to IR — inject imported CLR bindings first
        var lowering = new IrLowering(_diagnostics);

        foreach (var mod in compiledModules)
        {
            foreach (var (alias, (typeName, methodName)) in mod.ExportedClrImports)
                lowering.RegisterClrImport(alias, typeName, methodName);
        }

        var ir = lowering.Lower(program);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Build imported module info for emitters (instead of merging into main IR)
        var importedModules = compiledModules
            .Where(mod => mod.ExportedIrDefinitions.Count > 0)
            .Select(mod => (ModuleNameToClassName(mod.Name), mod.ExportedIrDefinitions))
            .ToList();

        // Collect CLR namespace imports from lowering and compiled modules
        var clrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in compiledModules)
            clrNamespaces.AddRange(mod.ExportedClrNamespaces);

        // Stage 6: Code generation
        if (_options.OutputMode == OutputMode.CSharp)
        {
            var emitter = new CSharpEmitter(_options.Namespace, className, clrNamespaces, importedModules);
            var csCode = emitter.Emit(ir);
            return new CompilationResult(csCode, _diagnostics);
        }

        // IL backend
        var ilEmitter = new IlEmitter(_options.Namespace, _diagnostics, className, clrNamespaces, _options.AssemblySearchPaths, importedModules);
        var bytes = ilEmitter.Emit(ir);
        if (bytes is null || _diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);
        return new CompilationResult(null, _diagnostics)
        {
            OutputBytes = bytes,
            IsExecutable = ilEmitter.HasEntryPoint
        };
    }

    private ModuleResolver CreateResolver(string importingFilePath)
    {
        var resolver = new ModuleResolver(_diagnostics);

        // 1. Directory of the importing source file
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(importingFilePath));
        if (sourceDir is not null)
            resolver.AddSearchPath(sourceDir);

        // 2. Explicit --stdlib path
        if (_options.StdLibPath is not null)
            resolver.AddSearchPath(_options.StdLibPath);

        // 3. Default: stdlib/ relative to the compiler executable
        var exeDir = Path.GetDirectoryName(typeof(Compilation).Assembly.Location);
        if (exeDir is not null)
            resolver.AddSearchPath(Path.Combine(exeDir, "stdlib"));

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
            graph.AddDependency(moduleName, import.ModuleName);

            var depResolved = resolver.Resolve(import.ModuleName);
            if (depResolved is not null)
                ScanDependencies(import.ModuleName, depResolved.Value.Source, depResolved.Value.Path, graph, resolver, scanned);
        }
    }

    private CompiledModule? CompileModule(string moduleName, ModuleResolver resolver)
    {
        if (_moduleCache.TryGetValue(moduleName, out var cached))
            return cached;

        if (!_compilingModules.Add(moduleName))
        {
            _diagnostics.Error($"Circular module dependency involving '{moduleName}'", SourceSpan.None);
            return null;
        }

        var resolved = resolver.Resolve(moduleName);
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
            var transMod = CompileModule(import.ModuleName, resolver);
            if (transMod is null)
                return null;
            _moduleCache[import.ModuleName] = transMod;
            transModules.Add(transMod);
        }

        // Macro expansion — seed with macros from dependencies
        var modMacroEnv = MacroEnvironment.Default();
        foreach (var mod in transModules)
        {
            foreach (var (name, macroDef) in mod.ExportedMacros)
                modMacroEnv.Define(name, macroDef);
        }
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
        {
            foreach (var (name, type) in mod.ExportedTypes)
                env.Define(name, type);
        }

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
            foreach (var (alias, (typeName, methodName)) in mod.ExportedClrImports)
                lowering.RegisterClrImport(alias, typeName, methodName);
        }

        var ir = lowering.Lower(program);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Extract export declarations
        var exportDecls = AllTopLevelForms(program).OfType<AstNode.Export>().ToList();
        var exportedNames = new HashSet<string>();
        foreach (var export in exportDecls)
        {
            foreach (var name in export.Names)
                exportedNames.Add(name);
        }

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
                _diagnostics.Warning($"Module '{moduleName}' exports '{name}' but it is not defined", SourceSpan.None);
            }
        }

        // Build exported CLR imports (filter to exported names)
        var exportedClrImports = new Dictionary<string, (string TypeName, string MethodName)>();
        foreach (var (alias, clrInfo) in lowering.ClrImports)
        {
            if (exportedNames.Contains(alias))
                exportedClrImports[alias] = clrInfo;
        }

        // Build exported IR definitions (filter to exported names)
        var exportedIrDefs = new List<IrNode>();
        CollectExportedIrDefs(ir, exportedNames, exportedIrDefs);

        _compilingModules.Remove(moduleName);

        // Collect CLR namespace imports from this module and its transitive deps
        var exportedClrNamespaces = new List<string>(lowering.ClrNamespaces);
        foreach (var mod in transModules)
            exportedClrNamespaces.AddRange(mod.ExportedClrNamespaces);

        // Build exported macros (filter to exported names + all user-defined macros)
        var exportedMacros = new Dictionary<string, MacroDefinition>();
        foreach (var (name, macroDef) in modMacroEnv.OwnMacros)
        {
            if (exportedNames.Contains(name))
                exportedMacros[name] = macroDef;
        }

        return new CompiledModule(
            moduleName,
            filePath,
            exportedNames,
            exportedTypes,
            exportedClrImports,
            exportedIrDefs,
            exportedClrNamespaces,
            exportedMacros
        );
    }

    private static IrNode MergeImportedIr(IrNode mainIr, List<CompiledModule> modules)
    {
        var importedDefs = new List<IrNode>();
        foreach (var mod in modules)
            importedDefs.AddRange(mod.ExportedIrDefinitions);

        if (importedDefs.Count == 0)
            return mainIr;

        if (mainIr is IrNode.Seq seq)
        {
            var merged = new List<IrNode>(importedDefs);
            merged.AddRange(seq.Nodes);
            return new IrNode.Seq(merged) { Type = seq.Type };
        }

        var nodes = new List<IrNode>(importedDefs) { mainIr };
        return new IrNode.Seq(nodes) { Type = mainIr.Type };
    }

    private static void CollectExportedIrDefs(IrNode node, HashSet<string> exportedNames, List<IrNode> result)
    {
        if (node is IrNode.Seq seq)
        {
            foreach (var child in seq.Nodes)
                CollectExportedIrDefs(child, exportedNames, result);
        }
        else if (node is IrNode.FuncDef funcDef && exportedNames.Contains(funcDef.Name))
        {
            result.Add(funcDef);
        }
        else if (node is IrNode.Let let && exportedNames.Contains(let.VarName))
        {
            result.Add(let);
        }
    }

    /// <summary>
    /// Converts named types that look like type parameters (single lowercase letters)
    /// into proper ForAll-wrapped type variables for cross-module use.
    /// e.g. Fn(a, a) → ForAll([1000], Fn(tv1000, tv1000))
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

    private static bool IsTypeParamName(string name) =>
        name.Length == 1 && char.IsLower(name[0]);

    private static ZType ReplaceTypeParamNames(ZType type, Dictionary<string, int> mapping) => type switch
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

    private static string ModuleNameToClassName(string moduleName) =>
        string.Concat(
            moduleName.Split('/', '-')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    private void CopyDiagnostics(DiagnosticBag source)
    {
        _diagnostics.AddRange(source);
    }
}

public sealed record CompilationResult(string? Output, DiagnosticBag Diagnostics)
{
    public byte[]? OutputBytes { get; init; }
    public bool IsExecutable { get; init; }
    public bool Success => !Diagnostics.HasErrors && (Output is not null || OutputBytes is not null);
}

namespace ZScript.Compiler.Pipeline;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed class Compilation
{
    private readonly CompilerOptions _options;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Dictionary<string, CompiledModule> _moduleCache = new();
    private readonly HashSet<string> _compilingModules = new();

    public Compilation(CompilerOptions? options = null)
    {
        _options = options ?? new CompilerOptions();
    }

    public DiagnosticBag Diagnostics => _diagnostics;

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

        // Stage 3: Build AST
        var astBuilder = new AstBuilder(_diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Extract namespace directive (if present) — source overrides options
        var nsDecls = program.TopLevelForms.OfType<AstNode.NamespaceDecl>().ToList();
        if (nsDecls.Count > 1)
            _diagnostics.Warning("Multiple namespace declarations; using the first one", nsDecls[1].Span);
        if (nsDecls.Count > 0)
            _options.Namespace = nsDecls[0].NsName;

        // Extract module name (if present) — convert to PascalCase class name
        var moduleDecls = program.TopLevelForms.OfType<AstNode.ModuleDecl>().ToList();
        if (moduleDecls.Count > 1)
            _diagnostics.Warning("Multiple module declarations; using the first one", moduleDecls[1].Span);
        var className = moduleDecls.Count > 0
            ? ModuleNameToClassName(moduleDecls[0].ModuleName)
            : "Program";

        // Stage 3.5: Resolve module imports
        var imports = program.TopLevelForms.OfType<AstNode.Import>().ToList();
        var compiledModules = new List<CompiledModule>();

        if (imports.Count > 0)
        {
            var resolver = CreateResolver(fileName);
            var graph = new ModuleGraph(_diagnostics);

            foreach (var import in imports)
            {
                graph.AddModule(import.ModuleName);
                var resolved = resolver.Resolve(import.ModuleName);
                if (resolved is null)
                    continue;

                // Scan the dependency for its own imports to build the graph
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

            // Collect only the directly imported modules
            foreach (var import in imports)
            {
                if (_moduleCache.TryGetValue(import.ModuleName, out var mod))
                    compiledModules.Add(mod);
            }
        }

        // Stage 4: Type inference — inject imported types first
        var env = TypeEnv.CreateRoot();

        foreach (var mod in compiledModules)
        {
            foreach (var (name, type) in mod.ExportedTypes)
                env.Define(name, type);
        }

        var inferer = new TypeInferer(_diagnostics);
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

        // Merge imported IR definitions (pure ZScript defs) into the output
        ir = MergeImportedIr(ir, compiledModules);

        // Stage 6: Code generation
        if (_options.OutputMode == OutputMode.CSharp)
        {
            var emitter = new CSharpEmitter(_options.Namespace, className);
            var csCode = emitter.Emit(ir);
            var typeDecls = emitter.EmitTypeDeclarations(ir);
            return new CompilationResult(typeDecls + csCode, _diagnostics);
        }

        // IL backend
        var ilEmitter = new IlEmitter(_options.Namespace, _diagnostics, className);
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

    private void ScanDependencies(string moduleName, string source, string filePath, ModuleGraph graph, ModuleResolver resolver, HashSet<string>? scanned = null)
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

        foreach (var import in program.TopLevelForms.OfType<AstNode.Import>())
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

        // Build AST
        var astBuilder = new AstBuilder(modDiag);
        var program = astBuilder.BuildProgram(sexprs);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Handle transitive imports
        var transImports = program.TopLevelForms.OfType<AstNode.Import>().ToList();
        var transModules = new List<CompiledModule>();

        foreach (var import in transImports)
        {
            var transMod = CompileModule(import.ModuleName, resolver);
            if (transMod is null)
                return null;
            _moduleCache[import.ModuleName] = transMod;
            transModules.Add(transMod);
        }

        // Type inference — inject transitive dependency types
        var env = TypeEnv.CreateRoot();
        foreach (var mod in transModules)
        {
            foreach (var (name, type) in mod.ExportedTypes)
                env.Define(name, type);
        }

        var inferer = new TypeInferer(modDiag);
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
        var exportDecls = program.TopLevelForms.OfType<AstNode.Export>().ToList();
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
        if (ir is IrNode.Seq seq)
        {
            foreach (var node in seq.Nodes)
            {
                if (node is IrNode.FuncDef funcDef && exportedNames.Contains(funcDef.Name))
                    exportedIrDefs.Add(funcDef);
                else if (node is IrNode.Let let && exportedNames.Contains(let.VarName))
                    exportedIrDefs.Add(let);
            }
        }

        _compilingModules.Remove(moduleName);

        return new CompiledModule(
            moduleName,
            filePath,
            exportedNames,
            exportedTypes,
            exportedClrImports,
            exportedIrDefs
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

        int nextId = 1000;
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

    internal static string ModuleNameToClassName(string moduleName) =>
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

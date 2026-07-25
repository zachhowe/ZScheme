using System.Diagnostics;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    private CompiledModule? CompileModule(
        string moduleName,
        ModuleResolver resolver,
        SourceSpan importSpan
    )
    {
        // Canonicalize through the package alias table (e.g. "http" → "http/http") before anything
        // keys off the name. A module reached under both its alias and its target would otherwise
        // compile twice — once per spelling — and every function it exports would join the overload
        // set twice under two different qualified names (see TypeEnv.DefineImportedBinding).
        moduleName = resolver.ResolveAlias(moduleName);

        if (_moduleCache.TryGetValue(moduleName, out var cached))
        {
            Log.Debug("Module {ModuleName}: cache hit", moduleName);
            return cached;
        }

        if (_failedModules.Contains(moduleName))
        {
            Log.Debug("Module {ModuleName}: previously failed, short-circuiting", moduleName);
            return null;
        }

        if (!_compilingModules.Add(moduleName))
        {
            Log.Debug(
                "Module {ModuleName}: circular dependency detected, currently compiling: [{Compiling}]",
                moduleName,
                string.Join(", ", _compilingModules)
            );
            _diagnostics.Error($"Circular module dependency involving '{moduleName}'", importSpan);
            return null;
        }

        CompiledModule? Fail()
        {
            _failedModules.Add(moduleName);
            return null;
        }

        try
        {
            Log.Debug("Module {ModuleName}: compiling from source", moduleName);
            var moduleSw = Stopwatch.StartNew();

            var resolved = resolver.Resolve(moduleName, importSpan);
            if (resolved is null)
                return Fail();

            var (filePath, source) = resolved.Value;
            Log.Debug(
                "Module {ModuleName}: resolved to {FilePath} ({SourceLength} chars)",
                moduleName,
                filePath,
                source.Length
            );

            // Lex
            var modDiag = new DiagnosticBag();
            var lexer = new Lexer(source, filePath, modDiag);
            var tokens = lexer.Tokenize();
            Log.Debug("Module {ModuleName}: lex {TokenCount} tokens", moduleName, tokens.Count);
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Parse
            var parser = new SExprParser(tokens, modDiag);
            var sexprs = parser.ParseAll();
            Log.Debug(
                "Module {ModuleName}: parse {SExprCount} s-expressions",
                moduleName,
                sexprs.Count
            );
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Pre-parse to find imports before macro expansion (macros may depend on imported macros)
            // Use a throwaway DiagnosticBag — define-syntax forms with brackets cause harmless errors
            var preDiag = new DiagnosticBag();
            var preBuilder = new AstBuilder(preDiag);
            var preProgram = preBuilder.BuildProgram(sexprs);

            var transImports = AllTopLevelForms(preProgram).OfType<AstNode.Import>().ToList();
            var transModules = new List<CompiledModule>();
            if (transImports.Count > 0)
                Log.Debug(
                    "Module {ModuleName}: {TransCount} transitive imports: [{ImportNames}]",
                    moduleName,
                    transImports.Count,
                    string.Join(", ", transImports.Select(i => i.ModuleName))
                );

            foreach (var import in transImports)
            {
                var transMod = CompileModule(import.ModuleName, resolver, import.Span);
                if (transMod is null)
                    return Fail();
                // Key the cache by the canonical name, not the spelling this import used, so an
                // alias and its target never occupy two entries for the same module.
                _moduleCache[resolver.ResolveAlias(import.ModuleName)] = transMod;
                transModules.Add(transMod);
            }

            // Macro expansion — seed with macros from dependencies
            var modMacroEnv = MacroEnvironment.Default();
            foreach (var mod in transModules)
            foreach (var (name, macroDef) in mod.ExportedMacros)
                modMacroEnv.Define(name, macroDef);
            var transMacroCount = transModules.Sum(m => m.ExportedMacros.Count);
            if (transMacroCount > 0)
                Log.Debug(
                    "Module {ModuleName}: seeded {MacroCount} macros from {DepCount} dependencies",
                    moduleName,
                    transMacroCount,
                    transModules.Count
                );
            var modExpander = new MacroExpander(modDiag);
            sexprs = modExpander.ExpandAll(sexprs, modMacroEnv);
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Build AST
            var astBuilder = new AstBuilder(modDiag);
            var program = astBuilder.BuildProgram(sexprs);
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Pre-pass: collect type aliases from this module's AST and from imported modules'
            // IR so the registry is populated before type inference.
            CollectTypeAliasesFromAst(program);
            foreach (var mod in transModules)
            {
                var modIr = mod.AllIrDefinitions ?? mod.ExportedIrDefinitions;
                foreach (var def in modIr)
                    CollectTypeAliases(def);
            }

            // Type inference — inject transitive dependency types
            var env = TypeEnv.CreateRoot();
            foreach (var mod in transModules)
            foreach (var (name, type) in mod.ExportedTypes)
                env.DefineImportedBinding(mod.Name, name, type);
            var transTypeCount = transModules.Sum(m => m.ExportedTypes.Count);
            if (transTypeCount > 0)
                Log.Debug(
                    "Module {ModuleName}: injected {TypeCount} types from dependencies",
                    moduleName,
                    transTypeCount
                );

            // Collect CLR namespaces from transitive dependencies for short-type-name resolution
            var modClrNamespaces = transModules
                .SelectMany(m => m.ExportedClrNamespaces)
                .Distinct()
                .ToList();

            var inferer = new TypeInferer(
                modDiag,
                _options.AssemblySearchPaths,
                TypeAliases,
                modClrNamespaces.Count > 0 ? modClrNamespaces : null
            )
            {
                CurrentModuleName = moduleName,
            };
            foreach (var mod in transModules)
                if (mod.ExportedClassInterfaces is not null)
                    inferer.RegisterClassInterfaces(mod.ExportedClassInterfaces);
            inferer.Infer(program, env);
            inferer.Resolve(program);
            new ExhaustivenessValidator(modDiag).Validate(
                program,
                transModules.SelectMany(m => m.ExportedIrDefinitions.OfType<IrNode.UnionDecl>())
            );
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Lower to IR — inject transitive CLR bindings
            var lowering = new IrLowering(
                modDiag,
                inferer.OutParamsByAlias,
                TypeAliases,
                _options.AssemblySearchPaths,
                _options.EnableClosureConversion
            );
            foreach (var mod in transModules)
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
                // Field-type metadata for pattern resolution — carried by the UnionDecl/RecordDecl
                // IR, which the flat ExportedUnionCtors/ExportedRecordCtors maps above lack.
                foreach (var union in mod.ExportedIrDefinitions.OfType<IrNode.UnionDecl>())
                    lowering.RegisterImportedUnion(union);
                foreach (var record in mod.ExportedIrDefinitions.OfType<IrNode.RecordDecl>())
                    lowering.RegisterImportedRecord(record);
                if (mod.ExportedRecordCtors is not null)
                    foreach (var (recordName, fieldNames) in mod.ExportedRecordCtors)
                        lowering.RegisterRecordCtor(recordName, fieldNames);
            }

            var modClrImports = transModules.Sum(m => m.ExportedClrImports.Count);
            var modUnionCtors = transModules.Sum(m => m.ExportedUnionCtors?.Count ?? 0);
            var modRecordCtors = transModules.Sum(m => m.ExportedRecordCtors?.Count ?? 0);
            if (modClrImports > 0 || modUnionCtors > 0 || modRecordCtors > 0)
                Log.Debug(
                    "Module {ModuleName}: IR lowering registered {ClrImports} CLR imports, {UnionCtors} union ctors, {RecordCtors} record ctors",
                    moduleName,
                    modClrImports,
                    modUnionCtors,
                    modRecordCtors
                );

            var ir = lowering.Lower(program);
            if (modDiag.HasErrors)
            {
                CopyDiagnostics(modDiag);
                return Fail();
            }

            // Extract export declarations
            var exportDecls = AllTopLevelForms(program).OfType<AstNode.Export>().ToList();
            var exportedNameSpans = new Dictionary<string, SourceSpan>();
            foreach (var export in exportDecls)
            foreach (var name in export.Names)
                exportedNameSpans.TryAdd(name, export.Span);
            var exportedNames = exportedNameSpans.Keys.ToHashSet();

            // Names of concrete types visible in this module (its own declarations plus
            // imported records/unions). A single-lowercase-letter type name such as a record
            // named `r` would otherwise be mistaken for a type parameter and erased when
            // generalizing the exported constructor/accessor types — see GeneralizeForExport.
            var knownTypeNames = new HashSet<string>();
            foreach (var recordName in lowering.RecordCtors.Keys)
                knownTypeNames.Add(recordName);
            foreach (var (caseName, unionName) in lowering.UnionCtors)
            {
                knownTypeNames.Add(caseName);
                knownTypeNames.Add(unionName);
            }

            foreach (var form in AllTopLevelForms(program))
                switch (form)
                {
                    case AstNode.RecordDecl rd:
                        knownTypeNames.Add(rd.RecordName);
                        break;
                    case AstNode.UnionDecl ud:
                        knownTypeNames.Add(ud.UnionName);
                        foreach (var unionCase in ud.Cases)
                            knownTypeNames.Add(unionCase.Name);
                        break;
                    case AstNode.ClassDecl cd:
                        knownTypeNames.Add(cd.ClassName);
                        break;
                    case AstNode.InterfaceDecl id:
                        knownTypeNames.Add(id.InterfaceName);
                        break;
                    case AstNode.TypeAliasDecl ta:
                        knownTypeNames.Add(ta.AliasName);
                        break;
                }

            // Build exported types — generalize type-parameter-like named types
            var exportedTypes = new Dictionary<string, ZType>();
            foreach (var name in exportedNames)
            {
                var type = env.Lookup(name);
                if (type is not null)
                {
                    var resolvedType = inferer.Substitution.Apply(type);
                    exportedTypes[name] = GeneralizeForExport(resolvedType, knownTypeNames);
                }
                else if (
                    !modMacroEnv.OwnMacros.ContainsKey(name)
                    && !modMacroEnv.OwnMacros.Values.Any(m => m.Literals.Contains(name))
                )
                {
                    _diagnostics.Warning(
                        $"Module '{moduleName}' exports '{name}' but it is not defined",
                        exportedNameSpans[name]
                    );
                }
            }

            // Build exported CLR imports (filter to exported names)
            Log.Debug(
                "Module {ModuleName}: before export CLR imports: lowering.ClrImports={ClrImportCount}, exportedNames={ExportedNames}",
                moduleName,
                lowering.ClrImports.Count,
                string.Join(", ", exportedNames)
            );
            var exportedClrImports =
                new Dictionary<
                    string,
                    (
                        string TypeName,
                        string MethodName,
                        int GenericArity,
                        ClrImportKind Kind,
                        IReadOnlyDictionary<string, GenericConstraintKind>? Constraints
                    )
                >();
            foreach (var (alias, clrInfo) in lowering.ClrImports)
                if (exportedNames.Contains(alias))
                    exportedClrImports[alias] = (
                        clrInfo.TypeName,
                        clrInfo.MethodName,
                        clrInfo.GenericArity,
                        clrInfo.Kind,
                        clrInfo.Constraints
                    );

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
            foreach (
                var accessorName in fieldNames.Select(fieldName => $"{recordName}/{fieldName}")
            )
            {
                exportedNames.Add(accessorName);
                var type = env.Lookup(accessorName);
                if (type is not null)
                    exportedTypes[accessorName] = GeneralizeForExport(
                        inferer.Substitution.Apply(type),
                        knownTypeNames
                    );
            }

            // Build exported IR definitions (filter to exported names)
            var exportedIrDefs = new List<IrNode>();
            CollectExportedIrDefs(ir, exportedNames, exportedIrDefs);

            // Build all IR definitions (for library compilation, which needs internal helpers too)
            var allIrDefs = new List<IrNode>();
            CollectAllIrDefs(ir, allIrDefs);

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

            // Collect class interface implementations for cross-module subtyping
            // Note: the parser puts the first name after ':' in BaseClassName (position-based).
            // If it's not a known ZScheme class, it's actually an interface. Include both.
            var exportedClassInterfaces = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var classDecl in AllTopLevelForms(program).OfType<AstNode.ClassDecl>())
            {
                var allInterfaces = new List<string>(classDecl.InterfaceNames);
                if (classDecl.BaseClassName is not null)
                    allInterfaces.Insert(0, classDecl.BaseClassName);
                if (allInterfaces.Count > 0)
                    exportedClassInterfaces[classDecl.ClassName] = allInterfaces;
            }

            Log.Debug(
                "Module {ModuleName}: compiled in {ElapsedMs}ms ({ExportCount} exports, {TypeCount} types, {ClrImportCount} CLR imports, {MacroCount} macros)",
                moduleName,
                moduleSw.ElapsedMilliseconds,
                exportedNames.Count,
                exportedTypes.Count,
                exportedClrImports.Count,
                exportedMacros.Count
            );

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
                exportedClassInterfaces,
                AllIrDefinitions: allIrDefs
            );
        }
        finally
        {
            _compilingModules.Remove(moduleName);
        }
    }

    /// <summary>
    ///     Compiles a single module from source and returns the CompiledModule.
    ///     Used by LibraryCompiler for building library packages.
    /// </summary>
    public CompiledModule? CompileAsModule(string moduleName, string source, string filePath)
    {
        Log.Debug(
            "CompileAsModule: {ModuleName} from {FilePath} ({SourceLength} chars)",
            moduleName,
            filePath,
            source.Length
        );

        // Require module declaration for files with top-level definitions.
        // Pre-parse to check before full compilation.
        // Use a throwaway DiagnosticBag — define-syntax forms with brackets
        // cause harmless errors that must not leak into the real diagnostics.
        var preDiag = new DiagnosticBag();
        var preLexer = new Lexer(source, filePath, preDiag);
        var preTokens = preLexer.Tokenize();
        if (preDiag.HasErrors)
        {
            CopyDiagnostics(preDiag);
            return null;
        }

        var preParser = new SExprParser(preTokens, preDiag);
        var preSexprs = preParser.ParseAll();
        if (preDiag.HasErrors)
        {
            CopyDiagnostics(preDiag);
            return null;
        }

        var preBuilder = new AstBuilder(preDiag);
        var preProgram = preBuilder.BuildProgram(preSexprs);

        var allForms = AllTopLevelForms(preProgram);
        var moduleDecls = allForms.OfType<AstNode.ModuleDecl>().ToList();
        var hasDefinitions = allForms.Any(f => f is AstNode.Define or AstNode.DefineValue);

        if (moduleDecls.Count == 0 && hasDefinitions)
        {
            var firstDefine = preProgram.TopLevelForms.FirstOrDefault(f =>
                f is AstNode.Define or AstNode.DefineValue
            );
            _diagnostics.Error(
                "Files with top-level definitions require a (module ...) declaration",
                firstDefine?.Span ?? SourceSpan.None
            );
            return null;
        }

        var resolver = CreateModuleResolver(filePath);
        resolver.InjectSource(moduleName, filePath, source);

        // Register prelude type aliases (e.g. Mutable-Vector from stdlib/mutable/vector) so they are
        // visible to this module without an explicit import — mirroring the whole-program Compile
        // path, which collects prelude aliases into the registry (see Compile / CompilePreludeModules).
        // The package/library compile path routes every module through here, so this is where the
        // compilation-wide alias registry must be seeded.
        RegisterPreludeTypeAliases(moduleName, resolver);

        return CompileModule(moduleName, resolver, SourceSpan.None);
    }

    /// <summary>
    ///     Seeds <see cref="TypeAliases" /> with the <c>define-type-alias</c> forms declared by the
    ///     prelude modules, so compilation-wide aliases (notably <c>Mutable-Vector</c>, which backs
    ///     the variadic rest-parameter type) resolve even when the module does not explicitly import
    ///     the defining submodule. Only the alias declarations are read — the prelude is parsed, not
    ///     fully compiled, and no value bindings are imported. Skipped for prelude modules themselves
    ///     (all stdlib modules), which already import what they need explicitly.
    /// </summary>
    private void RegisterPreludeTypeAliases(string moduleName, ModuleResolver resolver)
    {
        if (_options.DisablePrelude || _options.PreludeModules.Contains(moduleName))
            return;

        foreach (var preludeName in _options.PreludeModules)
        {
            var resolved = resolver.Resolve(preludeName, SourceSpan.None);
            if (resolved is null)
                continue; // Prelude module not available (e.g. package without stdlib) — skip silently.

            var (preludePath, preludeSource) = resolved.Value;

            // Parse only — a throwaway DiagnosticBag keeps prelude parse noise out of _diagnostics.
            var scratchDiag = new DiagnosticBag();
            var lexer = new Lexer(preludeSource, preludePath, scratchDiag);
            var tokens = lexer.Tokenize();
            if (scratchDiag.HasErrors)
                continue;
            var parser = new SExprParser(tokens, scratchDiag);
            var sexprs = parser.ParseAll();
            if (scratchDiag.HasErrors)
                continue;
            var astBuilder = new AstBuilder(scratchDiag);
            var program = astBuilder.BuildProgram(sexprs);
            if (scratchDiag.HasErrors)
                continue;

            CollectTypeAliasesFromAst(program);
        }
    }
}

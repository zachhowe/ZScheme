using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
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
        foreach (var mod in transModules)
            if (mod.ExportedClassInterfaces is not null)
                inferer.RegisterClassInterfaces(mod.ExportedClassInterfaces);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (modDiag.HasErrors)
        {
            CopyDiagnostics(modDiag);
            return null;
        }

        // Lower to IR — inject transitive CLR bindings
        var lowering = new IrLowering(modDiag, inferer.OutParamsByAlias);
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
                exportedClrImports[alias] = (clrInfo.TypeName, clrInfo.MethodName, clrInfo.GenericArity,
                    clrInfo.Kind, clrInfo.Constraints);

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
            ExportedClassInterfaces: exportedClassInterfaces,
            AllIrDefinitions: allIrDefs
        );
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
        foreach (var mod in transModules)
            if (mod.ExportedClassInterfaces is not null)
                inferer.RegisterClassInterfaces(mod.ExportedClassInterfaces);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (modDiag.HasErrors)
        {
            _diagnostics.AddRange(modDiag);
            return null;
        }

        // Lower to IR
        var lowering = new IrLowering(modDiag, inferer.OutParamsByAlias);
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
                exportedClrImports[alias] = (clrInfo.TypeName, clrInfo.MethodName, clrInfo.GenericArity,
                    clrInfo.Kind, clrInfo.Constraints);

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

        var exportedClassInterfaces2 = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var classDecl in AllTopLevelForms(program).OfType<AstNode.ClassDecl>())
        {
            var allInterfaces = new List<string>(classDecl.InterfaceNames);
            if (classDecl.BaseClassName is not null)
                allInterfaces.Insert(0, classDecl.BaseClassName);
            if (allInterfaces.Count > 0)
                exportedClassInterfaces2[classDecl.ClassName] = allInterfaces;
        }

        return new CompiledModule(
            moduleName, filePath, exportedNames, exportedTypes, exportedClrImports,
            exportedIrDefs, exportedClrNamespaces, exportedMacros,
            exportedUnionCtors, exportedRecordCtors,
            ExportedClassInterfaces: exportedClassInterfaces2,
            AllIrDefinitions: allIrDefs);
    }
}

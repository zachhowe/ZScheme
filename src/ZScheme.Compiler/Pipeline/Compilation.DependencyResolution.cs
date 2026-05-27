using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    /// <summary>
    ///     Builds a <see cref="ModuleResolver" /> configured with the search paths, package paths, and
    ///     module aliases needed to resolve imports starting from <paramref name="importingFilePath" />.
    /// </summary>
    /// <remarks>
    ///     Search paths are registered in priority order: the directory of the importing file first, then
    ///     any paths from <c>_options.ModuleSearchPaths</c>, followed by the <c>stdlib</c> package path
    ///     (if present). Package paths and module aliases from <c>_options</c> are also registered.
    /// </remarks>
    /// <param name="importingFilePath">Path of the source file whose imports will be resolved.</param>
    /// <returns>A configured <see cref="ModuleResolver" />.</returns>
    private ModuleResolver CreateModuleResolver(string importingFilePath)
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

        // 4. Register module aliases (e.g., "zunit" → "zunit/zunit")
        foreach (var (alias, qualified) in _options.ModuleAliases)
            resolver.AddModuleAlias(alias, qualified);

        Log.Debug(
            "Compilation: resolver configured, searchPaths={SearchPathCount}, packagePaths={PackagePathCount}, aliases={AliasCount}",
            _options.ModuleSearchPaths.Count + 1, _options.PackagePaths.Count, _options.ModuleAliases.Count);
        return resolver;
    }

    /// <summary>
    ///     Recursively walks a module's imports, populating <paramref name="graph" /> with the modules
    ///     reachable from <paramref name="moduleName" /> and the dependency edges between them.
    /// </summary>
    /// <remarks>
    ///     Performs a lightweight lex/parse/AST-build pass purely to discover <c>import</c> directives;
    ///     any diagnostics produced during the scan are discarded. The <paramref name="scanned" /> set
    ///     guards against revisiting modules and breaks import cycles.
    /// </remarks>
    /// <param name="moduleName">Qualified name of the module being scanned.</param>
    /// <param name="source">Source text of the module.</param>
    /// <param name="filePath">Path of the module's source file (used for diagnostics).</param>
    /// <param name="graph">Module graph to populate with discovered modules and edges.</param>
    /// <param name="resolver">Resolver used to locate imported modules on disk.</param>
    /// <param name="scanned">Set of already-visited module names; allocated on first call.</param>
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
        Log.Debug("ScanDependencies: scanning {ModuleName} from {FilePath}", moduleName, filePath);

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
}

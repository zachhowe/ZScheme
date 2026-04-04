using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
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

        // 4. Register module aliases (e.g., "zunit" → "zunit/zunit")
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
}

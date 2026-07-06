using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;

namespace ZScheme.MacroDebugger.Services;

/// <summary>
///     The outcome of running the compiler through stage 2.5 on a file.
///     <see cref="ExpansionRan" /> is false when compilation failed before macro expansion
///     (lex/parse/dependency errors), in which case <see cref="Steps" /> is empty.
/// </summary>
public sealed record ExpansionResult(
    string FilePath,
    IReadOnlyList<MacroStep> Steps,
    IReadOnlyList<SExpr>? RawForms,
    IReadOnlyList<SExpr>? ExpandedForms,
    IReadOnlyDictionary<int, IReadOnlyList<SExpr>>? ExpandedByRawIndex,
    DiagnosticBag Diagnostics,
    bool ExpansionRan
);

public static class ExpansionSession
{
    public static ExpansionResult Run(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var workspace = WorkspaceDiscovery.Discover(filePath);
        var trace = new MacroExpansionTrace();
        var options = new CompilerOptions
        {
            StopAfterMacroExpansion = true,
            MacroObserver = trace,
            AllowsImplicitModuleName = true,
            PackagePaths = workspace.PackagePaths,
            ModuleAliases = workspace.ModuleAliases,
            ModuleSearchPaths = workspace.ModuleSearchPaths,
        };

        var compilation = new Compilation(options);
        compilation.Compile(source, filePath);

        return new ExpansionResult(
            filePath,
            trace.Steps,
            compilation.RawSExprs,
            compilation.ExpandedSExprs,
            trace.ExpandedTopLevelForms,
            compilation.GetDiagnostics(),
            compilation.ExpandedSExprs is not null
        );
    }
}

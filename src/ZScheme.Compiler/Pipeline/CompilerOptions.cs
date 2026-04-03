namespace ZScheme.Compiler.Pipeline;

public enum OutputMode
{
    CSharp,
    Il
}

public sealed class CompilerOptions
{
    public OutputMode OutputMode { get; set; } = OutputMode.CSharp;
    public string OutputPath { get; set; } = "output";
    public string Namespace { get; set; } = "ZSchemeGenerated";
    public List<string> AssemblySearchPaths { get; set; } = [];
    public List<string> ModuleSearchPaths { get; set; } = [];
    public Dictionary<string, string> PackagePaths { get; set; } = new();
    public Dictionary<string, string> ModuleAliases { get; set; } = new();

    public List<string> PreludeModules { get; set; } =
        ["stdlib/option", "stdlib/result", "stdlib/error", "stdlib/core", "stdlib/list", "stdlib/array", "stdlib/map"];

    public bool DisablePrelude { get; set; } = true;
    /// <summary>
    /// When <c>true</c>, files without a <c>(module ...)</c> declaration compile using
    /// "UnnamedModule" as the class name instead of failing. Intended for REPL and unit
    /// test scenarios where there is no actual source file. Defaults to <c>false</c>.
    /// </summary>
    public bool AllowsImplicitModuleName { get; set; }
    public List<string> PrecompiledPackagePaths { get; set; } = [];
    public bool SuppressVersionPreamble { get; set; }
}

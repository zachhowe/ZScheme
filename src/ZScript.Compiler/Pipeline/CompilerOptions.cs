namespace ZScript.Compiler.Pipeline;

public enum OutputMode
{
    CSharp,
    IL
}

public sealed class CompilerOptions
{
    public OutputMode OutputMode { get; set; } = OutputMode.CSharp;
    public string OutputPath { get; set; } = "output";
    public string Namespace { get; set; } = "ZScriptGenerated";
    public bool EmitDebugInfo { get; set; }
    public string? StdLibPath { get; set; }
    public List<string> AssemblySearchPaths { get; set; } = [];
    public List<string> ModuleSearchPaths { get; set; } = [];
    public Dictionary<string, string> PackagePaths { get; set; } = new();
    public Dictionary<string, string> ModuleAliases { get; set; } = new();

    public List<string> PreludeModules { get; set; } =
        ["stdlib/option", "stdlib/result", "stdlib/error", "stdlib/core", "stdlib/list", "stdlib/vector", "stdlib/map"];

    public bool DisablePrelude { get; set; } = true;
    public bool UsePackageCache { get; set; } = true;
    public List<string> PrecompiledPackagePaths { get; set; } = [];
}

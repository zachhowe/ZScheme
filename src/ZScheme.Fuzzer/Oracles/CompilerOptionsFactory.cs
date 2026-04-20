using ZScheme.Compiler.Pipeline;

namespace ZScheme.Fuzzer.Oracles;

public sealed class CompilerOptionsFactory
{
    private readonly string _stdlibPath;

    public CompilerOptionsFactory(string stdlibPath) { _stdlibPath = stdlibPath; }

    public CompilerOptions Build(OutputMode mode) => new()
    {
        OutputMode = mode,
        Namespace = "ZSchemeFuzzed",
        AllowsImplicitModuleName = true,
        DisablePrelude = true,
        SuppressVersionPreamble = true,
        PackagePaths = new Dictionary<string, string> { ["stdlib"] = _stdlibPath },
    };
}

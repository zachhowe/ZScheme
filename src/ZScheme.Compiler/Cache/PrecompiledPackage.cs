using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Cache;

public sealed record PrecompiledPackage(
    string PackageName,
    string Version,
    string AssemblyPath,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    string? ImportPrefix = null,
    string? DefaultModule = null);

using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Cache;

public sealed record PrecompiledPackage(
    string PackageName,
    string Version,
    string AssemblyPath,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    string? ImportPrefix = null,
    string? DefaultModule = null,
    /// <summary>
    ///     Maps qualified module name to absolute path of the bundled .zs source file,
    ///     when the package was built with (bundle-source true). Null otherwise.
    ///     Used by the cross-assembly continuation recompiler so that callers can
    ///     selectively re-lower precompiled functions with the continuation transform.
    /// </summary>
    IReadOnlyDictionary<string, string>? ModuleSourcePaths = null,
    /// <summary>
    ///     Absolute path to the package directory in the cache (the parent of the .dll).
    /// </summary>
    string? PackageDir = null
);

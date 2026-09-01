using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Cache;

public sealed record PrecompiledPackage(
    string PackageName,
    string Version,
    string AssemblyPath,
    IReadOnlyDictionary<string, CompiledModule> Modules,
    string? ImportPrefix = null,
    string? DefaultModule = null,
    IReadOnlyList<PrecompiledPackageDependency>? Dependencies = null,
    string? InputFingerprint = null
)
{
    /// <summary>
    ///     The ZScheme packages this one was built against. A package assembly that references
    ///     its dependencies instead of carrying a copy of them is only loadable together with
    ///     them, so a consumer reads this to pull the rest of the closure.
    /// </summary>
    public IReadOnlyList<PrecompiledPackageDependency> Dependencies { get; init; } =
        Dependencies ?? [];
}

/// <summary>
///     A package this artifact was built against, as recorded in its metadata sidecar.
///     <c>Fingerprint</c> is that dependency's own input hash at the time this artifact was built,
///     which is what makes staleness decidable: an artifact whose own sources are unchanged is
///     still stale if the dependency now offers different signatures than the ones it was compiled
///     against. Null when the dependency could not be fingerprinted.
/// </summary>
public sealed record PrecompiledPackageDependency(
    string Name,
    string Version,
    string? Fingerprint = null
);

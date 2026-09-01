using Serilog;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package;

/// <summary>
///     How a compilation reaches each package in a dependency closure: by referencing a built
///     assembly, or by compiling its sources alongside its own.
/// </summary>
public sealed record DependencyWiring(
    IReadOnlyList<string> ModuleSearchPaths,
    IReadOnlyDictionary<string, string> PackagePaths,
    IReadOnlyDictionary<string, string> ModuleAliases,
    IReadOnlyList<string> PrecompiledAssemblyPaths
);

/// <summary>
///     Turns a resolved dependency closure into the search paths and precompiled assembly
///     references a compilation needs, deciding per package which of the two it gets.
///     <para>
///         Every entry point that compiles a package resolves through here so they agree. They did
///         not before: <c>PackageOptionsBuilder</c>, <c>PackageTester</c>,
///         <c>PackageAutoInstaller</c> and <c>generate-project</c> each walked the closure and
///         wired it themselves, which is how a dependency could be referenced by one and compiled
///         from source by another — the two then disagree about which assembly a type belongs to.
///     </para>
/// </summary>
public static class PackageDependencyWiring
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageDependencyWiring));

    /// <summary>
    ///     Wires an explicit list of packages, for a caller that resolves its dependencies itself
    ///     rather than through a transitive closure. Paths and prefixes are built from the list in
    ///     order, first writer winning on a shared prefix, matching what those callers did inline.
    /// </summary>
    /// <param name="preferPrecompiled">
    ///     When true, a package with a current built artifact is referenced and its sources are
    ///     dropped from the search paths, so its modules are not compiled a second time into the
    ///     consumer. When false every package is compiled from source, which is what a host that
    ///     wants to see through to a dependency's sources needs — the language server resolves
    ///     go-to-definition into them, and would otherwise be answering from a build that is only
    ///     as current as the last one.
    /// </param>
    public static DependencyWiring ForPackages(
        IReadOnlyList<ResolvedPackage> packages,
        bool preferPrecompiled,
        DiagnosticBag diagnostics,
        string? cacheDirectory = null
    )
    {
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var precompiled = new List<string>();

        foreach (var package in packages)
        {
            if (package.DefaultModule is { } defaultModule)
                moduleAliases.TryAdd(package.Prefix, $"{package.Prefix}/{defaultModule}");

            var artifact = preferPrecompiled
                ? PackageArtifactResolver.Resolve(package, diagnostics, cacheDirectory)
                : null;

            if (artifact is not null)
            {
                precompiled.Add(artifact.AssemblyPath);
                Log.Debug(
                    "PackageDependencyWiring: referencing {PackageName}@{Version} at {AssemblyPath}",
                    artifact.Name,
                    artifact.Version,
                    artifact.AssemblyPath
                );
                continue;
            }

            moduleSearchPaths.Add(package.SourceDir);
            packagePaths.TryAdd(package.Prefix, package.SourceDir);
        }

        return new DependencyWiring(moduleSearchPaths, packagePaths, moduleAliases, precompiled);
    }

    /// <inheritdoc cref="ForPackages" />
    public static DependencyWiring For(
        TransitiveZSchemeClosure closure,
        bool preferPrecompiled,
        DiagnosticBag diagnostics,
        string? cacheDirectory = null
    )
    {
        var moduleSearchPaths = new List<string>(closure.ModuleSearchPaths);
        var packagePaths = new Dictionary<string, string>(closure.PackagePaths);
        var moduleAliases = new Dictionary<string, string>(closure.ModuleAliases);
        var precompiled = new List<string>();

        if (!preferPrecompiled)
            return new DependencyWiring(moduleSearchPaths, packagePaths, moduleAliases, precompiled);

        foreach (var package in closure.Packages)
        {
            var artifact = PackageArtifactResolver.Resolve(package, diagnostics, cacheDirectory);
            if (artifact is null)
            {
                Log.Debug(
                    "PackageDependencyWiring: {PackageName} has no usable artifact, compiling from {SourceDir}",
                    package.Name,
                    package.SourceDir
                );
                continue;
            }

            precompiled.Add(artifact.AssemblyPath);

            // Drop the sources the closure pointed at, so the modules now coming from the assembly
            // are not also compiled into the consumer under the same names.
            moduleSearchPaths.RemoveAll(path =>
                string.Equals(path, package.SourceDir, StringComparison.OrdinalIgnoreCase)
            );

            // Only if this package is the one holding the prefix. A closure can carry two packages
            // claiming one prefix — first writer wins — and removing the other's entry would strand
            // it with neither an assembly nor a source path.
            if (
                packagePaths.TryGetValue(package.Prefix, out var owner)
                && string.Equals(owner, package.SourceDir, StringComparison.OrdinalIgnoreCase)
            )
                packagePaths.Remove(package.Prefix);

            Log.Debug(
                "PackageDependencyWiring: referencing {PackageName}@{Version} at {AssemblyPath}",
                artifact.Name,
                artifact.Version,
                artifact.AssemblyPath
            );
        }

        // Module aliases are kept either way. A precompiled package's metadata registers its own
        // prefix alias, but only for the default module; the closure's alias is what a consumer's
        // bare `(import stdlib)` resolves through, and it costs nothing to keep it accurate.
        return new DependencyWiring(moduleSearchPaths, packagePaths, moduleAliases, precompiled);
    }
}

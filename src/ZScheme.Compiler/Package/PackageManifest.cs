using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public sealed record PackageManifest(
    string Name,
    string Version,
    string? Entry,
    string? ImportPrefix,
    string? DefaultModule,
    string? Description,
    string? License,
    PackageDependencies Dependencies,
    PackageDependencies TestDependencies,
    BuildConfig Build,
    SourcePaths? Sources,
    SourceSpan Span);

public sealed record PackageDependencies(
    IReadOnlyList<ZSchemeDependency> ZScheme,
    IReadOnlyList<NuGetDependency> NuGet);

public sealed record ZSchemeDependency(string Name, ZSchemeDependencySource Source, SourceSpan Span);

public abstract record ZSchemeDependencySource
{
    public sealed record Git(string Url, string VersionOrRef) : ZSchemeDependencySource;

    public sealed record Local(string Path) : ZSchemeDependencySource;
}

public sealed record NuGetDependency(string PackageId, string Version, SourceSpan Span);

public sealed record BuildConfig(
    MainBuildConfig? Main,
    TestBuildConfig? Test);

public sealed record MainBuildConfig(
    string? OutputPath,
    OutputMode? Backend,
    string? Namespace,
    IReadOnlyList<string> RefPaths);

public sealed record TestBuildConfig(
    string? OutputPath,
    string? Namespace,
    IReadOnlyList<string> RefPaths);

public sealed record SourcePaths(string? Main, string? Test);

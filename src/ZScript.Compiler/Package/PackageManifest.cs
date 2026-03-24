namespace ZScript.Compiler.Package;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Pipeline;

public sealed record PackageManifest(
    string Name, string Version, string? Entry, string? ImportPrefix,
    string? DefaultModule,
    PackageDependencies Dependencies, BuildConfig Build,
    SourcePaths? Sources, SourceSpan Span);

public sealed record PackageDependencies(
    IReadOnlyList<ZScriptDependency> ZScript,
    IReadOnlyList<NuGetDependency> NuGet);

public sealed record ZScriptDependency(string Name, ZScriptDependencySource Source, SourceSpan Span);

public abstract record ZScriptDependencySource
{
    public sealed record Git(string Url, string VersionOrRef) : ZScriptDependencySource;
    public sealed record Local(string Path) : ZScriptDependencySource;
}

public sealed record NuGetDependency(string PackageId, string Version, SourceSpan Span);

public sealed record BuildConfig(
    string? OutputPath, OutputMode? Backend, string? Namespace,
    string? StdLibPath, IReadOnlyList<string> RefPaths);

public sealed record SourcePaths(string? Main, string? Test);

namespace ZScript.Compiler.Package.NuGet;

internal sealed record NuspecInfo(
    string Id,
    string Version,
    IReadOnlyList<NuspecDependencyGroup> DependencyGroups);

internal sealed record NuspecDependencyGroup(
    string? TargetFramework,
    IReadOnlyList<NuspecDependencyRef> Dependencies);

internal sealed record NuspecDependencyRef(string Id, string VersionRange);

internal sealed record ResolvedPackage(string Id, string Version, string NupkgPath);

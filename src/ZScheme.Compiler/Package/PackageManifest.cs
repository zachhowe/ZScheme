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
    SourceSpan Span
);

public sealed record PackageDependencies(
    IReadOnlyList<ZSchemeDependency> ZScheme,
    IReadOnlyList<NuGetDependency> NuGet,
    IReadOnlyList<FrameworkDependency> Frameworks
)
{
    public PackageDependencies(
        IReadOnlyList<ZSchemeDependency> zscheme,
        IReadOnlyList<NuGetDependency> nuget
    )
        : this(zscheme, nuget, []) { }
}

public sealed record ZSchemeDependency(
    string Name,
    ZSchemeDependencySource Source,
    SourceSpan Span
);

public abstract record ZSchemeDependencySource
{
    public sealed record Git(string Url, string VersionOrRef) : ZSchemeDependencySource;

    public sealed record Local(string Path) : ZSchemeDependencySource;
}

public sealed record NuGetDependency(string PackageId, string Version, SourceSpan Span);

/// <summary>
///     A shared-framework reference like Microsoft.AspNetCore.App. Emitted as
///     &lt;FrameworkReference Include="..."/&gt; in generated csproj files. Some framework
///     IDs imply a non-default Sdk (e.g. Microsoft.AspNetCore.App → Microsoft.NET.Sdk.Web).
/// </summary>
public sealed record FrameworkDependency(string Id, SourceSpan Span);

public sealed record BuildConfig(MainBuildConfig? Main, TestBuildConfig? Test);

public sealed record MainBuildConfig(
    string? OutputPath,
    OutputMode? Backend,
    string? Namespace,
    IReadOnlyList<string> RefPaths,
    string? Sdk = null,
    string? OutputType = null,
    // (warn-unused-params "false") disables ZS0003 unused-parameter warnings for the
    // package; null means "not specified" (compiler default: on). The CLI's
    // --no-warn-unused-params wins over this.
    bool? WarnUnusedParameters = null,
    // (warn-unlooped-recursion "false") disables ZS0005 warnings about self-recursion that
    // is not compiled as a loop; null means "not specified" (compiler default: on). The
    // CLI's --no-warn-unlooped-recursion wins over this.
    bool? WarnUnloopedRecursion = null,
    // (warn-deprecated-accessor-syntax "false") disables ZS0006 warnings about member
    // accessors written with the deprecated `Type/member` spelling; null means "not
    // specified" (compiler default: on). The CLI's --no-warn-deprecated-accessor-syntax
    // wins over this.
    bool? WarnDeprecatedAccessorSyntax = null
);

public sealed record TestBuildConfig(
    string? OutputPath,
    string? Namespace,
    IReadOnlyList<string> RefPaths
);

public sealed record SourcePaths(string? Main, string? Test);

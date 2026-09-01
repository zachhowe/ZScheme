using System.Text;
using Serilog;

namespace ZScheme.Compiler.Codegen;

public sealed record CSharpProjectOptions
{
    public string OutputType { get; init; } = "Exe";
    public string? LangVersion { get; init; }
    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];
    public IReadOnlyList<(string PackageId, string Version)> NuGetPackages { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    /// <summary>
    ///     Sdk attribute for the &lt;Project&gt; root. Defaults to <c>Microsoft.NET.Sdk</c>.
    ///     Set to <c>Microsoft.NET.Sdk.Web</c> for ASP.NET Core projects.
    /// </summary>
    public string Sdk { get; init; } = "Microsoft.NET.Sdk";

    /// <summary>
    ///     Shared-framework references emitted as &lt;FrameworkReference Include="..."/&gt;
    ///     (e.g. Microsoft.AspNetCore.App).
    /// </summary>
    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>
    ///     Simple names of resolved assemblies to move out of the global namespace by giving
    ///     them an <c>extern alias</c>. Needed when two assemblies in the reference set export
    ///     the same full type name: the IL backend binds a member reference to one specific
    ///     assembly, but C# name resolution sees both and reports CS0433. Hiding the one the
    ///     generated code does not use restores a single candidate.
    /// </summary>
    public IReadOnlyList<string> AliasedAssemblies { get; init; } = [];
}

public static class CSharpProjectGenerator
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(CSharpProjectGenerator));

    /// <summary>
    ///     Framework references each SDK already implies. Restating one is not an error but
    ///     NETSDK1086 warns about it, which a stricter consumer build turns into a failure.
    /// </summary>
    private static readonly Dictionary<string, string[]> SdkImpliedFrameworkReferences = new()
    {
        ["Microsoft.NET.Sdk.Web"] = ["Microsoft.AspNetCore.App"],
    };

    public static string GenerateCsproj(CSharpProjectOptions options)
    {
        var version = Environment.Version;
        var sb = new StringBuilder();

        var implied = SdkImpliedFrameworkReferences.TryGetValue(options.Sdk, out var ids)
            ? ids
            : [];
        var frameworkRefs = options.FrameworkReferences.Where(id => !implied.Contains(id)).ToList();

        sb.AppendLine($"<Project Sdk=\"{options.Sdk}\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <OutputType>{options.OutputType}</OutputType>");
        sb.AppendLine($"    <TargetFramework>net{version.Major}.{version.Minor}</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");

        if (options.LangVersion is not null)
            sb.AppendLine($"    <LangVersion>{options.LangVersion}</LangVersion>");

        var hasItems =
            options.AssemblyReferences.Count > 0
            || options.NuGetPackages.Count > 0
            || options.ProjectReferences.Count > 0
            || frameworkRefs.Count > 0;

        if (hasItems)
            sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");

        sb.AppendLine("  </PropertyGroup>");

        if (hasItems)
        {
            sb.AppendLine("  <ItemGroup>");

            foreach (var path in options.AssemblyReferences)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                sb.AppendLine($"    <Reference Include=\"{name}\">");
                sb.AppendLine($"      <HintPath>{path}</HintPath>");
                sb.AppendLine("    </Reference>");
            }

            foreach (var projectRef in options.ProjectReferences)
                sb.AppendLine($"    <ProjectReference Include=\"{projectRef}\" />");

            foreach (var (packageId, packageVersion) in options.NuGetPackages)
                sb.AppendLine(
                    $"    <PackageReference Include=\"{packageId}\" Version=\"{packageVersion}\" />"
                );

            foreach (var fwRef in frameworkRefs)
                sb.AppendLine($"    <FrameworkReference Include=\"{fwRef}\" />");

            sb.AppendLine("  </ItemGroup>");
        }

        // Aliasing has to happen after the SDK has resolved references — a shared-framework
        // assembly is not a <Reference> item we author, so Aliases metadata can only be
        // attached to the resolved ReferencePath item.
        if (options.AliasedAssemblies.Count > 0)
        {
            sb.AppendLine(
                "  <Target Name=\"ZsAliasAmbiguousReferences\" AfterTargets=\"ResolveReferences\">"
            );
            sb.AppendLine("    <ItemGroup>");
            foreach (var assembly in options.AliasedAssemblies)
            {
                var alias = "zs_" + assembly.Replace('.', '_');
                sb.AppendLine($"      <ReferencePath Condition=\"'%(FileName)' == '{assembly}'\">");
                sb.AppendLine($"        <Aliases>{alias}</Aliases>");
                sb.AppendLine("      </ReferencePath>");
            }

            sb.AppendLine("    </ItemGroup>");
            sb.AppendLine("  </Target>");
        }

        sb.Append("</Project>");
        return sb.ToString();
    }

    /// <summary>
    ///     A <c>Directory.Build.props</c> to drop at the root of a generated project tree.
    ///     MSBuild stops walking up the directory chain at the first one it finds, so this
    ///     shields generated code from whatever build settings happen to sit above the output
    ///     directory — most importantly a repo-wide <c>TreatWarningsAsErrors</c>, which turns
    ///     ordinary nullability warnings in machine-generated C# (and NuGet's NU1510 pruning
    ///     advice) into build failures.
    /// </summary>
    public static string GenerateIsolatingDirectoryBuildProps()
    {
        return """
            <Project>
              <!-- Generated by `zs generate-project`. Present so the generated projects do not
                   inherit build settings from a Directory.Build.props above this directory. -->
              <PropertyGroup>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """;
    }

    /// <param name="pruneGeneratedCsFiles">
    ///     Delete previously generated <c>.cs</c> files under <paramref name="outputDir" />
    ///     before writing. The csproj carries no <c>&lt;Compile&gt;</c> items, so the SDK
    ///     globs <c>**/*.cs</c>: once a project is more than one file, a renamed or deleted
    ///     module leaves behind a source file that still compiles, and the project fails on
    ///     duplicate definitions. Only files this compiler wrote are removed — see
    ///     <see cref="PruneGeneratedCsFiles" />.
    /// </param>
    public static void WriteProjectDirectory(
        string outputDir,
        string projectName,
        IReadOnlyList<(string FileName, string Content)> csFiles,
        CSharpProjectOptions options,
        bool pruneGeneratedCsFiles = false
    )
    {
        Directory.CreateDirectory(outputDir);

        if (pruneGeneratedCsFiles)
            PruneGeneratedCsFiles(outputDir);

        var csprojPath = Path.Combine(outputDir, $"{projectName}.csproj");
        File.WriteAllText(csprojPath, GenerateCsproj(options));

        foreach (var (fileName, content) in csFiles)
        {
            var filePath = Path.Combine(outputDir, fileName);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
                Directory.CreateDirectory(fileDir);
            File.WriteAllText(filePath, content);
        }
    }

    /// <summary>
    ///     Deletes the <c>.cs</c> files under <paramref name="outputDir" /> that this
    ///     compiler generated, leaving anything else alone. Generated output goes into
    ///     user-named directories (<c>zs compile --emit-project</c> points wherever the user
    ///     says), so the marker check is what keeps this from deleting hand-written sources;
    ///     a file emitted with the version preamble suppressed carries no marker and
    ///     survives, which only costs a stale file.
    /// </summary>
    private static void PruneGeneratedCsFiles(string outputDir)
    {
        // Materialized: the enumeration is being deleted from as it runs.
        var candidates = Directory
            .EnumerateFiles(outputDir, "*.cs", SearchOption.AllDirectories)
            .ToList();

        foreach (var path in candidates)
        {
            // Build output, not source. obj/ holds the SDK's own generated .cs, which it
            // rewrites on the next build; neither tree is compiled from here.
            var root = Path.GetRelativePath(outputDir, path).Split('/', '\\')[0];
            if (
                root.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || root.Equals("obj", StringComparison.OrdinalIgnoreCase)
            )
                continue;

            try
            {
                using (var reader = new StreamReader(path))
                {
                    var firstLine = reader.ReadLine();
                    if (
                        firstLine?.StartsWith(
                            CSharpEmitter.GeneratedFileMarker,
                            StringComparison.Ordinal
                        ) != true
                    )
                        continue;
                }

                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked or unreadable file is not worth failing the whole generation
                // over; the build that follows reports it far more clearly.
                Log.Warning(ex, "Could not remove stale generated file {Path}", path);
            }
        }
    }
}

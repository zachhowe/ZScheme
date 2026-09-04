using System.IO.Enumeration;
using System.Security;
using System.Text;

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

    /// <summary>
    ///     The source files to compile, as paths relative to the project directory. When
    ///     non-empty the SDK's default <c>**/*.cs</c> glob is switched off and exactly these
    ///     are compiled, so a stray <c>.cs</c> in the directory — a module's file from before
    ///     it was renamed, a per-module tree left where <c>zs compile --emit-project</c> now
    ///     writes one file, a hand-written source — is never picked up. Empty keeps the glob,
    ///     for a project whose sources the user adds by hand (<c>generate-project</c> with no
    ///     manifest).
    /// </summary>
    public IReadOnlyList<string> CompileItems { get; init; } = [];
}

public static class CSharpProjectGenerator
{
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

        if (options.CompileItems.Count > 0)
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");

        sb.AppendLine("  </PropertyGroup>");

        if (options.CompileItems.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var path in options.CompileItems)
                sb.AppendLine($"    <Compile Include=\"{SecurityElement.Escape(path)}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

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

    /// <summary>
    ///     Writes the csproj and every source file. The csproj lists exactly
    ///     <paramref name="csFiles" /> as its <c>&lt;Compile&gt;</c> items, so whatever else
    ///     sits under <paramref name="outputDir" /> is not compiled.
    /// </summary>
    /// <param name="pruneStaleGeneratedFiles">
    ///     First delete every <c>.cs</c> file a previous run generated anywhere under
    ///     <paramref name="outputDir" />, so a module renamed or deleted since does not leave
    ///     its old file lingering as if it were part of the project. Only for a caller that
    ///     owns the whole tree, as <c>generate-project</c> does: it writes one file per
    ///     module and every generated file under its directory is one of its own. A
    ///     single-file write such as <c>zs compile --emit-project</c> owns nothing but the
    ///     file it overwrites, and the directory it is pointed at can hold other compiles'
    ///     output — <c>-o .</c> at a repo root would sweep every generated tree below it.
    ///     Only files this compiler wrote are removed — see
    ///     <see cref="PruneGeneratedCsFiles" />.
    /// </param>
    public static void WriteProjectDirectory(
        string outputDir,
        string projectName,
        IReadOnlyList<(string FileName, string Content)> csFiles,
        CSharpProjectOptions options,
        bool pruneStaleGeneratedFiles
    )
    {
        Directory.CreateDirectory(outputDir);
        if (pruneStaleGeneratedFiles)
            PruneGeneratedCsFiles(outputDir);

        var csprojPath = Path.Combine(outputDir, $"{projectName}.csproj");
        File.WriteAllText(
            csprojPath,
            GenerateCsproj(options with { CompileItems = [.. csFiles.Select(f => f.FileName)] })
        );

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
    ///     user-named directories, so the marker check is what keeps this from deleting
    ///     hand-written sources; a file emitted with the version preamble suppressed carries
    ///     no marker and survives, which only costs a stale file.
    /// </summary>
    private static void PruneGeneratedCsFiles(string outputDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));

        // Materialized: the enumeration is being deleted from as it runs. A file that is
        // itself a link stays a candidate — it is as stale as any other, and deleting it
        // removes the link, not its target — but a directory link is not
        // descended into, so a symlink or junction under a user-named output directory
        // cannot walk this into another tree's files, or into itself. Nor is bin/ or obj/
        // at the root: build output, not source. obj/ holds the SDK's own generated .cs,
        // which it rewrites on the next build. An unreadable directory is not skipped
        // either: a stale file inside it has to fail the prune rather than be silently
        // left behind (EnumerationOptions ignores inaccessible entries by default;
        // SearchOption.AllDirectories never did).
        var candidates = new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            }
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && entry.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0
                && !(
                    entry.Directory.SequenceEqual(root)
                    && (
                        entry.FileName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || entry.FileName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    )
                ),
        }.ToList();

        foreach (var path in candidates)
        {
            // A locked or unreadable file propagates. Swallowing it would let the command
            // report success and leave the user to meet the stale file as a duplicate
            // definition in the next build, with nothing saying a prune was attempted.
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
    }
}

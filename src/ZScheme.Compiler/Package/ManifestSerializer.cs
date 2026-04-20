using System.Text;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public static class ManifestSerializer
{
    public static string Serialize(PackageManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(package");

        AppendStringField(sb, "name", manifest.Name);
        AppendStringField(sb, "version", manifest.Version);

        if (manifest.Entry is not null)
            AppendStringField(sb, "entry", manifest.Entry);
        if (manifest.ImportPrefix is not null)
            AppendStringField(sb, "import-prefix", manifest.ImportPrefix);
        if (manifest.DefaultModule is not null)
            AppendStringField(sb, "default-module", manifest.DefaultModule);
        if (manifest.Description is not null)
            AppendStringField(sb, "description", manifest.Description);
        if (manifest.License is not null)
            AppendStringField(sb, "license", manifest.License);

        if (manifest.Sources is not null)
            AppendSources(sb, manifest.Sources);

        if (HasDependencies(manifest.Dependencies))
            AppendDependencies(sb, "dependencies", manifest.Dependencies);

        if (HasDependencies(manifest.TestDependencies))
            AppendDependencies(sb, "test-dependencies", manifest.TestDependencies);

        if (HasBuildConfig(manifest.Build))
            AppendBuildConfig(sb, manifest.Build);

        // Close the (package form — replace trailing newline with closing paren
        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();

        return sb.ToString();
    }

    private static void AppendStringField(StringBuilder sb, string name, string value)
    {
        sb.AppendLine($"  ({name} \"{value}\")");
    }

    private static void AppendSources(StringBuilder sb, SourcePaths sources)
    {
        sb.AppendLine("  (sources");
        if (sources.Main is not null)
            sb.AppendLine($"    (main \"{sources.Main}\")");
        if (sources.Test is not null)
            sb.AppendLine($"    (test \"{sources.Test}\")");
        // Close sources — replace trailing newline, append closing paren
        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();
    }

    private static void AppendDependencies(StringBuilder sb, string sectionName, PackageDependencies deps)
    {
        sb.AppendLine($"  ({sectionName}");

        if (deps.ZScheme.Count > 0)
        {
            sb.AppendLine("    (zscheme");
            foreach (var dep in deps.ZScheme)
                switch (dep.Source)
                {
                    case ZSchemeDependencySource.Local local:
                        sb.AppendLine($"      [{dep.Name} :local \"{local.Path}\"]");
                        break;
                    case ZSchemeDependencySource.Git git:
                        sb.AppendLine($"      [{dep.Name} :git \"{git.Url}\" \"{git.VersionOrRef}\"]");
                        break;
                }

            sb.Length -= Environment.NewLine.Length;
            sb.Append(')');
            sb.AppendLine();
        }

        if (deps.NuGet.Count > 0)
        {
            sb.AppendLine("    (nuget");
            foreach (var dep in deps.NuGet)
                sb.AppendLine($"      [{dep.PackageId} \"{dep.Version}\"]");
            sb.Length -= Environment.NewLine.Length;
            sb.Append(')');
            sb.AppendLine();
        }

        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();
    }

    private static void AppendBuildConfig(StringBuilder sb, BuildConfig build)
    {
        sb.AppendLine("  (build");

        if (HasMainBuildConfig(build.Main))
            AppendMainBuildConfig(sb, build.Main!);

        if (HasTestBuildConfig(build.Test))
            AppendTestBuildConfig(sb, build.Test!);

        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();
    }

    private static void AppendMainBuildConfig(StringBuilder sb, MainBuildConfig main)
    {
        sb.AppendLine("    (main");

        if (main.Namespace is not null)
            sb.AppendLine($"      (namespace \"{main.Namespace}\")");
        if (main.OutputPath is not null)
            sb.AppendLine($"      (output \"{main.OutputPath}\")");
        if (main.Backend is not null)
        {
            var backendStr = main.Backend == OutputMode.Il ? "il" : "csharp";
            sb.AppendLine($"      (backend \"{backendStr}\")");
        }

        foreach (var refPath in main.RefPaths)
            sb.AppendLine($"      (ref \"{refPath}\")");

        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();
    }

    private static void AppendTestBuildConfig(StringBuilder sb, TestBuildConfig test)
    {
        sb.AppendLine("    (test");

        if (test.Namespace is not null)
            sb.AppendLine($"      (namespace \"{test.Namespace}\")");
        if (test.OutputPath is not null)
            sb.AppendLine($"      (output \"{test.OutputPath}\")");

        foreach (var refPath in test.RefPaths)
            sb.AppendLine($"      (ref \"{refPath}\")");

        sb.Length -= Environment.NewLine.Length;
        sb.Append(')');
        sb.AppendLine();
    }

    private static bool HasDependencies(PackageDependencies deps)
    {
        return deps.ZScheme.Count > 0 || deps.NuGet.Count > 0;
    }

    private static bool HasBuildConfig(BuildConfig build)
    {
        return HasMainBuildConfig(build.Main) || HasTestBuildConfig(build.Test);
    }

    private static bool HasMainBuildConfig(MainBuildConfig? main)
    {
        return main is not null
               && (main.Namespace is not null
                   || main.OutputPath is not null
                   || main.Backend is not null
                   || main.RefPaths.Count > 0);
    }

    private static bool HasTestBuildConfig(TestBuildConfig? test)
    {
        return test is not null
               && (test.Namespace is not null
                   || test.OutputPath is not null
                   || test.RefPaths.Count > 0);
    }
}

using System.Runtime.InteropServices;
using Serilog;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package;

/// <summary>
///     Resolves shared-framework reference assembly directories for declared
///     <see cref="FrameworkDependency" /> entries (e.g. Microsoft.AspNetCore.App).
///     Picks the highest installed version under <c>$DOTNET_ROOT/shared/&lt;id&gt;/</c>.
/// </summary>
public static class FrameworkResolver
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(FrameworkResolver));

    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<FrameworkDependency> frameworks,
        DiagnosticBag diagnostics)
    {
        if (frameworks.Count == 0) return [];

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            // Fall back to probing the dotnet runtime directory's grandparent.
            // RuntimeDirectory = .../shared/Microsoft.NETCore.App/<ver>/  → up 3 = dotnet root.
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }

        var sharedRoot = Path.Combine(dotnetRoot, "shared");
        var result = new List<string>();
        foreach (var fw in frameworks)
        {
            var fwDir = Path.Combine(sharedRoot, fw.Id);
            if (!Directory.Exists(fwDir))
            {
                diagnostics.Error(
                    $"Framework '{fw.Id}' is not installed at {fwDir}. Install the matching .NET runtime.",
                    fw.Span);
                continue;
            }

            var bestVersion = Directory.GetDirectories(fwDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => (Name: n!, Parsed: TryParseVersion(n!)))
                .Where(v => v.Parsed is not null)
                .OrderByDescending(v => v.Parsed)
                .FirstOrDefault();

            if (bestVersion.Name is null)
            {
                diagnostics.Error(
                    $"No installed versions of framework '{fw.Id}' found under {fwDir}.",
                    fw.Span);
                continue;
            }

            result.Add(Path.Combine(fwDir, bestVersion.Name));
            Log.Debug("FrameworkResolver: {Id} → {Path}", fw.Id, Path.Combine(fwDir, bestVersion.Name));
        }

        return result;
    }

    private static Version? TryParseVersion(string s)
    {
        return Version.TryParse(s, out var v) ? v : null;
    }
}

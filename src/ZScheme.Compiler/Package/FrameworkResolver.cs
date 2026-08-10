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

    /// <param name="dotnetRoot">
    ///     The dotnet root to resolve against, defaulting to <see cref="DefaultDotnetRoot" />.
    ///     This exists so a test can point the resolver at a fake root without writing
    ///     <c>DOTNET_ROOT</c>, which is process-wide: mutating it raced every other test that
    ///     resolves a real framework — <c>PackageAutoInstallerTests</c> failed intermittently
    ///     because its compile landed inside the window where the env var pointed at a temp
    ///     directory, so Microsoft.AspNetCore.App resolved to nothing (or to an empty fake).
    /// </param>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<FrameworkDependency> frameworks,
        DiagnosticBag diagnostics,
        string? dotnetRoot = null
    )
    {
        if (frameworks.Count == 0)
            return [];

        var sharedRoot = Path.Combine(dotnetRoot ?? DefaultDotnetRoot(), "shared");
        var result = new List<string>();
        foreach (var fw in frameworks)
        {
            var fwDir = Path.Combine(sharedRoot, fw.Id);
            if (!Directory.Exists(fwDir))
            {
                diagnostics.Error(
                    $"Framework '{fw.Id}' is not installed at {fwDir}. Install the matching .NET runtime.",
                    fw.Span
                );
                continue;
            }

            var bestVersion = Directory
                .GetDirectories(fwDir)
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
                    fw.Span
                );
                continue;
            }

            result.Add(Path.Combine(fwDir, bestVersion.Name));
            Log.Debug(
                "FrameworkResolver: {Id} → {Path}",
                fw.Id,
                Path.Combine(fwDir, bestVersion.Name)
            );
        }

        return result;
    }

    /// <summary><c>DOTNET_ROOT</c> when it is set, otherwise the dotnet runtime directory's
    ///     grandparent: RuntimeDirectory is <c>.../shared/Microsoft.NETCore.App/&lt;ver&gt;/</c>,
    ///     so up 3 is the root.</summary>
    internal static string DefaultDotnetRoot()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
            return dotnetRoot;

        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        return Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
    }

    private static Version? TryParseVersion(string s)
    {
        return Version.TryParse(s, out var v) ? v : null;
    }
}

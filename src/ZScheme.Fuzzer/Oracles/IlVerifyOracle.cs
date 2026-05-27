using System.Text;
using ZScheme.Fuzzer.Runtime;

namespace ZScheme.Fuzzer.Oracles;

public static class IlVerifyOracle
{
    public const string Name = "ilverify";

    public static OracleResult Run(CompiledArtifacts artifacts, string scratchDir, TimeSpan timeout)
    {
        if (artifacts.IlResult is null)
            return OracleResult.Fail(Name, "no IL output available");

        Directory.CreateDirectory(scratchDir);
        var dllPath = Path.Combine(scratchDir, "il.dll");
        File.WriteAllBytes(dllPath, artifacts.IlResult.OutputBytes);
        WriteRuntimeConfig(Path.ChangeExtension(dllPath, ".runtimeconfig.json"));

        var args = new List<string> { "ilverify", dllPath };
        foreach (var r in ReferenceAssemblyResolver.ReferenceDlls)
        {
            args.Add("-r");
            args.Add(r);
        }

        var result = ProcessRunner.Run(FuzzEnv.DotnetPath, args, timeout, workingDir: FuzzEnv.RepoRoot);

        if (result.TimedOut)
            return OracleResult.Fail(Name, "ilverify timed out",
                $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");

        if (result.ExitCode != 0 || HasVerificationErrors(result.Stdout, result.Stderr))
        {
            var details = new StringBuilder();
            details.AppendLine($"exit={result.ExitCode}");
            details.AppendLine("--- stdout ---");
            details.AppendLine(result.Stdout);
            details.AppendLine("--- stderr ---");
            details.AppendLine(result.Stderr);
            return OracleResult.Fail(Name, $"ilverify reported errors (exit={result.ExitCode})",
                details.ToString());
        }

        return OracleResult.Ok(Name);
    }

    private static bool HasVerificationErrors(string stdout, string stderr)
    {
        foreach (var line in (stdout + "\n" + stderr).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("All Classes and Methods", StringComparison.Ordinal)) continue;
            if (trimmed.Contains("[IL]:", StringComparison.Ordinal)) return true;
            if (trimmed.Contains("Error:", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    internal static void WriteRuntimeConfig(string path)
    {
        var version = Environment.Version;
        var config = $$"""
                       {
                         "runtimeOptions": {
                           "tfm": "net{{version.Major}}.{{version.Minor}}",
                           "framework": {
                             "name": "Microsoft.NETCore.App",
                             "version": "{{version.Major}}.{{version.Minor}}.0"
                           }
                         }
                       }
                       """;
        File.WriteAllText(path, config);
    }
}

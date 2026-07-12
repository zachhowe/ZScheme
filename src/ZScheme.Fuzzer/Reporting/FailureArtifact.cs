using System.Text.Json;
using ZScheme.Fuzzer.Generation;
using ZScheme.Fuzzer.Oracles;

namespace ZScheme.Fuzzer.Reporting;

public static class FailureArtifact
{
    public static string Write(
        string sessionDir,
        GeneratedProgram program,
        CompiledArtifacts? artifacts,
        OracleResult failure,
        string caseScratchDir
    )
    {
        var dir = Path.Combine(
            sessionDir,
            "artifacts",
            $"fuzz-failure-{(uint)program.CaseSeed:x8}"
        );
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "original.zs"), program.Source);

        // Aux modules must keep their declared module name as the file name: the
        // ModuleResolver locates an import by looking for `<module-name>.zs` on a
        // search path. Any decoration here (an `original-aux-` prefix, say) makes
        // the saved artifact unresolvable, and `--repro` dies with
        // "Module not found" on both backends instead of replaying the bug.
        foreach (var aux in program.Aux)
            File.WriteAllText(Path.Combine(dir, aux.FileName), aux.Source);

        if (artifacts?.CsResult is { } cs)
            File.WriteAllText(Path.Combine(dir, "csharp-output.cs"), cs.CsOutput);

        if (artifacts?.IlResult is { } il)
        {
            File.WriteAllBytes(Path.Combine(dir, "il-output.dll"), il.OutputBytes);
            IlVerifyOracle.WriteRuntimeConfig(Path.Combine(dir, "il-output.runtimeconfig.json"));
        }

        if (Directory.Exists(caseScratchDir))
            foreach (var file in Directory.EnumerateFiles(caseScratchDir))
            {
                var dest = Path.Combine(dir, "scratch-" + Path.GetFileName(file));
                try
                {
                    File.Copy(file, dest, true);
                }
                catch { }
            }

        var report = new
        {
            caseSeed = program.CaseSeed,
            caseSeedHex = $"{(uint)program.CaseSeed:x8}",
            moduleName = program.ModuleName,
            auxModules = program.Aux.Select(a => a.ModuleName).ToArray(),
            oracle = failure.OracleName,
            summary = failure.Summary,
            details = failure.Details,
        };
        File.WriteAllText(
            Path.Combine(dir, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
        );

        return dir;
    }
}

using System.Text.RegularExpressions;
using ZScheme.Fuzzer.Generation;
using ZScheme.Fuzzer.Oracles;
using ZScheme.Fuzzer.Runtime;

namespace ZScheme.Fuzzer;

/// <summary>
///     Re-runs the compile + differential-exec oracles on a single existing
///     <c>.zs</c> file. Invaluable for reducing a fuzzer failure artifact down to
///     a minimal repro by hand: edit the file, re-run, observe whether the
///     IL-vs-C# divergence still fires.
///
///     Usage: <c>zs-fuzz --repro &lt;file.zs&gt; [--aux &lt;dir&gt;]</c>
/// </summary>
public static class ReproRunner
{
    // Generous compared to the fuzzer's per-case budget: a repro is one case being inspected by
    // hand, so waiting is cheaper than a spurious timeout verdict.
    private static readonly TimeSpan ReproTimeout = TimeSpan.FromSeconds(30);

    public static int Run(string filePath, string? auxDir)
    {
        var repoRoot =
            FindRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root (ZScheme.slnx).");
        FuzzEnv.Initialize(repoRoot);
        var stdlibPath = Path.Combine(repoRoot, "packages", "stdlib", "src");
        var optsFactory = new CompilerOptionsFactory(stdlibPath);
        _ = ReferenceAssemblyResolver.ReferenceDlls;

        var source = File.ReadAllText(filePath);
        var moduleName = Regex.Match(source, @"\(module\s+([^\s)]+)\)").Groups[1].Value;
        if (string.IsNullOrEmpty(moduleName))
            moduleName = Path.GetFileNameWithoutExtension(filePath);

        // Failure artifacts save their aux modules next to the main source, so
        // default the search path to the repro's own directory. That makes a saved
        // artifact (or a repro copied out of one) replayable as-is; --aux stays
        // available for a hand-reduced repro whose modules live elsewhere.
        auxDir ??= Path.GetDirectoryName(Path.GetFullPath(filePath));

        var aux = new List<AuxModule>();
        if (auxDir is not null && Directory.Exists(auxDir))
            foreach (var f in Directory.GetFiles(auxDir, "*.zs"))
            {
                if (Path.GetFullPath(f) == Path.GetFullPath(filePath))
                    continue;
                aux.Add(new AuxModule(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));
            }

        var program = new GeneratedProgram(source, 0, moduleName, aux);
        var extraSearchPaths = auxDir is null ? null : new[] { auxDir };

        var (artifacts, compile) = CompileConsistencyOracle.Run(
            program,
            optsFactory,
            extraSearchPaths
        );
        Console.WriteLine($"[compile] {(compile.Passed ? "PASS" : "FAIL")}: {compile.Summary}");

        var scratch = Path.Combine(Path.GetTempPath(), $"zs-repro-{moduleName}");
        Directory.CreateDirectory(scratch);

        // Even when the consistency oracle fails (e.g. one backend emits
        // uncompilable code), run whichever backend did produce a loadable
        // assembly so the divergence can be inspected directly.
        if (artifacts.IlResult is not null)
            Console.WriteLine(
                "[il-run] "
                    + DifferentialExecOracle.DescribeOutOfProcess(
                        artifacts.IlResult.OutputBytes,
                        program.ModuleName,
                        Path.Combine(scratch, "il-run"),
                        ReproTimeout
                    )
            );

        if (!compile.Passed)
        {
            Console.WriteLine(compile.Details);
            return 1;
        }

        // Out-of-process throughout: a repro is a single case where surviving whatever the
        // saved artifact does matters far more than the two process spawns it costs. A
        // deep-recursion finding is only reproducible this way at all.
        var diff = DifferentialExecOracle.Run(artifacts, scratch, ReproTimeout, true);
        Console.WriteLine($"[diffexec] {(diff.Passed ? "PASS" : "FAIL")}: {diff.Summary}");
        if (!diff.Passed)
        {
            Console.WriteLine(diff.Details);
            return 1;
        }

        Console.WriteLine("All oracles passed (no divergence).");
        return 0;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ZScheme.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }
}

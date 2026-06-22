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

        var aux = new List<AuxModule>();
        if (auxDir is not null && Directory.Exists(auxDir))
            foreach (var f in Directory.GetFiles(auxDir, "*.zs"))
                aux.Add(new AuxModule(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));

        var program = new GeneratedProgram(source, 0, moduleName, aux);
        var extraSearchPaths = auxDir is null ? null : new[] { auxDir };

        var (artifacts, compile) = CompileConsistencyOracle.Run(
            program,
            optsFactory,
            extraSearchPaths
        );
        Console.WriteLine($"[compile] {(compile.Passed ? "PASS" : "FAIL")}: {compile.Summary}");

        // Even when the consistency oracle fails (e.g. one backend emits
        // uncompilable code), run whichever backend did produce a loadable
        // assembly so the divergence can be inspected directly.
        if (artifacts.IlResult is not null)
            Console.WriteLine(
                $"[il-run] {DescribeRun(artifacts.IlResult.OutputBytes, program.ModuleName)}"
            );

        if (!compile.Passed)
        {
            Console.WriteLine(compile.Details);
            return 1;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"zs-repro-{moduleName}");
        Directory.CreateDirectory(scratch);
        var diff = DifferentialExecOracle.Run(artifacts, scratch, TimeSpan.FromSeconds(30));
        Console.WriteLine($"[diffexec] {(diff.Passed ? "PASS" : "FAIL")}: {diff.Summary}");
        if (!diff.Passed)
        {
            Console.WriteLine(diff.Details);
            return 1;
        }

        Console.WriteLine("All oracles passed (no divergence).");
        return 0;
    }

    private static string DescribeRun(byte[] assemblyBytes, string moduleName)
    {
        try
        {
            var asm = System.Reflection.Assembly.Load(assemblyBytes);
            var method = asm.GetExportedTypes()
                .SelectMany(t => t.GetMethods())
                .FirstOrDefault(m =>
                    m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 0
                );
            if (method is null)
                return "no Compute() found";
            var result = method.Invoke(null, null);
            if (result is System.Threading.Tasks.Task<int> t)
                return $"returned {t.GetAwaiter().GetResult()}";
            return $"returned {result}";
        }
        catch (Exception ex)
        {
            return $"threw {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}";
        }
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

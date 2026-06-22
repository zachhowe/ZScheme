using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ZScheme.Fuzzer;
using ZScheme.Fuzzer.Generation;
using ZScheme.Fuzzer.Oracles;
using ZScheme.Fuzzer.Reporting;
using ZScheme.Fuzzer.Runtime;

if (args.Length >= 2 && args[0] == "--repro")
{
    var auxIdx = Array.IndexOf(args, "--aux");
    var auxDir = auxIdx >= 0 && auxIdx + 1 < args.Length ? args[auxIdx + 1] : null;
    return ReproRunner.Run(args[1], auxDir);
}

FuzzerOptions opts;
try
{
    opts = FuzzerOptions.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}

var repoRoot =
    opts.RepoRoot
    ?? FindRepoRoot()
    ?? throw new InvalidOperationException(
        "Could not find repo root (ZScheme.slnx). Pass --repo-root explicitly."
    );

FuzzEnv.Initialize(repoRoot);

var stdlibPath = Path.Combine(repoRoot, "packages", "stdlib", "src");
var optsFactory = new CompilerOptionsFactory(stdlibPath);

var outputBase = opts.OutputDir ?? Path.Combine(repoRoot, "fuzz-runs");
var sessionStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var sessionDir = Path.Combine(outputBase, $"{sessionStamp}-seed{(uint)opts.Seed:x8}");
var scratchRoot = Path.Combine(sessionDir, "scratch");
Directory.CreateDirectory(sessionDir);
Directory.CreateDirectory(scratchRoot);

Console.WriteLine(
    $"zs-fuzz  seed=0x{(uint)opts.Seed:x8} ({opts.Seed})  iterations={opts.Iterations}"
);
Console.WriteLine($"         session: {sessionDir}");
Console.WriteLine($"         oracles: {string.Join(",", opts.Oracles)}");
Console.WriteLine($"         workers: {opts.Workers}");
Console.WriteLine();

_ = ReferenceAssemblyResolver.ReferenceDlls;

var master = new Random((int)(opts.Seed ^ (opts.Seed >> 32)));

// Pre-derive case seeds on the main thread so a given --seed produces the
// same case set regardless of --workers. Each case's RNG is then seeded from
// its caseSeed, keeping per-case generation bit-for-bit reproducible.
var caseSeeds = new long[opts.Iterations];
for (var i = 0; i < opts.Iterations; i++)
    caseSeeds[i] = master.NextInt64() & 0x7FFFFFFFFFFFFFFF;

var counts = new ConcurrentDictionary<string, int>();
foreach (
    var key in new[]
    {
        "generated",
        "oracle.compile.passed",
        "oracle.compile.failed",
        "oracle.ilverify.passed",
        "oracle.ilverify.failed",
        "oracle.ilverify.skipped",
        "oracle.diffexec.passed",
        "oracle.diffexec.failed",
        "oracle.diffexec.skipped",
        "total.failures",
    }
)
    counts[key] = 0;

var failureArtifactPaths = new List<string>();
var failuresLock = new object();
var consoleLock = new object();
var logLock = new object();

var casesLogPath = Path.Combine(sessionDir, "cases.jsonl");
using var casesLog = new StreamWriter(casesLogPath, true);

var sessionSw = Stopwatch.StartNew();
try
{
    Parallel.For(
        0,
        opts.Iterations,
        new ParallelOptions { MaxDegreeOfParallelism = opts.Workers },
        i =>
        {
            var caseSeed = caseSeeds[i];
            var caseRng = new Random((int)(caseSeed ^ (caseSeed >> 32)));
            var caseGen = new ProgramGenerator(caseRng, opts.MaxDepth, opts.MaxFuncs);
            var program = caseGen.Generate(caseSeed);
            counts.AddOrUpdate("generated", 1, (_, v) => v + 1);

            var caseScratch = Path.Combine(scratchRoot, $"case-{(uint)caseSeed:x8}");
            Directory.CreateDirectory(caseScratch);

            // Write aux modules into caseScratch/aux so the compiler's ModuleResolver
            // can find them via an extra search path. Kept per-case so seeds are isolated.
            string? auxDir = null;
            if (program.Aux.Count > 0)
            {
                auxDir = Path.Combine(caseScratch, "aux");
                Directory.CreateDirectory(auxDir);
                foreach (var aux in program.Aux)
                    File.WriteAllText(Path.Combine(auxDir, aux.FileName), aux.Source);
            }

            var caseSw = Stopwatch.StartNew();
            var (artifacts, outcome, stageResults) = RunOracles(
                program,
                optsFactory,
                caseScratch,
                opts,
                auxDir
            );
            caseSw.Stop();

            foreach (var (oracle, result) in stageResults)
            {
                var status = result switch
                {
                    null => "skipped",
                    { Passed: true } => "passed",
                    _ => "failed",
                };
                counts.AddOrUpdate($"oracle.{oracle}.{status}", 1, (_, v) => v + 1);
            }

            if (outcome is null)
            {
                lock (logLock)
                {
                    WriteCaseLog(casesLog, program, true, null, caseSw.Elapsed, opts.KeepPassing);
                }

                if (opts.Verbose)
                    lock (consoleLock)
                    {
                        Console.WriteLine(
                            $"  [{i + 1}/{opts.Iterations}] ok  seed=0x{(uint)caseSeed:x8}  ({caseSw.ElapsedMilliseconds}ms)"
                        );
                    }

                TryCleanupScratch(caseScratch);
            }
            else
            {
                counts.AddOrUpdate("total.failures", 1, (_, v) => v + 1);

                var artifactDir = FailureArtifact.Write(
                    sessionDir,
                    program,
                    artifacts,
                    outcome,
                    caseScratch
                );
                lock (failuresLock)
                {
                    failureArtifactPaths.Add(artifactDir);
                }

                lock (logLock)
                {
                    WriteCaseLog(casesLog, program, false, outcome, caseSw.Elapsed, true);
                }

                lock (consoleLock)
                {
                    Console.WriteLine(
                        $"  [{i + 1}/{opts.Iterations}] FAIL ({outcome.OracleName}) seed=0x{(uint)caseSeed:x8}"
                    );
                    Console.WriteLine($"         {outcome.Summary}");
                    Console.WriteLine($"         artifact: {artifactDir}");
                }
            }
        }
    );
}
finally
{
    sessionSw.Stop();
    casesLog.Flush();
}

Console.WriteLine();
Console.WriteLine(
    $"Summary  (seed=0x{(uint)opts.Seed:x8}, {opts.Iterations} iterations, {sessionSw.Elapsed.TotalSeconds:F1}s)"
);
Console.WriteLine($"  Generated:              {counts["generated"]}");
Console.WriteLine(
    $"  Compile-consistent:     {counts["oracle.compile.passed"]}   ({counts["oracle.compile.failed"]} failed)"
);
Console.WriteLine(
    $"  ilverify passed:        {counts["oracle.ilverify.passed"]}   ({counts["oracle.ilverify.failed"]} failed)"
);
Console.WriteLine(
    $"  diff-exec agreed:       {counts["oracle.diffexec.passed"]}   ({counts["oracle.diffexec.failed"]} failed)"
);
Console.WriteLine($"  Total failures:         {counts["total.failures"]}");

var summary = new
{
    seed = opts.Seed,
    seedHex = $"{(uint)opts.Seed:x8}",
    iterations = opts.Iterations,
    durationSeconds = sessionSw.Elapsed.TotalSeconds,
    counts,
    failureArtifacts = failureArtifactPaths,
    oracles = opts.Oracles.Select(o => o.ToString()).ToArray(),
};
File.WriteAllText(
    Path.Combine(sessionDir, "session.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })
);

return counts["total.failures"] > 0 ? 1 : 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ZScheme.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}

static (
    CompiledArtifacts? Artifacts,
    OracleResult? Failure,
    List<(string Name, OracleResult? Result)> Stages
) RunOracles(
    GeneratedProgram program,
    CompilerOptionsFactory optsFactory,
    string caseScratch,
    FuzzerOptions opts,
    string? auxDir
)
{
    var stages = new List<(string, OracleResult?)>();
    CompiledArtifacts? artifacts;

    var extraSearchPaths = auxDir is null ? null : new[] { auxDir };
    var (art, compileResult) = CompileConsistencyOracle.Run(program, optsFactory, extraSearchPaths);
    artifacts = art;
    stages.Add(("compile", compileResult));

    if (opts.Oracles.Contains(OracleKind.Compile) && !compileResult.Passed)
    {
        stages.Add(("ilverify", null));
        stages.Add(("diffexec", null));
        return (artifacts, compileResult, stages);
    }

    if (artifacts.CsResult is null || artifacts.IlResult is null)
    {
        stages.Add(("ilverify", null));
        stages.Add(("diffexec", null));
        return (
            artifacts,
            OracleResult.Fail(
                "compile",
                "compile failed (pre-oracle)",
                "one or both backends produced no output"
            ),
            stages
        );
    }

    if (opts.Oracles.Contains(OracleKind.IlVerify))
    {
        var ilv = IlVerifyOracle.Run(artifacts, caseScratch, opts.PerCaseTimeout);
        stages.Add(("ilverify", ilv));
        if (!ilv.Passed)
        {
            stages.Add(("diffexec", null));
            return (artifacts, ilv, stages);
        }
    }
    else
    {
        stages.Add(("ilverify", null));
    }

    if (opts.Oracles.Contains(OracleKind.DiffExec))
    {
        var diff = DifferentialExecOracle.Run(artifacts, caseScratch, opts.PerCaseTimeout);
        stages.Add(("diffexec", diff));
        if (!diff.Passed)
            return (artifacts, diff, stages);
    }
    else
    {
        stages.Add(("diffexec", null));
    }

    return (artifacts, null, stages);
}

static void WriteCaseLog(
    StreamWriter log,
    GeneratedProgram program,
    bool passed,
    OracleResult? outcome,
    TimeSpan elapsed,
    bool keepPassing
)
{
    var record = new
    {
        caseSeed = program.CaseSeed,
        caseSeedHex = $"{(uint)program.CaseSeed:x8}",
        durationMs = (long)elapsed.TotalMilliseconds,
        passed,
        oracle = outcome?.OracleName,
        summary = outcome?.Summary,
        source = passed && !keepPassing ? null : program.Source,
    };
    log.WriteLine(JsonSerializer.Serialize(record));
}

static void TryCleanupScratch(string path)
{
    try
    {
        Directory.Delete(path, true);
    }
    catch { }
}

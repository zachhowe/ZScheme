using System.Globalization;

namespace ZScheme.Fuzzer;

public enum OracleKind
{
    Compile,
    IlVerify,
    DiffExec,
}

public sealed class FuzzerOptions
{
    public long Seed { get; set; }
    public int Iterations { get; set; } = 1000;
    public int MaxDepth { get; set; } = 5;
    public int MaxFuncs { get; set; } = 3;
    public string? OutputDir { get; set; }
    public bool KeepPassing { get; set; }

    public List<OracleKind> Oracles { get; set; } =
    [OracleKind.Compile, OracleKind.IlVerify, OracleKind.DiffExec];

    public TimeSpan PerCaseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public string? RepoRoot { get; set; }
    public bool Verbose { get; set; }
    public int Workers { get; set; } = Environment.ProcessorCount;

    public static FuzzerOptions Parse(string[] args)
    {
        var opts = new FuzzerOptions();
        var seedSet = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--seed" when i + 1 < args.Length:
                    opts.Seed = ParseSeed(args[++i]);
                    seedSet = true;
                    break;
                case "--iterations" or "-n" when i + 1 < args.Length:
                    opts.Iterations = ParseIntArg("--iterations", args[++i]);
                    break;
                case "--max-depth" when i + 1 < args.Length:
                    opts.MaxDepth = ParseIntArg("--max-depth", args[++i]);
                    break;
                case "--max-funcs" when i + 1 < args.Length:
                    opts.MaxFuncs = ParseIntArg("--max-funcs", args[++i]);
                    break;
                case "--output-dir" when i + 1 < args.Length:
                    opts.OutputDir = args[++i];
                    break;
                case "--repo-root" when i + 1 < args.Length:
                    opts.RepoRoot = args[++i];
                    break;
                case "--keep-passing":
                    opts.KeepPassing = true;
                    break;
                case "--timeout" when i + 1 < args.Length:
                    opts.PerCaseTimeout = TimeSpan.FromSeconds(
                        ParseDoubleArg("--timeout", args[++i])
                    );
                    break;
                case "--verbose" or "-v":
                    opts.Verbose = true;
                    break;
                case "--oracles" when i + 1 < args.Length:
                    opts.Oracles = ParseOracles(args[++i]);
                    break;
                case "--workers" or "-j" when i + 1 < args.Length:
                    opts.Workers = ParseIntArg("--workers", args[++i]);
                    if (opts.Workers < 1)
                        throw new ArgumentException("--workers must be >= 1");
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }

        if (!seedSet)
            opts.Seed = DateTime.UtcNow.Ticks & 0x7FFFFFFFFFFFFFFF;

        return opts;
    }

    // Seeds are reported everywhere in hex (session dir names, caseSeedHex in
    // cases.jsonl, fuzz-failure-<hex> artifact names), so accept the 0x form back
    // as well as plain decimal. Parse failures must surface as ArgumentException:
    // that is the only exception type the driver catches for the friendly
    // usage-error exit — anything else escapes as an unhandled exception and
    // aborts the process with a core dump.
    private static long ParseSeed(string value)
    {
        try
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Invalid --seed value '{value}': expected a decimal long or 0x-prefixed hex."
            );
        }
    }

    private static int ParseIntArg(string flag, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException($"Invalid {flag} value '{value}': expected an integer.");
        return result;
    }

    private static double ParseDoubleArg(string flag, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException($"Invalid {flag} value '{value}': expected a number.");
        return result;
    }

    private static List<OracleKind> ParseOracles(string csv)
    {
        var result = new List<OracleKind>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kind = part.Trim().ToLowerInvariant() switch
            {
                "compile" => OracleKind.Compile,
                "ilverify" => OracleKind.IlVerify,
                "diffexec" or "diff-exec" => OracleKind.DiffExec,
                _ => throw new ArgumentException($"Unknown oracle: {part}"),
            };
            if (!result.Contains(kind))
                result.Add(kind);
        }

        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Usage: zs-fuzz [options]
              --seed <long>               Seed for the master RNG, decimal or 0x-hex (default: time-based)
              --iterations <n>, -n <n>    Number of cases to generate (default: 1000)
              --max-depth <n>             Max expression tree depth (default: 5)
              --max-funcs <n>             Max user function defs per program (default: 3)
              --oracles <list>            Comma-separated: compile,ilverify,diffexec (default: all)
              --output-dir <path>         Base dir for fuzz-runs/ (default: <repo>/fuzz-runs)
              --repo-root <path>          Override repo root discovery
              --keep-passing              Save passing cases in cases.jsonl with full source
              --timeout <secs>            Per-subprocess timeout (default: 10)
              --workers <n>, -j <n>       Parallel workers (default: ProcessorCount)
              --verbose, -v               Log each case as it runs
              --help, -h                  Show this help
            """
        );
    }
}

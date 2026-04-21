using System.Text;
using ZScheme.Compiler.Pipeline;
using ZScheme.Fuzzer.Generation;

namespace ZScheme.Fuzzer.Oracles;

public static class CompileConsistencyOracle
{
    public const string Name = "compile";

    public static (CompiledArtifacts Artifacts, OracleResult Result) Run(
        GeneratedProgram program,
        CompilerOptionsFactory optsFactory,
        IReadOnlyList<string>? extraSearchPaths = null)
    {
        var (csRaw, csException) = TryCompile(program, optsFactory, OutputMode.CSharp, extraSearchPaths);
        var (ilRaw, ilException) = TryCompile(program, optsFactory, OutputMode.Il, extraSearchPaths);

        var csResult = csRaw as CompilationResult.CSharpOutputResult;
        var ilResult = ilRaw as CompilationResult.IlOutputResult;

        var artifacts = new CompiledArtifacts(program, csResult, ilResult, csRaw, ilRaw);

        // If either backend raised an uncaught exception, surface it as an oracle
        // failure rather than letting it crash the fuzzer process. This typically
        // indicates an internal compiler bug worth investigating.
        if (csException is not null || ilException is not null)
        {
            var summary = (csException is not null, ilException is not null) switch
            {
                (true, true) => "both backends threw exceptions during compile",
                (true, false) => "C# backend threw exception during compile",
                (false, true) => "IL backend threw exception during compile",
                _ => "unexpected state",
            };
            var details = new StringBuilder();
            if (csException is not null) details.Append("[csharp exception]\n").Append(csException).Append("\n---\n");
            if (ilException is not null) details.Append("[il exception]\n").Append(ilException);
            return (artifacts, OracleResult.Fail(Name, summary, details.ToString()));
        }

        var csOk = csRaw!.Success && csResult is not null;
        var ilOk = ilRaw!.Success && ilResult is not null;

        if (csOk && ilOk) return (artifacts, OracleResult.Ok(Name));

        if (!csOk && !ilOk)
        {
            var summary = "both backends failed to compile";
            var details = Describe(csRaw, "csharp") + "\n---\n" + Describe(ilRaw, "il");
            return (artifacts, OracleResult.Fail(Name, summary, details));
        }

        var which = csOk ? "IL only" : "C# only";
        var failedDetails = csOk ? Describe(ilRaw, "il") : Describe(csRaw, "csharp");
        return (artifacts, OracleResult.Fail(Name,
            $"only one backend succeeded ({which} failed)",
            failedDetails));
    }

    private static (CompilationResult? Result, Exception? Exception) TryCompile(
        GeneratedProgram program,
        CompilerOptionsFactory optsFactory,
        OutputMode mode,
        IReadOnlyList<string>? extraSearchPaths)
    {
        try
        {
            var opts = optsFactory.Build(mode, extraSearchPaths);
            var compilation = new Compilation(opts);
            return (compilation.Compile(program.Source, program.FileName), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static string Describe(CompilationResult result, string backend)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{backend}] type: {result.GetType().Name}, success: {result.Success}");
        foreach (var d in result.Diagnostics.Diagnostics)
            sb.AppendLine($"  {d}");
        return sb.ToString();
    }
}

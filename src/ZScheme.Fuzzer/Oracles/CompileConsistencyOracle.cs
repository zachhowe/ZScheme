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
        var csOpts = optsFactory.Build(OutputMode.CSharp, extraSearchPaths);
        var csCompilation = new Compilation(csOpts);
        var csRaw = csCompilation.Compile(program.Source, program.FileName);
        var csResult = csRaw as CompilationResult.CSharpOutputResult;

        var ilOpts = optsFactory.Build(OutputMode.Il, extraSearchPaths);
        var ilCompilation = new Compilation(ilOpts);
        var ilRaw = ilCompilation.Compile(program.Source, program.FileName);
        var ilResult = ilRaw as CompilationResult.IlOutputResult;

        var artifacts = new CompiledArtifacts(program, csResult, ilResult, csRaw, ilRaw);

        var csOk = csRaw.Success && csResult is not null;
        var ilOk = ilRaw.Success && ilResult is not null;

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

    private static string Describe(CompilationResult result, string backend)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{backend}] type: {result.GetType().Name}, success: {result.Success}");
        foreach (var d in result.Diagnostics.Diagnostics)
            sb.AppendLine($"  {d}");
        return sb.ToString();
    }
}

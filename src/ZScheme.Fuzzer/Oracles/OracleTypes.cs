using ZScheme.Compiler.Pipeline;
using ZScheme.Fuzzer.Generation;

namespace ZScheme.Fuzzer.Oracles;

public sealed record OracleResult(bool Passed, string OracleName, string Summary, string? Details)
{
    public static OracleResult Ok(string name)
    {
        return new OracleResult(true, name, "ok", null);
    }

    public static OracleResult Fail(string name, string summary, string? details = null)
    {
        return new OracleResult(false, name, summary, details);
    }
}

public sealed record CompiledArtifacts(
    GeneratedProgram Program,
    CompilationResult.CSharpOutputResult? CsResult,
    CompilationResult.IlOutputResult? IlResult,
    CompilationResult? CsRaw,
    CompilationResult? IlRaw
);

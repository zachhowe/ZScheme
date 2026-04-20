namespace ZScheme.Fuzzer.Generation;

public sealed record GeneratedProgram(string Source, long CaseSeed, string ModuleName)
{
    public string FileName => $"{ModuleName}.zs";
}

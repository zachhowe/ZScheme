namespace ZScheme.Fuzzer.Generation;

public sealed record GeneratedProgram(
    string Source,
    long CaseSeed,
    string ModuleName,
    IReadOnlyList<AuxModule> Aux)
{
    public string FileName => $"{ModuleName}.zs";
}

public sealed record AuxModule(string ModuleName, string Source)
{
    public string FileName => $"{ModuleName}.zs";
}

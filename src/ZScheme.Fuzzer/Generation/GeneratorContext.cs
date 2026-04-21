namespace ZScheme.Fuzzer.Generation;

public sealed class GeneratorContext
{
    public Random Rng { get; }
    public int MaxDepth { get; }
    public int MaxFuncs { get; }
    public List<UserFunc> UserFuncs { get; } = [];
    public List<UserUnionDecl> UserUnions { get; } = [];
    public List<UserRecordDecl> UserRecords { get; } = [];
    public HashSet<StdlibImport> Imports { get; } = [];
    public List<AuxExport> AuxExports { get; } = [];
    public List<AuxModule> AuxModules { get; } = [];

    private int _nameCounter;

    public GeneratorContext(Random rng, int maxDepth, int maxFuncs)
    {
        Rng = rng;
        MaxDepth = Math.Max(1, maxDepth);
        MaxFuncs = Math.Max(0, maxFuncs);
    }

    public void ResetPerCase()
    {
        _nameCounter = 0;
        UserFuncs.Clear();
        UserUnions.Clear();
        UserRecords.Clear();
        Imports.Clear();
        AuxExports.Clear();
        AuxModules.Clear();
    }

    public string Fresh() => $"x{_nameCounter++}";

    public T PickWeighted<T>(IReadOnlyList<(int Weight, T Value)> options)
    {
        var total = options.Sum(o => o.Weight);
        var pick = Rng.Next(total);
        var acc = 0;
        foreach (var (w, v) in options)
        {
            acc += w;
            if (pick < acc) return v;
        }
        return options[^1].Value;
    }
}

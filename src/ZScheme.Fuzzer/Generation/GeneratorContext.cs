namespace ZScheme.Fuzzer.Generation;

public sealed class GeneratorContext
{
    private int _nameCounter;

    public GeneratorContext(Random rng, int maxDepth, int maxFuncs)
    {
        Rng = rng;
        MaxDepth = Math.Max(1, maxDepth);
        MaxFuncs = Math.Max(0, maxFuncs);
    }

    public Random Rng { get; }
    public int MaxDepth { get; }
    public int MaxFuncs { get; }
    public List<UserFunc> UserFuncs { get; } = [];
    public List<UserUnionDecl> UserUnions { get; } = [];
    public List<UserRecordDecl> UserRecords { get; } = [];
    public List<UserClassDecl> UserClasses { get; } = [];
    public List<UserInterfaceDecl> UserInterfaces { get; } = [];
    public HashSet<StdlibImport> Imports { get; } = [];
    public HashSet<ClrBinding> EmittedClrBindings { get; } = [];
    public List<AuxExport> AuxExports { get; } = [];
    public List<AuxModule> AuxModules { get; } = [];

    // Names of generated `define-syntax` macros whose expansions produce an
    // Int-valued expression. ExprGenerator's GenInt emits use sites for any
    // registered macro. Each entry is a tuple of (macroName, arity) where arity
    // is the number of Int positional arguments the macro pattern accepts.
    public List<(string Name, int IntArity)> MacroIntCallables { get; } = [];

    // Per-program flag: when set, ProgramGenerator emits `(import-clr [...
    // :instance ...])` aliases for every user-class instance method, and
    // ExprGenerator's weight tables enable the construct-and-call reducer.
    // Gated to a fraction of cases because the IL backend currently has a
    // known stack-imbalance bug on this path; the gate keeps the fuzzer's
    // failure-artifact stream from being dominated by identical reports.
    public bool EnableClassInstanceCalls { get; set; }

    // Per-program flag: when set, ProgramGenerator emits compute as
    // `(define-async (compute) : (Task Int) ...)` instead of the synchronous form
    // and AsyncExprGenerator drives the body. DifferentialExecOracle awaits the
    // returned Task<int> to obtain the comparison value.
    public bool ComputeIsAsync { get; set; }

    public IEnumerable<UserFunc> SyncUserFuncs => UserFuncs.Where(f => !f.IsAsync);
    public IEnumerable<UserFunc> AsyncUserFuncs => UserFuncs.Where(f => f.IsAsync);

    public void ResetPerCase()
    {
        _nameCounter = 0;
        UserFuncs.Clear();
        UserUnions.Clear();
        UserRecords.Clear();
        UserClasses.Clear();
        UserInterfaces.Clear();
        Imports.Clear();
        EmittedClrBindings.Clear();
        AuxExports.Clear();
        AuxModules.Clear();
        MacroIntCallables.Clear();
        EnableClassInstanceCalls = false;
        ComputeIsAsync = false;
    }

    public string Fresh()
    {
        return $"x{_nameCounter++}";
    }

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

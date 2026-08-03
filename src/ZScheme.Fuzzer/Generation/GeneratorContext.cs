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

    // Per-program flag: when set, ProgramGenerator emits the `(delegate ...)`
    // helper defines (DelegateExprGenerator.EmitHelpers) into the main module
    // and ExprGenerator's GenInt enables the delegate reducers. Gated to a
    // fraction of cases so delegate-shaped programs are well represented without
    // crowding out other forms.
    public bool EnableDelegateForms { get; set; }

    // Per-program flag: when set, ExprGenerator's GenInt enables the stdlib/core
    // `is-null?` reducer. Gated to a fraction of cases because `is-null?` lowers
    // to a ReferenceEquals whose boxing path is suspected to differ between the
    // C# and IL backends; the low gate keeps this probe present without
    // dominating the failure-artifact stream.
    public bool EnableNullChecks { get; set; }

    // Per-program flag: when set, literal-only matches (int/float/string/tuple)
    // may omit their catchall arm. Both backends throw
    // InvalidOperationException("Non-exhaustive match") on fall-through, so the
    // outcome stays oracle-comparable; the match is wrapped in `with-handlers`
    // so the program computes a value either way. Gated per-case so a systemic
    // divergence in fall-through handling can't flood the artifact stream.
    public bool EnableMatchFallthrough { get; set; }

    // Per-program flag: when set, binder sites (let / let* / lambda params /
    // match binders) occasionally reuse an in-scope name of the same ExprType
    // instead of a fresh one, exercising each backend's independent
    // shadow-handling paths (C# rename machinery vs IL local slots).
    public bool EnableShadowing { get; set; }

    // Per-program flag: when set, StringExprGenerator may emit raw non-ASCII /
    // surrogate-pair / control characters inside string literals. Gated low
    // because encoding of such source is a deliberate, suspected-divergent probe.
    public bool EnableUnicodeStrings { get; set; }

    // Per-program flag: when set, NestedDefineExprGenerator may emit a body-level `define` — a
    // definition inside another function's body. AstBuilder desugars a run of those into a
    // `letrec`, so the form probes the desugar's grouping and scoping choices rather than a new
    // lowering path. Gated per-case for the same reason as the other structural forms: a systemic
    // divergence here would otherwise flood the artifact stream.
    public bool EnableNestedDefines { get; set; }

    // Per-program flag: when set, ProgramGenerator emits compute as
    // `(define-async (compute) : (Task Int) ...)` instead of the synchronous form
    // and AsyncExprGenerator drives the body. DifferentialExecOracle awaits the
    // returned Task<int> to obtain the comparison value.
    public bool ComputeIsAsync { get; set; }

    // Transient flag: set while AuxModuleGenerator is driving the shared
    // ExprGenerator to build an aux-module body. Aux modules don't import stdlib
    // and can't see the main module's user types, so scope-dependent constructs
    // (e.g. `typeof` over stdlib generics or user types) must not be emitted
    // there. Unlike the per-program flags above it is toggled within a case, so
    // it is not reset in ResetPerCase (the generator clears it after each module).
    public bool InAuxModule { get; set; }

    // True while generating a class/object method or constructor body, where fields are in
    // bare-name scope. `letrec` is suppressed there: the compiler lifts a recursive group to
    // top-level static functions, which have no instance to read a field through, so a group
    // that happened to close over a field would be a compile error on both backends. Toggled
    // within a case like InAuxModule, so it is not reset in ResetPerCase.
    public bool InInstanceContext { get; set; }

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
        EnableDelegateForms = false;
        EnableNullChecks = false;
        EnableMatchFallthrough = false;
        EnableShadowing = false;
        EnableUnicodeStrings = false;
        EnableNestedDefines = false;
        ComputeIsAsync = false;
    }

    public string Fresh()
    {
        return $"x{_nameCounter++}";
    }

    // Binder-name picker for shadowing coverage: when the per-case flag is on,
    // ~30% of binder sites rebind an existing in-scope name of the same
    // ExprType (same type keeps this Scope bookkeeping sound — Extend
    // overwrites, mirroring the language's innermost-binding-wins semantics).
    public string FreshOrShadow(Scope scope, ExprType type)
    {
        if (EnableShadowing && Rng.NextDouble() < 0.30)
        {
            var vars = scope.GetVars(type);
            if (vars.Count > 0)
                return vars[Rng.Next(vars.Count)];
        }

        return Fresh();
    }

    // Weighted `values` arity in [2,7]: biased low so most tuples stay small,
    // with a bump at 7 — the `values` maximum (AstBuilder errors above 7) and
    // the ValueTuple codegen boundary.
    public int PickTupleArity()
    {
        return PickWeighted([(5, 2), (4, 3), (1, 4), (1, 5), (1, 6), (2, 7)]);
    }

    public T PickWeighted<T>(IReadOnlyList<(int Weight, T Value)> options)
    {
        var total = options.Sum(o => o.Weight);
        var pick = Rng.Next(total);
        var acc = 0;
        foreach (var (w, v) in options)
        {
            acc += w;
            if (pick < acc)
                return v;
        }

        return options[^1].Value;
    }
}

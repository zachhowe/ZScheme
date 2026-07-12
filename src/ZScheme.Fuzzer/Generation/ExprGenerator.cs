using System.Globalization;
using ZScheme.Fuzzer.Generation.Stdlib;

namespace ZScheme.Fuzzer.Generation;

public sealed class ExprGenerator
{
    private readonly GeneratorContext _ctx;
    private ClassExprGenerator? _class;
    private ClrInteropExprGenerator? _clr;
    private ConversionExprGenerator? _conv;
    private DelegateExprGenerator? _delegate;
    private ExceptionExprGenerator? _exception;
    private LetStarExprGenerator? _letStar;
    private MatchExprGenerator? _match;
    private ObjectExprGenerator? _object;
    private PartialExprGenerator? _partial;

    private SequenceExprGenerator? _sequence;

    // Set by ProgramGenerator after construction to break the ctor cycle
    // (each collaborator needs ExprGenerator for inner Int sub-expressions, and
    // ExprGenerator needs them for their respective Int reducers).
    private StdlibGenerators? _stdlibGens;
    private StringExprGenerator? _string;
    private SymbolExprGenerator? _symbol;
    private TupleExprGenerator? _tuple;
    private UseExprGenerator? _use;
    private WidePrimitiveExprGenerator? _widePrim;
    private WithExprGenerator? _with;
    private TypeOfExprGenerator? _typeOf;

    public ExprGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    public void SetStdlibGenerators(StdlibGenerators stdlibGens)
    {
        _stdlibGens = stdlibGens;
    }

    public void SetConversion(ConversionExprGenerator conv)
    {
        _conv = conv;
    }

    public void SetSequence(SequenceExprGenerator sequence)
    {
        _sequence = sequence;
    }

    public void SetTuple(TupleExprGenerator tuple)
    {
        _tuple = tuple;
    }

    public void SetWith(WithExprGenerator with)
    {
        _with = with;
    }

    public void SetPartial(PartialExprGenerator partial)
    {
        _partial = partial;
    }

    public void SetException(ExceptionExprGenerator exception)
    {
        _exception = exception;
    }

    public void SetString(StringExprGenerator str)
    {
        _string = str;
    }

    public void SetClass(ClassExprGenerator cls)
    {
        _class = cls;
    }

    public void SetObject(ObjectExprGenerator obj)
    {
        _object = obj;
    }

    public void SetClrInterop(ClrInteropExprGenerator clr)
    {
        _clr = clr;
    }

    public void SetDelegate(DelegateExprGenerator del)
    {
        _delegate = del;
    }

    public void SetMatch(MatchExprGenerator match)
    {
        _match = match;
    }

    public void SetLetStar(LetStarExprGenerator letStar)
    {
        _letStar = letStar;
    }

    public void SetWidePrim(WidePrimitiveExprGenerator widePrim)
    {
        _widePrim = widePrim;
    }

    public void SetTypeOf(TypeOfExprGenerator typeOf)
    {
        _typeOf = typeOf;
    }

    public void SetUse(UseExprGenerator use)
    {
        _use = use;
    }

    public void SetSymbol(SymbolExprGenerator symbol)
    {
        _symbol = symbol;
    }

    public string GenString(Scope scope, int depth)
    {
        return _string is null
            ? throw new InvalidOperationException("StringExprGenerator not wired")
            : _string.GenString(scope, depth);
    }

    public string GenInt(Scope scope, int depth)
    {
        if (depth <= 0)
            return GenIntLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (3, () => GenIntLeaf(scope)),
            (3, () => GenIntBinOp(scope, depth)),
            (2, () => GenIntDivModOp(scope, depth)),
            (2, () => GenIf(ExprType.Int, scope, depth)),
            (2, () => GenLet(ExprType.Int, scope, depth)),
            (1, () => GenLambdaIife(scope, depth)),
            (2, () => GenMatch(ExprType.Int, scope, depth)),
        };
        if (_letStar is not null)
            weights.Add((2, () => _letStar.LetStarToInt(scope, depth)));
        if (_use is not null)
        {
            weights.Add((2, () => _use.UseToInt(scope, depth)));
            weights.Add((1, () => _use.UseStarToInt(scope, depth)));
            weights.Add((1, () => _use.UseDisposeOnThrowToInt(scope, depth)));
        }
        if (_ctx.SyncUserFuncs.Any())
            weights.Add((2, () => GenCall(scope, depth)));
        if (scope.HasVarOf(ExprType.IntFn))
            weights.Add((2, () => GenIntFnApply(scope, depth)));
        if (_ctx.UserUnions.Count > 0)
            weights.Add((2, () => GenUserUnionMatch(scope, depth)));
        if (_ctx.UserRecords.Count > 0)
            weights.Add((2, () => GenUserRecordAccess(scope, depth)));
        if (_stdlibGens is not null)
        {
            var sg = _stdlibGens;

            // Option reducers.
            if (sg.Option.IsImported())
            {
                // Pick one of: unwrap-or, match Some/None, unwrap, map+unwrap-or,
                // flat-map+unwrap-or. Equal weight; the table-level (2) keeps
                // overall option-reducer frequency similar to the old single-entry.
                weights.Add((1, () => sg.Option.UnwrapOrToInt(scope, depth)));
                weights.Add((1, () => sg.Option.MatchSomeNoneToInt(scope, depth)));
                weights.Add((1, () => sg.Option.UnwrapToInt(scope, depth)));
                weights.Add((1, () => sg.Option.MapThenUnwrapOrToInt(scope, depth)));
                weights.Add((1, () => sg.Option.FlatMapThenUnwrapOrToInt(scope, depth)));
            }

            // TreeList reducers.
            if (sg.TreeList.IsImported())
            {
                weights.Add((1, () => sg.TreeList.CountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.FoldToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.NthToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.HeadToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.TailCountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.ConsCountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.AppendCountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.ConcatCountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.MapCountToInt(scope, depth)));
                weights.Add((1, () => sg.TreeList.FilterCountToInt(scope, depth)));
                if (sg.TreeList.CanConvertVector())
                    weights.Add((1, () => sg.TreeList.VectorConversionToInt(scope, depth)));
            }

            // Result reducers.
            if (sg.Result.IsImported())
            {
                weights.Add((1, () => sg.Result.MatchOkErrToInt(scope, depth)));
                weights.Add((1, () => sg.Result.UnwrapToInt(scope, depth)));
                weights.Add((1, () => sg.Result.MapThenMatchToInt(scope, depth)));
                weights.Add((1, () => sg.Result.FlatMapThenMatchToInt(scope, depth)));
            }

            // Nested Option/Result patterns.
            if (sg.Option.CanNestOptionResult())
            {
                weights.Add((1, () => sg.Option.NestedOptionResultToInt(scope, depth)));
                weights.Add((1, () => sg.Option.NestedResultOptionToInt(scope, depth)));
                weights.Add((1, () => sg.Option.TripleNestedOptionResultToInt(scope, depth)));
            }

            if (sg.Option.IsImported())
                weights.Add((1, () => sg.Option.NestedOptionOptionToInt(scope, depth)));

            // Array reducers.
            if (sg.Vector.IsImported())
            {
                weights.Add((1, () => sg.Vector.CountToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.FoldToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.MapFoldToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.NthToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.AppendCountToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.SetNthToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.MapCountToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.FilterCountToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.MakeOrBuildRefToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.SortRefToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.TakeDropCountToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.CountOrFilterNotToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.ArgMinMaxToInt(scope, depth)));
                weights.Add((1, () => sg.Vector.AppendManyCountToInt(scope, depth)));
                if (sg.Option.IsImported())
                    weights.Add((1, () => sg.Vector.MemberUnwrapOrToInt(scope, depth)));
                if (sg.List.IsImported())
                    weights.Add((1, () => sg.Vector.ToListLengthToInt(scope, depth)));
            }

            // Hash reducers (Int-typed shapes).
            if (sg.Hash.IsImported())
            {
                weights.Add((1, () => sg.Hash.CountToInt(scope, depth)));
                weights.Add((1, () => sg.Hash.PutCountToInt(scope, depth)));
                weights.Add((1, () => sg.Hash.RemoveCountToInt(scope, depth)));
                if (sg.Option.IsImported())
                    weights.Add((1, () => sg.Hash.GetUnwrapOrToInt(scope, depth)));
                if (sg.Hash.CanReduceKeysOrValues())
                {
                    weights.Add((1, () => sg.Hash.KeysCountToInt(scope, depth)));
                    weights.Add((1, () => sg.Hash.ValuesCountToInt(scope, depth)));
                }
            }

            // String stdlib reducer (Int-typed shape).
            if (sg.String.IsImported() && _string is not null)
                weights.Add((1, () => sg.String.FormatEmptyToInt(scope, depth)));

            // Core combinators.
            if (sg.Core.IsImported())
            {
                weights.Add((1, () => sg.Core.IdToInt(scope, depth)));
                weights.Add((1, () => sg.Core.ComposeToInt(scope, depth)));
                // is-null? probe — gated low (suspected boxing divergence).
                if (_ctx.EnableNullChecks)
                    weights.Add((1, () => sg.Core.IsNullCheckToInt(scope, depth)));
            }

            // Cond — multi-arm conditional macro.
            if (sg.Cond.IsImported())
                weights.Add((1, () => sg.Cond.CondToInt(scope, depth)));

            // Pipe — left-to-right function composition macro.
            if (sg.Pipe.IsImported())
                weights.Add((1, () => sg.Pipe.PipeChainToInt(scope, depth)));

            // List — recursive linked-list ADT with nested pattern matches.
            if (sg.List.IsImported())
            {
                weights.Add((1, () => sg.List.LengthToInt(scope, depth)));
                weights.Add((1, () => sg.List.FoldToInt(scope, depth)));
                weights.Add((1, () => sg.List.MatchToInt(scope, depth)));
                weights.Add((1, () => sg.List.AccessorToInt(scope, depth)));
                weights.Add((1, () => sg.List.RearrangeLengthToInt(scope, depth)));
                weights.Add((1, () => sg.List.NthToInt(scope, depth)));
                weights.Add((1, () => sg.List.MapFilterToInt(scope, depth)));
                weights.Add((1, () => sg.List.VariadicCtorLengthToInt(scope, depth)));
                if (sg.List.CanConvertVector())
                    weights.Add((1, () => sg.List.VectorConversionToInt(scope, depth)));
                if (sg.List.CanConvertTreeList())
                    weights.Add((1, () => sg.List.TreeListConversionToInt(scope, depth)));
            }

            // Concurrent collections — count + try-read each. Each shape is a
            // `let` + `begin` over a CLR-backed mutable handle, so wrap with
            // `depth >= 1` (already true here) and rely on inner GenInt to
            // bottom out at the leaf path.
            if (sg.Concurrent.QueueImported())
            {
                weights.Add((1, () => sg.Concurrent.QueueCountToInt(scope, depth)));
                weights.Add((1, () => sg.Concurrent.QueueTryDequeueToInt(scope, depth)));
            }

            if (sg.Concurrent.StackImported())
            {
                weights.Add((1, () => sg.Concurrent.StackCountToInt(scope, depth)));
                weights.Add((1, () => sg.Concurrent.StackTryPopToInt(scope, depth)));
            }

            if (sg.Concurrent.BagImported())
            {
                weights.Add((1, () => sg.Concurrent.BagCountToInt(scope, depth)));
                weights.Add((1, () => sg.Concurrent.BagTryTakeToInt(scope, depth)));
            }

            if (sg.Concurrent.DictionaryImported())
            {
                weights.Add((1, () => sg.Concurrent.DictionaryCountToInt(scope, depth)));
                weights.Add((1, () => sg.Concurrent.DictionaryTryRemoveToInt(scope, depth)));
            }

            // Mutable collections.
            if (sg.Mutable.VectorImported())
            {
                weights.Add((1, () => sg.Mutable.VectorCountToInt(scope, depth)));
                weights.Add((1, () => sg.Mutable.VectorSetNthToInt(scope, depth)));
            }

            if (sg.Mutable.TreeListImported())
            {
                weights.Add((1, () => sg.Mutable.TreeListAddCountToInt(scope, depth)));
                weights.Add((1, () => sg.Mutable.TreeListNthToInt(scope, depth)));
            }

            if (sg.Mutable.HashImported())
                weights.Add((1, () => sg.Mutable.HashPutCountToInt(scope, depth)));

            // Error (stdlib/error). Cause-depth reducer matches on the
            // optional `inner` field to produce 0 or 1.
            if (sg.Error.IsImported())
                weights.Add((1, () => sg.Error.CauseDepthToInt(scope, depth)));

            // Control (when/unless) and catch macros.
            if (sg.Control.ControlImported())
            {
                weights.Add((1, () => sg.Control.WhenUnitToInt(scope, depth)));
                if (sg.Control.CanMutateEffect())
                    weights.Add((2, () => sg.Control.WhenMutateToInt(scope, depth)));
            }

            if (sg.Control.CatchImported())
                weights.Add((2, () => sg.Control.CatchToInt(scope, depth)));
        }

        // Built-in conversions (no import required).
        if (_conv is not null)
        {
            weights.Add((1, () => _conv.IntStringRoundTripToInt(scope, depth)));
            weights.Add((1, () => _conv.IntFloatRoundTripToInt(scope, depth)));
            weights.Add((1, () => _conv.DoubleEqToInt(scope, depth)));
        }

        if (_ctx.AuxExports.Count > 0)
            weights.Add((2, () => GenAuxCall(scope, depth)));
        if (_ctx.MacroIntCallables.Count > 0)
            weights.Add((1, () => GenMacroIntCall(scope, depth)));
        // Core-special-form reducers (weight 1 each — similar frequency to GenLambdaIife).
        if (_sequence is not null)
            weights.Add((1, () => _sequence.BeginToInt(scope, depth)));
        if (_tuple is not null)
        {
            weights.Add((1, () => _tuple.MatchTupleToInt(scope, depth)));
            weights.Add((1, () => _tuple.MatchMixedTupleToInt(scope, depth)));
        }

        if (_with is not null && _ctx.UserRecords.Count > 0)
            weights.Add((1, () => _with.WithUpdateToInt(scope, depth)));
        if (_partial is not null && PartialExprGenerator.HasEligible(_ctx))
            weights.Add((1, () => _partial.PartialApplyToInt(scope, depth)));
        if (_exception is not null)
        {
            weights.Add((1, () => _exception.WithHandlersToInt(scope, depth)));
            weights.Add((1, () => _exception.GenNestedHandlers(scope, depth)));
            weights.Add((1, () => _exception.GenRethrowingHandler(scope, depth)));
            // Fat-EH-section path is rare in real code and inflates program size,
            // so its weight is a fraction of the others — keep it firing in long
            // runs without dominating short ones.
            if (depth >= 2)
                weights.Add((1, () => _exception.GenManyHandlers(scope, depth)));
        }

        if (_string is not null)
            weights.Add((1, () => _string.StringEqualityToInt(scope, depth)));
        // Symbol reducers — no import needed (quote + conversions are builtins).
        if (_symbol is not null)
        {
            weights.Add((1, () => _symbol.SymbolEqToInt(scope, depth)));
            weights.Add((1, () => _symbol.SymbolToStringEqToInt(scope, depth)));
            weights.Add((1, () => _symbol.SymbolMatchToInt(scope, depth)));
            weights.Add((1, () => _symbol.SymbolLetToInt(scope, depth)));
        }
        if (_class is not null && _ctx.UserClasses.Count > 0)
        {
            weights.Add((1, () => _class.ConstructDiscardToInt(scope, depth)));
            if (_ctx.EnableClassInstanceCalls)
                weights.Add((1, () => _class.ConstructAndCallToInt(scope, depth)));
        }

        if (_object is not null && _object.HasEligible())
            weights.Add((1, () => _object.ObjectDiscardToInt(scope, depth)));
        if (_clr is not null)
        {
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathAbsInt))
                weights.Add((2, () => _clr.ReduceMathAbsIntToInt(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathMinInt))
                weights.Add((2, () => _clr.ReduceMathMinIntToInt(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathMaxInt))
                weights.Add((2, () => _clr.ReduceMathMaxIntToInt(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.StringLength) && _string is not null)
                weights.Add((1, () => _clr.ReduceStringLengthToInt(scope, depth)));
            // String indexer + Char->Int round-trip — both bindings are emitted
            // together so checking one is sufficient.
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.StringIndexer) && _string is not null)
                weights.Add((1, () => _clr.ReduceStringIndexerToInt(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.Int32TryParse) && _string is not null)
                weights.Add((1, () => _clr.ReduceTryParseToInt(scope, depth)));
        }

        if (_widePrim is not null)
        {
            if (_widePrim.LongAvailable)
            {
                weights.Add((1, () => _widePrim.ReduceLongRoundTripToInt(scope, depth)));
                // 64-bit equality only needs the Int<->Long conversion pair.
                weights.Add((1, () => _widePrim.ReduceLongEqToInt(scope, depth)));
            }

            if (_widePrim.ByteAvailable)
                weights.Add((1, () => _widePrim.ReduceByteRoundTripToInt(scope, depth)));
            // Genuine Long arithmetic — needs the Int64 Math overloads.
            if (_widePrim.LongArithAvailable)
            {
                weights.Add((1, () => _widePrim.ReduceLongMaxToInt(scope, depth)));
                weights.Add((1, () => _widePrim.ReduceLongMinToInt(scope, depth)));
                weights.Add((1, () => _widePrim.ReduceLongAbsToInt(scope, depth)));
                weights.Add((1, () => _widePrim.ReduceBigMulToInt(scope, depth)));
            }
        }

        if (_typeOf is not null && !_ctx.InAuxModule)
            weights.Add((1, () => _typeOf.GenTypeOfDiscard()));

        // Delegate-form reducers. Gated on !InAuxModule because the helpers are
        // only defined in the main module (aux modules are generated before the
        // flag is set), so the reducers must not fire while building aux bodies.
        if (_delegate is not null && _ctx.EnableDelegateForms && !_ctx.InAuxModule)
        {
            weights.Add((1, () => _delegate.ReduceFuncDelegateLambdaToInt(scope, depth)));
            weights.Add((1, () => _delegate.ReduceFuncDelegateNamedToInt(scope, depth)));
            weights.Add((1, () => _delegate.ReduceActionToInt(scope, depth)));
        }

        return _ctx.PickWeighted(weights)();
    }

    // Calls a function exported by a generated aux module using its fully qualified
    // name (e.g. `aux_12345678_0/h0`). All params are Int, body returns Int — trivially
    // preserves the Compute : Int invariant.
    private string GenAuxCall(Scope scope, int depth)
    {
        var export = _ctx.AuxExports[_ctx.Rng.Next(_ctx.AuxExports.Count)];
        var args = new List<string>();
        foreach (var p in export.ParamTypes)
            args.Add(
                p switch
                {
                    ExprType.Int => GenInt(scope, depth - 1),
                    _ => throw new InvalidOperationException($"Unsupported aux param type: {p}"),
                }
            );
        return $"({export.QualifiedName} {string.Join(" ", args)})";
    }

    // Emits a use site for one of the registered expression macros. The arity
    // sentinel encodes the shape: -1 = when (cond body), -2 = let1 (x v body),
    // -3 = ellipsis-sum (1-4 Int args), -4 = literal-dispatch (plus|minus a b),
    // -5 = hygiene (body under a macro-introduced x0 binding), positive N = N
    // straight Int args. Each shape produces an Int-valued expression so it
    // slots into GenInt's contract.
    private string GenMacroIntCall(Scope scope, int depth)
    {
        var (name, arity) = _ctx.MacroIntCallables[_ctx.Rng.Next(_ctx.MacroIntCallables.Count)];
        switch (arity)
        {
            case -1:
            {
                var cond = GenBool(scope, depth - 1);
                var body = GenInt(scope, depth - 1);
                return $"({name} {cond} {body})";
            }
            case -2:
            {
                var bindName = _ctx.Fresh();
                var v = GenInt(scope, depth - 1);
                var bodyScope = scope.Extend(bindName, ExprType.Int);
                var body = GenInt(bodyScope, depth - 1);
                return $"({name} {bindName} {v} {body})";
            }
            case -3:
            {
                var n = 1 + _ctx.Rng.Next(4);
                var args = new List<string>(n);
                for (var i = 0; i < n; i++)
                    args.Add(GenInt(scope, depth - 1));
                return $"({name} {string.Join(" ", args)})";
            }
            case -4:
            {
                var lit = _ctx.Rng.NextDouble() < 0.5 ? "plus" : "minus";
                var a = GenInt(scope, depth - 1);
                var b = GenInt(scope, depth - 1);
                return $"({name} {lit} {a} {b})";
            }
            case -5:
            {
                // The expander is NON-hygienic (verified): the template's
                // `(let* ([x0 42]) ...)` captures any `x0` the body mentions.
                // Generate the body with x0 retyped to Int so the generator's
                // view matches the post-expansion binding — otherwise an outer
                // Bool/Float x0 would make the body ill-typed on both backends.
                var bodyScope = scope.Extend("x0", ExprType.Int);
                var body = GenInt(bodyScope, depth - 1);
                return $"({name} {body})";
            }
            default:
            {
                var args = new List<string>(arity);
                for (var i = 0; i < arity; i++)
                    args.Add(GenInt(scope, depth - 1));
                return $"({name} {string.Join(" ", args)})";
            }
        }
    }

    private string GenUserUnionMatch(Scope scope, int depth)
    {
        return _match is null
            ? throw new InvalidOperationException("MatchExprGenerator not wired")
            : _match.GenUserUnionMatch(scope, depth);
    }

    // Builds a user-declared generic record (fields given Int values since each
    // type param is instantiated at Int here) and reads one field back out via the
    // generated `RecordName/fieldName` accessor.
    private string GenUserRecordAccess(Scope scope, int depth)
    {
        var r = _ctx.UserRecords[_ctx.Rng.Next(_ctx.UserRecords.Count)];

        var fieldArgs = new List<string>();
        foreach (var _ in r.Fields)
            fieldArgs.Add(GenInt(scope, depth - 1));

        var fieldIdx = _ctx.Rng.Next(r.Fields.Count);
        var fieldName = r.Fields[fieldIdx].Name;

        return $"({r.Name}/{fieldName} ({r.Name} {string.Join(" ", fieldArgs)}))";
    }

    private string GenIntLeaf(Scope scope)
    {
        var intVars = scope.GetVars(ExprType.Int);
        if (intVars.Count > 0 && _ctx.Rng.NextDouble() < 0.5)
            return intVars[_ctx.Rng.Next(intVars.Count)];

        var pick = _ctx.Rng.NextDouble();
        if (pick < 0.1)
            return int.MinValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.2)
            return int.MaxValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.5)
            return (_ctx.Rng.Next(0, 200001) - 100000).ToString(CultureInfo.InvariantCulture);
        return _ctx.Rng.Next(0, 101).ToString(CultureInfo.InvariantCulture);
    }

    private string GenIntBinOp(Scope scope, int depth)
    {
        var ops = new[] { "+", "-", "*" };
        var op = ops[_ctx.Rng.Next(ops.Length)];
        // Rare unary negation — single-arg `-` passes through AstBuilder's
        // variadic normalization as negate rather than a fold.
        if (op == "-" && _ctx.Rng.NextDouble() < 0.10)
            return $"(- {GenInt(scope, depth - 1)})";
        // ~35% n-ary (3-5 operands) to exercise AstBuilder's arith fold
        // (left-associated re-nesting); otherwise plain binary.
        var count = _ctx.Rng.NextDouble() < 0.35 ? 3 + _ctx.Rng.Next(3) : 2;
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
            args.Add(GenInt(scope, depth - 1));
        return $"({op} {string.Join(" ", args)})";
    }

    // Integer `/` and `%` over a variety of divisor shapes. The divisor is always
    // either a non-zero literal or a scope-variable-derived expression — NEVER an
    // arbitrary GenInt sub-expression (which could constant-fold to 0 and trip
    // Roslyn CS0020) and never a literal 0. Runtime div-by-zero / INT_MIN overflow
    // are deliberately produced but stay oracle-comparable: both backends throw the
    // same exception type+message (DiffExec treats matching throws as PASS).
    //
    //   * INT_MIN / -1 (pure literals) — arithmetic overflow. Safe because the C#
    //     emitter rewrites constant `/`/`%` as `(Math.Max(l,l) op r)` so Roslyn
    //     can't fold it (CSharpEmitter.Emit.cs), and the divisor -1 is non-zero so
    //     CS0020 doesn't apply; both backends throw OverflowException at runtime.
    //   * Runtime div-by-zero `(- y y)` / possibly-zero bare var — only when an
    //     Int var is in scope, mirroring ExceptionExprGenerator's `canNaturalThrow`.
    private string GenIntDivModOp(Scope scope, int depth)
    {
        var op = _ctx.Rng.NextDouble() < 0.5 ? "/" : "%";
        var a = GenInt(scope, depth - 1);
        var intVars = scope.GetVars(ExprType.Int);

        // Divisor shape weights. Runtime-var shapes are only eligible when an Int
        // var is in scope; otherwise fall back to the literal-only shapes (all of
        // which are constant-fold-safe).
        var shapes = new List<(int Weight, string Kind)>
        {
            (10, "pos-literal"), // dominant: positive divisor 1..99
            (3, "neg-literal"), // negative divisor: modulo-sign / round-toward-zero
            (2, "intmin-overflow"), // INT_MIN op -1: OverflowException on both backends
        };
        // `%` is strict-binary in AstBuilder's variadic normalization; only `/`
        // participates in the n-ary arith fold and the unary-reciprocal form.
        if (op == "/")
        {
            shapes.Add((2, "nary-literal")); // (/ a d1 d2): left-fold of division
            shapes.Add((1, "unary-recip")); // (/ y): 1/y, non-zero operand shapes only
        }

        if (intVars.Count > 0)
        {
            shapes.Add((2, "runtime-zero")); // (- y y): DivideByZeroException on both
            shapes.Add((2, "runtime-var")); // bare var: may or may not be zero
        }

        switch (_ctx.PickWeighted(shapes))
        {
            case "neg-literal":
                return $"({op} {a} -{1 + _ctx.Rng.Next(99)})";
            case "nary-literal":
            {
                // Divisors follow the same literal discipline as the binary
                // shapes: non-zero literals only, so nothing constant-folds to a
                // zero divisor.
                var d1 = 1 + _ctx.Rng.Next(99);
                var d2 =
                    _ctx.Rng.NextDouble() < 0.25
                        ? $"-{1 + _ctx.Rng.Next(99)}"
                        : (1 + _ctx.Rng.Next(99)).ToString(CultureInfo.InvariantCulture);
                return $"({op} {a} {d1} {d2})";
            }
            case "unary-recip":
            {
                // Operand is a non-zero literal or an in-scope var (which may be
                // zero at runtime — DivideByZeroException is oracle-comparable).
                var y =
                    intVars.Count > 0 && _ctx.Rng.NextDouble() < 0.5
                        ? intVars[_ctx.Rng.Next(intVars.Count)]
                        : (1 + _ctx.Rng.Next(99)).ToString(CultureInfo.InvariantCulture);
                return $"({op} {y})";
            }
            case "intmin-overflow":
                return $"({op} {int.MinValue.ToString(CultureInfo.InvariantCulture)} -1)";
            case "runtime-zero":
            {
                var y = intVars[_ctx.Rng.Next(intVars.Count)];
                return $"({op} {a} (- {y} {y}))";
            }
            case "runtime-var":
            {
                var y = intVars[_ctx.Rng.Next(intVars.Count)];
                return $"({op} {a} {y})";
            }
            default: // pos-literal
                return $"({op} {a} {1 + _ctx.Rng.Next(99)})";
        }
    }

    private string GenLambdaIife(Scope scope, int depth)
    {
        var pname = _ctx.FreshOrShadow(scope, ExprType.Int);
        var arg = GenInt(scope, depth - 1);
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var body = GenInt(bodyScope, depth - 1);
        return $"((lambda ([{pname} : Int]) {body}) {arg})";
    }

    private string GenLambdaValue(Scope scope, int depth)
    {
        var pname = _ctx.FreshOrShadow(scope, ExprType.Int);
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var bodyDepth = Math.Max(1, depth - 1);
        var body = GenInt(bodyScope, bodyDepth);
        return $"(lambda ([{pname} : Int]) {body})";
    }

    public string GenBool(Scope scope, int depth)
    {
        if (depth <= 0)
            return GenBoolLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (3, () => GenBoolLeaf(scope)),
            (4, () => GenComparison(scope, depth)),
            (2, () => GenBoolBinOp(scope, depth)),
            (1, () => $"(not {GenBool(scope, depth - 1)})"),
            (1, () => GenIf(ExprType.Bool, scope, depth)),
            (1, () => GenMatch(ExprType.Bool, scope, depth)),
            (2, () => GenFloatComparison(scope, depth)),
        };
        if (_letStar is not null)
            weights.Add((1, () => _letStar.LetStarToBool(scope, depth)));
        if (_stdlibGens is not null)
        {
            var sg = _stdlibGens;

            if (sg.Hash.IsImported())
            {
                weights.Add((1, () => sg.Hash.ContainsPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.Hash.EmptyPredicateToBool(scope, depth)));
            }

            if (sg.Option.IsImported())
            {
                weights.Add((1, () => sg.Option.SomePredicateToBool(scope, depth)));
                weights.Add((1, () => sg.Option.NonePredicateToBool(scope, depth)));
            }

            if (sg.Result.IsImported())
            {
                weights.Add((1, () => sg.Result.OkPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.Result.ErrPredicateToBool(scope, depth)));
            }

            if (sg.TreeList.IsImported())
                weights.Add((1, () => sg.TreeList.EmptyPredicateToBool(scope, depth)));

            if (sg.Vector.IsImported())
                weights.Add((1, () => sg.Vector.EmptyPredicateToBool(scope, depth)));

            if (sg.String.IsImported() && _string is not null)
            {
                weights.Add((1, () => sg.String.EqualsPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.EmptyPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.StartsWithPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.EndsWithPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.ContainsPredicateToBool(scope, depth)));
            }
        }

        if (
            _clr is not null
            && _ctx.EmittedClrBindings.Contains(ClrBinding.StringIsNullOrEmpty)
            && _string is not null
        )
            weights.Add((1, () => _clr.ReduceStringIsEmptyToBool(scope, depth)));
        if (
            _clr is not null
            && _ctx.EmittedClrBindings.Contains(ClrBinding.Int32TryParse)
            && _string is not null
        )
            weights.Add((1, () => _clr.ReduceTryParseSuccessToBool(scope, depth)));
        return _ctx.PickWeighted(weights)();
    }

    private string GenBoolLeaf(Scope scope)
    {
        var boolVars = scope.GetVars(ExprType.Bool);
        if (boolVars.Count > 0 && _ctx.Rng.NextDouble() < 0.5)
            return boolVars[_ctx.Rng.Next(boolVars.Count)];
        return _ctx.Rng.NextDouble() < 0.5 ? "#t" : "#f";
    }

    private string GenComparison(Scope scope, int depth)
    {
        var ops = new[] { "=", "!=", "<", ">", "<=", ">=" };
        var op = ops[_ctx.Rng.Next(ops.Length)];
        // ~35% chains of 3-4 operands. Ordered/equality chains desugar to
        // AND-chains; `!=` expands to all-pairwise distinctness. IMPURE middle
        // operands get bound to fresh $cmp_N/$neq_N vars — a shape the C#
        // backend currently emits as invalid C# (see
        // issues/csharp-cmp-chain-dollar-names-invalid.md), so middles stay
        // pure leaves except for a 5% known-bug probe.
        var count = _ctx.Rng.NextDouble() < 0.35 ? 3 + _ctx.Rng.Next(2) : 2;
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var isEnd = i == 0 || i == count - 1;
            // `!=` duplicates every operand in its pairwise expansion, so all
            // its operands are binding-eligible, not just the middles.
            var bindEligible = op == "!=" ? count > 2 : !isEnd;
            args.Add(
                !bindEligible || _ctx.Rng.NextDouble() < 0.05
                    ? GenInt(scope, depth - 1)
                    : GenIntLeaf(scope)
            );
        }

        return $"({op} {string.Join(" ", args)})";
    }

    private string GenBoolBinOp(Scope scope, int depth)
    {
        var op = _ctx.Rng.NextDouble() < 0.5 ? "and" : "or";
        // ~35% n-ary (3-5 operands): AstBuilder right-folds these into nested
        // short-circuit chains.
        var count = _ctx.Rng.NextDouble() < 0.35 ? 3 + _ctx.Rng.Next(3) : 2;
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
            args.Add(GenBool(scope, depth - 1));
        return $"({op} {string.Join(" ", args)})";
    }

    public string GenFloat(Scope scope, int depth)
    {
        if (depth <= 0)
            return GenFloatLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (3, () => GenFloatLeaf(scope)),
            (3, () => GenFloatBinOp(scope, depth)),
        };
        if (_clr is not null)
        {
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathSqrt))
                weights.Add((1, () => _clr.ReduceMathSqrtToFloat(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathAbsFloat))
                weights.Add((1, () => _clr.ReduceMathAbsFloatToFloat(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathMinDouble))
                weights.Add((1, () => _clr.ReduceMathMinMaxDoubleToFloat(scope, depth)));
            if (_ctx.EmittedClrBindings.Contains(ClrBinding.MathFloorDouble))
                weights.Add((1, () => _clr.ReduceMathFloorDoubleToFloat(scope, depth)));
        }

        if (_stdlibGens is not null && _stdlibGens.Math.IsImported())
        {
            var m = _stdlibGens.Math;
            weights.Add((1, () => m.SqrtToFloat(scope, depth)));
            weights.Add((1, () => m.FloorToFloat(scope, depth)));
            weights.Add((1, () => m.CeilingToFloat(scope, depth)));
            weights.Add((1, () => m.MaxfToFloat(scope, depth)));
            weights.Add((1, () => m.MinfToFloat(scope, depth)));
        }

        if (_conv is not null)
        {
            weights.Add((1, () => _conv.IntToFloatDirect(scope, depth)));
            weights.Add((1, () => _conv.FloatDoubleRoundTripToFloat(scope, depth)));
        }

        return _ctx.PickWeighted(weights)();
    }

    private string GenFloatLeaf(Scope scope)
    {
        var fltVars = scope.GetVars(ExprType.Float);
        if (fltVars.Count > 0 && _ctx.Rng.NextDouble() < 0.5)
            return fltVars[_ctx.Rng.Next(fltVars.Count)];

        var pick = _ctx.Rng.NextDouble();
        if (pick < 0.08)
            return "0.0";
        if (pick < 0.16)
            return "-0.0";
        if (pick < 0.24)
            return "1.0";
        if (pick < 0.32)
            return "-1.0";
        var value = _ctx.Rng.NextDouble() * 2000.0 - 1000.0;
        var s = value.ToString("G7", CultureInfo.InvariantCulture);
        // Ensure literal is parsed as float: force a decimal point if absent.
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
            s += ".0";
        return s;
    }

    private string GenFloatBinOp(Scope scope, int depth)
    {
        var ops = new[] { "+", "-", "*", "/" };
        var op = ops[_ctx.Rng.Next(ops.Length)];
        // ~35% n-ary fold; float `/ 0.0` yields Inf/NaN (no throw), so unlike
        // the Int path no divisor discipline is needed.
        var count = _ctx.Rng.NextDouble() < 0.35 ? 3 + _ctx.Rng.Next(3) : 2;
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
            args.Add(GenFloat(scope, depth - 1));
        return $"({op} {string.Join(" ", args)})";
    }

    private string GenFloatComparison(Scope scope, int depth)
    {
        var ops = new[] { "<", ">", "<=", ">=", "=", "!=" };
        var op = ops[_ctx.Rng.Next(ops.Length)];
        // ~35% chains — see GenComparison (incl. the $cmp_N known-bug gating);
        // NaN operands make chain semantics an extra divergence probe at Float.
        var count = _ctx.Rng.NextDouble() < 0.35 ? 3 + _ctx.Rng.Next(2) : 2;
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var isEnd = i == 0 || i == count - 1;
            var bindEligible = op == "!=" ? count > 2 : !isEnd;
            args.Add(
                !bindEligible || _ctx.Rng.NextDouble() < 0.05
                    ? GenFloat(scope, depth - 1)
                    : GenFloatLeaf(scope)
            );
        }

        return $"({op} {string.Join(" ", args)})";
    }

    private string GenIf(ExprType resultType, Scope scope, int depth)
    {
        var cond = GenBool(scope, depth - 1);
        var t = GenExpr(resultType, scope, depth - 1);
        var e = GenExpr(resultType, scope, depth - 1);
        return $"(if {cond} {t} {e})";
    }

    // ZScheme's `let` takes exactly ONE binding (multiple bindings use `let*`),
    // so this stays single-binding; multi-binding coverage lives in
    // LetStarExprGenerator.
    private string GenLet(ExprType resultType, Scope scope, int depth)
    {
        var pick = _ctx.Rng.NextDouble();
        ExprType bindingType;
        if (pick < 0.50)
            bindingType = ExprType.Int;
        else if (pick < 0.70)
            bindingType = ExprType.Bool;
        else if (pick < 0.85)
            bindingType = ExprType.Float;
        else if (_string is not null)
            bindingType = ExprType.String;
        else
            bindingType = ExprType.Float;

        var name = _ctx.FreshOrShadow(scope, bindingType);
        var value = GenBindableExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        // ~15% annotated form `[x : Type v]` — exercises AstBuilder's
        // annotated-Let path (the annotation is the binding's exact type).
        var ann = _ctx.Rng.NextDouble() < 0.15 ? $" : {GroundTypeName(bindingType)}" : "";
        return $"(let ([{name}{ann} {value}]) {body})";
    }

    private string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        return _match is null
            ? throw new InvalidOperationException("MatchExprGenerator not wired")
            : _match.GenMatch(resultType, scope, depth);
    }

    // Wraps a deliberately non-exhaustive match so the program still computes a
    // value: both backends throw InvalidOperationException("Non-exhaustive
    // match") on fall-through, so caught-vs-uncaught and the caught value are
    // both oracle-comparable.
    public string WrapMatchFallthrough(string matchExpr, ExprType resultType, Scope scope, int depth)
    {
        var e = _ctx.Fresh();
        var fallback = GenExpr(resultType, scope, Math.Max(0, depth - 1));
        return $"(with-handlers ([System.Exception {e}] {fallback}) {matchExpr})";
    }

    private string GenCall(Scope scope, int depth)
    {
        // Only sync user funcs are callable from a sync Int site; async funcs
        // return Task<Int> and are reached via AsyncExprGenerator's await.
        var syncFuncs = _ctx.SyncUserFuncs.ToList();
        var func = syncFuncs[_ctx.Rng.Next(syncFuncs.Count)];

        // For generic funcs, pick a ground type to instantiate ^a at (bias toward
        // Int so the existing Int-monomorphic call path stays well-exercised).
        // For non-generic funcs, the ground stays Int and IsGenericParam is all
        // false, so nothing changes.
        var ground = PickCallGround(func);

        var args = new List<string>();
        // For variadic funcs the last entry in ParamTypes is the variadic
        // element type — handled separately after the fixed prefix loop below.
        var fixedCount = func.IsVariadic ? func.ParamTypes.Count - 1 : func.ParamTypes.Count;
        for (var i = 0; i < fixedCount; i++)
        {
            var paramType = func.ParamTypes[i];
            if (func.Kind == UserFuncKind.Recursive && i == 0)
            {
                // First recursive-function argument must be a bounded small Int literal
                // so the recursion terminates regardless of TCO correctness.
                args.Add(_ctx.Rng.Next(0, 21).ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var isGeneric = i < func.IsGenericParam.Count && func.IsGenericParam[i];
            args.Add(
                paramType switch
                {
                    ExprType.Int when isGeneric => GenGroundLeaf(ground, scope, depth - 1),
                    ExprType.Int => GenInt(scope, depth - 1),
                    ExprType.IntFn when isGeneric => GenGroundFnArg(ground, scope, depth - 1),
                    ExprType.IntFn => GenIntFnArg(scope, depth - 1),
                    _ => throw new InvalidOperationException(
                        $"Unsupported param type: {paramType}"
                    ),
                }
            );
        }

        if (func.IsVariadic)
        {
            // 0-3 trailing Int args for the variadic position. Element type is
            // always Int today; if widened later, dispatch on ParamTypes[^1].
            var variadicCount = _ctx.Rng.Next(4);
            for (var i = 0; i < variadicCount; i++)
                args.Add(GenInt(scope, depth - 1));
        }

        var call = $"({func.Name} {string.Join(" ", args)})";
        // If the return type is the generic `^a`, reduce it back to Int at the
        // call site so GenInt's contract holds.
        return func.ReturnIsGeneric ? ReduceToInt(call, ground) : call;
    }

    private ExprType PickCallGround(UserFunc func)
    {
        if (func.AllowedGrounds.Count <= 1)
            return ExprType.Int;
        // Weighted roll: 60% Int, 20% Bool, 20% Float (only among allowed).
        var grounds = func.AllowedGrounds.ToArray();
        var weights = new List<(int, ExprType)>();
        foreach (var g in grounds)
        {
            var w = g switch
            {
                ExprType.Int => 3,
                ExprType.Bool => 1,
                ExprType.Float => 1,
                _ => 1,
            };
            weights.Add((w, g));
        }

        return _ctx.PickWeighted(weights);
    }

    private string GenGroundLeaf(ExprType ground, Scope scope, int depth)
    {
        return ground switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            ExprType.Float => GenFloat(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}"),
        };
    }

    // Emits `(lambda ([p : GroundType]) <int-body>)` for passing as (^a -> Int) arg.
    private string GenGroundFnArg(ExprType ground, Scope scope, int depth)
    {
        if (ground == ExprType.Int)
            return GenIntFnArg(scope, depth);

        var pname = _ctx.Fresh();
        var bodyScope = scope.Extend(pname, ground);
        var bodyDepth = Math.Max(1, depth - 1);
        var body = GenInt(bodyScope, bodyDepth);
        var typeName = GroundTypeName(ground);
        return $"(lambda ([{pname} : {typeName}]) {body})";
    }

    private static string GroundTypeName(ExprType ground)
    {
        return ground switch
        {
            ExprType.Int => "Int",
            ExprType.Bool => "Bool",
            ExprType.Float => "Float",
            ExprType.String => "String",
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}"),
        };
    }

    // Wraps a ground-typed expression so it reduces to Int.
    private static string ReduceToInt(string expr, ExprType ground)
    {
        return ground switch
        {
            ExprType.Int => expr,
            ExprType.Bool => $"(if {expr} 1 0)",
            // `float->int` is defined in the default TypeEnv (`Types/TypeEnv.cs`)
            // and lowers to `System.Convert.ToInt32(double)` in IrLowering.
            ExprType.Float => $"(float->int {expr})",
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}"),
        };
    }

    public string GenIntFnArg(Scope scope, int depth)
    {
        var inScope = scope.GetVars(ExprType.IntFn);
        if (inScope.Count > 0 && _ctx.Rng.NextDouble() < 0.4)
            return inScope[_ctx.Rng.Next(inScope.Count)];
        return GenLambdaValue(scope, Math.Max(1, depth));
    }

    private string GenIntFnApply(Scope scope, int depth)
    {
        var fns = scope.GetVars(ExprType.IntFn);
        var f = fns[_ctx.Rng.Next(fns.Count)];
        var arg = GenInt(scope, depth - 1);
        return $"({f} {arg})";
    }

    public string GenExpr(ExprType type, Scope scope, int depth)
    {
        return type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported type: {type}"),
        };
    }

    // Ground-typed generation for collaborators that carry per-slot ExprTypes
    // (typed class fields / interface method signatures).
    public string GenTyped(ExprType type, Scope scope, int depth)
    {
        return type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            ExprType.Float => GenFloat(scope, depth),
            ExprType.String => GenString(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported typed ground: {type}"),
        };
    }

    // ZScheme source name of a ground ExprType (Int/Bool/Float/String).
    public static string TypeNameOf(ExprType ground)
    {
        return GroundTypeName(ground);
    }

    // Reduces a ground-typed expression to Int at a call site.
    public static string ReduceTypedToInt(string expr, ExprType ground)
    {
        return ReduceToInt(expr, ground);
    }

    private string GenBindableExpr(ExprType type, Scope scope, int depth)
    {
        return type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            ExprType.Float => GenFloat(scope, depth),
            ExprType.String => GenString(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported binding type: {type}"),
        };
    }
}

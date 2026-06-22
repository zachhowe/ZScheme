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
    private TupleExprGenerator? _tuple;
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
        }

        // Built-in conversions (no import required).
        if (_conv is not null)
        {
            weights.Add((1, () => _conv.IntStringRoundTripToInt(scope, depth)));
            weights.Add((1, () => _conv.IntFloatRoundTripToInt(scope, depth)));
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
                weights.Add((1, () => _widePrim.ReduceLongRoundTripToInt(scope, depth)));
            if (_widePrim.ByteAvailable)
                weights.Add((1, () => _widePrim.ReduceByteRoundTripToInt(scope, depth)));
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
    // positive N = N straight Int args. Each shape produces an Int-valued
    // expression so it slots into GenInt's contract.
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
        var a = GenInt(scope, depth - 1);
        var b = GenInt(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenIntDivModOp(Scope scope, int depth)
    {
        var op = _ctx.Rng.NextDouble() < 0.5 ? "/" : "%";
        var a = GenInt(scope, depth - 1);
        var b = 1 + _ctx.Rng.Next(99);
        return $"({op} {a} {b})";
    }

    private string GenLambdaIife(Scope scope, int depth)
    {
        var pname = _ctx.Fresh();
        var arg = GenInt(scope, depth - 1);
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var body = GenInt(bodyScope, depth - 1);
        return $"((lambda ([{pname} : Int]) {body}) {arg})";
    }

    private string GenLambdaValue(Scope scope, int depth)
    {
        var pname = _ctx.Fresh();
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
        var a = GenInt(scope, depth - 1);
        var b = GenInt(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenBoolBinOp(Scope scope, int depth)
    {
        var op = _ctx.Rng.NextDouble() < 0.5 ? "and" : "or";
        var a = GenBool(scope, depth - 1);
        var b = GenBool(scope, depth - 1);
        return $"({op} {a} {b})";
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
        var a = GenFloat(scope, depth - 1);
        var b = GenFloat(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenFloatComparison(Scope scope, int depth)
    {
        var ops = new[] { "<", ">", "<=", ">=", "=", "!=" };
        var op = ops[_ctx.Rng.Next(ops.Length)];
        var a = GenFloat(scope, depth - 1);
        var b = GenFloat(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenIf(ExprType resultType, Scope scope, int depth)
    {
        var cond = GenBool(scope, depth - 1);
        var t = GenExpr(resultType, scope, depth - 1);
        var e = GenExpr(resultType, scope, depth - 1);
        return $"(if {cond} {t} {e})";
    }

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

        var name = _ctx.Fresh();
        var value = GenBindableExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        return $"(let ([{name} {value}]) {body})";
    }

    private string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        return _match is null
            ? throw new InvalidOperationException("MatchExprGenerator not wired")
            : _match.GenMatch(resultType, scope, depth);
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

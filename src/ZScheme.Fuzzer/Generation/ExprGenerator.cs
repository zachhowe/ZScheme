using System.Globalization;
using ZScheme.Fuzzer.Generation.Stdlib;

namespace ZScheme.Fuzzer.Generation;

public sealed class ExprGenerator
{
    private readonly GeneratorContext _ctx;
    // Set by ProgramGenerator after construction to break the ctor cycle
    // (each collaborator needs ExprGenerator for inner Int sub-expressions, and
    // ExprGenerator needs them for their respective Int reducers).
    private StdlibGenerators? _stdlibGens;
    private ConversionExprGenerator? _conv;
    private SequenceExprGenerator? _sequence;
    private TupleExprGenerator? _tuple;
    private WithExprGenerator? _with;
    private PartialExprGenerator? _partial;
    private ExceptionExprGenerator? _exception;
    private StringExprGenerator? _string;
    private ClassExprGenerator? _class;
    private ObjectExprGenerator? _object;
    private ClrInteropExprGenerator? _clr;

    public ExprGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public void SetStdlibGenerators(StdlibGenerators stdlibGens) { _stdlibGens = stdlibGens; }
    public void SetConversion(ConversionExprGenerator conv) { _conv = conv; }
    public void SetSequence(SequenceExprGenerator sequence) { _sequence = sequence; }
    public void SetTuple(TupleExprGenerator tuple) { _tuple = tuple; }
    public void SetWith(WithExprGenerator with) { _with = with; }
    public void SetPartial(PartialExprGenerator partial) { _partial = partial; }
    public void SetException(ExceptionExprGenerator exception) { _exception = exception; }
    public void SetString(StringExprGenerator str) { _string = str; }
    public void SetClass(ClassExprGenerator cls) { _class = cls; }
    public void SetObject(ObjectExprGenerator obj) { _object = obj; }
    public void SetClrInterop(ClrInteropExprGenerator clr) { _clr = clr; }

    public string GenString(Scope scope, int depth) =>
        _string is null
            ? throw new InvalidOperationException("StringExprGenerator not wired")
            : _string.GenString(scope, depth);

    public string GenInt(Scope scope, int depth)
    {
        if (depth <= 0) return GenIntLeaf(scope);

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

            // List reducers.
            if (sg.List.IsImported())
            {
                weights.Add((1, () => sg.List.CountToInt(scope, depth)));
                weights.Add((1, () => sg.List.FoldToInt(scope, depth)));
                weights.Add((1, () => sg.List.NthToInt(scope, depth)));
                weights.Add((1, () => sg.List.HeadToInt(scope, depth)));
                weights.Add((1, () => sg.List.TailCountToInt(scope, depth)));
                weights.Add((1, () => sg.List.ConsCountToInt(scope, depth)));
                weights.Add((1, () => sg.List.AppendCountToInt(scope, depth)));
                weights.Add((1, () => sg.List.ConcatCountToInt(scope, depth)));
                weights.Add((1, () => sg.List.MapCountToInt(scope, depth)));
                weights.Add((1, () => sg.List.FilterCountToInt(scope, depth)));
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
            if (sg.Array.IsImported())
            {
                weights.Add((1, () => sg.Array.CountToInt(scope, depth)));
                weights.Add((1, () => sg.Array.FoldToInt(scope, depth)));
                weights.Add((1, () => sg.Array.MapFoldToInt(scope, depth)));
                weights.Add((1, () => sg.Array.NthToInt(scope, depth)));
                weights.Add((1, () => sg.Array.AppendCountToInt(scope, depth)));
                weights.Add((1, () => sg.Array.SetNthToInt(scope, depth)));
                weights.Add((1, () => sg.Array.MapCountToInt(scope, depth)));
                weights.Add((1, () => sg.Array.FilterCountToInt(scope, depth)));
            }

            // Map reducers (Int-typed shapes).
            if (sg.Map.IsImported())
            {
                weights.Add((1, () => sg.Map.CountToInt(scope, depth)));
                weights.Add((1, () => sg.Map.PutCountToInt(scope, depth)));
                weights.Add((1, () => sg.Map.RemoveCountToInt(scope, depth)));
                if (sg.Option.IsImported())
                    weights.Add((1, () => sg.Map.GetUnwrapOrToInt(scope, depth)));
                if (sg.Map.CanReduceKeysOrValues())
                {
                    weights.Add((1, () => sg.Map.KeysCountToInt(scope, depth)));
                    weights.Add((1, () => sg.Map.ValuesCountToInt(scope, depth)));
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
        }

        // Built-in conversions (no import required).
        if (_conv is not null)
        {
            weights.Add((1, () => _conv.IntStringRoundTripToInt(scope, depth)));
            weights.Add((1, () => _conv.IntFloatRoundTripToInt(scope, depth)));
        }
        if (_ctx.AuxExports.Count > 0)
            weights.Add((2, () => GenAuxCall(scope, depth)));
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
            weights.Add((1, () => _exception.WithHandlersToInt(scope, depth)));
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
        {
            args.Add(p switch
            {
                ExprType.Int => GenInt(scope, depth - 1),
                _ => throw new InvalidOperationException($"Unsupported aux param type: {p}"),
            });
        }
        return $"({export.QualifiedName} {string.Join(" ", args)})";
    }

    // Constructs a value of a user-declared generic union (all type params
    // instantiated at Int) and destructures it via an exhaustive match down to Int.
    // The scrutinee is built using a non-nullary constructor when available so the
    // union type is pinned without needing a type annotation.
    //
    // Field patterns are a mix of binders, wildcards, and literals. When any arm
    // contains a literal pattern the match is no longer guaranteed exhaustive by
    // ctor coverage alone, so a terminal `[_ fallback]` catchall is appended.
    private string GenUserUnionMatch(Scope scope, int depth)
    {
        var u = _ctx.UserUnions[_ctx.Rng.Next(_ctx.UserUnions.Count)];

        // Pick a ctor that carries at least one field so inference pins the union
        // type arguments. Fall back to any ctor if (hypothetically) none carry fields.
        var withFields = u.Ctors.Where(c => c.FieldTypeParams.Count > 0).ToList();
        var scrutCtor = withFields.Count > 0
            ? withFields[_ctx.Rng.Next(withFields.Count)]
            : u.Ctors[_ctx.Rng.Next(u.Ctors.Count)];

        var scrutArgs = new List<string>();
        for (var i = 0; i < scrutCtor.FieldTypeParams.Count; i++)
            scrutArgs.Add(GenInt(scope, depth - 1));

        var scrutExpr = scrutArgs.Count == 0
            ? scrutCtor.Name
            : $"({scrutCtor.Name} {string.Join(" ", scrutArgs)})";

        var arms = new List<string>();
        var anyLiteral = false;
        foreach (var c in u.Ctors)
        {
            var (pattern, armScope, hasLiteral) = GenCtorArmPattern(c, scope);
            if (hasLiteral) anyLiteral = true;
            var body = GenInt(armScope, depth - 1);
            arms.Add($"[{pattern} {body}]");
        }

        if (anyLiteral)
        {
            var fallback = GenInt(scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }

        return $"(match {scrutExpr} {string.Join(" ", arms)})";
    }

    // Generates a pattern for a single ctor arm. Per field: 65% fresh binder,
    // 20% wildcard `_`, 15% compatible literal. Returns the pattern string, the
    // scope extended with any new binders, and whether the pattern contains a
    // literal (so the caller knows to emit a terminal catchall for exhaustiveness).
    private (string Pattern, Scope Scope, bool HasLiteral) GenCtorArmPattern(
        UserUnionCtor c, Scope scope)
    {
        if (c.FieldTypeParams.Count == 0) return (c.Name, scope, false);

        var parts = new List<string>();
        var armScope = scope;
        var hasLiteral = false;
        for (var i = 0; i < c.FieldTypeParams.Count; i++)
        {
            var roll = _ctx.Rng.NextDouble();
            if (roll < 0.65)
            {
                // Fresh binder — type param is instantiated at Int.
                var b = _ctx.Fresh();
                armScope = armScope.Extend(b, ExprType.Int);
                parts.Add(b);
            }
            else if (roll < 0.85)
            {
                parts.Add("_");
            }
            else
            {
                // Int literal — small value. Union type params all instantiate
                // at Int for this generator, so literal must match that type.
                var lit = _ctx.Rng.Next(-2, 5);
                parts.Add(lit.ToString(CultureInfo.InvariantCulture));
                hasLiteral = true;
            }
        }
        return ($"({c.Name} {string.Join(" ", parts)})", armScope, hasLiteral);
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
        if (pick < 0.1) return int.MinValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.2) return int.MaxValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.5) return (_ctx.Rng.Next(0, 200001) - 100000).ToString(CultureInfo.InvariantCulture);
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
        return $"((fn [[{pname} : Int]] {body}) {arg})";
    }

    private string GenLambdaValue(Scope scope, int depth)
    {
        var pname = _ctx.Fresh();
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var bodyDepth = Math.Max(1, depth - 1);
        var body = GenInt(bodyScope, bodyDepth);
        return $"(fn [[{pname} : Int]] {body})";
    }

    public string GenBool(Scope scope, int depth)
    {
        if (depth <= 0) return GenBoolLeaf(scope);

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
        if (_stdlibGens is not null)
        {
            var sg = _stdlibGens;

            if (sg.Map.IsImported())
            {
                weights.Add((1, () => sg.Map.ContainsPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.Map.EmptyPredicateToBool(scope, depth)));
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

            if (sg.List.IsImported())
                weights.Add((1, () => sg.List.EmptyPredicateToBool(scope, depth)));

            if (sg.Array.IsImported())
                weights.Add((1, () => sg.Array.EmptyPredicateToBool(scope, depth)));

            if (sg.String.IsImported() && _string is not null)
            {
                weights.Add((1, () => sg.String.EqualsPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.EmptyPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.StartsWithPredicateToBool(scope, depth)));
                weights.Add((1, () => sg.String.EndsWithPredicateToBool(scope, depth)));
            }
        }
        if (_clr is not null
            && _ctx.EmittedClrBindings.Contains(ClrBinding.StringIsNullOrEmpty)
            && _string is not null)
            weights.Add((1, () => _clr.ReduceStringIsEmptyToBool(scope, depth)));
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
        if (depth <= 0) return GenFloatLeaf(scope);

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
        if (pick < 0.08) return "0.0";
        if (pick < 0.16) return "-0.0";
        if (pick < 0.24) return "1.0";
        if (pick < 0.32) return "-1.0";
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
        if (pick < 0.50) bindingType = ExprType.Int;
        else if (pick < 0.70) bindingType = ExprType.Bool;
        else if (pick < 0.85) bindingType = ExprType.Float;
        else if (_string is not null) bindingType = ExprType.String;
        else bindingType = ExprType.Float;

        var name = _ctx.Fresh();
        var value = GenBindableExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        return $"(let [{name} {value}] {body})";
    }

    private string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        // Pick a scrutinee kind. Int dominates (matches the historical distribution);
        // tuple / float / string branches add decision-tree variety beyond the flat
        // Int-literal path.
        var kinds = new List<(int Weight, string Kind)>
        {
            (3, "bool"),
            (5, "int"),
            (2, "tuple"),
            (1, "float"),
        };
        if (_string is not null)
            kinds.Add((1, "string"));

        var kind = _ctx.PickWeighted(kinds);
        return kind switch
        {
            "bool" => GenMatchBool(resultType, scope, depth),
            "int" => GenMatchInt(resultType, scope, depth),
            "tuple" => GenMatchTuple(resultType, scope, depth),
            "float" => GenMatchFloat(resultType, scope, depth),
            "string" => GenMatchString(resultType, scope, depth),
            _ => throw new InvalidOperationException($"Unknown match kind: {kind}")
        };
    }

    private string GenMatchBool(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = GenBool(scope, depth - 1);
        var bodyT = GenExpr(resultType, scope, depth - 1);
        var bodyF = GenExpr(resultType, scope, depth - 1);
        var arms = new List<string> { $"[#t {bodyT}]", $"[#f {bodyF}]" };
        // Occasionally append a redundant wildcard arm to exercise redundant-arm handling.
        if (_ctx.Rng.NextDouble() < 0.15)
        {
            var bodyW = GenExpr(resultType, scope, depth - 1);
            arms.Add($"[_ {bodyW}]");
        }
        return $"(match {scrutinee} {string.Join(" ", arms)})";
    }

    private string GenMatchInt(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = GenInt(scope, depth - 1);
        var numLits = 1 + _ctx.Rng.Next(4);
        var usedLits = new HashSet<int>();
        var armParts = new List<string>();
        for (var i = 0; i < numLits; i++)
        {
            int lit;
            var attempts = 0;
            do
            {
                lit = _ctx.Rng.Next(-2, 5);
                attempts++;
            } while (!usedLits.Add(lit) && attempts < 8);
            if (attempts >= 8) break;
            var body = GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }

        // Final catch-all: either wildcard or variable-binder (binds scrutinee value).
        if (_ctx.Rng.NextDouble() < 0.5)
        {
            var bodyW = GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[_ {bodyW}]");
        }
        else
        {
            var k = _ctx.Fresh();
            var childScope = scope.Extend(k, ExprType.Int);
            var bodyK = GenExpr(resultType, childScope, depth - 1);
            armParts.Add($"[{k} {bodyK}]");
        }
        return $"(match {scrutinee} {string.Join(" ", armParts)})";
    }

    // Scrutinee: Int-typed tuple `(values <int> <int>)` of arity 2 or 3.
    // Arm pattern is a tuple pattern `(values p1 p2 ...)` where each pN is binder /
    // wildcard / Int-literal. Literal patterns trigger a terminal `[_ fallback]`
    // arm so the match remains exhaustive.
    private string GenMatchTuple(ExprType resultType, Scope scope, int depth)
    {
        var arity = 2 + _ctx.Rng.Next(2);
        var elems = new List<string>();
        for (var i = 0; i < arity; i++)
            elems.Add(GenInt(scope, depth - 1));

        var patternParts = new List<string>();
        var armScope = scope;
        var hasBinder = false;
        var hasLiteral = false;
        for (var i = 0; i < arity; i++)
        {
            var forceBinder = !hasBinder && i == arity - 1;
            var roll = forceBinder ? 0.0 : _ctx.Rng.NextDouble();
            if (roll < 0.60)
            {
                var b = _ctx.Fresh();
                patternParts.Add(b);
                armScope = armScope.Extend(b, ExprType.Int);
                hasBinder = true;
            }
            else if (roll < 0.85)
            {
                patternParts.Add("_");
            }
            else
            {
                patternParts.Add(_ctx.Rng.Next(-2, 5).ToString(CultureInfo.InvariantCulture));
                hasLiteral = true;
            }
        }

        var body = GenExpr(resultType, armScope, depth - 1);
        var scrutinee = $"(values {string.Join(" ", elems)})";
        var mainArm = $"[(values {string.Join(" ", patternParts)}) {body}]";
        if (hasLiteral)
        {
            var fallback = GenExpr(resultType, scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }
        return $"(match {scrutinee} {mainArm})";
    }

    // Scrutinee: Float. 1-3 float-literal arms plus terminal wildcard (float
    // matches are never exhaustive per ExhaustivenessChecker rules).
    private string GenMatchFloat(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = GenFloat(scope, depth - 1);
        var pool = new[] { "0.0", "-0.0", "1.0", "-1.0", "2.5", "-3.14" };
        var numLits = 1 + _ctx.Rng.Next(3);
        var shuffled = pool.OrderBy(_ => _ctx.Rng.Next()).Take(numLits).ToList();

        var armParts = new List<string>();
        foreach (var lit in shuffled)
        {
            var body = GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }
        var fallback = GenExpr(resultType, scope, depth - 1);
        armParts.Add($"[_ {fallback}]");
        return $"(match {scrutinee} {string.Join(" ", armParts)})";
    }

    // Scrutinee: String. 1-3 plain-ASCII literal arms plus terminal wildcard.
    private string GenMatchString(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = GenString(scope, depth - 1);
        var pool = new[] { "\"\"", "\"a\"", "\"abc\"", "\"hello\"", "\"fuzz\"" };
        var numLits = 1 + _ctx.Rng.Next(3);
        var shuffled = pool.OrderBy(_ => _ctx.Rng.Next()).Take(numLits).ToList();

        var armParts = new List<string>();
        foreach (var lit in shuffled)
        {
            var body = GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }
        var fallback = GenExpr(resultType, scope, depth - 1);
        armParts.Add($"[_ {fallback}]");
        return $"(match {scrutinee} {string.Join(" ", armParts)})";
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
        for (var i = 0; i < func.ParamTypes.Count; i++)
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
            args.Add(paramType switch
            {
                ExprType.Int when isGeneric => GenGroundLeaf(ground, scope, depth - 1),
                ExprType.Int => GenInt(scope, depth - 1),
                ExprType.IntFn when isGeneric => GenGroundFnArg(ground, scope, depth - 1),
                ExprType.IntFn => GenIntFnArg(scope, depth - 1),
                _ => throw new InvalidOperationException($"Unsupported param type: {paramType}")
            });
        }

        var call = $"({func.Name} {string.Join(" ", args)})";
        // If the return type is the generic `^a`, reduce it back to Int at the
        // call site so GenInt's contract holds.
        return func.ReturnIsGeneric ? ReduceToInt(call, ground) : call;
    }

    private ExprType PickCallGround(UserFunc func)
    {
        if (func.AllowedGrounds.Count <= 1) return ExprType.Int;
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

    private string GenGroundLeaf(ExprType ground, Scope scope, int depth) =>
        ground switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            ExprType.Float => GenFloat(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}")
        };

    // Emits `(fn [[p : GroundType]] <int-body>)` for passing as (Fn [^a] Int) arg.
    private string GenGroundFnArg(ExprType ground, Scope scope, int depth)
    {
        if (ground == ExprType.Int) return GenIntFnArg(scope, depth);

        var pname = _ctx.Fresh();
        var bodyScope = scope.Extend(pname, ground);
        var bodyDepth = Math.Max(1, depth - 1);
        var body = GenInt(bodyScope, bodyDepth);
        var typeName = GroundTypeName(ground);
        return $"(fn [[{pname} : {typeName}]] {body})";
    }

    private static string GroundTypeName(ExprType ground) =>
        ground switch
        {
            ExprType.Int => "Int",
            ExprType.Bool => "Bool",
            ExprType.Float => "Float",
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}")
        };

    // Wraps a ground-typed expression so it reduces to Int.
    private static string ReduceToInt(string expr, ExprType ground) =>
        ground switch
        {
            ExprType.Int => expr,
            ExprType.Bool => $"(if {expr} 1 0)",
            // `float->int` is defined in the default TypeEnv (`Types/TypeEnv.cs`)
            // and lowers to `System.Convert.ToInt32(double)` in IrLowering.
            ExprType.Float => $"(float->int {expr})",
            _ => throw new InvalidOperationException($"Unsupported ground: {ground}")
        };

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

    public string GenExpr(ExprType type, Scope scope, int depth) =>
        type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported type: {type}")
        };

    private string GenBindableExpr(ExprType type, Scope scope, int depth) =>
        type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            ExprType.Float => GenFloat(scope, depth),
            ExprType.String => GenString(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported binding type: {type}")
        };
}

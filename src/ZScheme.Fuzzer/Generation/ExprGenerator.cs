using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

public sealed class ExprGenerator
{
    private readonly GeneratorContext _ctx;
    // Set by ProgramGenerator after construction to break the ctor cycle
    // (StdlibImportGenerator needs ExprGenerator for inner-Int sub-expressions,
    // and ExprGenerator needs StdlibImportGenerator for import-driven Int branches).
    private StdlibImportGenerator? _stdlib;

    public ExprGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public void SetStdlib(StdlibImportGenerator stdlib) { _stdlib = stdlib; }

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
        if (_ctx.UserFuncs.Count > 0)
            weights.Add((2, () => GenCall(scope, depth)));
        if (scope.HasVarOf(ExprType.IntFn))
            weights.Add((2, () => GenIntFnApply(scope, depth)));
        if (_ctx.UserUnions.Count > 0)
            weights.Add((2, () => GenUserUnionMatch(scope, depth)));
        if (_ctx.UserRecords.Count > 0)
            weights.Add((2, () => GenUserRecordAccess(scope, depth)));
        if (_stdlib is not null)
        {
            if (_ctx.Imports.Contains(StdlibImport.Option))
                weights.Add((2, () => _stdlib.ReduceOptionToInt(scope, depth)));
            if (_ctx.Imports.Contains(StdlibImport.List))
                weights.Add((2, () => _stdlib.ReduceListToInt(scope, depth)));
            if (_ctx.Imports.Contains(StdlibImport.Result))
                weights.Add((2, () => _stdlib.ReduceResultToInt(scope, depth)));
        }
        if (_ctx.AuxExports.Count > 0)
            weights.Add((2, () => GenAuxCall(scope, depth)));

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

        // Exhaustive arms — one per declared ctor, in declaration order.
        var arms = new List<string>();
        foreach (var c in u.Ctors)
        {
            var binders = new List<string>();
            var armScope = scope;
            for (var i = 0; i < c.FieldTypeParams.Count; i++)
            {
                var b = _ctx.Fresh();
                binders.Add(b);
                // Every type param is instantiated at Int for this match.
                armScope = armScope.Extend(b, ExprType.Int);
            }

            var pattern = binders.Count == 0
                ? c.Name
                : $"({c.Name} {string.Join(" ", binders)})";
            var body = GenInt(armScope, depth - 1);
            arms.Add($"[{pattern} {body}]");
        }

        return $"(match {scrutExpr} {string.Join(" ", arms)})";
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
        if (pick < 0.55) bindingType = ExprType.Int;
        else if (pick < 0.80) bindingType = ExprType.Bool;
        else bindingType = ExprType.Float;

        var name = _ctx.Fresh();
        var value = GenBindableExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        return $"(let [{name} {value}] {body})";
    }

    private string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        var scrutineeIsBool = _ctx.Rng.NextDouble() < 0.3;
        if (scrutineeIsBool)
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
        else
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
    }

    private string GenCall(Scope scope, int depth)
    {
        var func = _ctx.UserFuncs[_ctx.Rng.Next(_ctx.UserFuncs.Count)];
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
            args.Add(paramType switch
            {
                ExprType.Int => GenInt(scope, depth - 1),
                ExprType.IntFn => GenIntFnArg(scope, depth - 1),
                _ => throw new InvalidOperationException($"Unsupported param type: {paramType}")
            });
        }
        return $"({func.Name} {string.Join(" ", args)})";
    }

    private string GenIntFnArg(Scope scope, int depth)
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
            _ => throw new InvalidOperationException($"Unsupported binding type: {type}")
        };
}

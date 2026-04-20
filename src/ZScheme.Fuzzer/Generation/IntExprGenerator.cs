using System.Text;

namespace ZScheme.Fuzzer.Generation;

public sealed class IntExprGenerator
{
    private enum ExprType { Int, Bool }

    private readonly Random _rng;
    private readonly int _maxDepth;
    private readonly int _maxFuncs;
    private int _nameCounter;
    private readonly List<UserFunc> _userFuncs = [];

    public IntExprGenerator(Random rng, int maxDepth, int maxFuncs)
    {
        _rng = rng;
        _maxDepth = Math.Max(1, maxDepth);
        _maxFuncs = Math.Max(0, maxFuncs);
    }

    public GeneratedProgram Generate(long caseSeed)
    {
        _nameCounter = 0;
        _userFuncs.Clear();

        var moduleName = $"fuzz_{(uint)caseSeed:x8}";
        var sb = new StringBuilder();
        sb.AppendLine("(namespace ZSchemeFuzzed)");
        sb.AppendLine();
        sb.AppendLine($"(module {moduleName})");
        sb.AppendLine();

        var numFuncs = _rng.Next(_maxFuncs + 1);
        for (var i = 0; i < numFuncs; i++)
        {
            var arity = 1 + _rng.Next(2);
            var func = GenerateFunction($"f{i}", arity);
            _userFuncs.Add(func);
            sb.AppendLine(func.Definition);
            sb.AppendLine();
        }

        var computeScope = new Scope();
        var computeExpr = GenInt(computeScope, _maxDepth);
        sb.AppendLine("(define (compute) : Int");
        sb.AppendLine($"  {computeExpr})");

        return new GeneratedProgram(sb.ToString(), caseSeed, moduleName);
    }

    private UserFunc GenerateFunction(string name, int arity)
    {
        var scope = new Scope();
        var paramNames = new List<string>();
        for (var i = 0; i < arity; i++)
        {
            var pname = Fresh();
            paramNames.Add(pname);
            scope = scope.Extend(pname, ExprType.Int);
        }

        var body = GenInt(scope, _maxDepth);
        var paramStr = string.Join(" ", paramNames.Select(p => $"[{p} : Int]"));
        var def = $"(define ({name} {paramStr}) : Int\n  {body})";
        return new UserFunc(name, arity, def);
    }

    private string GenInt(Scope scope, int depth)
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
        };
        if (_userFuncs.Count > 0)
            weights.Add((2, () => GenCall(scope, depth)));

        return PickWeighted(weights)();
    }

    private string GenIntLeaf(Scope scope)
    {
        var intVars = scope.GetVars(ExprType.Int);
        if (intVars.Count > 0 && _rng.NextDouble() < 0.5)
            return intVars[_rng.Next(intVars.Count)];

        var pick = _rng.NextDouble();
        if (pick < 0.1) return int.MinValue.ToString();
        if (pick < 0.2) return int.MaxValue.ToString();
        if (pick < 0.5) return (_rng.Next(0, 200001) - 100000).ToString();
        return _rng.Next(0, 101).ToString();
    }

    private string GenIntBinOp(Scope scope, int depth)
    {
        var ops = new[] { "+", "-", "*" };
        var op = ops[_rng.Next(ops.Length)];
        var a = GenInt(scope, depth - 1);
        var b = GenInt(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenIntDivModOp(Scope scope, int depth)
    {
        var op = _rng.NextDouble() < 0.5 ? "/" : "%";
        var a = GenInt(scope, depth - 1);
        var b = 1 + _rng.Next(99);
        return $"({op} {a} {b})";
    }

    private string GenLambdaIife(Scope scope, int depth)
    {
        var pname = Fresh();
        var arg = GenInt(scope, depth - 1);
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var body = GenInt(bodyScope, depth - 1);
        return $"((fn [[{pname} : Int]] {body}) {arg})";
    }

    private string GenBool(Scope scope, int depth)
    {
        if (depth <= 0) return GenBoolLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (3, () => GenBoolLeaf(scope)),
            (4, () => GenComparison(scope, depth)),
            (2, () => GenBoolBinOp(scope, depth)),
            (1, () => $"(not {GenBool(scope, depth - 1)})"),
            (1, () => GenIf(ExprType.Bool, scope, depth)),
        };
        return PickWeighted(weights)();
    }

    private string GenBoolLeaf(Scope scope)
    {
        var boolVars = scope.GetVars(ExprType.Bool);
        if (boolVars.Count > 0 && _rng.NextDouble() < 0.5)
            return boolVars[_rng.Next(boolVars.Count)];
        return _rng.NextDouble() < 0.5 ? "#t" : "#f";
    }

    private string GenComparison(Scope scope, int depth)
    {
        var ops = new[] { "=", "!=", "<", ">", "<=", ">=" };
        var op = ops[_rng.Next(ops.Length)];
        var a = GenInt(scope, depth - 1);
        var b = GenInt(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenBoolBinOp(Scope scope, int depth)
    {
        var op = _rng.NextDouble() < 0.5 ? "and" : "or";
        var a = GenBool(scope, depth - 1);
        var b = GenBool(scope, depth - 1);
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
        var bindingType = _rng.NextDouble() < 0.7 ? ExprType.Int : ExprType.Bool;
        var name = Fresh();
        var value = GenExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        return $"(let [{name} {value}] {body})";
    }

    private string GenCall(Scope scope, int depth)
    {
        var func = _userFuncs[_rng.Next(_userFuncs.Count)];
        var args = new List<string>();
        for (var i = 0; i < func.Arity; i++)
            args.Add(GenInt(scope, depth - 1));
        return $"({func.Name} {string.Join(" ", args)})";
    }

    private string GenExpr(ExprType type, Scope scope, int depth) =>
        type switch
        {
            ExprType.Int => GenInt(scope, depth),
            ExprType.Bool => GenBool(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported type: {type}")
        };

    private T PickWeighted<T>(IReadOnlyList<(int Weight, T Value)> options)
    {
        var total = options.Sum(o => o.Weight);
        var pick = _rng.Next(total);
        var acc = 0;
        foreach (var (w, v) in options)
        {
            acc += w;
            if (pick < acc) return v;
        }
        return options[^1].Value;
    }

    private string Fresh() => $"x{_nameCounter++}";

    private sealed record UserFunc(string Name, int Arity, string Definition);

    private sealed class Scope
    {
        private readonly Dictionary<string, ExprType> _bindings;

        public Scope() { _bindings = new Dictionary<string, ExprType>(); }
        private Scope(Dictionary<string, ExprType> bindings) { _bindings = bindings; }

        public Scope Extend(string name, ExprType type)
        {
            var copy = new Dictionary<string, ExprType>(_bindings) { [name] = type };
            return new Scope(copy);
        }

        public List<string> GetVars(ExprType type)
        {
            var result = new List<string>();
            foreach (var (k, v) in _bindings)
                if (v == type) result.Add(k);
            return result;
        }
    }
}

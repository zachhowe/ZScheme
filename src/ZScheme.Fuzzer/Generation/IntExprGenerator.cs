using System.Globalization;
using System.Text;

namespace ZScheme.Fuzzer.Generation;

public sealed class IntExprGenerator
{
    private enum ExprType { Int, Bool, Float, IntFn }

    private enum UserFuncKind { Regular, Recursive, HigherOrder }

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
            var func = GenerateUserFunction($"f{i}");
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

    private UserFunc GenerateUserFunction(string name)
    {
        var pick = _rng.NextDouble();
        if (pick < 0.25) return GenerateRecursiveFunction(name);
        if (pick < 0.50) return GenerateHigherOrderFunction(name);
        return GenerateRegularFunction(name);
    }

    private UserFunc GenerateRegularFunction(string name)
    {
        var arity = 1 + _rng.Next(2);
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
        var paramTypes = Enumerable.Repeat(ExprType.Int, arity).ToList();
        return new UserFunc(name, UserFuncKind.Regular, paramTypes, def);
    }

    private UserFunc GenerateRecursiveFunction(string name)
    {
        var nParam = Fresh();
        var accParam = Fresh();
        var scope = new Scope()
            .Extend(nParam, ExprType.Int)
            .Extend(accParam, ExprType.Int);

        var bodyDepth = Math.Min(_maxDepth, 3);
        var baseExpr = GenInt(scope, bodyDepth);
        var stepExpr = GenInt(scope, bodyDepth);

        var isTail = _rng.NextDouble() < 0.75;
        var recCall = $"({name} (- {nParam} 1) {stepExpr})";
        var elseBranch = isTail ? recCall : $"(+ 1 {recCall})";
        var body = $"(if (<= {nParam} 0) {baseExpr} {elseBranch})";

        var def = $"(define ({name} [{nParam} : Int] [{accParam} : Int]) : Int\n  {body})";
        return new UserFunc(name, UserFuncKind.Recursive, [ExprType.Int, ExprType.Int], def);
    }

    private UserFunc GenerateHigherOrderFunction(string name)
    {
        var fParam = Fresh();
        var xParam = Fresh();
        var scope = new Scope()
            .Extend(fParam, ExprType.IntFn)
            .Extend(xParam, ExprType.Int);

        var body = GenInt(scope, _maxDepth);
        var def = $"(define ({name} [{fParam} : (Fn [Int] Int)] [{xParam} : Int]) : Int\n  {body})";
        return new UserFunc(name, UserFuncKind.HigherOrder, [ExprType.IntFn, ExprType.Int], def);
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
            (2, () => GenMatch(ExprType.Int, scope, depth)),
        };
        if (_userFuncs.Count > 0)
            weights.Add((2, () => GenCall(scope, depth)));
        if (scope.HasVarOf(ExprType.IntFn))
            weights.Add((2, () => GenIntFnApply(scope, depth)));

        return PickWeighted(weights)();
    }

    private string GenIntLeaf(Scope scope)
    {
        var intVars = scope.GetVars(ExprType.Int);
        if (intVars.Count > 0 && _rng.NextDouble() < 0.5)
            return intVars[_rng.Next(intVars.Count)];

        var pick = _rng.NextDouble();
        if (pick < 0.1) return int.MinValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.2) return int.MaxValue.ToString(CultureInfo.InvariantCulture);
        if (pick < 0.5) return (_rng.Next(0, 200001) - 100000).ToString(CultureInfo.InvariantCulture);
        return _rng.Next(0, 101).ToString(CultureInfo.InvariantCulture);
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

    private string GenLambdaValue(Scope scope, int depth)
    {
        var pname = Fresh();
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var bodyDepth = Math.Max(1, depth - 1);
        var body = GenInt(bodyScope, bodyDepth);
        return $"(fn [[{pname} : Int]] {body})";
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
            (1, () => GenMatch(ExprType.Bool, scope, depth)),
            (2, () => GenFloatComparison(scope, depth)),
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

    private string GenFloat(Scope scope, int depth)
    {
        if (depth <= 0) return GenFloatLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (3, () => GenFloatLeaf(scope)),
            (3, () => GenFloatBinOp(scope, depth)),
        };
        return PickWeighted(weights)();
    }

    private string GenFloatLeaf(Scope scope)
    {
        var fltVars = scope.GetVars(ExprType.Float);
        if (fltVars.Count > 0 && _rng.NextDouble() < 0.5)
            return fltVars[_rng.Next(fltVars.Count)];

        var pick = _rng.NextDouble();
        if (pick < 0.08) return "0.0";
        if (pick < 0.16) return "-0.0";
        if (pick < 0.24) return "1.0";
        if (pick < 0.32) return "-1.0";
        var value = _rng.NextDouble() * 2000.0 - 1000.0;
        var s = value.ToString("G7", CultureInfo.InvariantCulture);
        // Ensure literal is parsed as float: force a decimal point if absent.
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
            s += ".0";
        return s;
    }

    private string GenFloatBinOp(Scope scope, int depth)
    {
        var ops = new[] { "+", "-", "*", "/" };
        var op = ops[_rng.Next(ops.Length)];
        var a = GenFloat(scope, depth - 1);
        var b = GenFloat(scope, depth - 1);
        return $"({op} {a} {b})";
    }

    private string GenFloatComparison(Scope scope, int depth)
    {
        var ops = new[] { "<", ">", "<=", ">=", "=", "!=" };
        var op = ops[_rng.Next(ops.Length)];
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
        var pick = _rng.NextDouble();
        ExprType bindingType;
        if (pick < 0.55) bindingType = ExprType.Int;
        else if (pick < 0.80) bindingType = ExprType.Bool;
        else bindingType = ExprType.Float;

        var name = Fresh();
        var value = GenBindableExpr(bindingType, scope, depth - 1);
        var childScope = scope.Extend(name, bindingType);
        var body = GenExpr(resultType, childScope, depth - 1);
        return $"(let [{name} {value}] {body})";
    }

    private string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        var scrutineeIsBool = _rng.NextDouble() < 0.3;
        if (scrutineeIsBool)
        {
            var scrutinee = GenBool(scope, depth - 1);
            var bodyT = GenExpr(resultType, scope, depth - 1);
            var bodyF = GenExpr(resultType, scope, depth - 1);
            var arms = new List<string> { $"[#t {bodyT}]", $"[#f {bodyF}]" };
            // Occasionally append a redundant wildcard arm to exercise redundant-arm handling.
            if (_rng.NextDouble() < 0.15)
            {
                var bodyW = GenExpr(resultType, scope, depth - 1);
                arms.Add($"[_ {bodyW}]");
            }
            return $"(match {scrutinee} {string.Join(" ", arms)})";
        }
        else
        {
            var scrutinee = GenInt(scope, depth - 1);
            var numLits = 1 + _rng.Next(4);
            var usedLits = new HashSet<int>();
            var armParts = new List<string>();
            for (var i = 0; i < numLits; i++)
            {
                int lit;
                var attempts = 0;
                do
                {
                    lit = _rng.Next(-2, 5);
                    attempts++;
                } while (!usedLits.Add(lit) && attempts < 8);
                if (attempts >= 8) break;
                var body = GenExpr(resultType, scope, depth - 1);
                armParts.Add($"[{lit} {body}]");
            }

            // Final catch-all: either wildcard or variable-binder (binds scrutinee value).
            if (_rng.NextDouble() < 0.5)
            {
                var bodyW = GenExpr(resultType, scope, depth - 1);
                armParts.Add($"[_ {bodyW}]");
            }
            else
            {
                var k = Fresh();
                var childScope = scope.Extend(k, ExprType.Int);
                var bodyK = GenExpr(resultType, childScope, depth - 1);
                armParts.Add($"[{k} {bodyK}]");
            }
            return $"(match {scrutinee} {string.Join(" ", armParts)})";
        }
    }

    private string GenCall(Scope scope, int depth)
    {
        var func = _userFuncs[_rng.Next(_userFuncs.Count)];
        var args = new List<string>();
        for (var i = 0; i < func.ParamTypes.Count; i++)
        {
            var paramType = func.ParamTypes[i];
            if (func.Kind == UserFuncKind.Recursive && i == 0)
            {
                // First recursive-function argument must be a bounded small Int literal
                // so the recursion terminates regardless of TCO correctness.
                args.Add(_rng.Next(0, 21).ToString(CultureInfo.InvariantCulture));
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
        if (inScope.Count > 0 && _rng.NextDouble() < 0.4)
            return inScope[_rng.Next(inScope.Count)];
        return GenLambdaValue(scope, Math.Max(1, depth));
    }

    private string GenIntFnApply(Scope scope, int depth)
    {
        var fns = scope.GetVars(ExprType.IntFn);
        var f = fns[_rng.Next(fns.Count)];
        var arg = GenInt(scope, depth - 1);
        return $"({f} {arg})";
    }

    private string GenExpr(ExprType type, Scope scope, int depth) =>
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

    private sealed record UserFunc(string Name, UserFuncKind Kind, List<ExprType> ParamTypes, string Definition);

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

        public bool HasVarOf(ExprType type)
        {
            foreach (var v in _bindings.Values)
                if (v == type) return true;
            return false;
        }
    }
}

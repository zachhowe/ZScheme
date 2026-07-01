using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Generates `(match scrut [pat body] ...)` expressions across the supported
// scrutinee shapes (bool, int, tuple, float, string, user union). Lifted out
// of ExprGenerator so the match-specific patterns and arm-mixing logic can
// grow without inflating ExprGenerator further.
//
// Coverage notes beyond the simple literal-arm case:
//   * Primitive matches (bool/int) mix wildcard-only and binder catchalls so
//     the exhaustiveness checker sees both shapes.
//   * Tuple matches mix per-slot binders / wildcards / literals.
//   * User-union matches over a recursive (Cons-shaped) union emit nested
//     ctor patterns like `(Cons_n h (Cons_n h2 _))` and the corresponding
//     nested scrutinee, so the PatternCompiler's nested decision-tree path
//     and the union-codegen recursive layout both get exercised.
public sealed class MatchExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    private MatchPatternExtensionsGenerator? _ext;

    public MatchExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public void SetExtensions(MatchPatternExtensionsGenerator ext)
    {
        _ext = ext;
    }

    public string GenMatch(ExprType resultType, Scope scope, int depth)
    {
        var kinds = new List<(int Weight, string Kind)>
        {
            (3, "bool"),
            (5, "int"),
            (2, "tuple"),
            (1, "float"),
            (1, "string"),
        };
        if (_ext is not null)
            kinds.Add((2, "het-tuple"));
        if (_ext is not null && _ext.HasRecord())
        {
            kinds.Add((2, "record"));
            kinds.Add((2, "tuple-of-record"));
        }

        var kind = _ctx.PickWeighted(kinds);
        return kind switch
        {
            "bool" => GenMatchBool(resultType, scope, depth),
            "int" => GenMatchInt(resultType, scope, depth),
            "tuple" => GenMatchTuple(resultType, scope, depth),
            "float" => GenMatchFloat(resultType, scope, depth),
            "string" => GenMatchString(resultType, scope, depth),
            "het-tuple" => _ext!.GenHeterogeneousTupleMatch(resultType, scope, depth),
            "record" => _ext!.GenRecordMatch(resultType, scope, depth),
            "tuple-of-record" => _ext!.GenTupleOfRecordMatch(resultType, scope, depth),
            _ => throw new InvalidOperationException($"Unknown match kind: {kind}"),
        };
    }

    public string GenMatchBool(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = _exprs.GenBool(scope, depth - 1);
        var bodyT = _exprs.GenExpr(resultType, scope, depth - 1);
        var bodyF = _exprs.GenExpr(resultType, scope, depth - 1);
        var arms = new List<string> { $"[#t {bodyT}]", $"[#f {bodyF}]" };
        if (_ctx.Rng.NextDouble() < 0.15)
        {
            var bodyW = _exprs.GenExpr(resultType, scope, depth - 1);
            arms.Add($"[_ {bodyW}]");
        }

        return $"(match {scrutinee} {string.Join(" ", arms)})";
    }

    public string GenMatchInt(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = _exprs.GenInt(scope, depth - 1);
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

            if (attempts >= 8)
                break;
            var body = _exprs.GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }

        if (_ctx.Rng.NextDouble() < 0.5)
        {
            var bodyW = _exprs.GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[_ {bodyW}]");
        }
        else
        {
            var k = _ctx.Fresh();
            var childScope = scope.Extend(k, ExprType.Int);
            var bodyK = _exprs.GenExpr(resultType, childScope, depth - 1);
            armParts.Add($"[{k} {bodyK}]");
        }

        return $"(match {scrutinee} {string.Join(" ", armParts)})";
    }

    public string GenMatchTuple(ExprType resultType, Scope scope, int depth)
    {
        var arity = 2 + _ctx.Rng.Next(2);
        var elems = new List<string>();
        for (var i = 0; i < arity; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));

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

        var body = _exprs.GenExpr(resultType, armScope, depth - 1);
        var scrutinee = $"(values {string.Join(" ", elems)})";
        var mainArm = $"[(values {string.Join(" ", patternParts)}) {body}]";
        if (hasLiteral)
        {
            var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }

        return $"(match {scrutinee} {mainArm})";
    }

    public string GenMatchFloat(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = _exprs.GenFloat(scope, depth - 1);
        var pool = new[] { "0.0", "-0.0", "1.0", "-1.0", "2.5", "-3.14" };
        var numLits = 1 + _ctx.Rng.Next(3);
        var shuffled = pool.OrderBy(_ => _ctx.Rng.Next()).Take(numLits).ToList();

        var armParts = new List<string>();
        foreach (var lit in shuffled)
        {
            var body = _exprs.GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }

        var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
        armParts.Add($"[_ {fallback}]");
        return $"(match {scrutinee} {string.Join(" ", armParts)})";
    }

    public string GenMatchString(ExprType resultType, Scope scope, int depth)
    {
        var scrutinee = _exprs.GenString(scope, depth - 1);
        var pool = new[] { "\"\"", "\"a\"", "\"abc\"", "\"hello\"", "\"fuzz\"" };
        var numLits = 1 + _ctx.Rng.Next(3);
        var shuffled = pool.OrderBy(_ => _ctx.Rng.Next()).Take(numLits).ToList();

        var armParts = new List<string>();
        foreach (var lit in shuffled)
        {
            var body = _exprs.GenExpr(resultType, scope, depth - 1);
            armParts.Add($"[{lit} {body}]");
        }

        var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
        armParts.Add($"[_ {fallback}]");
        return $"(match {scrutinee} {string.Join(" ", armParts)})";
    }

    // Constructs a value of a user-declared generic union (all type params
    // instantiated at Int) and destructures it via an exhaustive match down to Int.
    // For Cons-shaped (recursive) unions, the scrutinee may be a multi-level
    // nested ctor and the corresponding arm uses a nested ctor pattern.
    public string GenUserUnionMatch(Scope scope, int depth)
    {
        var u = _ctx.UserUnions[_ctx.Rng.Next(_ctx.UserUnions.Count)];

        // Pick a ctor that pins the union's type-param. Nullary ctors don't
        // carry enough info to instantiate ^a, so prefer ctors with at least
        // one non-recursive type-param slot.
        var withTypeParamFields = u.Ctors.Where(c => HasNonRecursiveField(c)).ToList();
        var scrutCtor =
            withTypeParamFields.Count > 0
                ? withTypeParamFields[_ctx.Rng.Next(withTypeParamFields.Count)]
                : u.Ctors[_ctx.Rng.Next(u.Ctors.Count)];

        // Build the scrutinee. For self-recursive slots we recursively build a
        // smaller union value; the depth budget caps recursion so emit doesn't
        // explode on Cons-shaped unions.
        var scrutExpr = BuildUnionValue(u, scrutCtor, scope, depth);

        var arms = new List<string>();
        var anyCatchall = false;
        foreach (var c in u.Ctors)
        {
            var (pattern, armScope, needsCatchall) = GenCtorArmPattern(u, c, scope, depth);
            if (needsCatchall)
                anyCatchall = true;
            var body = _exprs.GenInt(armScope, depth - 1);
            arms.Add($"[{pattern} {body}]");
        }

        if (anyCatchall)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }

        return $"(match {scrutExpr} {string.Join(" ", arms)})";
    }

    private static bool HasNonRecursiveField(UserUnionCtor c)
    {
        if (c.FieldTypeParams.Count == 0)
            return false;
        for (var i = 0; i < c.FieldTypeParams.Count; i++)
            if (!IsRecursiveSlot(c, i))
                return true;
        return false;
    }

    private static bool IsRecursiveSlot(UserUnionCtor c, int i)
    {
        return c.IsFieldSelfRecursive is { } flags && i < flags.Count && flags[i];
    }

    // Recursively constructs a value of `union` at ctor `ctor`. Self-recursive
    // field slots get a smaller union value (or the union's nullary ctor when
    // depth runs out). Type-param fields receive Int sub-expressions.
    private string BuildUnionValue(UserUnionDecl union, UserUnionCtor ctor, Scope scope, int depth)
    {
        if (ctor.FieldTypeParams.Count == 0)
            return ctor.Name;

        var args = new List<string>();
        for (var i = 0; i < ctor.FieldTypeParams.Count; i++)
            if (IsRecursiveSlot(ctor, i))
                args.Add(BuildRecursiveSlot(union, scope, depth - 1));
            else
                args.Add(_exprs.GenInt(scope, depth - 1));

        return $"({ctor.Name} {string.Join(" ", args)})";
    }

    // Builds a value at a self-recursive field slot. Picks the nullary ctor
    // when depth is exhausted; otherwise picks any ctor (with ~30% chance of
    // recursing again to grow the chain).
    private string BuildRecursiveSlot(UserUnionDecl union, Scope scope, int depth)
    {
        var nullary = union.Ctors.FirstOrDefault(c => c.FieldTypeParams.Count == 0);
        if (depth <= 0)
            // Should always exist for the Cons-shape (Nil partner), but fall
            // back to any ctor if not.
            if (nullary is not null)
                return nullary.Name;

        // 70% nullary, 30% recurse — keeps generated programs small.
        if (nullary is not null && _ctx.Rng.NextDouble() < 0.7)
            return nullary.Name;

        var ctor = union.Ctors[_ctx.Rng.Next(union.Ctors.Count)];
        return BuildUnionValue(union, ctor, scope, depth);
    }

    // Generates a pattern for a single ctor arm. Per non-recursive field: 60%
    // fresh binder, 18% wildcard, 14% literal; recursive slots get either a
    // nested ctor pattern, the nullary ctor, a wildcard, or a binder (typed at
    // Int — caveat below). HasCatchall is true when any literal/nested-ctor
    // slot makes structural exhaustiveness insufficient, signalling the caller
    // to append a terminal `[_ fallback]` arm.
    private (string Pattern, Scope Scope, bool HasCatchall) GenCtorArmPattern(
        UserUnionDecl union,
        UserUnionCtor c,
        Scope scope,
        int depth
    )
    {
        if (c.FieldTypeParams.Count == 0)
            return (c.Name, scope, false);

        var parts = new List<string>();
        var armScope = scope;
        var needsCatchall = false;
        for (var i = 0; i < c.FieldTypeParams.Count; i++)
        {
            if (IsRecursiveSlot(c, i))
            {
                var (p, s, cc) = GenRecursiveSlotPattern(union, armScope, depth);
                parts.Add(p);
                armScope = s;
                if (cc)
                    needsCatchall = true;
                continue;
            }

            var roll = _ctx.Rng.NextDouble();
            if (roll < 0.60)
            {
                var b = _ctx.Fresh();
                armScope = armScope.Extend(b, ExprType.Int);
                parts.Add(b);
            }
            else if (roll < 0.78)
            {
                parts.Add("_");
            }
            else
            {
                var lit = _ctx.Rng.Next(-2, 5);
                parts.Add(lit.ToString(CultureInfo.InvariantCulture));
                needsCatchall = true;
            }
        }

        return ($"({c.Name} {string.Join(" ", parts)})", armScope, needsCatchall);
    }

    // Picks a pattern shape for a self-recursive field slot. Wildcard is the
    // safest default; nested ctor patterns only emit when depth permits.
    // No binder is produced for recursive slots — the binder's type would be
    // the union itself rather than Int, and the rest of ExprGenerator only
    // produces expressions for Int/Bool/Float/String, so the bound variable
    // would be unusable in any sub-expression.
    private (string Pattern, Scope Scope, bool NeedsCatchall) GenRecursiveSlotPattern(
        UserUnionDecl union,
        Scope scope,
        int depth
    )
    {
        if (depth <= 0)
            return ("_", scope, false);

        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.55)
            return ("_", scope, false);

        var nullary = union.Ctors.FirstOrDefault(c => c.FieldTypeParams.Count == 0);
        if (roll < 0.80 && nullary is not null)
            // A bare nullary ctor pattern matches only the empty tail, so the
            // arm is no longer exhaustive on its own.
            return (nullary.Name, scope, true);

        // Nested ctor pattern. Pick any ctor (often the same recursive one);
        // nested fields shrink with depth so the generation terminates.
        var inner = union.Ctors[_ctx.Rng.Next(union.Ctors.Count)];
        var (innerPat, innerScope, _) = GenCtorArmPattern(union, inner, scope, depth - 1);
        // Nested ctor patterns are not exhaustive against the union, so the
        // outer match needs a catchall.
        return (innerPat, innerScope, true);
    }
}

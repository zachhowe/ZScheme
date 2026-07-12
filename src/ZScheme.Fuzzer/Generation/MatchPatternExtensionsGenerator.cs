using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Pattern-shape extensions that MatchExprGenerator delegates to for the
// not-already-covered match cases. The existing MatchExprGenerator emits:
//   * primitive matches (bool/int/float/string)
//   * homogeneous Int tuple matches
//   * generic-union matches with nested ctor patterns on recursive shapes
//
// What's added here:
//   * Heterogeneous tuple matches (mixed Int/Bool/Float elements with
//     correspondingly-typed binders / wildcards / literals).
//   * Record destructuring matches: `(match (Rec a b) [(Rec x y) ...])` —
//     records are exhaustive on the sole ctor, so a single non-catchall arm
//     suffices unless a literal slot forces a fallback.
//   * Tuple-of-record matches: `(values (Rec a b) c)` paired with
//     `(values (Rec x y) z)` to exercise nested destructuring across the
//     tuple/ctor boundary.
//
// NOT added here, despite mention in some plan iterations:
//   * Guard clauses (`:when`) — not parsed by AstBuilder
//   * Arrow form (`=>`) — not parsed by AstBuilder
//   * Or-patterns — not represented in Pattern.cs
// Generating any of those would produce programs the compiler rejects.
public sealed class MatchPatternExtensionsGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public MatchPatternExtensionsGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Emits a `(match (values e1 e2 ...) [(values pat1 pat2 ...) body] ...)`
    // where each element is independently typed (Int/Bool/Float). The pattern
    // for each slot is type-appropriate: Int slots can have Int literal/binder/
    // wildcard, Bool slots can have #t/#f/binder/wildcard, Float slots can
    // have float literal/binder/wildcard.
    public string GenHeterogeneousTupleMatch(ExprType resultType, Scope scope, int depth)
    {
        var arity = _ctx.PickTupleArity();
        var elemTypes = new ExprType[arity];
        var elems = new string[arity];
        for (var i = 0; i < arity; i++)
        {
            // 60% Int, 20% Bool, 20% Float — keep Int dominant so reductions
            // stay simple.
            var roll = _ctx.Rng.NextDouble();
            elemTypes[i] =
                roll < 0.60 ? ExprType.Int
                : roll < 0.80 ? ExprType.Bool
                : ExprType.Float;
            elems[i] = GenElement(elemTypes[i], scope, depth - 1);
        }

        var patternParts = new List<string>(arity);
        var armScope = scope;
        var hasBinder = false;
        var hasLiteral = false;
        for (var i = 0; i < arity; i++)
        {
            var forceBinder = !hasBinder && i == arity - 1;
            var roll = forceBinder ? 0.0 : _ctx.Rng.NextDouble();
            if (roll < 0.55)
            {
                var b = _ctx.Fresh();
                patternParts.Add(b);
                armScope = armScope.Extend(b, elemTypes[i]);
                hasBinder = true;
            }
            else if (roll < 0.80)
            {
                patternParts.Add("_");
            }
            else
            {
                patternParts.Add(LiteralFor(elemTypes[i]));
                hasLiteral = true;
            }
        }

        var body = _exprs.GenExpr(resultType, armScope, depth - 1);
        var scrutinee = $"(values {string.Join(" ", elems)})";
        var mainArm = $"[(values {string.Join(" ", patternParts)}) {body}]";
        if (hasLiteral)
        {
            if (_ctx.EnableMatchFallthrough && _ctx.Rng.NextDouble() < 0.4)
                return _exprs.WrapMatchFallthrough(
                    $"(match {scrutinee} {mainArm})",
                    resultType,
                    scope,
                    depth
                );
            var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }

        return $"(match {scrutinee} {mainArm})";
    }

    public bool HasRecord()
    {
        return _ctx.UserRecords.Count > 0;
    }

    // (match (RecName f1 f2 ...) [(RecName p1 p2 ...) body])
    // Each field is instantiated at Int (matching GenUserRecordAccess); the
    // pattern slot is wildcard / binder / literal with the same weights as the
    // tuple variants. A single arm is exhaustive when no slot is a literal.
    public string GenRecordMatch(ExprType resultType, Scope scope, int depth)
    {
        var rec = _ctx.UserRecords[_ctx.Rng.Next(_ctx.UserRecords.Count)];
        var ctorArgs = new List<string>(rec.Fields.Count);
        for (var i = 0; i < rec.Fields.Count; i++)
            ctorArgs.Add(_exprs.GenInt(scope, depth - 1));
        var scrutinee = $"({rec.Name} {string.Join(" ", ctorArgs)})";

        var (pattern, armScope, hasLiteral) = GenRecordCtorPattern(rec, scope);
        var body = _exprs.GenExpr(resultType, armScope, depth - 1);
        var arms = new List<string> { $"[{pattern} {body}]" };
        if (hasLiteral)
        {
            var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }

        return $"(match {scrutinee} {string.Join(" ", arms)})";
    }

    // (match (values (RecName f1 f2) e) [(values (RecName x y) z) body] [_ fallback])
    // Nested destructuring across the values-tuple / record-ctor boundary.
    public string GenTupleOfRecordMatch(ExprType resultType, Scope scope, int depth)
    {
        var rec = _ctx.UserRecords[_ctx.Rng.Next(_ctx.UserRecords.Count)];

        var ctorArgs = new List<string>(rec.Fields.Count);
        for (var i = 0; i < rec.Fields.Count; i++)
            ctorArgs.Add(_exprs.GenInt(scope, depth - 1));
        var trailing = _exprs.GenInt(scope, depth - 1);
        var scrutinee = $"(values ({rec.Name} {string.Join(" ", ctorArgs)}) {trailing})";

        var (recPat, armScope, hasLiteral) = GenRecordCtorPattern(rec, scope);
        // Trailing slot: prefer a fresh binder so the body has something Int to
        // reduce on; pick wildcard a fraction of the time.
        string trailingPart;
        if (_ctx.Rng.NextDouble() < 0.7)
        {
            var b = _ctx.Fresh();
            trailingPart = b;
            armScope = armScope.Extend(b, ExprType.Int);
        }
        else
        {
            trailingPart = "_";
        }

        var body = _exprs.GenExpr(resultType, armScope, depth - 1);
        var arms = new List<string> { $"[(values {recPat} {trailingPart}) {body}]" };
        // Always emit the fallback for tuple-of-record. Even with no literal in
        // the record pattern the structural match is exhaustive, but adding the
        // catchall exercises the wildcard codegen path consistently.
        var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
        arms.Add($"[_ {fallback}]");
        return $"(match {scrutinee} {string.Join(" ", arms)})";
    }

    private (string Pattern, Scope Scope, bool HasLiteral) GenRecordCtorPattern(
        UserRecordDecl rec,
        Scope scope
    )
    {
        var parts = new List<string>(rec.Fields.Count);
        var armScope = scope;
        var hasBinder = false;
        var hasLiteral = false;
        for (var i = 0; i < rec.Fields.Count; i++)
        {
            var forceBinder = !hasBinder && i == rec.Fields.Count - 1;
            var roll = forceBinder ? 0.0 : _ctx.Rng.NextDouble();
            if (roll < 0.60)
            {
                var b = _ctx.Fresh();
                parts.Add(b);
                armScope = armScope.Extend(b, ExprType.Int);
                hasBinder = true;
            }
            else if (roll < 0.82)
            {
                parts.Add("_");
            }
            else
            {
                parts.Add(_ctx.Rng.Next(-2, 5).ToString(CultureInfo.InvariantCulture));
                hasLiteral = true;
            }
        }

        return ($"({rec.Name} {string.Join(" ", parts)})", armScope, hasLiteral);
    }

    private string GenElement(ExprType t, Scope scope, int depth)
    {
        return t switch
        {
            ExprType.Int => _exprs.GenInt(scope, depth),
            ExprType.Bool => _exprs.GenBool(scope, depth),
            ExprType.Float => _exprs.GenFloat(scope, depth),
            _ => throw new InvalidOperationException($"No element generator for {t}"),
        };
    }

    private string LiteralFor(ExprType t)
    {
        return t switch
        {
            ExprType.Int => _ctx.Rng.Next(-2, 5).ToString(CultureInfo.InvariantCulture),
            ExprType.Bool => _ctx.Rng.NextDouble() < 0.5 ? "#t" : "#f",
            ExprType.Float => new[] { "0.0", "-0.0", "1.0", "-1.0", "2.5" }[_ctx.Rng.Next(5)],
            _ => throw new InvalidOperationException($"No literal generator for {t}"),
        };
    }
}

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
        var arity = 2 + _ctx.Rng.Next(2); // 2 or 3
        var elemTypes = new ExprType[arity];
        var elems = new string[arity];
        for (var i = 0; i < arity; i++)
        {
            // 60% Int, 20% Bool, 20% Float — keep Int dominant so reductions
            // stay simple.
            var roll = _ctx.Rng.NextDouble();
            elemTypes[i] = roll < 0.60 ? ExprType.Int
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
            var fallback = _exprs.GenExpr(resultType, scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }
        return $"(match {scrutinee} {mainArm})";
    }

    private string GenElement(ExprType t, Scope scope, int depth) => t switch
    {
        ExprType.Int => _exprs.GenInt(scope, depth),
        ExprType.Bool => _exprs.GenBool(scope, depth),
        ExprType.Float => _exprs.GenFloat(scope, depth),
        _ => throw new InvalidOperationException($"No element generator for {t}"),
    };

    private string LiteralFor(ExprType t) => t switch
    {
        ExprType.Int => _ctx.Rng.Next(-2, 5).ToString(CultureInfo.InvariantCulture),
        ExprType.Bool => _ctx.Rng.NextDouble() < 0.5 ? "#t" : "#f",
        ExprType.Float => new[] { "0.0", "-0.0", "1.0", "-1.0", "2.5" }[_ctx.Rng.Next(5)],
        _ => throw new InvalidOperationException($"No literal generator for {t}"),
    };
}

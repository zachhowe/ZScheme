namespace ZScheme.Fuzzer.Generation;

// Emits the receiver form of `set!` against a `#:mutable` record/struct field:
//
//   (let ([x_N (Rec v1 v2)]) (begin (set! x_N f v) (Rec/f x_N)))
//
// The receiver must be a bare variable (the inferer's restriction — a computed
// value-type receiver would mutate a throwaway copy), so the mutation is
// self-contained in its own `let`: the binder is a `Fresh()` name, which no
// generated subexpression can mention, and the read-back tail makes the write
// observable — the result is the *new* value, catching read-after-write codegen
// bugs that the clone-semantics `with` form (WithExprGenerator) never reaches.
//
// For struct records the local is a value slot, so this also exercises the
// lvalue materialization both backends need (`ldloca`/parameter address vs. the
// C# `slot.Field = value`); for records it is a plain reference write. The
// syntax is identical for record and struct.
public sealed class SetFieldExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public SetFieldExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // True when any user record/struct has a #:mutable field to mutate.
    public static bool HasEligible(GeneratorContext ctx)
        => ctx.UserRecords.Any(r => r.Fields.Any(f => f.IsMutable));

    public string SetFieldToInt(Scope scope, int depth)
    {
        var eligible = _ctx.UserRecords.Where(r => r.Fields.Any(f => f.IsMutable)).ToList();
        var r = eligible[_ctx.Rng.Next(eligible.Count)];
        var field = r.Fields.First(f => f.IsMutable);

        // Binder first: a Fresh() name is unique to this program, so none of the
        // subexpressions (all generated in the outer scope) can mention it — the
        // let's shadowing is total and the template's bare name resolves cleanly.
        var binder = _ctx.Fresh();

        var initialArgs = new List<string>();
        foreach (var _ in r.Fields)
            initialArgs.Add(_exprs.GenInt(scope, depth - 1));
        var newValue = _exprs.GenInt(scope, depth - 1);

        return $"(let ([{binder} ({r.Name} {string.Join(" ", initialArgs)})])"
            + $"  (begin (set! {binder} {field.Name} {newValue}) ({r.Name}/{field.Name} {binder})))";
    }
}

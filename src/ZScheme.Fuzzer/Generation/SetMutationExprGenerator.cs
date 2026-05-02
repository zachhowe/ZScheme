namespace ZScheme.Fuzzer.Generation;

// Generates Int-typed method bodies that exercise `(set! field expr)` against
// an enclosing class's mutable Int fields. The compiler restricts `set!` to
// method bodies (TypeInferer's _currentClassFieldDecls gate), so this
// generator's output is only meaningful when invoked by ClassExprGenerator
// while building a method body.
//
// Two body shapes are produced:
//   * `(begin (set! f val) f)`        — write-then-read, returns the new value
//   * `(begin (set! f val) (+ f n))`  — write-then-read-and-combine
// Both exercise SetField in IR plus the post-mutation read against the mutated
// field, which catches read-after-write codegen bugs that pure-method bodies
// (no mutation) never reach.
public sealed class SetMutationExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public SetMutationExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Builds a method body of the form `(begin (set! field <int>) <int>)` that
    // mutates one of the class's mutable fields and then returns an Int. The
    // body is wrapped in `(begin ...)` because `set!` returns Unit, so a
    // bare `(set! ...)` would type-check as Unit, not Int.
    //
    // `fieldScope` should be the scope inside which field names are bare Int
    // identifiers, mirroring what ClassExprGenerator passes to BuildMethodText.
    // `mutableFields` must be non-empty.
    public string BuildMutationMethodBody(
        IReadOnlyList<UserClassField> mutableFields,
        Scope fieldScope,
        int depth)
    {
        if (mutableFields.Count == 0)
            throw new InvalidOperationException(
                "BuildMutationMethodBody called with no mutable fields");

        var field = mutableFields[_ctx.Rng.Next(mutableFields.Count)];
        var newValue = _exprs.GenInt(fieldScope, depth - 1);

        // Tail expression: 50/50 between bare field read (most direct test of
        // write-then-read) and a small expression that combines the read with
        // a fresh Int — keeps the value flowing into the rest of the program.
        var tail = _ctx.Rng.NextDouble() < 0.5
            ? field.Name
            : $"(+ {field.Name} {_exprs.GenInt(fieldScope, depth - 1)})";

        return $"(begin (set! {field.Name} {newValue}) {tail})";
    }
}

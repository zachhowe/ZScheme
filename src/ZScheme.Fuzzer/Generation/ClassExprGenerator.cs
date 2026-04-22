using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits a `(class ...)` with mutable Int fields, a constructor that uses `set!`,
// and one or two methods (declared but not called from the compute body — see the
// note below). The class value is produced via `(new ClsName args)` inside a
// `begin` whose final expression is an Int, so the class instance is constructed
// (exercising ctor + set! codegen) but its runtime state is then discarded.
//
// Why construct-and-discard instead of calling an instance method from compute:
// invoking user-class instance methods requires `import-clr ... :instance` on the
// user class, and the IL backend's handling of that path currently fails with a
// stack-imbalance error during AsmResolver image build (reproducible with a
// minimal `(import-clr [m ZSchemeFuzzed.Mod.Cls.M :instance ...]) (m (new Cls))`).
// A deterministic IL failure on every emission would swamp the fuzzer with
// identical reports, so the generator deliberately avoids triggering it. Once the
// underlying compiler bug is addressed, this generator can be extended to call
// class methods and observe mutation end-to-end.
public sealed class ClassExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ClassExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Emits one class declaration at the given index. Shape is fixed at one
    // mutable Int field + a ctor that `set!`s it, plus a second mutable field
    // with a ctor that uses both in a small expression. A Sum() method is also
    // emitted so the class has at least one method body (exercising the
    // IR→IL and IR→C# method-body paths even though it's never called).
    public UserClassDecl GenerateClass(int index)
    {
        var name = $"FCls_{index}";
        var twoFields = _ctx.Rng.NextDouble() < 0.5;

        if (twoFields)
        {
            // Two mutable fields; ctor sets both.
            var def =
                $"(class {name}\n" +
                $"  [f0 : Int #:mutable]\n" +
                $"  [f1 : Int #:mutable]\n" +
                $"  (constructor [a : Int] [b : Int]\n" +
                $"    (set! f0 a)\n" +
                $"    (set! f1 (+ a b)))\n" +
                $"  (define (Sum) : Int (+ f0 f1)))";
            return new UserClassDecl(
                name,
                [new UserClassField("f0", true), new UserClassField("f1", true)],
                [ExprType.Int, ExprType.Int],
                def);
        }
        else
        {
            var def =
                $"(class {name}\n" +
                $"  [f0 : Int #:mutable]\n" +
                $"  (constructor [a : Int]\n" +
                $"    (set! f0 (* a 2)))\n" +
                $"  (define (Read) : Int f0))";
            return new UserClassDecl(
                name,
                [new UserClassField("f0", true)],
                [ExprType.Int],
                def);
        }
    }

    // Construct-and-discard reducer: `(begin (new ClsName <int> ...) <int>)`.
    // The class instance is discarded and the final Int is returned.
    public string ConstructDiscardToInt(Scope scope, int depth)
    {
        if (_ctx.UserClasses.Count == 0)
            throw new InvalidOperationException("ConstructDiscardToInt called with no user classes");

        var cls = _ctx.UserClasses[_ctx.Rng.Next(_ctx.UserClasses.Count)];
        var ctorArgs = new List<string>();
        foreach (var p in cls.ConstructorParamTypes)
        {
            if (p != ExprType.Int)
                throw new InvalidOperationException($"Unexpected class ctor param type: {p}");
            ctorArgs.Add(_exprs.GenInt(scope, depth - 1));
        }

        var construct = $"(new {cls.Name} {string.Join(" ", ctorArgs)})";
        var tail = _exprs.GenInt(scope, depth - 1);
        return $"(begin {construct} {tail})";
    }
}

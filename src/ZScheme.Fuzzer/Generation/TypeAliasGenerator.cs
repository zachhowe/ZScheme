namespace ZScheme.Fuzzer.Generation;

// Emits a `(define-type-alias ...)` declaration plus one uncalled top-level
// helper `define` that USES the alias in a parameter annotation. The helper's
// body is a constant Int, and it is deliberately NOT registered in
// _ctx.UserFuncs, so GenCall never calls it — this keeps the alias-resolution
// path exercised at codegen time on both backends without forcing construction
// of the (often generic-collection) aliased value.
//
// Alias targets are limited to what AstBuilder.BuildTypeAliasDecl accepts: an
// open-generic / arity-0 CLR type, or `:array` (exactly one type param). Primitive
// and tuple aliasing are not supported by the language, so they are not attempted.
public sealed class TypeAliasGenerator
{
    private readonly GeneratorContext _ctx;

    public TypeAliasGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    // Returns the alias declaration + helper define as a single text block.
    public string EmitAliasAndUser()
    {
        // Distinctive names — one alias block per program, so fixed names are
        // collision-free against user types (FRec_/FUn_) and functions (fN/gN/xN).
        const string alias = "FuzzAlias";
        const string fn = "fuzzaliasfn";

        return _ctx.Rng.Next(4) switch
        {
            // 1-param generic over a real BCL open generic.
            0 =>
                $"(define-type-alias ({alias} ^a) System.Collections.Generic.List)\n"
                + $"(define ({fn} [xs : ({alias} Int)]) : Int\n  0)",
            // 2-param generic.
            1 =>
                $"(define-type-alias ({alias} ^k ^v) System.Collections.Generic.Dictionary)\n"
                + $"(define ({fn} [m : ({alias} Int Int)]) : Int\n  0)",
            // Array alias — exactly one type param.
            2 =>
                $"(define-type-alias ({alias} ^a) :array)\n"
                + $"(define ({fn} [xs : ({alias} Int)]) : Int\n  0)",
            // Arity-0 alias over a concrete CLR type.
            _ =>
                $"(define-type-alias {alias} System.DateTime)\n"
                + $"(define ({fn} [d : {alias}]) : Int\n  0)",
        };
    }
}

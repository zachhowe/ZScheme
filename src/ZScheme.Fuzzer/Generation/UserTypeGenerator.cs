namespace ZScheme.Fuzzer.Generation;

public sealed class UserTypeGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly WhereConstraintGenerator _where;

    public UserTypeGenerator(GeneratorContext ctx, WhereConstraintGenerator where)
    {
        _ctx = ctx;
        _where = where;
    }

    // Emits a generic union like:
    //   (union (FUn_0 ^a) (Wrap_0 [value : ^a]) (Empty_0))
    //
    // Shape is chosen from a small set of 1-param or 2-param variants so the
    // match compiler, union codegen, and generic instantiation all get exercised.
    public UserUnionDecl GenerateUnion(int index)
    {
        var name = $"FUn_{index}";
        var shape = _ctx.Rng.Next(4);

        if (shape == 0)
        {
            // Option-shaped: 1 type param, Wrap[^a] | Empty
            var ctorWrap = $"Wrap_{index}";
            var ctorEmpty = $"Empty_{index}";
            var where = _where.MaybeEmit(["^a"], 0.04);
            var def = $"(union ({name} ^a){where} ({ctorWrap} [value : ^a]) ({ctorEmpty}))";
            return new UserUnionDecl(
                name,
                ["^a"],
                [new UserUnionCtor(ctorWrap, ["^a"]), new UserUnionCtor(ctorEmpty, [])],
                def
            );
        }

        if (shape == 1)
        {
            // Either-shaped: 2 type params, Left[^a] | Right[^b]
            var ctorL = $"Left_{index}";
            var ctorR = $"Right_{index}";
            var where = _where.MaybeEmit(["^a", "^b"], 0.04);
            var def = $"(union ({name} ^a ^b){where} ({ctorL} [lv : ^a]) ({ctorR} [rv : ^b]))";
            return new UserUnionDecl(
                name,
                ["^a", "^b"],
                [new UserUnionCtor(ctorL, ["^a"]), new UserUnionCtor(ctorR, ["^b"])],
                def
            );
        }

        if (shape == 2)
        {
            // Pair-shaped: 1 type param, two-field ctor plus nullary
            var ctorBoth = $"Both_{index}";
            var ctorNone = $"Neither_{index}";
            var where = _where.MaybeEmit(["^a"], 0.04);
            var def = $"(union ({name} ^a){where} ({ctorBoth} [a : ^a] [b : ^a]) ({ctorNone}))";
            return new UserUnionDecl(
                name,
                ["^a"],
                [new UserUnionCtor(ctorBoth, ["^a", "^a"]), new UserUnionCtor(ctorNone, [])],
                def
            );
        }
        else
        {
            // Cons-shaped: 1 type param, recursive linked list. The Cons ctor's
            // tail field has type `(FUn_n ^a)` — i.e. references the union being
            // defined — so match arms over this union can emit nested ctor
            // patterns like `(Cons_n h (Cons_n h2 _))`, which exercise both
            // backends' nested constructor-pattern paths (CSharpEmitter.EmitPattern
            // and IlEmitter.EmitConstructorPatternTest).
            var ctorCons = $"Cons_{index}";
            var ctorNil = $"Nil_{index}";
            // No :where on the recursive form — the recursive `(FUn_n ^a)` field
            // type may interact unpredictably with constraints; keep this shape
            // unconstrained as the safer fuzz path.
            var def =
                $"(union ({name} ^a) ({ctorCons} [head : ^a] [tail : ({name} ^a)]) ({ctorNil}))";
            return new UserUnionDecl(
                name,
                ["^a"],
                [
                    // tail's "type-param slot" is recorded as ^a (the head) for
                    // shape compatibility; IsFieldSelfRecursive flags the actual
                    // recursive shape.
                    new UserUnionCtor(ctorCons, ["^a", "^a"], [false, true]),
                    new UserUnionCtor(ctorNil, []),
                ],
                def
            );
        }
    }

    // Emits a generic record or generic struct like:
    //   (record (FRec_0 ^a ^b) [first : ^a] [second : ^b])
    //   (struct (FRec_0 ^a ^b) [first : ^a] [second : ^b])
    //
    // `struct` shares the AST path (IsValueType=true). The IL emitter has a known
    // bug producing invalid IL for generic structs (ilverify errors around
    // accessors expecting `readonly address of FRec_0` but finding
    // `address of FRec_0<T0,T1>`); the fuzzer surfaces this through DiffExec /
    // IlVerify failure artifacts, so we emit `struct` at a moderate rate to keep
    // the bug observable without flooding the report stream.
    public UserRecordDecl GenerateRecord(int index)
    {
        var name = $"FRec_{index}";
        var twoParams = _ctx.Rng.NextDouble() < 0.5;
        var isStruct = _ctx.Rng.NextDouble() < 0.25;
        var keyword = isStruct ? "define-struct" : "define-record";
        // One mutable field on ~half the generic *records* so SetFieldExprGenerator
        // reaches the generic-setter path (the IL emitter resolves it against the
        // closed generic instance). Generic structs stay all-immutable: they already
        // surface the known invalid-IL bug above, and a plain setter on top would
        // widen that bug's blast radius instead of probing this feature.
        var mutableAt = !isStruct && _ctx.Rng.NextDouble() < 0.5 ? _ctx.Rng.Next(2) : -1;

        if (twoParams)
        {
            var f1 = "first";
            var f2 = "second";
            var where = _where.MaybeEmit(["^a", "^b"], 0.04);
            var def = $"({keyword} ({name} ^a ^b){where} [{f1} : ^a{MutableTag(mutableAt == 0)}] [{f2} : ^b{MutableTag(mutableAt == 1)}])";
            return new UserRecordDecl(
                name,
                ["^a", "^b"],
                [
                    new UserRecordField(f1, "^a", mutableAt == 0),
                    new UserRecordField(f2, "^b", mutableAt == 1),
                ],
                def,
                isStruct
            );
        }
        else
        {
            var f1 = "x";
            var f2 = "y";
            var where = _where.MaybeEmit(["^a"], 0.04);
            var def = $"({keyword} ({name} ^a){where} [{f1} : ^a{MutableTag(mutableAt == 0)}] [{f2} : ^a{MutableTag(mutableAt == 1)}])";
            return new UserRecordDecl(
                name,
                ["^a"],
                [new UserRecordField(f1, "^a", mutableAt == 0), new UserRecordField(f2, "^a", mutableAt == 1)],
                def,
                isStruct
            );
        }
    }

    private static string MutableTag(bool isMutable) => isMutable ? " #:mutable" : "";
}

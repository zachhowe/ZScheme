using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits `(class ...)` declarations and the construct-and-discard reducer used in
// expression position. Three shapes are produced:
//
//   * Standalone class: 1-3 mutable Int fields, implicit or explicit constructor,
//     1-3 methods whose bodies are non-trivial Int expressions generated via the
//     shared ExprGenerator (so they exercise if/match/with-handlers/etc.).
//   * Open + derived pair: a base class marked #:open with an overridable method,
//     and a derived class that overrides it via `(super/MName ...)` — the only
//     super-call form the compiler supports.
//   * Interface-implementing class: the class additionally implements one of the
//     generated interfaces, supplying matching method bodies.
//
// All generated classes are constructed in expression position via
// `(begin (new Cls args...) <int>)` and the instance is then discarded. We do
// NOT call user-class instance methods externally because that path requires
// `import-clr ... :instance` on the user class, and the IL backend currently
// fails with a stack-imbalance error during AsmResolver image build for that
// case (reproducible with the minimal program in the original generator note).
// A deterministic IL failure on every emission would swamp the fuzzer with
// identical reports, so the generator deliberately avoids triggering it. Once
// the underlying compiler bug is addressed, the construct-and-discard reducer
// can be extended to call methods and observe mutation end-to-end.
public sealed class ClassExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ClassExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Top-level entry. Generates a standalone class at the given index. Caller
    // is responsible for adding the result to _ctx.UserClasses and emitting the
    // Definition into the program output.
    //
    // `isOpen` lets the caller request an inheritable base class when it intends
    // to follow up with GenerateDerivedClass. `interfaceToImplement` is optional;
    // when provided, the class implements every method of that interface.
    public UserClassDecl GenerateClass(
        int index,
        bool isOpen,
        UserInterfaceDecl? interfaceToImplement)
    {
        var name = $"FCls_{index}";
        var numFields = 1 + _ctx.Rng.Next(3); // 1..3
        var fields = new List<UserClassField>(numFields);
        var fieldDecls = new List<string>(numFields);
        for (var i = 0; i < numFields; i++)
        {
            // Always mutable so explicit ctors can `set!` them, and so future
            // generators can mutate fields without re-shaping the class.
            var fname = $"f{i}";
            fields.Add(new UserClassField(fname, IsMutable: true));
            fieldDecls.Add($"  [{fname} : Int #:mutable]");
        }

        // When the caller plans to derive from this class, force an implicit
        // constructor: a derived class without an explicit `(constructor (super ...))`
        // form cannot be instantiated when the base has an explicit constructor
        // (the derived `(new ...)` site triggers a compiler type-mismatch). The
        // implicit-ctor path concatenates base + own field args cleanly.
        var explicitCtor = !isOpen && _ctx.Rng.NextDouble() < 0.5;
        List<ExprType> ctorParamTypes;
        string? explicitCtorText = null;
        if (explicitCtor)
        {
            // Explicit ctor: 1-2 Int params; each field is set! to a small
            // arithmetic expression of those params (or a constant).
            var ctorArity = 1 + _ctx.Rng.Next(2); // 1..2
            ctorParamTypes = Enumerable.Repeat(ExprType.Int, ctorArity).ToList();
            var ctorParams = string.Join(" ",
                Enumerable.Range(0, ctorArity).Select(i => $"[a{i} : Int]"));
            var sets = new List<string>(numFields);
            for (var i = 0; i < numFields; i++)
            {
                var rhs = ExplicitCtorFieldRhs(ctorArity, i);
                sets.Add($"    (set! {fields[i].Name} {rhs})");
            }
            explicitCtorText = $"  (constructor {ctorParams}\n{string.Join("\n", sets)})";
        }
        else
        {
            // Implicit ctor: one Int param per field, in declaration order.
            ctorParamTypes = Enumerable.Repeat(ExprType.Int, numFields).ToList();
        }

        // Build a scope where each field is in scope as Int (methods can read
        // fields by bare name, just like the inheritance.zs example).
        var fieldScope = new Scope();
        foreach (var f in fields)
            fieldScope = fieldScope.Extend(f.Name, ExprType.Int);

        // Methods: start with the interface methods (if any), then add 1-2 extra
        // own methods. Interface method names/arities/return types are fixed by
        // the interface; own methods get fresh names.
        var methods = new List<UserClassMethod>();
        var methodTexts = new List<string>();
        var usedNames = new HashSet<string>();

        if (interfaceToImplement is not null)
        {
            foreach (var im in interfaceToImplement.Methods)
            {
                methods.Add(new UserClassMethod(im.Name, im.ParamTypes, im.RetType));
                methodTexts.Add(BuildMethodText(im.Name, im.ParamTypes, im.RetType, fieldScope));
                usedNames.Add(im.Name);
            }
        }

        var numOwn = 1 + _ctx.Rng.Next(2); // 1..2
        for (var i = 0; i < numOwn; i++)
        {
            var mName = $"M{index}_{i}";
            if (!usedNames.Add(mName)) continue;
            var arity = _ctx.Rng.Next(3); // 0..2
            var pTypes = Enumerable.Repeat(ExprType.Int, arity).ToList();
            methods.Add(new UserClassMethod(mName, pTypes, ExprType.Int));
            methodTexts.Add(BuildMethodText(mName, pTypes, ExprType.Int, fieldScope));
        }

        var implementsClause = interfaceToImplement is null
            ? string.Empty
            : $" : {interfaceToImplement.Name}";
        var openMarker = isOpen ? " #:open" : string.Empty;
        var header = $"(class{openMarker} {name}{implementsClause}";
        var bodyParts = new List<string>(fieldDecls);
        if (explicitCtorText is not null) bodyParts.Add(explicitCtorText);
        bodyParts.AddRange(methodTexts);
        var def = $"{header}\n{string.Join("\n", bodyParts)})";

        return new UserClassDecl(
            name,
            fields,
            ctorParamTypes,
            methods,
            isOpen,
            BaseName: null,
            ImplementedInterfaces: interfaceToImplement is null
                ? Array.Empty<string>()
                : new[] { interfaceToImplement.Name },
            def);
    }

    // Generates a derived class that inherits from `baseClass` (which must be
    // marked #:open). The derived class:
    //   * adds 0-1 own mutable Int field(s),
    //   * uses an implicit constructor whose params are (base ctor args, own field args),
    //   * overrides one of the base methods, body = `(super/MName base-args...) +/- own-data`.
    //
    // The override is the OO-coverage win: it exercises the override codegen path
    // and the only `super/Method` form the compiler supports.
    public UserClassDecl GenerateDerivedClass(int index, UserClassDecl baseClass)
    {
        if (!baseClass.IsOpen)
            throw new InvalidOperationException($"Base class {baseClass.Name} is not #:open");
        if (baseClass.Methods.Count == 0)
            throw new InvalidOperationException($"Base class {baseClass.Name} has no methods to override");

        var name = $"FCls_{index}";
        var numOwn = _ctx.Rng.Next(2); // 0..1
        var ownFields = new List<UserClassField>(numOwn);
        var fieldDecls = new List<string>(numOwn);
        for (var i = 0; i < numOwn; i++)
        {
            // Distinct prefix from base to avoid name collisions with inherited
            // fields (which use `f<n>`).
            var fname = $"d{i}";
            ownFields.Add(new UserClassField(fname, IsMutable: true));
            fieldDecls.Add($"  [{fname} : Int #:mutable]");
        }

        // Combined implicit ctor: base ctor params first, then own field params.
        // Construct sites must pass matching args in this order.
        var ctorParamTypes = baseClass.ConstructorParamTypes
            .Concat(Enumerable.Repeat(ExprType.Int, numOwn))
            .ToList();

        // Pick one base method to override. Same name, same signature.
        var baseMethod = baseClass.Methods[_ctx.Rng.Next(baseClass.Methods.Count)];
        var paramSig = string.Join(" ",
            Enumerable.Range(0, baseMethod.ParamTypes.Count).Select(i => $"[p{i} : Int]"));

        // Body: `(+ (super/MName p0 p1 ...) <int>)`. Forwarding to super tests the
        // override-path super-call codegen end-to-end at compile time. We keep the
        // body small to avoid runtime divergences for currently-uncalled methods.
        var superArgs = string.Join(" ",
            Enumerable.Range(0, baseMethod.ParamTypes.Count).Select(i => $"p{i}"));
        var superCall = baseMethod.ParamTypes.Count == 0
            ? $"(super/{baseMethod.Name})"
            : $"(super/{baseMethod.Name} {superArgs})";

        // If we have an own field, fold it in so the derived state matters at codegen.
        var bodyExpr = ownFields.Count > 0
            ? $"(+ {superCall} {ownFields[0].Name})"
            : superCall;

        var paramsPart = baseMethod.ParamTypes.Count == 0 ? "" : $" {paramSig}";
        var overrideText = $"  (define ({baseMethod.Name}{paramsPart}) : Int {bodyExpr})";

        var header = $"(class {name} : {baseClass.Name}";
        var bodyParts = new List<string>(fieldDecls) { overrideText };
        var def = $"{header}\n{string.Join("\n", bodyParts)})";

        return new UserClassDecl(
            name,
            ownFields,
            ctorParamTypes,
            // Inherits all base methods plus the override (override has same name).
            // For construct/call bookkeeping we list only the override's signature.
            new[] { new UserClassMethod(baseMethod.Name, baseMethod.ParamTypes, baseMethod.RetType) },
            IsOpen: false,
            BaseName: baseClass.Name,
            ImplementedInterfaces: Array.Empty<string>(),
            def);
    }

    // Builds a method text `(define (MName [p0 : Int] ...) : Int <body>)` whose
    // body comes from the shared ExprGenerator. Field names are in `fieldScope`
    // so the body may reference them as bare identifiers.
    private string BuildMethodText(
        string mName,
        IReadOnlyList<ExprType> paramTypes,
        ExprType retType,
        Scope fieldScope)
    {
        var paramSig = string.Join(" ",
            Enumerable.Range(0, paramTypes.Count).Select(i => $"[p{i} : Int]"));

        var bodyScope = fieldScope;
        for (var i = 0; i < paramTypes.Count; i++)
            bodyScope = bodyScope.Extend($"p{i}", ExprType.Int);

        // Method body depth is bounded so generated classes don't blow up emit time
        // — methods rarely benefit from full max-depth recursion the way compute does.
        var bodyDepth = Math.Min(_ctx.MaxDepth, 4);
        var body = retType switch
        {
            ExprType.Int => _exprs.GenInt(bodyScope, bodyDepth),
            _ => throw new InvalidOperationException($"Unsupported method return type: {retType}")
        };

        var paramsPart = paramTypes.Count == 0 ? "" : $" {paramSig}";
        return $"  (define ({mName}{paramsPart}) : Int {body})";
    }

    // Construct-and-discard reducer: `(begin (new ClsName <int> ...) <int>)`.
    // The class instance is discarded and the final Int is returned. Same
    // mechanism as before; updated only to draw constructor args from the
    // (potentially longer) ConstructorParamTypes list.
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

        var construct = ctorArgs.Count == 0
            ? $"(new {cls.Name})"
            : $"(new {cls.Name} {string.Join(" ", ctorArgs)})";
        var tail = _exprs.GenInt(scope, depth - 1);
        return $"(begin {construct} {tail})";
    }

    // RHS for an explicit constructor's `(set! field rhs)` line. Picks among the
    // ctor's params and small Int constants so the generated set! lines are
    // deterministic and well-typed.
    private string ExplicitCtorFieldRhs(int ctorArity, int fieldIdx)
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.5)
        {
            // Pure param reference.
            var p = _ctx.Rng.Next(ctorArity);
            return $"a{p}";
        }
        if (roll < 0.85 && ctorArity >= 2)
        {
            // Small arithmetic on params.
            var p1 = _ctx.Rng.Next(ctorArity);
            var p2 = _ctx.Rng.Next(ctorArity);
            var op = _ctx.Rng.NextDouble() < 0.5 ? "+" : "*";
            return $"({op} a{p1} a{p2})";
        }
        // Constant.
        return _ctx.Rng.Next(-10, 11).ToString(CultureInfo.InvariantCulture);
    }
}

using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits `(class ...)` declarations plus reducers that use those classes in
// expression position. Three declaration shapes are produced:
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
// Two reducers consume those classes:
//
//   * Construct-and-discard — `(begin (new Cls args...) <int>)`. The instance is
//     discarded and the final Int is returned. Always available when classes exist.
//   * Construct-and-call — gated on GeneratorContext.EnableClassInstanceCalls.
//     Constructs the instance into a `let`, then calls one of its methods via an
//     `(import-clr [alias Namespace.Cls.Method :instance ...])` alias emitted at
//     program scope. The IL backend currently has a known stack-imbalance bug on
//     this path (reproducible with the minimal program in the original generator
//     note); the gate keeps the failure-artifact stream from being dominated by
//     identical reports while still surfacing the bug end-to-end.
public sealed class ClassExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    // Set by ProgramGenerator after construction to break the ctor cycle
    // (AsyncExprGenerator is built later but BuildMethodText needs it for the
    // async-method shape).
    private AsyncExprGenerator? _async;
    // Optional: when wired, ClassExprGenerator may emit one mutation method
    // per non-#:open class whose body exercises `(set! field ...)`. Set! is
    // only valid inside a method body, so the generator can't emit it from
    // expression-position reducers.
    private SetMutationExprGenerator? _setMutation;

    public ClassExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public void SetAsync(AsyncExprGenerator async) { _async = async; }
    public void SetSetMutation(SetMutationExprGenerator setMutation) { _setMutation = setMutation; }

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
        // Async own-methods are only generated on standalone (non-#:open) classes.
        // Excluding #:open here keeps GenerateDerivedClass's override-picking path
        // free of sync/async signature-matching concerns. We don't require async
        // user funcs to exist — when none do, GenAsyncBodyInt falls back to a
        // plain Int body, but the state-machine wrapping still gets emitted.
        var asyncEligible = !isOpen && _async is not null;
        for (var i = 0; i < numOwn; i++)
        {
            var mName = $"M{index}_{i}";
            if (!usedNames.Add(mName)) continue;
            var arity = _ctx.Rng.Next(3); // 0..2
            var pTypes = Enumerable.Repeat(ExprType.Int, arity).ToList();
            var isAsync = asyncEligible && _ctx.Rng.NextDouble() < 0.25;
            methods.Add(new UserClassMethod(mName, pTypes, ExprType.Int, IsAsync: isAsync));
            methodTexts.Add(BuildMethodText(mName, pTypes, ExprType.Int, fieldScope, isAsync));
        }

        // Optional mutation method. Only added when SetMutationExprGenerator is
        // wired and the class has at least one mutable field (always true today
        // since fields are always emitted with #:mutable). Excluded from #:open
        // bases to keep the override-picker free of mutation-shaped bodies.
        var mutableFields = fields.Where(f => f.IsMutable).ToList();
        if (_setMutation is not null && !isOpen && mutableFields.Count > 0
            && _ctx.Rng.NextDouble() < 0.4)
        {
            var mName = $"Mut_{index}";
            if (usedNames.Add(mName))
            {
                methods.Add(new UserClassMethod(mName, [], ExprType.Int));
                var bodyDepth = Math.Min(_ctx.MaxDepth, 4);
                var body = _setMutation.BuildMutationMethodBody(
                    mutableFields, fieldScope, bodyDepth);
                methodTexts.Add($"  (define ({mName}) : Int {body})");
            }
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

        // Pick 1..N base methods to override. Multiple overrides on a single
        // derived class exercise vtable layout (slot ordering, contiguous
        // override entries) more thoroughly than the prior single-override shape.
        var howMany = 1 + _ctx.Rng.Next(baseClass.Methods.Count);
        var pickedIndices = Enumerable.Range(0, baseClass.Methods.Count)
            .OrderBy(_ => _ctx.Rng.Next())
            .Take(howMany)
            .ToList();

        var overrideTexts = new List<string>(pickedIndices.Count);
        var overriddenMethods = new List<UserClassMethod>(pickedIndices.Count);
        foreach (var idx in pickedIndices)
        {
            var baseMethod = baseClass.Methods[idx];
            var paramSig = string.Join(" ",
                Enumerable.Range(0, baseMethod.ParamTypes.Count).Select(i => $"[p{i} : Int]"));

            // Body: `(+ (super/MName p0 p1 ...) <int>)`. Forwarding to super tests
            // the override-path super-call codegen end-to-end at compile time. Body
            // stays small to avoid runtime divergences in currently-uncalled methods.
            var superArgs = string.Join(" ",
                Enumerable.Range(0, baseMethod.ParamTypes.Count).Select(i => $"p{i}"));
            var superCall = baseMethod.ParamTypes.Count == 0
                ? $"(super/{baseMethod.Name})"
                : $"(super/{baseMethod.Name} {superArgs})";

            var bodyExpr = ownFields.Count > 0
                ? $"(+ {superCall} {ownFields[0].Name})"
                : superCall;

            var paramsPart = baseMethod.ParamTypes.Count == 0 ? "" : $" {paramSig}";
            overrideTexts.Add($"  (define ({baseMethod.Name}{paramsPart}) : Int {bodyExpr})");
            overriddenMethods.Add(new UserClassMethod(baseMethod.Name, baseMethod.ParamTypes, baseMethod.RetType));
        }

        var header = $"(class {name} : {baseClass.Name}";
        var bodyParts = new List<string>(fieldDecls);
        bodyParts.AddRange(overrideTexts);
        var def = $"{header}\n{string.Join("\n", bodyParts)})";

        return new UserClassDecl(
            name,
            ownFields,
            ctorParamTypes,
            // Inherits all base methods plus the overrides (overrides share names).
            // For construct/call bookkeeping we list only the overrides' signatures.
            overriddenMethods,
            IsOpen: false,
            BaseName: baseClass.Name,
            ImplementedInterfaces: Array.Empty<string>(),
            def);
    }

    // Builds a method text `(define (MName [p0 : Int] ...) : Int <body>)` whose
    // body comes from the shared ExprGenerator. Field names are in `fieldScope`
    // so the body may reference them as bare identifiers. When `isAsync` is set,
    // emits `(define-async ... : (Task Int) <async-body>)` and draws the body
    // from AsyncExprGenerator so it may contain `(await ...)` calls into the
    // program's async-helper pool.
    private string BuildMethodText(
        string mName,
        IReadOnlyList<ExprType> paramTypes,
        ExprType retType,
        Scope fieldScope,
        bool isAsync = false)
    {
        var paramSig = string.Join(" ",
            Enumerable.Range(0, paramTypes.Count).Select(i => $"[p{i} : Int]"));

        var bodyScope = fieldScope;
        for (var i = 0; i < paramTypes.Count; i++)
            bodyScope = bodyScope.Extend($"p{i}", ExprType.Int);

        // Method body depth is bounded so generated classes don't blow up emit time
        // — methods rarely benefit from full max-depth recursion the way compute does.
        var bodyDepth = Math.Min(_ctx.MaxDepth, 4);

        var paramsPart = paramTypes.Count == 0 ? "" : $" {paramSig}";

        if (isAsync)
        {
            if (retType != ExprType.Int)
                throw new InvalidOperationException($"Async method requires Int return, got {retType}");
            if (_async is null)
                throw new InvalidOperationException("Async method requested before AsyncExprGenerator was wired");
            var asyncBody = _async.GenAsyncBodyInt(bodyScope, bodyDepth);
            return $"  (define-async ({mName}{paramsPart}) : (Task Int) {asyncBody})";
        }

        var body = retType switch
        {
            ExprType.Int => _exprs.GenInt(bodyScope, bodyDepth),
            _ => throw new InvalidOperationException($"Unsupported method return type: {retType}")
        };

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

    // Construct-and-call reducer: binds an instance with `let`, calls one of its
    // methods via the imported alias, and returns the Int result. Requires that
    // ProgramGenerator has emitted `EmitInstanceImportClrBlock()` so the alias
    // resolves at parse time.
    public string ConstructAndCallToInt(Scope scope, int depth)
    {
        if (_ctx.UserClasses.Count == 0)
            throw new InvalidOperationException("ConstructAndCallToInt called with no user classes");

        // Pick a (class, method) pair where the method returns Int and is sync.
        // Async methods return `(Task Int)`; calling them from this sync reducer
        // would require an `await` that this code path can't emit. They're still
        // emitted for compile-time codegen coverage of class-method state machines.
        var eligible = new List<(int ClassIdx, UserClassDecl Cls, UserClassMethod Method)>();
        for (var ci = 0; ci < _ctx.UserClasses.Count; ci++)
        {
            var cls = _ctx.UserClasses[ci];
            foreach (var m in cls.Methods)
                if (m.RetType == ExprType.Int && !m.IsAsync)
                    eligible.Add((ci, cls, m));
        }
        if (eligible.Count == 0)
            return ConstructDiscardToInt(scope, depth);

        var pick = eligible[_ctx.Rng.Next(eligible.Count)];
        var ctorArgs = new List<string>();
        foreach (var p in pick.Cls.ConstructorParamTypes)
            ctorArgs.Add(_exprs.GenInt(scope, depth - 1));

        var construct = ctorArgs.Count == 0
            ? $"(new {pick.Cls.Name})"
            : $"(new {pick.Cls.Name} {string.Join(" ", ctorArgs)})";

        var instance = _ctx.Fresh();
        var alias = InstanceMethodAlias(pick.ClassIdx, pick.Method.Name);

        var callArgs = new List<string> { instance };
        foreach (var pt in pick.Method.ParamTypes)
        {
            if (pt != ExprType.Int)
                throw new InvalidOperationException($"Unexpected class method param type: {pt}");
            callArgs.Add(_exprs.GenInt(scope, depth - 1));
        }

        return $"(let [{instance} {construct}]\n" +
               $"    ({alias} {string.Join(" ", callArgs)}))";
    }

    // Emits the `(import-clr ...)` block for every (class, method) pair in
    // _ctx.UserClasses. Returns empty string when no classes exist or when the
    // EnableClassInstanceCalls gate is off (callers should not invoke the block
    // emitter unless the call-site reducer is also enabled, otherwise the file
    // would contain unused aliases).
    public string EmitInstanceImportClrBlock(string namespaceName)
    {
        if (_ctx.UserClasses.Count == 0) return string.Empty;

        var lines = new List<string> { "(import-clr" };
        var entries = new List<string>();
        for (var ci = 0; ci < _ctx.UserClasses.Count; ci++)
        {
            var cls = _ctx.UserClasses[ci];
            foreach (var m in cls.Methods)
            {
                // Skip async methods — their return type is `(Task Int)`, and
                // ConstructAndCallToInt can't await from its sync call site.
                if (m.RetType != ExprType.Int || m.IsAsync) continue;
                var alias = InstanceMethodAlias(ci, m.Name);
                var paramSig = string.Join(" ", new[] { cls.Name }
                    .Concat(m.ParamTypes.Select(_ => "Int")));
                var clrPath = $"{namespaceName}.{cls.Name}.{m.Name}";
                entries.Add($"  [{alias} {clrPath} :instance : ({paramSig} -> Int)]");
            }
        }
        if (entries.Count == 0) return string.Empty;
        for (var i = 0; i < entries.Count; i++)
        {
            var line = entries[i];
            if (i == entries.Count - 1) line += ")";
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    private static string InstanceMethodAlias(int classIdx, string methodName) =>
        $"call-c{classIdx}-{methodName.ToLowerInvariant().Replace('_', '-')}";

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

namespace ZScheme.Fuzzer.Generation;

// Emits anonymous-object expressions in Int-returning position by binding the
// object to a `let` and discarding the binding:
//
//   (let [obj : IName (object IName (define (M ...) : Int <int-expr>) ...)] <int-tail>)
//
// When at least one #:open user class exists we additionally emit the
// base-inheriting form (note: NO type annotation on this binding — annotating
// `(let [x : Base (object : Base ...)] ...)` triggers a type-inferer error
// "'Base' vs '(Int, ...) -> Base'" because the annotation path conflicts with
// the anonymous subclass type produced by `(object : Base ...)`):
//
//   (let [obj (object : Base
//               (constructor (super <int-args>))
//               (define (M ...) : Int <int-expr>) ...)]
//     <int-tail>)
//
// The same external-instance-method-call constraint that limits ClassExprGenerator
// applies here: the object is constructed (exercising object-decl typing, IR
// lowering, and IL/C# emit for the anonymous nominal type) but no method is
// invoked from compute. See the note at the top of ClassExprGenerator for the
// underlying IL stack-imbalance bug that motivates this.
public sealed class ObjectExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ObjectExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool HasEligible()
    {
        return _ctx.UserInterfaces.Count > 0
               || _ctx.UserClasses.Any(c => c.IsOpen && c.Methods.Count > 0);
    }

    public string ObjectDiscardToInt(Scope scope, int depth)
    {
        // Prefer interface form when interfaces exist; otherwise inherit form.
        var hasIface = _ctx.UserInterfaces.Count > 0;
        var openBases = _ctx.UserClasses.Where(c => c.IsOpen && c.Methods.Count > 0).ToList();
        var hasBase = openBases.Count > 0;

        var pickInterface = hasIface && (!hasBase || _ctx.Rng.NextDouble() < 0.6);
        if (pickInterface)
            return EmitInterfaceObject(scope, depth);

        return EmitInheritingObject(scope, depth, openBases);
    }

    private string EmitInterfaceObject(Scope scope, int depth)
    {
        // Single-interface (existing) or two-interface (new). Multi-interface
        // objects exercise method-table layout when the synthesized anonymous
        // type implements multiple unrelated interfaces. Limited to two so we
        // don't blow up program size; method-name collisions across interfaces
        // are deduped by name (interface methods are uniquely named in practice).
        var ifaces = _ctx.UserInterfaces;
        var pickTwo = ifaces.Count >= 2 && _ctx.Rng.NextDouble() < 0.4;

        var picked = new List<UserInterfaceDecl>();
        if (pickTwo)
        {
            var idx1 = _ctx.Rng.Next(ifaces.Count);
            int idx2;
            do
            {
                idx2 = _ctx.Rng.Next(ifaces.Count);
            } while (idx2 == idx1);

            picked.Add(ifaces[idx1]);
            picked.Add(ifaces[idx2]);
        }
        else
        {
            picked.Add(ifaces[_ctx.Rng.Next(ifaces.Count)]);
        }

        var bindName = _ctx.Fresh();
        var seenNames = new HashSet<string>();
        var methodTexts = new List<string>();
        foreach (var iface in picked)
        foreach (var im in iface.Methods)
        {
            if (!seenNames.Add(im.Name)) continue;
            // Pass the enclosing scope so the method body can reference
            // captures (e.g., enclosing-class fields when this object is
            // emitted inside a class method — the path commit a221d41 fixed).
            methodTexts.Add(BuildMethodText(im.Name, im.ParamTypes, im.RetType, depth, scope));
        }

        // Single interface: bare atom. Multi-interface: grouped list `(IFoo IBar)`
        // — the bare-atom form only takes one interface name (AstBuilder.cs:1110-1113).
        var headerNames = picked.Count == 1
            ? picked[0].Name
            : $"({string.Join(" ", picked.Select(i => i.Name))})";
        var body = $"(object {headerNames}\n{string.Join("\n", methodTexts)})";
        var tail = _exprs.GenInt(scope, depth - 1);
        // Type annotation only when the object's nominal type is namable: a
        // single interface name. Multi-interface forms produce a synthesized
        // intersection type that can't be written as a `: Type` annotation.
        if (picked.Count == 1)
            return $"(let [{bindName} : {picked[0].Name} {body}] {tail})";
        return $"(let [{bindName} {body}] {tail})";
    }

    private string EmitInheritingObject(Scope scope, int depth, IReadOnlyList<UserClassDecl> openBases)
    {
        var baseCls = openBases[_ctx.Rng.Next(openBases.Count)];
        var bindName = _ctx.Fresh();

        var superArgs = new List<string>(baseCls.ConstructorParamTypes.Count);
        foreach (var p in baseCls.ConstructorParamTypes)
        {
            if (p != ExprType.Int)
                throw new InvalidOperationException($"Unexpected base ctor param type: {p}");
            superArgs.Add(_exprs.GenInt(scope, depth - 1));
        }

        var superCall = superArgs.Count == 0
            ? "(super)"
            : $"(super {string.Join(" ", superArgs)})";

        // Override one base method (must match its signature). Pass the
        // enclosing scope so captures (notably enclosing-class fields, per
        // commit a221d41) are reachable in the method body.
        var baseMethod = baseCls.Methods[_ctx.Rng.Next(baseCls.Methods.Count)];
        var overrideText = BuildMethodText(baseMethod.Name, baseMethod.ParamTypes, baseMethod.RetType, depth, scope);

        var body =
            $"(object : {baseCls.Name}\n" +
            $"  (constructor {superCall})\n" +
            $"{overrideText})";
        var tail = _exprs.GenInt(scope, depth - 1);
        // Type annotation deliberately omitted — see file-top comment.
        return $"(let [{bindName} {body}] {tail})";
    }

    // `outerScope` lets the generated method body reference captures from the
    // enclosing context — most importantly enclosing-class fields when this
    // object is emitted inside a class method (commit a221d41). When null, the
    // method body uses an empty scope (only its own params) — the original shape.
    //
    // `callerDepth` is the depth at which the surrounding ObjectDiscardToInt
    // reducer was invoked; the body depth strictly decreases from it so that
    // an object expression nested inside another object expression's method
    // body bottoms out (otherwise BuildMethodText could re-enter itself
    // unboundedly via repeated ObjectDiscardToInt picks at constant depth=3).
    private string BuildMethodText(
        string mName,
        IReadOnlyList<ExprType> paramTypes,
        ExprType retType,
        int callerDepth,
        Scope? outerScope = null)
    {
        var paramSig = string.Join(" ",
            Enumerable.Range(0, paramTypes.Count).Select(i => $"[p{i} : Int]"));
        var bodyScope = outerScope ?? new Scope();
        for (var i = 0; i < paramTypes.Count; i++)
            bodyScope = bodyScope.Extend($"p{i}", ExprType.Int);

        var bodyDepth = Math.Min(Math.Max(0, callerDepth - 1), 3);
        var body = retType switch
        {
            ExprType.Int => _exprs.GenInt(bodyScope, bodyDepth),
            _ => throw new InvalidOperationException($"Unsupported method return type: {retType}")
        };
        var paramsPart = paramTypes.Count == 0 ? "" : $" {paramSig}";
        return $"  (define ({mName}{paramsPart}) : Int {body})";
    }
}

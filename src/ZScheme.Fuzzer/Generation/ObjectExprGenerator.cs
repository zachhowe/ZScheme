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

    public bool HasEligible() =>
        _ctx.UserInterfaces.Count > 0
        || _ctx.UserClasses.Any(c => c.IsOpen && c.Methods.Count > 0);

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
        var iface = _ctx.UserInterfaces[_ctx.Rng.Next(_ctx.UserInterfaces.Count)];
        var bindName = _ctx.Fresh();
        var methodTexts = new List<string>(iface.Methods.Count);
        foreach (var im in iface.Methods)
            methodTexts.Add(BuildMethodText(im.Name, im.ParamTypes, im.RetType));

        var body = $"(object {iface.Name}\n{string.Join("\n", methodTexts)})";
        var tail = _exprs.GenInt(scope, depth - 1);
        return $"(let [{bindName} : {iface.Name} {body}] {tail})";
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

        // Override one base method (must match its signature).
        var baseMethod = baseCls.Methods[_ctx.Rng.Next(baseCls.Methods.Count)];
        var overrideText = BuildMethodText(baseMethod.Name, baseMethod.ParamTypes, baseMethod.RetType);

        var body =
            $"(object : {baseCls.Name}\n" +
            $"  (constructor {superCall})\n" +
            $"{overrideText})";
        var tail = _exprs.GenInt(scope, depth - 1);
        // Type annotation deliberately omitted — see file-top comment.
        return $"(let [{bindName} {body}] {tail})";
    }

    private string BuildMethodText(string mName, IReadOnlyList<ExprType> paramTypes, ExprType retType)
    {
        var paramSig = string.Join(" ",
            Enumerable.Range(0, paramTypes.Count).Select(i => $"[p{i} : Int]"));
        var bodyScope = new Scope();
        for (var i = 0; i < paramTypes.Count; i++)
            bodyScope = bodyScope.Extend($"p{i}", ExprType.Int);

        var bodyDepth = Math.Min(_ctx.MaxDepth, 3);
        var body = retType switch
        {
            ExprType.Int => _exprs.GenInt(bodyScope, bodyDepth),
            _ => throw new InvalidOperationException($"Unsupported method return type: {retType}")
        };
        var paramsPart = paramTypes.Count == 0 ? "" : $" {paramSig}";
        return $"  (define ({mName}{paramsPart}) : Int {body})";
    }
}

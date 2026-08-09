using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Lambda-lifting sub-pass. Rewrites each capturing lambda (an <see cref="IrNode.FuncDef" />
///     with free variables bound in an enclosing local scope) into a top-level static
///     <see cref="IrNode.FuncDef" /> whose capture values are prepended as parameters, and
///     replaces the lambda expression with an <see cref="IrNode.Closure" /> node carrying the
///     lifted function's name and the captured value expressions. Both backends consume the
///     <see cref="IrNode.Closure" /> node to build the delegate.
///
///     Only <b>capturing</b> lambdas are lifted. A lambda with nothing to capture is left as a
///     bare <see cref="IrNode.FuncDef" /> for the backends' own emission (the C# emitter emits a
///     native lambda; the IL emitter emits a static method). Two categories are also left as
///     bare <see cref="IrNode.FuncDef" /> because a context-free IR pass cannot lift them
///     soundly (see <see cref="ConvertFuncDef" />):
///     <list type="bullet">
///         <item>lambdas inside a class/instance context (they may capture <c>this</c>/fields,
///         which this pass cannot see) — the whole <see cref="IrNode.ClassDecl" /> subtree is
///         left untouched;</item>
///         <item>lambdas that capture an enclosing generic function's type variables — the
///         lifted static function would leave those type parameters undeclared.</item>
///     </list>
///
///     The traversal mirrors <see cref="PatternResolver" />'s recursion set (exhaustive over
///     every node that can contain a nested lambda). It runs after <see cref="IiffeBetaReducer" />
///     (so immediately-invoked lambdas are already <c>let</c> spines and never needlessly lifted)
///     and before <see cref="PatternResolver" /> (which then resolves <c>match</c> patterns in
///     both the residual bodies and the spliced lifted functions, and already descends into
///     <see cref="IrNode.Closure" /> nodes).
/// </summary>
public sealed class ClosureConverter
{
    private readonly List<IrNode.FuncDef> _liftedFunctions = [];
    private int _closureId;

    /// <summary>
    ///     The top-level static functions produced by lifting, in creation order. The wiring
    ///     step splices these into the program's top-level <see cref="IrNode.Seq" />.
    /// </summary>
    public IReadOnlyList<IrNode.FuncDef> LiftedFunctions => _liftedFunctions;

    public IrNode Convert(IrNode node)
    {
        return Rewrite(node, []);
    }

    /// <summary>
    ///     Rewrites <paramref name="node" />, lifting capturing lambdas. <paramref name="locals" />
    ///     is the set of names bound by enclosing <b>local</b> binders (enclosing function/lambda
    ///     params, <c>let</c>/<c>use</c> bindings, <c>match</c> arm bindings, exception-handler
    ///     bindings) between the top level and this node. A lambda captures exactly its free
    ///     variables that are in this set; everything else (globals, top-level functions) is left
    ///     as a free reference resolved at top level.
    /// </summary>
    private IrNode Rewrite(IrNode node, HashSet<string> locals)
    {
        switch (node)
        {
            case IrNode.FuncDef func:
                return ConvertFuncDef(func, locals);

            case IrNode.Closure closure:
                return new IrNode.Closure(
                    closure.LiftedFuncName,
                    closure.CapturedValues.Select(v => Rewrite(v, locals)).ToList()
                )
                {
                    Type = closure.Type,
                    Span = closure.Span,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(n => Rewrite(n, locals)).ToList())
                {
                    Type = seq.Type,
                    Span = seq.Span,
                };

            case IrNode.Let let:
                return new IrNode.Let(
                    let.VarName,
                    Rewrite(let.Value, locals),
                    Rewrite(let.Body, Extend(locals, let.VarName)),
                    let.VarType,
                    let.EmitName
                )
                {
                    Type = let.Type,
                    Span = let.Span,
                };

            case IrNode.Use use:
                return new IrNode.Use(
                    use.VarName,
                    Rewrite(use.Value, locals),
                    Rewrite(use.Body, Extend(locals, use.VarName)),
                    use.VarType
                )
                {
                    Type = use.Type,
                    Span = use.Span,
                };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition, locals),
                    Rewrite(ifNode.Then, locals),
                    Rewrite(ifNode.Else, locals)
                )
                {
                    Type = ifNode.Type,
                    Span = ifNode.Span,
                };

            case IrNode.Call call:
                return new IrNode.Call(
                    Rewrite(call.Function, locals),
                    call.Args.Select(a => Rewrite(a, locals)).ToList()
                )
                {
                    Type = call.Type,
                    Span = call.Span,
                };

            case IrNode.BinOp binop:
                return new IrNode.BinOp(
                    binop.Op,
                    Rewrite(binop.Left, locals),
                    Rewrite(binop.Right, locals)
                )
                {
                    Type = binop.Type,
                    Span = binop.Span,
                };

            case IrNode.UnaryOp unary:
                return new IrNode.UnaryOp(unary.Op, Rewrite(unary.Operand, locals))
                {
                    Type = unary.Type,
                    Span = unary.Span,
                };

            case IrNode.Match match:
                return new IrNode.Match(
                    Rewrite(match.Scrutinee, locals),
                    match
                        .Arms.Select(a => new IrMatchArm(
                            a.Pattern,
                            Rewrite(a.Body, Extend(locals, a.Pattern.BoundNames()))
                        ))
                        .ToList()
                )
                {
                    Type = match.Type,
                    Span = match.Span,
                };

            case IrNode.MethodCall mc:
                return mc with
                {
                    Receiver = Rewrite(mc.Receiver, locals),
                    Args = mc.Args.Select(a => Rewrite(a, locals)).ToList(),
                };

            case IrNode.ClrNew cn:
                return new IrNode.ClrNew(
                    cn.QualifiedTypeName,
                    cn.TypeArgs,
                    cn.Args.Select(a => Rewrite(a, locals)).ToList()
                )
                {
                    Type = cn.Type,
                    Span = cn.Span,
                };

            case IrNode.ClrCall cc:
                return cc with { Args = cc.Args.Select(a => Rewrite(a, locals)).ToList() };

            case IrNode.TupleNew tn:
                return new IrNode.TupleNew(tn.Elements.Select(e => Rewrite(e, locals)).ToList())
                {
                    Type = tn.Type,
                    Span = tn.Span,
                };

            case IrNode.UnionCaseNew ucn:
                return new IrNode.UnionCaseNew(
                    ucn.UnionName,
                    ucn.CaseName,
                    ucn.Args.Select(a => Rewrite(a, locals)).ToList()
                )
                {
                    Type = ucn.Type,
                    Span = ucn.Span,
                };

            case IrNode.RecordNew rn:
                return new IrNode.RecordNew(
                    rn.TypeName,
                    rn.Fields.Select(f => (f.FieldName, Rewrite(f.Value, locals))).ToList()
                )
                {
                    Type = rn.Type,
                    Span = rn.Span,
                };

            case IrNode.RecordWith rw:
                return new IrNode.RecordWith(
                    rw.TypeName,
                    Rewrite(rw.Record, locals),
                    rw.Updates.Select(u => (u.FieldName, Rewrite(u.Value, locals))).ToList()
                )
                {
                    Type = rw.Type,
                    Span = rw.Span,
                };

            case IrNode.MutableArrayNew man:
                return new IrNode.MutableArrayNew(
                    man.ElementType,
                    man.Elements.Select(e => Rewrite(e, locals)).ToList()
                )
                {
                    Type = man.Type,
                    Span = man.Span,
                };

            case IrNode.FieldGet fg:
                return new IrNode.FieldGet(Rewrite(fg.Record, locals), fg.FieldName)
                {
                    Type = fg.Type,
                    Span = fg.Span,
                };

            case IrNode.Throw thr:
                return new IrNode.Throw(Rewrite(thr.Expr, locals))
                {
                    Type = thr.Type,
                    Span = thr.Span,
                };

            case IrNode.Await aw:
                return new IrNode.Await(Rewrite(aw.Expr, locals))
                {
                    Type = aw.Type,
                    Span = aw.Span,
                };

            case IrNode.SetField sf:
                return new IrNode.SetField(sf.FieldName, Rewrite(sf.Value, locals))
                {
                    Type = sf.Type,
                    Span = sf.Span,
                };

            case IrNode.SuperMethodCall smc:
                return new IrNode.SuperMethodCall(
                    smc.MethodName,
                    smc.Args.Select(a => Rewrite(a, locals)).ToList()
                )
                {
                    Type = smc.Type,
                    Span = smc.Span,
                };

            case IrNode.WithHandlers wh:
                return new IrNode.WithHandlers(
                    Rewrite(wh.Body, locals),
                    wh.Handlers.Select(h => new IrHandlerClause(
                            h.ExceptionTypeName,
                            h.BindingVarName,
                            Rewrite(h.HandlerBody, Extend(locals, h.BindingVarName))
                        ))
                        .ToList()
                )
                {
                    Type = wh.Type,
                    Span = wh.Span,
                };

            default:
                // Leaves (literals, Var, TypeOf) and childless declaration nodes (RecordDecl,
                // UnionDecl, TypeAliasDecl, InterfaceDecl). ClassDecl is also returned unchanged:
                // lambdas inside a class/instance context are left as FuncDefs for the backends'
                // own closure paths, since this pass cannot see class fields / `this` and so
                // cannot capture them soundly. ObjectExpr is already lifted to ClassDecl by
                // ObjectLifter, and TcoJump is introduced later by TailCallLowering (which runs
                // just before codegen), so neither reaches this pass — matching PatternResolver's
                // recursion set.
                return node;
        }
    }

    private IrNode ConvertFuncDef(IrNode.FuncDef func, HashSet<string> locals)
    {
        var paramNames = func.Params.Select(p => p.Name).ToHashSet();

        // Free variables of the body that are bound in an enclosing local scope become captures.
        // Names not in `locals` (globals, top-level functions, imported names) are left as free
        // references in the lifted body — they resolve at top level — so a global is never
        // captured. This mirrors the IL backend, which only captures names found among the
        // enclosing method's locals/params.
        var captures = CollectFreeVars(func.Body, paramNames)
            .Where(v => locals.Contains(v.Name))
            .ToList();

        var bodyLocals = Extend(locals, paramNames);

        // Conservative subset: leave the lambda as a bare FuncDef (for the backends' own closure
        // paths) when there is nothing to capture or when lifting would be unsound (outer
        // generic type-var capture — see CapturesOuterGenerics).
        if (captures.Count == 0 || CapturesOuterGenerics(func, captures))
            return func with { Body = Rewrite(func.Body, bodyLocals) };

        var captureParams = captures.Select(v => new IrParam(v.Name, v.Type)).ToList();
        var allParams = captureParams.Concat(func.Params).ToList();
        var liftedBody = Rewrite(func.Body, bodyLocals);
        var liftedName = $"__closure_{_closureId++}_{func.Name}";

        // The lifted function is an ordinary top-level static function: its signature is
        // (captures..., original params...) -> return, NOT the lambda's delegate/func type. So
        // it is typed by its own signature and keeps ClrDelegateTypeName null. The delegate/func
        // type stays on the Closure node (Type below) — that is what both backends read to build
        // the delegate value from the lifted method plus the captured values.
        var liftedType = new ZType.ZFuncType(
            [.. captureParams.Select(p => p.Type), .. func.Params.Select(p => p.Type)],
            func.ReturnType
        );
        var liftedFunc = func with
        {
            Name = liftedName,
            Params = allParams,
            Body = liftedBody,
            ClrDelegateTypeName = null,
            IsSelfRecursive = false,
            Type = liftedType,
        };
        _liftedFunctions.Add(liftedFunc);

        // Reuse the actual free Var nodes as the captured values, so they carry their inferred
        // Type (and any ModuleName/EmitName routing) into the construction site.
        var capturedValues = captures.Cast<IrNode>().ToList();
        return new IrNode.Closure(liftedName, capturedValues)
        {
            Type = func.Type,
            Span = func.Span,
        };
    }

    /// <summary>
    ///     True when the lambda's own type or any captured value's type still mentions a free
    ///     type variable — i.e. it refers to an enclosing generic function's type parameters.
    ///     Lifting such a lambda to a top-level static function would leave those type parameters
    ///     undeclared, producing invalid output, so it is left to the backends' own lambda paths
    ///     (the IL emitter propagates the outer generic parameters onto the synthesized method).
    ///     Mirrors the IL backend's own detection via <see cref="Substitution.FreeVars(ZType)" />.
    /// </summary>
    private static bool CapturesOuterGenerics(IrNode.FuncDef func, List<IrNode.Var> captures)
    {
        return Substitution.FreeVars(func.Type).Count > 0
            || captures.Any(v => Substitution.FreeVars(v.Type).Count > 0);
    }

    /// <summary>
    ///     Collects the free-variable <see cref="IrNode.Var" /> nodes of <paramref name="node" />
    ///     with respect to <paramref name="bound" />, in stable first-seen order and deduplicated
    ///     by name (so each capture carries a concrete inferred <see cref="IrNode.Type" /> and the
    ///     generated parameter order is reproducible). The recursion set is exhaustive over every
    ///     node that can contain a variable reference, mirroring the IL emitter's own
    ///     free-variable walk so the two never disagree on what a lambda captures.
    /// </summary>
    private static List<IrNode.Var> CollectFreeVars(IrNode node, HashSet<string> bound)
    {
        var seen = new HashSet<string>();
        var result = new List<IrNode.Var>();
        Collect(node, bound);
        return result;

        void Collect(IrNode n, HashSet<string> b)
        {
            switch (n)
            {
                case IrNode.Var v:
                    if (!b.Contains(v.Name) && seen.Add(v.Name))
                        result.Add(v);
                    break;
                case IrNode.Let let:
                    Collect(let.Value, b);
                    Collect(let.Body, Extend(b, let.VarName));
                    break;
                case IrNode.Use use:
                    Collect(use.Value, b);
                    Collect(use.Body, Extend(b, use.VarName));
                    break;
                case IrNode.If ifNode:
                    Collect(ifNode.Condition, b);
                    Collect(ifNode.Then, b);
                    Collect(ifNode.Else, b);
                    break;
                case IrNode.Call call:
                    Collect(call.Function, b);
                    foreach (var a in call.Args)
                        Collect(a, b);
                    break;
                case IrNode.BinOp binop:
                    Collect(binop.Left, b);
                    Collect(binop.Right, b);
                    break;
                case IrNode.UnaryOp unary:
                    Collect(unary.Operand, b);
                    break;
                case IrNode.FuncDef func:
                    Collect(func.Body, Extend(b, func.Params.Select(p => p.Name)));
                    break;
                case IrNode.Match match:
                    Collect(match.Scrutinee, b);
                    foreach (var arm in match.Arms)
                        Collect(arm.Body, Extend(b, arm.Pattern.BoundNames()));
                    break;
                case IrNode.MethodCall mc:
                    Collect(mc.Receiver, b);
                    foreach (var a in mc.Args)
                        Collect(a, b);
                    break;
                case IrNode.UnionCaseNew ucn:
                    foreach (var a in ucn.Args)
                        Collect(a, b);
                    break;
                case IrNode.ClrNew cn:
                    foreach (var a in cn.Args)
                        Collect(a, b);
                    break;
                case IrNode.ClrCall cc:
                    foreach (var a in cc.Args)
                        Collect(a, b);
                    break;
                case IrNode.TupleNew tn:
                    foreach (var e in tn.Elements)
                        Collect(e, b);
                    break;
                case IrNode.RecordNew rn:
                    foreach (var f in rn.Fields)
                        Collect(f.Value, b);
                    break;
                case IrNode.RecordWith rw:
                    Collect(rw.Record, b);
                    foreach (var u in rw.Updates)
                        Collect(u.Value, b);
                    break;
                case IrNode.MutableArrayNew man:
                    foreach (var e in man.Elements)
                        Collect(e, b);
                    break;
                case IrNode.Seq seq:
                    foreach (var s in seq.Nodes)
                        Collect(s, b);
                    break;
                case IrNode.Throw thr:
                    Collect(thr.Expr, b);
                    break;
                case IrNode.WithHandlers wh:
                    Collect(wh.Body, b);
                    foreach (var h in wh.Handlers)
                        Collect(h.HandlerBody, Extend(b, h.BindingVarName));
                    break;
                case IrNode.Await aw:
                    Collect(aw.Expr, b);
                    break;
                case IrNode.SetField sf:
                    Collect(sf.Value, b);
                    break;
                case IrNode.FieldGet fg:
                    Collect(fg.Record, b);
                    break;
                case IrNode.SuperMethodCall smc:
                    foreach (var a in smc.Args)
                        Collect(a, b);
                    break;
                case IrNode.TcoJump tj:
                    foreach (var a in tj.NewArgs)
                        Collect(a, b);
                    break;
                case IrNode.Closure cl:
                    foreach (var v in cl.CapturedValues)
                        Collect(v, b);
                    break;
                // Leaves (literals, TypeOf) and declaration nodes bind/reference nothing here.
            }
        }
    }

    private static HashSet<string> Extend(HashSet<string> bound, string name)
    {
        return new HashSet<string>(bound) { name };
    }

    private static HashSet<string> Extend(HashSet<string> bound, IEnumerable<string> names)
    {
        var result = new HashSet<string>(bound);
        result.UnionWith(names);
        return result;
    }
}

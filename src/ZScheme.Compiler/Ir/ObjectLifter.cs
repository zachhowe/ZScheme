using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Lowers <c>(object ...)</c> anonymous-class expressions (<see cref="IrNode.ObjectExpr" />)
///     into synthesized named classes (<see cref="IrNode.ClassDecl" />) plus an ordinary
///     construction expression (<see cref="IrNode.ClrNew" />) at the original site. After this
///     pass runs, no <see cref="IrNode.ObjectExpr" /> remains in the IR, so the post-lowering
///     passes and both code emitters handle the synthesized classes through the same path they
///     already use for <c>(define-class ...)</c>.
///
///     <para>
///         A captured variable is a name referenced inside the object that is bound in an
///         enclosing local scope — a function/lambda parameter, a <c>let</c>/<c>use</c>/match
///         binding, or (when the object is nested inside a class method) one of that class's
///         fields. Module-level globals and top-level functions are NOT captured: they remain
///         statically reachable from the synthesized class, exactly as they are from any other
///         method. This is why the pass threads the enclosing bound-name set as it walks —
///         capture analysis is fundamentally scope-dependent.
///     </para>
///
///     <para>
///         Each synthesized class is spliced into the top-level sequence immediately before the
///         top-level form that constructs it. The IL backend resolves <c>ClrNew</c> of a
///         ZScheme class via a define-before-use type table, so a synthesized class must be
///         emitted before the function/value whose body constructs it; placing it right before
///         that form preserves the ordering (and keeps it after any user base class, which
///         appears earlier in source order). Nested object expressions are emitted inner-first
///         for the same reason.
///     </para>
/// </summary>
public sealed class ObjectLifter
{
    private int _objectId;

    // The synthesized classes for the top-level form currently being transformed.
    // Drained by Lift() and spliced in immediately before that form.
    private List<IrNode.ClassDecl> _sink = [];

    public IrNode Lift(IrNode program)
    {
        if (program is not IrNode.Seq seq)
        {
            var single = new List<IrNode.ClassDecl>();
            _sink = single;
            var lifted = TransformTopForm(program);
            if (single.Count == 0)
                return lifted;
            var nodes = new List<IrNode>(single) { lifted };
            return new IrNode.Seq(nodes) { Type = program.Type, Span = program.Span };
        }

        var newNodes = new List<IrNode>();
        foreach (var form in seq.Nodes)
        {
            _sink = [];
            var lifted = TransformTopForm(form);
            newNodes.AddRange(_sink); // synthesized classes precede the form that uses them
            newNodes.Add(lifted);
        }

        return new IrNode.Seq(newNodes) { Type = seq.Type, Span = seq.Span };
    }

    // Top-level forms establish their own local scope. Their own names (a top-level
    // FuncDef's name, a top-level Let binding) are module statics, never captured, so
    // they are not added to the bound set.
    private IrNode TransformTopForm(IrNode form)
    {
        switch (form)
        {
            case IrNode.FuncDef fd:
                return fd with { Body = Transform(fd.Body, Bound(fd.Params)) };

            case IrNode.ClassDecl cd:
                return TransformClassDecl(cd, []);

            case IrNode.Let let:
                return let with
                {
                    Value = Transform(let.Value, []),
                    Body = TransformTopForm(let.Body),
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(TransformTopForm).ToList())
                {
                    Type = seq.Type,
                    Span = seq.Span,
                };

            case IrNode.RecordDecl
            or IrNode.UnionDecl
            or IrNode.InterfaceDecl
            or IrNode.TypeAliasDecl
            or IrNode.UnitConst:
                return form;

            default:
                return Transform(form, []);
        }
    }

    private IrNode Transform(IrNode node, HashSet<string> bound)
    {
        switch (node)
        {
            case IrNode.ObjectExpr oe:
                return ConvertObject(oe, bound);

            case IrNode.Var
            or IrNode.IntConst
            or IrNode.FloatConst
            or IrNode.BoolConst
            or IrNode.StringConst
            or IrNode.UnitConst
            or IrNode.NullConst
            or IrNode.TypeOf:
                return node;

            case IrNode.Let let:
                return let with
                {
                    Value = Transform(let.Value, bound),
                    Body = Transform(let.Body, Bound(bound, let.VarName)),
                };

            case IrNode.Use use:
                return use with
                {
                    Value = Transform(use.Value, bound),
                    Body = Transform(use.Body, Bound(bound, use.VarName)),
                };

            case IrNode.If ifn:
                return ifn with
                {
                    Condition = Transform(ifn.Condition, bound),
                    Then = Transform(ifn.Then, bound),
                    Else = Transform(ifn.Else, bound),
                };

            case IrNode.Seq seq:
                return seq with { Nodes = seq.Nodes.Select(n => Transform(n, bound)).ToList() };

            case IrNode.BinOp bin:
                return bin with
                {
                    Left = Transform(bin.Left, bound),
                    Right = Transform(bin.Right, bound),
                };

            case IrNode.UnaryOp un:
                return un with { Operand = Transform(un.Operand, bound) };

            case IrNode.Call call:
                return call with
                {
                    Function = Transform(call.Function, bound),
                    Args = call.Args.Select(a => Transform(a, bound)).ToList(),
                };

            case IrNode.MethodCall mc:
                return mc with
                {
                    Receiver = Transform(mc.Receiver, bound),
                    Args = mc.Args.Select(a => Transform(a, bound)).ToList(),
                };

            case IrNode.ClrNew cn:
                return cn with { Args = cn.Args.Select(a => Transform(a, bound)).ToList() };

            case IrNode.ClrCall cc:
                return cc with { Args = cc.Args.Select(a => Transform(a, bound)).ToList() };

            case IrNode.UnionCaseNew ucn:
                return ucn with { Args = ucn.Args.Select(a => Transform(a, bound)).ToList() };

            case IrNode.TupleNew tn:
                return tn with { Elements = tn.Elements.Select(e => Transform(e, bound)).ToList() };

            case IrNode.MutableArrayNew man:
                return man with
                {
                    Elements = man.Elements.Select(e => Transform(e, bound)).ToList(),
                };

            case IrNode.RecordNew rn:
                return rn with
                {
                    Fields = rn
                        .Fields.Select(f => (f.FieldName, Transform(f.Value, bound)))
                        .ToList(),
                };

            case IrNode.RecordWith rw:
                return rw with
                {
                    Record = Transform(rw.Record, bound),
                    Updates = rw
                        .Updates.Select(u => (u.FieldName, Transform(u.Value, bound)))
                        .ToList(),
                };

            case IrNode.FieldGet fg:
                return fg with { Record = Transform(fg.Record, bound) };

            case IrNode.SetField sf:
                return sf with { Value = Transform(sf.Value, bound) };

            case IrNode.TypeTest tt:
                return tt with { Value = Transform(tt.Value, bound) };

            case IrNode.Throw th:
                return th with { Expr = Transform(th.Expr, bound) };

            case IrNode.Await aw:
                return aw with { Expr = Transform(aw.Expr, bound) };

            case IrNode.SuperMethodCall smc:
                return smc with { Args = smc.Args.Select(a => Transform(a, bound)).ToList() };

            case IrNode.TcoJump tj:
                return tj with { NewArgs = tj.NewArgs.Select(a => Transform(a, bound)).ToList() };

            case IrNode.Closure cl:
                return cl with
                {
                    CapturedValues = cl.CapturedValues.Select(v => Transform(v, bound)).ToList(),
                };

            case IrNode.Match match:
                return match with
                {
                    Scrutinee = Transform(match.Scrutinee, bound),
                    Arms = match
                        .Arms.Select(a => new IrMatchArm(
                            a.Pattern,
                            Transform(a.Body, BoundWithPattern(bound, a.Pattern))
                        ))
                        .ToList(),
                };

            case IrNode.WithHandlers wh:
                return wh with
                {
                    Body = Transform(wh.Body, bound),
                    Handlers = wh
                        .Handlers.Select(h => new IrHandlerClause(
                            h.ExceptionTypeName,
                            h.BindingVarName,
                            Transform(h.HandlerBody, Bound(bound, h.BindingVarName))
                        ))
                        .ToList(),
                };

            case IrNode.FuncDef fd:
                return fd with { Body = Transform(fd.Body, Bound(bound, fd.Params)) };

            case IrNode.ClassDecl cd:
                return TransformClassDecl(cd, bound);

            default:
                return node;
        }
    }

    private IrNode TransformClassDecl(IrNode.ClassDecl cd, HashSet<string> outerBound)
    {
        var fieldNames = cd.Fields.Select(f => f.Name);
        var classScope = Bound(outerBound, fieldNames);
        return cd with
        {
            Methods = cd
                .Methods.Select(m =>
                    m with
                    {
                        Body = Transform(m.Body, Bound(classScope, m.Params)),
                    }
                )
                .ToList(),
            Constructor = TransformConstructor(cd.Constructor, classScope),
        };
    }

    private IrConstructor? TransformConstructor(IrConstructor? ctor, HashSet<string> classScope)
    {
        if (ctor is null)
            return null;
        var ctorScope = Bound(classScope, ctor.Params);
        return ctor with
        {
            SuperArgs = ctor.SuperArgs?.Select(a => Transform(a, ctorScope)).ToList(),
            FieldSets = ctor
                .FieldSets.Select(fs => (fs.FieldName, Transform(fs.Value, ctorScope)))
                .ToList(),
            BodyExprs = ctor.BodyExprs.Select(e => Transform(e, ctorScope)).ToList(),
        };
    }

    private IrNode ConvertObject(IrNode.ObjectExpr oe, HashSet<string> bound)
    {
        // 1. Collect free names (with their first-seen type) over the object's own scopes.
        var free = new List<(string Name, ZType Type)>();
        var seen = new HashSet<string>();
        foreach (var m in oe.Methods)
            CollectFree(m.Body, Bound(m.Params), free, seen);
        if (oe.Constructor is { } ctor)
        {
            var ctorScope = Bound(ctor.Params);
            if (ctor.SuperArgs is not null)
                foreach (var a in ctor.SuperArgs)
                    CollectFree(a, ctorScope, free, seen);
            foreach (var (_, v) in ctor.FieldSets)
                CollectFree(v, ctorScope, free, seen);
            foreach (var e in ctor.BodyExprs)
                CollectFree(e, ctorScope, free, seen);
        }

        // 2. A free name is captured iff it is bound in the enclosing local scope.
        var captures = free.Where(f => bound.Contains(f.Name))
            .Select(f => (f.Name, Type: DefaultFreeTypeVars(f.Type)))
            .ToList();

        var className = $"__Object_{_objectId++}";

        // 3. Inside the synthesized class, captures are reachable as fields (same names),
        // so transform the object's bodies for any nested object expressions with the
        // capture names in scope alongside the method/ctor parameters.
        var captureNames = captures.Select(c => c.Name);
        var methods = oe
            .Methods.Select(m => m with { Body = Transform(m.Body, Bound(captureNames, m.Params)) })
            .ToList();

        IrConstructor? loweredObjCtor = null;
        if (oe.Constructor is { } objCtor)
        {
            var ctorScope = Bound(captureNames, objCtor.Params);
            loweredObjCtor = objCtor with
            {
                SuperArgs = objCtor.SuperArgs?.Select(a => Transform(a, ctorScope)).ToList(),
                FieldSets = objCtor
                    .FieldSets.Select(fs => (fs.FieldName, Transform(fs.Value, ctorScope)))
                    .ToList(),
                BodyExprs = objCtor.BodyExprs.Select(e => Transform(e, ctorScope)).ToList(),
            };
        }

        // 4. Build the synthesized constructor: capture params (named identically to the
        // capture fields) initialize the fields, then any explicit object constructor's
        // super-args / field-sets / body run. Within a class constructor, a bare Var that
        // names both a parameter and a field resolves to the parameter on both backends,
        // so `this.<cap> = <cap>` is correct.
        var fields = captures.Select(c => new IrField(c.Name, c.Type)).ToList();
        var ctorParams = captures.Select(c => new IrParam(c.Name, c.Type)).ToList();
        var fieldSets = new List<(string FieldName, IrNode Value)>();
        foreach (var c in captures)
            fieldSets.Add((c.Name, new IrNode.Var(c.Name) { Type = c.Type, Span = oe.Span }));
        if (loweredObjCtor is not null)
            fieldSets.AddRange(loweredObjCtor.FieldSets);

        var synthCtor = new IrConstructor(
            ctorParams,
            loweredObjCtor?.SuperArgs,
            fieldSets,
            loweredObjCtor?.BodyExprs ?? []
        );

        var classDecl = new IrNode.ClassDecl(
            className,
            [],
            oe.InterfaceNames,
            fields,
            methods,
            IsOpen: false,
            oe.BaseClassName,
            synthCtor,
            IsObjectLifted: true
        )
        {
            Type = ZType.Unit,
            Span = oe.Span,
        };
        _sink.Add(classDecl);

        // 5. Replace the object expression with a positional construction. The capture
        // argument Vars resolve in the original (outer) scope.
        var args = captures
            .Select(c => (IrNode)new IrNode.Var(c.Name) { Type = c.Type, Span = oe.Span })
            .ToList();
        return new IrNode.ClrNew(className, [], args) { Type = oe.Type, Span = oe.Span };
    }

    private static void CollectFree(
        IrNode node,
        HashSet<string> bound,
        List<(string Name, ZType Type)> acc,
        HashSet<string> seen
    )
    {
        switch (node)
        {
            case IrNode.Var v:
                if (!bound.Contains(v.Name) && seen.Add(v.Name))
                    acc.Add((v.Name, v.Type));
                break;

            case IrNode.Let let:
                CollectFree(let.Value, bound, acc, seen);
                CollectFree(let.Body, Bound(bound, let.VarName), acc, seen);
                break;

            case IrNode.Use use:
                CollectFree(use.Value, bound, acc, seen);
                CollectFree(use.Body, Bound(bound, use.VarName), acc, seen);
                break;

            case IrNode.If ifn:
                CollectFree(ifn.Condition, bound, acc, seen);
                CollectFree(ifn.Then, bound, acc, seen);
                CollectFree(ifn.Else, bound, acc, seen);
                break;

            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    CollectFree(n, bound, acc, seen);
                break;

            case IrNode.BinOp bin:
                CollectFree(bin.Left, bound, acc, seen);
                CollectFree(bin.Right, bound, acc, seen);
                break;

            case IrNode.UnaryOp un:
                CollectFree(un.Operand, bound, acc, seen);
                break;

            case IrNode.Call call:
                CollectFree(call.Function, bound, acc, seen);
                foreach (var a in call.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.MethodCall mc:
                CollectFree(mc.Receiver, bound, acc, seen);
                foreach (var a in mc.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.ClrCall cc:
                foreach (var a in cc.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.UnionCaseNew ucn:
                foreach (var a in ucn.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    CollectFree(e, bound, acc, seen);
                break;

            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    CollectFree(e, bound, acc, seen);
                break;

            case IrNode.RecordNew rn:
                foreach (var (_, v) in rn.Fields)
                    CollectFree(v, bound, acc, seen);
                break;

            case IrNode.RecordWith rw:
                CollectFree(rw.Record, bound, acc, seen);
                foreach (var (_, v) in rw.Updates)
                    CollectFree(v, bound, acc, seen);
                break;

            case IrNode.FieldGet fg:
                CollectFree(fg.Record, bound, acc, seen);
                break;

            case IrNode.SetField sf:
                CollectFree(sf.Value, bound, acc, seen);
                break;

            case IrNode.TypeTest tt:
                CollectFree(tt.Value, bound, acc, seen);
                break;

            case IrNode.Throw th:
                CollectFree(th.Expr, bound, acc, seen);
                break;

            case IrNode.Await aw:
                CollectFree(aw.Expr, bound, acc, seen);
                break;

            case IrNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.TcoJump tj:
                foreach (var a in tj.NewArgs)
                    CollectFree(a, bound, acc, seen);
                break;

            case IrNode.Closure cl:
                foreach (var v in cl.CapturedValues)
                    CollectFree(v, bound, acc, seen);
                break;

            case IrNode.Match match:
                CollectFree(match.Scrutinee, bound, acc, seen);
                foreach (var arm in match.Arms)
                    CollectFree(arm.Body, BoundWithPattern(bound, arm.Pattern), acc, seen);
                break;

            case IrNode.WithHandlers wh:
                CollectFree(wh.Body, bound, acc, seen);
                foreach (var h in wh.Handlers)
                    CollectFree(h.HandlerBody, Bound(bound, h.BindingVarName), acc, seen);
                break;

            case IrNode.FuncDef fd:
                CollectFree(fd.Body, Bound(bound, fd.Params), acc, seen);
                break;

            case IrNode.ObjectExpr oe:
                foreach (var m in oe.Methods)
                    CollectFree(m.Body, Bound(bound, m.Params), acc, seen);
                if (oe.Constructor is { } ctor)
                {
                    var ctorScope = Bound(bound, ctor.Params);
                    if (ctor.SuperArgs is not null)
                        foreach (var a in ctor.SuperArgs)
                            CollectFree(a, ctorScope, acc, seen);
                    foreach (var (_, v) in ctor.FieldSets)
                        CollectFree(v, ctorScope, acc, seen);
                    foreach (var e in ctor.BodyExprs)
                        CollectFree(e, ctorScope, acc, seen);
                }

                break;
        }
    }

    private static HashSet<string> Bound(IEnumerable<string> names)
    {
        return [.. names];
    }

    private static HashSet<string> Bound(HashSet<string> bound, string name)
    {
        return new HashSet<string>(bound) { name };
    }

    private static HashSet<string> Bound(HashSet<string> bound, IEnumerable<string> names)
    {
        var result = new HashSet<string>(bound);
        foreach (var n in names)
            result.Add(n);
        return result;
    }

    private static HashSet<string> Bound(HashSet<string> bound, IEnumerable<IrParam> ps)
    {
        var result = new HashSet<string>(bound);
        foreach (var p in ps)
            result.Add(p.Name);
        return result;
    }

    private static HashSet<string> Bound(IEnumerable<IrParam> ps)
    {
        return [.. ps.Select(p => p.Name)];
    }

    private static HashSet<string> Bound(IEnumerable<string> names, IEnumerable<IrParam> ps)
    {
        var result = new HashSet<string>(names);
        foreach (var p in ps)
            result.Add(p.Name);
        return result;
    }

    private static HashSet<string> BoundWithPattern(HashSet<string> bound, IrPattern pattern)
    {
        var result = new HashSet<string>(bound);
        AddPatternBindings(pattern, result);
        return result;
    }

    private static void AddPatternBindings(IrPattern pattern, HashSet<string> bindings)
    {
        switch (pattern)
        {
            case IrPattern.Variable v:
                bindings.Add(v.Name);
                break;
            case IrPattern.Constructor c:
                foreach (var f in c.Fields)
                    AddPatternBindings(f, bindings);
                break;
            case IrPattern.Tuple t:
                foreach (var e in t.Elements)
                    AddPatternBindings(e, bindings);
                break;
        }
    }

    // Captured field/param types come from the referencing Var's inferred type. Any leftover
    // inference variable can't be a field type on the non-generic synthesized class, so default
    // it to Int (matching the C# emitter's historical capture-type defaulting).
    private static ZType DefaultFreeTypeVars(ZType t)
    {
        return t switch
        {
            ZType.ZTypeVar => ZType.Int,
            ZType.ZConstrainedVar => ZType.Int,
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) => ZType.Int,
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(DefaultFreeTypeVars).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(DefaultFreeTypeVars).ToList(),
                DefaultFreeTypeVars(ft.Return),
                ft.IsVariadic
            ),
            ZType.ZNullableType { Inner: var inner } => new ZType.ZNullableType(
                DefaultFreeTypeVars(inner)
            ),
            _ => t,
        };
    }

    private static bool IsUnresolvedTypeVariable(string name)
    {
        return name.Length == 1 && char.IsLower(name[0]);
    }
}

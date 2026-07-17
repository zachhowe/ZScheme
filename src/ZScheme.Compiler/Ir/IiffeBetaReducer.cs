namespace ZScheme.Compiler.Ir;

/// <summary>
///     Beta-reduces immediately-invoked function expressions (IIFEs) into <c>let</c> bindings.
///     A <c>Call</c> whose function operand is a <c>FuncDef</c> literal — i.e. a lambda that is
///     created and immediately applied, like <c>((lambda (x y) body) a b)</c> — is rewritten into
///     a nested <c>Let</c> spine <c>(let x a (let y b body))</c>, which is semantically identical
///     under call-by-value and lets both backends emit plain locals / inline statements instead of
///     allocating a delegate and invoking it on the spot.
///
///     A <c>FuncDef</c> that is used as a first-class value (passed, stored, or returned) is never
///     the <c>Call.Function</c> operand, so it is left untouched and still emits as a real
///     <c>Func&lt;&gt;</c>/<c>Action&lt;&gt;</c> delegate.
/// </summary>
public sealed class IiffeBetaReducer
{
    public IrNode Reduce(IrNode node)
    {
        return Rewrite(node);
    }

    private IrNode Rewrite(IrNode node)
    {
        switch (node)
        {
            case IrNode.IntConst
            or IrNode.FloatConst
            or IrNode.BoolConst
            or IrNode.StringConst
            or IrNode.SymbolConst
            or IrNode.UnitConst
            or IrNode.NullConst
            or IrNode.Var
            or IrNode.TypeOf
            or IrNode.RecordDecl
            or IrNode.TypeAliasDecl
            or IrNode.UnionDecl
            or IrNode.InterfaceDecl:
                return node;

            case IrNode.Call call:
            {
                var fn = Rewrite(call.Function);
                var args = call.Args.Select(Rewrite).ToList();
                if (fn is IrNode.FuncDef fd && CanBetaReduce(fd, args))
                    return BuildLetSpine(fd, args, call.Type);
                return new IrNode.Call(fn, args)
                {
                    Type = call.Type,
                    Span = call.Span,
                };
            }

            case IrNode.Let let:
                return new IrNode.Let(
                    let.VarName,
                    Rewrite(let.Value),
                    Rewrite(let.Body),
                    let.VarType
                )
                {
                    Type = let.Type,
                    Span = let.Span,
                };

            case IrNode.Use use:
                return new IrNode.Use(
                    use.VarName,
                    Rewrite(use.Value),
                    Rewrite(use.Body),
                    use.VarType
                )
                {
                    Type = use.Type,
                    Span = use.Span,
                };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition),
                    Rewrite(ifNode.Then),
                    Rewrite(ifNode.Else)
                )
                {
                    Type = ifNode.Type,
                    Span = ifNode.Span,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList())
                {
                    Type = seq.Type,
                    Span = seq.Span,
                };

            case IrNode.BinOp binop:
                return new IrNode.BinOp(binop.Op, Rewrite(binop.Left), Rewrite(binop.Right))
                {
                    Type = binop.Type,
                    Span = binop.Span,
                };

            case IrNode.UnaryOp unary:
                return new IrNode.UnaryOp(unary.Op, Rewrite(unary.Operand))
                {
                    Type = unary.Type,
                    Span = unary.Span,
                };

            case IrNode.FuncDef fd:
                return fd with { Body = Rewrite(fd.Body) };

            case IrNode.Closure closure:
                return new IrNode.Closure(
                    closure.LiftedFuncName,
                    closure.CapturedValues.Select(Rewrite).ToList()
                )
                {
                    Type = closure.Type,
                    Span = closure.Span,
                };

            case IrNode.MethodCall mc:
                return mc with
                {
                    Receiver = Rewrite(mc.Receiver),
                    Args = mc.Args.Select(Rewrite).ToList(),
                };

            case IrNode.ClrNew cn:
                return new IrNode.ClrNew(
                    cn.QualifiedTypeName,
                    cn.TypeArgs,
                    cn.Args.Select(Rewrite).ToList()
                )
                {
                    Type = cn.Type,
                    Span = cn.Span,
                };

            case IrNode.ClrCall cc:
                return new IrNode.ClrCall(
                    cc.QualifiedTypeName,
                    cc.MethodName,
                    cc.Args.Select(Rewrite).ToList(),
                    cc.GenericArity,
                    cc.GenericTypeArgs,
                    cc.OutParams,
                    cc.ResolvedMethodInfo
                )
                {
                    Type = cc.Type,
                    Span = cc.Span,
                };

            case IrNode.TupleNew tn:
                return new IrNode.TupleNew(tn.Elements.Select(Rewrite).ToList())
                {
                    Type = tn.Type,
                    Span = tn.Span,
                };

            case IrNode.UnionCaseNew ucn:
                return new IrNode.UnionCaseNew(
                    ucn.UnionName,
                    ucn.CaseName,
                    ucn.Args.Select(Rewrite).ToList()
                )
                {
                    Type = ucn.Type,
                    Span = ucn.Span,
                };

            case IrNode.RecordNew rn:
                return new IrNode.RecordNew(
                    rn.TypeName,
                    rn.Fields.Select(f => (f.FieldName, Rewrite(f.Value))).ToList()
                )
                {
                    Type = rn.Type,
                    Span = rn.Span,
                };

            case IrNode.RecordWith rw:
                return new IrNode.RecordWith(
                    rw.TypeName,
                    Rewrite(rw.Record),
                    rw.Updates.Select(u => (u.FieldName, Rewrite(u.Value))).ToList()
                )
                {
                    Type = rw.Type,
                    Span = rw.Span,
                };

            case IrNode.MutableArrayNew man:
                return new IrNode.MutableArrayNew(
                    man.ElementType,
                    man.Elements.Select(Rewrite).ToList()
                )
                {
                    Type = man.Type,
                    Span = man.Span,
                };

            case IrNode.FieldGet fg:
                return new IrNode.FieldGet(Rewrite(fg.Record), fg.FieldName)
                {
                    Type = fg.Type,
                    Span = fg.Span,
                };

            case IrNode.Match match:
                return new IrNode.Match(
                    Rewrite(match.Scrutinee),
                    match.Arms.Select(a => new IrMatchArm(a.Pattern, Rewrite(a.Body))).ToList()
                )
                {
                    Type = match.Type,
                    Span = match.Span,
                };

            case IrNode.Throw thr:
                return new IrNode.Throw(Rewrite(thr.Expr))
                {
                    Type = thr.Type,
                    Span = thr.Span,
                };

            case IrNode.Await aw:
                return new IrNode.Await(Rewrite(aw.Expr))
                {
                    Type = aw.Type,
                    Span = aw.Span,
                };

            case IrNode.SetField sf:
                return new IrNode.SetField(sf.FieldName, Rewrite(sf.Value))
                {
                    Type = sf.Type,
                    Span = sf.Span,
                };

            case IrNode.SuperMethodCall smc:
                return new IrNode.SuperMethodCall(smc.MethodName, smc.Args.Select(Rewrite).ToList())
                {
                    Type = smc.Type,
                    Span = smc.Span,
                };

            case IrNode.WithHandlers wh:
                return new IrNode.WithHandlers(
                    Rewrite(wh.Body),
                    wh.Handlers.Select(h => new IrHandlerClause(
                            h.ExceptionTypeName,
                            h.BindingVarName,
                            Rewrite(h.HandlerBody)
                        ))
                        .ToList()
                )
                {
                    Type = wh.Type,
                    Span = wh.Span,
                };

            case IrNode.ClassDecl cd:
                return cd with
                {
                    Methods = cd.Methods.Select(m => m with { Body = Rewrite(m.Body) }).ToList(),
                    Constructor = RewriteConstructor(cd.Constructor),
                };

            default:
                return node;
        }
    }

    private IrConstructor? RewriteConstructor(IrConstructor? ctor)
    {
        if (ctor is null)
            return null;
        return ctor with
        {
            BodyExprs = ctor.BodyExprs.Select(Rewrite).ToList(),
            FieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Rewrite(fs.Value))).ToList(),
            SuperArgs = ctor.SuperArgs?.Select(Rewrite).ToList(),
        };
    }

    private static bool CanBetaReduce(IrNode.FuncDef f, IReadOnlyList<IrNode> args)
    {
        // A self-recursive lambda references its own name in its body; inlining would
        // drop that binding. (Anonymous lambdas are never self-recursive, but guard anyway.)
        if (f.IsSelfRecursive)
            return false;
        // An async lambda's body may contain awaits; inlining into a non-async context breaks.
        if (f.IsAsync)
            return false;
        // A generic lambda used monomorphically cannot be expressed as a let local.
        if (f.TypeParams is { Count: > 0 })
            return false;
        // The user explicitly typed this lambda to a CLR delegate — keep it as a delegate value.
        if (f.ClrDelegateTypeName is not null)
            return false;
        // Partial application / over-application: arity must match exactly.
        if (f.Params.Count != args.Count)
            return false;
        // Variadic params have their args packed into an array by lowering; binding differs.
        if (f.Params.Any(p => p.IsVariadic))
            return false;
        // Name capture: if a param name occurs (even conservatively) in any argument, the
        // nested let spine would wrongly capture it (args evaluate in the outer scope).
        var paramNames = f.Params.Select(p => p.Name).ToHashSet();
        if (args.Any(a => ReferencesAny(a, paramNames)))
            return false;
        return true;
    }

    private IrNode BuildLetSpine(
        IrNode.FuncDef f,
        IReadOnlyList<IrNode> args,
        Types.ZType resultType
    )
    {
        var result = f.Body;
        for (var i = f.Params.Count - 1; i >= 0; i--)
            result = new IrNode.Let(f.Params[i].Name, args[i], result, f.Params[i].Type)
            {
                Type = resultType,
                Span = f.Span,
            };
        return result;
    }

    /// <summary>
    ///     Conservatively reports whether any <c>Var</c> whose name is in <paramref name="names" />
    ///     appears anywhere in <paramref name="node" />. Shadowing is ignored (a match is reported
    ///     even if an inner binder would shadow the name), which only ever over-blocks reduction.
    /// </summary>
    private static bool ReferencesAny(IrNode node, HashSet<string> names)
    {
        switch (node)
        {
            case IrNode.IntConst
            or IrNode.FloatConst
            or IrNode.BoolConst
            or IrNode.StringConst
            or IrNode.SymbolConst
            or IrNode.UnitConst
            or IrNode.NullConst
            or IrNode.TypeOf:
                return false;
            case IrNode.Var v:
                return names.Contains(v.Name);
            case IrNode.Let let:
                return ReferencesAny(let.Value, names) || ReferencesAny(let.Body, names);
            case IrNode.Use use:
                return ReferencesAny(use.Value, names) || ReferencesAny(use.Body, names);
            case IrNode.If i:
                return ReferencesAny(i.Condition, names)
                    || ReferencesAny(i.Then, names)
                    || ReferencesAny(i.Else, names);
            case IrNode.Call c:
                return ReferencesAny(c.Function, names) || c.Args.Any(a => ReferencesAny(a, names));
            case IrNode.BinOp b:
                return ReferencesAny(b.Left, names) || ReferencesAny(b.Right, names);
            case IrNode.UnaryOp u:
                return ReferencesAny(u.Operand, names);
            case IrNode.FuncDef fd:
                return ReferencesAny(fd.Body, names);
            case IrNode.Closure cl:
                return cl.CapturedValues.Any(a => ReferencesAny(a, names));
            case IrNode.MethodCall mc:
                return ReferencesAny(mc.Receiver, names)
                    || mc.Args.Any(a => ReferencesAny(a, names));
            case IrNode.ClrNew cn:
                return cn.Args.Any(a => ReferencesAny(a, names));
            case IrNode.ClrCall cc:
                return cc.Args.Any(a => ReferencesAny(a, names));
            case IrNode.TupleNew tn:
                return tn.Elements.Any(a => ReferencesAny(a, names));
            case IrNode.UnionCaseNew ucn:
                return ucn.Args.Any(a => ReferencesAny(a, names));
            case IrNode.RecordNew rn:
                return rn.Fields.Any(fld => ReferencesAny(fld.Value, names));
            case IrNode.RecordWith rw:
                return ReferencesAny(rw.Record, names)
                    || rw.Updates.Any(up => ReferencesAny(up.Value, names));
            case IrNode.MutableArrayNew man:
                return man.Elements.Any(a => ReferencesAny(a, names));
            case IrNode.FieldGet fg:
                return ReferencesAny(fg.Record, names);
            case IrNode.Match match:
                return ReferencesAny(match.Scrutinee, names)
                    || match.Arms.Any(a => ReferencesAny(a.Body, names));
            case IrNode.Throw th:
                return ReferencesAny(th.Expr, names);
            case IrNode.Await aw:
                return ReferencesAny(aw.Expr, names);
            case IrNode.SetField sf:
                return ReferencesAny(sf.Value, names);
            case IrNode.SuperMethodCall smc:
                return smc.Args.Any(a => ReferencesAny(a, names));
            case IrNode.Seq seq:
                return seq.Nodes.Any(a => ReferencesAny(a, names));
            case IrNode.WithHandlers wh:
                return ReferencesAny(wh.Body, names)
                    || wh.Handlers.Any(h => ReferencesAny(h.HandlerBody, names));
            default:
                // Unknown / declaration node in argument position — be conservative and block.
                return true;
        }
    }
}

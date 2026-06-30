using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     A-normalizes any compound expression that transitively contains a <c>with-handlers</c> node.
///     The IL verifier requires stack depth 0 at try-block entry, so sub-expressions containing
///     try/catch must be evaluated in <c>Let.Value</c> positions rather than as operands left on
///     a non-empty stack.
/// </summary>
public sealed class WithHandlersHoister
{
    private int _counter;

    public IrNode Hoist(IrNode node)
    {
        return Rewrite(node);
    }

    // The reconstruction in RewriteInner copies Type/IsTailCall but not Span, which would erase
    // every node's source provenance. Restore it from the original node so later passes (e.g.
    // coverage instrumentation) can still map IR back to source.
    private IrNode Rewrite(IrNode node)
    {
        var result = RewriteInner(node);
        if (
            !ReferenceEquals(result, node)
            && result.Span == SourceSpan.None
            && node.Span != SourceSpan.None
        )
            result = result with { Span = node.Span };
        return result;
    }

    private IrNode RewriteInner(IrNode node)
    {
        switch (node)
        {
            case IrNode.IntConst
            or IrNode.FloatConst
            or IrNode.BoolConst
            or IrNode.StringConst
            or IrNode.UnitConst
            or IrNode.NullConst
            or IrNode.Var:
                return node;

            case IrNode.WithHandlers wh:
                var whBody = Rewrite(wh.Body);
                var whHandlers = wh
                    .Handlers.Select(h => new IrHandlerClause(
                        h.ExceptionTypeName,
                        h.BindingVarName,
                        Rewrite(h.HandlerBody)
                    ))
                    .ToList();
                return new IrNode.WithHandlers(whBody, whHandlers)
                {
                    Type = wh.Type,
                    IsTailCall = wh.IsTailCall,
                };

            case IrNode.Let let:
                return new IrNode.Let(
                    let.VarName,
                    Rewrite(let.Value),
                    Rewrite(let.Body),
                    let.VarType
                )
                {
                    Type = let.Type,
                    IsTailCall = let.IsTailCall,
                };

            case IrNode.Use use:
                // Like WithHandlers, a 'use' emits an IL try region that requires an
                // empty eval stack at entry, so it is itself a hoist barrier (see
                // ContainsWithHandlers). Just A-normalize its sub-expressions here.
                return new IrNode.Use(
                    use.VarName,
                    Rewrite(use.Value),
                    Rewrite(use.Body),
                    use.VarType
                )
                {
                    Type = use.Type,
                    IsTailCall = use.IsTailCall,
                };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition),
                    Rewrite(ifNode.Then),
                    Rewrite(ifNode.Else)
                )
                {
                    Type = ifNode.Type,
                    IsTailCall = ifNode.IsTailCall,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList())
                {
                    Type = seq.Type,
                    IsTailCall = seq.IsTailCall,
                };

            case IrNode.BinOp binop:
            {
                var l = Rewrite(binop.Left);
                var r = Rewrite(binop.Right);
                // Short-circuit operators must not A-normalize their operands: doing so
                // evaluates the right operand unconditionally (via Let), defeating
                // short-circuit semantics. The IL emitter's EmitShortCircuit evaluates
                // each operand at the BinOp's own stack depth, so any with-handlers in
                // an operand is fine as long as the BinOp itself is at stack depth 0 —
                // and that's the parent expression's responsibility (it'll hoist the
                // whole BinOp into a Let when it appears in a non-zero-stack position,
                // because ContainsWithHandlers walks into BinOp operands).
                // Short-circuit operators must not A-normalize their operands: doing so
                // evaluates the right operand unconditionally (via Let), defeating
                // short-circuit semantics. The IL emitter's EmitShortCircuit evaluates
                // each operand at the BinOp's own stack depth, so any with-handlers in
                // an operand is fine as long as the BinOp itself is at stack depth 0 —
                // and that's the parent expression's responsibility. Parents already
                // see this BinOp via ContainsWithHandlers (which walks into operands)
                // and will A-normalize the whole BinOp into a Let when it appears in a
                // non-zero-stack position.
                if (binop.Op is "and" or "or")
                    return new IrNode.BinOp(binop.Op, l, r)
                    {
                        Type = binop.Type,
                        IsTailCall = binop.IsTailCall,
                    };
                if (!ContainsWithHandlers(l) && !ContainsWithHandlers(r))
                    return new IrNode.BinOp(binop.Op, l, r)
                    {
                        Type = binop.Type,
                        IsTailCall = binop.IsTailCall,
                    };
                return Anf(
                    [l, r],
                    vars => new IrNode.BinOp(binop.Op, vars[0], vars[1])
                    {
                        Type = binop.Type,
                        IsTailCall = binop.IsTailCall,
                    }
                );
            }

            case IrNode.UnaryOp unary:
            {
                var operand = Rewrite(unary.Operand);
                if (!ContainsWithHandlers(operand))
                    return new IrNode.UnaryOp(unary.Op, operand)
                    {
                        Type = unary.Type,
                        IsTailCall = unary.IsTailCall,
                    };
                return Anf(
                    [operand],
                    vars => new IrNode.UnaryOp(unary.Op, vars[0])
                    {
                        Type = unary.Type,
                        IsTailCall = unary.IsTailCall,
                    }
                );
            }

            case IrNode.Call call:
            {
                // Keep Call.Function as-is when it's a Var — EmitCall relies on direct-name
                // resolution (_methods, precompiled, etc.). A Var can never contain WithHandlers.
                var fn = call.Function is IrNode.Var ? call.Function : Rewrite(call.Function);
                var args = call.Args.Select(Rewrite).ToList();
                if (!ContainsWithHandlers(fn) && !args.Any(ContainsWithHandlers))
                    return new IrNode.Call(fn, args)
                    {
                        Type = call.Type,
                        IsTailCall = call.IsTailCall,
                    };
                if (fn is IrNode.Var)
                    // Only A-normalize the args; leave the Var function reference untouched.
                    return Anf(
                        args,
                        vars => new IrNode.Call(fn, vars)
                        {
                            Type = call.Type,
                            IsTailCall = call.IsTailCall,
                        }
                    );

                var all = new List<IrNode> { fn };
                all.AddRange(args);
                return Anf(
                    all,
                    vars => new IrNode.Call(vars[0], vars.Skip(1).ToList())
                    {
                        Type = call.Type,
                        IsTailCall = call.IsTailCall,
                    }
                );
            }

            case IrNode.MethodCall mc:
            {
                var receiver = Rewrite(mc.Receiver);
                var mcArgs = mc.Args.Select(Rewrite).ToList();
                if (!ContainsWithHandlers(receiver) && !mcArgs.Any(ContainsWithHandlers))
                    return mc with { Receiver = receiver, Args = mcArgs };
                var mcAll = new List<IrNode> { receiver };
                mcAll.AddRange(mcArgs);
                return Anf(
                    mcAll,
                    vars => mc with { Receiver = vars[0], Args = vars.Skip(1).ToList() }
                );
            }

            case IrNode.ClrNew cn:
            {
                var cnArgs = cn.Args.Select(Rewrite).ToList();
                if (!cnArgs.Any(ContainsWithHandlers))
                    return new IrNode.ClrNew(cn.QualifiedTypeName, cn.TypeArgs, cnArgs)
                    {
                        Type = cn.Type,
                        IsTailCall = cn.IsTailCall,
                    };
                return Anf(
                    cnArgs,
                    vars => new IrNode.ClrNew(cn.QualifiedTypeName, cn.TypeArgs, vars)
                    {
                        Type = cn.Type,
                        IsTailCall = cn.IsTailCall,
                    }
                );
            }

            case IrNode.ClrCall cc:
            {
                var ccArgs = cc.Args.Select(Rewrite).ToList();
                if (!ccArgs.Any(ContainsWithHandlers))
                    return new IrNode.ClrCall(
                        cc.QualifiedTypeName,
                        cc.MethodName,
                        ccArgs,
                        cc.GenericArity,
                        cc.GenericTypeArgs,
                        cc.OutParams,
                        cc.ResolvedMethodInfo
                    )
                    {
                        Type = cc.Type,
                        IsTailCall = cc.IsTailCall,
                    };
                return Anf(
                    ccArgs,
                    vars => new IrNode.ClrCall(
                        cc.QualifiedTypeName,
                        cc.MethodName,
                        vars,
                        cc.GenericArity,
                        cc.GenericTypeArgs,
                        cc.OutParams,
                        cc.ResolvedMethodInfo
                    )
                    {
                        Type = cc.Type,
                        IsTailCall = cc.IsTailCall,
                    }
                );
            }

            case IrNode.TupleNew tn:
            {
                var tnEls = tn.Elements.Select(Rewrite).ToList();
                if (!tnEls.Any(ContainsWithHandlers))
                    return new IrNode.TupleNew(tnEls)
                    {
                        Type = tn.Type,
                        IsTailCall = tn.IsTailCall,
                    };
                return Anf(
                    tnEls,
                    vars => new IrNode.TupleNew(vars) { Type = tn.Type, IsTailCall = tn.IsTailCall }
                );
            }

            case IrNode.UnionCaseNew ucn:
            {
                var ucnArgs = ucn.Args.Select(Rewrite).ToList();
                if (!ucnArgs.Any(ContainsWithHandlers))
                    return new IrNode.UnionCaseNew(ucn.UnionName, ucn.CaseName, ucnArgs)
                    {
                        Type = ucn.Type,
                        IsTailCall = ucn.IsTailCall,
                    };
                return Anf(
                    ucnArgs,
                    vars => new IrNode.UnionCaseNew(ucn.UnionName, ucn.CaseName, vars)
                    {
                        Type = ucn.Type,
                        IsTailCall = ucn.IsTailCall,
                    }
                );
            }

            case IrNode.RecordNew rn:
            {
                var rnFields = rn
                    .Fields.Select(f => (f.FieldName, Value: Rewrite(f.Value)))
                    .ToList();
                if (!rnFields.Any(f => ContainsWithHandlers(f.Value)))
                    return new IrNode.RecordNew(
                        rn.TypeName,
                        rnFields.Select(f => (f.FieldName, f.Value)).ToList()
                    )
                    {
                        Type = rn.Type,
                        IsTailCall = rn.IsTailCall,
                    };
                var rnValues = rnFields.Select(f => f.Value).ToList();
                return Anf(
                    rnValues,
                    vars =>
                    {
                        var newFields = rnFields
                            .Zip(vars, (f, v) => (f.FieldName, Value: v))
                            .ToList();
                        return new IrNode.RecordNew(rn.TypeName, newFields)
                        {
                            Type = rn.Type,
                            IsTailCall = rn.IsTailCall,
                        };
                    }
                );
            }

            case IrNode.RecordWith rw:
            {
                var rec = Rewrite(rw.Record);
                var updates = rw
                    .Updates.Select(u => (u.FieldName, Value: Rewrite(u.Value)))
                    .ToList();
                if (!ContainsWithHandlers(rec) && !updates.Any(u => ContainsWithHandlers(u.Value)))
                    return new IrNode.RecordWith(
                        rw.TypeName,
                        rec,
                        updates.Select(u => (u.FieldName, u.Value)).ToList()
                    )
                    {
                        Type = rw.Type,
                        IsTailCall = rw.IsTailCall,
                    };
                var rwAll = new List<IrNode> { rec };
                rwAll.AddRange(updates.Select(u => u.Value));
                return Anf(
                    rwAll,
                    vars =>
                    {
                        var newUpdates = updates
                            .Zip(vars.Skip(1), (u, v) => (u.FieldName, Value: v))
                            .ToList();
                        return new IrNode.RecordWith(rw.TypeName, vars[0], newUpdates)
                        {
                            Type = rw.Type,
                            IsTailCall = rw.IsTailCall,
                        };
                    }
                );
            }

            case IrNode.MutableArrayNew man:
            {
                var elements = man.Elements.Select(Rewrite).ToList();
                if (!elements.Any(ContainsWithHandlers))
                    return new IrNode.MutableArrayNew(man.ElementType, elements)
                    {
                        Type = man.Type,
                        IsTailCall = man.IsTailCall,
                    };
                return Anf(
                    elements,
                    vars => new IrNode.MutableArrayNew(man.ElementType, vars)
                    {
                        Type = man.Type,
                        IsTailCall = man.IsTailCall,
                    }
                );
            }

            case IrNode.Match match:
            {
                var scrutinee = Rewrite(match.Scrutinee);
                var arms = match
                    .Arms.Select(a => new IrMatchArm(a.Pattern, Rewrite(a.Body)))
                    .ToList();
                if (!ContainsWithHandlers(scrutinee))
                    return new IrNode.Match(scrutinee, arms)
                    {
                        Type = match.Type,
                        IsTailCall = match.IsTailCall,
                    };
                return Anf(
                    [scrutinee],
                    vars => new IrNode.Match(vars[0], arms)
                    {
                        Type = match.Type,
                        IsTailCall = match.IsTailCall,
                    }
                );
            }

            case IrNode.Throw thr:
            {
                var expr = Rewrite(thr.Expr);
                if (!ContainsWithHandlers(expr))
                    return new IrNode.Throw(expr) { Type = thr.Type, IsTailCall = thr.IsTailCall };
                return Anf(
                    [expr],
                    vars => new IrNode.Throw(vars[0])
                    {
                        Type = thr.Type,
                        IsTailCall = thr.IsTailCall,
                    }
                );
            }

            case IrNode.Await aw:
            {
                var expr = Rewrite(aw.Expr);
                if (!ContainsWithHandlers(expr))
                    return new IrNode.Await(expr) { Type = aw.Type, IsTailCall = aw.IsTailCall };
                return Anf(
                    [expr],
                    vars => new IrNode.Await(vars[0]) { Type = aw.Type, IsTailCall = aw.IsTailCall }
                );
            }

            case IrNode.SetField sf:
            {
                var val = Rewrite(sf.Value);
                if (!ContainsWithHandlers(val))
                    return new IrNode.SetField(sf.FieldName, val)
                    {
                        Type = sf.Type,
                        IsTailCall = sf.IsTailCall,
                    };
                return Anf(
                    [val],
                    vars => new IrNode.SetField(sf.FieldName, vars[0])
                    {
                        Type = sf.Type,
                        IsTailCall = sf.IsTailCall,
                    }
                );
            }

            case IrNode.TypeTest tt:
            {
                var val = Rewrite(tt.Value);
                if (!ContainsWithHandlers(val))
                    return new IrNode.TypeTest(val, tt.TypeName, tt.BindVar)
                    {
                        Type = tt.Type,
                        IsTailCall = tt.IsTailCall,
                    };
                return Anf(
                    [val],
                    vars => new IrNode.TypeTest(vars[0], tt.TypeName, tt.BindVar)
                    {
                        Type = tt.Type,
                        IsTailCall = tt.IsTailCall,
                    }
                );
            }

            case IrNode.FieldGet fg:
            {
                var rec = Rewrite(fg.Record);
                if (!ContainsWithHandlers(rec))
                    return new IrNode.FieldGet(rec, fg.FieldName)
                    {
                        Type = fg.Type,
                        IsTailCall = fg.IsTailCall,
                    };
                return Anf(
                    [rec],
                    vars => new IrNode.FieldGet(vars[0], fg.FieldName)
                    {
                        Type = fg.Type,
                        IsTailCall = fg.IsTailCall,
                    }
                );
            }

            case IrNode.SuperMethodCall smc:
            {
                var smcArgs = smc.Args.Select(Rewrite).ToList();
                if (!smcArgs.Any(ContainsWithHandlers))
                    return new IrNode.SuperMethodCall(smc.MethodName, smcArgs)
                    {
                        Type = smc.Type,
                        IsTailCall = smc.IsTailCall,
                    };
                return Anf(
                    smcArgs,
                    vars => new IrNode.SuperMethodCall(smc.MethodName, vars)
                    {
                        Type = smc.Type,
                        IsTailCall = smc.IsTailCall,
                    }
                );
            }

            case IrNode.FuncDef fd:
                return fd with { Body = Rewrite(fd.Body) };

            case IrNode.ClassDecl cd:
                return cd with
                {
                    Methods = cd.Methods.Select(m => m with { Body = Rewrite(m.Body) }).ToList(),
                    Constructor = cd.Constructor is null
                        ? null
                        : cd.Constructor with
                        {
                            BodyExprs = cd.Constructor.BodyExprs.Select(Rewrite).ToList(),
                            FieldSets = cd
                                .Constructor.FieldSets.Select(fs =>
                                    (fs.FieldName, Rewrite(fs.Value))
                                )
                                .ToList(),
                            SuperArgs = cd.Constructor.SuperArgs?.Select(Rewrite).ToList(),
                        },
                };

            default:
                return node;
        }
    }

    private IrNode Anf(IReadOnlyList<IrNode> args, Func<List<IrNode>, IrNode> builder)
    {
        var names = args.Select(_ => $"__wh_hoist_{_counter++}").ToList();
        var vars = args.Zip(names, (a, n) => (IrNode)new IrNode.Var(n) { Type = a.Type }).ToList();
        var result = builder(vars);
        for (var i = args.Count - 1; i >= 0; i--)
            result = new IrNode.Let(names[i], args[i], result) { Type = result.Type };
        return result;
    }

    public static bool ContainsWithHandlers(IrNode node)
    {
        switch (node)
        {
            case IrNode.WithHandlers:
                return true;
            case IrNode.Use:
                // A 'use' lowers to an IL try/finally region, which (like with-handlers)
                // must be entered at stack depth 0 — so treat it as a hoist barrier.
                return true;
            case IrNode.Let let:
                return ContainsWithHandlers(let.Value) || ContainsWithHandlers(let.Body);
            case IrNode.If i:
                return ContainsWithHandlers(i.Condition)
                    || ContainsWithHandlers(i.Then)
                    || ContainsWithHandlers(i.Else);
            case IrNode.BinOp b:
                return ContainsWithHandlers(b.Left) || ContainsWithHandlers(b.Right);
            case IrNode.UnaryOp u:
                return ContainsWithHandlers(u.Operand);
            case IrNode.Call c:
                return ContainsWithHandlers(c.Function) || c.Args.Any(ContainsWithHandlers);
            case IrNode.MethodCall mc:
                return ContainsWithHandlers(mc.Receiver) || mc.Args.Any(ContainsWithHandlers);
            case IrNode.ClrNew cn:
                return cn.Args.Any(ContainsWithHandlers);
            case IrNode.ClrCall cc:
                return cc.Args.Any(ContainsWithHandlers);
            case IrNode.TupleNew tn:
                return tn.Elements.Any(ContainsWithHandlers);
            case IrNode.UnionCaseNew ucn:
                return ucn.Args.Any(ContainsWithHandlers);
            case IrNode.RecordNew rn:
                return rn.Fields.Any(f => ContainsWithHandlers(f.Value));
            case IrNode.RecordWith rw:
                return ContainsWithHandlers(rw.Record)
                    || rw.Updates.Any(u => ContainsWithHandlers(u.Value));
            case IrNode.MutableArrayNew man:
                return man.Elements.Any(ContainsWithHandlers);
            case IrNode.Match match:
                return ContainsWithHandlers(match.Scrutinee)
                    || match.Arms.Any(a => ContainsWithHandlers(a.Body));
            case IrNode.Throw th:
                return ContainsWithHandlers(th.Expr);
            case IrNode.Await aw:
                return ContainsWithHandlers(aw.Expr);
            case IrNode.SetField sf:
                return ContainsWithHandlers(sf.Value);
            case IrNode.TypeTest tt:
                return ContainsWithHandlers(tt.Value);
            case IrNode.FieldGet fg:
                return ContainsWithHandlers(fg.Record);
            case IrNode.Seq seq:
                return seq.Nodes.Any(ContainsWithHandlers);
            case IrNode.SuperMethodCall smc:
                return smc.Args.Any(ContainsWithHandlers);
            case IrNode.FuncDef fd:
                return ContainsWithHandlers(fd.Body);
            default:
                return false;
        }
    }
}

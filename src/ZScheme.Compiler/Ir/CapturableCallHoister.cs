using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     A-normalizes any value-consuming sub-expression that transitively contains a
///     continuation-capturing runtime call (CallCcTyped, ShiftTyped, ControlTyped, CallCompTyped,
///     Reset, and their tagged variants). After this pass, every such call appears as the
///     immediate <c>Value</c> of a <c>Let</c> binding, which is the only shape
///     <see cref="ContinuationTransform"/> can wrap with frame-synthesizing handlers.
///
///     Without this pass, a call appearing as a sub-expression of a BinOp / Call arg /
///     constructor field / etc. silently has its surrounding context dropped — invoking the
///     captured continuation would skip the post-call computation that consumes the call's value.
///
///     Branches of <c>If</c> and <c>Match</c>, and the right operand of short-circuit
///     <c>and</c>/<c>or</c>, are NOT hoisted: those positions are conditionally evaluated, and
///     hoisting would force unconditional evaluation. <see cref="ContinuationTransform"/>
///     handles the let-bound-If/Match shape directly.
/// </summary>
public sealed class CapturableCallHoister
{
    private int _counter;

    public IrNode Hoist(IrNode node) => Rewrite(node);

    private IrNode Rewrite(IrNode node)
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

            case IrNode.If ifNode:
            {
                var cond = Rewrite(ifNode.Condition);
                var then = Rewrite(ifNode.Then);
                var els = Rewrite(ifNode.Else);
                // The if-condition is a value-consuming position: the condition's value is
                // turned into a branching decision and is NOT the if's value. Once the
                // condition is hoisted into a Let, that Let must float OUT of the if so the
                // call/cc-bearing Let-value is at the if's parent level. Otherwise the inner
                // wrap throws SaveContinuation with a frame typed at the call/cc's result
                // type, but the if's value type is different — the chain breaks.
                if (ContainsCapturable(cond))
                    return Anf(
                        [cond],
                        vars => new IrNode.If(vars[0], then, els)
                        {
                            Type = ifNode.Type,
                            IsTailCall = ifNode.IsTailCall,
                        }
                    );
                return new IrNode.If(cond, then, els)
                {
                    Type = ifNode.Type,
                    IsTailCall = ifNode.IsTailCall,
                };
            }

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList())
                {
                    Type = seq.Type,
                    IsTailCall = seq.IsTailCall,
                };

            case IrNode.WithHandlers wh:
                var whHandlers = wh
                    .Handlers.Select(h => new IrHandlerClause(
                        h.ExceptionTypeName,
                        h.BindingVarName,
                        Rewrite(h.HandlerBody)
                    ))
                    .ToList();
                return new IrNode.WithHandlers(Rewrite(wh.Body), whHandlers)
                {
                    Type = wh.Type,
                    IsTailCall = wh.IsTailCall,
                };

            case IrNode.BinOp binop:
            {
                var l = Rewrite(binop.Left);
                var r = Rewrite(binop.Right);
                // Short-circuit operators must not have their operands hoisted into Let-bindings:
                // doing so unconditionally evaluates the right operand, defeating short-circuit
                // semantics. We accept that capture through `and`/`or` operands remains a minor
                // limitation — users can manually let-bind, same as before.
                if (binop.Op is "and" or "or")
                    return new IrNode.BinOp(binop.Op, l, r)
                    {
                        Type = binop.Type,
                        IsTailCall = binop.IsTailCall,
                    };
                if (!ContainsCapturable(l) && !ContainsCapturable(r))
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
                if (!ContainsCapturable(operand))
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
                // Keep Call.Function untouched when it's a Var — direct-name resolution depends
                // on it. A Var can never contain a capturable runtime call.
                var fn = call.Function is IrNode.Var ? call.Function : Rewrite(call.Function);
                var args = call.Args.Select(Rewrite).ToList();
                if (!ContainsCapturable(fn) && !args.Any(ContainsCapturable))
                    return call with { Function = fn, Args = args };
                if (fn is IrNode.Var)
                    return Anf(args, vars => call with { Function = fn, Args = vars });
                var all = new List<IrNode> { fn };
                all.AddRange(args);
                return Anf(
                    all,
                    vars => call with { Function = vars[0], Args = vars.Skip(1).ToList() }
                );
            }

            case IrNode.MethodCall mc:
            {
                var receiver = Rewrite(mc.Receiver);
                var mcArgs = mc.Args.Select(Rewrite).ToList();
                // `with`, so the instance overload IR lowering resolved (ResolvedMethodInfo)
                // survives the rewrite — the backends read it instead of re-resolving.
                if (!ContainsCapturable(receiver) && !mcArgs.Any(ContainsCapturable))
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
                if (!cnArgs.Any(ContainsCapturable))
                    return cn with { Args = cnArgs };
                return Anf(
                    cnArgs,
                    vars => cn with
                    {
                        Args = vars,
                    }
                );
            }

            case IrNode.ClrCall cc:
            {
                // `with` rather than a fresh ClrCall: only the args change, and rebuilding
                // by hand would drop the overload IR lowering already resolved
                // (ResolvedMethodInfo) — the backends no longer re-resolve it themselves.
                var ccArgs = cc.Args.Select(Rewrite).ToList();
                if (!ccArgs.Any(ContainsCapturable))
                    return cc with { Args = ccArgs };
                return Anf(ccArgs, vars => cc with { Args = vars });
            }

            case IrNode.TupleNew tn:
            {
                var tnEls = tn.Elements.Select(Rewrite).ToList();
                if (!tnEls.Any(ContainsCapturable))
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
                if (!ucnArgs.Any(ContainsCapturable))
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
                if (!rnFields.Any(f => ContainsCapturable(f.Value)))
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
                if (!ContainsCapturable(rec) && !updates.Any(u => ContainsCapturable(u.Value)))
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
                if (!elements.Any(ContainsCapturable))
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
                // Scrutinee is value-consumed (decision-tree compilation reads it); arms are
                // conditionally evaluated and must NOT be hoisted.
                var scrutinee = Rewrite(match.Scrutinee);
                var arms = match
                    .Arms.Select(a => new IrMatchArm(a.Pattern, Rewrite(a.Body)))
                    .ToList();
                if (!ContainsCapturable(scrutinee))
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
                if (!ContainsCapturable(expr))
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
                if (!ContainsCapturable(expr))
                    return new IrNode.Await(expr) { Type = aw.Type, IsTailCall = aw.IsTailCall };
                return Anf(
                    [expr],
                    vars => new IrNode.Await(vars[0]) { Type = aw.Type, IsTailCall = aw.IsTailCall }
                );
            }

            case IrNode.SetField sf:
            {
                var val = Rewrite(sf.Value);
                if (!ContainsCapturable(val))
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

            case IrNode.FieldGet fg:
            {
                var rec = Rewrite(fg.Record);
                if (!ContainsCapturable(rec))
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

            case IrNode.Cast cast:
            {
                var expr = Rewrite(cast.Expr);
                if (!ContainsCapturable(expr))
                    return new IrNode.Cast(expr, cast.TargetType)
                    {
                        Type = cast.Type,
                        IsTailCall = cast.IsTailCall,
                    };
                return Anf(
                    [expr],
                    vars => new IrNode.Cast(vars[0], cast.TargetType)
                    {
                        Type = cast.Type,
                        IsTailCall = cast.IsTailCall,
                    }
                );
            }

            case IrNode.SuperMethodCall smc:
            {
                var smcArgs = smc.Args.Select(Rewrite).ToList();
                if (!smcArgs.Any(ContainsCapturable))
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

            case IrNode.ObjectExpr oe:
                return oe with
                {
                    Methods = oe.Methods.Select(m => m with { Body = Rewrite(m.Body) }).ToList(),
                    Constructor = oe.Constructor is null
                        ? null
                        : oe.Constructor with
                        {
                            BodyExprs = oe.Constructor.BodyExprs.Select(Rewrite).ToList(),
                            FieldSets = oe
                                .Constructor.FieldSets.Select(fs =>
                                    (fs.FieldName, Rewrite(fs.Value))
                                )
                                .ToList(),
                            SuperArgs = oe.Constructor.SuperArgs?.Select(Rewrite).ToList(),
                        },
                };

            default:
                return node;
        }
    }

    private IrNode Anf(IReadOnlyList<IrNode> args, Func<List<IrNode>, IrNode> builder)
    {
        // Bind every non-trivial operand so left-to-right evaluation order is preserved when
        // a capturable call is hoisted out. If we kept a non-trivial earlier operand inline
        // and let-bound only the capturable later one, the let chain would evaluate the
        // capturable BEFORE the earlier operand — flipping order for any side-effecting
        // earlier operand. Trivial leaf nodes (Var, literal) have no side effects, so we
        // leave those inline to keep the IR small.
        var bindings = new List<(string Name, IrNode Value)>();
        var operands = new List<IrNode>();
        foreach (var arg in args)
        {
            if (IsTrivial(arg))
            {
                operands.Add(arg);
            }
            else
            {
                var name = $"__cc_hoist_{_counter++}";
                bindings.Add((name, arg));
                operands.Add(new IrNode.Var(name) { Type = arg.Type });
            }
        }
        var result = builder(operands);
        for (var i = bindings.Count - 1; i >= 0; i--)
            result = new IrNode.Let(bindings[i].Name, bindings[i].Value, result)
            {
                Type = result.Type,
            };
        return result;
    }

    private static bool IsTrivial(IrNode node) =>
        node
            is IrNode.Var
                or IrNode.IntConst
                or IrNode.FloatConst
                or IrNode.BoolConst
                or IrNode.StringConst
                or IrNode.UnitConst
                or IrNode.NullConst;

    /// <summary>
    ///     True if <paramref name="node"/> transitively contains a continuation-capturing runtime
    ///     call. Reuses the detection list from <see cref="ContinuationTransform.ContainsCallCc"/>.
    /// </summary>
    public static bool ContainsCapturable(IrNode node) =>
        ContinuationTransform.ContainsCallCc(node);
}

using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     A-normalizes any compound expression that transitively contains an <c>await</c>.
///     The IL state-machine lowering suspends a fiber by emitting a <c>Leave</c> out of the
///     try block and resuming via a switch-table jump back to a label inside MoveNext. For
///     that resume label to share a stack height with the IsCompleted=true fall-through path,
///     the await must be emitted with an empty evaluation stack — otherwise operands left on
///     the stack from a surrounding expression (e.g. <c>(h0 a (await (g0 b)))</c>) cause
///     <c>StackImbalanceException</c> at PE write time. Hoisting awaits to <c>Let.Value</c>
///     positions guarantees stack depth 0 at every suspension point.
/// </summary>
public sealed class AwaitHoister
{
    private int _counter;

    public IrNode Hoist(IrNode node)
    {
        return Rewrite(node);
    }

    // Reconstruction in RewriteInner copies Type but not Span; restore it from the original node
    // so source provenance survives for later passes (e.g. coverage).
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
            or IrNode.SymbolConst
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
                return new IrNode.WithHandlers(whBody, whHandlers) { Type = wh.Type };

            // `with` rather than a positional rebuild: Let carries an EmitName assigned by
            // EmitNameResolver (which runs well before this pass), and reconstructing the
            // record by hand silently dropped it — the renamed module-level value then
            // emitted its static field under the very name the rename had moved it away
            // from. Use has no such field today, but shares the shape.
            case IrNode.Let let:
                return let with { Value = Rewrite(let.Value), Body = Rewrite(let.Body) };

            case IrNode.Use use:
                return use with { Value = Rewrite(use.Value), Body = Rewrite(use.Body) };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition),
                    Rewrite(ifNode.Then),
                    Rewrite(ifNode.Else)
                )
                {
                    Type = ifNode.Type,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList()) { Type = seq.Type };

            case IrNode.BinOp binop:
            {
                var l = Rewrite(binop.Left);
                var r = Rewrite(binop.Right);
                if (
                    !AsyncStateMachineAnalyzer.ContainsAwait(l)
                    && !AsyncStateMachineAnalyzer.ContainsAwait(r)
                )
                    return new IrNode.BinOp(binop.Op, l, r) { Type = binop.Type };
                return Anf(
                    [l, r],
                    vars => new IrNode.BinOp(binop.Op, vars[0], vars[1]) { Type = binop.Type }
                );
            }

            case IrNode.UnaryOp unary:
            {
                var operand = Rewrite(unary.Operand);
                if (!AsyncStateMachineAnalyzer.ContainsAwait(operand))
                    return new IrNode.UnaryOp(unary.Op, operand) { Type = unary.Type };
                return Anf(
                    [operand],
                    vars => new IrNode.UnaryOp(unary.Op, vars[0]) { Type = unary.Type }
                );
            }

            case IrNode.Call call:
            {
                var fn = call.Function is IrNode.Var ? call.Function : Rewrite(call.Function);
                var args = call.Args.Select(Rewrite).ToList();
                if (
                    !AsyncStateMachineAnalyzer.ContainsAwait(fn)
                    && !args.Any(AsyncStateMachineAnalyzer.ContainsAwait)
                )
                    return new IrNode.Call(fn, args) { Type = call.Type };
                if (fn is IrNode.Var)
                    return Anf(args, vars => new IrNode.Call(fn, vars) { Type = call.Type });

                var all = new List<IrNode> { fn };
                all.AddRange(args);
                return Anf(
                    all,
                    vars => new IrNode.Call(vars[0], vars.Skip(1).ToList()) { Type = call.Type }
                );
            }

            case IrNode.MethodCall mc:
            {
                var receiver = Rewrite(mc.Receiver);
                var mcArgs = mc.Args.Select(Rewrite).ToList();
                if (
                    !AsyncStateMachineAnalyzer.ContainsAwait(receiver)
                    && !mcArgs.Any(AsyncStateMachineAnalyzer.ContainsAwait)
                )
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
                if (!cnArgs.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return new IrNode.ClrNew(cn.QualifiedTypeName, cn.TypeArgs, cnArgs)
                    {
                        Type = cn.Type,
                    };
                return Anf(
                    cnArgs,
                    vars => new IrNode.ClrNew(cn.QualifiedTypeName, cn.TypeArgs, vars)
                    {
                        Type = cn.Type,
                    }
                );
            }

            case IrNode.ClrCall cc:
            {
                var ccArgs = cc.Args.Select(Rewrite).ToList();
                if (!ccArgs.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return cc with { Args = ccArgs };
                return Anf(ccArgs, vars => cc with { Args = vars });
            }

            case IrNode.TupleNew tn:
            {
                var tnEls = tn.Elements.Select(Rewrite).ToList();
                if (!tnEls.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return new IrNode.TupleNew(tnEls) { Type = tn.Type };
                return Anf(tnEls, vars => new IrNode.TupleNew(vars) { Type = tn.Type });
            }

            case IrNode.UnionCaseNew ucn:
            {
                var ucnArgs = ucn.Args.Select(Rewrite).ToList();
                if (!ucnArgs.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return new IrNode.UnionCaseNew(ucn.UnionName, ucn.CaseName, ucnArgs)
                    {
                        Type = ucn.Type,
                    };
                return Anf(
                    ucnArgs,
                    vars => new IrNode.UnionCaseNew(ucn.UnionName, ucn.CaseName, vars)
                    {
                        Type = ucn.Type,
                    }
                );
            }

            case IrNode.RecordNew rn:
            {
                var rnFields = rn
                    .Fields.Select(f => (f.FieldName, Value: Rewrite(f.Value)))
                    .ToList();
                if (!rnFields.Any(f => AsyncStateMachineAnalyzer.ContainsAwait(f.Value)))
                    return new IrNode.RecordNew(
                        rn.TypeName,
                        rnFields.Select(f => (f.FieldName, f.Value)).ToList()
                    )
                    {
                        Type = rn.Type,
                    };
                var rnValues = rnFields.Select(f => f.Value).ToList();
                return Anf(
                    rnValues,
                    vars =>
                    {
                        var newFields = rnFields
                            .Zip(vars, (f, v) => (f.FieldName, Value: v))
                            .ToList();
                        return new IrNode.RecordNew(rn.TypeName, newFields) { Type = rn.Type };
                    }
                );
            }

            case IrNode.RecordWith rw:
            {
                var rec = Rewrite(rw.Record);
                var updates = rw
                    .Updates.Select(u => (u.FieldName, Value: Rewrite(u.Value)))
                    .ToList();
                if (
                    !AsyncStateMachineAnalyzer.ContainsAwait(rec)
                    && !updates.Any(u => AsyncStateMachineAnalyzer.ContainsAwait(u.Value))
                )
                    return new IrNode.RecordWith(
                        rw.TypeName,
                        rec,
                        updates.Select(u => (u.FieldName, u.Value)).ToList()
                    )
                    {
                        Type = rw.Type,
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
                        };
                    }
                );
            }

            case IrNode.MutableArrayNew man:
            {
                var elements = man.Elements.Select(Rewrite).ToList();
                if (!elements.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return new IrNode.MutableArrayNew(man.ElementType, elements)
                    {
                        Type = man.Type,
                    };
                return Anf(
                    elements,
                    vars => new IrNode.MutableArrayNew(man.ElementType, vars) { Type = man.Type }
                );
            }

            case IrNode.Match match:
            {
                var scrutinee = Rewrite(match.Scrutinee);
                var arms = match
                    .Arms.Select(a => new IrMatchArm(a.Pattern, Rewrite(a.Body)))
                    .ToList();
                if (!AsyncStateMachineAnalyzer.ContainsAwait(scrutinee))
                    return new IrNode.Match(scrutinee, arms) { Type = match.Type };
                return Anf(
                    [scrutinee],
                    vars => new IrNode.Match(vars[0], arms) { Type = match.Type }
                );
            }

            case IrNode.Throw thr:
            {
                var expr = Rewrite(thr.Expr);
                if (!AsyncStateMachineAnalyzer.ContainsAwait(expr))
                    return new IrNode.Throw(expr) { Type = thr.Type };
                return Anf([expr], vars => new IrNode.Throw(vars[0]) { Type = thr.Type });
            }

            case IrNode.Await aw:
            {
                var expr = Rewrite(aw.Expr);
                return new IrNode.Await(expr) { Type = aw.Type };
            }

            case IrNode.SetField sf:
            {
                var val = Rewrite(sf.Value);
                if (!AsyncStateMachineAnalyzer.ContainsAwait(val))
                    return new IrNode.SetField(sf.FieldName, val) { Type = sf.Type };
                return Anf(
                    [val],
                    vars => new IrNode.SetField(sf.FieldName, vars[0]) { Type = sf.Type }
                );
            }

            case IrNode.FieldGet fg:
            {
                var rec = Rewrite(fg.Record);
                if (!AsyncStateMachineAnalyzer.ContainsAwait(rec))
                    return new IrNode.FieldGet(rec, fg.FieldName) { Type = fg.Type };
                return Anf(
                    [rec],
                    vars => new IrNode.FieldGet(vars[0], fg.FieldName) { Type = fg.Type }
                );
            }

            case IrNode.SuperMethodCall smc:
            {
                var smcArgs = smc.Args.Select(Rewrite).ToList();
                if (!smcArgs.Any(AsyncStateMachineAnalyzer.ContainsAwait))
                    return new IrNode.SuperMethodCall(smc.MethodName, smcArgs) { Type = smc.Type };
                return Anf(
                    smcArgs,
                    vars => new IrNode.SuperMethodCall(smc.MethodName, vars) { Type = smc.Type }
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
        var names = args.Select(_ => $"__await_hoist_{_counter++}").ToList();
        var vars = args.Zip(names, (a, n) => (IrNode)new IrNode.Var(n) { Type = a.Type }).ToList();
        var result = builder(vars);
        for (var i = args.Count - 1; i >= 0; i--)
            result = new IrNode.Let(names[i], args[i], result) { Type = result.Type };
        return result;
    }
}

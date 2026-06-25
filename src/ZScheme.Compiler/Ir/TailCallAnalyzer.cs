namespace ZScheme.Compiler.Ir;

/// <summary>
///     Marks calls in tail position with IsTailCall flag.
/// </summary>
public sealed class TailCallAnalyzer
{
    public void Analyze(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
                foreach (var child in seq.Nodes)
                    Analyze(child);
                break;

            case IrNode.FuncDef func:
                MarkTailCalls(func.Body, func.Name, true);
                break;

            case IrNode.ClassDecl cd:
                MarkTailCalls(cd, "<class>", false);
                break;

            // Other top-level forms (RecordDecl, UnionDecl, InterfaceDecl, etc.) carry no
            // expression bodies that need tail-call analysis.
        }
    }

    private void MarkTailCalls(IrNode node, string funcName, bool isTailPosition)
    {
        switch (node)
        {
            case IrNode.Call call:
                if (isTailPosition)
                    call.IsTailCall = true;
                // Args and function position are non-tail
                foreach (var arg in call.Args)
                    MarkTailCalls(arg, funcName, false);
                MarkTailCalls(call.Function, funcName, false);
                break;

            case IrNode.If @if:
                MarkTailCalls(@if.Condition, funcName, false);
                MarkTailCalls(@if.Then, funcName, isTailPosition);
                MarkTailCalls(@if.Else, funcName, isTailPosition);
                break;

            case IrNode.Let let:
                MarkTailCalls(let.Value, funcName, false);
                MarkTailCalls(let.Body, funcName, isTailPosition);
                break;

            case IrNode.Use use:
                // The body is NOT in tail position: disposal runs after it returns
                // (the finally executes before control leaves the method). Recurse with
                // isTailPosition=false so no call inside the body is marked tail, while
                // still analyzing any nested function definitions.
                MarkTailCalls(use.Value, funcName, false);
                MarkTailCalls(use.Body, funcName, false);
                break;

            case IrNode.Match match:
                MarkTailCalls(match.Scrutinee, funcName, false);
                foreach (var arm in match.Arms)
                    MarkTailCalls(arm.Body, funcName, isTailPosition);
                break;

            case IrNode.BinOp binop:
                MarkTailCalls(binop.Left, funcName, false);
                MarkTailCalls(binop.Right, funcName, false);
                break;

            case IrNode.UnaryOp unary:
                MarkTailCalls(unary.Operand, funcName, false);
                break;

            case IrNode.WithHandlers wh:
                MarkTailCalls(wh.Body, funcName, isTailPosition);
                foreach (var h in wh.Handlers)
                    MarkTailCalls(h.HandlerBody, funcName, isTailPosition);
                break;

            case IrNode.Seq seq:
                for (var i = 0; i < seq.Nodes.Count; i++)
                    MarkTailCalls(
                        seq.Nodes[i],
                        funcName,
                        isTailPosition && i == seq.Nodes.Count - 1
                    );
                break;

            case IrNode.Throw th:
                MarkTailCalls(th.Expr, funcName, false);
                break;

            case IrNode.Await aw:
                MarkTailCalls(aw.Expr, funcName, false);
                break;

            case IrNode.Closure cl:
                foreach (var v in cl.CapturedValues)
                    MarkTailCalls(v, funcName, false);
                break;

            case IrNode.MethodCall mc:
                MarkTailCalls(mc.Receiver, funcName, false);
                foreach (var arg in mc.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.ClrCall cc:
                foreach (var arg in cc.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.ClrNew cn:
                foreach (var arg in cn.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.RecordNew rn:
                foreach (var (_, val) in rn.Fields)
                    MarkTailCalls(val, funcName, false);
                break;

            case IrNode.RecordWith rw:
                MarkTailCalls(rw.Record, funcName, false);
                foreach (var (_, val) in rw.Updates)
                    MarkTailCalls(val, funcName, false);
                break;

            case IrNode.TupleNew tn:
                foreach (var el in tn.Elements)
                    MarkTailCalls(el, funcName, false);
                break;

            case IrNode.UnionCaseNew un:
                foreach (var arg in un.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.MutableArrayNew man:
                foreach (var el in man.Elements)
                    MarkTailCalls(el, funcName, false);
                break;

            case IrNode.FieldGet fg:
                MarkTailCalls(fg.Record, funcName, false);
                break;

            case IrNode.SuperMethodCall smc:
                foreach (var arg in smc.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.TcoJump tj:
                foreach (var arg in tj.NewArgs)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.FuncDef func:
                // Nested function: analyze separately with its own tail context
                MarkTailCalls(func.Body, func.Name, true);
                break;

            case IrNode.SetField sf:
                MarkTailCalls(sf.Value, funcName, false);
                break;

            // Type/class declarations don't carry expression-level tail-call semantics for the
            // enclosing function; their inner method bodies are analyzed as their own contexts.
            case IrNode.ClassDecl cd:
                if (cd.Constructor is not null)
                {
                    foreach (var (_, v) in cd.Constructor.FieldSets)
                        MarkTailCalls(v, funcName, false);
                    if (cd.Constructor.SuperArgs is not null)
                        foreach (var a in cd.Constructor.SuperArgs)
                            MarkTailCalls(a, funcName, false);
                    foreach (var b in cd.Constructor.BodyExprs)
                        MarkTailCalls(b, funcName, false);
                }
                foreach (var m in cd.Methods)
                    MarkTailCalls(m.Body, m.Name, true);
                break;

            case IrNode.ObjectExpr oe:
                if (oe.Constructor is not null)
                {
                    foreach (var (_, v) in oe.Constructor.FieldSets)
                        MarkTailCalls(v, funcName, false);
                    if (oe.Constructor.SuperArgs is not null)
                        foreach (var a in oe.Constructor.SuperArgs)
                            MarkTailCalls(a, funcName, false);
                    foreach (var b in oe.Constructor.BodyExprs)
                        MarkTailCalls(b, funcName, false);
                }
                foreach (var m in oe.Methods)
                    MarkTailCalls(m.Body, m.Name, true);
                break;
        }
    }
}

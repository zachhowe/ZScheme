namespace ZScheme.Compiler.Ir;

/// <summary>
///     Marks calls in tail position with IsTailCall flag.
/// </summary>
public sealed class TailCallAnalyzer
{
    public void Analyze(IrNode node)
    {
        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                Analyze(child);
        else if (node is IrNode.FuncDef func)
            MarkTailCalls(func.Body, func.Name, true);
    }

    private void MarkTailCalls(IrNode node, string funcName, bool isTailPosition)
    {
        switch (node)
        {
            case IrNode.Call call:
                if (isTailPosition)
                    call.IsTailCall = true;
                // Args are not in tail position
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

            case IrNode.FuncDef func:
                // Nested function: analyze separately
                MarkTailCalls(func.Body, func.Name, true);
                break;

            case IrNode.ClrNew cn:
                foreach (var arg in cn.Args)
                    MarkTailCalls(arg, funcName, false);
                break;

            case IrNode.SetField sf:
                MarkTailCalls(sf.Value, funcName, false);
                break;
        }
    }
}

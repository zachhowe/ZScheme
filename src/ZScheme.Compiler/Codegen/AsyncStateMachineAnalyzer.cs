using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Analyzes an async function's IR body to identify await points and hoisted locals
///     needed for async state machine generation.
/// </summary>
public static class AsyncStateMachineAnalyzer
{
    public static bool ContainsAwait(IrNode node)
    {
        return node switch
        {
            IrNode.Await => true,
            IrNode.Let let => ContainsAwait(let.Value) || ContainsAwait(let.Body),
            IrNode.If @if => ContainsAwait(@if.Condition) || ContainsAwait(@if.Then) || ContainsAwait(@if.Else),
            IrNode.Match match => ContainsAwait(match.Scrutinee) || match.Arms.Any(a => ContainsAwait(a.Body)),
            IrNode.Call call => ContainsAwait(call.Function) || call.Args.Any(ContainsAwait),
            _ => false
        };
    }

    public static AsyncMethodInfo Analyze(IrNode.FuncDef func)
    {
        return Analyze(func.ReturnType, func.Body);
    }

    private static AsyncMethodInfo Analyze(ZType returnType, IrNode body)
    {
        var awaitPoints = new List<AwaitPointInfo>();
        var hoistedLocals = new List<HoistedLocal>();
        var seenLocals = new HashSet<string>();

        var isVoidReturn = returnType is
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } or
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] };

        CollectInfo(body, awaitPoints, hoistedLocals, seenLocals);

        return new AsyncMethodInfo(awaitPoints, hoistedLocals, isVoidReturn);
    }

    private static void CollectInfo(
        IrNode node,
        List<AwaitPointInfo> awaitPoints,
        List<HoistedLocal> hoistedLocals,
        HashSet<string> seenLocals)
    {
        switch (node)
        {
            case IrNode.Await awaitNode:
                var resultType = GetAwaitResultType(awaitNode.Expr.Type);
                awaitPoints.Add(new AwaitPointInfo(
                    awaitPoints.Count,
                    awaitNode.Expr.Type,
                    resultType));
                break;

            case IrNode.Let let:
                // Recurse into value first (may contain await)
                CollectInfo(let.Value, awaitPoints, hoistedLocals, seenLocals);

                // Record the let-bound variable as a hoisted local
                if (let.Value.Type is not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    && seenLocals.Add(let.VarName))
                    hoistedLocals.Add(new HoistedLocal(let.VarName, let.Value.Type));

                // Recurse into body
                CollectInfo(let.Body, awaitPoints, hoistedLocals, seenLocals);
                break;

            case IrNode.If @if:
                CollectInfo(@if.Condition, awaitPoints, hoistedLocals, seenLocals);
                CollectInfo(@if.Then, awaitPoints, hoistedLocals, seenLocals);
                CollectInfo(@if.Else, awaitPoints, hoistedLocals, seenLocals);
                break;

            case IrNode.Match match:
                CollectInfo(match.Scrutinee, awaitPoints, hoistedLocals, seenLocals);
                foreach (var arm in match.Arms)
                    CollectInfo(arm.Body, awaitPoints, hoistedLocals, seenLocals);
                break;

            case IrNode.Call call:
                CollectInfo(call.Function, awaitPoints, hoistedLocals, seenLocals);
                foreach (var arg in call.Args)
                    CollectInfo(arg, awaitPoints, hoistedLocals, seenLocals);
                break;

            case IrNode.BinOp binOp:
                CollectInfo(binOp.Left, awaitPoints, hoistedLocals, seenLocals);
                CollectInfo(binOp.Right, awaitPoints, hoistedLocals, seenLocals);
                break;

            case IrNode.UnaryOp unaryOp:
                CollectInfo(unaryOp.Operand, awaitPoints, hoistedLocals, seenLocals);
                break;

            // Leaf nodes and others that can't contain await — do nothing
        }
    }

    /// <summary>
    ///     Extracts the T from Task&lt;T&gt; or returns Unit for non-generic Task.
    /// </summary>
    public static ZType GetAwaitResultType(ZType taskType)
    {
        return taskType switch
        {
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var inner] } => inner,
            _ => ZType.Unit
        };
    }

    public sealed record AwaitPointInfo(
        int StateNumber,
        ZType TaskExprType,
        ZType ResultType);

    public sealed record HoistedLocal(
        string Name,
        ZType Type);

    public sealed record AsyncMethodInfo(
        IReadOnlyList<AwaitPointInfo> AwaitPoints,
        IReadOnlyList<HoistedLocal> HoistedLocals,
        bool IsVoidReturn);
}

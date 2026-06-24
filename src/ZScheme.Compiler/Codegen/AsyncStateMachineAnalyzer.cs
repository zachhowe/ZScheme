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
            IrNode.Use use => ContainsAwait(use.Value) || ContainsAwait(use.Body),
            IrNode.If @if => ContainsAwait(@if.Condition)
                || ContainsAwait(@if.Then)
                || ContainsAwait(@if.Else),
            IrNode.Match match => ContainsAwait(match.Scrutinee)
                || match.Arms.Any(a => ContainsAwait(a.Body)),
            IrNode.Call call => ContainsAwait(call.Function) || call.Args.Any(ContainsAwait),
            IrNode.BinOp binOp => ContainsAwait(binOp.Left) || ContainsAwait(binOp.Right),
            IrNode.UnaryOp unaryOp => ContainsAwait(unaryOp.Operand),
            IrNode.Seq seq => seq.Nodes.Any(ContainsAwait),
            IrNode.WithHandlers wh => ContainsAwait(wh.Body)
                || wh.Handlers.Any(h => ContainsAwait(h.HandlerBody)),
            IrNode.Throw th => ContainsAwait(th.Expr),
            IrNode.MethodCall mc => ContainsAwait(mc.Receiver) || mc.Args.Any(ContainsAwait),
            IrNode.ClrCall cc => cc.Args.Any(ContainsAwait),
            IrNode.ClrNew cn => cn.Args.Any(ContainsAwait),
            IrNode.RecordNew rn => rn.Fields.Any(f => ContainsAwait(f.Value)),
            IrNode.RecordWith rw => ContainsAwait(rw.Record)
                || rw.Updates.Any(u => ContainsAwait(u.Value)),
            IrNode.UnionCaseNew ucn => ucn.Args.Any(ContainsAwait),
            IrNode.TupleNew tn => tn.Elements.Any(ContainsAwait),
            IrNode.MutableArrayNew man => man.Elements.Any(ContainsAwait),
            IrNode.FieldGet fg => ContainsAwait(fg.Record),
            IrNode.SetField sf => ContainsAwait(sf.Value),
            IrNode.TypeTest tt => ContainsAwait(tt.Value),
            IrNode.SuperMethodCall smc => smc.Args.Any(ContainsAwait),
            _ => false,
        };
    }

    public static AsyncMethodInfo Analyze(IrNode.FuncDef func, TypeAliasRegistry typeAliases)
    {
        return Analyze(func.ReturnType, func.Body, typeAliases);
    }

    private static AsyncMethodInfo Analyze(
        ZType returnType,
        IrNode body,
        TypeAliasRegistry typeAliases
    )
    {
        var awaitPoints = new List<AwaitPointInfo>();
        var hoistedLocals = new List<HoistedLocal>();
        var seenLocals = new HashSet<string>();
        var tryBodyStack = new List<IrNode.WithHandlers>();

        var isVoidReturn =
            returnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
            || (
                returnType is ZType.ZNamedType { TypeArgs: [] } taskRet
                && typeAliases.IsTaskName(taskRet.Name)
            );

        CollectInfo(body, awaitPoints, hoistedLocals, seenLocals, tryBodyStack, typeAliases);

        return new AsyncMethodInfo(awaitPoints, hoistedLocals, isVoidReturn);
    }

    private static void CollectInfo(
        IrNode node,
        List<AwaitPointInfo> awaitPoints,
        List<HoistedLocal> hoistedLocals,
        HashSet<string> seenLocals,
        List<IrNode.WithHandlers> tryBodyStack,
        TypeAliasRegistry typeAliases
    )
    {
        switch (node)
        {
            case IrNode.Await awaitNode:
                // Recurse into the awaited expression first so any nested awaits
                // (e.g. `(await (g (await (g 1))))`) are counted in the order the
                // IL emitter encounters them. The emitter pushes the inner Expr
                // before consuming the outer Await, so the inner await runs first
                // and consumes the lower state number.
                CollectInfo(
                    awaitNode.Expr,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );

                var resultType = GetAwaitResultType(awaitNode.Expr.Type, typeAliases);
                awaitPoints.Add(
                    new AwaitPointInfo(
                        awaitPoints.Count,
                        awaitNode.Expr.Type,
                        resultType,
                        tryBodyStack.ToArray()
                    )
                );
                break;

            case IrNode.Let let:
                // Recurse into value first (may contain await)
                CollectInfo(
                    let.Value,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );

                // Record the let-bound variable as a hoisted local. Skip the
                // discard binding "_" — `(begin a b c)` desugars to
                // `(let [_ a] (let [_ b] c))`, so multiple `_` Lets with
                // different value types coexist in one method. Hoisting all of
                // them under the single name "_" would alias them to one
                // state-machine field whose type matches whichever `_` Let was
                // seen first; a later `_` Let with a different value type
                // would then `stfld` (and on resume `ldfld`) a mismatched type
                // and ilverify would reject it. The `_` binding is never read,
                // so it does not need to survive across awaits.
                if (
                    let.Value.Type is not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    && let.VarName != "_"
                    && seenLocals.Add(let.VarName)
                )
                    hoistedLocals.Add(new HoistedLocal(let.VarName, let.Value.Type));

                // Recurse into body
                CollectInfo(
                    let.Body,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.Use use:
                // Structurally like Let for await collection: recurse value, hoist the
                // resource local, recurse body. (The try/finally emission itself is the
                // IL backend's concern.)
                CollectInfo(
                    use.Value,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                if (
                    use.Value.Type is not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    && use.VarName != "_"
                    && seenLocals.Add(use.VarName)
                )
                    hoistedLocals.Add(new HoistedLocal(use.VarName, use.Value.Type));
                CollectInfo(
                    use.Body,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.If @if:
                CollectInfo(
                    @if.Condition,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                CollectInfo(
                    @if.Then,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                CollectInfo(
                    @if.Else,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.Match match:
                CollectInfo(
                    match.Scrutinee,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                foreach (var arm in match.Arms)
                    CollectInfo(
                        arm.Body,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.Call call:
                CollectInfo(
                    call.Function,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                foreach (var arg in call.Args)
                    CollectInfo(
                        arg,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.BinOp binOp:
                CollectInfo(
                    binOp.Left,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                CollectInfo(
                    binOp.Right,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.UnaryOp unaryOp:
                CollectInfo(
                    unaryOp.Operand,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    CollectInfo(
                        n,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.WithHandlers wh:
                tryBodyStack.Add(wh);
                CollectInfo(
                    wh.Body,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                tryBodyStack.RemoveAt(tryBodyStack.Count - 1);
                // Handler bodies are emitted *outside* the catch region by the IL
                // emitter (catch only captures the exception and tags it; the
                // body runs after the try). So awaits inside handler bodies are
                // NOT enclosed by this with-handlers' try region — they sit in
                // the parent scope. Recurse without pushing wh on the stack, and
                // hoist any bound exception variable that may need to survive
                // an await in the handler body.
                foreach (var h in wh.Handlers)
                {
                    if (
                        h.BindingVarName != "_"
                        && ContainsAwait(h.HandlerBody)
                        && seenLocals.Add(h.BindingVarName)
                    )
                        hoistedLocals.Add(
                            new HoistedLocal(
                                h.BindingVarName,
                                new ZType.ZNamedType(h.ExceptionTypeName, [])
                            )
                        );
                    CollectInfo(
                        h.HandlerBody,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                }

                break;

            case IrNode.Throw th:
                CollectInfo(
                    th.Expr,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.MethodCall mc:
                CollectInfo(
                    mc.Receiver,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                foreach (var a in mc.Args)
                    CollectInfo(
                        a,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.ClrCall cc:
                foreach (var a in cc.Args)
                    CollectInfo(
                        a,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                    CollectInfo(
                        a,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.RecordNew rn:
                foreach (var f in rn.Fields)
                    CollectInfo(
                        f.Value,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.RecordWith rw:
                CollectInfo(
                    rw.Record,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                foreach (var u in rw.Updates)
                    CollectInfo(
                        u.Value,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.UnionCaseNew ucn:
                foreach (var a in ucn.Args)
                    CollectInfo(
                        a,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    CollectInfo(
                        e,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    CollectInfo(
                        e,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            case IrNode.FieldGet fg:
                CollectInfo(
                    fg.Record,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.SetField sf:
                CollectInfo(
                    sf.Value,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.TypeTest tt:
                CollectInfo(
                    tt.Value,
                    awaitPoints,
                    hoistedLocals,
                    seenLocals,
                    tryBodyStack,
                    typeAliases
                );
                break;

            case IrNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                    CollectInfo(
                        a,
                        awaitPoints,
                        hoistedLocals,
                        seenLocals,
                        tryBodyStack,
                        typeAliases
                    );
                break;

            // Leaf nodes and others that can't contain await — do nothing
        }
    }

    /// <summary>
    ///     Extracts the T from Task&lt;T&gt; or returns Unit for non-generic Task.
    /// </summary>
    public static ZType GetAwaitResultType(ZType taskType, TypeAliasRegistry typeAliases)
    {
        return taskType switch
        {
            ZType.ZNamedType { TypeArgs: [var inner] } taskNt
                when typeAliases.IsTaskName(taskNt.Name) => inner,
            _ => ZType.Unit,
        };
    }

    public sealed record AwaitPointInfo(
        int StateNumber,
        ZType TaskExprType,
        ZType ResultType,
        IReadOnlyList<IrNode.WithHandlers> EnclosingTryBodies
    );

    public sealed record HoistedLocal(string Name, ZType Type);

    public sealed record AsyncMethodInfo(
        IReadOnlyList<AwaitPointInfo> AwaitPoints,
        IReadOnlyList<HoistedLocal> HoistedLocals,
        bool IsVoidReturn
    );
}

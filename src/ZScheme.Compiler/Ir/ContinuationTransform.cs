using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
/// Implements the IR side of "Continuations from Generalized Stack Inspection" (Pettyjohn et al.,
/// ICFP 2005). Wraps each non-tail call in a try/catch that, on a SaveContinuation throw, appends
/// a frame describing the post-call computation to the in-flight exception and rethrows. The
/// frame replays the post-call computation by calling a synthesized "continuation function" with
/// captured live variables.
///
/// Stage 5 scope: handles the trivial Let-binding shape <c>Let(t, NonTailCall, body)</c>.
/// Larger shapes (nested if/match, multi-let, etc.) are added in Stage 6.
/// </summary>
public sealed class ContinuationTransform
{
    private readonly List<IrNode> _newSiblings = new();
    private int _frameCounter;
    private string _currentFuncName = "<top>";
    private bool _currentFuncIsAsync;
    private bool _producedAnyAsyncFrame;

    /// <summary>
    /// True if this transform synthesized at least one async-tail continuation function (and
    /// therefore at least one async frame class). Programs that have any such frame must use
    /// <see cref="ZScheme.Runtime.Runtime.RunAsync{T}"/> at the entry point so the dispatch
    /// loop awaits frames instead of blocking on them.
    /// </summary>
    public bool ProducedAnyAsyncFrame => _producedAnyAsyncFrame;

    /// <summary>
    /// Returns true if any non-tail Call/ClrCall occurs that this pass would wrap. Triggers on
    /// any continuation-capturing runtime call — <c>CallCcTyped</c> (call/cc), <c>Reset</c>,
    /// <c>ResetAt</c>, <c>ShiftTyped</c>, <c>ShiftTypedAt</c>, <c>ControlTyped</c>,
    /// <c>ControlTypedAt</c>, <c>CallCompTyped</c>, <c>CallCompTypedAt</c> — because they all
    /// share the SaveContinuation throw path and need per-call-site frame wrappers.
    /// </summary>
    public static bool ProgramUsesCallCc(IrNode root)
    {
        return root switch
        {
            IrNode.Seq seq => seq.Nodes.Any(ProgramUsesCallCc),
            IrNode.FuncDef func => ContainsCallCc(func.Body),
            IrNode.ClassDecl cd => cd.Methods.Any(m => ContainsCallCc(m.Body))
                || (cd.Constructor?.BodyExprs.Any(ContainsCallCc) ?? false),
            IrNode.ObjectExpr oe => oe.Methods.Any(m => ContainsCallCc(m.Body))
                || (oe.Constructor?.BodyExprs.Any(ContainsCallCc) ?? false),
            _ => ContainsCallCc(root),
        };
    }

    internal static bool ContainsCallCc(IrNode node) =>
        node switch
        {
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "CallCcTyped"
            } => true,
            IrNode.ClrCall { QualifiedTypeName: "ZScheme.Runtime.Runtime", MethodName: "Reset" } =>
                true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "ResetAt"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "ShiftTyped"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "ShiftTypedAt"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "ControlTyped"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "ControlTypedAt"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "CallCompTyped"
            } => true,
            IrNode.ClrCall
            {
                QualifiedTypeName: "ZScheme.Runtime.Runtime",
                MethodName: "CallCompTypedAt"
            } => true,
            IrNode.Let l => ContainsCallCc(l.Value) || ContainsCallCc(l.Body),
            IrNode.If i => ContainsCallCc(i.Condition)
                || ContainsCallCc(i.Then)
                || ContainsCallCc(i.Else),
            IrNode.Call c => ContainsCallCc(c.Function) || c.Args.Any(ContainsCallCc),
            IrNode.ClrCall cc => cc.Args.Any(ContainsCallCc),
            IrNode.BinOp b => ContainsCallCc(b.Left) || ContainsCallCc(b.Right),
            IrNode.UnaryOp u => ContainsCallCc(u.Operand),
            IrNode.Match m => ContainsCallCc(m.Scrutinee)
                || m.Arms.Any(a => ContainsCallCc(a.Body)),
            IrNode.WithHandlers wh => ContainsCallCc(wh.Body)
                || wh.Handlers.Any(h => ContainsCallCc(h.HandlerBody)),
            IrNode.Seq s => s.Nodes.Any(ContainsCallCc),
            IrNode.Throw t => ContainsCallCc(t.Expr),
            IrNode.Await a => ContainsCallCc(a.Expr),
            IrNode.FuncDef fn => ContainsCallCc(fn.Body),
            IrNode.MethodCall mc => ContainsCallCc(mc.Receiver) || mc.Args.Any(ContainsCallCc),
            IrNode.RecordNew rn => rn.Fields.Any(f => ContainsCallCc(f.Value)),
            IrNode.RecordWith rw => ContainsCallCc(rw.Record)
                || rw.Updates.Any(u => ContainsCallCc(u.Value)),
            IrNode.TupleNew tn => tn.Elements.Any(ContainsCallCc),
            IrNode.UnionCaseNew un => un.Args.Any(ContainsCallCc),
            IrNode.MutableArrayNew man => man.Elements.Any(ContainsCallCc),
            IrNode.FieldGet fg => ContainsCallCc(fg.Record),
            IrNode.ObjectExpr oe => oe.Methods.Any(m => ContainsCallCc(m.Body))
                || (oe.Constructor?.BodyExprs.Any(ContainsCallCc) ?? false),
            _ => false,
        };

    public IrNode Transform(IrNode node)
    {
        if (node is IrNode.Seq seq)
            return TransformSeq(seq);
        if (node is IrNode.FuncDef fn)
        {
            var transformed = TransformTopLevelFuncDef(fn);
            if (_newSiblings.Count == 0)
                return transformed;
            var combined = new List<IrNode> { transformed };
            combined.AddRange(_newSiblings);
            _newSiblings.Clear();
            return new IrNode.Seq(combined) { Type = transformed.Type };
        }
        return node;
    }

    private IrNode TransformSeq(IrNode.Seq seq)
    {
        var collected = new List<IrNode>();
        foreach (var child in seq.Nodes)
        {
            if (child is IrNode.FuncDef fn)
            {
                var transformed = TransformTopLevelFuncDef(fn);
                collected.Add(transformed);
                collected.AddRange(_newSiblings);
                _newSiblings.Clear();
            }
            else
            {
                collected.Add(child);
            }
        }
        return seq with { Nodes = collected };
    }

    private IrNode.FuncDef TransformTopLevelFuncDef(IrNode.FuncDef fn)
    {
        var prevName = _currentFuncName;
        var prevIsAsync = _currentFuncIsAsync;
        _currentFuncName = fn.Name;
        _currentFuncIsAsync = fn.IsAsync;

        var env = new Dictionary<string, ZType>();
        foreach (var p in fn.Params)
            env[p.Name] = p.Type;

        var newBody = TransformExpr(fn.Body, env, fn.ReturnType);

        _currentFuncName = prevName;
        _currentFuncIsAsync = prevIsAsync;
        return fn with { Body = newBody };
    }

    private IrNode TransformExpr(
        IrNode node,
        IReadOnlyDictionary<string, ZType> env,
        ZType resultType
    )
    {
        switch (node)
        {
            case IrNode.Let let when IsCapturable(let.Value):
                return TransformCapturableLet(let, env, resultType);

            case IrNode.Let let:
            {
                var newValue = TransformExpr(let.Value, env, let.Value.Type);
                var nestedEnv = ExtendEnv(env, let.VarName, let.Value.Type);
                var newBody = TransformExpr(let.Body, nestedEnv, resultType);
                return let with { Value = newValue, Body = newBody };
            }

            case IrNode.If ifNode:
                return ifNode with
                {
                    Then = TransformExpr(ifNode.Then, env, resultType),
                    Else = TransformExpr(ifNode.Else, env, resultType),
                };

            case IrNode.Match match:
                return match with
                {
                    Arms = match
                        .Arms.Select(a => new IrMatchArm(
                            a.Pattern,
                            TransformExpr(a.Body, env, resultType)
                        ))
                        .ToList(),
                };

            case IrNode.Seq seq:
                return seq with
                {
                    Nodes = seq
                        .Nodes.Select(
                            (n, i) =>
                                TransformExpr(
                                    n,
                                    env,
                                    i == seq.Nodes.Count - 1 ? resultType : n.Type
                                )
                        )
                        .ToList(),
                };

            case IrNode.ClrCall clr:
                return clr with
                {
                    Args = clr.Args.Select(a => TransformExpr(a, env, a.Type)).ToList(),
                };

            case IrNode.Call call:
                return call with
                {
                    Function = TransformExpr(call.Function, env, call.Function.Type),
                    Args = call.Args.Select(a => TransformExpr(a, env, a.Type)).ToList(),
                };

            // Nested FuncDefs (user lambdas, synthesized reset/shift body thunks) are inlined
            // as ClrCall/Call args. Process them like top-level FuncDefs — non-tail Lets inside
            // their body need wrapping too, otherwise frames captured around (let v (shift…) …)
            // inside a reset body silently disappear. We inherit the parent env so vars from
            // the enclosing function show up as live vars when an inner let needs a frame.
            case IrNode.FuncDef fn:
                return TransformNestedFuncDef(fn, env);

            default:
                return node;
        }
    }

    private IrNode.FuncDef TransformNestedFuncDef(
        IrNode.FuncDef fn,
        IReadOnlyDictionary<string, ZType> parentEnv
    )
    {
        var prevName = _currentFuncName;
        var prevIsAsync = _currentFuncIsAsync;
        _currentFuncName = fn.Name;
        _currentFuncIsAsync = fn.IsAsync;

        var env = new Dictionary<string, ZType>(parentEnv);
        foreach (var p in fn.Params)
            env[p.Name] = p.Type;

        var newBody = TransformExpr(fn.Body, env, fn.ReturnType);

        _currentFuncName = prevName;
        _currentFuncIsAsync = prevIsAsync;
        return fn with { Body = newBody };
    }

    /// <summary>
    ///     A let-value is "capturable" when it can throw <c>SaveContinuation</c>, requiring the
    ///     let-body to be packaged as a resumption frame. The base case is a non-tail call (which
    ///     might transitively reach a continuation operator). For compound shapes we recurse only
    ///     into positions whose result becomes the let-value's value: <c>If</c>/<c>Match</c>
    ///     branches, <c>Seq</c> tail, <c>WithHandlers</c> body, and nested <c>Let</c> value/body.
    ///     We do NOT recurse into BinOp / Call args / etc. — those positions consume the inner
    ///     value before it becomes the let-value, so wrapping would attach a frame with the wrong
    ///     resumption type and skip the consuming computation. The <see cref="CapturableCallHoister"/>
    ///     pre-pass guarantees no capturable runtime call appears in such positions, so this
    ///     restricted recursion is sound.
    /// </summary>
    private static bool IsCapturable(IrNode value) =>
        value switch
        {
            IrNode.Call call => !call.IsTailCall,
            IrNode.ClrCall clr => !clr.IsTailCall,
            IrNode.If ifNode => IsCapturable(ifNode.Then) || IsCapturable(ifNode.Else),
            IrNode.Match match => match.Arms.Any(a => IsCapturable(a.Body)),
            IrNode.Seq seq => seq.Nodes.Count > 0 && IsCapturable(seq.Nodes[^1]),
            IrNode.Let inner => IsCapturable(inner.Value) || IsCapturable(inner.Body),
            IrNode.WithHandlers wh => IsCapturable(wh.Body),
            _ => false,
        };

    private IrNode TransformCapturableLet(
        IrNode.Let let,
        IReadOnlyDictionary<string, ZType> env,
        ZType resultType
    )
    {
        // Transform the value first — its own nested FuncDefs (reset/shift body thunks, user
        // lambdas) may contain capturable Lets that need wrapping. Without this, a `(let v
        // (reset (let w (shift …) …)) …)` pattern silently loses the inner frame.
        let = let with
        {
            Value = TransformExpr(let.Value, env, let.Value.Type),
        };

        // Recurse into body first — its own non-tail calls are wrapped before we lift it.
        var bodyEnv = ExtendEnv(env, let.VarName, let.Value.Type);
        var transformedBody = TransformExpr(let.Body, bodyEnv, resultType);

        // Live vars in the transformed body, minus the bound name. Each must be in our env
        // (function-local). Vars from outer scopes won't be — we ignore them as globals.
        var freeNames = FreeVarsCollector.Collect(transformedBody);
        freeNames.Remove(let.VarName);
        var liveVars = freeNames
            .Where(env.ContainsKey)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (Name: n, Type: env[n]))
            .ToList();

        var idx = _frameCounter++;
        var safeFuncName = SanitizeFuncName(_currentFuncName);
        var contName = $"__cont_{safeFuncName}_{idx}";
        var frameName = $"__Frame_{safeFuncName}_{idx}";

        // The synthesized continuation function inherits async-ness from its parent only when
        // it actually needs to await something. If the parent is async but the post-call body
        // is purely sync (no await downstream), the cont stays sync — we don't pay async
        // overhead unnecessarily. If the parent is sync, the cont is sync (TypeInferer rejects
        // await outside async, so transformedBody can't have an Await in that case anyway).
        var contIsAsync =
            _currentFuncIsAsync && AsyncStateMachineAnalyzer.ContainsAwait(transformedBody);

        // Continuation function — re-runs `body` with let.VarName + live vars as parameters.
        var contParams = new List<IrParam> { new(let.VarName, let.Value.Type) };
        contParams.AddRange(liveVars.Select(lv => new IrParam(lv.Name, lv.Type)));
        var contFnType = new ZType.ZFuncType(contParams.Select(p => p.Type).ToList(), resultType);
        var contFn = new IrNode.FuncDef(
            contName,
            contParams,
            resultType,
            transformedBody,
            IsSelfRecursive: false,
            IsAsync: contIsAsync
        )
        {
            Type = contFnType,
        };
        _newSiblings.Add(contFn);

        if (contIsAsync)
            _producedAnyAsyncFrame = true;

        // Frame class — implements ZScheme.Runtime.IFrame. Fields hold captured live vars.
        // Constructor takes the captured vars and assigns them to fields. Invoke unboxes the
        // returnValue, calls the continuation function, and returns the result boxed.
        var frameFields = liveVars.Select(lv => new IrField(lv.Name, lv.Type)).ToList();

        var objectTypeForParam = new ZType.ZNamedType("System.Object", []);
        var frameMethods = new List<IrObjectMethod>();

        if (contIsAsync)
        {
            // Async cont: emit BOTH an InvokeAsync (the only path the runtime actually uses
            // for async frames, via Runtime.ResumeAsync) and a sync Invoke that fails loudly.
            // A sync caller hitting this frame is a bug — better to surface than silently
            // block via .GetAwaiter().GetResult() on possibly-suspended async work.
            //
            // ReturnType for InvokeAsync follows the class-method convention: store the
            // already-Task-wrapped form (Task<object>). The C# emitter prepends `async ` and
            // outputs the type verbatim, yielding `async Task<object> InvokeAsync(...)`.
            var taskOfObject = new ZType.ZNamedType(
                "Task",
                [new ZType.ZNamedType("System.Object", [])]
            );
            var invokeAsyncBody = BuildInvokeBodyAsync(
                contName,
                contFnType,
                let.VarName,
                let.Value.Type,
                liveVars,
                resultType
            );
            frameMethods.Add(
                new IrObjectMethod(
                    "InvokeAsync",
                    [new IrParam("returnValue", objectTypeForParam)],
                    taskOfObject,
                    invokeAsyncBody,
                    IsAsync: true
                )
            );

            var syncFallbackBody = BuildSyncFallbackInvokeBody(frameName);
            frameMethods.Add(
                new IrObjectMethod(
                    "Invoke",
                    [new IrParam("returnValue", objectTypeForParam)],
                    objectTypeForParam,
                    syncFallbackBody
                )
            );
        }
        else
        {
            var invokeBody = BuildInvokeBody(
                contName,
                contFnType,
                let.VarName,
                let.Value.Type,
                liveVars,
                resultType
            );
            frameMethods.Add(
                new IrObjectMethod(
                    "Invoke",
                    [new IrParam("returnValue", objectTypeForParam)],
                    objectTypeForParam,
                    invokeBody
                )
            );
        }

        var ctorFieldSets = liveVars
            .Select(lv => (lv.Name, (IrNode)new IrNode.Var(lv.Name) { Type = lv.Type }))
            .ToList();
        var ctorParams = liveVars.Select(lv => new IrParam(lv.Name, lv.Type)).ToList();
        var ctor = new IrConstructor(
            ctorParams,
            SuperArgs: null,
            FieldSets: ctorFieldSets,
            BodyExprs: []
        );

        var frameClass = new IrNode.ClassDecl(
            Name: frameName,
            TypeParams: [],
            InterfaceNames: ["ZScheme.Runtime.IFrame"],
            Fields: frameFields,
            Methods: frameMethods,
            Constructor: ctor
        );
        _newSiblings.Add(frameClass);

        // New let body: hand off to the cont function. For sync conts this is a tail call.
        // For async conts the call returns Task<T> and we need T (the parent body's tail type),
        // so we wrap in Await — the parent is async, so Await is legal. The Await is no longer
        // a tail call (await is never a tail position).
        var contCallArgs = new List<IrNode>
        {
            new IrNode.Var(let.VarName) { Type = let.Value.Type },
        };
        contCallArgs.AddRange(
            liveVars.Select(lv => (IrNode)new IrNode.Var(lv.Name) { Type = lv.Type })
        );
        IrNode contCall;
        if (contIsAsync)
        {
            var taskCall = new IrNode.Call(
                new IrNode.Var(contName) { Type = contFnType },
                contCallArgs
            )
            {
                Type = new ZType.ZNamedType("Task", [resultType]),
                IsTailCall = false,
            };
            contCall = new IrNode.Await(taskCall) { Type = resultType };
        }
        else
        {
            contCall = new IrNode.Call(new IrNode.Var(contName) { Type = contFnType }, contCallArgs)
            {
                Type = resultType,
                IsTailCall = true,
            };
        }

        // Handler: extend the in-flight SaveContinuation with a new frame, then rethrow.
        var sceVar = new IrNode.Var("__sce")
        {
            Type = new ZType.ZNamedType("ZScheme.Runtime.SaveContinuation", []),
        };
        var frameCtorArgs = liveVars
            .Select(lv => (IrNode)new IrNode.Var(lv.Name) { Type = lv.Type })
            .ToList();
        var frameInstance = new IrNode.ClrNew(frameName, [], frameCtorArgs)
        {
            Type = new ZType.ZNamedType(frameName, []),
        };
        var extendCall = new IrNode.MethodCall(
            sceVar,
            "Extend",
            [frameInstance],
            IsProperty: false,
            IsIndexer: false
        )
        {
            Type = ZType.Unit,
        };
        var handlerBody = new IrNode.Seq([
            extendCall,
            new IrNode.Throw(sceVar) { Type = let.Value.Type },
        ])
        {
            Type = let.Value.Type,
        };

        var handler = new IrHandlerClause("ZScheme.Runtime.SaveContinuation", "__sce", handlerBody);

        // Wrap ONLY the value evaluation in the handler, not the continuation call. If we
        // also covered contCall, a shift fired from inside the let body would trigger the
        // handler again and double-append this let's frame — an invocation of the captured
        // continuation would re-enter the body and (for shift, where k returns normally) re-fire
        // the same shift, producing wrong results. The catch must scope to value evaluation only.
        var guardedValue = new IrNode.WithHandlers(let.Value, [handler]) { Type = let.Value.Type };

        return let with
        {
            Value = guardedValue,
            Body = contCall,
            Type = resultType,
        };
    }

    private static IrNode BuildInvokeBody(
        string contName,
        ZType.ZFuncType contFnType,
        string returnVarName,
        ZType returnVarType,
        IReadOnlyList<(string Name, ZType Type)> liveVars,
        ZType resultType
    )
    {
        // Frame.Invoke(object returnValue) → object:
        //   return (object)contFn((T_t)returnValue!, field1, field2, ...);
        // The C# emitter resolves bare Var references to instance fields automatically
        // when emitting inside a ClassDecl method (via _currentClassFields).
        var objectType = new ZType.ZNamedType("System.Object", []);
        var rawReturnValue = new IrNode.Var("returnValue") { Type = objectType };
        var castedReturnValue = new IrNode.Cast(rawReturnValue, returnVarType)
        {
            Type = returnVarType,
        };

        var contArgs = new List<IrNode> { castedReturnValue };
        foreach (var lv in liveVars)
            contArgs.Add(new IrNode.Var(lv.Name) { Type = lv.Type });

        var contCall = new IrNode.Call(new IrNode.Var(contName) { Type = contFnType }, contArgs)
        {
            Type = resultType,
        };

        // Box back to object on return — C# auto-boxes value types to object so this is implicit
        // for primitives, but emitting an explicit cast keeps reference-type-vs-object cases sane.
        return new IrNode.Cast(contCall, objectType) { Type = objectType };
    }

    /// <summary>
    /// Builds the body of <c>InvokeAsync(object? returnValue) -&gt; Task&lt;object?&gt;</c> for an
    /// async-tail frame. The method is emitted as <c>IsAsync = true</c>, so the codegen wraps
    /// the body's value in <c>Task&lt;object?&gt;</c> automatically. Body shape:
    /// <code>(object?)(await __cont((T)returnValue, field1, field2, ...))</code>
    /// </summary>
    private static IrNode BuildInvokeBodyAsync(
        string contName,
        ZType.ZFuncType contFnType,
        string returnVarName,
        ZType returnVarType,
        IReadOnlyList<(string Name, ZType Type)> liveVars,
        ZType resultType
    )
    {
        var objectType = new ZType.ZNamedType("System.Object", []);
        var rawReturnValue = new IrNode.Var("returnValue") { Type = objectType };
        var castedReturnValue = new IrNode.Cast(rawReturnValue, returnVarType)
        {
            Type = returnVarType,
        };

        var contArgs = new List<IrNode> { castedReturnValue };
        foreach (var lv in liveVars)
            contArgs.Add(new IrNode.Var(lv.Name) { Type = lv.Type });

        // The cont function is async, so the call returns Task<resultType>. Await unwraps it.
        var contTaskType = new ZType.ZNamedType("Task", [resultType]);
        var contCall = new IrNode.Call(new IrNode.Var(contName) { Type = contFnType }, contArgs)
        {
            Type = contTaskType,
        };
        var awaited = new IrNode.Await(contCall) { Type = resultType };

        return new IrNode.Cast(awaited, objectType) { Type = objectType };
    }

    /// <summary>
    /// Sync <c>Invoke</c> body for an async-tail frame: throws <c>NotSupportedException</c>
    /// rather than silently blocking on the underlying <see cref="System.Threading.Tasks.Task"/>.
    /// Reaching this method means a sync <see cref="ZScheme.Runtime.Runtime.Resume"/> path
    /// encountered an async frame — the program needs to use <see cref="ZScheme.Runtime.Runtime.RunAsync{T}"/>
    /// at its entry point. The C# emitter's entry-point synthesis arranges that automatically when
    /// any async frame is produced, so this fallback exists to surface accidental misuse loudly.
    /// </summary>
    private static IrNode BuildSyncFallbackInvokeBody(string frameName)
    {
        var objectType = new ZType.ZNamedType("System.Object", []);
        var msg = new IrNode.StringConst(
            $"Cannot synchronously Invoke async frame '{frameName}'. "
                + "This continuation captured an async tail; resume it via ResumeAsync "
                + "(by entering the program through Runtime.RunAsync) or call InvokeAsync directly."
        )
        {
            Type = ZType.String,
        };
        var ex = new IrNode.ClrNew("System.NotSupportedException", [], [msg])
        {
            Type = new ZType.ZNamedType("System.NotSupportedException", []),
        };
        return new IrNode.Throw(ex) { Type = objectType };
    }

    private static string SanitizeFuncName(string name)
    {
        // Synthesized identifier name must be a valid C# identifier — replace ZScheme-only chars.
        return name.Replace('-', '_')
            .Replace('?', 'q')
            .Replace('!', 'b')
            .Replace('/', '_')
            .Replace('>', 'g')
            .Replace('<', 'l')
            .Replace('=', 'e');
    }

    private static IReadOnlyDictionary<string, ZType> ExtendEnv(
        IReadOnlyDictionary<string, ZType> env,
        string name,
        ZType type
    )
    {
        var newEnv = new Dictionary<string, ZType>(env) { [name] = type };
        return newEnv;
    }
}

/// <summary>Collects the names of variables free in an IR expression.</summary>
internal static class FreeVarsCollector
{
    public static HashSet<string> Collect(IrNode node)
    {
        var result = new HashSet<string>();
        Walk(node, result, new HashSet<string>());
        return result;
    }

    private static void Walk(IrNode node, HashSet<string> free, HashSet<string> bound)
    {
        switch (node)
        {
            case IrNode.Var v:
                if (!bound.Contains(v.Name))
                    free.Add(v.Name);
                break;

            case IrNode.Let let:
                Walk(let.Value, free, bound);
                bound.Add(let.VarName);
                Walk(let.Body, free, bound);
                bound.Remove(let.VarName);
                break;

            case IrNode.If ifNode:
                Walk(ifNode.Condition, free, bound);
                Walk(ifNode.Then, free, bound);
                Walk(ifNode.Else, free, bound);
                break;

            case IrNode.Call call:
                Walk(call.Function, free, bound);
                foreach (var a in call.Args)
                    Walk(a, free, bound);
                break;

            case IrNode.ClrCall clr:
                foreach (var a in clr.Args)
                    Walk(a, free, bound);
                break;

            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                    Walk(a, free, bound);
                break;

            case IrNode.BinOp bin:
                Walk(bin.Left, free, bound);
                Walk(bin.Right, free, bound);
                break;

            case IrNode.UnaryOp un:
                Walk(un.Operand, free, bound);
                break;

            case IrNode.Match m:
                Walk(m.Scrutinee, free, bound);
                foreach (var arm in m.Arms)
                {
                    var armBound = CollectPatternBound(arm.Pattern);
                    foreach (var n in armBound)
                        bound.Add(n);
                    Walk(arm.Body, free, bound);
                    foreach (var n in armBound)
                        bound.Remove(n);
                }
                break;

            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    Walk(n, free, bound);
                break;

            case IrNode.WithHandlers wh:
                Walk(wh.Body, free, bound);
                foreach (var h in wh.Handlers)
                {
                    if (h.BindingVarName != "_")
                        bound.Add(h.BindingVarName);
                    Walk(h.HandlerBody, free, bound);
                    if (h.BindingVarName != "_")
                        bound.Remove(h.BindingVarName);
                }
                break;

            case IrNode.Throw th:
                Walk(th.Expr, free, bound);
                break;

            case IrNode.Await aw:
                Walk(aw.Expr, free, bound);
                break;

            case IrNode.MethodCall mc:
                Walk(mc.Receiver, free, bound);
                foreach (var a in mc.Args)
                    Walk(a, free, bound);
                break;

            case IrNode.RecordNew rn:
                foreach (var (_, v) in rn.Fields)
                    Walk(v, free, bound);
                break;

            case IrNode.RecordWith rw:
                Walk(rw.Record, free, bound);
                foreach (var (_, v) in rw.Updates)
                    Walk(v, free, bound);
                break;

            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    Walk(e, free, bound);
                break;

            case IrNode.UnionCaseNew un:
                foreach (var a in un.Args)
                    Walk(a, free, bound);
                break;

            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    Walk(e, free, bound);
                break;

            case IrNode.FieldGet fg:
                Walk(fg.Record, free, bound);
                break;

            case IrNode.Closure cl:
                foreach (var v in cl.CapturedValues)
                    Walk(v, free, bound);
                break;

            case IrNode.FuncDef fn:
            {
                var savedBound = new HashSet<string>(bound);
                foreach (var p in fn.Params)
                    bound.Add(p.Name);
                Walk(fn.Body, free, bound);
                bound.Clear();
                foreach (var b in savedBound)
                    bound.Add(b);
                break;
            }

            case IrNode.SetField sf:
                Walk(sf.Value, free, bound);
                break;
        }
    }

    private static HashSet<string> CollectPatternBound(IrPattern p)
    {
        var result = new HashSet<string>();
        Visit(p, result);
        return result;

        static void Visit(IrPattern pat, HashSet<string> r)
        {
            switch (pat)
            {
                case IrPattern.Variable v:
                    r.Add(v.Name);
                    break;
                case IrPattern.Constructor c:
                    foreach (var f in c.Fields)
                        Visit(f, r);
                    break;
                case IrPattern.Tuple t:
                    foreach (var e in t.Elements)
                        Visit(e, r);
                    break;
            }
        }
    }
}

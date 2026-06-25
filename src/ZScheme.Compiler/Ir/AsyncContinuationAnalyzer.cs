using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Ir;

/// Detects continuation capture (call/cc, shift, reset, control, call/comp) inside
/// async contexts that <see cref="ContinuationTransform"/> cannot rewrite.
///
/// Top-level <c>[async]</c> functions are fully supported: when the synthesized continuation
/// function's body contains an <c>await</c>, the transform marks it <c>IsAsync</c>, awaits it
/// from the parent body, and emits an <c>InvokeAsync</c> method on the frame class.
/// <see cref="ZScheme.Runtime.Runtime.ResumeAsync"/> drives those frames without blocking.
///
/// What's still rejected: async methods on object/class declarations that contain an
/// <c>await</c>. <see cref="ContinuationTransform"/> only walks top-level <c>FuncDef</c>s
/// and nested <c>FuncDef</c> inside their bodies — it does not recurse into
/// <see cref="IrNode.ClassDecl"/> or <see cref="IrNode.ObjectExpr"/> method bodies, so a
/// non-tail call to a continuation operator there is not wrapped with a frame extender and
/// the captured continuation list would be missing those frames. Until the transform
/// covers those positions, this case has to be rejected to avoid silently corrupt resumption.
public sealed class AsyncContinuationAnalyzer
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, IrNode.FuncDef> _userFuncs = new();
    private readonly HashSet<string> _tainted = new();

    public AsyncContinuationAnalyzer(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void Analyze(IrNode root)
    {
        CollectUserFuncs(root);
        ComputeTaint();
        ScanContainers(root);
    }

    private void CollectUserFuncs(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    CollectUserFuncs(n);
                break;
            case IrNode.FuncDef fn:
                _userFuncs[fn.Name] = fn;
                break;
        }
    }

    private void ComputeTaint()
    {
        var calleesOf = new Dictionary<string, HashSet<string>>();
        var directlyTainted = new HashSet<string>();

        foreach (var (name, fn) in _userFuncs)
        {
            var callees = new HashSet<string>();
            CollectCallees(fn.Body, callees);
            calleesOf[name] = callees;
            if (ContinuationTransform.ContainsCallCc(fn.Body))
                directlyTainted.Add(name);
        }

        var callers = new Dictionary<string, HashSet<string>>();
        foreach (var (caller, callees) in calleesOf)
        {
            foreach (var callee in callees)
            {
                if (!_userFuncs.ContainsKey(callee))
                    continue;
                if (!callers.TryGetValue(callee, out var set))
                    callers[callee] = set = new HashSet<string>();
                set.Add(caller);
            }
        }

        var work = new Queue<string>();
        foreach (var t in directlyTainted)
        {
            _tainted.Add(t);
            work.Enqueue(t);
        }
        while (work.Count > 0)
        {
            var t = work.Dequeue();
            if (!callers.TryGetValue(t, out var prev))
                continue;
            foreach (var p in prev)
                if (_tainted.Add(p))
                    work.Enqueue(p);
        }
    }

    private static void CollectCallees(IrNode node, HashSet<string> callees)
    {
        switch (node)
        {
            case IrNode.Call c:
                if (c.Function is IrNode.Var v)
                    callees.Add(v.Name);
                else
                    CollectCallees(c.Function, callees);
                foreach (var a in c.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.Closure cl:
                callees.Add(cl.LiftedFuncName);
                foreach (var cv in cl.CapturedValues)
                    CollectCallees(cv, callees);
                break;
            case IrNode.Let l:
                CollectCallees(l.Value, callees);
                CollectCallees(l.Body, callees);
                break;
            case IrNode.If i:
                CollectCallees(i.Condition, callees);
                CollectCallees(i.Then, callees);
                CollectCallees(i.Else, callees);
                break;
            case IrNode.Match m:
                CollectCallees(m.Scrutinee, callees);
                foreach (var a in m.Arms)
                    CollectCallees(a.Body, callees);
                break;
            case IrNode.Seq s:
                foreach (var n in s.Nodes)
                    CollectCallees(n, callees);
                break;
            case IrNode.BinOp b:
                CollectCallees(b.Left, callees);
                CollectCallees(b.Right, callees);
                break;
            case IrNode.UnaryOp u:
                CollectCallees(u.Operand, callees);
                break;
            case IrNode.WithHandlers wh:
                CollectCallees(wh.Body, callees);
                foreach (var h in wh.Handlers)
                    CollectCallees(h.HandlerBody, callees);
                break;
            case IrNode.Throw th:
                CollectCallees(th.Expr, callees);
                break;
            case IrNode.Await aw:
                CollectCallees(aw.Expr, callees);
                break;
            case IrNode.ClrCall cc:
                foreach (var a in cc.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.MethodCall mc:
                CollectCallees(mc.Receiver, callees);
                foreach (var a in mc.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.RecordNew rn:
                foreach (var (_, fv) in rn.Fields)
                    CollectCallees(fv, callees);
                break;
            case IrNode.RecordWith rw:
                CollectCallees(rw.Record, callees);
                foreach (var (_, fv) in rw.Updates)
                    CollectCallees(fv, callees);
                break;
            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    CollectCallees(e, callees);
                break;
            case IrNode.UnionCaseNew un:
                foreach (var a in un.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    CollectCallees(e, callees);
                break;
            case IrNode.FieldGet fg:
                CollectCallees(fg.Record, callees);
                break;
            case IrNode.SetField sf:
                CollectCallees(sf.Value, callees);
                break;
            case IrNode.Cast cast:
                CollectCallees(cast.Expr, callees);
                break;
            case IrNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                    CollectCallees(a, callees);
                break;
            case IrNode.ClassDecl cd:
                foreach (var m in cd.Methods)
                    CollectCallees(m.Body, callees);
                if (cd.Constructor is { } ctor)
                {
                    foreach (var be in ctor.BodyExprs)
                        CollectCallees(be, callees);
                    foreach (var (_, v2) in ctor.FieldSets)
                        CollectCallees(v2, callees);
                    if (ctor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            CollectCallees(s2, callees);
                }
                break;
            case IrNode.ObjectExpr oe:
                foreach (var m in oe.Methods)
                    CollectCallees(m.Body, callees);
                if (oe.Constructor is { } octor)
                {
                    foreach (var be in octor.BodyExprs)
                        CollectCallees(be, callees);
                    foreach (var (_, v2) in octor.FieldSets)
                        CollectCallees(v2, callees);
                    if (octor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            CollectCallees(s2, callees);
                }
                break;
            case IrNode.FuncDef fn:
                CollectCallees(fn.Body, callees);
                break;
        }
    }

    private void ScanContainers(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    ScanContainers(n);
                break;
            case IrNode.FuncDef fn:
                // Top-level async FuncDefs (with await) are fully supported by ContinuationTransform —
                // it splits at non-tail calls, marks the synthesized cont async if needed, and emits
                // InvokeAsync on the frame class. No diagnostic here.
                ScanContainers(fn.Body);
                break;
            case IrNode.ClassDecl cd:
                foreach (var m in cd.Methods)
                {
                    if (m.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(m.Body))
                        CheckUnsupportedContext(
                            m.Body,
                            $"{cd.Name}.{m.Name}",
                            node.Span,
                            "async method"
                        );
                    ScanContainers(m.Body);
                }
                if (cd.Constructor is { } ctor)
                {
                    foreach (var be in ctor.BodyExprs)
                        ScanContainers(be);
                    foreach (var (_, fv) in ctor.FieldSets)
                        ScanContainers(fv);
                    if (ctor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            ScanContainers(s2);
                }
                break;
            case IrNode.ObjectExpr oe:
                foreach (var m in oe.Methods)
                {
                    if (m.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(m.Body))
                        CheckUnsupportedContext(m.Body, m.Name, node.Span, "async method");
                    ScanContainers(m.Body);
                }
                if (oe.Constructor is { } octor)
                {
                    foreach (var be in octor.BodyExprs)
                        ScanContainers(be);
                    foreach (var (_, fv) in octor.FieldSets)
                        ScanContainers(fv);
                    if (octor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            ScanContainers(s2);
                }
                break;
            case IrNode.Let l:
                ScanContainers(l.Value);
                ScanContainers(l.Body);
                break;
            case IrNode.If i:
                ScanContainers(i.Condition);
                ScanContainers(i.Then);
                ScanContainers(i.Else);
                break;
            case IrNode.Match m:
                ScanContainers(m.Scrutinee);
                foreach (var a in m.Arms)
                    ScanContainers(a.Body);
                break;
            case IrNode.Call c:
                ScanContainers(c.Function);
                foreach (var a in c.Args)
                    ScanContainers(a);
                break;
            case IrNode.WithHandlers wh:
                ScanContainers(wh.Body);
                foreach (var h in wh.Handlers)
                    ScanContainers(h.HandlerBody);
                break;
        }
    }

    private void CheckUnsupportedContext(
        IrNode body,
        string ownerName,
        SourceSpan span,
        string contextLabel
    )
    {
        var offender = FindFirstOffender(body);
        if (offender is not null)
            Report(
                ownerName,
                offender.Value.TargetName,
                offender.Value.Span != SourceSpan.None ? offender.Value.Span : span,
                contextLabel
            );
    }

    private (string TargetName, SourceSpan Span)? FindFirstOffender(IrNode node)
    {
        switch (node)
        {
            case IrNode.ClrCall clr when ContinuationOperatorName(clr) is { } opName:
                return (opName, clr.Span);

            case IrNode.Call call:
            {
                var taintedTarget = ResolveTaintedTarget(call.Function);
                if (taintedTarget is not null)
                    return (taintedTarget, call.Span);
                var inFn = FindFirstOffender(call.Function);
                if (inFn is not null)
                    return inFn;
                foreach (var a in call.Args)
                {
                    var r = FindFirstOffender(a);
                    if (r is not null)
                        return r;
                }
                return null;
            }

            case IrNode.Closure cl when _tainted.Contains(cl.LiftedFuncName):
                return (cl.LiftedFuncName, cl.Span);

            case IrNode.Closure cl:
                foreach (var cv in cl.CapturedValues)
                {
                    var r = FindFirstOffender(cv);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.Let l:
                return FindFirstOffender(l.Value) ?? FindFirstOffender(l.Body);

            case IrNode.If i:
                return FindFirstOffender(i.Condition)
                    ?? FindFirstOffender(i.Then)
                    ?? FindFirstOffender(i.Else);

            case IrNode.Match m:
            {
                var r = FindFirstOffender(m.Scrutinee);
                if (r is not null)
                    return r;
                foreach (var arm in m.Arms)
                {
                    var ar = FindFirstOffender(arm.Body);
                    if (ar is not null)
                        return ar;
                }
                return null;
            }

            case IrNode.Seq s:
                foreach (var n in s.Nodes)
                {
                    var r = FindFirstOffender(n);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.BinOp b:
                return FindFirstOffender(b.Left) ?? FindFirstOffender(b.Right);

            case IrNode.UnaryOp u:
                return FindFirstOffender(u.Operand);

            case IrNode.WithHandlers wh:
            {
                var r = FindFirstOffender(wh.Body);
                if (r is not null)
                    return r;
                foreach (var h in wh.Handlers)
                {
                    var hr = FindFirstOffender(h.HandlerBody);
                    if (hr is not null)
                        return hr;
                }
                return null;
            }

            case IrNode.Throw th:
                return FindFirstOffender(th.Expr);

            case IrNode.Await aw:
                return FindFirstOffender(aw.Expr);

            case IrNode.ClrCall cc:
                foreach (var a in cc.Args)
                {
                    var r = FindFirstOffender(a);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                {
                    var r = FindFirstOffender(a);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.MethodCall mc:
            {
                var r = FindFirstOffender(mc.Receiver);
                if (r is not null)
                    return r;
                foreach (var a in mc.Args)
                {
                    var ar = FindFirstOffender(a);
                    if (ar is not null)
                        return ar;
                }
                return null;
            }

            case IrNode.RecordNew rn:
                foreach (var (_, fv) in rn.Fields)
                {
                    var r = FindFirstOffender(fv);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.RecordWith rw:
            {
                var r = FindFirstOffender(rw.Record);
                if (r is not null)
                    return r;
                foreach (var (_, fv) in rw.Updates)
                {
                    var ur = FindFirstOffender(fv);
                    if (ur is not null)
                        return ur;
                }
                return null;
            }

            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                {
                    var r = FindFirstOffender(e);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.UnionCaseNew un:
                foreach (var a in un.Args)
                {
                    var r = FindFirstOffender(a);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                {
                    var r = FindFirstOffender(e);
                    if (r is not null)
                        return r;
                }
                return null;

            case IrNode.FieldGet fg:
                return FindFirstOffender(fg.Record);

            case IrNode.SetField sf:
                return FindFirstOffender(sf.Value);

            case IrNode.Cast cast:
                return FindFirstOffender(cast.Expr);

            case IrNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                {
                    var r = FindFirstOffender(a);
                    if (r is not null)
                        return r;
                }
                return null;

            default:
                return null;
        }
    }

    private string? ResolveTaintedTarget(IrNode arg) =>
        arg switch
        {
            IrNode.Closure cl when _tainted.Contains(cl.LiftedFuncName) => cl.LiftedFuncName,
            IrNode.Var v when _tainted.Contains(v.Name) => v.Name,
            _ => null,
        };

    private static string? ContinuationOperatorName(IrNode.ClrCall clr)
    {
        if (clr.QualifiedTypeName != "ZScheme.Runtime.Runtime")
            return null;
        return clr.MethodName switch
        {
            "CallCcTyped" => "call/cc",
            "Reset" or "ResetAt" => "reset",
            "ShiftTyped" or "ShiftTypedAt" => "shift",
            "ControlTyped" or "ControlTypedAt" => "control",
            "CallCompTyped" or "CallCompTypedAt" => "call/comp",
            _ => null,
        };
    }

    private void Report(string ownerName, string offenderName, SourceSpan span, string contextLabel)
    {
        _diagnostics.Error(
            $"Cannot use continuation capture inside {contextLabel} '{ownerName}': "
                + $"'{offenderName}' may capture a continuation (call/cc, shift, reset, control, "
                + $"or call/comp). ContinuationTransform does not yet rewrite class/object method bodies, "
                + $"so the captured continuation list would be missing the surrounding frames and "
                + $"resumption would corrupt execution. Move the continuation operator to a top-level "
                + $"function, or remove [async] from this method. See docs/CONTINUATIONS.md.",
            span
        );
    }
}

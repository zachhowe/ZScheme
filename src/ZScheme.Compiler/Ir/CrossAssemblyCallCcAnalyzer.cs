using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Ir;

/// Detects (call/cc ...) usage that would cross a precompiled-assembly boundary at
/// runtime. ContinuationTransform only wraps non-tail calls in modules currently being
/// compiled. If a callback that may invoke call/cc is passed to a precompiled higher-order
/// function (e.g. stdlib's list/map), the SaveContinuation exception escapes into stdlib
/// frames that have no handlers, so resumption replays a frame list missing the stdlib
/// portion of the stack and corrupts execution silently.
///
/// Until proper "safety marks" (Pettyjohn et al. 2005) land at runtime, this analyzer
/// rejects the program at compile time when the unsafe pattern is statically detectable.
public sealed class CrossAssemblyCallCcAnalyzer
{
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<string> _precompiledFuncNames;
    private readonly Dictionary<string, IrNode.FuncDef> _userFuncs = new();
    private readonly HashSet<string> _tainted = new();

    public CrossAssemblyCallCcAnalyzer(
        DiagnosticBag diagnostics,
        IEnumerable<string> precompiledFuncNames
    )
    {
        _diagnostics = diagnostics;
        _precompiledFuncNames = precompiledFuncNames.ToHashSet();
    }

    public void Analyze(IrNode root)
    {
        if (_precompiledFuncNames.Count == 0)
            return;

        CollectUserFuncs(root);
        ComputeTaint();
        ScanCallSites(root);
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

        // Reverse edges: callers[g] = { f | f calls g }
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

    private void ScanCallSites(IrNode node)
    {
        switch (node)
        {
            case IrNode.Call c:
                if (c.Function is IrNode.Var v && _precompiledFuncNames.Contains(v.Name))
                    CheckCallbackArgs(v.Name, c.Args);
                ScanCallSites(c.Function);
                foreach (var a in c.Args)
                    ScanCallSites(a);
                break;
            case IrNode.Let l:
                ScanCallSites(l.Value);
                ScanCallSites(l.Body);
                break;
            case IrNode.If i:
                ScanCallSites(i.Condition);
                ScanCallSites(i.Then);
                ScanCallSites(i.Else);
                break;
            case IrNode.Match m:
                ScanCallSites(m.Scrutinee);
                foreach (var a in m.Arms)
                    ScanCallSites(a.Body);
                break;
            case IrNode.Seq s:
                foreach (var n in s.Nodes)
                    ScanCallSites(n);
                break;
            case IrNode.BinOp b:
                ScanCallSites(b.Left);
                ScanCallSites(b.Right);
                break;
            case IrNode.UnaryOp u:
                ScanCallSites(u.Operand);
                break;
            case IrNode.WithHandlers wh:
                ScanCallSites(wh.Body);
                foreach (var h in wh.Handlers)
                    ScanCallSites(h.HandlerBody);
                break;
            case IrNode.Throw th:
                ScanCallSites(th.Expr);
                break;
            case IrNode.Await aw:
                ScanCallSites(aw.Expr);
                break;
            case IrNode.ClrCall cc:
                foreach (var a in cc.Args)
                    ScanCallSites(a);
                break;
            case IrNode.ClrNew cn:
                foreach (var a in cn.Args)
                    ScanCallSites(a);
                break;
            case IrNode.MethodCall mc:
                ScanCallSites(mc.Receiver);
                foreach (var a in mc.Args)
                    ScanCallSites(a);
                break;
            case IrNode.RecordNew rn:
                foreach (var (_, fv) in rn.Fields)
                    ScanCallSites(fv);
                break;
            case IrNode.RecordWith rw:
                ScanCallSites(rw.Record);
                foreach (var (_, fv) in rw.Updates)
                    ScanCallSites(fv);
                break;
            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    ScanCallSites(e);
                break;
            case IrNode.UnionCaseNew un:
                foreach (var a in un.Args)
                    ScanCallSites(a);
                break;
            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    ScanCallSites(e);
                break;
            case IrNode.FieldGet fg:
                ScanCallSites(fg.Record);
                break;
            case IrNode.SetField sf:
                ScanCallSites(sf.Value);
                break;
            case IrNode.Cast cast:
                ScanCallSites(cast.Expr);
                break;
            case IrNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                    ScanCallSites(a);
                break;
            case IrNode.Closure cl:
                foreach (var cv in cl.CapturedValues)
                    ScanCallSites(cv);
                break;
            case IrNode.FuncDef fn:
                ScanCallSites(fn.Body);
                break;
            case IrNode.ClassDecl cd:
                foreach (var m in cd.Methods)
                    ScanCallSites(m.Body);
                if (cd.Constructor is { } ctor)
                {
                    foreach (var be in ctor.BodyExprs)
                        ScanCallSites(be);
                    foreach (var (_, v2) in ctor.FieldSets)
                        ScanCallSites(v2);
                    if (ctor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            ScanCallSites(s2);
                }
                break;
            case IrNode.ObjectExpr oe:
                foreach (var m in oe.Methods)
                    ScanCallSites(m.Body);
                if (oe.Constructor is { } octor)
                {
                    foreach (var be in octor.BodyExprs)
                        ScanCallSites(be);
                    foreach (var (_, v2) in octor.FieldSets)
                        ScanCallSites(v2);
                    if (octor.SuperArgs is { } sa)
                        foreach (var s2 in sa)
                            ScanCallSites(s2);
                }
                break;
        }
    }

    private void CheckCallbackArgs(string precompiledName, IReadOnlyList<IrNode> args)
    {
        foreach (var arg in args)
        {
            var taintedTarget = ResolveTaintedTarget(arg);
            if (taintedTarget is not null)
                Report(precompiledName, taintedTarget);
        }
    }

    private string? ResolveTaintedTarget(IrNode arg) =>
        arg switch
        {
            IrNode.Closure cl when _tainted.Contains(cl.LiftedFuncName) => cl.LiftedFuncName,
            IrNode.Var v when _tainted.Contains(v.Name) => v.Name,
            _ => null,
        };

    private void Report(string precompiledName, string callbackName)
    {
        _diagnostics.Error(
            $"Cannot pass callback '{callbackName}' to precompiled function '{precompiledName}': "
                + $"the callback may capture a continuation (call/cc, shift, reset, control, or call/comp), "
                + $"but continuations cannot be safely captured across a precompiled-assembly boundary "
                + $"(the callee was not compiled with the continuation transform, so its stack frames "
                + $"would be missing from the captured continuation). Rebuild the package with "
                + $"(bundle-source true) in its manifest so the compiler can route it through source "
                + $"compilation when continuation operators are in use, reference the package as a "
                + $":local dependency, or refactor so the callback does not reach a continuation "
                + $"operator. See docs/CONTINUATIONS.md.",
            SourceSpan.None
        );
    }
}

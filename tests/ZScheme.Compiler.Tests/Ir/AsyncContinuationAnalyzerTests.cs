using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

/// <summary>
/// Continuation capture inside top-level <c>[async]</c> functions is now fully supported by
/// <see cref="ContinuationTransform"/> — the synthesized continuation function inherits
/// <c>IsAsync</c> when its body crosses an await, and the frame class emits an
/// <c>InvokeAsync</c> method that <see cref="ZScheme.Runtime.Runtime.ResumeAsync"/> drives
/// without blocking.
///
/// What's still out of reach is continuation capture inside async <em>methods</em> on object
/// or class declarations: <see cref="ContinuationTransform"/> doesn't recurse into class /
/// object method bodies, so a non-tail call there would not be wrapped with a
/// <c>SaveContinuation</c> handler and the captured continuation list would be missing those
/// frames. <see cref="AsyncContinuationAnalyzer"/> rejects only that narrowed case now.
/// </summary>
public class AsyncContinuationAnalyzerTests
{
    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);

    private static IrNode.ClrCall RuntimeCall(string method, params IrNode[] args) =>
        new("ZScheme.Runtime.Runtime", method, args) { Type = ZType.Int };

    private static IrNode.ClrCall CallCc(IrNode userFn) => RuntimeCall("CallCcTyped", userFn);

    private static IrNode.Await Await(IrNode expr) => new(expr) { Type = ZType.Int };

    private static IrNode.FuncDef AsyncFunc(string name, IrNode body, params IrParam[] @params) =>
        new(name, @params, ZType.Int, body, IsSelfRecursive: false, IsAsync: true)
        {
            Type = TaskInt,
        };

    private static IrNode.FuncDef Func(string name, IrNode body, params IrParam[] @params) =>
        new(name, @params, ZType.Int, body, IsSelfRecursive: false) { Type = ZType.Int };

    private static (DiagnosticBag Diagnostics, AsyncContinuationAnalyzer Analyzer) MakeAnalyzer()
    {
        var diags = new DiagnosticBag();
        var analyzer = new AsyncContinuationAnalyzer(diags);
        return (diags, analyzer);
    }

    [Fact]
    public void DoesNotReport_DirectCallCcInsideTopLevelAsyncWithAwait()
    {
        // Now supported: ContinuationTransform splits the let and marks the cont async.
        var fetch = new IrNode.Var("fetch") { Type = TaskInt };
        var awaitedFetch = Await(fetch);
        var body = new IrNode.Let(
            "v",
            awaitedFetch,
            CallCc(new IrNode.Var("k") { Type = ZType.Int })
        )
        {
            Type = ZType.Int,
        };

        var fn = AsyncFunc("f", body);
        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([fn]));

        Assert.False(
            diags.HasErrors,
            "Top-level async + call/cc must be allowed: "
                + string.Join("\n", diags.Diagnostics.Select(d => d.Message))
        );
    }

    [Fact]
    public void DoesNotReport_DirectShiftResetControlCallCompInsideTopLevelAsync()
    {
        foreach (
            var method in new[]
            {
                "ShiftTyped",
                "Reset",
                "ControlTyped",
                "CallCompTyped",
                "ShiftTypedAt",
                "ResetAt",
                "ControlTypedAt",
                "CallCompTypedAt",
            }
        )
        {
            var body = new IrNode.Let(
                "v",
                Await(new IrNode.Var("fetch") { Type = TaskInt }),
                RuntimeCall(method, new IrNode.Var("k") { Type = ZType.Int })
            )
            {
                Type = ZType.Int,
            };

            var fn = AsyncFunc("f", body);
            var (diags, analyzer) = MakeAnalyzer();
            analyzer.Analyze(new IrNode.Seq([fn]));

            Assert.False(
                diags.HasErrors,
                $"{method} in top-level async must be allowed: "
                    + string.Join("\n", diags.Diagnostics.Select(d => d.Message))
            );
        }
    }

    [Fact]
    public void DoesNotReport_TransitiveCallCcThroughHelperFromTopLevelAsync()
    {
        var helper = Func("helper", CallCc(new IrNode.Var("k") { Type = ZType.Int }));

        var asyncBody = new IrNode.Let(
            "v",
            Await(new IrNode.Var("fetch") { Type = TaskInt }),
            new IrNode.Call(new IrNode.Var("helper") { Type = ZType.Int }, []) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };
        var fn = AsyncFunc("f", asyncBody);

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([helper, fn]));

        Assert.False(
            diags.HasErrors,
            "Transitive call/cc from top-level async must be allowed: "
                + string.Join("\n", diags.Diagnostics.Select(d => d.Message))
        );
    }

    [Fact]
    public void DoesNotReport_AsyncWithoutAwait_EvenIfBodyHasCallCc()
    {
        var fn = AsyncFunc("f", CallCc(new IrNode.Var("k") { Type = ZType.Int }));

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([fn]));

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void DoesNotReport_NonAsyncFunctionWithCallCc()
    {
        var fn = Func("f", CallCc(new IrNode.Var("k") { Type = ZType.Int }));

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([fn]));

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void DoesNotReport_NonAsyncFunctionCallingTaintedHelper()
    {
        var helper = Func("helper", CallCc(new IrNode.Var("k") { Type = ZType.Int }));
        var caller = Func(
            "caller",
            new IrNode.Call(new IrNode.Var("helper") { Type = ZType.Int }, []) { Type = ZType.Int }
        );

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([helper, caller]));

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void Reports_CallCcInsideAsyncClassMethodWithAwait()
    {
        // Async methods on a ClassDecl are NOT reached by ContinuationTransform, so they
        // remain unsupported and the analyzer keeps rejecting them.
        var methodBody = new IrNode.Let(
            "v",
            Await(new IrNode.Var("fetch") { Type = TaskInt }),
            CallCc(new IrNode.Var("k") { Type = ZType.Int })
        )
        {
            Type = ZType.Int,
        };
        var method = new IrObjectMethod("m", [], ZType.Int, methodBody, IsAsync: true);

        var classDecl = new IrNode.ClassDecl(
            Name: "C",
            TypeParams: [],
            InterfaceNames: [],
            Fields: [],
            Methods: [method]
        );

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([classDecl]));

        Assert.True(diags.HasErrors);
        Assert.Contains("'C.m'", diags.Diagnostics[0].Message);
        Assert.Contains("call/cc", diags.Diagnostics[0].Message);
        Assert.Contains("async method", diags.Diagnostics[0].Message);
    }

    [Fact]
    public void Reports_CallCcInsideAsyncObjectMethodWithAwait()
    {
        var methodBody = new IrNode.Let(
            "v",
            Await(new IrNode.Var("fetch") { Type = TaskInt }),
            RuntimeCall("ShiftTyped", new IrNode.Var("k") { Type = ZType.Int })
        )
        {
            Type = ZType.Int,
        };
        var method = new IrObjectMethod("doIt", [], ZType.Int, methodBody, IsAsync: true);

        var objectExpr = new IrNode.ObjectExpr([], [method]);

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([objectExpr]));

        Assert.True(diags.HasErrors);
        Assert.Contains("'doIt'", diags.Diagnostics[0].Message);
        Assert.Contains("'shift'", diags.Diagnostics[0].Message);
    }

    [Fact]
    public void DoesNotReport_NonAsyncMethodWithCallCc()
    {
        var methodBody = CallCc(new IrNode.Var("k") { Type = ZType.Int });
        var method = new IrObjectMethod("m", [], ZType.Int, methodBody, IsAsync: false);

        var classDecl = new IrNode.ClassDecl(
            Name: "C",
            TypeParams: [],
            InterfaceNames: [],
            Fields: [],
            Methods: [method]
        );

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([classDecl]));

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void DoesNotReport_AsyncClassMethodWithoutAwait_EvenIfHasCallCc()
    {
        // No await ⇒ method lowers to plain Task.FromResult wrapper, so capture remains safe.
        var methodBody = CallCc(new IrNode.Var("k") { Type = ZType.Int });
        var method = new IrObjectMethod("m", [], ZType.Int, methodBody, IsAsync: true);

        var classDecl = new IrNode.ClassDecl(
            Name: "C",
            TypeParams: [],
            InterfaceNames: [],
            Fields: [],
            Methods: [method]
        );

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([classDecl]));

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void Reports_DiagnosticContainsAsyncMethodNameAndDocsLink()
    {
        var methodBody = new IrNode.Let(
            "v",
            Await(new IrNode.Var("fetch") { Type = TaskInt }),
            CallCc(new IrNode.Var("k") { Type = ZType.Int })
        )
        {
            Type = ZType.Int,
        };
        var method = new IrObjectMethod("compute_async", [], ZType.Int, methodBody, IsAsync: true);
        var classDecl = new IrNode.ClassDecl(
            Name: "Worker",
            TypeParams: [],
            InterfaceNames: [],
            Fields: [],
            Methods: [method]
        );

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(new IrNode.Seq([classDecl]));

        Assert.True(diags.HasErrors);
        Assert.Contains("'Worker.compute_async'", diags.Diagnostics[0].Message);
        Assert.Contains("docs/CONTINUATIONS.md", diags.Diagnostics[0].Message);
    }
}

using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

/// <summary>
/// Unit tests for the IR shape <see cref="ContinuationTransform"/> produces when the parent
/// FuncDef is async and the synthesized continuation function's body crosses an
/// <see cref="IrNode.Await"/>. The post-transform shape is the contract the C# / IL
/// emitters depend on for async-frame replay.
/// </summary>
public class ContinuationTransformAsyncTests
{
    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);

    private static IrNode.ClrCall CallCc(IrNode userFn) =>
        new("ZScheme.Runtime.Runtime", "CallCcTyped", [userFn]) { Type = ZType.Int };

    private static IrNode.Var IntVar(string n) => new(n) { Type = ZType.Int };

    private static IrNode.FuncDef AsyncFunc(string name, IrNode body, params IrParam[] @params) =>
        new(name, @params, ZType.Int, body, IsSelfRecursive: false, IsAsync: true)
        {
            Type = TaskInt,
        };

    private static IrNode.FuncDef SyncFunc(string name, IrNode body, params IrParam[] @params) =>
        new(name, @params, ZType.Int, body, IsSelfRecursive: false) { Type = ZType.Int };

    [Fact]
    public void Sync_NonTailCallCc_ProducesSyncCont_NoIsAsyncFlag()
    {
        // Sync parent, sync body — synthesized cont stays sync, frame has only Invoke.
        var body = new IrNode.Let(
            "v",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }) with
            {
                IsTailCall = false,
            },
            new IrNode.BinOp("+", IntVar("v"), new IrNode.IntConst(1) { Type = ZType.Int })
            {
                Type = ZType.Int,
            }
        )
        {
            Type = ZType.Int,
        };
        var fn = SyncFunc("f", body);

        var transformed = (IrNode.Seq)new ContinuationTransform().Transform(new IrNode.Seq([fn]));

        var cont = transformed
            .Nodes.OfType<IrNode.FuncDef>()
            .Single(f => f.Name.StartsWith("__cont_"));
        var frame = transformed
            .Nodes.OfType<IrNode.ClassDecl>()
            .Single(c => c.Name.StartsWith("__Frame_"));

        Assert.False(cont.IsAsync);
        Assert.Single(frame.Methods);
        Assert.Equal("Invoke", frame.Methods[0].Name);
    }

    [Fact]
    public void AsyncParent_PureSyncBody_ProducesSyncCont()
    {
        // Async parent but the post-call body is pure-sync (no await) — cont stays sync.
        // No async overhead is paid when not needed.
        var body = new IrNode.Let(
            "v",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }) with
            {
                IsTailCall = false,
            },
            new IrNode.BinOp("+", IntVar("v"), new IrNode.IntConst(1) { Type = ZType.Int })
            {
                Type = ZType.Int,
            }
        )
        {
            Type = ZType.Int,
        };
        var fn = AsyncFunc("f", body);

        var transformed = (IrNode.Seq)new ContinuationTransform().Transform(new IrNode.Seq([fn]));

        var cont = transformed
            .Nodes.OfType<IrNode.FuncDef>()
            .Single(f => f.Name.StartsWith("__cont_"));
        Assert.False(cont.IsAsync);
    }

    [Fact]
    public void AsyncParent_BodyContainsAwait_ProducesAsyncCont_AndAwaitedTailCall()
    {
        // Async parent, post-call body contains an await — cont gets IsAsync=true; the
        // parent's tail call to cont is wrapped in Await; the frame class has BOTH
        // InvokeAsync (the runtime-driven path) and a sync Invoke fallback that throws.
        var awaitedCall = new IrNode.Await(
            new IrNode.Call(new IrNode.Var("g") { Type = TaskInt }, []) { Type = TaskInt }
        )
        {
            Type = ZType.Int,
        };
        var body = new IrNode.Let(
            "v",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }) with
            {
                IsTailCall = false,
            },
            awaitedCall
        )
        {
            Type = ZType.Int,
        };
        var fn = AsyncFunc("f", body);

        var transformed = (IrNode.Seq)new ContinuationTransform().Transform(new IrNode.Seq([fn]));

        var cont = transformed
            .Nodes.OfType<IrNode.FuncDef>()
            .Single(f => f.Name.StartsWith("__cont_"));
        var frame = transformed
            .Nodes.OfType<IrNode.ClassDecl>()
            .Single(c => c.Name.StartsWith("__Frame_"));

        Assert.True(cont.IsAsync);

        // Frame has both methods.
        Assert.Equal(2, frame.Methods.Count);
        Assert.Contains(frame.Methods, m => m.Name == "InvokeAsync" && m.IsAsync);
        Assert.Contains(frame.Methods, m => m.Name == "Invoke" && !m.IsAsync);

        // InvokeAsync return type is Task<object>.
        var invokeAsync = frame.Methods.Single(m => m.Name == "InvokeAsync");
        var taskRet = Assert.IsType<ZType.ZNamedType>(invokeAsync.ReturnType);
        Assert.Equal("Task", taskRet.Name);
        Assert.Single(taskRet.TypeArgs);

        // Sync Invoke body throws NotSupportedException.
        var invokeSync = frame.Methods.Single(m => m.Name == "Invoke");
        var throwNode = Assert.IsType<IrNode.Throw>(invokeSync.Body);
        var clrNew = Assert.IsType<IrNode.ClrNew>(throwNode.Expr);
        Assert.Equal("System.NotSupportedException", clrNew.QualifiedTypeName);

        // Parent body's tail call to the cont is wrapped in Await.
        var transformedFn = transformed.Nodes.OfType<IrNode.FuncDef>().Single(f => f.Name == "f");
        var parentLet = Assert.IsType<IrNode.Let>(transformedFn.Body);
        var parentTail = Assert.IsType<IrNode.Await>(parentLet.Body);
        var parentCall = Assert.IsType<IrNode.Call>(parentTail.Expr);
        var parentCallTarget = Assert.IsType<IrNode.Var>(parentCall.Function);
        Assert.StartsWith("__cont_", parentCallTarget.Name);
    }

    [Fact]
    public void TransformProducedAnyAsyncFrameFlag_TrueOnlyWhenAsyncFrameSynthesized()
    {
        // No async frame for sync parent.
        {
            var fn = SyncFunc(
                "f",
                new IrNode.Let(
                    "v",
                    CallCc(new IrNode.Var("k") { Type = ZType.Int }) with
                    {
                        IsTailCall = false,
                    },
                    new IrNode.BinOp("+", IntVar("v"), new IrNode.IntConst(1) { Type = ZType.Int })
                    {
                        Type = ZType.Int,
                    }
                )
                {
                    Type = ZType.Int,
                }
            );
            var t = new ContinuationTransform();
            t.Transform(new IrNode.Seq([fn]));
            Assert.False(t.ProducedAnyAsyncFrame);
        }

        // Async frame produced when parent is async + body has await.
        {
            var awaitedCall = new IrNode.Await(
                new IrNode.Call(new IrNode.Var("g") { Type = TaskInt }, []) { Type = TaskInt }
            )
            {
                Type = ZType.Int,
            };
            var fn = AsyncFunc(
                "f",
                new IrNode.Let(
                    "v",
                    CallCc(new IrNode.Var("k") { Type = ZType.Int }) with
                    {
                        IsTailCall = false,
                    },
                    awaitedCall
                )
                {
                    Type = ZType.Int,
                }
            );
            var t = new ContinuationTransform();
            t.Transform(new IrNode.Seq([fn]));
            Assert.True(t.ProducedAnyAsyncFrame);
        }
    }
}

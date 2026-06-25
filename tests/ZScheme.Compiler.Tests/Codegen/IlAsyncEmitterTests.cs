using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AsmResolver.DotNet;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Tests for <see cref="IlAsyncEmitter" />'s async state-machine emission. The emitter is
///     deliberately NOT unit-tested standalone: its entry point takes <see cref="IlEmitter" />'s
///     internal mid-emission state (module, type table, emit context), so a standalone harness
///     would replicate half of the host's setup and test only mock plumbing (contra
///     docs/MOCKS.md). Instead these tests drive it through <see cref="IlEmitter.Emit" /> over
///     hand-built async IR and assert on the produced PE — the state machine's structural
///     contract — plus one execution-level check that the machine actually runs.
/// </summary>
public class IlAsyncEmitterTests
{
    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);
    private static readonly ZType TaskUnit = new ZType.ZNamedType("Task", []);

    /// <summary>
    ///     An async function with no await in its body: (define-async (<name> [x : Int])
    ///     : (Task Int) (+ x 1)). Emitted via the fast path (no state machine).
    /// </summary>
    private static IrNode.FuncDef AsyncAddOne(string name)
    {
        return new IrNode.FuncDef(
            name,
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp(
                "+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.IntConst(1) { Type = ZType.Int }
            )
            {
                Type = ZType.Int,
            },
            IsSelfRecursive: false,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([ZType.Int], TaskInt),
        };
    }

    /// <summary>
    ///     An async function that awaits its Task parameter:
    ///     (define-async (<name> [t : (Task Int)]) : (Task Int) (await t)).
    ///     Contains an await, so it gets a real state machine.
    /// </summary>
    private static IrNode.FuncDef AsyncAwaitParam(string name)
    {
        return new IrNode.FuncDef(
            name,
            [new IrParam("t", TaskInt)],
            ZType.Int,
            new IrNode.Await(new IrNode.Var("t") { Type = TaskInt }) { Type = ZType.Int },
            IsSelfRecursive: false,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([TaskInt], TaskInt),
        };
    }

    private static byte[] Emit(params IrNode[] forms)
    {
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("AsyncSmTests", diag, "TestClass");
        var bytes = emitter.Emit(new IrNode.Seq(forms) { Type = ZType.Unit });
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(bytes);
        return bytes;
    }

    private static IReadOnlyList<TypeDefinition> StateMachineTypes(byte[] peBytes)
    {
        var module = ModuleDefinition.FromBytes(peBytes);
        return module
            .GetAllTypes()
            .Where(t => t.Name is not null && t.Name.Value.Contains(">d__"))
            .ToList();
    }

    [Fact]
    public void AsyncFuncDefWithoutAwaitProducesNoStateMachine()
    {
        // The fast path: an async fn whose body never awaits is emitted as a plain
        // method whose result is wrapped in a completed Task — no state machine.
        var bytes = Emit(AsyncAddOne("add-async"));

        Assert.Empty(StateMachineTypes(bytes));
    }

    [Fact]
    public void AwaitingAsyncFuncDefProducesStateMachineImplementingIAsyncStateMachine()
    {
        var bytes = Emit(AsyncAwaitParam("add-async"));

        var sm = Assert.Single(StateMachineTypes(bytes));
        // Sanitize PascalCases kebab-case names: add-async -> AddAsync.
        Assert.StartsWith("<AddAsync>d__", sm.Name!.Value);

        Assert.Contains(
            sm.Interfaces,
            i => i.Interface?.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"
        );
        Assert.Contains(sm.Methods, m => m.Name == "MoveNext");
        Assert.Contains(sm.Methods, m => m.Name == "SetStateMachine");
    }

    [Fact]
    public void TaskOfIntUsesGenericBuilderField()
    {
        var bytes = Emit(AsyncAwaitParam("add-async"));

        var sm = Assert.Single(StateMachineTypes(bytes));
        var builder = Assert.Single(sm.Fields, f => f.Name == "__builder");
        Assert.StartsWith(
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
            builder.Signature!.FieldType.FullName
        );
    }

    [Fact]
    public void TaskUnitUsesNonGenericBuilderField()
    {
        // (define-async (do-work [t : Task]) : Task (await t))
        var fn = new IrNode.FuncDef(
            "do-work",
            [new IrParam("t", TaskUnit)],
            ZType.Unit,
            new IrNode.Await(new IrNode.Var("t") { Type = TaskUnit }) { Type = ZType.Unit },
            IsSelfRecursive: false,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([TaskUnit], TaskUnit),
        };

        var bytes = Emit(fn);

        var sm = Assert.Single(StateMachineTypes(bytes));
        var builder = Assert.Single(sm.Fields, f => f.Name == "__builder");
        Assert.Equal(
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
            builder.Signature!.FieldType.FullName
        );
    }

    [Fact]
    public void TwoAsyncFunctionsGetDistinctStateMachineSuffixes()
    {
        var bytes = Emit(AsyncAwaitParam("first-async"), AsyncAwaitParam("second-async"));

        var machines = StateMachineTypes(bytes);
        Assert.Equal(2, machines.Count);
        var names = machines.Select(m => m.Name!.Value).OrderBy(n => n).ToList();
        Assert.Contains(names, n => n.EndsWith("d__0"));
        Assert.Contains(names, n => n.EndsWith("d__1"));
    }

    [Fact]
    public void AwaitingAsyncFunctionProducesAwaiterStateInMoveNext()
    {
        // (define-async (outer [x : Int]) : (Task Int) (await (add-async x)))
        var outer = new IrNode.FuncDef(
            "outer-async",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("add-async")
                    {
                        Type = new ZType.ZFuncType([ZType.Int], TaskInt),
                    },
                    [new IrNode.Var("x") { Type = ZType.Int }]
                )
                {
                    Type = TaskInt,
                }
            )
            {
                Type = ZType.Int,
            },
            IsSelfRecursive: false,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([ZType.Int], TaskInt),
        };

        var bytes = Emit(AsyncAddOne("add-async"), outer);

        var outerSm = Assert.Single(
            StateMachineTypes(bytes),
            t => t.Name!.Value.StartsWith("<OuterAsync>")
        );
        // A suspension point hoists a TaskAwaiter<T> into a state-machine field.
        Assert.Contains(
            outerSm.Fields,
            f =>
                f.Signature!.FieldType.FullName.StartsWith(
                    "System.Runtime.CompilerServices.TaskAwaiter"
                )
        );
        var moveNext = Assert.Single(outerSm.Methods, m => m.Name == "MoveNext");
        Assert.NotNull(moveNext.CilMethodBody);
        Assert.True(moveNext.CilMethodBody.Instructions.Count > 10);
    }

    #region TCO loop mode

    /// <summary>
    ///     `(define-async (f [n : Int]) : (Task Int) (if (= n 0) n (await (g (await (f n))))))`
    ///     — an awaited tail self-call (which TailCallLowering turns into a TcoJump, making the
    ///     function a loop) plus a second, non-tail await so the body still needs a real state
    ///     machine. That combination is the whole point of MoveNext's loop mode.
    /// </summary>
    private static IrNode.FuncDef AsyncTailRecursive(string name)
    {
        var selfCall = new IrNode.Call(
            new IrNode.Var(name) { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
            [new IrNode.Var("n") { Type = ZType.Int }]
        )
        {
            Type = TaskInt,
        };

        var body = new IrNode.If(
            new IrNode.BinOp(
                "=",
                new IrNode.Var("n") { Type = ZType.Int },
                new IrNode.IntConst(0) { Type = ZType.Int }
            )
            {
                Type = ZType.Bool,
            },
            new IrNode.Var("n") { Type = ZType.Int },
            // A non-tail await keeps ContainsAwait true, so the state machine survives TCO.
            new IrNode.Let(
                "hoisted",
                new IrNode.Await(
                    new IrNode.Call(
                        new IrNode.Var("add-async")
                        {
                            Type = new ZType.ZFuncType([ZType.Int], TaskInt),
                        },
                        [new IrNode.Var("n") { Type = ZType.Int }]
                    )
                    {
                        Type = TaskInt,
                    }
                )
                {
                    Type = ZType.Int,
                },
                new IrNode.Await(selfCall) { Type = ZType.Int },
                ZType.Int
            )
            {
                Type = ZType.Int,
            }
        )
        {
            Type = ZType.Int,
        };

        return new IrNode.FuncDef(
            name,
            [new IrParam("n", ZType.Int)],
            ZType.Int,
            body,
            IsSelfRecursive: true,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([ZType.Int], TaskInt),
        };
    }

    private static MethodDefinition TcoLoopMoveNext()
    {
        var bytes = Emit(AsyncAddOne("add-async"), AsyncTailRecursive("tco-async"));
        var sm = Assert.Single(StateMachineTypes(bytes), t => t.Name!.Value.Contains("TcoAsync"));
        return Assert.Single(sm.Methods, m => m.Name == "MoveNext");
    }

    [Fact]
    public void TcoLoopAsyncFuncDefStillProducesOneStateMachine()
    {
        // Looping does not change the emission strategy: a body that still awaits keeps exactly
        // one state machine, which is the point — N nested machines collapse into this one.
        var bytes = Emit(AsyncAddOne("add-async"), AsyncTailRecursive("tco-async"));

        var sm = Assert.Single(StateMachineTypes(bytes), t => t.Name!.Value.Contains("TcoAsync"));
        Assert.Contains(
            sm.Interfaces,
            i => i.Interface?.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"
        );
    }

    [Fact]
    public void TcoLoopMoveNextHasNoRetInsideTheTryRegion()
    {
        // `ret` inside a protected region is invalid metadata that AsmResolver will happily
        // write and only ilverify/the JIT rejects, so the sync loop walker's `Ret` leaf must
        // never reach MoveNext — a leaf there stores the result and `Leave`s instead.
        var moveNext = TcoLoopMoveNext();
        var body = moveNext.CilMethodBody!;
        var handler = Assert.Single(body.ExceptionHandlers);

        var tryStart = handler.TryStart!.Offset;
        var handlerEnd = handler.HandlerEnd!.Offset;
        Assert.DoesNotContain(
            body.Instructions,
            i =>
                i.OpCode.Code == AsmResolver.PE.DotNet.Cil.CilCode.Ret
                && i.Offset >= tryStart
                && i.Offset < handlerEnd
        );
    }

    [Fact]
    public void TcoLoopMoveNextBranchesBackwards()
    {
        // The back-edge itself: a Br to an earlier offset. Without it the "loop" would just be
        // straight-line code that fell out of the body.
        var moveNext = TcoLoopMoveNext();
        var body = moveNext.CilMethodBody!;
        var handler = Assert.Single(body.ExceptionHandlers);

        Assert.Contains(
            body.Instructions,
            i =>
                i.OpCode.Code
                    is AsmResolver.PE.DotNet.Cil.CilCode.Br
                        or AsmResolver.PE.DotNet.Cil.CilCode.Br_S
                && i.Operand is AsmResolver.PE.DotNet.Cil.ICilLabel target
                && target.Offset < i.Offset
                && i.Offset >= handler.TryStart!.Offset
                && i.Offset < handler.TryEnd!.Offset
        );
    }

    [Fact]
    public void TcoLoopMoveNextDoesNotStarg()
    {
        // MoveNext() is a nullary instance method: it has no argument slots, so the synchronous
        // back-edge's `Starg` is not merely wrong here but unencodable. Parameters live as
        // locals, and the jump writes them with Stloc.
        Assert.DoesNotContain(
            TcoLoopMoveNext().CilMethodBody!.Instructions,
            i =>
                i.OpCode.Code
                    is AsmResolver.PE.DotNet.Cil.CilCode.Starg
                        or AsmResolver.PE.DotNet.Cil.CilCode.Starg_S
        );
    }

    #endregion

    [Fact]
    public async Task EmittedStateMachineActuallyRuns()
    {
        var outer = new IrNode.FuncDef(
            "outer-async",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("add-async")
                    {
                        Type = new ZType.ZFuncType([ZType.Int], TaskInt),
                    },
                    [new IrNode.Var("x") { Type = ZType.Int }]
                )
                {
                    Type = TaskInt,
                }
            )
            {
                Type = ZType.Int,
            },
            IsSelfRecursive: false,
            IsAsync: true
        )
        {
            Type = new ZType.ZFuncType([ZType.Int], TaskInt),
        };

        var bytes = Emit(AsyncAddOne("add-async"), outer);

        var ctx = new AssemblyLoadContext("IlAsyncEmitterTests", isCollectible: true);
        ctx.Resolving += (c, name) =>
            name.Name == "ZScheme.Runtime"
                ? c.LoadFromAssemblyPath(typeof(global::ZScheme.Runtime.ZSymbol).Assembly.Location)
                : null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var asm = ctx.LoadFromStream(ms);
            var entry = asm.GetTypes().First(t => t.Name == "TestClass");

            var method = entry.GetMethods().Single(m => m.Name == "OuterAsync");
            var task = Assert.IsType<Task<int>>(method.Invoke(null, [41]));
            Assert.Equal(42, await task);
        }
        finally
        {
            ctx.Unload();
        }
    }
}

using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;
using Compilation = ZScheme.Compiler.Pipeline.Compilation;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end coverage for Unit-typed values flowing into stack-consuming
// positions on the IL backend: call arguments (the primary EmitCall branches),
// TcoJump back-edge arguments, closure captures, ctor set! values, plus
// statement positions and let bindings of Unit-typed variables. EmitArgNode
// materializes default(ValueTuple) for Unit-typed arguments and relies on
// EmitNode pushing nothing for ANY Unit-typed node, Var included (see the Var
// case); before that invariant was normalized, the most common call forms —
// `(f ())`, TCO loops with a Unit parameter, closures capturing a Unit value —
// crashed with AsmResolver's StackImbalanceException. The entry point is a
// zero-arg `Compute` returning int.
public class UnitArgumentIlTests
{
    private static CompilerOptions Options(bool closureConversion)
    {
        return new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            EnableClosureConversion = closureConversion,
        };
    }

    private static int RunIl(string source, bool closureConversion = false)
    {
        var result = new Compilation(Options(closureConversion)).Compile(source);
        Assert.True(
            result.Success,
            "IL compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
        return Invoke(asm);
    }

    private static int Invoke(Assembly asm)
    {
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        try
        {
            return (int)method.Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    [Fact]
    public void PlainCall_UnitArgument()
    {
        // `(f ())`: a Unit literal argument through the primary EmitCall branch.
        Assert.Equal(
            42,
            RunIl(
                """
                (define (f [x : Unit]) : Int 42)
                (define (Compute) : Int (f ()))
                """
            )
        );
    }

    [Fact]
    public void TcoLoop_UnitConstantArgument()
    {
        // TcoJump back-edge with a Unit constant argument: the jump loop must
        // materialize the Unit slot when reassigning parameter slots.
        Assert.Equal(
            0,
            RunIl(
                """
                (define (spin [x : Unit] [n : Int]) : Int
                  (if (= n 0) n (spin () (- n 1))))
                (define (Compute) : Int (spin () 5))
                """
            )
        );
    }

    [Fact]
    public void TcoLoop_UnitVariableArgument()
    {
        // TcoJump back-edge with a Unit-typed *variable* argument. Pins the Var
        // invariant: EmitNode must push nothing for a Unit-typed read, and
        // EmitArgNode must materialize the slot — a double-push regression
        // fails here with stack imbalance.
        Assert.Equal(
            0,
            RunIl(
                """
                (define (spin [x : Unit] [n : Int]) : Int
                  (if (= n 0) n (spin x (- n 1))))
                (define (Compute) : Int (spin () 5))
                """
            )
        );
    }

    [Fact]
    public void StatementPosition_UnitVariable()
    {
        // `(begin x 42)` with x : Unit — a statement-position read of a
        // Unit-typed variable must leave the stack empty.
        Assert.Equal(
            42,
            RunIl(
                """
                (define (f [x : Unit]) : Int
                  (begin x 42))
                (define (Compute) : Int (f ()))
                """
            )
        );
    }

    [Fact]
    public void LetBinding_UnitValue()
    {
        // Let-bound to a Unit-typed variable: EmitLetBinding skips the Stloc,
        // so the binding must not leave a value on the stack.
        Assert.Equal(
            42,
            RunIl(
                """
                (define (f [x : Unit]) : Int
                  (let ([y x]) 42))
                (define (Compute) : Int (f ()))
                """
            )
        );
    }

    [Fact]
    public void Closure_CapturesUnitValue()
    {
        // Captured value in the closure display fill loop: the Stfld must
        // receive a materialized Unit (default(ValueTuple)).
        Assert.Equal(
            42,
            RunIl(
                """
                (define (make [x : Unit])
                  (lambda () (begin x 42)))
                (define (Compute) : Int ((make ())))
                """,
                closureConversion: true
            )
        );
    }
}

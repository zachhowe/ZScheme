using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class TypeInfererShiftResetTests
{
    private static (AstNode.Program Program, DiagnosticBag Diagnostics) InferProgram(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diag);
        inferer.Infer(program, env);
        inferer.Resolve(program);

        return (program, diag);
    }

    [Fact]
    public void Reset_TypeMatchesBody()
    {
        var (prog, diag) = InferProgram("(reset 5)");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(ZType.Int, prog.TopLevelForms[0].ResolvedType);
    }

    [Fact]
    public void Reset_WithUnusedShift_TypesAsAnswer()
    {
        // (shift k 7) discards k and returns 7. Reset's answer type is Int (= shift body type).
        var (prog, diag) = InferProgram("(reset (shift k 7))");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(ZType.Int, prog.TopLevelForms[0].ResolvedType);
    }

    [Fact]
    public void Reset_WithComposedShift_TypesAsAnswer()
    {
        // (+ 1 (shift k (k 10))) — k:Int->Int, body=Int, alpha=Int. Reset answer = Int.
        var (prog, diag) = InferProgram("(reset (+ 1 (shift k (k 10))))");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(ZType.Int, prog.TopLevelForms[0].ResolvedType);
    }

    [Fact]
    public void Shift_AnswerTypeMismatch_Reports()
    {
        // (shift k "hi") inside a reset whose body must be Int — mismatch.
        var (_, diag) = InferProgram("(+ 1 (reset (shift k \"hi\")))");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void Shift_OutsideReset_Reports()
    {
        var (_, diag) = InferProgram("(shift k 1)");
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("(shift ...) used outside any enclosing (reset ...)")
        );
    }

    [Fact]
    public void Shift_OutsideReset_InsideFunction_Reports()
    {
        // The answer-type stack is dynamic-with-respect-to-the-inferer. A function body that
        // uses shift but isn't textually inside a reset must still be reported, since the
        // inferer can't see runtime callers.
        var source =
            @"
            (define (bad) : Int (shift k 1))
            (reset (bad))";
        var (_, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void Shift_MultiShot_TypesCorrectly()
    {
        // (+ (k 1) (k 2)) — k composed twice; both calls must unify to Int.
        var (prog, diag) = InferProgram("(reset (shift k (+ (k 1) (k 2))))");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(ZType.Int, prog.TopLevelForms[0].ResolvedType);
    }

    [Fact]
    public void Reset_NestedDifferentTypes_EachShiftMatchesItsOwnReset()
    {
        // Inner reset with a String shift body, outer with Int. Each shift's k must unify
        // against its own enclosing reset's answer type.
        var source =
            @"
            (reset
              (let ([s (reset (shift k1 ""x""))])
                (+ 1 (shift k2 (k2 10)))))";
        var (_, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AnswerTypeStack_PoppedOnSiblingResets()
    {
        // After the first (reset ...) finishes inferring, its answer type must be popped so a
        // subsequent shift outside any reset is correctly rejected.
        var source =
            @"
            (define (a) : Int (reset (shift k (k 1))))
            (define (b) : Int (shift k 1))";
        var (_, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("(shift ...) used outside any enclosing (reset ...)")
        );
    }
}

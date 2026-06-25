using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
/// Type-inference coverage for multi-value continuation auto-bundling. The contract is:
/// (k v1 v2 ... vn) where k is a continuation parameter and n ≥ 2 unifies α with a
/// ValueTuple[T1..Tn] and rewrites the call to a single-arg <c>TupleNew</c>.
/// </summary>
public class TypeInfererMultiValueContinuationTests
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

    /// <summary>Find the (single) Apply node that calls the named identifier inside the program.</summary>
    private static AstNode.Apply FindCallTo(AstNode root, string name)
    {
        AstNode.Apply? result = null;
        Walk(root);
        Assert.NotNull(result);
        return result!;

        void Walk(AstNode node)
        {
            if (result is not null)
                return;
            if (node is AstNode.Apply { Function: AstNode.Name n } a && n.Value == name)
            {
                result = a;
                return;
            }
            foreach (var child in EnumerateChildren(node))
                Walk(child);
        }
    }

    private static IEnumerable<AstNode> EnumerateChildren(AstNode node) =>
        node switch
        {
            AstNode.Program p => p.TopLevelForms,
            AstNode.Define d => [d.Body],
            AstNode.Lambda l => [l.Body],
            AstNode.CallCc cc => [cc.Function],
            AstNode.CallComp cc => [cc.Function],
            AstNode.CallCompAt cc => [cc.Tag, cc.Function],
            AstNode.Reset r => [r.Body],
            AstNode.ResetAt r => [r.Tag, r.Body],
            AstNode.Shift s => [s.Body],
            AstNode.ShiftAt s => [s.Tag, s.Body],
            AstNode.Prompt p => [p.Body],
            AstNode.PromptAt p => [p.Tag, p.Body],
            AstNode.Control c => [c.Body],
            AstNode.ControlAt c => [c.Tag, c.Body],
            AstNode.Let l => [l.Value, l.Body],
            AstNode.If i => [i.Condition, i.Then, i.Else],
            AstNode.Apply a => new[] { a.Function }.Concat(a.Args),
            AstNode.Match m => new[] { m.Scrutinee }.Concat(m.Arms.Select(arm => arm.Body)),
            AstNode.TupleNew t => t.Elements,
            _ => Array.Empty<AstNode>(),
        };

    // ====== Auto-bundling rewrite ======

    [Fact]
    public void CallCc_TwoArgInvocation_AlphaResolvesToValueTuple()
    {
        var src = @"(define (mv) : (Int * Int) (call/cc (lambda (k) (k 1 2))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Result type of mv is (Int * Int) tuple.
        var def = (AstNode.Define)prog.TopLevelForms[0];
        var fnType = Assert.IsType<ZType.ZFuncType>(def.ResolvedType);
        var ret = Assert.IsType<ZType.ZNamedType>(fnType.Return);
        Assert.Equal("ValueTuple", ret.Name);
        Assert.Equal(2, ret.TypeArgs.Count);
        Assert.Equal(ZType.Int, ret.TypeArgs[0]);
        Assert.Equal(ZType.Int, ret.TypeArgs[1]);
    }

    [Fact]
    public void CallCc_TwoArgInvocation_RewritesArgsToSingleTupleNew()
    {
        var src = @"(define (mv) : (Int * Int) (call/cc (lambda (k) (k 1 2))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var kCall = FindCallTo(prog, "k");
        Assert.NotNull(kCall.RewrittenArgs);
        Assert.Single(kCall.RewrittenArgs!);
        var bundled = Assert.IsType<AstNode.TupleNew>(kCall.RewrittenArgs![0]);
        Assert.Equal(2, bundled.Elements.Count);
    }

    [Fact]
    public void CallCc_SingleArgInvocation_NoRewrite()
    {
        // The single-value path must remain untouched: no RewrittenArgs, args unchanged.
        var src = @"(define (sv) : Int (call/cc (lambda (k) (k 42))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors);

        var kCall = FindCallTo(prog, "k");
        Assert.Null(kCall.RewrittenArgs);
        Assert.Single(kCall.Args);
    }

    [Fact]
    public void Shift_TwoArgInvocation_RewritesToTuple()
    {
        var src = @"(define (mv) : (Int * Int) (reset (shift k (k 10 20))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var kCall = FindCallTo(prog, "k");
        Assert.NotNull(kCall.RewrittenArgs);
        var bundled = Assert.IsType<AstNode.TupleNew>(kCall.RewrittenArgs![0]);
        Assert.Equal(2, bundled.Elements.Count);
    }

    [Fact]
    public void Control_TwoArgInvocation_RewritesToTuple()
    {
        var src = @"(define (mv) : (Int * Int) (prompt (control k (k 1 2))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors);
        var kCall = FindCallTo(prog, "k");
        Assert.NotNull(kCall.RewrittenArgs);
    }

    [Fact]
    public void CallComp_TwoArgInvocation_RewritesToTuple()
    {
        var src = @"(define (mv) : (Int * Int) (prompt (call/comp (lambda (k) (k 1 2)))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors);
        var kCall = FindCallTo(prog, "k");
        Assert.NotNull(kCall.RewrittenArgs);
    }

    [Fact]
    public void CallCc_MixedArityInvocations_AreATypeError()
    {
        // The same continuation can't be called as both unary and binary — α can't be both
        // T and Tuple[T, T].
        var src =
            @"(define (bad [n : Int]) : Int (call/cc (lambda (k) (if (= n 0) (k 1) (k 1 2)))))";
        var (_, diag) = InferProgram(src);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void NonContinuationFunction_MultiArgCall_NotAutoBundled()
    {
        // A regular function with a single tuple parameter is NOT auto-bundled — that
        // ergonomic generalization is intentionally limited to continuation parameters.
        var src =
            @"
(define (takes-tuple [t : (Int * Int)]) : Int (value/0 t))
(define (use) : Int (takes-tuple 1 2))";
        var (_, diag) = InferProgram(src);
        Assert.True(
            diag.HasErrors,
            "Calling takes-tuple with two args (instead of (values 1 2)) must remain a type error"
        );
    }

    // ====== Marker propagation through let ======

    [Fact]
    public void LetRebindingOfContinuation_PropagatesMarker()
    {
        var src =
            @"(define (mv) : (Int * Int)
  (call/cc (lambda (k) (let ([k2 k]) (k2 7 8)))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var k2Call = FindCallTo(prog, "k2");
        Assert.NotNull(k2Call.RewrittenArgs);
    }

    [Fact]
    public void LetRebindingThroughIntermediate_PropagatesAcrossChain()
    {
        var src =
            @"(define (mv) : (Int * Int)
  (call/cc (lambda (k)
    (let ([a k])
      (let ([b a])
        (b 7 8))))))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var bCall = FindCallTo(prog, "b");
        Assert.NotNull(bCall.RewrittenArgs);
    }

    [Fact]
    public void LetBindingOfNonContinuation_DoesNotMark()
    {
        // A let whose RHS is not a continuation Name binding should NOT mark its LHS.
        var src = @"(define (use) : Int (let ([k (lambda (a b) (+ a b))]) (k 1 2)))";
        var (prog, diag) = InferProgram(src);
        Assert.False(diag.HasErrors);

        var kCall = FindCallTo(prog, "k");
        // k here is a regular function with two params; the call is a normal 2-arg call,
        // no rewrite, no auto-bundle.
        Assert.Null(kCall.RewrittenArgs);
    }

    // ====== Arity guard ======

    [Fact]
    public void ContinuationCall_EightArgs_RejectedAtRewrite()
    {
        var src = @"(define (mv) : Int (call/cc (lambda (k) (k 1 2 3 4 5 6 7 8))))";
        var (_, diag) = InferProgram(src);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("at most 7"));
    }

    [Fact]
    public void ContinuationCall_SevenArgs_AcceptedAtMaxArity()
    {
        var src =
            @"(define (mv) : (Int * Int * Int * Int * Int * Int * Int)
  (call/cc (lambda (k) (k 1 2 3 4 5 6 7))))";
        var (_, diag) = InferProgram(src);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }
}

using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Ast;

/// <summary>
/// AST-level coverage for the multi-value continuation marker (<see cref="Param.IsContinuation"/>)
/// and the new <c>let-values</c> / <c>call-with-values</c> consumer forms.
/// </summary>
public class AstBuilderMultiValueContinuationTests
{
    private static (AstNode.Program Program, DiagnosticBag Diagnostics) BuildWithDiagnostics(
        string source
    )
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);
        return (program, diag);
    }

    private static AstNode.Program Build(string source)
    {
        var (program, diag) = BuildWithDiagnostics(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return program;
    }

    // ====== IsContinuation marker on lambda args ======

    [Fact]
    public void CallCc_LambdaParam_IsMarkedContinuation()
    {
        var prog = Build("(call/cc (lambda (k) 1))");
        var callcc = Assert.IsType<AstNode.CallCc>(prog.TopLevelForms[0]);
        var lam = Assert.IsType<AstNode.Lambda>(callcc.Function);
        Assert.True(lam.Params[0].IsContinuation, "k must be marked as a continuation parameter");
        Assert.Equal("k", lam.Params[0].Name);
    }

    [Fact]
    public void CallComp_LambdaParam_IsMarkedContinuation()
    {
        var prog = Build("(prompt (call/comp (lambda (k) 1)))");
        var prompt = Assert.IsType<AstNode.Prompt>(prog.TopLevelForms[0]);
        var cc = Assert.IsType<AstNode.CallComp>(prompt.Body);
        var lam = Assert.IsType<AstNode.Lambda>(cc.Function);
        Assert.True(lam.Params[0].IsContinuation);
    }

    [Fact]
    public void CallCompTagged_LambdaParam_IsMarkedContinuation()
    {
        var prog = Build(
            "(let ([tag (make-prompt-tag)]) (prompt tag (call/comp (lambda (k) 1) tag)))"
        );
        // Walk: Let body → PromptAt → CallCompAt
        var letNode = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        var promptAt = Assert.IsType<AstNode.PromptAt>(letNode.Body);
        var ccAt = Assert.IsType<AstNode.CallCompAt>(promptAt.Body);
        var lam = Assert.IsType<AstNode.Lambda>(ccAt.Function);
        Assert.True(lam.Params[0].IsContinuation);
    }

    [Fact]
    public void CallCc_NonLambdaArg_NoMarker()
    {
        // Non-literal-lambda argument (e.g. a Name reference) is parsed as a call/cc with
        // a Name function — no Lambda to mark. The marker is keyed on the literal
        // (lambda (k) ...) shape; type inference will still see k as α → β.
        var prog = Build(
            @"
(define (helper [f : ((Int -> Int) -> Int)]) : Int (f (lambda (_) 0)))
(define (use) : Int (call/cc helper))"
        );
        var defUse = Assert.IsType<AstNode.Define>(prog.TopLevelForms[1]);
        var callcc = Assert.IsType<AstNode.CallCc>(defUse.Body);
        // The Function position is a Name, not a Lambda — so there is no Param.IsContinuation
        // to set. The lookup at type-inference time is what governs auto-bundling.
        Assert.IsType<AstNode.Name>(callcc.Function);
    }

    [Fact]
    public void CallCc_MultiParamLambda_NoMarker()
    {
        // (call/cc (lambda (k extra) ...)) is malformed but parser permits the lambda;
        // the marker only fires for the canonical single-param shape so we don't mis-mark
        // a 2-arity function whose first param happens to align with α.
        var prog = Build("(call/cc (lambda (k x) 1))");
        var callcc = Assert.IsType<AstNode.CallCc>(prog.TopLevelForms[0]);
        var lam = Assert.IsType<AstNode.Lambda>(callcc.Function);
        Assert.False(lam.Params[0].IsContinuation);
    }

    [Fact]
    public void RegularLambda_NoContinuationMarker()
    {
        var prog = Build("(lambda (k) (k 1 2))");
        var lam = Assert.IsType<AstNode.Lambda>(prog.TopLevelForms[0]);
        Assert.False(lam.Params[0].IsContinuation);
    }

    // ====== let-values desugaring ======

    [Fact]
    public void LetValues_ArityTwo_DesugarsToMatch()
    {
        var prog = Build("(let-values ([(a b) (values 1 2)]) a)");
        // Match scrutinee = TupleNew(1,2); single arm with tuple pattern of two variables.
        var match = Assert.IsType<AstNode.Match>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.TupleNew>(match.Scrutinee);
        Assert.Single(match.Arms);
        var tuplePat = Assert.IsType<Pattern.Tuple>(match.Arms[0].Pattern);
        Assert.Equal(2, tuplePat.Elements.Count);
        var pa = Assert.IsType<Pattern.Variable>(tuplePat.Elements[0]);
        var pb = Assert.IsType<Pattern.Variable>(tuplePat.Elements[1]);
        Assert.Equal("a", pa.Name);
        Assert.Equal("b", pb.Name);
    }

    [Fact]
    public void LetValues_ArityOne_DesugarsToPlainLet()
    {
        var prog = Build("(let-values ([(x) 7]) x)");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", let.VarName);
    }

    [Fact]
    public void LetValues_MultipleBindings_NestSequentially()
    {
        // Outer is the first (a b) binding (a Match); its body is the second (c) binding (a Let).
        var prog = Build("(let-values ([(a b) (values 1 2)] [(c) 3]) c)");
        var outerMatch = Assert.IsType<AstNode.Match>(prog.TopLevelForms[0]);
        var arm = outerMatch.Arms[0];
        var innerLet = Assert.IsType<AstNode.Let>(arm.Body);
        Assert.Equal("c", innerLet.VarName);
    }

    [Fact]
    public void LetValues_MalformedBinding_Diagnoses()
    {
        var (_, diag) = BuildWithDiagnostics("(let-values [(a b) (values 1 2)] a)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("let-values"));
    }

    [Fact]
    public void LetValues_NoBindings_Diagnoses()
    {
        var (_, diag) = BuildWithDiagnostics("(let-values () body)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void LetValues_TooManyNames_Diagnoses()
    {
        // 8 names exceeds the 7-element ValueTuple ceiling.
        var (_, diag) = BuildWithDiagnostics(
            "(let-values ([(a b c d e f g h) (values 1 2 3 4 5 6 7)]) a)"
        );
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("at most 7"));
    }

    // ====== call-with-values desugaring ======

    [Fact]
    public void CallWithValues_LiteralFn_DesugarsToMatchOverProducerCall()
    {
        var prog = Build("(call-with-values (lambda () (values 1 2)) (lambda (a b) (+ a b)))");
        var match = Assert.IsType<AstNode.Match>(prog.TopLevelForms[0]);
        // Scrutinee is a 0-arg call to the producer thunk.
        var producerCall = Assert.IsType<AstNode.Apply>(match.Scrutinee);
        Assert.IsType<AstNode.Lambda>(producerCall.Function);
        Assert.Empty(producerCall.Args);

        var tuplePat = Assert.IsType<Pattern.Tuple>(match.Arms[0].Pattern);
        Assert.Equal(2, tuplePat.Elements.Count);
        Assert.Equal("a", ((Pattern.Variable)tuplePat.Elements[0]).Name);
        Assert.Equal("b", ((Pattern.Variable)tuplePat.Elements[1]).Name);
    }

    [Fact]
    public void CallWithValues_SingleParamConsumer_DesugarsToPlainLet()
    {
        var prog = Build("(call-with-values (lambda () 42) (lambda (x) (+ x 1)))");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", let.VarName);
        // Producer is invoked as a 0-arg call.
        var producerCall = Assert.IsType<AstNode.Apply>(let.Value);
        Assert.Empty(producerCall.Args);
    }

    [Fact]
    public void CallWithValues_NonLambdaConsumer_Diagnoses()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define (consumer [a : Int] [b : Int]) : Int 0)\n(call-with-values (lambda () (values 1 2)) consumer)"
        );
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("call-with-values") && d.Message.Contains("literal")
        );
    }

    [Fact]
    public void CallWithValues_NoConsumerParams_Diagnoses()
    {
        var (_, diag) = BuildWithDiagnostics("(call-with-values (lambda () 1) (lambda () 2))");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void CallWithValues_TooManyConsumerParams_Diagnoses()
    {
        // 8 params exceeds the ValueTuple ceiling.
        var (_, diag) = BuildWithDiagnostics(
            "(call-with-values (lambda () (values 1 2 3 4 5 6 7)) (lambda (a b c d e f g h) a))"
        );
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void CallWithValues_WrongArity_Diagnoses()
    {
        var (_, diag) = BuildWithDiagnostics("(call-with-values (lambda () 1))");
        Assert.True(diag.HasErrors);
    }
}

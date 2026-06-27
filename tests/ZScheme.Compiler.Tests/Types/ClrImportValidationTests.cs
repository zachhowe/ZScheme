using System.Linq;
using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     Exercises the import-clr annotation validator: an annotated <c>(import-clr ...)</c> is
///     cross-checked against the real CLR member, so a wrong annotation surfaces a diagnostic at the
///     import site instead of silently propagating downstream.
/// </summary>
public class ClrImportValidationTests
{
    private static DiagnosticBag Infer(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        var registry = new TypeAliasRegistry();
        registry.RegisterBuiltIn(
            new TypeAliasInfo(
                "Seq",
                ["^a"],
                "System.Collections.Generic.IEnumerable",
                "System.Private.CoreLib",
                TypeAliasKind.GenericClrType,
                default
            )
        );

        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diag, typeAliases: registry);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        return diag;
    }

    private static bool HasImportMismatch(DiagnosticBag diag)
    {
        return diag.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error
            && d.Message.Contains("does not match the CLR member")
        );
    }

    [Fact]
    public void CorrectStaticAnnotation_NoDiagnostic()
    {
        var diag = Infer("(import-clr System [sqrt System.Math/Sqrt : (Double -> Double)])");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Message.Contains("does not match the CLR member")
        );
    }

    [Fact]
    public void WrongArity_Errors()
    {
        // Math.Sqrt is (Double -> Double); declaring two parameters is a lie.
        var diag = Infer("(import-clr System [sqrt System.Math/Sqrt : (Int Bool -> String)])");
        Assert.True(HasImportMismatch(diag));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void WrongReturnType_Errors()
    {
        var diag = Infer("(import-clr System [sqrt System.Math/Sqrt : (Double -> String)])");
        Assert.True(HasImportMismatch(diag));
    }

    [Fact]
    public void CorrectInstanceProperty_NoDiagnostic()
    {
        // StringBuilder.Length is an int instance property; the receiver is synthesized as param 0.
        var diag = Infer(
            "(import-clr System.Text "
                + "[sb-len System.Text.StringBuilder.Length :instance-property : (System.Text.StringBuilder -> Int)])"
        );
        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Message.Contains("does not match the CLR member")
        );
    }

    [Fact]
    public void WrongInstancePropertyReturn_Errors()
    {
        var diag = Infer(
            "(import-clr System.Text "
                + "[sb-len System.Text.StringBuilder.Length :instance-property : (System.Text.StringBuilder -> String)])"
        );
        Assert.True(HasImportMismatch(diag));
    }

    [Fact]
    public void InheritedInterfaceProperty_NoDiagnostic()
    {
        // IList<int>.Count is inherited from the base interface ICollection<int>; the validator
        // must walk base interfaces to find it (mirroring inherited instance-method validation)
        // rather than silently skipping the check.
        var diag = Infer(
            "(import-clr System.Collections.Generic "
                + "[c System.Collections.Generic.IList.Count "
                + ":instance-property : ((System.Collections.Generic.IList Int) -> Int)])"
        );
        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Message.Contains("does not match the CLR member")
        );
    }

    [Fact]
    public void WrongInheritedInterfacePropertyReturn_Errors()
    {
        // Count is Int; declaring Bool is a lie the validator now catches because it resolves the
        // inherited property (before the interface-walk fix it was silently skipped).
        var diag = Infer(
            "(import-clr System.Collections.Generic "
                + "[c System.Collections.Generic.IList.Count "
                + ":instance-property : ((System.Collections.Generic.IList Int) -> Bool)])"
        );
        Assert.True(HasImportMismatch(diag));
    }

    [Fact]
    public void EnumDeclaredAsInt_Errors()
    {
        // HttpResponseMessage.StatusCode returns the HttpStatusCode enum, not Int — per the
        // honest-enum policy this is reported rather than silently tolerated.
        var diag = Infer(
            "(import-clr System.Net.Http "
                + "[code System.Net.Http.HttpResponseMessage.StatusCode "
                + ":instance-property : (System.Net.Http.HttpResponseMessage -> Int)])"
        );
        Assert.True(HasImportMismatch(diag));
    }

    [Fact]
    public void IEnumerableReturnAsSeq_NoDiagnostic()
    {
        // ImmutableDictionary.Keys returns IEnumerable<TKey>; annotating it (Seq ^k) is honest and
        // must pass, while annotating it as a concrete list would not.
        var diag = Infer(
            "(import-clr System.Collections.Immutable "
                + "[keys System.Collections.Immutable.ImmutableDictionary.Keys "
                + ":instance-property : ((System.Collections.Immutable.ImmutableDictionary ^k ^v) -> (Seq ^k))])"
        );
        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Message.Contains("does not match the CLR member")
        );
    }
}

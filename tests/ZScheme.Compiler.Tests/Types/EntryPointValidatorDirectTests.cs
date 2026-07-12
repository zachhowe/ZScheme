using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     Direct unit tests for <see cref="EntryPointValidator" /> over hand-built AST nodes.
///     Complements <see cref="EntryPointValidatorTests" />, which exercises the same class
///     end-to-end through the compilation pipeline.
/// </summary>
public class EntryPointValidatorDirectTests
{
    private static readonly ZType StringArray = new ZType.ZNamedType(
        "Mutable-Vector",
        [ZType.String]
    );

    private static DiagnosticBag Validate(params AstNode[] forms)
    {
        var diag = new DiagnosticBag();
        var registry = new TypeAliasRegistry();
        registry.RegisterBuiltIn(
            new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, default)
        );
        var validator = new EntryPointValidator(diag, registry);
        validator.Validate(new AstNode.Program(forms, SourceSpan.None));
        return diag;
    }

    private static AstNode.Define SyncMain(IReadOnlyList<Param> parms, ZType returnType)
    {
        return new AstNode.Define(
            "main",
            parms,
            ReturnTypeAnnotation: null,
            new AstNode.IntLit(0, SourceSpan.None),
            SourceSpan.None
        )
        {
            ResolvedType = new ZType.ZFuncType(parms.Select(p => p.TypeAnnotation!).ToList(), returnType),
        };
    }

    private static AstNode.DefineAsync AsyncMain(ZType returnType)
    {
        return new AstNode.DefineAsync(
            "main",
            [],
            ReturnTypeAnnotation: null,
            new AstNode.IntLit(0, SourceSpan.None),
            SourceSpan.None
        )
        {
            ResolvedType = new ZType.ZFuncType([], returnType),
        };
    }

    private static Param Param(string name, ZType type, bool isVariadic = false)
    {
        return new Param(name, type, SourceSpan.None, IsVariadic: isVariadic);
    }

    [Fact]
    public void ZeroParamIntMainIsValid()
    {
        var diag = Validate(SyncMain([], ZType.Int));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnitReturningMainIsValid()
    {
        var diag = Validate(SyncMain([], ZType.Unit));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void StringArrayParamIsValid()
    {
        var diag = Validate(SyncMain([Param("args", StringArray)], ZType.Int));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void TwoParamsIsAnError()
    {
        var diag = Validate(
            SyncMain([Param("a", StringArray), Param("b", StringArray)], ZType.Int)
        );
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("at most one parameter", d.Message);
    }

    [Fact]
    public void VariadicParamIsAnError()
    {
        var diag = Validate(SyncMain([Param("args", StringArray, isVariadic: true)], ZType.Int));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("variadic", d.Message);
    }

    [Fact]
    public void NonStringElementParamIsAnError()
    {
        var intArray = new ZType.ZNamedType("Mutable-Vector", [ZType.Int]);
        var diag = Validate(SyncMain([Param("args", intArray)], ZType.Int));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("CLR string array", d.Message);
    }

    [Fact]
    public void PlainIntParamIsAnError()
    {
        var diag = Validate(SyncMain([Param("args", ZType.Int)], ZType.Int));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("CLR string array", d.Message);
    }

    [Fact]
    public void SyncMainReturningTaskSuggestsDefineAsync()
    {
        var taskInt = new ZType.ZNamedType("Task", [ZType.Int]);
        var diag = Validate(SyncMain([], taskInt));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("(define-async", d.Message);
    }

    [Fact]
    public void SyncMainReturningStringIsAnError()
    {
        var diag = Validate(SyncMain([], ZType.String));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("must return Int or Unit", d.Message);
    }

    [Fact]
    public void SyncMainWithoutResolvedTypeReportsUnknownType()
    {
        var main = new AstNode.Define(
            "main",
            [],
            ReturnTypeAnnotation: null,
            new AstNode.IntLit(0, SourceSpan.None),
            SourceSpan.None
        );
        var diag = Validate(main);
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("an unknown type", d.Message);
    }

    [Fact]
    public void AsyncMainReturningTaskOfIntIsValid()
    {
        var diag = Validate(AsyncMain(new ZType.ZNamedType("Task", [ZType.Int])));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void AsyncMainReturningNonGenericTaskIsValid()
    {
        var diag = Validate(AsyncMain(new ZType.ZNamedType("Task", [])));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void AsyncMainReturningTaskOfStringIsAnError()
    {
        var diag = Validate(AsyncMain(new ZType.ZNamedType("Task", [ZType.String])));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("Task of String", d.Message);
    }

    [Fact]
    public void AsyncMainReturningNonTaskIsAnError()
    {
        var diag = Validate(AsyncMain(ZType.Int));
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("must return (Task Int) or (Task Unit)", d.Message);
    }

    [Fact]
    public void MainInsideModuleDeclIsStillValidated()
    {
        var main = SyncMain([Param("a", StringArray), Param("b", StringArray)], ZType.Int);
        var module = new AstNode.ModuleDecl("app", [main], SourceSpan.None);
        var diag = Validate(module);
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("at most one parameter", d.Message);
    }

    [Fact]
    public void NonMainDefinesAreIgnored()
    {
        var helper = new AstNode.Define(
            "helper",
            [Param("a", ZType.String), Param("b", ZType.String)],
            ReturnTypeAnnotation: null,
            new AstNode.IntLit(0, SourceSpan.None),
            SourceSpan.None
        )
        {
            ResolvedType = new ZType.ZFuncType([ZType.String, ZType.String], ZType.String),
        };
        var diag = Validate(helper);
        Assert.False(diag.HasErrors);
    }
}

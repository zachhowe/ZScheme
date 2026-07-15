using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class PatternResolverTests
{
    private static readonly ZType Int = ZType.Int;

    // (define-union Shape (Circle [radius : Int]) (Rect [w : Int] [h : Int]))
    private static IrNode.UnionDecl ShapeUnion() =>
        new(
            "Shape",
            [],
            [
                new IrUnionCase("Circle", [new IrField("radius", Int)]),
                new IrUnionCase("Rect", [new IrField("w", Int), new IrField("h", Int)]),
            ]
        );

    // (define-union Option (Some [value : T0]) (None)) — one type param.
    private static IrNode.UnionDecl OptionUnion() =>
        new(
            "Option",
            ["T0"],
            [
                new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("T0", []))]),
                new IrUnionCase("None", []),
            ]
        );

    private static ZType Named(string name, params ZType[] args) =>
        new ZType.ZNamedType(name, args);

    private static IrNode Body() => new IrNode.IntConst(0) { Type = Int };

    // ---- UnionCaseRegistry ----

    [Fact]
    public void FieldType_MonomorphicUnion_ReturnsDeclaredType()
    {
        var reg = new UnionCaseRegistry();
        reg.RegisterUnion(ShapeUnion());

        Assert.Equal(Int, reg.FieldType(Named("Shape"), "Circle", 0));
        Assert.Equal(Int, reg.FieldType(Named("Shape"), "Rect", 1));
    }

    [Fact]
    public void FieldType_GenericUnion_SubstitutesTypeArguments()
    {
        var reg = new UnionCaseRegistry();
        reg.RegisterUnion(OptionUnion());

        // Scrutinee Option<Int> — the Some field template T0 becomes Int.
        Assert.Equal(Int, reg.FieldType(Named("Option", Int), "Some", 0));
    }

    [Fact]
    public void FieldType_NestedGeneric_SubstitutesOuterArgIntoField()
    {
        var reg = new UnionCaseRegistry();
        reg.RegisterUnion(OptionUnion());

        // Scrutinee Option<Option<Int>> — the Some field becomes Option<Int>.
        var inner = Named("Option", Int);
        Assert.Equal(inner, reg.FieldType(Named("Option", inner), "Some", 0));
    }

    [Fact]
    public void ResolveUnion_BareTypeVariableScrutinee_FallsBackToCaseMap()
    {
        var reg = new UnionCaseRegistry();
        reg.RegisterUnion(OptionUnion());

        // Scrutinee is a bare type variable "T" — its name resolves no "T.Some" key, so the
        // case→union fallback must still find Option. This is the resolution the IL backend
        // historically lacked.
        Assert.Equal("Option", reg.ResolveUnion(Named("T"), "Some"));
        // With no type arguments to substitute, the unsubstituted field template is returned.
        Assert.Equal(new ZType.ZNamedType("T0", []), reg.FieldType(Named("T"), "Some", 0));
    }

    [Fact]
    public void FieldType_UnknownCase_ReturnsNull()
    {
        var reg = new UnionCaseRegistry();
        reg.RegisterUnion(ShapeUnion());

        Assert.Null(reg.FieldType(Named("Shape"), "Nonexistent", 0));
        Assert.Null(reg.ResolveUnion(Named("Shape"), "Nonexistent"));
    }

    // ---- PatternResolver ----

    private static PatternResolver ResolverFor(params IrNode.UnionDecl[] unions)
    {
        var reg = new UnionCaseRegistry();
        foreach (var u in unions)
            reg.RegisterUnion(u);
        return new PatternResolver(reg, new TypeAliasRegistry());
    }

    private static IrNode.Match ResolveMatch(PatternResolver resolver, IrNode.Match match) =>
        Assert.IsType<IrNode.Match>(resolver.Resolve(match));

    [Fact]
    public void Resolve_ConstructorPattern_AttachesUnionAndFieldTypes()
    {
        var resolver = ResolverFor(ShapeUnion());
        var match = new IrNode.Match(
            new IrNode.Var("s") { Type = Named("Shape") },
            [
                new IrMatchArm(
                    new IrPattern.Constructor("Circle", [new IrPattern.Variable("r")]),
                    Body()
                ),
            ]
        )
        {
            Type = Int,
        };

        var resolved = ResolveMatch(resolver, match);
        var ctor = Assert.IsType<IrPattern.Constructor>(resolved.Arms[0].Pattern);
        Assert.Equal("Shape", ctor.ResolvedUnion);
        Assert.NotNull(ctor.FieldTypes);
        Assert.Equal(Int, Assert.Single(ctor.FieldTypes!));
    }

    [Fact]
    public void Resolve_NestedConstructorPattern_ResolvesInnerScrutineeType()
    {
        var resolver = ResolverFor(OptionUnion());
        // (match x [(Some (Some y)) ...]) where x : Option<Option<Int>>. Reuse the inner
        // Option<Int> instance: substitution returns the scrutinee's own type argument, and
        // ZType.ZNamedType compares its TypeArgs list by reference, so the assertion below
        // checks the resolver threaded that exact instance down to the field.
        var innerOption = Named("Option", Int);
        var match = new IrNode.Match(
            new IrNode.Var("x") { Type = Named("Option", innerOption) },
            [
                new IrMatchArm(
                    new IrPattern.Constructor(
                        "Some",
                        [new IrPattern.Constructor("Some", [new IrPattern.Variable("y")])]
                    ),
                    Body()
                ),
            ]
        )
        {
            Type = Int,
        };

        var resolved = ResolveMatch(resolver, match);
        var outer = Assert.IsType<IrPattern.Constructor>(resolved.Arms[0].Pattern);
        Assert.Equal("Option", outer.ResolvedUnion);
        Assert.Same(innerOption, outer.FieldTypes![0]);

        var inner = Assert.IsType<IrPattern.Constructor>(outer.Fields[0]);
        Assert.Equal("Option", inner.ResolvedUnion);
        Assert.Equal(Int, inner.FieldTypes![0]);
    }

    [Fact]
    public void Resolve_UnknownConstructor_LeavesAnnotationsNull()
    {
        var resolver = ResolverFor(ShapeUnion());
        var match = new IrNode.Match(
            new IrNode.Var("s") { Type = Named("Shape") },
            [new IrMatchArm(new IrPattern.Constructor("Ghost", []), Body())]
        )
        {
            Type = Int,
        };

        var resolved = ResolveMatch(resolver, match);
        var ctor = Assert.IsType<IrPattern.Constructor>(resolved.Arms[0].Pattern);
        Assert.Null(ctor.ResolvedUnion);
    }

    [Fact]
    public void Resolve_MatchNestedInsideFuncBody_IsReached()
    {
        var resolver = ResolverFor(ShapeUnion());
        var match = new IrNode.Match(
            new IrNode.Var("s") { Type = Named("Shape") },
            [
                new IrMatchArm(
                    new IrPattern.Constructor(
                        "Rect",
                        [new IrPattern.Variable("w"), new IrPattern.Variable("h")]
                    ),
                    Body()
                ),
            ]
        )
        {
            Type = Int,
        };
        // Wrap the match deep inside a function body inside a Seq, to confirm the traversal
        // descends to nested matches rather than only touching a top-level one.
        var func = new IrNode.FuncDef(
            "area",
            [new IrParam("s", Named("Shape"))],
            Int,
            new IrNode.If(new IrNode.BoolConst(true) { Type = ZType.Bool }, match, Body())
            {
                Type = Int,
            },
            IsSelfRecursive: false
        );
        var program = new IrNode.Seq([func]);

        var resolved = resolver.Resolve(program);
        var seq = Assert.IsType<IrNode.Seq>(resolved);
        var fd = Assert.IsType<IrNode.FuncDef>(seq.Nodes[0]);
        var ifNode = Assert.IsType<IrNode.If>(fd.Body);
        var innerMatch = Assert.IsType<IrNode.Match>(ifNode.Then);
        var ctor = Assert.IsType<IrPattern.Constructor>(innerMatch.Arms[0].Pattern);
        Assert.Equal("Shape", ctor.ResolvedUnion);
        Assert.Equal([Int, Int], ctor.FieldTypes);
    }
}

using Xunit;
using ZScheme.Compiler.Builtins;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Builtins;

public class BuiltinRegistryTests
{
    [Fact]
    public void All_HasNoDuplicateNames()
    {
        var names = BuiltinRegistry.All.Select(b => b.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void ByName_CoversEveryBuiltin()
    {
        foreach (var b in BuiltinRegistry.All)
            Assert.Same(b, BuiltinRegistry.ByName[b.Name]);
    }

    [Fact]
    public void CreateRoot_RegistersExactlyTheRegistrySignatures()
    {
        var env = TypeEnv.CreateRoot();
        foreach (var b in BuiltinRegistry.All)
            Assert.Equal(b.Signature, env.Lookup(b.Name));
    }

    [Fact]
    public void BinaryOps_MatchOperatorsFlaggedBinary()
    {
        var expected = BuiltinRegistry
            .All.Where(b => b.Lowering is BuiltinLowering.Operator { Binary: true })
            .Select(b => b.Name)
            .ToHashSet();
        Assert.Equal(expected, BuiltinRegistry.BinaryOps.ToHashSet());
    }

    [Fact]
    public void UnaryOps_MatchOperatorsFlaggedUnary()
    {
        var expected = BuiltinRegistry
            .All.Where(b => b.Lowering is BuiltinLowering.Operator { Unary: true })
            .Select(b => b.Name)
            .ToHashSet();
        Assert.Equal(expected, BuiltinRegistry.UnaryOps.ToHashSet());
    }

    [Fact]
    public void Minus_IsBothBinaryAndUnary()
    {
        Assert.Contains("-", BuiltinRegistry.BinaryOps);
        Assert.Contains("-", BuiltinRegistry.UnaryOps);
    }

    [Fact]
    public void Not_IsUnaryOnly()
    {
        Assert.Contains("not", BuiltinRegistry.UnaryOps);
        Assert.DoesNotContain("not", BuiltinRegistry.BinaryOps);
    }

    [Fact]
    public void StringAppend_LowersToPlusOperator()
    {
        var op = Assert.IsType<BuiltinLowering.Operator>(
            BuiltinRegistry.ByName["string-append"].Lowering
        );
        Assert.True(op.Binary);
        Assert.Equal("+", op.OpOverride);
    }

    [Theory]
    [InlineData("+", FoldKind.LeftFoldIdentity)]
    [InlineData("*", FoldKind.LeftFoldIdentity)]
    [InlineData("string-append", FoldKind.LeftFoldIdentity)]
    [InlineData("-", FoldKind.ArithUnary)]
    [InlineData("/", FoldKind.ArithUnary)]
    [InlineData("%", FoldKind.ArithStrict)]
    [InlineData("<", FoldKind.CmpChain)]
    [InlineData("=", FoldKind.CmpChain)]
    [InlineData("!=", FoldKind.NeqAllDistinct)]
    [InlineData("and", FoldKind.BoolFold)]
    [InlineData("or", FoldKind.BoolFold)]
    [InlineData("not", FoldKind.None)]
    [InlineData("int->string", FoldKind.None)]
    public void Fold_HasExpectedCategory(string name, FoldKind expected)
    {
        Assert.Equal(expected, BuiltinRegistry.ByName[name].Fold);
    }

    [Fact]
    public void Plus_AllowsStringInAdditionToNumerics()
    {
        Assert.Equal(
            new HashSet<PrimitiveKind>
            {
                PrimitiveKind.Int,
                PrimitiveKind.Float,
                PrimitiveKind.String,
            },
            AllowedKindsOf("+")
        );
    }

    // The other constrained operators share a numeric-only kind set. `+` deliberately
    // gets its own; if that set were ever widened in place, `(< "a" "b")` and `(- "a" "b")`
    // would start type-checking.
    [Theory]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void NonPlusConstrainedOperators_RejectString(string name)
    {
        Assert.Equal(
            new HashSet<PrimitiveKind> { PrimitiveKind.Int, PrimitiveKind.Float },
            AllowedKindsOf(name)
        );
    }

    // Every constrained operator's signature is `forall a:{kinds}. (a, a) -> ...`, so the
    // kind set lives on the first parameter's ZConstrainedVar.
    private static HashSet<PrimitiveKind> AllowedKindsOf(string name)
    {
        var forall = Assert.IsType<ZType.ZForAllType>(BuiltinRegistry.ByName[name].Signature);
        var fn = Assert.IsType<ZType.ZFuncType>(forall.Body);
        var cv = Assert.IsType<ZType.ZConstrainedVar>(fn.Params[0]);
        return cv.AllowedKinds.ToHashSet();
    }

    [Fact]
    public void ConcreteReturnOrUnit_UsesUnitForPolymorphicSignatures()
    {
        // Arithmetic `+` is forall-quantified, so it has no concrete return type.
        Assert.Equal(ZType.Unit, BuiltinRegistry.ConcreteReturnOrUnit(BuiltinRegistry.ByName["+"]));
        // `string-append` is monomorphic → String.
        Assert.Equal(
            ZType.String,
            BuiltinRegistry.ConcreteReturnOrUnit(BuiltinRegistry.ByName["string-append"])
        );
    }
}

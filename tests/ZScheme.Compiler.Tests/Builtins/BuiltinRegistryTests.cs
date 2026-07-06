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
    [InlineData("+", FoldKind.ArithIdentity)]
    [InlineData("*", FoldKind.ArithIdentity)]
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

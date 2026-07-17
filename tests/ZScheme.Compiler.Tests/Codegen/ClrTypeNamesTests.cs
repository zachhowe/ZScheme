using Xunit;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Tests.Codegen;

public class ClrTypeNamesTests
{
    [Fact]
    public void NonGenericNamePassesThrough()
    {
        Assert.Equal("System.Action", ClrTypeNames.ConvertToReflectionTypeName("System.Action"));
    }

    [Fact]
    public void SimpleGenericGetsArityAndBracketedArgs()
    {
        Assert.Equal(
            "System.Func`2[System.Int32,System.Int32]",
            ClrTypeNames.ConvertToReflectionTypeName("System.Func<int,int>")
        );
    }

    [Fact]
    public void SingleArgGenericGetsArityOne()
    {
        Assert.Equal(
            "System.Action`1[System.String]",
            ClrTypeNames.ConvertToReflectionTypeName("System.Action<string>")
        );
    }

    [Fact]
    public void TypeArgsAreTrimmed()
    {
        Assert.Equal(
            "System.Func`2[System.Int32,System.Boolean]",
            ClrTypeNames.ConvertToReflectionTypeName("System.Func< int , bool >")
        );
    }

    [Fact]
    public void UnbalancedAngleBracketsPassThrough()
    {
        Assert.Equal("Weird>Name<", ClrTypeNames.ConvertToReflectionTypeName("Weird>Name<"));
    }

    [Theory]
    [InlineData("int", "System.Int32")]
    [InlineData("Int32", "System.Int32")]
    [InlineData("long", "System.Int64")]
    [InlineData("short", "System.Int16")]
    [InlineData("ushort", "System.UInt16")]
    [InlineData("uint", "System.UInt32")]
    [InlineData("sbyte", "System.SByte")]
    [InlineData("float", "System.Single")]
    [InlineData("double", "System.Double")]
    [InlineData("bool", "System.Boolean")]
    [InlineData("string", "System.String")]
    [InlineData("char", "System.Char")]
    [InlineData("unit", "System.Object")]
    [InlineData("Unit", "System.Object")]
    [InlineData("My.Custom.Type", "My.Custom.Type")]
    public void ConvertTypeArgMapsPrimitiveAliases(string input, string expected)
    {
        Assert.Equal(expected, ClrTypeNames.ConvertTypeArg(input));
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("Byte")]
    public void ByteMapsToSystemByte(string input)
    {
        Assert.Equal("System.Byte", ClrTypeNames.ConvertTypeArg(input));
    }

    [Fact]
    public void NestedGenericArgsAreConvertedRecursively()
    {
        Assert.Equal(
            "System.Func`2[System.Func`2[System.Int32,System.Int32],System.Int32]",
            ClrTypeNames.ConvertToReflectionTypeName("System.Func<System.Func<int,int>,int>")
        );
    }
}

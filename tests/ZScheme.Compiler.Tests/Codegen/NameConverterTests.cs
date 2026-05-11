using Xunit;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Tests.Codegen;

public class NameConverterTests
{
    [Theory]
    [InlineData("value", "Value")]
    [InlineData("x", "X")]
    [InlineData("value-a", "ValueA")]
    [InlineData("my-cool-func", "MyCoolFunc")]
    [InlineData("value/a", "Value_A")]
    [InlineData("my-cool/func-name", "MyCool_FuncName")]
    [InlineData("a/b/c", "A_B_C")]
    [InlineData("a-b", "AB")]
    [InlineData("has?", "Has_q")]
    [InlineData("greater>", "Greater_gt")]
    [InlineData("pipe|here", "Pipe_pipehere")]
    [InlineData("caret^gone", "Caretgone")]
    [InlineData("set!", "Set_b")]
    [InlineData("mutable-vector/set!", "MutableVector_Set_b")]
    public void SanitizeIdentifier_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, NameConverter.SanitizeIdentifier(input));
    }

    [Theory]
    [InlineData("stdlib/option", "Stdlib_OptionModule")]
    [InlineData("my-lib/cool-module", "MyLib_CoolModuleModule")]
    [InlineData("simple", "SimpleModule")]
    [InlineData("a/b/c", "A_B_CModule")]
    [InlineData("my-cool-lib", "MyCoolLibModule")]
    public void ClassNameFromModuleName_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, NameConverter.ClassNameFromModuleName(input));
    }

    [Theory]
    [InlineData("value", "value")]
    [InlineData("x", "x")]
    [InlineData("value-a", "valueA")]
    [InlineData("my-cool-func", "myCoolFunc")]
    [InlineData("value/a", "value_A")]
    [InlineData("my-cool/func-name", "myCool_FuncName")]
    [InlineData("a/b/c", "a_B_C")]
    [InlineData("a-b", "aB")]
    [InlineData("has?", "has_q")]
    [InlineData("greater>", "greater_gt")]
    [InlineData("set!", "set_b")]
    public void SanitizeParameter_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, NameConverter.SanitizeParameter(input));
    }
}

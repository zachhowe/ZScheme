using System.Collections.Immutable;
using Xunit;
using ZScheme.Compiler.Repl;
using ZScheme.Compiler.Types;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Repl;

public class ReplValueFormatterTests
{
    [Fact]
    public void NullFormatsAsNull()
    {
        Assert.Equal("null", ReplValueFormatter.Format(null));
    }

    [Fact]
    public void UnitTypedValueFormatsAsEmptyParens()
    {
        Assert.Equal("()", ReplValueFormatter.Format(0, ZType.Unit));
    }

    [Fact]
    public void BoolsFormatAsSchemeLiterals()
    {
        Assert.Equal("#t", ReplValueFormatter.Format(true));
        Assert.Equal("#f", ReplValueFormatter.Format(false));
    }

    [Fact]
    public void CharsFormatWithHashBackslash()
    {
        Assert.Equal("#\\a", ReplValueFormatter.Format('a'));
    }

    [Fact]
    public void SymbolsFormatWithLeadingQuote()
    {
        Assert.Equal("'foo", ReplValueFormatter.Format(ZSymbol.Intern("foo")));
    }

    [Fact]
    public void StringsAreQuotedAndEscaped()
    {
        Assert.Equal(
            "\"a\\\"b\\n\\r\\t\\\\\"",
            ReplValueFormatter.Format("a\"b\n\r\t\\")
        );
    }

    [Fact]
    public void FloatsUseInvariantRoundTripFormat()
    {
        Assert.Equal("3.14", ReplValueFormatter.Format(3.14));
        Assert.Equal("2.5", ReplValueFormatter.Format(2.5f));
    }

    [Fact]
    public void IntegersUseInvariantFormat()
    {
        Assert.Equal("1234567", ReplValueFormatter.Format(1234567));
        Assert.Equal("-42", ReplValueFormatter.Format(-42L));
    }

    [Fact]
    public void TuplesFormatWithNestedValues()
    {
        Assert.Equal("(1, \"x\")", ReplValueFormatter.Format((1, "x")));
    }

    [Fact]
    public void ImmutableListsFormatAsParenthesizedSequence()
    {
        Assert.Equal("(1 2 3)", ReplValueFormatter.Format(ImmutableList.Create(1, 2, 3)));
    }

    [Fact]
    public void ImmutableSetsFormatWithHashBraces()
    {
        Assert.Equal("#{1}", ReplValueFormatter.Format(ImmutableHashSet.Create(1)));
    }

    [Fact]
    public void DictionariesFormatAsKeyValueBraces()
    {
        var dict = new Dictionary<string, int> { ["k"] = 1 };
        Assert.Equal("{\"k\": 1}", ReplValueFormatter.Format(dict));
    }

    [Fact]
    public void MutableListsFallToObjectFormatting()
    {
        // Only System.Collections.Immutable sequences get scheme-style sequence
        // formatting; a mutable List<int> falls through to the object formatter.
        var formatted = ReplValueFormatter.Format(new List<int> { 1, 2 });
        Assert.DoesNotContain("(1 2)", formatted);
    }

    private sealed class PlainClass
    {
        public int X { get; } = 1;
        public string Name { get; } = "n";
    }

    [Fact]
    public void PlainClassFormatsViaReflection()
    {
        Assert.Equal(
            "PlainClass { X = 1, Name = \"n\" }",
            ReplValueFormatter.Format(new PlainClass())
        );
    }

    private sealed record RecordWithToString(int X);

    [Fact]
    public void RecordCustomToStringPassesThrough()
    {
        Assert.Equal(
            new RecordWithToString(7).ToString(),
            ReplValueFormatter.Format(new RecordWithToString(7))
        );
    }

    private sealed class ThrowingProperty
    {
        public int Ok { get; } = 5;
        public int Boom => throw new InvalidOperationException("no");
    }

    [Fact]
    public void ThrowingPropertyGetterIsSkipped()
    {
        Assert.Equal(
            "ThrowingProperty { Ok = 5 }",
            ReplValueFormatter.Format(new ThrowingProperty())
        );
    }

    private sealed class NoProperties;

    [Fact]
    public void ClassWithoutPropertiesFormatsAsBareTypeName()
    {
        Assert.Equal("NoProperties", ReplValueFormatter.Format(new NoProperties()));
    }
}

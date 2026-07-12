using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public sealed class SelectionRangeTests
{
    private static Position At(string source, string token, int occurrence = 1)
    {
        var (line, col) = LspTestSession.Locate(source, token, occurrence);
        return new Position(line - 1, col - 1);
    }

    private static List<Range> ChainOf(SelectionRange? range)
    {
        var chain = new List<Range>();
        for (var current = range; current is not null; current = current.Parent)
            chain.Add(current.Range);
        return chain;
    }

    [Fact]
    public void CursorOnAtom_ChainWidensToTopLevelForm()
    {
        var source = "(define (f x)\n  (let ([y 1])\n    (+ x y)))\n";
        var chain = ChainOf(SelectionRangeHandler.Compute(source, At(source, "y", 2)));

        // Innermost first: the atom itself.
        Assert.Equal(new Position(2, 9), chain[0].Start);
        Assert.Equal(new Position(2, 10), chain[0].End);

        // Every step strictly contains the previous one.
        for (var i = 1; i < chain.Count; i++)
        {
            Assert.True(chain[i].Start <= chain[i - 1].Start);
            Assert.True(chain[i].End >= chain[i - 1].End);
            Assert.NotEqual(chain[i], chain[i - 1]);
        }

        // Outermost is the whole define form.
        var outer = chain[^1];
        Assert.Equal(new Position(0, 0), outer.Start);
        Assert.Equal(new Position(2, 13), outer.End);
    }

    [Fact]
    public void ChainIncludesInteriorAndFullBracketSteps()
    {
        var source = "(+ a b)";
        var chain = ChainOf(SelectionRangeHandler.Compute(source, At(source, "a")));

        // atom -> interior "+ a b" -> full "(+ a b)"
        Assert.Equal(3, chain.Count);
        Assert.Equal(new Range(new Position(0, 3), new Position(0, 4)), chain[0]);
        Assert.Equal(new Range(new Position(0, 1), new Position(0, 6)), chain[1]);
        Assert.Equal(new Range(new Position(0, 0), new Position(0, 7)), chain[2]);
    }

    [Fact]
    public void CursorOnOpenParen_SkipsInterior()
    {
        var source = "(+ a b)";
        var chain = ChainOf(SelectionRangeHandler.Compute(source, new Position(0, 0)));

        var full = Assert.Single(chain);
        Assert.Equal(new Range(new Position(0, 0), new Position(0, 7)), full);
    }

    [Fact]
    public void BindingBracket_IsAStepInTheChain()
    {
        var source = "(let ([x 1]) x)";
        var chain = ChainOf(SelectionRangeHandler.Compute(source, At(source, "1")));

        // The [x 1] bracket with and without delimiters.
        Assert.Contains(chain, r => r == new Range(new Position(0, 6), new Position(0, 11)));
        Assert.Contains(chain, r => r == new Range(new Position(0, 7), new Position(0, 10)));
    }

    [Fact]
    public void OutsideAnyForm_ReturnsNull()
    {
        var source = "(define x 1)\n\n";
        Assert.Null(SelectionRangeHandler.Compute(source, new Position(1, 0)));
    }
}

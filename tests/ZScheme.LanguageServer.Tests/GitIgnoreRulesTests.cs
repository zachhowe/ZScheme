using Xunit;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests;

/// <summary>Pattern-matching unit tests for the <c>.gitignore</c> subset the workspace
///     scan relies on.</summary>
public sealed class GitIgnoreRulesTests
{
    private static GitIgnoreRules Rules(params string[] lines) => GitIgnoreRules.Parse(lines);

    [Fact]
    public void BareName_MatchesAtAnyDepth()
    {
        var rules = Rules("original.zs");

        Assert.True(rules.Match("original.zs", isDirectory: false));
        Assert.True(rules.Match("a/b/original.zs", isDirectory: false));
        Assert.Null(rules.Match("a/other.zs", isDirectory: false));
    }

    [Fact]
    public void LeadingSlash_AnchorsToTheGitIgnoreDirectory()
    {
        var rules = Rules("/grammars");

        Assert.True(rules.Match("grammars", isDirectory: true));
        Assert.Null(rules.Match("editor/grammars", isDirectory: true));
    }

    [Fact]
    public void EmbeddedSlash_Anchors()
    {
        var rules = Rules("examples/out");

        Assert.True(rules.Match("examples/out", isDirectory: true));
        Assert.Null(rules.Match("pkg/examples/out", isDirectory: true));
    }

    [Fact]
    public void TrailingSlash_MatchesDirectoriesOnly()
    {
        var rules = Rules("fuzz-runs/");

        Assert.True(rules.Match("fuzz-runs", isDirectory: true));
        Assert.Null(rules.Match("fuzz-runs", isDirectory: false));
    }

    [Fact]
    public void Negation_ReIncludes_LastMatchWins()
    {
        var rules = Rules("*.gen.zs", "!keep.gen.zs");

        Assert.True(rules.Match("a/foo.gen.zs", isDirectory: false));
        Assert.False(rules.Match("a/keep.gen.zs", isDirectory: false));
    }

    [Fact]
    public void CommentsAndBlankLines_AreSkipped()
    {
        var rules = Rules("", "   ", "# dist", "dist/");

        Assert.Null(rules.Match("dist", isDirectory: false));
        Assert.True(rules.Match("dist", isDirectory: true));
    }

    [Fact]
    public void EscapedHash_IsALiteralName()
    {
        var rules = Rules("\\#weird.zs");

        Assert.True(rules.Match("#weird.zs", isDirectory: false));
    }

    [Fact]
    public void Star_DoesNotCrossDirectorySeparators()
    {
        var rules = Rules("src/*.zs");

        Assert.True(rules.Match("src/a.zs", isDirectory: false));
        Assert.Null(rules.Match("src/nested/a.zs", isDirectory: false));
    }

    [Fact]
    public void DoubleStar_SpansDirectories()
    {
        var leading = Rules("**/artifacts");
        Assert.True(leading.Match("artifacts", isDirectory: true));
        Assert.True(leading.Match("runs/2026/artifacts", isDirectory: true));

        var infix = Rules("runs/**/original.zs");
        Assert.True(infix.Match("runs/original.zs", isDirectory: false));
        Assert.True(infix.Match("runs/a/b/original.zs", isDirectory: false));
        Assert.Null(infix.Match("other/a/original.zs", isDirectory: false));

        var trailing = Rules("runs/**");
        Assert.True(trailing.Match("runs/a/b.zs", isDirectory: false));
        Assert.Null(trailing.Match("runs", isDirectory: true));
    }

    [Fact]
    public void QuestionMarkAndCharacterClasses()
    {
        var question = Rules("v?.zs");
        Assert.True(question.Match("v1.zs", isDirectory: false));
        Assert.Null(question.Match("v10.zs", isDirectory: false));

        var cls = Rules("case[0-9].zs");
        Assert.True(cls.Match("case7.zs", isDirectory: false));
        Assert.Null(cls.Match("casex.zs", isDirectory: false));

        var negatedCls = Rules("case[!0-9].zs");
        Assert.True(negatedCls.Match("casex.zs", isDirectory: false));
        Assert.Null(negatedCls.Match("case7.zs", isDirectory: false));
    }

    [Fact]
    public void TrailingSpaces_AreInsignificantUnlessEscaped()
    {
        Assert.True(Rules("temp.zs   ").Match("temp.zs", isDirectory: false));
        Assert.True(Rules("temp.zs\\ ").Match("temp.zs ", isDirectory: false));
    }

    [Fact]
    public void UnmatchedPattern_HasNoOpinion()
    {
        Assert.Null(Rules("dist/").Match("packages/stdlib/src/list.zs", isDirectory: false));
    }
}

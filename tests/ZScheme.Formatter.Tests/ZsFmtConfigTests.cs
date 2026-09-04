using Xunit;
using ZScheme.Formatter;

namespace ZScheme.Formatter.Tests;

public class ZsFmtConfigTests
{
    [Fact]
    public void NoZsFmt_ReturnsBaseUnchanged()
    {
        var dir = NewTempDir();
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.Equal(4, result.IndentSize);
            Assert.Equal(100, result.MaxLineLength);
            Assert.True(result.MergeImports);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParsesEveryScalarOption()
    {
        var dir = WriteZsFmt(
            NewTempDir(),
            """
            (format
              (indent-size 2)
              (indent-style tab)
              (max-line-length 80)
              (insert-final-newline #f)
              (trim-trailing-whitespace #f)
              (merge-imports #f)
              (trailing-comment-spaces 3))
            """
        );
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.Equal(2, result.IndentSize);
            Assert.True(result.UseTabs);
            Assert.Equal(80, result.MaxLineLength);
            Assert.False(result.InsertFinalNewline);
            Assert.False(result.TrimTrailingWhitespace);
            Assert.False(result.MergeImports);
            Assert.Equal(3, result.TrailingCommentSpaces);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Booleans_AcceptHashAndWordSpellings()
    {
        var dir = WriteZsFmt(
            NewTempDir(),
            "(format (merge-imports true) (insert-final-newline false) (trim-trailing-whitespace #t))"
        );
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.True(result.MergeImports);
            Assert.False(result.InsertFinalNewline);
            Assert.True(result.TrimTrailingWhitespace);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KeywordSets_AddAndRemoveRelativeToDefaults()
    {
        var dir = WriteZsFmt(
            NewTempDir(),
            """
            (format
              (keep-first-operand foo -if)
              (always-break-body bar -match))
            """
        );
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);

            Assert.Contains("foo", result.KeepFirstOperand);
            Assert.DoesNotContain("if", result.KeepFirstOperand);
            Assert.Contains("when", result.KeepFirstOperand); // untouched default survives

            Assert.Contains("bar", result.AlwaysBreakBody);
            Assert.DoesNotContain("match", result.AlwaysBreakBody);
            Assert.Contains("cond", result.AlwaysBreakBody); // untouched default survives
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NestedZsFmt_OverridesParentButInheritsUnsetFields()
    {
        var parent = NewTempDir();
        WriteZsFmt(parent, "(format (indent-size 2) (max-line-length 80))");
        var child = Directory.CreateDirectory(Path.Combine(parent, "sub")).FullName;
        WriteZsFmt(child, "(format (indent-size 8))");
        try
        {
            var inChild = ZsFmtConfig.Resolve(
                Path.Combine(child, "f.zs"),
                FormattingOptions.Default
            );
            Assert.Equal(8, inChild.IndentSize); // child wins
            Assert.Equal(80, inChild.MaxLineLength); // inherited from parent

            var inParent = ZsFmtConfig.Resolve(
                Path.Combine(parent, "f.zs"),
                FormattingOptions.Default
            );
            Assert.Equal(2, inParent.IndentSize);
            Assert.Equal(80, inParent.MaxLineLength);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void RootMarker_StopsWalkUp()
    {
        var parent = NewTempDir();
        WriteZsFmt(parent, "(format (indent-size 2) (max-line-length 50))");
        var child = Directory.CreateDirectory(Path.Combine(parent, "sub")).FullName;
        WriteZsFmt(child, "(format (root #t) (indent-size 8))");
        try
        {
            var result = ZsFmtConfig.Resolve(
                Path.Combine(child, "f.zs"),
                FormattingOptions.Default
            );
            Assert.Equal(8, result.IndentSize); // from child
            Assert.Equal(100, result.MaxLineLength); // parent ignored past the root marker
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void MalformedFile_IsIgnored()
    {
        var dir = WriteZsFmt(NewTempDir(), "(((");
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.Equal(4, result.IndentSize); // falls back to base
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UnknownClause_IsIgnored_AndOtherClausesStillApply()
    {
        var dir = WriteZsFmt(NewTempDir(), "(format (bogus 1) (indent-size 3))");
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.Equal(3, result.IndentSize);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MalformedValues_AreSkipped()
    {
        var dir = WriteZsFmt(
            NewTempDir(),
            "(format (indent-size oops) (max-line-length 0) (trailing-comment-spaces -1))"
        );
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            Assert.Equal(4, result.IndentSize); // non-integer skipped
            Assert.Equal(100, result.MaxLineLength); // non-positive skipped
            Assert.Equal(2, result.TrailingCommentSpaces); // negative skipped
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RenderDefault_RoundTripsToDefaults()
    {
        var dir = WriteZsFmt(NewTempDir(), ZsFmtConfig.RenderDefault());
        try
        {
            var result = ZsFmtConfig.Resolve(Path.Combine(dir, "f.zs"), FormattingOptions.Default);
            var defaults = FormattingOptions.Default;

            Assert.Equal(defaults.IndentSize, result.IndentSize);
            Assert.Equal(defaults.UseTabs, result.UseTabs);
            Assert.Equal(defaults.MaxLineLength, result.MaxLineLength);
            Assert.Equal(defaults.InsertFinalNewline, result.InsertFinalNewline);
            Assert.Equal(defaults.TrimTrailingWhitespace, result.TrimTrailingWhitespace);
            Assert.Equal(defaults.MergeImports, result.MergeImports);
            Assert.Equal(defaults.TrailingCommentSpaces, result.TrailingCommentSpaces);
            Assert.True(result.KeepFirstOperand.SetEquals(defaults.KeepFirstOperand));
            Assert.True(result.AlwaysBreakBody.SetEquals(defaults.AlwaysBreakBody));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zsfmt-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteZsFmt(string dir, string content)
    {
        File.WriteAllText(Path.Combine(dir, ZsFmtConfig.FileName), content);
        return dir;
    }
}

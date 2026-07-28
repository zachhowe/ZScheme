using Xunit;
using ZScheme.Formatter;

namespace ZScheme.Formatter.Tests;

public class EditorConfigParserTests
{
    [Fact]
    public void NoEditorConfig_ReturnsNull()
    {
        var result = EditorConfigParser.TryParse("/tmp/nonexistent-file-xyz.zs");
        Assert.Null(result);
    }

    [Fact]
    public void EditorConfigWithIndentSize_ParsesCorrectly()
    {
        var dir = CreateTempEditorConfig("indent_size = 2");
        try
        {
            var result = EditorConfigParser.TryParse(Path.Combine(dir, "test.zs"));
            Assert.NotNull(result);
            Assert.Equal(2, result!.IndentSize);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EditorConfigWithTabs_ParsesCorrectly()
    {
        var dir = CreateTempEditorConfig("indent_style = tab");
        try
        {
            var result = EditorConfigParser.TryParse(Path.Combine(dir, "test.zs"));
            Assert.NotNull(result);
            Assert.True(result!.UseTabs);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EditorConfigWithFinalNewline_ParsesCorrectly()
    {
        var dir = CreateTempEditorConfig("insert_final_newline = true");
        try
        {
            var result = EditorConfigParser.TryParse(Path.Combine(dir, "test.zs"));
            Assert.NotNull(result);
            Assert.True(result!.InsertFinalNewline);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string CreateTempEditorConfig(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"editorconfig-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, ".editorconfig"),
            $@"[*{{.zs}}]
{content}
"
        );
        return dir;
    }
}

using Xunit;
using ZScheme.Formatter;
using Fmt = ZScheme.Formatter.Formatter;

namespace ZScheme.Formatter.Tests;

public class FormatterTests
{
    [Fact]
    public void FormatFile_PreservesSemanticsAndIsIdempotent()
    {
        var source = string.Join(
            "\n",
            ";; a module",
            "(module demo)",
            "",
            "(import b)",
            "(import a)",
            "",
            "(define (greet [name : String]) : String",
            "  (string-append \"hi \" name)) ; trailing",
            ""
        );

        using var file = new TempFile(source);
        var first = Fmt.FormatFile(file.Path, FormattingOptions.Default);

        Assert.Null(first.Warning); // re-lex guard did not trip
        Assert.Contains("(import b\n        a)", first.Formatted); // consecutive imports merged, one per line
        Assert.Contains("\"hi \"", first.Formatted); // string literal kept its quotes
        Assert.Contains("; trailing", first.Formatted); // trailing comment kept

        using var file2 = new TempFile(first.Formatted);
        var second = Fmt.FormatFile(file2.Path, FormattingOptions.Default);
        Assert.Null(second.Warning);
        Assert.False(second.Changed); // already formatted -> idempotent
        Assert.Equal(first.Formatted, second.Formatted);
    }

    [Fact]
    public void FormatFile_OnSyntaxErrors_LeavesSourceUntouchedWithWarning()
    {
        using var file = new TempFile("(define x");
        var result = Fmt.FormatFile(file.Path, FormattingOptions.Default);

        Assert.NotNull(result.Warning);
        Assert.False(result.Changed);
        Assert.Equal("(define x", result.Formatted);
    }

    [Fact]
    public void FormatFile_DiscoversZsFmt_AndDisablesImportMerging()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zsfmt-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".zsfmt"), "(format (merge-imports #f))");
            var zsPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(zsPath, "(import a)\n(import b)\n");

            // No explicit options -> FormatFile resolves .zsfmt from the file's directory.
            var result = Fmt.FormatFile(zsPath);

            Assert.Null(result.Warning);
            Assert.Contains("(import a)", result.Formatted);
            Assert.Contains("(import b)", result.Formatted);
            Assert.DoesNotContain("(import a b)", result.Formatted); // merging disabled by config
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string contents)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"zsfmt-{Guid.NewGuid():N}.zs"
            );
            File.WriteAllText(Path, contents);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            { /* best effort */
            }
        }
    }
}

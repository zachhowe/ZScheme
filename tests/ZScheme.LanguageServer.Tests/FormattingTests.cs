using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Fmt = ZScheme.Formatter.Formatter;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public class FormattingTests
{
    private static IReadOnlyList<TextEdit> Format(
        AnalysisService service,
        string uri,
        Range? range = null,
        FormattingOptions? options = null
    )
    {
        return FormattingSupport.ComputeEdits(service, DocumentUri.From(uri), options, range);
    }

    private static string Apply(string source, IReadOnlyList<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start.Line))
        {
            var start = SourceText.OffsetAt(
                source,
                edit.Range.Start.Line,
                edit.Range.Start.Character
            );
            var end = SourceText.OffsetAt(source, edit.Range.End.Line, edit.Range.End.Character);
            source = source[..start] + edit.NewText + source[end..];
        }

        return source;
    }

    private static string PathOf(string uri)
    {
        return DocumentUri.From(uri).GetFileSystemPath();
    }

    [Fact]
    public void UnformattedDocument_EditsReproduceFormatterOutput()
    {
        var source = "(define    (add a b)\n(+ a\nb))\n";
        var (service, uri) = LspTestSession.Open(source);

        var edits = Format(service, uri);

        Assert.NotEmpty(edits);
        var expected = Fmt.FormatSource(source, PathOf(uri)).Formatted;
        Assert.Equal(expected, Apply(source, edits));
    }

    [Fact]
    public void AlreadyFormatted_NoEdits()
    {
        var path = LspTestSession.SyntheticUri(nameof(AlreadyFormatted_NoEdits));
        var formatted = Fmt.FormatSource(
            "(define    (add a b)\n(+ a\nb))\n",
            PathOf(path)
        ).Formatted;

        var (service, uri) = LspTestSession.Open(formatted);

        Assert.Empty(Format(service, uri));
    }

    [Fact]
    public void SyntaxError_NoEdits()
    {
        // Unbalanced parens: the formatter declines, and formatting must stay silent rather
        // than reporting an error on what is a normal mid-edit state.
        var (service, uri) = LspTestSession.Open("(define    (add a b)\n(+ a b)\n");

        Assert.Empty(Format(service, uri));
    }

    [Fact]
    public void UnknownDocument_NoEdits()
    {
        var service = new AnalysisService();

        Assert.Empty(Format(service, LspTestSession.SyntheticUri(nameof(UnknownDocument_NoEdits))));
    }

    [Fact]
    public void ManifestDocument_NotFormatted()
    {
        // .zspkg manifests are a different grammar than the formatter targets.
        var (service, uri) = LspTestSession.Open("(package   (name  \"demo\"))\n", ".zspkg");

        Assert.Empty(Format(service, uri));
    }

    [Fact]
    public void SmallChange_DoesNotReplaceWholeDocument()
    {
        var lines = Enumerable.Range(0, 20).Select(i => $"(define x{i} {i})").ToList();
        lines[10] = "(define    x10 10)";
        var source = string.Join("\n", lines) + "\n";

        var (service, uri) = LspTestSession.Open(source);

        var edit = Assert.Single(Format(service, uri));
        Assert.Equal(10, edit.Range.Start.Line);
        Assert.Equal(11, edit.Range.End.Line);
    }

    [Fact]
    public void RangeFormatting_LeavesFormsOutsideSelectionAlone()
    {
        var source = "(define    a 1)\n(define    b 2)\n(define    c 3)\n";
        var (service, uri) = LspTestSession.Open(source);

        var edits = Format(service, uri, new Range(new Position(1, 0), new Position(1, 15)));

        var edit = Assert.Single(edits);
        Assert.Equal(1, edit.Range.Start.Line);
        var updated = Apply(source, edits);
        Assert.Equal("(define    a 1)\n(define b 2)\n(define    c 3)\n", updated);
    }

    [Fact]
    public void RangeFormatting_AgreesWithFullFormatForTheLinesItCovers()
    {
        var source = "(define    a 1)\n(define    b 2)\n(define    c 3)\n";
        var (service, uri) = LspTestSession.Open(source);

        var whole = Apply(source, Format(service, uri));
        var everything = Apply(
            source,
            Format(service, uri, new Range(new Position(0, 0), new Position(3, 0)))
        );

        Assert.Equal(whole, everything);
    }

    [Fact]
    public async Task UsesCurrentBuffer_NotTheDebouncedAnalysisSnapshot()
    {
        var (service, uri) = LspTestSession.Open("(define x 1)\n");

        // Analysis is debounced 300ms, so the stored DocumentState still holds the old text
        // here. Formatting the stale snapshot would produce edits that corrupt the buffer.
        var pending = service.AnalyzeAsync(uri, "(define    y 2)\n", 2);
        Assert.Equal("(define x 1)\n", service.GetDocument(uri)!.Source);

        var edits = Format(service, uri);
        Assert.Equal("(define y 2)\n", Apply("(define    y 2)\n", edits));

        await pending;
    }

    [Fact]
    public void ClientTabSize_AppliesWhenNoProjectConfig()
    {
        using var dir = new TempDirectory();
        var (service, uri) = dir.Open("(define (f x)\n(+ x\n1))\n");

        var edits = Format(
            service,
            uri,
            options: new FormattingOptions { TabSize = 7, InsertSpaces = true }
        );

        Assert.Contains("\n       ", Apply(dir.Source, edits));
    }

    [Fact]
    public void ZsFmt_OverridesClientTabSize()
    {
        using var dir = new TempDirectory();
        dir.WriteConfig(".zsfmt", "(format (root #t) (indent-size 3))\n");
        var (service, uri) = dir.Open("(define (f x)\n(+ x\n1))\n");

        var edits = Format(
            service,
            uri,
            options: new FormattingOptions { TabSize = 7, InsertSpaces = true }
        );

        var formatted = Apply(dir.Source, edits);
        Assert.Contains("\n   ", formatted);
        Assert.DoesNotContain("\n       ", formatted);
    }

    /// <summary>A scratch directory outside the repo, so the repo's own <c>.zsfmt</c> and
    ///     <c>.editorconfig</c> do not decide the outcome of config-precedence tests.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            Directory
                .CreateDirectory(
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "zs-lsp-fmt-" + Guid.NewGuid().ToString("n")
                    )
                )
                .FullName;

        public string Source { get; private set; } = "";

        public void WriteConfig(string name, string contents)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);
        }

        public (AnalysisService Service, string Uri) Open(string source)
        {
            Source = source;
            var file = System.IO.Path.Combine(Path, "sample.zs");
            File.WriteAllText(file, source);

            var uri = new Uri(file).AbsoluteUri;
            var service = new AnalysisService();
            service.AnalyzeImmediate(uri, source, 1);
            return (service, uri);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}

using System.Text;
using ZScheme.Compiler.Syntax;

namespace ZScheme.MacroDebugger.ViewModels;

/// <summary>
///     Per-file cache of printed top-level form segments for the progressive full-file view:
///     forms above the current one show their final expanded output, forms below show raw
///     source. define-syntax forms (absent from the expanded-output map because the expander
///     consumes them) stay visible as written in both states. Each form is printed at most
///     once per file load; prefix/suffix joins are memoized per top-level index.
/// </summary>
public sealed class DocumentContext
{
    public const string FormSeparator = "\n\n";

    public static readonly DocumentContext Empty = new([], []);

    private readonly string[] _finalSegments;
    private readonly Dictionary<int, string> _prefixCache = [];
    private readonly string[] _rawSegments;
    private readonly Dictionary<int, string> _suffixCache = [];

    private DocumentContext(string[] rawSegments, string[] finalSegments)
    {
        _rawSegments = rawSegments;
        _finalSegments = finalSegments;
    }

    public static DocumentContext Build(
        IReadOnlyList<SExpr>? rawForms,
        IReadOnlyDictionary<int, IReadOnlyList<SExpr>>? expandedByRawIndex,
        int printWidth
    )
    {
        if (rawForms is null || rawForms.Count == 0)
            return Empty;

        var raw = new string[rawForms.Count];
        var final = new string[rawForms.Count];
        for (var i = 0; i < rawForms.Count; i++)
        {
            raw[i] = SExprPrinter.Print(rawForms[i], null, printWidth).Text;
            if (expandedByRawIndex is not null && expandedByRawIndex.TryGetValue(i, out var outputs))
                final[i] = JoinForms(outputs, printWidth);
            else
                // Consumed (define-syntax) or no expansion data — show the form as written
                final[i] = raw[i];
        }

        return new DocumentContext(raw, final);
    }

    /// <summary>Forms [0, topLevelIndex) in their final expanded state.</summary>
    public string PrefixFor(int topLevelIndex)
    {
        if (!_prefixCache.TryGetValue(topLevelIndex, out var prefix))
        {
            var end = Math.Min(topLevelIndex, _finalSegments.Length);
            prefix = JoinSegments(0, end, i => _finalSegments[i]);
            _prefixCache[topLevelIndex] = prefix;
        }
        return prefix;
    }

    /// <summary>Forms (topLevelIndex, n) as written in source.</summary>
    public string SuffixFor(int topLevelIndex)
    {
        if (!_suffixCache.TryGetValue(topLevelIndex, out var suffix))
        {
            var start = Math.Max(topLevelIndex + 1, 0);
            suffix = JoinSegments(start, _rawSegments.Length, i => _rawSegments[i]);
            _suffixCache[topLevelIndex] = suffix;
        }
        return suffix;
    }

    private static string JoinForms(IReadOnlyList<SExpr> forms, int printWidth)
    {
        var sb = new StringBuilder();
        foreach (var form in forms)
        {
            if (sb.Length > 0)
                sb.Append(FormSeparator);
            sb.Append(SExprPrinter.Print(form, null, printWidth).Text);
        }
        return sb.ToString();
    }

    private string JoinSegments(int start, int end, Func<int, string> segment)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var text = segment(i);
            if (text.Length == 0)
                continue; // form expanded to nothing — no empty slot in the document
            if (sb.Length > 0)
                sb.Append(FormSeparator);
            sb.Append(text);
        }
        return sb.ToString();
    }
}

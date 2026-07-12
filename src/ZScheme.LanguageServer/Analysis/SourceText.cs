namespace ZScheme.LanguageServer.Analysis;

/// <summary>Position/offset conversions and lexical helpers over a document's source
///     text, shared by completion (prefix extraction) and code actions (edit synthesis).</summary>
internal static class SourceText
{
    /// <summary>
    ///     Offset of the (0-based) <paramref name="line" />/<paramref name="character" />
    ///     position. Clamped to the end of the line (and the end of the document), so
    ///     out-of-range client positions never index past a newline.
    /// </summary>
    public static int OffsetAt(string source, int line, int character)
    {
        var offset = 0;
        var currentLine = 0;
        while (currentLine < line && offset < source.Length)
        {
            if (source[offset] == '\n')
                currentLine++;
            offset++;
        }

        var lineEnd = offset;
        while (lineEnd < source.Length && source[lineEnd] != '\n')
            lineEnd++;

        return Math.Min(offset + Math.Max(0, character), lineEnd);
    }

    /// <summary>The (0-based) line/character position of <paramref name="offset" />.</summary>
    public static (int Line, int Character) PositionAt(string source, int offset)
    {
        var line = 0;
        var lineStart = 0;
        var end = Math.Min(offset, source.Length);
        for (var i = 0; i < end; i++)
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }

        return (line, end - lineStart);
    }

    /// <summary>
    ///     Offset just past the bracket that closes the opener at
    ///     <paramref name="openOffset" /> (one of <c>( [ {</c>), skipping nested pairs of
    ///     every kind, string literals, and <c>;</c> comments. Returns -1 when unbalanced.
    /// </summary>
    public static int SkipBalanced(string source, int openOffset)
    {
        if (openOffset >= source.Length || source[openOffset] is not ('(' or '[' or '{'))
            return -1;

        var depth = 0;
        for (var i = openOffset; i < source.Length; i++)
        {
            var c = source[i];
            switch (c)
            {
                case '"':
                    i++;
                    while (i < source.Length && source[i] != '"')
                        i += source[i] == '\\' ? 2 : 1;
                    break;
                case ';':
                    while (i < source.Length && source[i] != '\n')
                        i++;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0)
                        return i + 1;
                    break;
            }
        }

        return -1;
    }

    /// <summary>Characters that can appear in a ZScheme identifier (covers operator
    ///     names like <c>list-&gt;vector</c> and qualified names like <c>list/map</c>).</summary>
    public static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || "-*/+!?<>=_.^".Contains(c);
    }

    /// <summary>The partial identifier immediately before <paramref name="offset" />
    ///     (empty when the cursor follows whitespace or a delimiter).</summary>
    public static string IdentifierPrefixAt(string source, int offset)
    {
        var start = Math.Min(offset, source.Length);
        while (start > 0 && IsIdentifierChar(source[start - 1]))
            start--;
        return source[start..Math.Min(offset, source.Length)];
    }
}

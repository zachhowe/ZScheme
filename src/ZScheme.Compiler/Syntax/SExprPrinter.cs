using System.Text;

namespace ZScheme.Compiler.Syntax;

/// <summary>
///     Pretty-prints s-expressions with indentation, optionally reporting the span the
///     subtree at <c>markPath</c> (child indices from the root) occupies in the printed
///     text. Marking is path-based because macro expansion can insert the same
///     <see cref="SExpr" /> instance at multiple positions, and macro-produced nodes all
///     carry the call-site <see cref="Diagnostics.SourceSpan" /> — printed offsets are the
///     only reliable way to point at a subtree.
/// </summary>
public static class SExprPrinter
{
    public static Result Print(SExpr root, IReadOnlyList<int>? markPath = null, int maxWidth = 100)
    {
        var writer = new Writer(maxWidth);
        writer.Emit(root, markPath, 0);
        return new Result(writer.Text, writer.MarkedSpan);
    }

    /// <summary>Renders an atom, re-quoting and escaping string literals (the lexer stores
    ///     unescaped content, so <see cref="SExpr.Atom.Text" /> alone is not valid source).</summary>
    public static string AtomText(SExpr.Atom atom)
    {
        if (atom.Kind != TokenKind.StringLit)
            return atom.Text;

        var sb = new StringBuilder(atom.Text.Length + 2);
        sb.Append('"');
        foreach (var c in atom.Text)
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        sb.Append('"');
        return sb.ToString();
    }

    public readonly record struct TextSpan(int Start, int Length);

    public sealed record Result(string Text, TextSpan? MarkedSpan);

    private sealed class Writer(int maxWidth)
    {
        private readonly StringBuilder _sb = new();

        private readonly Dictionary<SExpr, int> _widths = new(ReferenceEqualityComparer.Instance);
        private int _column;

        public string Text => _sb.ToString();
        public TextSpan? MarkedSpan { get; private set; }

        /// <param name="markPath">
        ///     Non-null when the marked node lies in this subtree; the remaining path is
        ///     <c>markPath[depth..]</c>. Null when this subtree does not contain the mark.
        /// </param>
        public void Emit(SExpr node, IReadOnlyList<int>? markPath, int depth)
        {
            var marked = markPath is not null && depth == markPath.Count;
            var markStart = _sb.Length;

            switch (node)
            {
                case SExpr.Atom atom:
                    Append(AtomText(atom));
                    break;
                case SExpr.SList list:
                    EmitList(node, list.Items, '(', ')', markPath, depth);
                    break;
                case SExpr.BracketList bracket:
                    EmitList(node, bracket.Items, '[', ']', markPath, depth);
                    break;
            }

            if (marked)
                MarkedSpan = new TextSpan(markStart, _sb.Length - markStart);
        }

        private void EmitList(
            SExpr node,
            IReadOnlyList<SExpr> items,
            char open,
            char close,
            IReadOnlyList<int>? markPath,
            int depth
        )
        {
            var flat = items.Count <= 1 || _column + Measure(node) <= maxWidth;
            var openColumn = _column;

            Append(open);
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    if (flat)
                        Append(' ');
                    else
                        NewLine(openColumn + 2);
                }

                var childPath =
                    markPath is not null && depth < markPath.Count && markPath[depth] == i
                        ? markPath
                        : null;
                Emit(items[i], childPath, depth + 1);
            }
            Append(close);
        }

        private int Measure(SExpr node)
        {
            if (_widths.TryGetValue(node, out var cached))
                return cached;

            var width = node switch
            {
                SExpr.Atom atom => AtomText(atom).Length,
                SExpr.SList list => MeasureItems(list.Items),
                SExpr.BracketList bracket => MeasureItems(bracket.Items),
                _ => 0,
            };
            _widths[node] = width;
            return width;
        }

        private int MeasureItems(IReadOnlyList<SExpr> items)
        {
            var width = 2; // delimiters
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    width += 1; // separating space
                width += Measure(items[i]);
            }
            return width;
        }

        private void Append(char c)
        {
            _sb.Append(c);
            _column += 1;
        }

        private void Append(string s)
        {
            _sb.Append(s);
            _column += s.Length;
        }

        private void NewLine(int indent)
        {
            _sb.Append('\n');
            _sb.Append(' ', indent);
            _column = indent;
        }
    }
}

using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

/// <summary>
/// Anchors lexed comments to S-expression nodes by source position, producing a <see cref="CommentLayout"/>.
/// Comments live outside the syntax tree (the lexer collects them separately and <see cref="SExpr"/> carries
/// no trivia), so this works purely from line/column positions:
/// <list type="bullet">
/// <item>A comment that shares a line with code becomes the <i>trailing</i> comment of the outermost node
/// ending on that line.</item>
/// <item>A comment on its own line becomes a <i>leading</i> comment of the next node that starts on a later
/// line (outermost such node, so it attaches at the right nesting level).</item>
/// <item>Anything left over (e.g. a comment dangling before a closing paren or at end-of-file) is flushed at
/// EOF by the printer rather than dropped.</item>
/// </list>
/// Node end-lines are approximated as the deepest start-line in a node's subtree, because the parser does not
/// record a reliable end position for multi-line lists.
/// </summary>
public static class CommentAttacher
{
    private sealed class NodeInfo
    {
        public required SExpr Node;
        public int StartLine;
        public int StartColumn;
        public int EndLine;
        public List<SExpr> Children = [];
    }

    public static CommentLayout Attach(
        IReadOnlyList<SExpr> forms,
        IReadOnlyList<Token> comments,
        IReadOnlyList<Token> tokens
    )
    {
        if (comments.Count == 0 && forms.Count <= 1)
            return CommentLayout.Empty;

        var info = new Dictionary<SExpr, NodeInfo>(ReferenceEqualityComparer.Instance);
        var all = new List<NodeInfo>();
        foreach (var form in forms)
            Collect(form, info, all);

        var ordered = all.OrderBy(n => n.StartLine).ThenBy(n => n.StartColumn).ToList();

        var leading = new Dictionary<SExpr, List<Token>>(ReferenceEqualityComparer.Instance);
        var trailing = new Dictionary<SExpr, Token>(ReferenceEqualityComparer.Instance);

        // Every source line that contains a token (including bare delimiters like a lone ')') or a comment.
        // A line absent from this set is genuinely blank — the precise, layout-independent signal that makes
        // blank-line preservation idempotent.
        var occupied = new HashSet<int>();
        foreach (var t in tokens)
            if (t.Kind != TokenKind.Eof)
                occupied.Add(t.Span.Line);
        foreach (var c in comments)
            occupied.Add(c.Span.Line);

        foreach (var comment in comments.OrderBy(c => c.Span.Line).ThenBy(c => c.Span.Column))
        {
            var line = comment.Span.Line;
            var col = comment.Span.Column;

            // A line comment runs to EOL, so any node starting on this line necessarily starts before it.
            var codeOnLine = ordered.Any(n => n.StartLine == line);

            if (codeOnLine)
            {
                // Trailing: attach to the outermost node whose subtree ends on this line.
                var owner = ordered
                    .Where(n => n.EndLine == line)
                    .OrderBy(n => n.StartColumn)
                    .ThenBy(n => n.StartLine)
                    .FirstOrDefault();
                if (owner != null && !trailing.ContainsKey(owner.Node))
                    trailing[owner.Node] = comment;
                // else: left unemitted -> flushed at EOF.
            }
            else
            {
                // Leading: attach to the next node that begins on a later line (outermost on the
                // earliest such line, since `ordered` is sorted by line then column).
                var target = ordered.FirstOrDefault(n => n.StartLine > line);
                if (target != null)
                {
                    if (!leading.TryGetValue(target.Node, out var list))
                        leading[target.Node] = list = [];
                    list.Add(comment);
                }
                // else: trailing/dangling at EOF -> flushed by the printer.
            }
        }

        var blankBefore = ComputeBlankBefore(forms, info, leading, occupied);
        var blocksFlat = ComputeBlocksFlat(forms, info, leading, trailing);

        return new CommentLayout(leading, trailing, blankBefore, blocksFlat, comments, occupied);
    }

    private static NodeInfo Collect(
        SExpr node,
        Dictionary<SExpr, NodeInfo> info,
        List<NodeInfo> all
    )
    {
        var ni = new NodeInfo
        {
            Node = node,
            StartLine = node.Span.Line,
            StartColumn = node.Span.Column,
            EndLine = node.Span.Line,
        };
        info[node] = ni;
        all.Add(ni);

        foreach (var child in Children(node))
        {
            ni.Children.Add(child);
            var ci = Collect(child, info, all);
            if (ci.EndLine > ni.EndLine)
                ni.EndLine = ci.EndLine;
        }

        return ni;
    }

    private static IReadOnlyList<SExpr> Children(SExpr node) =>
        node switch
        {
            SExpr.SList list => list.Items,
            SExpr.BracketList list => list.Items,
            _ => [],
        };

    // Marks a non-first sibling when the source had a blank line directly above its block (its leading
    // comments, or the node itself). "Blank" means the line immediately above is not occupied by any token
    // or comment. Because the printer emits exactly one blank line in that case, re-parsing its own output
    // reproduces the same decision — so the formatter is a fixed point (idempotent), and runs of blank lines
    // collapse to one.
    private static HashSet<SExpr> ComputeBlankBefore(
        IReadOnlyList<SExpr> forms,
        Dictionary<SExpr, NodeInfo> info,
        Dictionary<SExpr, List<Token>> leading,
        HashSet<int> occupied
    )
    {
        var result = new HashSet<SExpr>(ReferenceEqualityComparer.Instance);
        MarkSiblings(forms, info, leading, occupied, result);
        foreach (var ni in info.Values)
            MarkSiblings(ni.Children, info, leading, occupied, result);
        return result;
    }

    private static void MarkSiblings(
        IReadOnlyList<SExpr> siblings,
        Dictionary<SExpr, NodeInfo> info,
        Dictionary<SExpr, List<Token>> leading,
        HashSet<int> occupied,
        HashSet<SExpr> result
    )
    {
        for (var i = 1; i < siblings.Count; i++)
        {
            var cur = siblings[i];
            var topLine = info[cur].StartLine;
            if (leading.TryGetValue(cur, out var lead) && lead.Count > 0)
                topLine = Math.Min(topLine, lead.Min(c => c.Span.Line));

            // The blank line must sit strictly between the previous sibling's first line and this one,
            // otherwise a child that shares its parent's opening line (e.g. the name in
            // `(define-syntax foo`) would inherit the blank that precedes the whole form.
            var prevLine = info[siblings[i - 1]].StartLine;
            if (topLine - 1 > prevLine && !occupied.Contains(topLine - 1))
                result.Add(cur);
        }
    }

    // A node cannot be rendered on a single line if any child carries a leading or trailing comment, or
    // recursively blocks flattening. (A node's own trailing comment is fine — it is appended after the
    // flat render — and its own leading comment is emitted by the container before the node.)
    private static HashSet<SExpr> ComputeBlocksFlat(
        IReadOnlyList<SExpr> forms,
        Dictionary<SExpr, NodeInfo> info,
        Dictionary<SExpr, List<Token>> leading,
        Dictionary<SExpr, Token> trailing
    )
    {
        var blocks = new HashSet<SExpr>(ReferenceEqualityComparer.Instance);
        foreach (var form in forms)
            Visit(form);
        return blocks;

        bool Visit(SExpr node)
        {
            var blocked = false;
            foreach (var child in info[node].Children)
            {
                var childBlocks = Visit(child);
                if (childBlocks || leading.ContainsKey(child) || trailing.ContainsKey(child))
                    blocked = true;
            }

            if (blocked)
                blocks.Add(node);
            return blocked;
        }
    }
}

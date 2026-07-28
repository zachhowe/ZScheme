using System.Text;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

/// <summary>
/// Renders S-expressions to text using a "flat-first" strategy: every form is first rendered on a single
/// line and kept as-is when it fits the line-width budget at its current indent; otherwise it falls back to
/// a form-specific multi-line layout whose children independently re-apply the same decision. Comments and
/// blank lines come from a <see cref="CommentLayout"/> side-table.
/// <para>
/// Line breaks are always emitted as a literal <c>'\n'</c>, never via <c>StringBuilder.AppendLine</c>, which
/// would use <see cref="Environment.NewLine"/> and so format the same file differently on Windows than on
/// Linux — a repo shared across both would churn on every save. LF is the project's canonical ending
/// (<c>.gitattributes</c> pins <c>*.zs text eol=lf</c>), and <c>Formatter.FormatSource</c> normalizes CRLF
/// input before comparing, so LF output is what "already formatted" means on every platform.
/// </para>
/// </summary>
public static class PrettyPrinter
{
    private readonly record struct Ctx(FormattingOptions Options, CommentLayout Comments);

    // The line-width budget and the special-form layout keyword sets are configurable; their defaults live on
    // FormattingOptions (DefaultKeepFirstOperand / DefaultAlwaysBreakBody). `KeepFirstOperand` keeps a form's
    // first operand on the head line when it breaks (e.g. `(if <cond> \n then \n else)`); `AlwaysBreakBody`
    // stacks a form's body/clauses one-per-line even when it would fit. `define`/`let` are handled in
    // ForcesBreak instead because whether they have a body depends on their shape.

    public static string Format(
        IReadOnlyList<SExpr> forms,
        FormattingOptions options,
        CommentLayout comments
    )
    {
        var ctx = new Ctx(options, comments);
        var sb = new StringBuilder();

        for (var i = 0; i < forms.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            EmitChild(sb, forms[i], 0, ctx);
        }

        // Flush any comment that could not be anchored to a node, rather than dropping it.
        foreach (var comment in comments.Unemitted())
        {
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.Append('\n');
            sb.Append(comment.Text);
        }

        if (options.InsertFinalNewline && (sb.Length == 0 || sb[^1] != '\n'))
            sb.Append('\n');

        if (options.TrimTrailingWhitespace)
            return TrimTrailingWhitespace(sb.ToString());

        return sb.ToString();
    }

    // Emits a node that occupies its own line(s): an optional preserved blank line, its leading comments,
    // the node itself (indented), then any trailing comment. Assumes the cursor is at column 0.
    private static void EmitChild(StringBuilder sb, SExpr node, int indent, Ctx ctx)
    {
        if (ctx.Comments.BlankBefore(node))
            sb.Append('\n');

        var indentStr = Indent(ctx.Options, indent);
        Token? prevComment = null;
        foreach (var leading in ctx.Comments.LeadingOf(node))
        {
            if (
                prevComment != null
                && ctx.Comments.BlankBetween(prevComment.Span.Line, leading.Span.Line)
            )
                sb.Append('\n');
            sb.Append(indentStr);
            sb.Append(leading.Text);
            sb.Append('\n');
            ctx.Comments.MarkEmitted(leading);
            prevComment = leading;
        }

        // Preserve a blank line between the last leading comment and the node it annotates.
        if (prevComment != null && ctx.Comments.BlankBetween(prevComment.Span.Line, node.Span.Line))
            sb.Append('\n');

        sb.Append(indentStr);
        sb.Append(Render(node, indent, ctx));

        var trailing = ctx.Comments.TrailingOf(node);
        if (trailing != null)
        {
            sb.Append(' ', ctx.Options.TrailingCommentSpaces);
            sb.Append(trailing.Text);
            ctx.Comments.MarkEmitted(trailing);
        }
    }

    private static string Render(SExpr node, int indent, Ctx ctx)
    {
        if (ctx.Comments.CanFlatten(node) && !ForcesBreak(node, ctx.Options))
        {
            var flat = Flat(node);
            if (Fits(flat, indent, ctx.Options))
                return flat;
        }

        return node switch
        {
            SExpr.Atom atom => FormatAtomText(atom),
            SExpr.SList list => RenderList(list, indent, ctx),
            SExpr.BracketList list => RenderBracketList(list, indent, ctx),
            _ => node.ToString(),
        };
    }

    private static string RenderList(SExpr.SList list, int indent, Ctx ctx)
    {
        if (list.Items.Count == 0)
            return "()";

        if (TryQuotePrefix(list, out var prefix))
            return prefix + Render(list.Items[1], indent, ctx);

        var keyword = list.Items[0] is SExpr.Atom atom ? atom.Text : null;

        return keyword switch
        {
            "define" or "define-async" => RenderDefine(list, indent, ctx),
            "let" or "let*" or "use" or "use*" => RenderLet(list, keyword, indent, ctx),
            "import" => RenderImport(list, indent, ctx),
            _ when keyword != null
                    && (
                        ctx.Options.KeepFirstOperand.Contains(keyword)
                        || ctx.Options.AlwaysBreakBody.Contains(keyword)
                    ) => RenderForm(list, InlineCount(keyword, ctx.Options), indent, ctx),
            _ => RenderCall(list, indent, ctx),
        };
    }

    // A plain call/macro form that has to break. Keeps a "header" on the opening line — the operator, any
    // leading atom arguments, and one following parameter-list-shaped argument — then stacks the remaining
    // "body" arguments one per line. This matches how definition-like macros read, e.g.
    //   (theory-case addition ([x : Int] [y : Int])
    //     (inline-data 1 2 3)
    //     ...)
    // Items carrying comments are pushed to the body so their comments get their own line.
    private static string RenderCall(SExpr.SList list, int indent, Ctx ctx)
    {
        var budget = ctx.Options.MaxLineLength - indent * ctx.Options.IndentSize;
        var headLen = 1 + Flat(list.Items[0]).Length; // "(" + operator
        var headEnd = 1;

        while (
            headEnd < list.Items.Count
            && list.Items[headEnd] is SExpr.Atom
            && !HasComments(list.Items[headEnd], ctx)
        )
        {
            var len = Flat(list.Items[headEnd]).Length + 1;
            if (headLen + len > budget)
                break;
            headLen += len;
            headEnd++;
        }

        if (
            headEnd < list.Items.Count
            && IsParamList(list.Items[headEnd])
            && !HasComments(list.Items[headEnd], ctx)
        )
        {
            var rendered = Flat(list.Items[headEnd]);
            if (headLen + rendered.Length + 1 <= budget)
                headEnd++;
        }

        return RenderForm(list, headEnd, indent, ctx);
    }

    // A list that reads as a parameter/binding/literals list rather than a call. Brackets always qualify; a
    // parenthesised list qualifies when it is empty, all atoms (e.g. a `syntax-rules` literals list `(a b)`),
    // or all sub-lists (e.g. a parameter list `([x : Int] [y : Int])`) — anything mixing an atom head with
    // compound arguments reads as a call and is treated as body.
    private static bool IsParamList(SExpr node) =>
        node switch
        {
            SExpr.BracketList => true,
            SExpr.SList list => list.Items.All(i => i is SExpr.Atom)
                || list.Items.All(i => i is SExpr.SList or SExpr.BracketList),
            _ => false,
        };

    private static bool HasComments(SExpr node, Ctx ctx) =>
        HasLeading(node, ctx) || HasTrailing(node, ctx);

    private static int InlineCount(string? keyword, FormattingOptions options) =>
        keyword != null && options.KeepFirstOperand.Contains(keyword) ? 2 : 1;

    // True when a form must be laid out across multiple lines even though it would fit on one — i.e. it has
    // a body or clause list that should be stacked. Width-based forms (records, if, plain calls) return false
    // and are collapsed by Render whenever they fit.
    private static bool ForcesBreak(SExpr node, FormattingOptions options)
    {
        if (node is not SExpr.SList list || list.Items is not [SExpr.Atom keyword, ..])
            return false;

        return keyword.Text switch
        {
            "define" or "define-async" => list.Items.Count > 2 && list.Items[1] is SExpr.SList,
            "let" or "let*" => list.Items.Count > 2,
            "import" => list.Items.Count > 2, // 2+ modules always break; (import foo) stays flat
            var kw => options.AlwaysBreakBody.Contains(kw)
                && list.Items.Count > InlineCount(kw, options),
        };
    }

    // (head ...inline... \n  rest \n  rest). `inlineCount` items stay on the opening line; the remainder
    // are laid out one per line at indent+1.
    private static string RenderForm(SExpr.SList list, int inlineCount, int indent, Ctx ctx)
    {
        var sb = new StringBuilder();
        sb.Append('(');

        var head = Math.Min(inlineCount, list.Items.Count);
        for (var i = 0; i < head; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(Render(list.Items[i], indent, ctx));
        }

        for (var i = head; i < list.Items.Count; i++)
        {
            sb.Append('\n');
            EmitChild(sb, list.Items[i], indent + 1, ctx);
        }

        sb.Append(')');
        return sb.ToString();
    }

    // A merged import with two or more modules: keep the first module on the head line and stack the
    // remaining modules aligned directly under it, e.g.
    //   (import stdlib/list
    //           stdlib/map)
    // A single-module import stays flat (handled by Render's flat-first path). Modules are always bare
    // atoms — the only shape ImportMerger produces — so each renders trivially.
    private static string RenderImport(SExpr.SList list, int indent, Ctx ctx)
    {
        var keyword = Flat(list.Items[0]); // "import"
        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(keyword);
        sb.Append(' ');
        sb.Append(Render(list.Items[1], indent, ctx)); // first module on the head line

        var align = Indent(ctx.Options, indent) + new string(' ', 1 + keyword.Length + 1);
        for (var i = 2; i < list.Items.Count; i++)
        {
            sb.Append('\n');
            sb.Append(align);
            sb.Append(Render(list.Items[i], indent, ctx));
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string RenderDefine(SExpr.SList list, int indent, Ctx ctx)
    {
        // Value form, or malformed: keep the name on the head line, value/body below.
        if (list.Items.Count < 2 || list.Items[1] is not SExpr.SList)
            return RenderForm(list, 2, indent, ctx);

        var keyword = ((SExpr.Atom)list.Items[0]).Text; // "define" or "define-async"
        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(keyword);
        sb.Append(' ');
        sb.Append(Render(list.Items[1], indent, ctx)); // function signature, kept on the head line

        var bodyStart = 2;

        if (bodyStart < list.Items.Count && list.Items[bodyStart] is SExpr.Atom { Text: ":" })
        {
            sb.Append(" : ");
            bodyStart++;
            if (bodyStart < list.Items.Count)
            {
                sb.Append(Render(list.Items[bodyStart], indent, ctx));
                bodyStart++;
            }
        }

        if (
            bodyStart < list.Items.Count
            && list.Items[bodyStart] is SExpr.SList { Items: [SExpr.Atom { Text: "where" }, ..] }
        )
        {
            sb.Append(' ');
            sb.Append(Render(list.Items[bodyStart], indent, ctx));
            bodyStart++;
        }

        for (var i = bodyStart; i < list.Items.Count; i++)
        {
            sb.Append('\n');
            EmitChild(sb, list.Items[i], indent + 1, ctx);
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string RenderLet(SExpr.SList list, string keyword, int indent, Ctx ctx)
    {
        if (list.Items.Count < 2)
            return $"({keyword})";

        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(keyword);
        sb.Append(' ');
        sb.Append(RenderLetBindings(list.Items[1], keyword, indent, ctx)); // bindings on the head line

        for (var i = 2; i < list.Items.Count; i++)
        {
            sb.Append('\n');
            EmitChild(sb, list.Items[i], indent + 1, ctx);
        }

        sb.Append(')');
        return sb.ToString();
    }

    // Renders a let/let*/use/use* binding list. When the whole list fits on the head line it stays flat;
    // otherwise each binding goes on its own line, aligned under the first binding (Scheme convention), e.g.
    //   (let* ([x2 (* x x)]
    //          [ax2 (* a x2)])
    //     body)
    // The subsequent bindings align under the first binding's opening bracket — one column past the binding
    // list's own bracket, which itself sits after "(let* ".
    private static string RenderLetBindings(SExpr bindings, string keyword, int indent, Ctx ctx)
    {
        var (open, close, items) = bindings switch
        {
            SExpr.SList l => ('(', ')', (IReadOnlyList<SExpr>)l.Items),
            SExpr.BracketList b => ('[', ']', b.Items),
            _ => ('\0', '\0', null),
        };

        // Malformed bindings (not a list) or an empty list: defer to the generic renderer.
        if (items is null or { Count: 0 })
            return Render(bindings, indent, ctx);

        // Column of the first character after "(let* " — where the binding list's opening bracket sits.
        var headCol = indent * ctx.Options.IndentSize + 1 + keyword.Length + 1;
        if (ctx.Comments.CanFlatten(bindings))
        {
            var flat = Flat(bindings);
            if (headCol + flat.Length <= ctx.Options.MaxLineLength)
                return flat;
        }

        // Alignment prefix for every binding after the first: base indent, then past "(let* (" so the
        // bindings' brackets line up under the first binding's bracket.
        var align = Indent(ctx.Options, indent) + new string(' ', 1 + keyword.Length + 1 + 1);

        var sb = new StringBuilder();
        sb.Append(open);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            if (i == 0 && !HasLeading(item, ctx))
            {
                sb.Append(Render(item, indent, ctx)); // first binding stays on the "(let* (" head line
            }
            else
            {
                if (i > 0 && ctx.Comments.BlankBefore(item))
                    sb.Append('\n');
                foreach (var comment in ctx.Comments.LeadingOf(item))
                {
                    sb.Append('\n');
                    sb.Append(align);
                    sb.Append(comment.Text);
                    ctx.Comments.MarkEmitted(comment);
                }
                sb.Append('\n');
                sb.Append(align);
                sb.Append(Render(item, indent, ctx));
            }

            var trailing = ctx.Comments.TrailingOf(item);
            if (trailing != null)
            {
                sb.Append(' ', ctx.Options.TrailingCommentSpaces);
                sb.Append(trailing.Text);
                ctx.Comments.MarkEmitted(trailing);
            }
        }
        sb.Append(close);
        return sb.ToString();
    }

    private static string RenderBracketList(SExpr.BracketList list, int indent, Ctx ctx)
    {
        if (list.Items.Count == 0)
            return "[]";

        var sb = new StringBuilder();
        sb.Append('[');

        // Keep the first element on the opening-bracket line (clause/binding style), e.g.
        //   [(pattern ...)
        //     body]
        // unless it carries a leading comment, which needs its own line.
        var firstInline = !HasLeading(list.Items[0], ctx);
        var start = 0;
        if (firstInline)
        {
            sb.Append(Render(list.Items[0], indent, ctx));
            start = 1;
        }

        for (var i = start; i < list.Items.Count; i++)
        {
            sb.Append('\n');
            EmitChild(sb, list.Items[i], indent + 1, ctx);
        }

        // Attach the closing bracket to the last element's line, unless that line already ends in a trailing
        // comment (which would otherwise swallow the bracket).
        if (HasTrailing(list.Items[^1], ctx))
        {
            sb.Append('\n');
            sb.Append(Indent(ctx.Options, indent));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static bool HasLeading(SExpr node, Ctx ctx) => ctx.Comments.LeadingOf(node).Count > 0;

    private static bool HasTrailing(SExpr node, Ctx ctx) => ctx.Comments.TrailingOf(node) != null;

    // Single-line rendering of a node, used both as the candidate when deciding whether a form fits and as
    // the validation oracle for the re-lex safety guard. Never contains newlines or comments.
    internal static string Flat(SExpr node)
    {
        switch (node)
        {
            case SExpr.Atom atom:
                return FormatAtomText(atom);
            case SExpr.SList list:
                if (list.Items.Count == 0)
                    return "()";
                if (TryQuotePrefix(list, out var prefix))
                    return prefix + Flat(list.Items[1]);
                return "(" + string.Join(" ", list.Items.Select(Flat)) + ")";
            case SExpr.BracketList bracket:
                return "[" + string.Join(" ", bracket.Items.Select(Flat)) + "]";
            default:
                return node.ToString();
        }
    }

    private static bool TryQuotePrefix(SExpr.SList list, out string prefix)
    {
        prefix = "";
        if (list.Items.Count != 2 || list.Items[0] is not SExpr.Atom atom)
            return false;

        prefix = atom.Text switch
        {
            "quote" => "'",
            "quasiquote" => "`",
            "unquote" => ",",
            "unquote-splicing" => ",@",
            _ => "",
        };
        return prefix.Length > 0;
    }

    private static bool Fits(string flat, int indent, FormattingOptions options) =>
        !flat.Contains('\n') && indent * options.IndentSize + flat.Length <= options.MaxLineLength;

    // Re-synthesizes an atom's surface syntax. String literals are stored by the lexer already unquoted and
    // unescaped, so they must be re-quoted/re-escaped or the emitted text would re-lex as bare symbols (or
    // break tokenization entirely).
    private static string FormatAtomText(SExpr.Atom atom) =>
        atom.Kind == TokenKind.StringLit ? QuoteString(atom.Text) : atom.Text;

    private static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
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
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Indent(FormattingOptions options, int level) =>
        options.UseTabs ? new string('\t', level) : new string(' ', level * options.IndentSize);

    private static string TrimTrailingWhitespace(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\t');
        return string.Join('\n', lines);
    }
}

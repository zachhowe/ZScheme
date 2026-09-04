using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

/// <summary>
/// Side-table that maps S-expression nodes (by <b>reference identity</b> — <see cref="SExpr"/> records
/// use value equality, so structurally-identical sub-forms would otherwise collide) to the comments
/// that should print around them, plus blank-line and flat-suppression information.
/// Built by <see cref="CommentAttacher"/> and consumed by <see cref="PrettyPrinter"/>.
/// </summary>
public sealed class CommentLayout
{
    private readonly Dictionary<SExpr, List<Token>> _leading;
    private readonly Dictionary<SExpr, Token> _trailing;
    private readonly HashSet<SExpr> _blankBefore;
    private readonly HashSet<SExpr> _blocksFlat;
    private readonly HashSet<Token> _emitted = [];
    private readonly IReadOnlyList<Token> _all;
    private readonly HashSet<int> _occupied;

    internal CommentLayout(
        Dictionary<SExpr, List<Token>> leading,
        Dictionary<SExpr, Token> trailing,
        HashSet<SExpr> blankBefore,
        HashSet<SExpr> blocksFlat,
        IReadOnlyList<Token> all,
        HashSet<int> occupied
    )
    {
        _leading = leading;
        _trailing = trailing;
        _blankBefore = blankBefore;
        _blocksFlat = blocksFlat;
        _all = all;
        _occupied = occupied;
    }

    public static CommentLayout Empty { get; } =
        new(
            new(ReferenceEqualityComparer.Instance),
            new(ReferenceEqualityComparer.Instance),
            new(ReferenceEqualityComparer.Instance),
            new(ReferenceEqualityComparer.Instance),
            [],
            []
        );

    /// <summary>A node may render on a single line only if nothing in its subtree needs a comment on
    /// its own line (a leading comment, or a trailing comment on a proper descendant).</summary>
    public bool CanFlatten(SExpr node) => !_blocksFlat.Contains(node);

    public bool BlankBefore(SExpr node) => _blankBefore.Contains(node);

    /// <summary>True when a genuinely blank (unoccupied) source line sat directly above
    /// <paramref name="lowerLine"/> and strictly below <paramref name="upperLine"/>. Mirrors the blank test
    /// in <see cref="CommentAttacher"/> so re-parsing the emitted single blank reproduces it (idempotent),
    /// and runs of blank lines collapse to one.</summary>
    public bool BlankBetween(int upperLine, int lowerLine) =>
        lowerLine - upperLine > 1 && !_occupied.Contains(lowerLine - 1);

    public IReadOnlyList<Token> LeadingOf(SExpr node) =>
        _leading.TryGetValue(node, out var list) ? list : [];

    public Token? TrailingOf(SExpr node) => _trailing.TryGetValue(node, out var t) ? t : null;

    public void MarkEmitted(Token comment) => _emitted.Add(comment);

    /// <summary>Comments that were never placed against a node (hard-to-anchor / dangling). The printer
    /// flushes these at end-of-file so a comment is never silently dropped.</summary>
    public IEnumerable<Token> Unemitted() =>
        _all.Where(c => !_emitted.Contains(c)).OrderBy(c => c.Span.Line).ThenBy(c => c.Span.Column);
}

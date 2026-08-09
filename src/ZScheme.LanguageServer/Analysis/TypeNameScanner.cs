using ZScheme.Compiler.Syntax;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>A named type written in a type position. <paramref name="Name" /> is the token
///     text with a trailing <c>?</c> stripped (the nullable suffix is not part of the name);
///     <paramref name="Arity" /> is the type-argument count, which is what
///     <c>TypeNameCanonicalizer.Canonical</c> needs to pick between <c>Foo</c> and
///     <c>Foo`n</c>.</summary>
internal readonly record struct TypeNameOccurrence(Token Token, string Name, int Arity);

/// <summary>The type half of an <c>import-clr</c> member binding's <c>Type/Member</c> path.
///     <paramref name="Token" /> is the whole path atom, so the type name still starts at its
///     column. Kept apart from <see cref="TypeNameOccurrence" /> because this is not a type
///     <em>position</em>: it never becomes a <see cref="ZScheme.Compiler.Types.ZType" />, it
///     carries no arity, and the primitive-name exclusion that applies to annotations does not
///     apply to it.</summary>
internal readonly record struct ImportMemberOccurrence(Token Token, string TypeName);

/// <summary>Every part of a single pass: the type names written in the file, the bare namespace
///     atoms of its own <c>(import-clr Ns …)</c> forms, and those forms' member paths.</summary>
internal sealed record TypeNameScan(
    IReadOnlyList<TypeNameOccurrence> TypeNames,
    IReadOnlyList<Token> ClrNamespaces,
    IReadOnlyList<ImportMemberOccurrence> ImportMembers
);

/// <summary>
///     Recovers every type-position name — with its source token — from the raw token stream.
///     <para>
///         Type annotations do not survive parsing: <c>AstBuilder.ParseTypeExpr</c> turns them
///         into <see cref="ZScheme.Compiler.Types.ZType" />, which carries no span. So anything
///         that needs to point at a written type name has to work at the token level, the same
///         way <see cref="TypePosition" /> does for completion.
///     </para>
///     <para>
///         The walk mirrors the real grammar rather than guessing: a type expression is parsed
///         exactly as <c>ParseTypeExpr</c> parses it, and the forms that introduce type positions
///         outside a <c>:</c> annotation (<c>new</c>, <c>typeof</c>, the base/interface runs of
///         <c>define-class</c>/<c>define-interface</c>/<c>object</c>, and <c>with-handlers</c>
///         clauses) are recognised by head symbol. Keeping the two in step matters: a name this
///         scanner reports but the compiler never treats as a type would be a false positive for
///         every consumer.
///     </para>
///     Bracket-imbalance tolerant, so it keeps working mid-edit.
/// </summary>
internal static class TypeNameScanner
{
    public static TypeNameScan Scan(IReadOnlyList<Token> tokens)
    {
        var walker = new Walker(tokens);
        walker.Run();
        return new TypeNameScan(walker.TypeNames, walker.ClrNamespaces, walker.ImportMembers);
    }

    private sealed class Walker
    {
        /// <summary>Words that follow a <c>:</c> without being a type: the <c>import-clr</c>
        ///     member kinds and <c>:from</c>/<c>:where</c>, all of which the lexer splits into
        ///     a <see cref="TokenKind.Colon" /> plus a bare symbol (see
        ///     <c>AstBuilder.BuildImportClr</c>'s <c>case ":"</c> arm).</summary>
        private static readonly HashSet<string> NonTypeKeywords = new(StringComparer.Ordinal)
        {
            "instance",
            "instance-property",
            "instance-property-set",
            "instance-property-init",
            "instance-indexer",
            "instance-indexer-set",
            "from",
            "where",
        };

        private readonly Token[] _tokens;

        public Walker(IReadOnlyList<Token> tokens)
        {
            _tokens = [.. tokens.Where(t => t.Kind is not (TokenKind.Comment or TokenKind.Eof))];
        }

        public List<TypeNameOccurrence> TypeNames { get; } = [];
        public List<Token> ClrNamespaces { get; } = [];
        public List<ImportMemberOccurrence> ImportMembers { get; } = [];

        public void Run()
        {
            var i = 0;
            while (i < _tokens.Length)
            {
                var next = ScanExpr(i);
                i = next > i ? next : i + 1;
            }
        }

        /// <summary>Walks one expression, returning the index just past it.</summary>
        private int ScanExpr(int i)
        {
            if (i >= _tokens.Length)
                return i;

            switch (_tokens[i].Kind)
            {
                case TokenKind.Quote
                or TokenKind.Quasiquote
                or TokenKind.Unquote
                or TokenKind.UnquoteSplicing:
                    // Quoted data is inert: a `:` inside it annotates nothing.
                    return SkipDatum(i + 1);
                case TokenKind.LParen or TokenKind.LBracket:
                    break;
                default:
                    return i + 1;
            }

            var (items, next) = Children(i);
            var head =
                items.Count > 0 && _tokens[items[0]].Kind == TokenKind.Symbol
                    ? _tokens[items[0]].Text
                    : null;

            int start;
            switch (head)
            {
                // A macro template's type positions belong to whichever file expands it, and
                // that file need not import the namespace this one does.
                case "define-syntax":
                    return next;
                case "import-clr":
                    ScanImportClr(items);
                    return next;
                case "new":
                    // (new Type arg …) — item 1 is the type, the rest are constructor arguments.
                    if (items.Count > 1)
                        ScanTypeExpr(items[1]);
                    for (var c = 2; c < items.Count; c++)
                        ScanExpr(items[c]);
                    return next;
                case "typeof":
                    if (items.Count > 1)
                        ScanTypeExpr(items[1]);
                    return next;
                case "define-class" or "define-interface":
                    start = ScanTypeDeclHeader(items);
                    break;
                case "object":
                    start = ScanObjectHeader(items);
                    break;
                case "with-handlers":
                    ScanHandlerTypes(items);
                    start = 1;
                    break;
                default:
                    start = 0;
                    break;
            }

            for (var c = start; c < items.Count; c++)
            {
                if (_tokens[items[c]].Kind != TokenKind.Colon)
                {
                    ScanExpr(items[c]);
                    continue;
                }

                if (c + 1 >= items.Count)
                    continue;

                // `: where (^k notnull)` / `: from "Asm"`: consume the keyword and let the next
                // iteration walk its operand as an ordinary expression.
                if (
                    _tokens[items[c + 1]].Kind == TokenKind.Symbol
                    && NonTypeKeywords.Contains(_tokens[items[c + 1]].Text)
                )
                {
                    c++;
                    continue;
                }

                ScanTypeExpr(items[c + 1]);
                c++;
            }

            return next;
        }

        /// <summary>Bare atoms of an <c>import-clr</c> form are namespace hints; bracketed
        ///     entries are member bindings, whose <c>Type/Member</c> path sits at item 1. That
        ///     path is not a type position — only the <c>: (…)</c> signature is — so it is
        ///     recorded separately rather than through <see cref="Record" />.</summary>
        private void ScanImportClr(List<int> items)
        {
            for (var c = 1; c < items.Count; c++)
                if (_tokens[items[c]].Kind == TokenKind.Symbol)
                {
                    ClrNamespaces.Add(_tokens[items[c]]);
                }
                else
                {
                    RecordImportMember(items[c]);
                    ScanExpr(items[c]);
                }
        }

        /// <summary>
        ///     Records the type half of <c>[alias Ns.Type/Member …]</c>.
        ///     <c>AstBuilder.BuildImportClr</c> takes the alias at item 0 and the path at item 1,
        ///     so nothing else in the bracket can be one.
        ///     <para>
        ///         The split is the one every consumer makes — the last <c>/</c>, or the last
        ///         <c>.</c> when there is none (<c>ClrInterop.DetectOutParams</c>,
        ///         <c>IrLowering.LowerImportClr</c>, <c>TypeInferer.ValidateClrImportAnnotation</c>).
        ///         It needs no knowledge of the <c>:instance…</c> kind: a member name never
        ///         contains a <c>.</c> and a type half never contains a <c>/</c>, so the
        ///         kind-specific rules agree.
        ///     </para>
        /// </summary>
        private void RecordImportMember(int i)
        {
            if (!IsOpener(i))
                return;
            var (bracket, _) = Children(i);
            if (bracket.Count < 2 || _tokens[bracket[1]].Kind != TokenKind.Symbol)
                return;

            var token = _tokens[bracket[1]];
            var path = token.Text;
            var slash = path.LastIndexOf('/');
            var split = slash >= 0 ? slash : path.LastIndexOf('.');
            if (split <= 0 || split == path.Length - 1)
                return;

            var typeName = path[..split];
            // A closed generic carries its own arguments, a second '/' is not a Type/Member path,
            // and `^a`/`#:flag` atoms are the enclosing form's own syntax.
            if (typeName.Contains('<') || typeName.Contains('/') || typeName[0] is '^' or '#')
                return;

            ImportMembers.Add(new ImportMemberOccurrence(token, typeName));
        }

        /// <summary>
        ///     Consumes <c>(define-class [#:open] Name|(Name ^a) [: Base IFoo IBar])</c> and the
        ///     <c>define-interface</c> equivalent, recording the base/interface run. Mirrors
        ///     <c>AstBuilder.BuildClass</c>/<c>BuildInterface</c>, including their stop condition:
        ///     the run ends at the first item that is not an upper-case bare name. Returns the
        ///     index of the first member.
        /// </summary>
        private int ScanTypeDeclHeader(List<int> items)
        {
            var c = 1;
            if (c < items.Count && IsSymbolStartingWith(items[c], '#'))
                c++;
            // The declared name is a definition, not a use — never a shortening candidate.
            if (c < items.Count)
                c++;
            if (c >= items.Count || _tokens[items[c]].Kind != TokenKind.Colon)
                return c;

            c++;
            while (c < items.Count && IsUpperCaseName(items[c]))
            {
                Record(_tokens[items[c]], 0);
                c++;
            }

            return c;
        }

        /// <summary>
        ///     Consumes an <c>object</c> expression's interface header — <c>(object : Base IFoo
        ///     (IBar IBaz) …)</c>, <c>(object IFoo …)</c>, or <c>(object (IFoo IBar) …)</c> —
        ///     mirroring <c>AstBuilder.BuildObjectExpr</c>. Returns the index of the first member.
        /// </summary>
        private int ScanObjectHeader(List<int> items)
        {
            var c = 1;
            if (c >= items.Count)
                return c;

            if (_tokens[items[c]].Kind != TokenKind.Colon)
            {
                if (_tokens[items[c]].Kind == TokenKind.Symbol)
                    Record(_tokens[items[c]], 0);
                else if (!RecordInterfaceGroup(items[c]))
                    return c;
                return c + 1;
            }

            c++;
            while (c < items.Count && IsUpperCaseName(items[c]))
            {
                Record(_tokens[items[c]], 0);
                c++;
            }

            if (c < items.Count && RecordInterfaceGroup(items[c]))
                c++;
            return c;
        }

        /// <summary>The exception type of each <c>with-handlers</c> clause —
        ///     <c>([ExceptionType var] body)</c>. The bodies are left to the generic child loop,
        ///     so nothing is walked twice.</summary>
        private void ScanHandlerTypes(List<int> items)
        {
            for (var c = 1; c < items.Count - 1; c++)
            {
                if (!IsOpener(items[c]))
                    continue;
                var (clause, _) = Children(items[c]);
                if (clause.Count != 2 || !IsOpener(clause[0]))
                    continue;
                var (binding, _) = Children(clause[0]);
                if (binding.Count == 2 && _tokens[binding[0]].Kind == TokenKind.Symbol)
                    Record(_tokens[binding[0]], 0);
            }
        }

        /// <summary>Walks one type expression, mirroring <c>AstBuilder.ParseTypeExpr</c>.</summary>
        private void ScanTypeExpr(int i)
        {
            if (i >= _tokens.Length)
                return;

            if (_tokens[i].Kind == TokenKind.Symbol)
            {
                Record(_tokens[i], 0);
                return;
            }

            if (!IsOpener(i))
                return;

            var (items, _) = Children(i);
            if (items.Count == 0)
                return;

            // (delegate System.Func<int,int>): a C#-style closed generic, split across tokens and
            // deliberately left alone by TypeNameCanonicalizer.
            if (IsSymbol(items[0], "delegate"))
                return;

            if (items.Count(s => IsSymbol(s, "->")) == 1)
            {
                foreach (var item in items)
                    if (!IsSymbol(item, "->"))
                        ScanTypeExpr(item);
                return;
            }

            if (IsInfixTuple(items))
            {
                for (var c = 0; c < items.Count; c += 2)
                    ScanTypeExpr(items[c]);
                return;
            }

            if (_tokens[items[0]].Kind == TokenKind.Symbol)
                Record(_tokens[items[0]], items.Count - 1);
            for (var c = 1; c < items.Count; c++)
                ScanTypeExpr(items[c]);
        }

        private bool IsInfixTuple(List<int> items)
        {
            if (items.Count < 3 || items.Count % 2 == 0)
                return false;
            for (var c = 1; c < items.Count; c += 2)
                if (!IsSymbol(items[c], "*"))
                    return false;
            return true;
        }

        private void Record(Token token, int arity)
        {
            var name = token.Text;
            // `Foo?` parses as ZNullableType(Foo); the name to resolve is the part before the '?'.
            if (name.Length > 1 && name[^1] == '?' && name[0] != '^')
                name = name[..^1];

            if (name.Length == 0)
                return;
            // Type variable, or a `#:flag` that the enclosing form's own parse would have eaten.
            if (name[0] is '^' or '#')
                return;
            // A closed generic carries its arguments in the name, and a `/` marks a member or
            // module path — neither is a plain named type.
            if (name.Contains('<') || name.Contains('/'))
                return;
            if (name is "->" or "*" or "...")
                return;

            TypeNames.Add(new TypeNameOccurrence(token, name, arity));
        }

        /// <summary>Records <c>(IFoo IBar)</c> as an interface group, but only when every item
        ///     is an upper-case bare name — otherwise it is a method definition
        ///     (<c>AstBuilder.BuildObjectExpr</c> makes the same distinction).</summary>
        private bool RecordInterfaceGroup(int i)
        {
            if (!IsOpener(i))
                return false;
            var (group, _) = Children(i);
            if (group.Count == 0 || !group.All(IsUpperCaseName))
                return false;
            foreach (var item in group)
                Record(_tokens[item], 0);
            return true;
        }

        /// <summary>The start index of each direct child of the bracket opening at
        ///     <paramref name="i" />, plus the index just past its closer.</summary>
        private (List<int> Items, int Next) Children(int i)
        {
            var items = new List<int>();
            var j = i + 1;
            while (
                j < _tokens.Length
                && _tokens[j].Kind is not (TokenKind.RParen or TokenKind.RBracket)
            )
            {
                items.Add(j);
                var next = SkipDatum(j);
                j = next > j ? next : j + 1;
            }

            return (items, j < _tokens.Length ? j + 1 : j);
        }

        private int SkipDatum(int i)
        {
            if (i >= _tokens.Length)
                return i;
            return _tokens[i].Kind switch
            {
                TokenKind.LParen or TokenKind.LBracket => SkipBracket(i),
                TokenKind.Quote
                or TokenKind.Quasiquote
                or TokenKind.Unquote
                or TokenKind.UnquoteSplicing => SkipDatum(i + 1),
                _ => i + 1,
            };
        }

        /// <summary>An unclosed bracket runs to the end of the stream, matching
        ///     <see cref="LexicalStructure.BuildTree" />'s mid-edit tolerance.</summary>
        private int SkipBracket(int i)
        {
            var depth = 0;
            for (; i < _tokens.Length; i++)
                switch (_tokens[i].Kind)
                {
                    case TokenKind.LParen or TokenKind.LBracket:
                        depth++;
                        break;
                    case TokenKind.RParen or TokenKind.RBracket:
                        if (--depth == 0)
                            return i + 1;
                        break;
                }

            return i;
        }

        private bool IsOpener(int i)
        {
            return i < _tokens.Length && _tokens[i].Kind is TokenKind.LParen or TokenKind.LBracket;
        }

        private bool IsSymbol(int i, string text)
        {
            return _tokens[i].Kind == TokenKind.Symbol
                && string.Equals(_tokens[i].Text, text, StringComparison.Ordinal);
        }

        private bool IsSymbolStartingWith(int i, char c)
        {
            return _tokens[i].Kind == TokenKind.Symbol
                && _tokens[i].Text.Length > 0
                && _tokens[i].Text[0] == c;
        }

        private bool IsUpperCaseName(int i)
        {
            return _tokens[i].Kind == TokenKind.Symbol
                && _tokens[i].Text.Length > 0
                && char.IsUpper(_tokens[i].Text[0]);
        }
    }
}

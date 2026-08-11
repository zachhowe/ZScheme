using System.Globalization;
using Serilog;
using ZScheme.Compiler.Builtins;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ast;

public sealed class AstBuilder(DiagnosticBag diagnostics)
{
    private static readonly ILogger _log = Log.ForContext<AstBuilder>();

    private int _freshCounter;

    private string FreshName(string prefix)
    {
        return $"${prefix}_{_freshCounter++}";
    }

    public AstNode.Program BuildProgram(IReadOnlyList<SExpr> exprs)
    {
        var forms = new List<AstNode>();
        var pendingAttrs = new List<AttributeDecl>();

        for (var i = 0; i < exprs.Count; i++)
        {
            if (IsAttributeForm(exprs[i]))
            {
                pendingAttrs.Add(ParseAttributeDecl((SExpr.SList)exprs[i]));
                continue;
            }

            var node = Build(exprs[i]);

            // Flatten spliced nodes (e.g., multi-module import expands to multiple Import nodes)
            if (node is AstNode.Program splice)
            {
                foreach (var child in splice.TopLevelForms)
                    forms.Add(child);
                continue;
            }

            node = ApplyPendingAttributes(node, pendingAttrs);

            // If we got a ModuleDecl with an empty body, absorb remaining forms
            if (node is AstNode.ModuleDecl { Body.Count: 0 } mod)
            {
                var body = BuildRemainingForms(exprs, i + 1, pendingAttrs);
                var nestedModule = body.OfType<AstNode.ModuleDecl>().FirstOrDefault();
                if (nestedModule is not null)
                    diagnostics.Error(
                        "Ambiguous module declaration; use explicit module bodies for multiple modules: (module name ...)",
                        nestedModule.Span
                    );
                node = mod with { Body = body };
                forms.Add(node);
                break;
            }

            forms.Add(node);
        }

        if (pendingAttrs.Count > 0)
            diagnostics.Error("Attribute(s) with no target declaration", pendingAttrs[0].Span);

        var span = exprs.Count > 0 ? exprs[0].Span : SourceSpan.None;
        return new AstNode.Program(forms, span);
    }

    private AstNode ApplyPendingAttributes(AstNode node, List<AttributeDecl> pendingAttrs)
    {
        if (pendingAttrs.Count == 0)
            return node;

        var attrs = pendingAttrs.ToList();
        pendingAttrs.Clear();
        return node switch
        {
            AstNode.Define d => d with { Attributes = attrs },
            AstNode.DefineAsync d => d with { Attributes = attrs },
            AstNode.DefineValue d => d with { Attributes = attrs },
            AstNode.RecordDecl r => r with { Attributes = attrs },
            AstNode.UnionDecl u => u with { Attributes = attrs },
            AstNode.ClassDecl c => c with { Attributes = attrs },
            AstNode.InterfaceDecl iface => iface with { Attributes = attrs },
            _ => ReportBadAttributeTarget(node, attrs),
        };
    }

    private List<AstNode> BuildRemainingForms(
        IReadOnlyList<SExpr> exprs,
        int startIndex,
        List<AttributeDecl> pendingAttrs
    )
    {
        var body = new List<AstNode>();

        for (var j = startIndex; j < exprs.Count; j++)
        {
            if (IsAttributeForm(exprs[j]))
            {
                pendingAttrs.Add(ParseAttributeDecl((SExpr.SList)exprs[j]));
                continue;
            }

            var bodyNode = Build(exprs[j]);

            // Flatten spliced nodes (e.g., multi-module import)
            if (bodyNode is AstNode.Program splice)
            {
                body.AddRange(splice.TopLevelForms);
                continue;
            }

            bodyNode = ApplyPendingAttributes(bodyNode, pendingAttrs);
            body.Add(bodyNode);
        }

        return body;
    }

    private AstNode ReportBadAttributeTarget(AstNode node, List<AttributeDecl> attrs)
    {
        diagnostics.Error(
            "Attributes can only be applied to define, define-record, define-union, define-class, or define-interface declarations",
            attrs[0].Span
        );
        return node;
    }

    private static bool IsAttributeForm(SExpr expr)
    {
        return expr is SExpr.SList list
            && list.Items.Count >= 2
            && list.Items[0] is SExpr.Atom { Text: "@" };
    }

    private AttributeDecl ParseAttributeDecl(SExpr.SList list)
    {
        // (@ Name positional... [NamedKey value] ...)
        var name = ((SExpr.Atom)list.Items[1]).Text;
        var positionalArgs = new List<object>();
        var namedArgs = new List<(string Name, object Value)>();

        for (var i = 2; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (item is SExpr.BracketList bracket && bracket.Items.Count == 2)
            {
                var key = ((SExpr.Atom)bracket.Items[0]).Text;
                var value = ParseAttributeArgValue(bracket.Items[1]);
                namedArgs.Add((key, value));
            }
            else if (item is SExpr.Atom atom)
            {
                positionalArgs.Add(ParseAttributeArgValueFromAtom(atom));
            }
            else
            {
                diagnostics.Error("Invalid attribute argument", item.Span);
            }
        }

        return new AttributeDecl(name, positionalArgs, namedArgs, list.Span);
    }

    private static object ParseAttributeArgValue(SExpr expr)
    {
        return expr switch
        {
            SExpr.Atom atom => ParseAttributeArgValueFromAtom(atom),
            _ => expr.ToString() ?? "",
        };
    }

    private static object ParseAttributeArgValueFromAtom(SExpr.Atom atom)
    {
        return atom.Kind switch
        {
            TokenKind.StringLit => atom.Text,
            TokenKind.IntLit => int.Parse(atom.Text),
            TokenKind.FloatLit => float.Parse(atom.Text, CultureInfo.InvariantCulture),
            TokenKind.BoolLit => atom.Text == "#t",
            _ => new SymbolRef(atom.Text),
        };
    }

    public AstNode Build(SExpr expr)
    {
        return expr switch
        {
            SExpr.Atom atom => BuildAtom(atom),
            SExpr.SList list => BuildList(list),
            SExpr.BracketList bracket => BuildBracketExpr(bracket),
            _ => throw new InvalidOperationException($"Unknown SExpr type: {expr.GetType()}"),
        };
    }

    private AstNode BuildAtom(SExpr.Atom atom)
    {
        // super/MethodName as a bare reference (e.g., passed as a value)
        if (atom.Text.StartsWith("super/"))
            return new AstNode.SuperMethodCall(atom.Text["super/".Length..], [], atom.Span);

        return atom.Kind switch
        {
            TokenKind.IntLit => new AstNode.IntLit(int.Parse(atom.Text), atom.Span),
            TokenKind.FloatLit => new AstNode.FloatLit(ParseFloat(atom.Text), atom.Span),
            TokenKind.BoolLit => new AstNode.BoolLit(atom.Text == "#t", atom.Span),
            TokenKind.NullLit => new AstNode.NullLit(atom.Span),
            TokenKind.StringLit => new AstNode.StringLit(atom.Text, atom.Span),
            TokenKind.Symbol => new AstNode.Name(atom.Text, atom.Span),
            _ => new AstNode.Name(atom.Text, atom.Span),
        };
    }

    private static float ParseFloat(string text)
    {
        var clean = text.TrimEnd('f', 'F');
        return float.Parse(clean, CultureInfo.InvariantCulture);
    }

    private AstNode BuildList(SExpr.SList list)
    {
        if (list.Items.Count == 0)
            return new AstNode.UnitLit(list.Span);

        // Check for special forms
        if (list.Items[0] is SExpr.Atom head)
            switch (head.Text)
            {
                case "define":
                    return BuildDefine(list);
                case "let":
                    return BuildLet(list);
                case "let*":
                    return BuildLetStar(list);
                case "letrec":
                    return BuildLetrec(list);
                case "use":
                    return BuildUse(list);
                case "use*":
                    return BuildUseStar(list);
                case "if":
                    return BuildIf(list);
                case "lambda":
                    return BuildLambda(list);
                case "match":
                    return BuildMatch(list);
                case "define-record":
                    return BuildRecord(list);
                case "define-struct":
                    return BuildStruct(list);
                case "define-union":
                    return BuildUnion(list);
                case "partial":
                    return BuildPartial(list);
                case "import-clr":
                    return BuildImportClr(list);
                case "define-type-alias":
                    return BuildTypeAliasDecl(list);
                case "namespace":
                    return BuildNamespace(list);
                case "module":
                    return BuildModule(list);
                case "import":
                    return BuildImport(list);
                case "export":
                    return BuildExport(list);
                case "object":
                    return BuildObjectExpr(list);
                case "begin":
                    return BuildBegin(list);
                case "new":
                    return BuildNew(list);
                case "typeof":
                    return BuildTypeOf(list);
                case "raise":
                    return BuildRaise(list);
                case "define-async":
                    return BuildDefineAsync(list);
                case "await":
                    return BuildAwait(list);
                case "define-class":
                    return BuildClass(list);
                case "define-interface":
                    return BuildInterface(list);
                case "with-handlers":
                    return BuildWithHandlers(list);
                case "with":
                    return BuildWith(list);
                case "set!":
                    return BuildSetField(list);
                case "values":
                    return BuildTupleNew(list);
                case "quote":
                    return BuildQuote(list);
            }

        // super/MethodName call: (super/Speak arg1 arg2 ...)
        if (list.Items[0] is SExpr.Atom superAtom && superAtom.Text.StartsWith("super/"))
        {
            var methodName = superAtom.Text["super/".Length..];
            var args = new List<AstNode>();
            for (var i = 1; i < list.Items.Count; i++)
                args.Add(Build(list.Items[i]));
            return new AstNode.SuperMethodCall(methodName, args, list.Span);
        }

        // Function application
        return BuildApply(list);
    }

    /// <summary>
    ///     Builds a <c>(quote x)</c> form (the desugaring of <c>'x</c>). In this first pass only
    ///     quoted symbols and quoted self-evaluating literals are supported: a quoted symbol
    ///     becomes an <see cref="AstNode.SymbolLit" />; a quoted int/float/bool/string/null becomes
    ///     that literal. Quoted lists (<c>'(a b c)</c>) are reported as not-yet-supported.
    /// </summary>
    private AstNode BuildQuote(SExpr.SList list)
    {
        if (list.Items.Count != 2)
        {
            diagnostics.Error(
                "'quote' requires exactly one datum, e.g. (quote x) or 'x",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        switch (list.Items[1])
        {
            // Use list.Span (the whole '<datum> form) rather than the inner atom's span, so
            // span-based text slicing (e.g. the REPL's) recovers the leading quote.
            case SExpr.Atom atom:
                return atom.Kind switch
                {
                    TokenKind.IntLit => new AstNode.IntLit(int.Parse(atom.Text), list.Span),
                    TokenKind.FloatLit => new AstNode.FloatLit(ParseFloat(atom.Text), list.Span),
                    TokenKind.BoolLit => new AstNode.BoolLit(atom.Text == "#t", list.Span),
                    TokenKind.StringLit => new AstNode.StringLit(atom.Text, list.Span),
                    TokenKind.NullLit => new AstNode.NullLit(list.Span),
                    // Symbols and any other bare atom (e.g. punctuation) quote to a symbol value.
                    _ => new AstNode.SymbolLit(atom.Text, list.Span),
                };
            default:
                diagnostics.Error(
                    "Quoted lists are not yet supported; only quoted symbols and literals are allowed",
                    list.Items[1].Span
                );
                return new AstNode.UnitLit(list.Span);
        }
    }

    private AstNode BuildBracketExpr(SExpr.BracketList bracket)
    {
        // Brackets in expression position are an error
        diagnostics.Error("Unexpected bracket expression in expression position", bracket.Span);
        return new AstNode.UnitLit(bracket.Span);
    }

    /// <summary>
    ///     Strips a leading <c>#:recursive</c> marker from a <c>define</c>/<c>define-async</c>
    ///     form, returning whether it was present and the form with the marker removed (so the
    ///     rest of the builder sees the ordinary shape). The marker asserts that the
    ///     definition's self-recursion is intended, silencing
    ///     <see cref="DiagnosticCodes.NonLoopedSelfRecursion" />; it carries no other meaning
    ///     and never reaches the IR. Lexed as one Symbol token, like <c>#:open</c>.
    /// </summary>
    private (bool Present, SExpr.SList Form) StripRecursiveMarker(SExpr.SList list, string formName)
    {
        if (list.Items.Count < 2 || list.Items[1] is not SExpr.Atom { Text: "#:recursive" } marker)
            return (false, list);

        // A value binding has no recursion to speak of — `(define #:recursive name expr)`.
        if (list.Items.Count > 2 && list.Items[2] is SExpr.Atom)
        {
            diagnostics.Error(
                $"'#:recursive' applies to a function definition, not a value binding: "
                    + $"({formName} #:recursive (name ...) body)",
                marker.Span
            );
            return (false, list with { Items = [list.Items[0], .. list.Items.Skip(2)] });
        }

        return (true, list with { Items = [list.Items[0], .. list.Items.Skip(2)] });
    }

    private AstNode BuildDefine(SExpr.SList list)
    {
        // (define (name [params...]) : ReturnType body)
        // (define name expr)
        bool allowsUnloopedRecursion;
        (allowsUnloopedRecursion, list) = StripRecursiveMarker(list, "define");

        if (list.Items.Count < 3)
        {
            diagnostics.Error("'define' requires at least a name and body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        // (define name expr)
        if (list.Items[1] is SExpr.Atom nameAtom)
        {
            var value = Build(list.Items[2]);
            return new AstNode.DefineValue(
                nameAtom.Text,
                value,
                list.Span,
                NameSpan: nameAtom.Span
            );
        }

        // (define (name [params...]) : ReturnType body)
        if (list.Items[1] is SExpr.SList sig)
        {
            if (sig.Items.Count == 0)
            {
                diagnostics.Error("Function signature must have a name", list.Span);
                return new AstNode.UnitLit(list.Span);
            }

            var fnNameAtom = (SExpr.Atom)sig.Items[0];
            var fnName = fnNameAtom.Text;
            var parms = new List<Param>();

            for (var i = 1; i < sig.Items.Count; i++)
                parms.Add(ParseParam(sig.Items[i]));
            ValidateVariadicParams(parms, list.Span);

            // Look for return type annotation: ... : ReturnType body
            ZType? returnType = null;
            Dictionary<string, GenericConstraintKind>? typeParamConstraints = null;
            var bodyStart = 2;

            if (
                bodyStart < list.Items.Count
                && list.Items[bodyStart] is SExpr.Atom colon
                && colon.Text == ":"
            )
            {
                bodyStart++;
                if (bodyStart < list.Items.Count)
                {
                    returnType = ParseTypeExpr(list.Items[bodyStart]);
                    bodyStart++;
                }
            }

            // Look for :where clause (colon and 'where' are separate tokens)
            if (
                bodyStart + 1 < list.Items.Count
                && list.Items[bodyStart] is SExpr.Atom whereColon
                && whereColon.Text == ":"
                && list.Items[bodyStart + 1] is SExpr.Atom whereKw
                && whereKw.Text == "where"
            )
            {
                bodyStart += 2;
                if (bodyStart < list.Items.Count)
                {
                    typeParamConstraints = ParseWhereClause(list.Items[bodyStart]);
                    bodyStart++;
                }
            }

            if (bodyStart >= list.Items.Count)
            {
                diagnostics.Error("Function definition requires a body", list.Span);
                return new AstNode.UnitLit(list.Span);
            }

            var body = BuildExprSequence(list.Items.Skip(bodyStart).ToList(), list.Span);

            return new AstNode.Define(
                fnName,
                parms,
                returnType,
                body,
                list.Span,
                TypeParamConstraints: typeParamConstraints,
                NameSpan: fnNameAtom.Span,
                AllowsUnloopedRecursion: allowsUnloopedRecursion
            );
        }

        diagnostics.Error("Invalid 'define' form", list.Span);
        return new AstNode.UnitLit(list.Span);
    }

    private AstNode BuildLet(SExpr.SList list)
    {
        // (let ([x expr]) body) or (let ([x expr]) body1 body2 ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'let' requires a binding and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (
            list.Items[1] is not SExpr.SList bindings
            || bindings.Items.Count != 1
            || bindings.Items[0] is not SExpr.BracketList binding
            || binding.Items.Count < 2
        )
        {
            diagnostics.Error(
                "'let' requires exactly one binding: (let ([name expr]) body) or (let ([name : Type expr]) body)",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // Shared body builder, so a nested `define` here groups into a letrec exactly as it
        // does in a `define`/`lambda` body rather than being silently dropped.
        var body = BuildExprSequence(list.Items.Skip(2).ToList(), list.Span);

        // [name : Type expr] — annotated binding for upcasting
        var nameAtom = (SExpr.Atom)binding.Items[0];
        if (binding.Items.Count >= 4 && binding.Items[1] is SExpr.Atom { Text: ":" })
        {
            var type = ParseTypeExpr(binding.Items[2]);
            var value = Build(binding.Items[3]);
            return new AstNode.Let(nameAtom.Text, value, body, list.Span, type, nameAtom.Span);
        }

        var uvalue = Build(binding.Items[1]);

        return new AstNode.Let(nameAtom.Text, uvalue, body, list.Span, NameSpan: nameAtom.Span);
    }

    private AstNode BuildLetStar(SExpr.SList list)
    {
        // (let* ([x expr1] [y expr2] ...) body) or (let* ([x expr1] [y expr2] ...) body1 body2 ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'let*' requires a bindings list and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.SList bindings)
        {
            diagnostics.Error(
                "'let*' bindings must be a parenthesized list of [name expr] pairs",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // Shared body builder, so a nested `define` here groups into a letrec exactly as it
        // does in a `define`/`lambda` body rather than being silently dropped.
        var body = BuildExprSequence(list.Items.Skip(2).ToList(), list.Span);

        // Zero bindings → just the body
        if (bindings.Items.Count == 0)
            return body;

        // Fold right-to-left: innermost binding wraps body, then each outer binding wraps the result
        for (var i = bindings.Items.Count - 1; i >= 0; i--)
        {
            if (bindings.Items[i] is not SExpr.BracketList binding || binding.Items.Count < 2)
            {
                diagnostics.Error(
                    "'let*' each binding must be [name expr] or [name : Type expr]",
                    bindings.Items[i].Span
                );
                return new AstNode.UnitLit(list.Span);
            }

            var nameAtom = (SExpr.Atom)binding.Items[0];
            if (binding.Items.Count >= 4 && binding.Items[1] is SExpr.Atom { Text: ":" })
            {
                var type = ParseTypeExpr(binding.Items[2]);
                var value = Build(binding.Items[3]);
                body = new AstNode.Let(nameAtom.Text, value, body, list.Span, type, nameAtom.Span);
            }
            else
            {
                var value = Build(binding.Items[1]);
                body = new AstNode.Let(
                    nameAtom.Text,
                    value,
                    body,
                    list.Span,
                    NameSpan: nameAtom.Span
                );
            }
        }

        return body;
    }

    private AstNode BuildLetrec(SExpr.SList list)
    {
        // (letrec ([f expr] [g expr] ...) body) — a recursive binding group.
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'letrec' requires a bindings list and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.SList bindings)
        {
            diagnostics.Error(
                "'letrec' bindings must be a parenthesized list of [name expr] pairs",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // Shared body builder, so a nested `define` here groups into a letrec exactly as it
        // does in a `define`/`lambda` body rather than being silently dropped.
        var body = BuildExprSequence(list.Items.Skip(2).ToList(), list.Span);

        // Zero bindings → just the body. Nothing is recursive, so there is no group to keep.
        if (bindings.Items.Count == 0)
            return body;

        var parsed = new List<AstNode.LetrecBinding>(bindings.Items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in bindings.Items)
        {
            if (
                item is not SExpr.BracketList binding
                || binding.Items.Count < 2
                || binding.Items[0] is not SExpr.Atom nameAtom
            )
            {
                diagnostics.Error(
                    "'letrec' each binding must be [name expr] or [name : Type expr]",
                    item.Span
                );
                return new AstNode.UnitLit(list.Span);
            }

            // Unlike `let*`, a letrec group cannot shadow within itself: every name is in
            // scope in every value, so a repeat would make references ambiguous.
            if (!seen.Add(nameAtom.Text))
            {
                diagnostics.Error(
                    $"'letrec' binding '{nameAtom.Text}' is already bound in this group",
                    nameAtom.Span
                );
                return new AstNode.UnitLit(list.Span);
            }

            if (binding.Items.Count >= 4 && binding.Items[1] is SExpr.Atom { Text: ":" })
            {
                var type = ParseTypeExpr(binding.Items[2]);
                parsed.Add(
                    new AstNode.LetrecBinding(
                        nameAtom.Text,
                        Build(binding.Items[3]),
                        type,
                        nameAtom.Span
                    )
                );
            }
            else
            {
                parsed.Add(
                    new AstNode.LetrecBinding(
                        nameAtom.Text,
                        Build(binding.Items[1]),
                        NameSpan: nameAtom.Span
                    )
                );
            }
        }

        LetrecInitializationChecker.Check(parsed, diagnostics);
        return new AstNode.Letrec(parsed, body, list.Span);
    }

    private AstNode BuildUse(SExpr.SList list)
    {
        // (use ([x expr]) body) or (use ([x : Type expr]) body1 body2 ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'use' requires a binding and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (
            list.Items[1] is not SExpr.SList bindings
            || bindings.Items.Count != 1
            || bindings.Items[0] is not SExpr.BracketList binding
            || binding.Items.Count < 2
        )
        {
            diagnostics.Error(
                "'use' requires exactly one binding: (use ([name expr]) body) or (use ([name : Type expr]) body)",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // Shared body builder, so a nested `define` here groups into a letrec exactly as it
        // does in a `define`/`lambda` body rather than being silently dropped.
        var body = BuildExprSequence(list.Items.Skip(2).ToList(), list.Span);

        // [name : Type expr] — annotated binding for upcasting (e.g., MemoryStream → Stream)
        var nameAtom = (SExpr.Atom)binding.Items[0];
        if (binding.Items.Count >= 4 && binding.Items[1] is SExpr.Atom { Text: ":" })
        {
            var type = ParseTypeExpr(binding.Items[2]);
            var value = Build(binding.Items[3]);
            return new AstNode.Use(nameAtom.Text, value, body, list.Span, type, nameAtom.Span);
        }

        var uvalue = Build(binding.Items[1]);

        return new AstNode.Use(nameAtom.Text, uvalue, body, list.Span, NameSpan: nameAtom.Span);
    }

    private AstNode BuildUseStar(SExpr.SList list)
    {
        // (use* ([x expr1] [y expr2] ...) body) — desugars to nested 'use', so each
        // resource is disposed in reverse binding order (innermost first).
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'use*' requires a bindings list and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.SList bindings)
        {
            diagnostics.Error(
                "'use*' bindings must be a parenthesized list of [name expr] pairs",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // Shared body builder, so a nested `define` here groups into a letrec exactly as it
        // does in a `define`/`lambda` body rather than being silently dropped.
        var body = BuildExprSequence(list.Items.Skip(2).ToList(), list.Span);

        // Zero bindings → just the body
        if (bindings.Items.Count == 0)
            return body;

        // Fold right-to-left into nested 'use' nodes: innermost binding wraps body,
        // then each outer binding wraps the result.
        for (var i = bindings.Items.Count - 1; i >= 0; i--)
        {
            if (bindings.Items[i] is not SExpr.BracketList binding || binding.Items.Count < 2)
            {
                diagnostics.Error(
                    "'use*' each binding must be [name expr] or [name : Type expr]",
                    bindings.Items[i].Span
                );
                return new AstNode.UnitLit(list.Span);
            }

            var nameAtom = (SExpr.Atom)binding.Items[0];
            if (binding.Items.Count >= 4 && binding.Items[1] is SExpr.Atom { Text: ":" })
            {
                var type = ParseTypeExpr(binding.Items[2]);
                var value = Build(binding.Items[3]);
                body = new AstNode.Use(nameAtom.Text, value, body, list.Span, type, nameAtom.Span);
            }
            else
            {
                var value = Build(binding.Items[1]);
                body = new AstNode.Use(
                    nameAtom.Text,
                    value,
                    body,
                    list.Span,
                    NameSpan: nameAtom.Span
                );
            }
        }

        return body;
    }

    private AstNode BuildIf(SExpr.SList list)
    {
        // (if cond then else)
        if (list.Items.Count != 4)
        {
            diagnostics.Error("'if' requires condition, then, and else branches", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var cond = Build(list.Items[1]);
        var then = Build(list.Items[2]);
        var @else = Build(list.Items[3]);

        return new AstNode.If(cond, then, @else, list.Span);
    }

    private AstNode BuildLambda(SExpr.SList list)
    {
        // (lambda (params...) body)
        // (lambda (params...) : ReturnType body)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'lambda' requires parameters and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.SList paramList)
        {
            diagnostics.Error("'lambda' parameters must be in parentheses", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var parms = paramList.Items.Select(ParseParam).ToList();
        ValidateVariadicParams(parms, list.Span);

        // Look for return type annotation: ... : ReturnType body
        ZType? returnType = null;
        var bodyStart = 2;

        if (
            bodyStart < list.Items.Count
            && list.Items[bodyStart] is SExpr.Atom colon
            && colon.Text == ":"
        )
        {
            bodyStart++;
            if (bodyStart < list.Items.Count)
            {
                returnType = ParseTypeExpr(list.Items[bodyStart]);
                bodyStart++;
            }
        }

        if (bodyStart >= list.Items.Count)
        {
            diagnostics.Error("'lambda' requires a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var body = BuildExprSequence(list.Items.Skip(bodyStart).ToList(), list.Span);

        return new AstNode.Lambda(parms, returnType, body, list.Span);
    }

    private AstNode BuildMatch(SExpr.SList list)
    {
        // (match expr [pattern body] ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'match' requires a scrutinee and at least one arm", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var scrutinee = Build(list.Items[1]);
        var arms = new List<MatchArm>();

        for (var i = 2; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.BracketList arm && arm.Items.Count >= 2)
            {
                var pattern = ParsePattern(arm.Items[0]);
                var body = Build(arm.Items[1]);
                arms.Add(new MatchArm(pattern, body, arm.Span));
            }
            else
            {
                diagnostics.Error("Match arm must be [pattern body]", list.Items[i].Span);
            }

        return new AstNode.Match(scrutinee, arms, list.Span);
    }

    private AstNode BuildRecord(SExpr.SList list)
    {
        return BuildRecordLike(list, "define-record", false);
    }

    private AstNode BuildStruct(SExpr.SList list)
    {
        return BuildRecordLike(list, "define-struct", true);
    }

    private AstNode BuildRecordLike(SExpr.SList list, string keyword, bool isValueType)
    {
        // (define-record Name [field : Type] ...)  or  (define-struct Name [field : Type] ...)
        // (define-record (Name a b) [field : Type] ...)  or  (define-struct (Name a b) [field : Type] ...)
        if (list.Items.Count < 2)
        {
            diagnostics.Error($"'{keyword}' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        SourceSpan nameSpan;
        var typeParams = new List<string>();
        int fieldsStart;

        if (list.Items[1] is SExpr.SList nameList)
        {
            var nameAtom = (SExpr.Atom)nameList.Items[0];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            for (var i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            fieldsStart = 2;
        }
        else
        {
            var nameAtom = (SExpr.Atom)list.Items[1];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            fieldsStart = 2;
        }

        Dictionary<string, GenericConstraintKind>? typeParamConstraints = null;
        if (
            fieldsStart + 1 < list.Items.Count
            && list.Items[fieldsStart] is SExpr.Atom recWhereColon
            && recWhereColon.Text == ":"
            && list.Items[fieldsStart + 1] is SExpr.Atom recWhereKw
            && recWhereKw.Text == "where"
        )
        {
            fieldsStart += 2;
            if (fieldsStart < list.Items.Count)
            {
                typeParamConstraints = ParseWhereClause(list.Items[fieldsStart]);
                fieldsStart++;
            }
        }

        var fields = new List<FieldDecl>();
        for (var i = fieldsStart; i < list.Items.Count; i++)
            fields.Add(ParseFieldDecl(list.Items[i]));

        return new AstNode.RecordDecl(
            name,
            typeParams,
            fields,
            list.Span,
            TypeParamConstraints: typeParamConstraints,
            IsValueType: isValueType,
            NameSpan: nameSpan
        );
    }

    private AstNode BuildTypeAliasDecl(SExpr.SList list)
    {
        // (define-type-alias (Name ^a ^b ...) Fully.Qualified.OpenGenericClrType :from "AssemblyName")
        // (define-type-alias (Name ^a) :array)
        // (define-type-alias Name ClrType)                       — arity 0
        if (list.Items.Count < 3)
        {
            diagnostics.Error(
                "'define-type-alias' requires a name (with optional type params) and a CLR target",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        SourceSpan nameSpan;
        var typeParams = new List<string>();

        if (list.Items[1] is SExpr.SList headList && headList.Items.Count >= 1)
        {
            if (headList.Items[0] is not SExpr.Atom nameAtom)
            {
                diagnostics.Error("'define-type-alias' name must be an identifier", headList.Span);
                return new AstNode.UnitLit(list.Span);
            }

            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            for (var i = 1; i < headList.Items.Count; i++)
            {
                if (headList.Items[i] is not SExpr.Atom paramAtom)
                {
                    diagnostics.Error(
                        "'define-type-alias' type params must be identifiers starting with '^'",
                        headList.Items[i].Span
                    );
                    continue;
                }

                if (!paramAtom.Text.StartsWith("^"))
                {
                    diagnostics.Error(
                        $"'define-type-alias' type params must start with '^' (got '{paramAtom.Text}')",
                        paramAtom.Span
                    );
                    continue;
                }

                if (typeParams.Contains(paramAtom.Text))
                {
                    diagnostics.Error(
                        $"'define-type-alias' duplicate type parameter '{paramAtom.Text}'",
                        paramAtom.Span
                    );
                    continue;
                }

                typeParams.Add(paramAtom.Text);
            }
        }
        else if (list.Items[1] is SExpr.Atom bareName)
        {
            name = bareName.Text;
            nameSpan = bareName.Span;
        }
        else
        {
            diagnostics.Error(
                "'define-type-alias' name must be an identifier or (Name ^a ...) form",
                list.Items[1].Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
        {
            diagnostics.Error(
                $"'define-type-alias' name must start with an uppercase letter (got '{name}')",
                list.Items[1].Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        // The CLR target may be either a single atom whose text starts with ':' (e.g. ':array')
        // — written contiguously in source — or a `:` atom followed by the keyword name (the
        // lexer always emits ':' as a separate Colon token, so the second form is what stdlib
        // actually produces). Both shapes are accepted.
        var idx = 2;
        if (
            !TryReadKeywordOrTarget(
                list.Items,
                ref idx,
                out var targetKeyword,
                out var targetText,
                out var targetSpan
            )
        )
        {
            diagnostics.Error(
                "'define-type-alias' target must be a single identifier (or ':array')",
                list.Items[2].Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        var isArray = targetKeyword == "array";
        var clrTarget = isArray ? "" : targetText!;

        if (isArray && typeParams.Count != 1)
        {
            diagnostics.Error(
                $"'define-type-alias :array' requires exactly one type parameter (got {typeParams.Count})",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        if (!isArray)
            if (
                string.IsNullOrEmpty(clrTarget)
                || clrTarget.StartsWith(".")
                || clrTarget.EndsWith(".")
            )
            {
                diagnostics.Error(
                    $"'define-type-alias' CLR target '{clrTarget}' is not a valid identifier",
                    targetSpan
                );
                return new AstNode.UnitLit(list.Span);
            }

        string? assemblyHint = null;
        if (
            TryPeekKeyword(list.Items, idx, out var fromKw, out var consumed, out var fromSpan)
            && fromKw == "from"
        )
        {
            idx += consumed;
            if (idx >= list.Items.Count || list.Items[idx] is not SExpr.Atom asmAtom)
            {
                diagnostics.Error(
                    "'define-type-alias :from' requires an assembly name string",
                    fromSpan
                );
                return new AstNode.UnitLit(list.Span);
            }

            var asmText = asmAtom.Text;
            if (asmText.Length >= 2 && asmText[0] == '"' && asmText[^1] == '"')
                asmText = asmText[1..^1];
            assemblyHint = asmText;
            idx++;
        }

        if (idx < list.Items.Count)
            diagnostics.Error(
                "'define-type-alias' has unexpected trailing items",
                list.Items[idx].Span
            );

        return new AstNode.TypeAliasDecl(
            name,
            typeParams,
            clrTarget,
            assemblyHint,
            isArray,
            nameSpan,
            list.Span
        );
    }

    /// <summary>
    ///     Reads either a `:keyword` written contiguously (one atom) or `:` + `keyword` (two atoms,
    ///     since the lexer emits ':' as a separate Colon token), or a plain identifier (CLR target).
    ///     Returns the keyword text (without leading ':') in <paramref name="keyword" /> when the
    ///     atom is a colon-keyword; otherwise <paramref name="keyword" /> is null and
    ///     <paramref name="rawText" /> holds the plain identifier. Advances <paramref name="idx" />
    ///     past the consumed atoms. Returns false if the next item is not an atom.
    /// </summary>
    private static bool TryReadKeywordOrTarget(
        IReadOnlyList<SExpr> items,
        ref int idx,
        out string? keyword,
        out string? rawText,
        out SourceSpan span
    )
    {
        keyword = null;
        rawText = null;
        span = default;
        if (idx >= items.Count)
            return false;
        if (items[idx] is not SExpr.Atom atom)
            return false;
        span = atom.Span;
        if (atom.Text == ":")
        {
            if (idx + 1 < items.Count && items[idx + 1] is SExpr.Atom nextAtom)
            {
                keyword = nextAtom.Text;
                rawText = nextAtom.Text;
                span = nextAtom.Span;
                idx += 2;
                return true;
            }

            idx++;
            return true;
        }

        if (atom.Text.Length > 1 && atom.Text[0] == ':')
        {
            keyword = atom.Text[1..];
            rawText = atom.Text;
            idx++;
            return true;
        }

        // Plain identifier (CLR target)
        rawText = atom.Text;
        idx++;
        return true;
    }

    /// <summary>
    ///     Like <see cref="TryReadKeywordOrTarget" /> but only succeeds when the next atom is a
    ///     colon-keyword. Does not advance <paramref name="idx" /> on failure.
    /// </summary>
    private static bool TryPeekKeyword(
        IReadOnlyList<SExpr> items,
        int idx,
        out string? keyword,
        out int consumed,
        out SourceSpan span
    )
    {
        keyword = null;
        consumed = 0;
        span = default;
        if (idx >= items.Count || items[idx] is not SExpr.Atom atom)
            return false;
        if (atom.Text == ":" && idx + 1 < items.Count && items[idx + 1] is SExpr.Atom nextAtom)
        {
            keyword = nextAtom.Text;
            span = nextAtom.Span;
            consumed = 2;
            return true;
        }

        if (atom.Text.Length > 1 && atom.Text[0] == ':')
        {
            keyword = atom.Text[1..];
            span = atom.Span;
            consumed = 1;
            return true;
        }

        return false;
    }

    private AstNode BuildUnion(SExpr.SList list)
    {
        // (define-union Name (Case1 [field : Type]) ...)
        // (define-union (Name a) (Case1 [field : Type]) ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'define-union' requires a name and at least one case", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        SourceSpan nameSpan;
        var typeParams = new List<string>();
        int casesStart;

        if (list.Items[1] is SExpr.SList nameList)
        {
            var nameAtom = (SExpr.Atom)nameList.Items[0];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            for (var i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            casesStart = 2;
        }
        else
        {
            var nameAtom = (SExpr.Atom)list.Items[1];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            casesStart = 2;
        }

        // Look for :where clause
        Dictionary<string, GenericConstraintKind>? typeParamConstraints = null;
        if (
            casesStart + 1 < list.Items.Count
            && list.Items[casesStart] is SExpr.Atom unionWhereColon
            && unionWhereColon.Text == ":"
            && list.Items[casesStart + 1] is SExpr.Atom unionWhereKw
            && unionWhereKw.Text == "where"
        )
        {
            casesStart += 2;
            if (casesStart < list.Items.Count)
            {
                typeParamConstraints = ParseWhereClause(list.Items[casesStart]);
                casesStart++;
            }
        }

        var cases = new List<UnionCase>();
        for (var i = casesStart; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.SList caseList && caseList.Items.Count >= 1)
            {
                var caseAtom = (SExpr.Atom)caseList.Items[0];
                var fields = new List<FieldDecl>();
                for (var j = 1; j < caseList.Items.Count; j++)
                    fields.Add(ParseFieldDecl(caseList.Items[j]));
                cases.Add(new UnionCase(caseAtom.Text, fields, caseList.Span, caseAtom.Span));
            }

        return new AstNode.UnionDecl(
            name,
            typeParams,
            cases,
            list.Span,
            TypeParamConstraints: typeParamConstraints,
            NameSpan: nameSpan
        );
    }

    private AstNode BuildPartial(SExpr.SList list)
    {
        // (partial f arg1 arg2 ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'partial' requires a function and at least one argument", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var func = Build(list.Items[1]);
        var args = new List<AstNode>();
        for (var i = 2; i < list.Items.Count; i++)
            args.Add(Build(list.Items[i]));

        return new AstNode.Partial(func, args, list.Span);
    }

    private AstNode BuildRaise(SExpr.SList list)
    {
        // (raise expr)
        if (list.Items.Count != 2)
        {
            diagnostics.Error("'raise' requires exactly one expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        return new AstNode.Raise(Build(list.Items[1]), list.Span);
    }

    private AstNode BuildSetField(SExpr.SList list)
    {
        // (set! field-name expr)
        if (list.Items.Count != 3)
        {
            diagnostics.Error("'set!' requires a field name and a value expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.Atom fieldAtom)
        {
            diagnostics.Error("'set!' field name must be an identifier", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        return new AstNode.SetField(fieldAtom.Text, Build(list.Items[2]), list.Span);
    }

    private AstNode BuildTupleNew(SExpr.SList list)
    {
        // (values expr1 expr2 ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error("'values' requires at least 2 elements", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items.Count > 8) // keyword + max 7 elements
        {
            diagnostics.Error("'values' supports at most 7 elements", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var elements = new List<AstNode>();
        for (var i = 1; i < list.Items.Count; i++)
            elements.Add(Build(list.Items[i]));
        return new AstNode.TupleNew(elements, list.Span);
    }

    private AstNode BuildWithHandlers(SExpr.SList list)
    {
        // (with-handlers ([ExType var] handler-body) ... body-expr)
        // Minimum: keyword + 1 handler + body = 3 items
        if (list.Items.Count < 3)
        {
            diagnostics.Error(
                "'with-handlers' requires at least one handler and a body expression",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        var handlers = new List<HandlerClause>();
        // Items 1..N-1 are handler clauses, last item is the body
        for (var i = 1; i < list.Items.Count - 1; i++)
        {
            // Each handler clause must be a list: ([ExType var] handler-body)
            if (list.Items[i] is not SExpr.SList clause || clause.Items.Count != 2)
            {
                diagnostics.Error(
                    "'with-handlers' handler must be ([ExceptionType var] handler-body)",
                    list.Items[i].Span
                );
                continue;
            }

            // First element is [ExType var] — may be BracketList or SList
            IReadOnlyList<SExpr> bindingItems;
            SourceSpan bindingSpan;
            if (clause.Items[0] is SExpr.BracketList bl && bl.Items.Count == 2)
            {
                bindingItems = bl.Items;
                bindingSpan = bl.Span;
            }
            else if (clause.Items[0] is SExpr.SList sl && sl.Items.Count == 2)
            {
                bindingItems = sl.Items;
                bindingSpan = sl.Span;
            }
            else
            {
                diagnostics.Error(
                    "'with-handlers' handler binding must be [ExceptionType var]",
                    clause.Items[0].Span
                );
                continue;
            }

            if (bindingItems[0] is not SExpr.Atom typeAtom)
            {
                diagnostics.Error(
                    "'with-handlers' exception type must be a name",
                    bindingItems[0].Span
                );
                continue;
            }

            if (bindingItems[1] is not SExpr.Atom varAtom)
            {
                diagnostics.Error(
                    "'with-handlers' binding variable must be a name",
                    bindingItems[1].Span
                );
                continue;
            }

            var handlerBody = Build(clause.Items[1]);
            handlers.Add(new HandlerClause(typeAtom.Text, varAtom.Text, handlerBody, clause.Span));
        }

        var body = Build(list.Items[^1]);
        return new AstNode.WithHandlers(handlers, body, list.Span);
    }

    private AstNode BuildWith(SExpr.SList list)
    {
        // (with record-expr [field value] ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error(
                "'with' requires a record expression and at least one [field value] update",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        var recordExpr = Build(list.Items[1]);
        var updates = new List<(string FieldName, AstNode Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 2; i < list.Items.Count; i++)
        {
            if (list.Items[i] is not SExpr.BracketList clause || clause.Items.Count != 2)
            {
                diagnostics.Error("'with' update must be [field value]", list.Items[i].Span);
                continue;
            }

            if (clause.Items[0] is not SExpr.Atom fieldAtom)
            {
                diagnostics.Error("'with' field name must be an identifier", clause.Items[0].Span);
                continue;
            }

            if (!seen.Add(fieldAtom.Text))
            {
                diagnostics.Error(
                    $"'with' specifies field '{fieldAtom.Text}' more than once",
                    clause.Items[0].Span
                );
                continue;
            }

            var value = Build(clause.Items[1]);
            updates.Add((fieldAtom.Text, value));
        }

        return new AstNode.With(recordExpr, updates, list.Span);
    }

    // Consume the assembly-name atom following a `:from` keyword inside an
    // import-clr bracket, advancing `j` past it. Strips surrounding quotes.
    private string? ParseFromAssembly(IReadOnlyList<SExpr> items, ref int j, SourceSpan fromSpan)
    {
        if (j >= items.Count || items[j] is not SExpr.Atom asmAtom)
        {
            diagnostics.Error("'import-clr :from' requires an assembly name string", fromSpan);
            return null;
        }

        var asmText = asmAtom.Text;
        if (asmText.Length >= 2 && asmText[0] == '"' && asmText[^1] == '"')
            asmText = asmText[1..^1];
        j++;
        return asmText;
    }

    private AstNode BuildImportClr(SExpr.SList list)
    {
        // (import-clr [alias Type/Method] ... Namespace ...)
        // Extended syntax:
        //   [alias Type.Method :instance : (args -> ret)]
        //   [alias Type.Prop :instance-property : (args -> ret)]
        //   [alias Type.Item :instance-indexer : (args -> ret)]
        _log.Debug("BuildImportClr: processing list with {ItemCount} items", list.Items.Count);
        var imports = new List<ClrImport>();
        var namespaces = new List<string>();
        for (var i = 1; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.BracketList bracket && bracket.Items.Count >= 2)
            {
                var aliasAtom = (SExpr.Atom)bracket.Items[0];
                var alias = aliasAtom.Text;
                var qualName = ((SExpr.Atom)bracket.Items[1]).Text;
                _log.Debug(
                    "BuildImportClr: bracket item {Index}: alias={Alias}, qualName={QualName}, bracketItems={BracketItems}",
                    i,
                    alias,
                    qualName,
                    bracket.Items.Count
                );
                var typeParams = new List<string>();
                var kind = ClrImportKind.Static;
                ZType? typeAnnotation = null;
                Dictionary<string, GenericConstraintKind>? typeParamConstraints = null;
                string? assemblyHint = null;

                var j = 2;
                while (j < bracket.Items.Count)
                {
                    if (bracket.Items[j] is SExpr.Atom kw)
                    {
                        switch (kw.Text)
                        {
                            case ":instance":
                                kind = ClrImportKind.Instance;
                                j++;
                                continue;
                            case ":instance-property":
                                kind = ClrImportKind.InstanceProperty;
                                j++;
                                continue;
                            case ":instance-property-set":
                                kind = ClrImportKind.InstancePropertySet;
                                j++;
                                continue;
                            case ":instance-property-init":
                                kind = ClrImportKind.InstancePropertyInit;
                                j++;
                                continue;
                            case ":instance-indexer":
                                kind = ClrImportKind.InstanceIndexer;
                                j++;
                                continue;
                            case ":instance-indexer-set":
                                kind = ClrImportKind.InstanceIndexerSet;
                                j++;
                                continue;
                            case ":from":
                                j++;
                                assemblyHint = ParseFromAssembly(bracket.Items, ref j, kw.Span);
                                continue;
                            case ":":
                                // Check if the next token is a kind keyword (colon was tokenized separately)
                                if (
                                    j + 1 < bracket.Items.Count
                                    && bracket.Items[j + 1] is SExpr.Atom nextKw
                                )
                                    switch (nextKw.Text)
                                    {
                                        case "instance":
                                            kind = ClrImportKind.Instance;
                                            j += 2;
                                            continue;
                                        case "instance-property":
                                            kind = ClrImportKind.InstanceProperty;
                                            j += 2;
                                            continue;
                                        case "instance-property-set":
                                            kind = ClrImportKind.InstancePropertySet;
                                            j += 2;
                                            continue;
                                        case "instance-property-init":
                                            kind = ClrImportKind.InstancePropertyInit;
                                            j += 2;
                                            continue;
                                        case "instance-indexer":
                                            kind = ClrImportKind.InstanceIndexer;
                                            j += 2;
                                            continue;
                                        case "instance-indexer-set":
                                            kind = ClrImportKind.InstanceIndexerSet;
                                            j += 2;
                                            continue;
                                        case "from":
                                            j += 2;
                                            assemblyHint = ParseFromAssembly(
                                                bracket.Items,
                                                ref j,
                                                nextKw.Span
                                            );
                                            continue;
                                        case "where":
                                            j += 2;
                                            if (j < bracket.Items.Count)
                                                typeParamConstraints = ParseWhereClause(
                                                    bracket.Items[j]
                                                );
                                            else
                                                diagnostics.Error(
                                                    "Expected constraint list after ':where'",
                                                    nextKw.Span
                                                );
                                            j++;
                                            continue;
                                    }

                                // Type annotation follows
                                j++;
                                if (j < bracket.Items.Count)
                                {
                                    typeAnnotation = ParseTypeExpr(bracket.Items[j]);
                                    j++;
                                }
                                else
                                {
                                    diagnostics.Error(
                                        "Expected type annotation after ':'",
                                        kw.Span
                                    );
                                }

                                continue;
                        }

                        // Not a keyword — must be a type parameter like ^a
                        if (kw.Text.StartsWith('^'))
                            typeParams.Add(kw.Text);
                        else
                            diagnostics.Error(
                                $"Unexpected token '{kw.Text}' in import-clr bracket",
                                kw.Span
                            );
                    }
                    else
                    {
                        diagnostics.Error(
                            "Type parameter must be an atom like ^a",
                            bracket.Items[j].Span
                        );
                    }

                    j++;
                }

                imports.Add(
                    new ClrImport(
                        alias,
                        qualName,
                        typeParams,
                        bracket.Span,
                        kind,
                        typeAnnotation,
                        typeParamConstraints,
                        assemblyHint,
                        aliasAtom.Span
                    )
                );
            }
            else if (list.Items[i] is SExpr.Atom atom)
            {
                namespaces.Add(atom.Text);
            }
            else
            {
                diagnostics.Error(
                    "import-clr entry must be [alias qualified/Name] or a namespace",
                    list.Items[i].Span
                );
            }

        return new AstNode.ImportClr(imports, namespaces, list.Span);
    }

    /// <summary>
    ///     Parses a where clause like (^k notnull) or ((^k notnull) (^v struct class)).
    ///     A single parenthesized pair means one constraint; nested lists mean multiple.
    /// </summary>
    private Dictionary<string, GenericConstraintKind> ParseWhereClause(SExpr expr)
    {
        var constraints = new Dictionary<string, GenericConstraintKind>();

        if (expr is SExpr.SList clauseList)
        {
            // Check if this is a single constraint like (^k notnull)
            // or multiple constraints like ((^k notnull) (^v struct))
            if (
                clauseList.Items.Count >= 2
                && clauseList.Items[0] is SExpr.Atom first
                && first.Text.StartsWith('^')
            )
                // Single constraint: (^k notnull struct ...)
                ParseSingleConstraint(clauseList, constraints);
            else
                // Multiple constraints: ((^k notnull) (^v struct))
                foreach (var item in clauseList.Items)
                    if (item is SExpr.SList sub)
                        ParseSingleConstraint(sub, constraints);
                    else
                        diagnostics.Error(
                            "Expected constraint clause like (^k notnull)",
                            item.Span
                        );
        }
        else
        {
            diagnostics.Error("Expected constraint list after ':where'", expr.Span);
        }

        return constraints;
    }

    private void ParseSingleConstraint(
        SExpr.SList clause,
        Dictionary<string, GenericConstraintKind> constraints
    )
    {
        if (
            clause.Items.Count < 2
            || clause.Items[0] is not SExpr.Atom paramAtom
            || !paramAtom.Text.StartsWith('^')
        )
        {
            diagnostics.Error(
                "Constraint clause must start with a type parameter like ^k",
                clause.Span
            );
            return;
        }

        var paramName = paramAtom.Text;
        var kind = GenericConstraintKind.None;
        for (var i = 1; i < clause.Items.Count; i++)
            if (clause.Items[i] is SExpr.Atom constraintAtom)
                kind |= constraintAtom.Text switch
                {
                    "notnull" => GenericConstraintKind.NotNull,
                    "struct" => GenericConstraintKind.Struct,
                    "class" => GenericConstraintKind.Class,
                    "new" => GenericConstraintKind.New,
                    "unmanaged" => GenericConstraintKind.Unmanaged,
                    "default" => GenericConstraintKind.Default,
                    _ => ReportUnknownConstraint(constraintAtom),
                };
            else
                diagnostics.Error(
                    "Constraint must be an atom like 'notnull', 'struct', 'class', 'new', 'unmanaged', or 'default'",
                    clause.Items[i].Span
                );

        if (kind != GenericConstraintKind.None)
            constraints[paramName] = kind;
    }

    private GenericConstraintKind ReportUnknownConstraint(SExpr.Atom atom)
    {
        diagnostics.Error(
            $"Unknown constraint '{atom.Text}'. Expected 'notnull', 'struct', 'class', 'new', 'unmanaged', or 'default'",
            atom.Span
        );
        return GenericConstraintKind.None;
    }

    private AstNode BuildNamespace(SExpr.SList list)
    {
        if (list.Items.Count != 2)
        {
            diagnostics.Error("'namespace' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var name = ((SExpr.Atom)list.Items[1]).Text;
        return new AstNode.NamespaceDecl(name, list.Span);
    }

    private AstNode BuildModule(SExpr.SList list)
    {
        if (list.Items.Count < 2)
        {
            diagnostics.Error("'module' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var name = ((SExpr.Atom)list.Items[1]).Text;
        _log.Debug(
            "BuildModule: module '{Name}' has {ItemCount} items in list (body explicit={HasBody})",
            name,
            list.Items.Count,
            list.Items.Count > 2
        );

        if (list.Items.Count > 2)
        {
            // Explicit body: (module name form1 form2 ...)
            var body = list.Items.Skip(2).Select(Build).ToList();
            _log.Debug(
                "BuildModule: module '{Name}' explicit body has {BodyCount} forms",
                name,
                body.Count
            );
            return new AstNode.ModuleDecl(name, body, list.Span);
        }

        // No explicit body — BuildProgram will absorb remaining forms
        _log.Debug(
            "BuildModule: module '{Name}' empty body, BuildProgram will absorb remaining forms",
            name
        );
        return new AstNode.ModuleDecl(name, [], list.Span);
    }

    private AstNode BuildImport(SExpr.SList list)
    {
        if (list.Items.Count < 2)
        {
            diagnostics.Error("'import' requires at least one module name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        // Single module: (import foo)
        if (list.Items.Count == 2)
        {
            var name = ((SExpr.Atom)list.Items[1]).Text;
            return new AstNode.Import(name, list.Span);
        }

        // Multiple modules: (import foo bar baz) — return Program splice to be flattened
        var imports = new List<AstNode>();
        for (var i = 1; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.Atom atom)
                imports.Add(new AstNode.Import(atom.Text, atom.Span));
            else
                diagnostics.Error("'import' entries must be module names", list.Items[i].Span);
        return new AstNode.Program(imports, list.Span);
    }

    private AstNode BuildExport(SExpr.SList list)
    {
        var names = new List<string>();
        for (var i = 1; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.Atom atom)
                names.Add(atom.Text);
            else
                diagnostics.Error("'export' entries must be names", list.Items[i].Span);

        if (names.Count == 0)
            diagnostics.Error("'export' requires at least one name", list.Span);

        return new AstNode.Export(names, list.Span);
    }

    private AstNode BuildObjectExpr(SExpr.SList list)
    {
        // (object IFoo (define (Method [params...]) : RetType body) ...)
        // (object (IFoo IBar) (define (Method [params...]) : RetType body) ...)
        // (object : BaseClass IFoo (define (Method [params...]) : RetType body) ...)
        // (object : BaseClass (constructor (super args...) ...) (define (Method ...) ...) ...)
        if (list.Items.Count < 3)
        {
            diagnostics.Error(
                "'object' requires interface name(s) and at least one method",
                list.Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        string? baseClassName = null;
        var interfaceNames = new List<string>();
        var membersStart = 2;

        // Check for : BaseClass syntax at position 1
        if (list.Items[1] is SExpr.Atom colonAtom && colonAtom.Text == ":")
        {
            // Parse base class + optional interface names (same pattern as BuildClass)
            var idx = 2;
            var allNames = new List<string>();
            while (
                idx < list.Items.Count
                && list.Items[idx] is SExpr.Atom nameAtom
                && nameAtom.Text != ":"
                && char.IsUpper(nameAtom.Text[0])
            )
            {
                allNames.Add(nameAtom.Text);
                idx++;
            }

            // Support grouped interfaces: (object : BaseClass (IFoo IBar) ...)
            // Only treat as interface group if ALL items are uppercase atoms (not a method definition)
            if (
                idx < list.Items.Count
                && list.Items[idx] is SExpr.SList ifaceGroup
                && ifaceGroup.Items.Count > 0
                && ifaceGroup.Items.All(item => item is SExpr.Atom a && char.IsUpper(a.Text[0]))
            )
            {
                foreach (var item in ifaceGroup.Items)
                    interfaceNames.Add(((SExpr.Atom)item).Text);
                idx++;
            }

            if (allNames.Count > 0)
            {
                baseClassName = allNames[0];
                interfaceNames.AddRange(allNames.Skip(1));
            }
            else
            {
                diagnostics.Error("'object :' requires a base class name", list.Span);
                return new AstNode.UnitLit(list.Span);
            }

            membersStart = idx;
        }
        else if (list.Items[1] is SExpr.Atom ifaceAtom)
        {
            interfaceNames.Add(ifaceAtom.Text);
        }
        else if (list.Items[1] is SExpr.SList ifaceList)
        {
            foreach (var item in ifaceList.Items)
                if (item is SExpr.Atom a)
                    interfaceNames.Add(a.Text);
                else
                    diagnostics.Error("Interface name must be an identifier", item.Span);
        }
        else
        {
            diagnostics.Error("'object' requires interface name(s)", list.Items[1].Span);
            return new AstNode.UnitLit(list.Span);
        }

        var methods = new List<ObjectMethod>();
        ConstructorDecl? constructorDecl = null;
        for (var i = membersStart; i < list.Items.Count; i++)
        {
            // Detect constructor block
            if (
                list.Items[i] is SExpr.SList sl
                && sl.Items.Count >= 1
                && sl.Items[0] is SExpr.Atom ctorAtom
                && ctorAtom.Text == "constructor"
            )
            {
                if (constructorDecl is not null)
                {
                    diagnostics.Error(
                        "Object expression cannot have multiple constructors",
                        sl.Span
                    );
                    continue;
                }

                constructorDecl = ParseConstructorDecl(sl);
                continue;
            }

            var method = ParseObjectMethod(list.Items[i]);
            if (method is not null)
                methods.Add(method);
        }

        return new AstNode.ObjectExpr(
            interfaceNames,
            methods,
            list.Span,
            baseClassName,
            constructorDecl
        );
    }

    private ObjectMethod? ParseObjectMethod(SExpr expr, bool isAsync = false)
    {
        if (
            expr is SExpr.SList outerMethodList
            && outerMethodList.Items.Count >= 2
            && outerMethodList.Items[0] is SExpr.Atom headAtom
            && (headAtom.Text == "define" || headAtom.Text == "define-async")
        )
        {
            var isAsyncForm = headAtom.Text == "define-async";
            var keyword = headAtom.Text;

            // A method of a sealed class becomes a loop just as a top-level define does, so it
            // takes the same `#:recursive` opt-out from ZS0005.
            var (allowsUnloopedRecursion, methodList) = StripRecursiveMarker(
                outerMethodList,
                keyword
            );

            // (define (Name [params...]) : RetType body)
            // (define-async (Name [params...]) : RetType body)
            if (
                methodList.Items[1] is not SExpr.SList sig
                || sig.Items.Count == 0
                || sig.Items[0] is not SExpr.Atom nameAtom
            )
            {
                diagnostics.Error(
                    $"'{keyword}' method requires a signature (Name [params...])",
                    methodList.Span
                );
                return null;
            }

            var methodName = nameAtom.Text;
            var parms = new List<Param>();
            for (var i = 1; i < sig.Items.Count; i++)
                parms.Add(ParseParam(sig.Items[i]));

            ZType? returnType = null;
            var bodyStart = 2;
            if (
                bodyStart < methodList.Items.Count
                && methodList.Items[bodyStart] is SExpr.Atom colon
                && colon.Text == ":"
            )
            {
                bodyStart++;
                if (bodyStart < methodList.Items.Count)
                {
                    returnType = ParseTypeExpr(methodList.Items[bodyStart]);
                    bodyStart++;
                }
            }

            if (bodyStart >= methodList.Items.Count)
            {
                diagnostics.Error("Method requires a body", methodList.Span);
                return null;
            }

            // A method body is a single expression. Extra forms used to be dropped without a
            // word, which turns a nested `define` here into a confusing type error on the
            // method's return type instead of a statement about what is unsupported.
            if (bodyStart < methodList.Items.Count - 1)
                diagnostics.Error(
                    $"'{keyword}' method '{methodName}' has more than one body expression, which "
                        + "is not supported. Wrap the body in '(begin ...)' — or in a 'let' if it "
                        + "needs a nested 'define'",
                    methodList.Items[bodyStart + 1].Span
                );

            var body = Build(methodList.Items[bodyStart]);
            return new ObjectMethod(
                methodName,
                parms,
                returnType,
                body,
                methodList.Span,
                IsAsync: isAsyncForm || isAsync,
                AllowsUnloopedRecursion: allowsUnloopedRecursion,
                NameSpan: nameAtom.Span
            );
        }

        diagnostics.Error(
            "Method must be defined with 'define' or 'define-async'. "
                + "Replace '(Name [params...] : RetType body)' with "
                + "'(define (Name [params...]) : RetType body)' or "
                + "'(define-async (Name [params...]) : RetType body)'",
            expr.Span
        );
        return null;
    }

    private AstNode BuildClass(SExpr.SList list)
    {
        // (define-class Name [field : Type] ... (define (Method [params...]) : RetType body) ...)
        // (define-class #:open Name ...)
        // (define-class (Name a b) ...)
        // (define-class Name : BaseClass IFoo IBar [field : Type] ... (define (Method ...) ...) ...)
        // (define-class Name : BaseClass (constructor [params...] (super args...) (set! field expr) ...) ...)
        if (list.Items.Count < 2)
        {
            diagnostics.Error("'define-class' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        // Parse optional #:open flag — lexed as a single Symbol token.
        var isOpen = false;
        var nameIdx = 1;
        if (
            list.Items.Count >= 2
            && list.Items[1] is SExpr.Atom openFlag
            && openFlag.Text == "#:open"
        )
        {
            isOpen = true;
            nameIdx = 2;
            if (list.Items.Count < 3)
            {
                diagnostics.Error("'class #:open' requires a name", list.Span);
                return new AstNode.UnitLit(list.Span);
            }
        }

        string name;
        SourceSpan nameSpan;
        var typeParams = new List<string>();
        int membersStart;

        if (list.Items[nameIdx] is SExpr.SList nameList)
        {
            // Generic: (define-class (Container a) ...)
            var nameAtom = (SExpr.Atom)nameList.Items[0];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            for (var i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            membersStart = nameIdx + 1;
        }
        else
        {
            var nameAtom = (SExpr.Atom)list.Items[nameIdx];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            membersStart = nameIdx + 1;
        }

        // Parse optional base class / interface list: : BaseClass IFoo IBar
        // First name after ':' is treated as base class (position-based); rest are interfaces.
        // Type inference will validate whether the first name is actually a class or interface.
        string? baseClassName = null;
        var interfaceNames = new List<string>();
        if (
            membersStart < list.Items.Count
            && list.Items[membersStart] is SExpr.Atom colonAtom
            && colonAtom.Text == ":"
        )
        {
            membersStart++;
            var allNames = new List<string>();
            while (
                membersStart < list.Items.Count
                && list.Items[membersStart] is SExpr.Atom nameAtom
                && nameAtom.Text != ":"
                && char.IsUpper(nameAtom.Text[0])
            )
            {
                allNames.Add(nameAtom.Text);
                membersStart++;
            }

            if (allNames.Count > 0)
            {
                // First name is base class candidate; rest are interfaces
                baseClassName = allNames[0];
                interfaceNames.AddRange(allNames.Skip(1));
            }
        }

        // Look for :where clause
        Dictionary<string, GenericConstraintKind>? classConstraints = null;
        if (
            membersStart + 1 < list.Items.Count
            && list.Items[membersStart] is SExpr.Atom classWhereColon
            && classWhereColon.Text == ":"
            && list.Items[membersStart + 1] is SExpr.Atom classWhereKw
            && classWhereKw.Text == "where"
        )
        {
            membersStart += 2;
            if (membersStart < list.Items.Count)
            {
                classConstraints = ParseWhereClause(list.Items[membersStart]);
                membersStart++;
            }
        }

        var fields = new List<FieldDecl>();
        var methods = new List<ObjectMethod>();
        ConstructorDecl? constructorDecl = null;

        // Flatten (begin ...) forms and collect pending attributes for methods
        var members = new List<SExpr>();
        for (var i = membersStart; i < list.Items.Count; i++)
            if (
                list.Items[i] is SExpr.SList sl
                && sl.Items.Count >= 1
                && sl.Items[0] is SExpr.Atom a
                && a.Text == "begin"
            )
                for (var j = 1; j < sl.Items.Count; j++)
                    members.Add(sl.Items[j]);
            else
                members.Add(list.Items[i]);

        var pendingAttrs = new List<AttributeDecl>();
        var seenLet = false;
        foreach (var member in members)
            if (IsAttributeForm(member))
            {
                pendingAttrs.Add(ParseAttributeDecl((SExpr.SList)member));
            }
            else if (member is SExpr.BracketList)
            {
                if (pendingAttrs.Count > 0)
                {
                    diagnostics.Error(
                        "Attributes cannot be applied to fields",
                        pendingAttrs[0].Span
                    );
                    pendingAttrs.Clear();
                }

                fields.Add(ParseFieldDecl(member));
            }
            else if (
                member is SExpr.SList memberList
                && memberList.Items.Count >= 1
                && memberList.Items[0] is SExpr.Atom ctorAtom
                && ctorAtom.Text == "constructor"
            )
            {
                if (constructorDecl is not null)
                {
                    diagnostics.Error("Class cannot have multiple constructors", memberList.Span);
                    continue;
                }

                constructorDecl = ParseConstructorDecl(memberList);
            }
            else if (member is SExpr.SList memberSList)
            {
                // Track 'let' forms in class body — they indicate define-async is nested
                // inside a let (which is not supported).
                if (
                    memberSList.Items.Count >= 1
                    && memberSList.Items[0] is SExpr.Atom letAtom
                    && letAtom.Text == "let"
                )
                {
                    seenLet = true;
                }

                // Check for define-async inside let body (detected by seeing a let before
                // the define-async in the flattened class members). This pattern is not
                // supported because define-async creates a class method, not a local binding.
                if (
                    seenLet
                    && memberSList.Items.Count >= 1
                    && memberSList.Items[0] is SExpr.Atom headAtom
                    && headAtom.Text == "define-async"
                )
                {
                    diagnostics.Error(
                        "'define-async' is not supported inside 'let' bodies. "
                            + "Top-level 'define-async' (at module or class level) is supported. "
                            + "Restructure your code to define async functions at the top level.",
                        memberSList.Span
                    );
                    continue;
                }

                var method = ParseObjectMethod(memberSList);
                if (method is not null)
                {
                    if (pendingAttrs.Count > 0)
                    {
                        method = method with { Attributes = pendingAttrs.ToList() };
                        pendingAttrs.Clear();
                    }

                    methods.Add(method);
                }
            }
            else
            {
                diagnostics.Error(
                    "Class member must be a field [name : Type], "
                        + "method (define (Name [params...]) : RetType body), "
                        + "or async method (define-async (Name [params...]) : RetType body)",
                    member.Span
                );
            }

        if (pendingAttrs.Count > 0)
            diagnostics.Error(
                "Attribute(s) with no target method in class body",
                pendingAttrs[0].Span
            );

        return new AstNode.ClassDecl(
            name,
            typeParams,
            interfaceNames,
            fields,
            methods,
            list.Span,
            isOpen,
            baseClassName,
            constructorDecl,
            TypeParamConstraints: classConstraints,
            NameSpan: nameSpan
        );
    }

    private ConstructorDecl ParseConstructorDecl(SExpr.SList list)
    {
        // (constructor [param : Type] ... (super args...) (set! field expr) ... body-exprs...)
        var idx = 1;
        var parms = new List<Param>();

        // Parse parameters (bracket lists)
        while (idx < list.Items.Count && list.Items[idx] is SExpr.BracketList)
        {
            var bracket = (SExpr.BracketList)list.Items[idx];
            if (
                bracket.Items.Count >= 3
                && bracket.Items[1] is SExpr.Atom colonCheck
                && colonCheck.Text == ":"
            )
            {
                var nameAtom = (SExpr.Atom)bracket.Items[0];
                var paramType = ParseTypeExpr(bracket.Items[2]);
                parms.Add(
                    new Param(nameAtom.Text, paramType, bracket.Span, NameSpan: nameAtom.Span)
                );
            }
            else if (bracket.Items.Count == 1)
            {
                var nameAtom = (SExpr.Atom)bracket.Items[0];
                parms.Add(new Param(nameAtom.Text, null, bracket.Span, NameSpan: nameAtom.Span));
            }

            idx++;
        }

        // Parse body: (super args...) calls, (set! field expr) forms, and other expressions
        List<AstNode>? superArgs = null;
        var fieldSets = new List<(string, AstNode)>();
        var bodyExprs = new List<AstNode>();

        while (idx < list.Items.Count)
        {
            var item = list.Items[idx];
            if (item is SExpr.SList sl && sl.Items.Count >= 1 && sl.Items[0] is SExpr.Atom head)
            {
                if (head.Text == "super")
                {
                    if (superArgs is not null)
                    {
                        diagnostics.Error(
                            "Constructor cannot have multiple (super ...) calls",
                            sl.Span
                        );
                    }
                    else
                    {
                        superArgs = new List<AstNode>();
                        for (var i = 1; i < sl.Items.Count; i++)
                            superArgs.Add(Build(sl.Items[i]));
                    }
                }
                else if (
                    head.Text == "set!"
                    && sl.Items.Count == 3
                    && sl.Items[1] is SExpr.Atom fieldAtom
                )
                {
                    fieldSets.Add((fieldAtom.Text, Build(sl.Items[2])));
                }
                else
                {
                    bodyExprs.Add(Build(item));
                }
            }
            else
            {
                bodyExprs.Add(Build(item));
            }

            idx++;
        }

        return new ConstructorDecl(parms, superArgs, fieldSets, bodyExprs, list.Span);
    }

    private AstNode BuildInterface(SExpr.SList list)
    {
        // (define-interface Name (Method [params...] : RetType) ...)
        // (define-interface (Name a b) ...)
        // (define-interface Name : IFoo IBar (Method ...) ...)
        if (list.Items.Count < 2)
        {
            diagnostics.Error("'define-interface' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        SourceSpan nameSpan;
        var typeParams = new List<string>();
        int membersStart;

        if (list.Items[1] is SExpr.SList nameList)
        {
            // Generic: (define-interface (IContainer a) ...)
            var nameAtom = (SExpr.Atom)nameList.Items[0];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            for (var i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            membersStart = 2;
        }
        else
        {
            var nameAtom = (SExpr.Atom)list.Items[1];
            name = nameAtom.Text;
            nameSpan = nameAtom.Span;
            membersStart = 2;
        }

        // Parse optional base interface list: : IFoo IBar
        var baseInterfaceNames = new List<string>();
        if (
            membersStart < list.Items.Count
            && list.Items[membersStart] is SExpr.Atom colonAtom
            && colonAtom.Text == ":"
        )
        {
            membersStart++;
            while (
                membersStart < list.Items.Count
                && list.Items[membersStart] is SExpr.Atom ifaceAtom
                && ifaceAtom.Text != ":"
                && char.IsUpper(ifaceAtom.Text[0])
            )
            {
                baseInterfaceNames.Add(ifaceAtom.Text);
                membersStart++;
            }
        }

        // Look for :where clause
        Dictionary<string, GenericConstraintKind>? ifaceConstraints = null;
        if (
            membersStart + 1 < list.Items.Count
            && list.Items[membersStart] is SExpr.Atom ifaceWhereColon
            && ifaceWhereColon.Text == ":"
            && list.Items[membersStart + 1] is SExpr.Atom ifaceWhereKw
            && ifaceWhereKw.Text == "where"
        )
        {
            membersStart += 2;
            if (membersStart < list.Items.Count)
            {
                ifaceConstraints = ParseWhereClause(list.Items[membersStart]);
                membersStart++;
            }
        }

        var methods = new List<InterfaceMethodSignature>();

        for (var i = membersStart; i < list.Items.Count; i++)
        {
            var member = list.Items[i];
            if (member is SExpr.BracketList)
            {
                diagnostics.Error("Interfaces cannot have fields", member.Span);
            }
            else if (member is SExpr.SList)
            {
                var method = ParseInterfaceMethodSignature(member);
                if (method is not null)
                    methods.Add(method);
            }
            else
            {
                diagnostics.Error(
                    "Interface member must be a method signature (Name [params...] : RetType)",
                    member.Span
                );
            }
        }

        return new AstNode.InterfaceDecl(
            name,
            typeParams,
            baseInterfaceNames,
            methods,
            list.Span,
            TypeParamConstraints: ifaceConstraints,
            NameSpan: nameSpan
        );
    }

    private InterfaceMethodSignature? ParseInterfaceMethodSignature(SExpr expr)
    {
        if (expr is SExpr.SList methodList && methodList.Items.Count >= 2)
        {
            var methodName = ((SExpr.Atom)methodList.Items[0]).Text;
            var parms = new List<Param>();
            var idx = 1;

            // Parse parameters (bracket lists)
            if (
                idx < methodList.Items.Count
                && methodList.Items[idx] is SExpr.BracketList emptyBracket
                && emptyBracket.Items.Count == 0
            )
                idx++;
            else
                while (idx < methodList.Items.Count && methodList.Items[idx] is SExpr.BracketList)
                {
                    parms.Add(ParseParam(methodList.Items[idx]));
                    idx++;
                }

            // Parse required return type annotation: : RetType
            if (
                idx < methodList.Items.Count
                && methodList.Items[idx] is SExpr.Atom colon
                && colon.Text == ":"
            )
            {
                idx++;
                if (idx < methodList.Items.Count)
                {
                    var returnType = ParseTypeExpr(methodList.Items[idx]);
                    idx++;

                    if (idx < methodList.Items.Count)
                        diagnostics.Error("Interface methods cannot have a body", methodList.Span);

                    return new InterfaceMethodSignature(
                        methodName,
                        parms,
                        returnType,
                        methodList.Span
                    );
                }
            }

            diagnostics.Error(
                "Interface method requires a return type annotation",
                methodList.Span
            );
            return null;
        }

        diagnostics.Error("Method signature must be (Name [params...] : RetType)", expr.Span);
        return null;
    }

    private AstNode BuildBegin(SExpr.SList list) =>
        // (begin e1 e2 ... en) — drop the `begin` keyword and sequence the operands.
        BuildExprSequence(list.Items.Skip(1).ToList(), list.Span);

    // Sequences a run of body expressions. Plain expressions become nested discarded `let`
    // bindings: e1 e2 ... en → (let [_ e1] (let [_ e2] ... en)). A run of consecutive `define`
    // forms instead becomes a single `letrec` scoping over everything after it — that is what
    // makes a nested definition able to recurse, to call its neighbours in the same run, and to
    // close over the enclosing function's parameters, all of which `letrec` lowering already
    // handles. Shared by `begin`, by the `let`/`let*`/`letrec`/`use`/`use*` bodies, and by the
    // implicit multi-expression bodies of `define`, `lambda`, and `define-async`.
    // Attribute (`@`) forms are legal in a body and bind to the following definition.
    private AstNode BuildExprSequence(IReadOnlyList<SExpr> exprs, SourceSpan span)
    {
        // The name map is per body, so a definition in a nested body (a `let` inside this one)
        // starts fresh — shadowing across nesting levels is ordinary lexical scoping, not the
        // accident the warning is about.
        return exprs.Count == 0
            ? new AstNode.UnitLit(span)
            : BuildBodyFrom(
                exprs,
                0,
                span,
                new Dictionary<string, SourceSpan>(StringComparer.Ordinal)
            );
    }

    /// <summary>Builds <c>exprs[start..]</c> as a body: every form but the last is evaluated for
    ///     effect, the last one is the body's value, and a run of definitions binds over the
    ///     rest.</summary>
    /// <param name="definedInBody">Names already defined by an earlier group in this same body,
    ///     mapped to where they were defined, so a later group redefining one can be reported.</param>
    private AstNode BuildBodyFrom(
        IReadOnlyList<SExpr> exprs,
        int start,
        SourceSpan span,
        Dictionary<string, SourceSpan> definedInBody
    )
    {
        var pendingAttrs = new List<AttributeDecl>();
        var i = start;
        while (i < exprs.Count && IsAttributeForm(exprs[i]))
            pendingAttrs.Add(ParseAttributeDecl((SExpr.SList)exprs[i++]));

        if (i == exprs.Count)
        {
            if (pendingAttrs.Count > 0)
                diagnostics.Error("Attribute(s) with no target declaration", pendingAttrs[0].Span);
            return new AstNode.UnitLit(span);
        }

        if (IsBodyDefinition(exprs[i]))
            return BuildBodyDefinitionGroup(exprs, i, span, pendingAttrs, definedInBody);

        RejectDeclarationInBody(exprs[i]);

        var node = Build(exprs[i]);
        if (pendingAttrs.Count > 0)
            node = ApplyPendingAttributes(node, pendingAttrs);

        return i == exprs.Count - 1
            ? node
            : new AstNode.Let("_", node, BuildBodyFrom(exprs, i + 1, span, definedInBody), span);
    }

    /// <summary>A body form that introduces a name for the rest of the body, i.e. a nested
    ///     <c>define</c>. Inside a class or <c>object</c> member list a <c>define</c> means a
    ///     method instead, and that list never reaches here.</summary>
    private static bool IsBodyDefinition(SExpr expr)
    {
        return expr is SExpr.SList { Items: [SExpr.Atom { Text: "define" }, ..] };
    }

    /// <summary>
    ///     Reports the declaration forms that build fine in a body but have nowhere to go: a type
    ///     declaration lowers to a nested expression neither backend can place, and an async
    ///     function cannot be lifted out of a body. Only <c>define</c> is a real nested definition.
    /// </summary>
    private void RejectDeclarationInBody(SExpr expr)
    {
        if (expr is not SExpr.SList { Items: [SExpr.Atom head, ..] })
            return;

        switch (head.Text)
        {
            case "define-async":
                diagnostics.Error(
                    "'define-async' is not supported inside a body. Move it to the top level "
                        + "(module or class level), which is where async functions are emitted",
                    expr.Span
                );
                break;

            case "define-record"
            or "define-struct"
            or "define-union"
            or "define-class"
            or "define-interface"
            or "define-type-alias":
                diagnostics.Error(
                    $"'{head.Text}' is only allowed at the top level, not inside a body. Types "
                        + "are emitted as CLR types and have no enclosing scope to live in",
                    expr.Span
                );
                break;
        }
    }

    /// <summary>
    ///     Turns the maximal run of <c>define</c> forms starting at <paramref name="start" /> into
    ///     one <c>letrec</c> group whose body is the rest of the sequence. Grouping the whole run
    ///     rather than one define at a time is what lets neighbouring definitions call each other.
    /// </summary>
    private AstNode BuildBodyDefinitionGroup(
        IReadOnlyList<SExpr> exprs,
        int start,
        SourceSpan span,
        List<AttributeDecl> pendingAttrs,
        Dictionary<string, SourceSpan> definedInBody
    )
    {
        if (pendingAttrs.Count > 0)
            diagnostics.Error(
                "Attributes cannot be applied to a definition inside a body: a nested definition "
                    + "is compiled to a local function and has no metadata target. Move it to the "
                    + "top level to attach attributes",
                pendingAttrs[0].Span
            );

        var bindings = new List<AstNode.LetrecBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = start;
        for (; i < exprs.Count && IsBodyDefinition(exprs[i]); i++)
        {
            var binding = BuildBodyDefinition(exprs[i]);
            if (binding is null)
                continue;
            if (!seen.Add(binding.Name))
            {
                diagnostics.Error(
                    $"'{binding.Name}' is already defined in this body",
                    exprs[i].Span
                );
                continue;
            }

            // A redefinition in a *later* group of the same body is legal — each group is its own
            // `letrec`, so this one simply shadows the earlier binding for the rest of the body.
            // It is almost always a mistake, though: the two definitions look like one repeated
            // declaration, but the forms between them still see the earlier one.
            //
            // Same opt-outs as UnusedBindingAnalyzer, the codebase's other lint warning: a binding
            // with no NameSpan came from a macro expansion or a desugar, where the author cannot
            // act on the warning, and a `_`-prefixed name is an explicit "I meant this".
            if (binding.NameSpan.Length > 0 && !binding.Name.StartsWith('_'))
            {
                if (definedInBody.TryGetValue(binding.Name, out var earlier))
                    diagnostics.Warning(
                        $"'{binding.Name}' shadows an earlier definition in this body. Forms "
                            + "between the two still refer to the earlier one; everything after "
                            + "this point refers to this one. Rename one of them if that was not "
                            + "intended",
                        binding.NameSpan,
                        related:
                        [
                            new DiagnosticRelatedInfo(
                                earlier,
                                $"earlier definition of '{binding.Name}'"
                            ),
                        ]
                    );
                definedInBody[binding.Name] = binding.NameSpan;
            }

            bindings.Add(binding);
        }

        if (i == exprs.Count)
            diagnostics.Error(
                "A body cannot end with a definition — it would have no result value. Add an "
                    + "expression after it",
                exprs[^1].Span
            );

        // Every binding failed to parse; the errors are already reported, so keep the tree simple.
        if (bindings.Count == 0)
            return i == exprs.Count
                ? new AstNode.UnitLit(span)
                : BuildBodyFrom(exprs, i, span, definedInBody);

        var body =
            i == exprs.Count
                ? new AstNode.UnitLit(span)
                : BuildBodyFrom(exprs, i, span, definedInBody);
        LetrecInitializationChecker.Check(bindings, diagnostics, "define");
        return new AstNode.Letrec(bindings, body, span);
    }

    /// <summary>
    ///     One nested <c>define</c> as a <c>letrec</c> binding, or null when it was rejected.
    ///     A function definition must produce a literal <see cref="AstNode.Lambda" />: that is the
    ///     shape <see cref="LetrecInitializationChecker" /> exempts from its ordering rule, and
    ///     mutual recursion between neighbours depends on the exemption.
    /// </summary>
    private AstNode.LetrecBinding? BuildBodyDefinition(SExpr expr)
    {
        switch (Build(expr))
        {
            case AstNode.Define d:
                if (d.TypeParamConstraints is { Count: > 0 })
                    diagnostics.Error(
                        "':where' constraints are not supported on a definition inside a body. "
                            + "Constraints on the enclosing function's type parameters already "
                            + "apply; move the definition to the top level to add its own",
                        d.Span
                    );
                return new AstNode.LetrecBinding(
                    d.FnName,
                    new AstNode.Lambda(d.Params, d.ReturnTypeAnnotation, d.Body, d.Span),
                    NameSpan: d.NameSpan,
                    AllowsUnloopedRecursion: d.AllowsUnloopedRecursion
                );

            case AstNode.DefineValue dv:
                return new AstNode.LetrecBinding(dv.VarName, dv.Value, NameSpan: dv.NameSpan);

            default:
                // BuildDefine already reported why it could not build a definition.
                return null;
        }
    }

    private AstNode BuildNew(SExpr.SList list)
    {
        // (new TypeName args...) or (new (GenericType Arg1 Arg2) args...)
        if (list.Items.Count < 2)
        {
            diagnostics.Error("'new' requires a type name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string typeName;
        IReadOnlyList<ZType> typeArgs;

        if (list.Items[1] is SExpr.Atom typeAtom)
        {
            // Simple: (new Foo args...)
            typeName = typeAtom.Text;
            typeArgs = [];
        }
        else if (list.Items[1] is SExpr.SList)
        {
            // Generic: (new (Dictionary String Int) args...)
            var parsedType = ParseTypeExpr(list.Items[1]);
            if (parsedType is ZType.ZNamedType nt)
            {
                typeName = nt.Name;
                typeArgs = nt.TypeArgs;
            }
            else if (parsedType is ZType.ZNullableType { Inner: var inner })
            {
                typeName = "System.Nullable";
                typeArgs = [inner];
            }
            else
            {
                diagnostics.Error("'new' type expression must be a named type", list.Items[1].Span);
                return new AstNode.UnitLit(list.Span);
            }
        }
        else
        {
            diagnostics.Error(
                "'new' type name must be an identifier or generic type expression",
                list.Items[1].Span
            );
            return new AstNode.UnitLit(list.Span);
        }

        var args = new List<AstNode>();
        for (var i = 2; i < list.Items.Count; i++)
            args.Add(Build(list.Items[i]));

        return new AstNode.ClrNew(typeName, typeArgs, args, list.Span);
    }

    private AstNode BuildTypeOf(SExpr.SList list)
    {
        // (typeof TypeExpr) — produces a System.Type value
        if (list.Items.Count != 2)
        {
            diagnostics.Error("'typeof' requires exactly one type expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var type = ParseTypeExpr(list.Items[1]);
        return new AstNode.TypeOf(type, list.Span);
    }

    private AstNode BuildDefineAsync(SExpr.SList list)
    {
        // (define-async (name [params...]) : ReturnType body)
        bool allowsUnloopedRecursion;
        (allowsUnloopedRecursion, list) = StripRecursiveMarker(list, "define-async");

        if (list.Items.Count < 3)
        {
            diagnostics.Error("'define-async' requires a signature and body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.SList sig || sig.Items.Count == 0)
        {
            diagnostics.Error("'define-async' requires a function signature", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var fnNameAtom = (SExpr.Atom)sig.Items[0];
        var fnName = fnNameAtom.Text;
        var parms = new List<Param>();

        for (var i = 1; i < sig.Items.Count; i++)
            parms.Add(ParseParam(sig.Items[i]));
        ValidateVariadicParams(parms, list.Span);

        ZType? returnType = null;
        var bodyStart = 2;

        if (
            bodyStart < list.Items.Count
            && list.Items[bodyStart] is SExpr.Atom colon
            && colon.Text == ":"
        )
        {
            bodyStart++;
            if (bodyStart < list.Items.Count)
            {
                returnType = ParseTypeExpr(list.Items[bodyStart]);
                bodyStart++;
            }
        }

        // Look for :where clause
        Dictionary<string, GenericConstraintKind>? typeParamConstraints = null;
        if (
            bodyStart + 1 < list.Items.Count
            && list.Items[bodyStart] is SExpr.Atom whereColon2
            && whereColon2.Text == ":"
            && list.Items[bodyStart + 1] is SExpr.Atom whereKw2
            && whereKw2.Text == "where"
        )
        {
            bodyStart += 2;
            if (bodyStart < list.Items.Count)
            {
                typeParamConstraints = ParseWhereClause(list.Items[bodyStart]);
                bodyStart++;
            }
        }

        if (bodyStart >= list.Items.Count)
        {
            diagnostics.Error("Async function definition requires a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var body = BuildExprSequence(list.Items.Skip(bodyStart).ToList(), list.Span);
        return new AstNode.DefineAsync(
            fnName,
            parms,
            returnType,
            body,
            list.Span,
            TypeParamConstraints: typeParamConstraints,
            NameSpan: fnNameAtom.Span,
            AllowsUnloopedRecursion: allowsUnloopedRecursion
        );
    }

    private AstNode BuildAwait(SExpr.SList list)
    {
        // (await expr)
        if (list.Items.Count != 2)
        {
            diagnostics.Error("'await' requires exactly one expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        return new AstNode.Await(Build(list.Items[1]), list.Span);
    }

    private AstNode BuildApply(SExpr.SList list)
    {
        var func = Build(list.Items[0]);
        var args = new List<AstNode>();
        for (var i = 1; i < list.Items.Count; i++)
            args.Add(Build(list.Items[i]));

        // Variadic operators are normalized here (per BuiltinRegistry's FoldKind) so the
        // type system, IR lowering, and codegen only ever see arity-2 (or arity-1 for
        // unary negation/inversion) operator calls.
        if (
            func is AstNode.Name name
            && BuiltinRegistry.ByName.TryGetValue(name.Value, out var builtin)
        )
            switch (builtin.Fold)
            {
                // (+ a b c) / (* a b c) / (string-append a b c): single arg returns
                // unchanged (Scheme identity).
                case FoldKind.LeftFoldIdentity:
                    return ExpandLeftFold(name.Value, args, list.Span, allowSingle: true);
                // (- a b c) / (/ a b c): single arg passes through for unary neg/invert.
                case FoldKind.ArithUnary:
                    return ExpandLeftFold(name.Value, args, list.Span, allowSingle: false);
                // (% a b c) → (% (% a b) c): left-assoc, no single-arg form.
                case FoldKind.ArithStrict:
                    return ExpandLeftFold(name.Value, args, list.Span, allowSingle: false);
                case FoldKind.CmpChain:
                    return ExpandComparisonChain(name.Value, args, list.Span);
                case FoldKind.NeqAllDistinct:
                    return ExpandNeqAllDistinct(args, list.Span);
                case FoldKind.BoolFold:
                    return ExpandBoolFold(name.Value, args, list.Span);
            }

        return new AstNode.Apply(func, args, list.Span);
    }

    // (+ a b c d) → (+ (+ (+ a b) c) d). For LeftFoldIdentity (`+`, `*`, `string-append`),
    // single-arg returns the arg unchanged (Scheme identity convention). For ArithUnary
    // (`-`, `/`), single-arg flows through unchanged so the type inferer and IR
    // lowering can lower it to unary negation / inversion (the literal `0` or `1`
    // would mistype against `Float`, so the rewrite has to happen later).
    private AstNode ExpandLeftFold(string op, List<AstNode> args, SourceSpan span, bool allowSingle)
    {
        if (args.Count == 0)
        {
            diagnostics.Error($"'{op}' requires at least 1 argument", span);
            return new AstNode.UnitLit(span);
        }

        if (args.Count == 1)
        {
            if (allowSingle)
                return args[0];
            // Pass single-arg `-`/`/` straight through; downstream stages handle it.
            return new AstNode.Apply(new AstNode.Name(op, span), args, span);
        }

        if (args.Count == 2)
            return new AstNode.Apply(new AstNode.Name(op, span), args, span);

        var acc = new AstNode.Apply(new AstNode.Name(op, span), [args[0], args[1]], span);
        for (var i = 2; i < args.Count; i++)
            acc = new AstNode.Apply(new AstNode.Name(op, span), [acc, args[i]], span);
        return acc;
    }

    // (< a b c d) → (let [$cmp_0 b] (let [$cmp_1 c] (and (< a $cmp_0) (and (< $cmp_0 $cmp_1) (< $cmp_1 d)))))
    // Middle args that are pure (Name/literal) skip the let-binding so the IR
    // stays readable and the type inferer doesn't generate extra fresh vars.
    private AstNode ExpandComparisonChain(string op, List<AstNode> args, SourceSpan span)
    {
        if (args.Count < 2)
        {
            diagnostics.Error($"'{op}' requires at least 2 arguments", span);
            return new AstNode.UnitLit(span);
        }

        if (args.Count == 2)
            return new AstNode.Apply(new AstNode.Name(op, span), args, span);

        // Bind non-pure middle args (indices 1..n-2). First and last appear once.
        var bindings = new List<(string Name, AstNode Value)>();
        var operands = new List<AstNode> { args[0] };
        for (var i = 1; i < args.Count - 1; i++)
            if (IsPureRepeatable(args[i]))
            {
                operands.Add(args[i]);
            }
            else
            {
                var fresh = FreshName("cmp");
                bindings.Add((fresh, args[i]));
                operands.Add(new AstNode.Name(fresh, span));
            }

        operands.Add(args[^1]);

        // Right-fold AND chain: (and (< a b) (and (< b c) (< c d)))
        AstNode chain = new AstNode.Apply(
            new AstNode.Name(op, span),
            [operands[^2], operands[^1]],
            span
        );
        for (var i = operands.Count - 3; i >= 0; i--)
        {
            var pair = new AstNode.Apply(
                new AstNode.Name(op, span),
                [operands[i], operands[i + 1]],
                span
            );
            chain = new AstNode.Apply(new AstNode.Name("and", span), [pair, chain], span);
        }

        // Wrap in nested Lets (innermost binding wraps the chain).
        for (var i = bindings.Count - 1; i >= 0; i--)
            chain = new AstNode.Let(bindings[i].Name, bindings[i].Value, chain, span);
        return chain;
    }

    // (!= a b c) → all-distinct: AND of every (!= ai aj) pair with i<j.
    // Each non-pure arg is bound exactly once because it appears in N-1 pairs.
    private AstNode ExpandNeqAllDistinct(List<AstNode> args, SourceSpan span)
    {
        if (args.Count < 2)
        {
            diagnostics.Error("'!=' requires at least 2 arguments", span);
            return new AstNode.UnitLit(span);
        }

        if (args.Count == 2)
            return new AstNode.Apply(new AstNode.Name("!=", span), args, span);

        var bindings = new List<(string Name, AstNode Value)>();
        var operands = new List<AstNode>();
        foreach (var arg in args)
            if (IsPureRepeatable(arg))
            {
                operands.Add(arg);
            }
            else
            {
                var fresh = FreshName("neq");
                bindings.Add((fresh, arg));
                operands.Add(new AstNode.Name(fresh, span));
            }

        // Build all i<j pairs as (!= ai aj), AND them together (right-fold).
        var pairs = new List<AstNode>();
        for (var i = 0; i < operands.Count; i++)
        for (var j = i + 1; j < operands.Count; j++)
            pairs.Add(
                new AstNode.Apply(new AstNode.Name("!=", span), [operands[i], operands[j]], span)
            );

        var chain = pairs[^1];
        for (var i = pairs.Count - 2; i >= 0; i--)
            chain = new AstNode.Apply(new AstNode.Name("and", span), [pairs[i], chain], span);

        for (var i = bindings.Count - 1; i >= 0; i--)
            chain = new AstNode.Let(bindings[i].Name, bindings[i].Value, chain, span);
        return chain;
    }

    // (and a b c) → (and a (and b c)). Right-fold preserves the short-circuit
    // shape that IlEmitter.EmitShortCircuit already produces for binary and/or.
    private AstNode ExpandBoolFold(string op, List<AstNode> args, SourceSpan span)
    {
        if (args.Count == 0)
        {
            diagnostics.Error($"'{op}' requires at least 1 argument", span);
            return new AstNode.UnitLit(span);
        }

        if (args.Count == 1)
            return args[0];
        if (args.Count == 2)
            return new AstNode.Apply(new AstNode.Name(op, span), args, span);

        var chain = new AstNode.Apply(new AstNode.Name(op, span), [args[^2], args[^1]], span);
        for (var i = args.Count - 3; i >= 0; i--)
            chain = new AstNode.Apply(new AstNode.Name(op, span), [args[i], chain], span);
        return chain;
    }

    private static bool IsPureRepeatable(AstNode node)
    {
        return node
            is AstNode.Name
                or AstNode.IntLit
                or AstNode.FloatLit
                or AstNode.BoolLit
                or AstNode.StringLit
                or AstNode.UnitLit
                or AstNode.NullLit;
    }

    private Param ParseParam(SExpr expr)
    {
        // [name : Type] or [(@ Attr) name : Type] or just name
        if (expr is SExpr.BracketList bracket)
        {
            // Check for leading attribute(s) inside bracket list
            var attrs = new List<AttributeDecl>();
            var offset = 0;
            while (offset < bracket.Items.Count && IsAttributeForm(bracket.Items[offset]))
            {
                attrs.Add(ParseAttributeDecl((SExpr.SList)bracket.Items[offset]));
                offset++;
            }

            var remaining = bracket.Items.Skip(offset).ToList();
            IReadOnlyList<AttributeDecl>? attrList = attrs.Count > 0 ? attrs : null;

            if (remaining.Count >= 3 && remaining[1] is SExpr.Atom colon && colon.Text == ":")
            {
                var nameAtom = (SExpr.Atom)remaining[0];
                var type = ParseTypeExpr(remaining[2]);
                // Check for trailing ... to mark variadic parameter: [name : Type ...]
                var isVariadic =
                    remaining.Count >= 4 && remaining[3] is SExpr.Atom dots && dots.Text == "...";
                return new Param(
                    nameAtom.Text,
                    type,
                    bracket.Span,
                    attrList,
                    isVariadic,
                    nameAtom.Span
                );
            }

            if (
                remaining.Count >= 2
                && remaining.Count <= 2
                && remaining[0] is SExpr.Atom untyped
                && remaining[1] is SExpr.Atom dotsUntyped
                && dotsUntyped.Text == "..."
            )
                return new Param(untyped.Text, null, bracket.Span, attrList, true, untyped.Span);

            if (remaining.Count == 1 && remaining[0] is SExpr.Atom single)
                return new Param(single.Text, null, bracket.Span, attrList, false, single.Span);

            diagnostics.Error("Invalid parameter syntax", bracket.Span);
            return new Param("_", null, bracket.Span);
        }

        if (expr is SExpr.Atom atom)
            return new Param(atom.Text, null, atom.Span, NameSpan: atom.Span);

        diagnostics.Error("Invalid parameter", expr.Span);
        return new Param("_", null, expr.Span);
    }

    private void ValidateVariadicParams(List<Param> parms, SourceSpan span)
    {
        var variadicCount = 0;
        for (var i = 0; i < parms.Count; i++)
        {
            if (!parms[i].IsVariadic)
                continue;
            variadicCount++;
            if (variadicCount > 1)
                diagnostics.Error("Only one variadic parameter is allowed", parms[i].Span);
            if (i != parms.Count - 1)
                diagnostics.Error("Variadic parameter must be the last parameter", parms[i].Span);
        }
    }

    private FieldDecl ParseFieldDecl(SExpr expr)
    {
        if (expr is SExpr.BracketList bracket)
        {
            // Check for leading attribute(s) inside bracket list
            var attrs = new List<AttributeDecl>();
            var offset = 0;
            while (offset < bracket.Items.Count && IsAttributeForm(bracket.Items[offset]))
            {
                attrs.Add(ParseAttributeDecl((SExpr.SList)bracket.Items[offset]));
                offset++;
            }

            var remaining = bracket.Items.Skip(offset).ToList();
            IReadOnlyList<AttributeDecl>? attrList = attrs.Count > 0 ? attrs : null;

            if (remaining.Count >= 3 && remaining[1] is SExpr.Atom colon && colon.Text == ":")
            {
                var name = ((SExpr.Atom)remaining[0]).Text;
                var type = ParseTypeExpr(remaining[2]);
                var isMutable =
                    remaining.Count >= 4 && remaining[3] is SExpr.Atom { Text: "#:mutable" };
                var isInit = remaining.Count >= 4 && remaining[3] is SExpr.Atom { Text: "#:init" };
                return new FieldDecl(name, type, bracket.Span, attrList, isMutable, isInit);
            }
        }

        diagnostics.Error("Field must be [name : Type]", expr.Span);
        return new FieldDecl("_", ZType.Unit, expr.Span);
    }

    private Pattern ParsePattern(SExpr expr)
    {
        return expr switch
        {
            SExpr.Atom { Text: "_" } a => new Pattern.Wildcard(a.Span),
            SExpr.Atom { Kind: TokenKind.IntLit } a => new Pattern.Literal(
                int.Parse(a.Text),
                a.Span
            ),
            SExpr.Atom { Kind: TokenKind.FloatLit } a => new Pattern.Literal(
                ParseFloat(a.Text),
                a.Span
            ),
            SExpr.Atom { Kind: TokenKind.BoolLit } a => new Pattern.Literal(a.Text == "#t", a.Span),
            SExpr.Atom { Kind: TokenKind.StringLit } a => new Pattern.Literal(a.Text, a.Span),
            SExpr.Atom a when a.Text.Length > 0 && char.IsUpper(a.Text[0]) =>
                new Pattern.Constructor(a.Text, [], a.Span),
            SExpr.Atom a => new Pattern.Variable(a.Text, a.Span),
            SExpr.SList list
                when list.Items.Count >= 3 && list.Items[0] is SExpr.Atom { Text: "values" } =>
                ParseTuplePattern(list),
            // A quoted datum pattern, e.g. 'foo — desugared to (quote foo).
            SExpr.SList { Items: [SExpr.Atom { Text: "quote" }, _] } list => ParseQuotePattern(
                list
            ),
            SExpr.SList list when list.Items.Count >= 1 => ParseConstructorPattern(list),
            _ => ReportBadPattern(expr),
        };
    }

    private Pattern ParseQuotePattern(SExpr.SList list)
    {
        if (list.Items[1] is SExpr.Atom atom)
            return atom.Kind switch
            {
                TokenKind.IntLit => new Pattern.Literal(int.Parse(atom.Text), atom.Span),
                TokenKind.FloatLit => new Pattern.Literal(ParseFloat(atom.Text), atom.Span),
                TokenKind.BoolLit => new Pattern.Literal(atom.Text == "#t", atom.Span),
                TokenKind.StringLit => new Pattern.Literal(atom.Text, atom.Span),
                // A quoted symbol: store an interned ZSymbol so it is distinguishable from a
                // string literal pattern (the pattern compiler keys on the value's CLR type).
                _ => new Pattern.Literal(
                    global::ZScheme.Runtime.ZSymbol.Intern(atom.Text),
                    atom.Span
                ),
            };

        diagnostics.Error(
            "Quoted lists are not yet supported in patterns; only quoted symbols and literals are allowed",
            list.Items[1].Span
        );
        return new Pattern.Wildcard(list.Span);
    }

    private Pattern ParseConstructorPattern(SExpr.SList list)
    {
        var name = ((SExpr.Atom)list.Items[0]).Text;
        var fields = new List<Pattern>();
        for (var i = 1; i < list.Items.Count; i++)
            fields.Add(ParsePattern(list.Items[i]));
        return new Pattern.Constructor(name, fields, list.Span);
    }

    private Pattern ParseTuplePattern(SExpr.SList list)
    {
        var elements = new List<Pattern>();
        for (var i = 1; i < list.Items.Count; i++)
            elements.Add(ParsePattern(list.Items[i]));
        return new Pattern.Tuple(elements, list.Span);
    }

    private Pattern ReportBadPattern(SExpr expr)
    {
        diagnostics.Error("Invalid pattern", expr.Span);
        return new Pattern.Wildcard(expr.Span);
    }

    public ZType ParseTypeExpr(SExpr expr)
    {
        return expr switch
        {
            SExpr.Atom a
                when a.Text.EndsWith('?') && a.Text.Length > 1 && !a.Text.StartsWith('^') =>
                new ZType.ZNullableType(
                    ParseTypeExpr(new SExpr.Atom(new Token(a.Kind, a.Text[..^1], a.Span)))
                ),
            SExpr.Atom a when a.Text.StartsWith('^') && a.Text.Length > 1 => new ZType.ZNamedType(
                a.Text,
                []
            ),
            // A primitive answers to its keyword and to its CLR full name alike, so
            // `System.Int32` and `Int` land on one ZType instead of two that never unify.
            SExpr.Atom a => PrimitiveTypeNames.Lookup(a.Text) ?? new ZType.ZNamedType(a.Text, []),
            SExpr.SList list
                when list.Items.Count >= 2
                    && list.Items[0] is SExpr.Atom atom0
                    && atom0.Text == "delegate" => ParseDelegateType(list),
            SExpr.SList list when IsInfixFuncType(list) => ParseInfixFuncType(list),
            SExpr.SList list when list.Items.Count >= 3 && IsInfixTupleType(list) =>
                ParseInfixTupleType(list),
            SExpr.SList list when list.Items.Count >= 1 => ParseNamedType(list),
            _ => ReportInvalidTypeExpr(expr),
        };
    }

    private ZType ReportInvalidTypeExpr(SExpr expr)
    {
        diagnostics.Error("Invalid type expression", expr.Span);
        return ZType.Unit;
    }

    private static bool IsInfixFuncType(SExpr.SList list)
    {
        // (T1 T2 ... -> R) — exactly one '->' atom anywhere in the list
        var arrowCount = 0;
        foreach (var item in list.Items)
            if (item is SExpr.Atom { Text: "->" })
                arrowCount++;
        return arrowCount == 1;
    }

    private ZType ParseInfixFuncType(SExpr.SList list)
    {
        var arrowIdx = -1;
        for (var i = 0; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.Atom { Text: "->" })
            {
                arrowIdx = i;
                break;
            }

        if (arrowIdx == list.Items.Count - 1)
        {
            diagnostics.Error("Function type must have a return type after '->'", list.Span);
            return ZType.Unit;
        }

        if (arrowIdx < list.Items.Count - 2)
        {
            diagnostics.Error(
                "Function type must have exactly one return type after '->'",
                list.Span
            );
            return ZType.Unit;
        }

        var pars = new List<ZType>();
        for (var i = 0; i < arrowIdx; i++)
            pars.Add(ParseTypeExpr(list.Items[i]));
        var ret = ParseTypeExpr(list.Items[arrowIdx + 1]);
        return new ZType.ZFuncType(pars, ret);
    }

    private static bool IsInfixTupleType(SExpr.SList list)
    {
        // (T1 * T2 * T3 ...) — odd-indexed items must all be '*'
        if (list.Items.Count < 3 || list.Items.Count % 2 == 0)
            return false;
        for (var i = 1; i < list.Items.Count; i += 2)
            if (list.Items[i] is not SExpr.Atom { Text: "*" })
                return false;
        return true;
    }

    private ZType ParseInfixTupleType(SExpr.SList list)
    {
        // (Int * String * Bool) -> ZNamedType("ValueTuple", [Int, String, Bool])
        var elements = new List<ZType>();
        for (var i = 0; i < list.Items.Count; i += 2)
            elements.Add(ParseTypeExpr(list.Items[i]));

        if (elements.Count > 7)
        {
            diagnostics.Error("Tuple type supports at most 7 element types", list.Span);
            return ZType.Unit;
        }

        return new ZType.ZNamedType("ValueTuple", elements);
    }

    private ZType ParseDelegateType(SExpr.SList list)
    {
        // (delegate Fully.Qualified.DelegateTypeName)
        // The type name may be split across multiple atoms due to S-Expression tokenization
        // (e.g., (delegate System.Func<int,int>) → atoms: "System.Func<int", ",", "int>")
        // Note: ',' is tokenized as Unquote which creates an SList [unquote, nextExpr],
        // so we need to handle both atoms and these synthetic SLists.
        if (list.Items.Count < 2)
        {
            diagnostics.Error("(delegate ...) requires a delegate type name", list.Span);
            return ZType.Unit;
        }

        var parts = new List<string>();
        for (var i = 1; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (item is SExpr.Atom atom)
                parts.Add(atom.Text);
            else if (
                item is SExpr.SList subList
                && subList.Items.Count == 2
                && subList.Items[0] is SExpr.Atom { Text: "unquote" }
            )
            {
                // Handle unquote desugaring: [unquote, expr] → add comma + expr text
                // The comma was consumed by the unquote tokenization, so we add it back
                if (subList.Items[1] is SExpr.Atom innerAtom)
                    parts.Add("," + innerAtom.Text);
                else
                {
                    diagnostics.Error("(delegate ...) expects a type name as argument", item.Span);
                    return ZType.Unit;
                }
            }
            else
            {
                diagnostics.Error("(delegate ...) expects a type name as argument", item.Span);
                return ZType.Unit;
            }
        }

        var typeName = string.Join("", parts);

        if (string.IsNullOrEmpty(typeName))
        {
            diagnostics.Error("(delegate ...) expects a type name as argument", list.Span);
            return ZType.Unit;
        }

        return new ZType.ZDelegateType(typeName);
    }

    private ZType ParseNamedType(SExpr.SList list)
    {
        // (Result Int String) or (Option Int) etc.
        var name = ((SExpr.Atom)list.Items[0]).Text;
        var args = new List<ZType>();
        for (var i = 1; i < list.Items.Count; i++)
            args.Add(ParseTypeExpr(list.Items[i]));
        // Canonicalize: Nullable<T> is always represented as ZNullableType, never as a
        // named type — a named "System.Nullable" would not unify with T? elsewhere.
        if (name is "Nullable" or "System.Nullable" && args.Count == 1)
            return new ZType.ZNullableType(args[0]);
        return new ZType.ZNamedType(name, args);
    }
}

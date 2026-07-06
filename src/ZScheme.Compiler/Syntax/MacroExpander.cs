using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Syntax;

public sealed class MacroExpander(
    DiagnosticBag diagnostics,
    IMacroExpansionObserver? observer = null
)
{
    private const int MaxExpansionDepth = 100;

    private int _currentTopLevelIndex;
    private int _stepCounter;

    public List<SExpr> ExpandAll(List<SExpr> sexprs, MacroEnvironment env)
    {
        var result = new List<SExpr>();
        for (var i = 0; i < sexprs.Count; i++)
        {
            var sexpr = sexprs[i];
            if (IsDefineSyntax(sexpr))
            {
                var parser = new MacroParser(diagnostics);
                var def = parser.Parse((SExpr.SList)sexpr);
                if (def is not null)
                    env.Define(def.Name, def);
                // define-syntax forms are consumed, not emitted
            }
            else
            {
                _currentTopLevelIndex = i;
                var expanded = Expand(sexpr, env, 0, observer is null ? null : Cursor.Root);
                FlattenBegin(expanded, result);
            }
        }

        return result;
    }

    /// <summary>
    ///     Locates the expansion site within its top-level form so an observer can snapshot the
    ///     whole form around each rewrite. Each frame captures the parent's *live* output list
    ///     (which holds exactly <c>Index</c> already-expanded left siblings for the duration of
    ///     the child's recursion) plus the parent's original items for the not-yet-expanded
    ///     right siblings. Reconstruction copies eagerly at step time; frames must not outlive
    ///     the recursion they were created in. This is sound because expansion below top level
    ///     is always a 1-for-1 node replacement (begin-splicing happens only in
    ///     <see cref="ExpandAll" /> after <see cref="Expand" /> returns), so the hole index is
    ///     stable and one path addresses the hole in both the before and after snapshots.
    /// </summary>
    private abstract class Cursor
    {
        public static readonly Cursor Root = new RootCursor();

        public abstract SExpr Reconstruct(SExpr hole);

        protected abstract void AppendPath(List<int> path);

        public List<int> Path()
        {
            var path = new List<int>();
            AppendPath(path);
            return path;
        }

        private sealed class RootCursor : Cursor
        {
            public override SExpr Reconstruct(SExpr hole)
            {
                return hole;
            }

            protected override void AppendPath(List<int> path) { }
        }

        public sealed class Frame(
            bool isBracket,
            List<SExpr> expandedSoFar,
            IReadOnlyList<SExpr> original,
            int index,
            SourceSpan parentSpan,
            Cursor parent
        ) : Cursor
        {
            public override SExpr Reconstruct(SExpr hole)
            {
                var items = new List<SExpr>(original.Count);
                for (var i = 0; i < index; i++)
                    items.Add(expandedSoFar[i]);
                items.Add(hole);
                for (var i = index + 1; i < original.Count; i++)
                    items.Add(original[i]);
                SExpr rebuilt = isBracket
                    ? new SExpr.BracketList(items, parentSpan)
                    : new SExpr.SList(items, parentSpan);
                return parent.Reconstruct(rebuilt);
            }

            protected override void AppendPath(List<int> path)
            {
                if (parent is Frame parentFrame)
                    parentFrame.AppendPath(path);
                path.Add(index);
            }
        }
    }

    private static void FlattenBegin(SExpr expr, List<SExpr> output)
    {
        if (
            expr is SExpr.SList list
            && list.Items.Count >= 1
            && list.Items[0] is SExpr.Atom { Text: "begin" }
        )
            for (var i = 1; i < list.Items.Count; i++)
                FlattenBegin(list.Items[i], output);
        else
            output.Add(expr);
    }

    private SExpr Expand(SExpr expr, MacroEnvironment env, int depth, Cursor? cursor)
    {
        if (depth > MaxExpansionDepth)
        {
            observer?.OnDepthLimitExceeded(expr, depth);
            diagnostics.Error(
                "Macro expansion depth limit exceeded (possible infinite expansion)",
                expr.Span
            );
            return expr;
        }

        if (expr is SExpr.BracketList bracket)
        {
            var expandedBracketItems = new List<SExpr>();
            for (var i = 0; i < bracket.Items.Count; i++)
                expandedBracketItems.Add(
                    Expand(
                        bracket.Items[i],
                        env,
                        depth,
                        cursor is null
                            ? null
                            : new Cursor.Frame(
                                true,
                                expandedBracketItems,
                                bracket.Items,
                                i,
                                bracket.Span,
                                cursor
                            )
                    )
                );
            return new SExpr.BracketList(expandedBracketItems, bracket.Span);
        }

        if (expr is not SExpr.SList list || list.Items.Count == 0)
            return expr;

        // Check if head is a macro name
        if (list.Items[0] is SExpr.Atom head)
        {
            var macro = env.Lookup(head.Text);
            if (macro is not null)
            {
                var expanded = TryExpandMacro(list, macro, out var ruleIndex);
                if (expanded is not null)
                {
                    if (observer is not null && cursor is not null)
                        observer.OnStep(
                            new MacroStep(
                                _stepCounter++,
                                macro,
                                ruleIndex,
                                depth,
                                list,
                                expanded,
                                cursor.Reconstruct(list),
                                cursor.Reconstruct(expanded),
                                cursor.Path(),
                                _currentTopLevelIndex
                            )
                        );
                    // Same cursor: the expansion replaces the redex at the same tree position
                    return Expand(expanded, env, depth + 1, cursor);
                }
            }
        }

        // Not a macro call — recursively expand sub-expressions
        var expandedItems = new List<SExpr>();
        for (var i = 0; i < list.Items.Count; i++)
            expandedItems.Add(
                Expand(
                    list.Items[i],
                    env,
                    depth,
                    cursor is null
                        ? null
                        : new Cursor.Frame(false, expandedItems, list.Items, i, list.Span, cursor)
                )
            );
        return new SExpr.SList(expandedItems, list.Span);
    }

    private SExpr? TryExpandMacro(SExpr.SList callSite, MacroDefinition macro, out int ruleIndex)
    {
        for (var i = 0; i < macro.Rules.Count; i++)
        {
            var rule = macro.Rules[i];
            var bindings = new Dictionary<string, MacroBinding>();
            if (MatchPattern(rule.Pattern, callSite, macro.Literals, bindings))
            {
                var scope = new MacroScope(macro.Name);
                ruleIndex = i;
                return Instantiate(rule.Template, bindings, scope, callSite.Span);
            }
        }

        diagnostics.Error($"No matching rule for macro '{macro.Name}'", callSite.Span);
        ruleIndex = -1;
        return null;
    }

    private static bool MatchPattern(
        MacroPattern pattern,
        SExpr expr,
        IReadOnlyList<string> literals,
        Dictionary<string, MacroBinding> bindings
    )
    {
        return pattern switch
        {
            MacroPattern.Wildcard => true,
            MacroPattern.Literal lit => expr is SExpr.Atom a && a.Text == lit.Name,
            MacroPattern.Variable v => BindVariable(v.Name, expr, bindings),
            MacroPattern.PatList patList => MatchPatList(patList, expr, literals, bindings),
            MacroPattern.PatBracketList patBracketList => MatchPatBracketList(
                patBracketList,
                expr,
                literals,
                bindings
            ),
            MacroPattern.Ellipsis => throw new InvalidOperationException(
                "Ellipsis at top level should be inside PatList"
            ),
            _ => false,
        };
    }

    private static bool BindVariable(
        string name,
        SExpr expr,
        Dictionary<string, MacroBinding> bindings
    )
    {
        bindings[name] = new MacroBinding.Single(expr);
        return true;
    }

    private static bool MatchPatList(
        MacroPattern.PatList patList,
        SExpr expr,
        IReadOnlyList<string> literals,
        Dictionary<string, MacroBinding> bindings
    )
    {
        if (expr is not SExpr.SList list)
            return false;

        var patterns = patList.Elements;
        var items = list.Items;

        // Find the ellipsis pattern (if any)
        var ellipsisIndex = -1;
        for (var i = 0; i < patterns.Count; i++)
            if (patterns[i] is MacroPattern.Ellipsis)
            {
                ellipsisIndex = i;
                break;
            }

        if (ellipsisIndex < 0)
        {
            // No ellipsis — exact length match
            if (items.Count != patterns.Count)
                return false;

            for (var i = 0; i < patterns.Count; i++)
                if (!MatchPattern(patterns[i], items[i], literals, bindings))
                    return false;
            return true;
        }

        // Has ellipsis — patterns before, ellipsis, patterns after
        var beforeCount = ellipsisIndex;
        var afterCount = patterns.Count - ellipsisIndex - 1;

        if (items.Count < beforeCount + afterCount)
            return false;

        // Match patterns before ellipsis
        for (var i = 0; i < beforeCount; i++)
            if (!MatchPattern(patterns[i], items[i], literals, bindings))
                return false;

        // Match patterns after ellipsis
        for (var i = 0; i < afterCount; i++)
            if (
                !MatchPattern(
                    patterns[ellipsisIndex + 1 + i],
                    items[items.Count - afterCount + i],
                    literals,
                    bindings
                )
            )
                return false;

        // Match ellipsis pattern against the middle elements
        var ellipsis = (MacroPattern.Ellipsis)patterns[ellipsisIndex];
        var repeatCount = items.Count - beforeCount - afterCount;

        // Collect variable names from the inner pattern
        var varNames = new HashSet<string>();
        CollectPatternVars(ellipsis.Inner, varNames);

        // Initialize repeated bindings
        var repeatedLists = new Dictionary<string, List<MacroBinding>>();
        foreach (var name in varNames)
            repeatedLists[name] = new List<MacroBinding>();

        for (var i = 0; i < repeatCount; i++)
        {
            var iterBindings = new Dictionary<string, MacroBinding>();
            if (!MatchPattern(ellipsis.Inner, items[beforeCount + i], literals, iterBindings))
                return false;

            foreach (var name in varNames)
                if (iterBindings.TryGetValue(name, out var binding))
                    repeatedLists[name].Add(binding);
        }

        foreach (var (varName, repeatedBindings) in repeatedLists)
            bindings[varName] = new MacroBinding.Repeated(repeatedBindings);

        return true;
    }

    private static bool MatchPatBracketList(
        MacroPattern.PatBracketList patBracketList,
        SExpr expr,
        IReadOnlyList<string> literals,
        Dictionary<string, MacroBinding> bindings
    )
    {
        if (expr is not SExpr.BracketList bracketList)
            return false;

        var patterns = patBracketList.Elements;
        var items = bracketList.Items;

        // Find the ellipsis pattern (if any)
        var ellipsisIndex = -1;
        for (var i = 0; i < patterns.Count; i++)
            if (patterns[i] is MacroPattern.Ellipsis)
            {
                ellipsisIndex = i;
                break;
            }

        if (ellipsisIndex < 0)
        {
            // No ellipsis — exact length match
            if (items.Count != patterns.Count)
                return false;

            for (var i = 0; i < patterns.Count; i++)
                if (!MatchPattern(patterns[i], items[i], literals, bindings))
                    return false;
            return true;
        }

        // Has ellipsis — patterns before, ellipsis, patterns after
        var beforeCount = ellipsisIndex;
        var afterCount = patterns.Count - ellipsisIndex - 1;

        if (items.Count < beforeCount + afterCount)
            return false;

        // Match patterns before ellipsis
        for (var i = 0; i < beforeCount; i++)
            if (!MatchPattern(patterns[i], items[i], literals, bindings))
                return false;

        // Match patterns after ellipsis
        for (var i = 0; i < afterCount; i++)
            if (
                !MatchPattern(
                    patterns[ellipsisIndex + 1 + i],
                    items[items.Count - afterCount + i],
                    literals,
                    bindings
                )
            )
                return false;

        // Match ellipsis pattern against the middle elements
        var ellipsis = (MacroPattern.Ellipsis)patterns[ellipsisIndex];
        var repeatCount = items.Count - beforeCount - afterCount;

        // Collect variable names from the inner pattern
        var varNames = new HashSet<string>();
        CollectPatternVars(ellipsis.Inner, varNames);

        // Initialize repeated bindings
        var repeatedLists = new Dictionary<string, List<MacroBinding>>();
        foreach (var name in varNames)
            repeatedLists[name] = new List<MacroBinding>();

        for (var i = 0; i < repeatCount; i++)
        {
            var iterBindings = new Dictionary<string, MacroBinding>();
            if (!MatchPattern(ellipsis.Inner, items[beforeCount + i], literals, iterBindings))
                return false;

            foreach (var name in varNames)
                if (iterBindings.TryGetValue(name, out var binding))
                    repeatedLists[name].Add(binding);
        }

        foreach (var (varName, repeatedBindings) in repeatedLists)
            bindings[varName] = new MacroBinding.Repeated(repeatedBindings);

        return true;
    }

    private static void CollectPatternVars(MacroPattern pattern, HashSet<string> vars)
    {
        switch (pattern)
        {
            case MacroPattern.Variable v:
                vars.Add(v.Name);
                break;
            case MacroPattern.PatList pl:
                foreach (var elem in pl.Elements)
                    CollectPatternVars(elem, vars);
                break;
            case MacroPattern.PatBracketList pbl:
                foreach (var elem in pbl.Elements)
                    CollectPatternVars(elem, vars);
                break;
            case MacroPattern.Ellipsis e:
                CollectPatternVars(e.Inner, vars);
                break;
        }
    }

    private SExpr Instantiate(
        MacroTemplate template,
        Dictionary<string, MacroBinding> bindings,
        MacroScope scope,
        SourceSpan span
    )
    {
        return template switch
        {
            MacroTemplate.Datum d => d.Value,
            MacroTemplate.Variable v => InstantiateVariable(v, bindings, scope, span),
            MacroTemplate.TList tl => InstantiateList(tl, bindings, scope, span),
            MacroTemplate.TBracketList bl => InstantiateBracketList(bl, bindings, scope, span),
            MacroTemplate.Ellipsis => throw new InvalidOperationException(
                "Ellipsis at top level should be inside TList"
            ),
            _ => throw new InvalidOperationException(
                $"Unknown template type: {template.GetType()}"
            ),
        };
    }

    private static SExpr InstantiateVariable(
        MacroTemplate.Variable v,
        Dictionary<string, MacroBinding> bindings,
        MacroScope scope,
        SourceSpan span
    )
    {
        if (bindings.TryGetValue(v.Name, out var binding))
            return binding switch
            {
                MacroBinding.Single s => s.Value,
                _ => new SExpr.Atom(new Token(TokenKind.Symbol, v.Name, span)),
            };

        // Macro-introduced identifier — gensym for hygiene
        var gensymName = scope.Gensym(v.Name);
        return new SExpr.Atom(new Token(TokenKind.Symbol, gensymName, span));
    }

    private SExpr InstantiateList(
        MacroTemplate.TList tl,
        Dictionary<string, MacroBinding> bindings,
        MacroScope scope,
        SourceSpan span
    )
    {
        var result = new List<SExpr>();

        foreach (var elem in tl.Elements)
            if (elem is MacroTemplate.Ellipsis ellipsis)
            {
                // Find the repeated binding to determine iteration count
                var repeatedVars = new HashSet<string>();
                CollectTemplateVars(ellipsis.Inner, repeatedVars);

                var count = 0;
                string? repeatedVarName = null;
                foreach (var varName in repeatedVars)
                    if (bindings.TryGetValue(varName, out var b) && b is MacroBinding.Repeated rep)
                    {
                        count = rep.Items.Count;
                        repeatedVarName = varName;
                        break;
                    }

                if (repeatedVarName is null)
                    // No repeated binding found — skip
                    continue;

                for (var i = 0; i < count; i++)
                {
                    // Create iteration-specific bindings
                    var iterBindings = new Dictionary<string, MacroBinding>(bindings);
                    foreach (var varName in repeatedVars)
                        if (
                            bindings.TryGetValue(varName, out var b)
                            && b is MacroBinding.Repeated rep
                            && i < rep.Items.Count
                        )
                            iterBindings[varName] = rep.Items[i];

                    result.Add(Instantiate(ellipsis.Inner, iterBindings, scope, span));
                }
            }
            else
            {
                result.Add(Instantiate(elem, bindings, scope, span));
            }

        return new SExpr.SList(result, span);
    }

    private SExpr InstantiateBracketList(
        MacroTemplate.TBracketList bl,
        Dictionary<string, MacroBinding> bindings,
        MacroScope scope,
        SourceSpan span
    )
    {
        var result = new List<SExpr>();

        foreach (var elem in bl.Elements)
            if (elem is MacroTemplate.Ellipsis ellipsis)
            {
                var repeatedVars = new HashSet<string>();
                CollectTemplateVars(ellipsis.Inner, repeatedVars);

                var count = 0;
                string? repeatedVarName = null;
                foreach (var varName in repeatedVars)
                    if (bindings.TryGetValue(varName, out var b) && b is MacroBinding.Repeated rep)
                    {
                        count = rep.Items.Count;
                        repeatedVarName = varName;
                        break;
                    }

                if (repeatedVarName is null)
                    continue;

                for (var i = 0; i < count; i++)
                {
                    var iterBindings = new Dictionary<string, MacroBinding>(bindings);
                    foreach (var varName in repeatedVars)
                        if (
                            bindings.TryGetValue(varName, out var b)
                            && b is MacroBinding.Repeated rep
                            && i < rep.Items.Count
                        )
                            iterBindings[varName] = rep.Items[i];

                    result.Add(Instantiate(ellipsis.Inner, iterBindings, scope, span));
                }
            }
            else
            {
                result.Add(Instantiate(elem, bindings, scope, span));
            }

        return new SExpr.BracketList(result, span);
    }

    private static void CollectTemplateVars(MacroTemplate template, HashSet<string> vars)
    {
        switch (template)
        {
            case MacroTemplate.Variable v:
                vars.Add(v.Name);
                break;
            case MacroTemplate.TList tl:
                foreach (var elem in tl.Elements)
                    CollectTemplateVars(elem, vars);
                break;
            case MacroTemplate.TBracketList bl:
                foreach (var elem in bl.Elements)
                    CollectTemplateVars(elem, vars);
                break;
            case MacroTemplate.Ellipsis e:
                CollectTemplateVars(e.Inner, vars);
                break;
        }
    }

    private static bool IsDefineSyntax(SExpr expr)
    {
        return expr is SExpr.SList list
            && list.Items.Count >= 1
            && list.Items[0] is SExpr.Atom { Text: "define-syntax" };
    }
}

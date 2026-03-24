using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Syntax;

public sealed class MacroParser(DiagnosticBag diagnostics)
{
    public MacroDefinition? Parse(SExpr.SList form)
    {
        // (define-syntax name (syntax-rules (literals...) [pattern template] ...))
        if (form.Items.Count != 3)
        {
            diagnostics.Error("'define-syntax' requires a name and a syntax-rules body", form.Span);
            return null;
        }

        if (form.Items[1] is not SExpr.Atom nameAtom)
        {
            diagnostics.Error("'define-syntax' name must be an identifier", form.Items[1].Span);
            return null;
        }

        if (form.Items[2] is not SExpr.SList syntaxRules ||
            syntaxRules.Items.Count < 2 ||
            syntaxRules.Items[0] is not SExpr.Atom { Text: "syntax-rules" })
        {
            diagnostics.Error("'define-syntax' body must be (syntax-rules ...)", form.Items[2].Span);
            return null;
        }

        // Parse literals list
        if (syntaxRules.Items[1] is not SExpr.SList literalsList)
        {
            diagnostics.Error("'syntax-rules' requires a literals list", syntaxRules.Items[1].Span);
            return null;
        }

        var literals = new List<string>();
        foreach (var item in literalsList.Items)
            if (item is SExpr.Atom litAtom)
                literals.Add(litAtom.Text);
            else
                diagnostics.Error("Literal must be an identifier", item.Span);

        // Parse rules: [pattern template]
        var rules = new List<MacroRule>();
        for (var i = 2; i < syntaxRules.Items.Count; i++)
            if (syntaxRules.Items[i] is SExpr.BracketList rule && rule.Items.Count == 2)
            {
                var pattern = ParsePattern(rule.Items[0], nameAtom.Text, literals);
                var patternVars = new HashSet<string>();
                CollectPatternVarNames(pattern, patternVars);
                var template = ParseTemplate(rule.Items[1], literals, patternVars);
                rules.Add(new MacroRule(pattern, template, rule.Span));
            }
            else
            {
                diagnostics.Error("Macro rule must be [pattern template]", syntaxRules.Items[i].Span);
            }

        if (rules.Count == 0)
        {
            diagnostics.Error("'syntax-rules' requires at least one rule", syntaxRules.Span);
            return null;
        }

        return new MacroDefinition(nameAtom.Text, literals, rules, form.Span);
    }

    private MacroPattern ParsePattern(SExpr expr, string macroName, IReadOnlyList<string> literals)
    {
        return expr switch
        {
            SExpr.Atom { Text: "_" } a => new MacroPattern.Wildcard(a.Span),
            SExpr.Atom { Text: "..." } a => throw new InvalidOperationException(
                "Ellipsis not allowed at top level of pattern"),
            SExpr.Atom a when a.Text == macroName => new MacroPattern.Literal(a.Text, a.Span),
            SExpr.Atom a when literals.Contains(a.Text) => new MacroPattern.Literal(a.Text, a.Span),
            SExpr.Atom a => new MacroPattern.Variable(a.Text, a.Span),
            SExpr.SList list => ParsePatternList(list, macroName, literals),
            _ => new MacroPattern.Wildcard(expr.Span)
        };
    }

    private MacroPattern ParsePatternList(SExpr.SList list, string macroName, IReadOnlyList<string> literals)
    {
        var elements = new List<MacroPattern>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (i + 1 < list.Items.Count && list.Items[i + 1] is SExpr.Atom { Text: "..." })
            {
                var inner = ParsePattern(item, macroName, literals);
                elements.Add(new MacroPattern.Ellipsis(inner, item.Span));
                i++; // skip the ...
            }
            else
            {
                elements.Add(ParsePattern(item, macroName, literals));
            }
        }

        return new MacroPattern.PatList(elements, list.Span);
    }

    private static void CollectPatternVarNames(MacroPattern pattern, HashSet<string> vars)
    {
        switch (pattern)
        {
            case MacroPattern.Variable v:
                vars.Add(v.Name);
                break;
            case MacroPattern.PatList pl:
                foreach (var elem in pl.Elements)
                    CollectPatternVarNames(elem, vars);
                break;
            case MacroPattern.Ellipsis e:
                CollectPatternVarNames(e.Inner, vars);
                break;
        }
    }

    private MacroTemplate ParseTemplate(SExpr expr, IReadOnlyList<string> literals, HashSet<string> patternVars)
    {
        return expr switch
        {
            SExpr.Atom { Text: "..." } => throw new InvalidOperationException(
                "Ellipsis not allowed at top level of template"),
            SExpr.Atom a when literals.Contains(a.Text) => new MacroTemplate.Datum(a, a.Span),
            SExpr.Atom a when a.Kind == TokenKind.IntLit || a.Kind == TokenKind.FloatLit ||
                              a.Kind == TokenKind.StringLit || a.Kind == TokenKind.BoolLit =>
                new MacroTemplate.Datum(a, a.Span),
            SExpr.Atom a when patternVars.Contains(a.Text) => new MacroTemplate.Variable(a.Text, a.Span),
            SExpr.Atom a => new MacroTemplate.Datum(a, a.Span),
            SExpr.SList list => ParseTemplateList(list, literals, patternVars),
            SExpr.BracketList bracket => ParseTemplateBracketList(bracket, literals, patternVars),
            _ => new MacroTemplate.Datum(expr, expr.Span)
        };
    }

    private MacroTemplate ParseTemplateList(SExpr.SList list, IReadOnlyList<string> literals,
        HashSet<string> patternVars)
    {
        var elements = new List<MacroTemplate>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (i + 1 < list.Items.Count && list.Items[i + 1] is SExpr.Atom { Text: "..." })
            {
                var inner = ParseTemplate(item, literals, patternVars);
                elements.Add(new MacroTemplate.Ellipsis(inner, item.Span));
                i++; // skip the ...
            }
            else
            {
                elements.Add(ParseTemplate(item, literals, patternVars));
            }
        }

        return new MacroTemplate.TList(elements, list.Span);
    }

    private MacroTemplate ParseTemplateBracketList(SExpr.BracketList bracket, IReadOnlyList<string> literals,
        HashSet<string> patternVars)
    {
        var elements = new List<MacroTemplate>();
        for (var i = 0; i < bracket.Items.Count; i++)
        {
            var item = bracket.Items[i];
            if (i + 1 < bracket.Items.Count && bracket.Items[i + 1] is SExpr.Atom { Text: "..." })
            {
                var inner = ParseTemplate(item, literals, patternVars);
                elements.Add(new MacroTemplate.Ellipsis(inner, item.Span));
                i++; // skip the ...
            }
            else
            {
                elements.Add(ParseTemplate(item, literals, patternVars));
            }
        }

        return new MacroTemplate.TBracketList(elements, bracket.Span);
    }
}

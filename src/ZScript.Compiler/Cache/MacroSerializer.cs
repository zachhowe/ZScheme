using System.Text.Json.Nodes;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;

namespace ZScript.Compiler.Cache;

public static class MacroSerializer
{
    public static JsonObject Serialize(MacroDefinition macro)
    {
        var literalsArray = new JsonArray();
        foreach (var lit in macro.Literals)
            literalsArray.Add(lit);

        var rulesArray = new JsonArray();
        foreach (var rule in macro.Rules)
            rulesArray.Add(SerializeRule(rule));

        return new JsonObject
        {
            ["name"] = macro.Name,
            ["literals"] = literalsArray,
            ["rules"] = rulesArray,
        };
    }

    public static MacroDefinition Deserialize(JsonNode node)
    {
        var obj = node as JsonObject
            ?? throw new ArgumentException("Expected a JSON object for MacroDefinition");

        var name = obj["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'name' field in MacroDefinition JSON");

        var literalsArray = obj["literals"] as JsonArray ?? [];
        var literals = new List<string>();
        foreach (var lit in literalsArray)
        {
            if (lit?.GetValue<string>() is { } s)
                literals.Add(s);
        }

        var rulesArray = obj["rules"] as JsonArray ?? [];
        var rules = new List<MacroRule>();
        foreach (var ruleNode in rulesArray)
        {
            if (ruleNode is JsonObject ruleObj)
                rules.Add(DeserializeRule(ruleObj));
        }

        return new MacroDefinition(name, literals, rules, SourceSpan.None);
    }

    private static JsonObject SerializeRule(MacroRule rule)
    {
        return new JsonObject
        {
            ["pattern"] = SerializePattern(rule.Pattern),
            ["template"] = SerializeTemplate(rule.Template),
        };
    }

    private static MacroRule DeserializeRule(JsonObject obj)
    {
        var patternNode = obj["pattern"] as JsonObject
            ?? throw new ArgumentException("Missing 'pattern' field in MacroRule JSON");
        var templateNode = obj["template"] as JsonObject
            ?? throw new ArgumentException("Missing 'template' field in MacroRule JSON");

        return new MacroRule(
            DeserializePattern(patternNode),
            DeserializeTemplate(templateNode),
            SourceSpan.None);
    }

    private static JsonObject SerializePattern(MacroPattern pattern)
    {
        return pattern switch
        {
            MacroPattern.Literal lit => new JsonObject
            {
                ["kind"] = "literal",
                ["name"] = lit.Name,
            },
            MacroPattern.Variable v => new JsonObject
            {
                ["kind"] = "variable",
                ["name"] = v.Name,
            },
            MacroPattern.Wildcard => new JsonObject
            {
                ["kind"] = "wildcard",
            },
            MacroPattern.PatList pl => SerializePatList(pl),
            MacroPattern.Ellipsis e => new JsonObject
            {
                ["kind"] = "ellipsis",
                ["inner"] = SerializePattern(e.Inner),
            },
            _ => throw new ArgumentException($"Unknown MacroPattern variant: {pattern.GetType().Name}"),
        };
    }

    private static JsonObject SerializePatList(MacroPattern.PatList pl)
    {
        var elementsArray = new JsonArray();
        foreach (var elem in pl.Elements)
            elementsArray.Add(SerializePattern(elem));

        return new JsonObject
        {
            ["kind"] = "patList",
            ["elements"] = elementsArray,
        };
    }

    private static MacroPattern DeserializePattern(JsonObject obj)
    {
        var kind = obj["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'kind' field in MacroPattern JSON");

        return kind switch
        {
            "literal" => new MacroPattern.Literal(
                obj["name"]?.GetValue<string>()
                    ?? throw new ArgumentException("Missing 'name' in literal pattern"),
                SourceSpan.None),
            "variable" => new MacroPattern.Variable(
                obj["name"]?.GetValue<string>()
                    ?? throw new ArgumentException("Missing 'name' in variable pattern"),
                SourceSpan.None),
            "wildcard" => new MacroPattern.Wildcard(SourceSpan.None),
            "patList" => DeserializePatList(obj),
            "ellipsis" => new MacroPattern.Ellipsis(
                DeserializePattern(obj["inner"] as JsonObject
                    ?? throw new ArgumentException("Missing 'inner' in ellipsis pattern")),
                SourceSpan.None),
            _ => throw new ArgumentException($"Unknown MacroPattern kind: {kind}"),
        };
    }

    private static MacroPattern.PatList DeserializePatList(JsonObject obj)
    {
        var elementsArray = obj["elements"] as JsonArray
            ?? throw new ArgumentException("Missing 'elements' in patList pattern");

        var elements = new List<MacroPattern>();
        foreach (var elem in elementsArray)
        {
            if (elem is JsonObject elemObj)
                elements.Add(DeserializePattern(elemObj));
        }

        return new MacroPattern.PatList(elements, SourceSpan.None);
    }

    private static JsonObject SerializeTemplate(MacroTemplate template)
    {
        return template switch
        {
            MacroTemplate.Datum d => new JsonObject
            {
                ["kind"] = "datum",
                ["value"] = SerializeSExpr(d.Value),
            },
            MacroTemplate.Variable v => new JsonObject
            {
                ["kind"] = "variable",
                ["name"] = v.Name,
            },
            MacroTemplate.TList tl => SerializeTList(tl),
            MacroTemplate.TBracketList tbl => SerializeTBracketList(tbl),
            MacroTemplate.Ellipsis e => new JsonObject
            {
                ["kind"] = "ellipsis",
                ["inner"] = SerializeTemplate(e.Inner),
            },
            _ => throw new ArgumentException($"Unknown MacroTemplate variant: {template.GetType().Name}"),
        };
    }

    private static JsonObject SerializeTList(MacroTemplate.TList tl)
    {
        var elementsArray = new JsonArray();
        foreach (var elem in tl.Elements)
            elementsArray.Add(SerializeTemplate(elem));

        return new JsonObject
        {
            ["kind"] = "tList",
            ["elements"] = elementsArray,
        };
    }

    private static JsonObject SerializeTBracketList(MacroTemplate.TBracketList tbl)
    {
        var elementsArray = new JsonArray();
        foreach (var elem in tbl.Elements)
            elementsArray.Add(SerializeTemplate(elem));

        return new JsonObject
        {
            ["kind"] = "tBracketList",
            ["elements"] = elementsArray,
        };
    }

    private static MacroTemplate DeserializeTemplate(JsonObject obj)
    {
        var kind = obj["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'kind' field in MacroTemplate JSON");

        return kind switch
        {
            "datum" => new MacroTemplate.Datum(
                DeserializeSExpr(obj["value"]
                    ?? throw new ArgumentException("Missing 'value' in datum template")),
                SourceSpan.None),
            "variable" => new MacroTemplate.Variable(
                obj["name"]?.GetValue<string>()
                    ?? throw new ArgumentException("Missing 'name' in variable template"),
                SourceSpan.None),
            "tList" => DeserializeTList(obj),
            "tBracketList" => DeserializeTBracketList(obj),
            "ellipsis" => new MacroTemplate.Ellipsis(
                DeserializeTemplate(obj["inner"] as JsonObject
                    ?? throw new ArgumentException("Missing 'inner' in ellipsis template")),
                SourceSpan.None),
            _ => throw new ArgumentException($"Unknown MacroTemplate kind: {kind}"),
        };
    }

    private static MacroTemplate.TList DeserializeTList(JsonObject obj)
    {
        var elementsArray = obj["elements"] as JsonArray
            ?? throw new ArgumentException("Missing 'elements' in tList template");

        var elements = new List<MacroTemplate>();
        foreach (var elem in elementsArray)
        {
            if (elem is JsonObject elemObj)
                elements.Add(DeserializeTemplate(elemObj));
        }

        return new MacroTemplate.TList(elements, SourceSpan.None);
    }

    private static MacroTemplate.TBracketList DeserializeTBracketList(JsonObject obj)
    {
        var elementsArray = obj["elements"] as JsonArray
            ?? throw new ArgumentException("Missing 'elements' in tBracketList template");

        var elements = new List<MacroTemplate>();
        foreach (var elem in elementsArray)
        {
            if (elem is JsonObject elemObj)
                elements.Add(DeserializeTemplate(elemObj));
        }

        return new MacroTemplate.TBracketList(elements, SourceSpan.None);
    }

    private static JsonObject SerializeSExpr(SExpr sexpr)
    {
        return sexpr switch
        {
            SExpr.Atom atom => new JsonObject
            {
                ["kind"] = "atom",
                ["tokenKind"] = atom.Kind.ToString(),
                ["text"] = atom.Text,
            },
            SExpr.SList sl => SerializeSList(sl),
            SExpr.BracketList bl => SerializeBracketList(bl),
            _ => throw new ArgumentException($"Unknown SExpr variant: {sexpr.GetType().Name}"),
        };
    }

    private static JsonObject SerializeSList(SExpr.SList sl)
    {
        var itemsArray = new JsonArray();
        foreach (var item in sl.Items)
            itemsArray.Add(SerializeSExpr(item));

        return new JsonObject
        {
            ["kind"] = "sList",
            ["items"] = itemsArray,
        };
    }

    private static JsonObject SerializeBracketList(SExpr.BracketList bl)
    {
        var itemsArray = new JsonArray();
        foreach (var item in bl.Items)
            itemsArray.Add(SerializeSExpr(item));

        return new JsonObject
        {
            ["kind"] = "bracketList",
            ["items"] = itemsArray,
        };
    }

    private static SExpr DeserializeSExpr(JsonNode node)
    {
        var obj = node as JsonObject
            ?? throw new ArgumentException("Expected a JSON object for SExpr");

        var kind = obj["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'kind' field in SExpr JSON");

        return kind switch
        {
            "atom" => DeserializeAtom(obj),
            "sList" => DeserializeSList(obj),
            "bracketList" => DeserializeBracketList(obj),
            _ => throw new ArgumentException($"Unknown SExpr kind: {kind}"),
        };
    }

    private static SExpr.Atom DeserializeAtom(JsonObject obj)
    {
        var tokenKindStr = obj["tokenKind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'tokenKind' in atom SExpr");
        var text = obj["text"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'text' in atom SExpr");

        if (!Enum.TryParse<TokenKind>(tokenKindStr, out var tokenKind))
            throw new ArgumentException($"Unknown TokenKind: {tokenKindStr}");

        return new SExpr.Atom(new Token(tokenKind, text, SourceSpan.None));
    }

    private static SExpr.SList DeserializeSList(JsonObject obj)
    {
        var itemsArray = obj["items"] as JsonArray
            ?? throw new ArgumentException("Missing 'items' in sList SExpr");

        var items = new List<SExpr>();
        foreach (var item in itemsArray)
        {
            if (item is not null)
                items.Add(DeserializeSExpr(item));
        }

        return new SExpr.SList(items, SourceSpan.None);
    }

    private static SExpr.BracketList DeserializeBracketList(JsonObject obj)
    {
        var itemsArray = obj["items"] as JsonArray
            ?? throw new ArgumentException("Missing 'items' in bracketList SExpr");

        var items = new List<SExpr>();
        foreach (var item in itemsArray)
        {
            if (item is not null)
                items.Add(DeserializeSExpr(item));
        }

        return new SExpr.BracketList(items, SourceSpan.None);
    }
}

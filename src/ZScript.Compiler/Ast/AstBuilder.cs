namespace ZScript.Compiler.Ast;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed class AstBuilder
{
    private readonly DiagnosticBag _diagnostics;

    public AstBuilder(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public AstNode.Program BuildProgram(IReadOnlyList<SExpr> exprs)
    {
        var forms = new List<AstNode>();
        foreach (var expr in exprs)
        {
            forms.Add(Build(expr));
        }

        var span = exprs.Count > 0 ? exprs[0].Span : SourceSpan.None;
        return new AstNode.Program(forms, span);
    }

    public AstNode Build(SExpr expr) => expr switch
    {
        SExpr.Atom atom => BuildAtom(atom),
        SExpr.SList list => BuildList(list),
        SExpr.BracketList bracket => BuildBracketExpr(bracket),
        _ => throw new InvalidOperationException($"Unknown SExpr type: {expr.GetType()}")
    };

    private AstNode BuildAtom(SExpr.Atom atom) => atom.Kind switch
    {
        TokenKind.IntLit => new AstNode.IntLit(int.Parse(atom.Text), atom.Span),
        TokenKind.FloatLit => new AstNode.FloatLit(ParseFloat(atom.Text), atom.Span),
        TokenKind.BoolLit => new AstNode.BoolLit(atom.Text == "true", atom.Span),
        TokenKind.StringLit => new AstNode.StringLit(atom.Text, atom.Span),
        TokenKind.Symbol => new AstNode.Name(atom.Text, atom.Span),
        _ => new AstNode.Name(atom.Text, atom.Span)
    };

    private static float ParseFloat(string text)
    {
        var clean = text.TrimEnd('f', 'F');
        return float.Parse(clean, System.Globalization.CultureInfo.InvariantCulture);
    }

    private AstNode BuildList(SExpr.SList list)
    {
        if (list.Items.Count == 0)
            return new AstNode.UnitLit(list.Span);

        // Check for special forms
        if (list.Items[0] is SExpr.Atom head)
        {
            switch (head.Text)
            {
                case "define": return BuildDefine(list);
                case "let": return BuildLet(list);
                case "if": return BuildIf(list);
                case "fn": return BuildLambda(list);
                case "match": return BuildMatch(list);
                case "record": return BuildRecord(list);
                case "union": return BuildUnion(list);
                case "|>": return BuildPipe(list);
                case "partial": return BuildPartial(list);
                case "try": return BuildTry(list);
                case "?": return BuildPropagate(list);
                case "import-clr": return BuildImportClr(list);
                case "module": return BuildModule(list);
                case "import": return BuildImport(list);
                case "list": return BuildListExpr(list);
                case "vector": return BuildVectorExpr(list);
                case "map-of": return BuildMapExpr(list);
            }
        }

        // Function application
        return BuildApply(list);
    }

    private AstNode BuildBracketExpr(SExpr.BracketList bracket)
    {
        // Brackets in expression position are an error
        _diagnostics.Error("Unexpected bracket expression in expression position", bracket.Span);
        return new AstNode.UnitLit(bracket.Span);
    }

    private AstNode BuildDefine(SExpr.SList list)
    {
        // (define (name [params...]) : ReturnType body)
        // (define name expr)
        if (list.Items.Count < 3)
        {
            _diagnostics.Error("'define' requires at least a name and body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        // (define name expr)
        if (list.Items[1] is SExpr.Atom nameAtom)
        {
            var value = Build(list.Items[2]);
            return new AstNode.DefineValue(nameAtom.Text, value, list.Span);
        }

        // (define (name [params...]) : ReturnType body)
        if (list.Items[1] is SExpr.SList sig)
        {
            if (sig.Items.Count == 0)
            {
                _diagnostics.Error("Function signature must have a name", list.Span);
                return new AstNode.UnitLit(list.Span);
            }

            var fnName = ((SExpr.Atom)sig.Items[0]).Text;
            var parms = new List<Param>();

            for (int i = 1; i < sig.Items.Count; i++)
            {
                parms.Add(ParseParam(sig.Items[i]));
            }

            // Look for return type annotation: ... : ReturnType body
            ZType? returnType = null;
            int bodyStart = 2;

            if (bodyStart < list.Items.Count &&
                list.Items[bodyStart] is SExpr.Atom colon && colon.Text == ":")
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
                _diagnostics.Error("Function definition requires a body", list.Span);
                return new AstNode.UnitLit(list.Span);
            }

            var body = Build(list.Items[bodyStart]);
            return new AstNode.Define(fnName, parms, returnType, body, list.Span);
        }

        _diagnostics.Error("Invalid 'define' form", list.Span);
        return new AstNode.UnitLit(list.Span);
    }

    private AstNode BuildLet(SExpr.SList list)
    {
        // (let [x expr] body)
        if (list.Items.Count != 3)
        {
            _diagnostics.Error("'let' requires a binding and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.BracketList binding || binding.Items.Count < 2)
        {
            _diagnostics.Error("'let' binding must be [name expr]", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var name = ((SExpr.Atom)binding.Items[0]).Text;
        var value = Build(binding.Items[1]);
        var body = Build(list.Items[2]);

        return new AstNode.Let(name, value, body, list.Span);
    }

    private AstNode BuildIf(SExpr.SList list)
    {
        // (if cond then else)
        if (list.Items.Count != 4)
        {
            _diagnostics.Error("'if' requires condition, then, and else branches", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var cond = Build(list.Items[1]);
        var then = Build(list.Items[2]);
        var @else = Build(list.Items[3]);

        return new AstNode.If(cond, then, @else, list.Span);
    }

    private AstNode BuildLambda(SExpr.SList list)
    {
        // (fn [params...] body)
        if (list.Items.Count != 3)
        {
            _diagnostics.Error("'fn' requires parameters and a body", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        if (list.Items[1] is not SExpr.BracketList paramList)
        {
            _diagnostics.Error("'fn' parameters must be in brackets", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var parms = new List<Param>();
        foreach (var item in paramList.Items)
        {
            if (item is SExpr.Atom a)
                parms.Add(new Param(a.Text, null, a.Span));
            else
                parms.Add(ParseParam(item));
        }

        var body = Build(list.Items[2]);
        return new AstNode.Lambda(parms, body, list.Span);
    }

    private AstNode BuildMatch(SExpr.SList list)
    {
        // (match expr [pattern body] ...)
        if (list.Items.Count < 3)
        {
            _diagnostics.Error("'match' requires a scrutinee and at least one arm", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var scrutinee = Build(list.Items[1]);
        var arms = new List<MatchArm>();

        for (int i = 2; i < list.Items.Count; i++)
        {
            if (list.Items[i] is SExpr.BracketList arm && arm.Items.Count >= 2)
            {
                var pattern = ParsePattern(arm.Items[0]);
                var body = Build(arm.Items[1]);
                arms.Add(new MatchArm(pattern, body, arm.Span));
            }
            else
            {
                _diagnostics.Error("Match arm must be [pattern body]", list.Items[i].Span);
            }
        }

        return new AstNode.Match(scrutinee, arms, list.Span);
    }

    private AstNode BuildRecord(SExpr.SList list)
    {
        // (record Name [field : Type] ...)
        // (record (Name a b) [field : Type] ...)
        if (list.Items.Count < 2)
        {
            _diagnostics.Error("'record' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        var typeParams = new List<string>();
        int fieldsStart;

        if (list.Items[1] is SExpr.SList nameList)
        {
            // Generic: (record (Pair a b) ...)
            name = ((SExpr.Atom)nameList.Items[0]).Text;
            for (int i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            fieldsStart = 2;
        }
        else
        {
            name = ((SExpr.Atom)list.Items[1]).Text;
            fieldsStart = 2;
        }

        var fields = new List<FieldDecl>();
        for (int i = fieldsStart; i < list.Items.Count; i++)
        {
            fields.Add(ParseFieldDecl(list.Items[i]));
        }

        return new AstNode.RecordDecl(name, typeParams, fields, list.Span);
    }

    private AstNode BuildUnion(SExpr.SList list)
    {
        // (union Name (Case1 [field : Type]) ...)
        // (union (Name a) (Case1 [field : Type]) ...)
        if (list.Items.Count < 3)
        {
            _diagnostics.Error("'union' requires a name and at least one case", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        string name;
        var typeParams = new List<string>();
        int casesStart;

        if (list.Items[1] is SExpr.SList nameList)
        {
            name = ((SExpr.Atom)nameList.Items[0]).Text;
            for (int i = 1; i < nameList.Items.Count; i++)
                typeParams.Add(((SExpr.Atom)nameList.Items[i]).Text);
            casesStart = 2;
        }
        else
        {
            name = ((SExpr.Atom)list.Items[1]).Text;
            casesStart = 2;
        }

        var cases = new List<UnionCase>();
        for (int i = casesStart; i < list.Items.Count; i++)
        {
            if (list.Items[i] is SExpr.SList caseList && caseList.Items.Count >= 1)
            {
                var caseName = ((SExpr.Atom)caseList.Items[0]).Text;
                var fields = new List<FieldDecl>();
                for (int j = 1; j < caseList.Items.Count; j++)
                    fields.Add(ParseFieldDecl(caseList.Items[j]));
                cases.Add(new UnionCase(caseName, fields, caseList.Span));
            }
        }

        return new AstNode.UnionDecl(name, typeParams, cases, list.Span);
    }

    private AstNode BuildPipe(SExpr.SList list)
    {
        // (|> x (f a) (g b))
        if (list.Items.Count < 3)
        {
            _diagnostics.Error("'|>' requires an initial value and at least one step", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var initial = Build(list.Items[1]);
        var steps = new List<AstNode>();
        for (int i = 2; i < list.Items.Count; i++)
            steps.Add(Build(list.Items[i]));

        return new AstNode.Pipe(initial, steps, list.Span);
    }

    private AstNode BuildPartial(SExpr.SList list)
    {
        // (partial f arg1 arg2 ...)
        if (list.Items.Count < 3)
        {
            _diagnostics.Error("'partial' requires a function and at least one argument", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var func = Build(list.Items[1]);
        var args = new List<AstNode>();
        for (int i = 2; i < list.Items.Count; i++)
            args.Add(Build(list.Items[i]));

        return new AstNode.Partial(func, args, list.Span);
    }

    private AstNode BuildTry(SExpr.SList list)
    {
        // (try body)
        if (list.Items.Count != 2)
        {
            _diagnostics.Error("'try' requires exactly one body expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        return new AstNode.Try(Build(list.Items[1]), list.Span);
    }

    private AstNode BuildPropagate(SExpr.SList list)
    {
        // (? expr)
        if (list.Items.Count != 2)
        {
            _diagnostics.Error("'?' requires exactly one expression", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        return new AstNode.Propagate(Build(list.Items[1]), list.Span);
    }

    private AstNode BuildImportClr(SExpr.SList list)
    {
        // (import-clr [alias Type/Method] ...)
        var imports = new List<ClrImport>();
        for (int i = 1; i < list.Items.Count; i++)
        {
            if (list.Items[i] is SExpr.BracketList bracket && bracket.Items.Count == 2)
            {
                var alias = ((SExpr.Atom)bracket.Items[0]).Text;
                var qualName = ((SExpr.Atom)bracket.Items[1]).Text;
                imports.Add(new ClrImport(alias, qualName, bracket.Span));
            }
            else
            {
                _diagnostics.Error("import-clr entry must be [alias qualified/Name]", list.Items[i].Span);
            }
        }

        return new AstNode.ImportClr(imports, list.Span);
    }

    private AstNode BuildModule(SExpr.SList list)
    {
        if (list.Items.Count != 2)
        {
            _diagnostics.Error("'module' requires a name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var name = ((SExpr.Atom)list.Items[1]).Text;
        return new AstNode.ModuleDecl(name, list.Span);
    }

    private AstNode BuildImport(SExpr.SList list)
    {
        if (list.Items.Count != 2)
        {
            _diagnostics.Error("'import' requires a module name", list.Span);
            return new AstNode.UnitLit(list.Span);
        }

        var name = ((SExpr.Atom)list.Items[1]).Text;
        return new AstNode.Import(name, list.Span);
    }

    private AstNode BuildListExpr(SExpr.SList list)
    {
        var elems = new List<AstNode>();
        for (int i = 1; i < list.Items.Count; i++)
            elems.Add(Build(list.Items[i]));
        return new AstNode.ListExpr(elems, list.Span);
    }

    private AstNode BuildVectorExpr(SExpr.SList list)
    {
        var elems = new List<AstNode>();
        for (int i = 1; i < list.Items.Count; i++)
            elems.Add(Build(list.Items[i]));
        return new AstNode.VectorExpr(elems, list.Span);
    }

    private AstNode BuildMapExpr(SExpr.SList list)
    {
        var entries = new List<(AstNode Key, AstNode Value)>();
        for (int i = 1; i < list.Items.Count; i++)
        {
            if (list.Items[i] is SExpr.SList pair && pair.Items.Count == 2)
            {
                entries.Add((Build(pair.Items[0]), Build(pair.Items[1])));
            }
            else
            {
                _diagnostics.Error("map-of entry must be (key value)", list.Items[i].Span);
            }
        }
        return new AstNode.MapExpr(entries, list.Span);
    }

    private AstNode BuildApply(SExpr.SList list)
    {
        var func = Build(list.Items[0]);
        var args = new List<AstNode>();
        for (int i = 1; i < list.Items.Count; i++)
            args.Add(Build(list.Items[i]));
        return new AstNode.Apply(func, args, list.Span);
    }

    private Param ParseParam(SExpr expr)
    {
        // [name : Type] or just name
        if (expr is SExpr.BracketList bracket)
        {
            if (bracket.Items.Count >= 3 &&
                bracket.Items[1] is SExpr.Atom colon && colon.Text == ":")
            {
                var name = ((SExpr.Atom)bracket.Items[0]).Text;
                var type = ParseTypeExpr(bracket.Items[2]);
                return new Param(name, type, bracket.Span);
            }

            if (bracket.Items.Count == 1 && bracket.Items[0] is SExpr.Atom single)
            {
                return new Param(single.Text, null, bracket.Span);
            }

            _diagnostics.Error("Invalid parameter syntax", bracket.Span);
            return new Param("_", null, bracket.Span);
        }

        if (expr is SExpr.Atom atom)
        {
            return new Param(atom.Text, null, atom.Span);
        }

        _diagnostics.Error("Invalid parameter", expr.Span);
        return new Param("_", null, expr.Span);
    }

    private FieldDecl ParseFieldDecl(SExpr expr)
    {
        if (expr is SExpr.BracketList bracket &&
            bracket.Items.Count >= 3 &&
            bracket.Items[1] is SExpr.Atom colon && colon.Text == ":")
        {
            var name = ((SExpr.Atom)bracket.Items[0]).Text;
            var type = ParseTypeExpr(bracket.Items[2]);
            return new FieldDecl(name, type, bracket.Span);
        }

        _diagnostics.Error("Field must be [name : Type]", expr.Span);
        return new FieldDecl("_", ZType.Unit, expr.Span);
    }

    private Pattern ParsePattern(SExpr expr) => expr switch
    {
        SExpr.Atom { Text: "_" } a => new Pattern.Wildcard(a.Span),
        SExpr.Atom { Kind: TokenKind.IntLit } a =>
            new Pattern.Literal(int.Parse(a.Text), a.Span),
        SExpr.Atom { Kind: TokenKind.FloatLit } a =>
            new Pattern.Literal(ParseFloat(a.Text), a.Span),
        SExpr.Atom { Kind: TokenKind.BoolLit } a =>
            new Pattern.Literal(a.Text == "true", a.Span),
        SExpr.Atom { Kind: TokenKind.StringLit } a =>
            new Pattern.Literal(a.Text, a.Span),
        SExpr.Atom a =>
            new Pattern.Variable(a.Text, a.Span),
        SExpr.SList list when list.Items.Count >= 1 =>
            ParseConstructorPattern(list),
        _ =>
            ReportBadPattern(expr)
    };

    private Pattern ParseConstructorPattern(SExpr.SList list)
    {
        var name = ((SExpr.Atom)list.Items[0]).Text;
        var fields = new List<Pattern>();
        for (int i = 1; i < list.Items.Count; i++)
            fields.Add(ParsePattern(list.Items[i]));
        return new Pattern.Constructor(name, fields, list.Span);
    }

    private Pattern ReportBadPattern(SExpr expr)
    {
        _diagnostics.Error("Invalid pattern", expr.Span);
        return new Pattern.Wildcard(expr.Span);
    }

    public ZType ParseTypeExpr(SExpr expr) => expr switch
    {
        SExpr.Atom a => a.Text switch
        {
            "Int" => ZType.Int,
            "Long" => ZType.Long,
            "Float" => ZType.Float,
            "Double" => ZType.Double,
            "Byte" => ZType.Byte,
            "Char" => ZType.Char,
            "Bool" => ZType.Bool,
            "String" => ZType.String,
            "Unit" => ZType.Unit,
            _ => new ZType.ZNamedType(a.Text, [])
        },
        SExpr.SList list when list.Items.Count >= 2 &&
            list.Items[0] is SExpr.Atom { Text: "Fn" } =>
            ParseFuncType(list),
        SExpr.SList list when list.Items.Count >= 1 =>
            ParseNamedType(list),
        _ => ZType.Unit
    };

    private ZType ParseFuncType(SExpr.SList list)
    {
        // (Fn [A B] C)
        if (list.Items.Count == 3 && list.Items[1] is SExpr.BracketList paramsBracket)
        {
            var pars = paramsBracket.Items.Select(ParseTypeExpr).ToList();
            var ret = ParseTypeExpr(list.Items[2]);
            return new ZType.ZFuncType(pars, ret);
        }

        _diagnostics.Error("Invalid function type syntax", list.Span);
        return ZType.Unit;
    }

    private ZType ParseNamedType(SExpr.SList list)
    {
        // (Result Int String) or (Option Int) etc.
        var name = ((SExpr.Atom)list.Items[0]).Text;
        var args = new List<ZType>();
        for (int i = 1; i < list.Items.Count; i++)
            args.Add(ParseTypeExpr(list.Items[i]));
        return new ZType.ZNamedType(name, args);
    }
}

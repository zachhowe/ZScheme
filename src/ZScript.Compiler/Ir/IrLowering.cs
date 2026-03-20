namespace ZScript.Compiler.Ir;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Types;

public sealed class IrLowering
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, (string TypeName, string MethodName)> _clrImports = new();
    private readonly List<string> _clrNamespaces = new();
    private readonly Dictionary<string, string> _unionCtors = new();
    private readonly Dictionary<string, List<string>> _recordCtors = new();

    private static readonly HashSet<string> BinaryOps =
        ["+", "-", "*", "/", "%", "=", "!=", "<", ">", "<=", ">=", "and", "or"];

    private static readonly HashSet<string> UnaryOps = ["not"];

    private static readonly Dictionary<string, (string RuntimeType, string? CaseName)> BuiltinCtors = new()
    {
        ["Ok"]    = ("ZsResult", "Ok"),
        ["Err"]   = ("ZsResult", "Err"),
        ["Some"]  = ("ZsOption", "Some"),
        ["None"]  = ("ZsOption", "None"),
        ["Error"] = ("ZsError", null),
    };


    public IrLowering(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void RegisterClrImport(string alias, string typeName, string methodName)
        => _clrImports[alias] = (typeName, methodName);

    public IReadOnlyDictionary<string, (string TypeName, string MethodName)> ClrImports => _clrImports;
    public IReadOnlyList<string> ClrNamespaces => _clrNamespaces;

    public IrNode Lower(AstNode node) => node switch
    {
        AstNode.Program p => LowerProgram(p),
        AstNode.IntLit n => new IrNode.IntConst(n.Value) { Type = ZType.Int },
        AstNode.FloatLit n => new IrNode.FloatConst(n.Value) { Type = ZType.Float },
        AstNode.BoolLit n => new IrNode.BoolConst(n.Value) { Type = ZType.Bool },
        AstNode.StringLit n => new IrNode.StringConst(n.Value) { Type = ZType.String },
        AstNode.UnitLit _ => new IrNode.UnitConst() { Type = ZType.Unit },
        AstNode.Name n when n.Value == "None" =>
            new IrNode.BuiltinCtorCall("ZsOption", "None", [], ExtractTypeArgs(n.ResolvedType))
            { Type = n.ResolvedType ?? ZType.Unit },
        AstNode.Name n => new IrNode.Var(n.Value) { Type = n.ResolvedType ?? ZType.Unit },
        AstNode.Let n => LowerLet(n),
        AstNode.If n => LowerIf(n),
        AstNode.Apply n => LowerApply(n),
        AstNode.Lambda n => LowerLambda(n),
        AstNode.Define n => LowerDefine(n),
        AstNode.DefineValue n => LowerDefineValue(n),
        AstNode.RecordDecl n => LowerRecordDecl(n),
        AstNode.UnionDecl n => LowerUnionDecl(n),
        AstNode.Match n => LowerMatch(n),
        AstNode.Pipe n => LowerPipe(n),
        AstNode.Partial n => LowerPartial(n),
        AstNode.ListExpr n => LowerListExpr(n),
        AstNode.VectorExpr n => LowerVectorExpr(n),
        AstNode.MapExpr n => LowerMapExpr(n),
        AstNode.Try n => Lower(n.Body),
        AstNode.Propagate n => new IrNode.Propagate(Lower(n.Expr), n.Expr.ResolvedType ?? ZType.Unit)
            { Type = n.ResolvedType ?? ZType.Unit },
        AstNode.Catch n => new IrNode.TryCatch(Lower(n.Body)) { Type = n.ResolvedType ?? ZType.Unit },
        AstNode.ObjectExpr n => LowerObjectExpr(n),
        AstNode.ImportClr n => LowerImportClr(n),
        AstNode.NamespaceDecl _ => new IrNode.UnitConst() { Type = ZType.Unit },
        AstNode.ModuleDecl _ => new IrNode.UnitConst() { Type = ZType.Unit },
        AstNode.Import _ => new IrNode.UnitConst() { Type = ZType.Unit },
        AstNode.Export _ => new IrNode.UnitConst() { Type = ZType.Unit },
        AstNode.TestCase n => LowerTestCase(n),
        _ => new IrNode.UnitConst() { Type = ZType.Unit }
    };

    private IrNode LowerProgram(AstNode.Program p)
    {
        var nodes = p.TopLevelForms.Select(Lower).ToList();
        return new IrNode.Seq(nodes) { Type = p.ResolvedType ?? ZType.Unit };
    }

    private IrNode LowerLet(AstNode.Let n) =>
        new IrNode.Let(n.VarName, Lower(n.Value), Lower(n.Body))
        {
            Type = n.ResolvedType ?? ZType.Unit
        };

    private IrNode LowerIf(AstNode.If n) =>
        new IrNode.If(Lower(n.Condition), Lower(n.Then), Lower(n.Else))
        {
            Type = n.ResolvedType ?? ZType.Unit
        };

    private IrNode LowerApply(AstNode.Apply n)
    {
        // Check for binary operator optimization
        if (n.Function is AstNode.Name name && n.Args.Count == 2 && BinaryOps.Contains(name.Value))
        {
            return new IrNode.BinOp(name.Value, Lower(n.Args[0]), Lower(n.Args[1]))
            {
                Type = n.ResolvedType ?? ZType.Unit
            };
        }

        // Check for unary operator
        if (n.Function is AstNode.Name uname && n.Args.Count == 1 && UnaryOps.Contains(uname.Value))
        {
            return new IrNode.UnaryOp(uname.Value, Lower(n.Args[0]))
            {
                Type = n.ResolvedType ?? ZType.Unit
            };
        }

        // Check for builtin functions (string-append, int->string, etc.)
        if (n.Function is AstNode.Name builtinName)
        {
            switch (builtinName.Value)
            {
                case "string-append" when n.Args.Count == 2:
                    return new IrNode.BinOp("+", Lower(n.Args[0]), Lower(n.Args[1]))
                    { Type = n.ResolvedType ?? ZType.String };
                case "int->string" when n.Args.Count == 1:
                    return new IrNode.MethodCall(Lower(n.Args[0]), "ToString", [], false, false)
                    { Type = n.ResolvedType ?? ZType.String };
                case "float->int" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToInt32", [Lower(n.Args[0])])
                    { Type = n.ResolvedType ?? ZType.Int };
                case "int->float" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToSingle", [Lower(n.Args[0])])
                    { Type = n.ResolvedType ?? ZType.Float };
            }
        }

        // Check for collection method call (list/head, vector/map, map/get, etc.)
        if (n.Function is AstNode.Name cmName)
        {
            var loweredArgs = n.Args.Select(Lower).ToList();
            var result = TryLowerCollectionMethod(cmName.Value, loweredArgs, n.ResolvedType ?? ZType.Unit);
            if (result is not null) return result;
        }

        // Check for CLR import call
        if (n.Function is AstNode.Name clrName && _clrImports.TryGetValue(clrName.Value, out var clrInfo))
        {
            return new IrNode.ClrCall(clrInfo.TypeName, clrInfo.MethodName, n.Args.Select(Lower).ToList())
            {
                Type = n.ResolvedType ?? ZType.Unit
            };
        }

        // Check for built-in constructor call (Ok, Err, Some, Error)
        if (n.Function is AstNode.Name ctorName && BuiltinCtors.TryGetValue(ctorName.Value, out var info))
        {
            var typeArgs = ExtractTypeArgs(n.ResolvedType);
            return new IrNode.BuiltinCtorCall(info.RuntimeType, info.CaseName,
                n.Args.Select(Lower).ToList(), typeArgs) { Type = n.ResolvedType ?? ZType.Unit };
        }

        // Check for user-defined union constructor call
        if (n.Function is AstNode.Name uName && _unionCtors.TryGetValue(uName.Value, out var unionName))
        {
            return new IrNode.UnionCaseNew(unionName, uName.Value, n.Args.Select(Lower).ToList())
            { Type = n.ResolvedType ?? ZType.Unit };
        }

        // Check for record constructor call
        if (n.Function is AstNode.Name rName && _recordCtors.TryGetValue(rName.Value, out var fieldNames))
        {
            var fields = fieldNames.Zip(n.Args, (name, arg) => (name, Lower(arg))).ToList();
            return new IrNode.RecordNew(rName.Value, fields) { Type = n.ResolvedType ?? ZType.Unit };
        }

        return new IrNode.Call(Lower(n.Function), n.Args.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode? TryLowerCollectionMethod(string name, List<IrNode> args, ZType resultType)
    {
        if (!BuiltinMethodRegistry.CollectionMethods.TryGetValue(name, out var info) || args.Count == 0)
            return null;

        var receiver = args[0];
        var methodArgs = args.Skip(1).ToList();

        return new IrNode.MethodCall(receiver, info.CSharpName, methodArgs, info.IsProperty, info.IsIndexer)
        {
            Type = resultType
        };
    }

    private IrNode LowerLambda(AstNode.Lambda n)
    {
        var parms = n.ResolvedType is ZType.ZFuncType ft2
            ? n.Params.Select((p, i) => new IrParam(p.Name, i < ft2.Params.Count ? ft2.Params[i] : p.TypeAnnotation ?? ZType.Unit)).ToList()
            : n.Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
        var body = Lower(n.Body);
        var retType = n.ResolvedType is ZType.ZFuncType ft ? ft.Return : ZType.Unit;

        // For now, emit as a FuncDef with a generated name (closure conversion later)
        var name = $"__lambda_{n.Span.Line}_{n.Span.Column}";
        return new IrNode.FuncDef(name, parms, retType, body, false)
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerDefine(AstNode.Define n)
    {
        var parms = n.Params.Select(p =>
            new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit, LowerAttributes(p.Attributes))).ToList();
        var body = Lower(n.Body);

        var retType = n.ReturnTypeAnnotation ?? (n.ResolvedType is ZType.ZFuncType ft ? ft.Return : ZType.Unit);
        var isSelfRecursive = BodyReferences(n.Body, n.FnName);

        return new IrNode.FuncDef(n.FnName, parms, retType, body, isSelfRecursive, LowerAttributes(n.Attributes))
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerDefineValue(AstNode.DefineValue n) =>
        new IrNode.Let(n.VarName, Lower(n.Value), new IrNode.UnitConst() { Type = ZType.Unit })
        {
            Type = ZType.Unit
        };

    private IrNode LowerRecordDecl(AstNode.RecordDecl n)
    {
        var fields = n.Fields.Select(f => new IrField(f.Name, f.TypeAnnotation, LowerAttributes(f.Attributes))).ToList();
        _recordCtors[n.RecordName] = n.Fields.Select(f => f.Name).ToList();
        return new IrNode.RecordDecl(n.RecordName, n.TypeParams.ToList(), fields, LowerAttributes(n.Attributes))
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerUnionDecl(AstNode.UnionDecl n)
    {
        var cases = n.Cases.Select(c =>
            new IrUnionCase(c.Name,
                c.Fields.Select(f => new IrField(f.Name, f.TypeAnnotation, LowerAttributes(f.Attributes))).ToList())).ToList();

        // Register union case names for constructor lowering
        foreach (var c in n.Cases)
            _unionCtors[c.Name] = n.UnionName;

        return new IrNode.UnionDecl(n.UnionName, n.TypeParams.ToList(), cases, LowerAttributes(n.Attributes))
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerMatch(AstNode.Match n)
    {
        var scrutinee = Lower(n.Scrutinee);
        var arms = n.Arms.Select(a =>
            new IrMatchArm(LowerPattern(a.Pattern), Lower(a.Body))).ToList();
        return new IrNode.Match(scrutinee, arms) { Type = n.ResolvedType ?? ZType.Unit };
    }

    private IrPattern LowerPattern(Pattern p) => p switch
    {
        Pattern.Wildcard => new IrPattern.Wildcard(),
        Pattern.Variable v => new IrPattern.Variable(v.Name),
        Pattern.Literal l => new IrPattern.Literal(l.Value),
        Pattern.Constructor c =>
            new IrPattern.Constructor(c.Name, c.Fields.Select(LowerPattern).ToList()),
        _ => new IrPattern.Wildcard()
    };

    private IrNode LowerPipe(AstNode.Pipe n)
    {
        // Desugar: (|> x (f a) (g b)) => (g (f x a) b)
        var current = Lower(n.Initial);

        foreach (var step in n.Steps)
        {
            if (step is AstNode.Apply apply)
            {
                // Check for collection method in pipe: (|> xs (list/map f))
                if (apply.Function is AstNode.Name applyName)
                {
                    var args = new List<IrNode> { current };
                    args.AddRange(apply.Args.Select(Lower));
                    var methodResult = TryLowerCollectionMethod(applyName.Value, args, step.ResolvedType ?? ZType.Unit);
                    if (methodResult is not null)
                    {
                        current = methodResult;
                        continue;
                    }
                }

                var callArgs = new List<IrNode> { current };
                callArgs.AddRange(apply.Args.Select(Lower));
                current = new IrNode.Call(Lower(apply.Function), callArgs)
                {
                    Type = step.ResolvedType ?? ZType.Unit
                };
            }
            else if (step is AstNode.Name stepName)
            {
                // Check for collection property in pipe: (|> xs list/head)
                var args = new List<IrNode> { current };
                var methodResult = TryLowerCollectionMethod(stepName.Value, args, step.ResolvedType ?? ZType.Unit);
                if (methodResult is not null)
                {
                    current = methodResult;
                    continue;
                }

                current = new IrNode.Call(Lower(step), [current])
                {
                    Type = step.ResolvedType ?? ZType.Unit
                };
            }
        }

        return current;
    }

    private IrNode LowerPartial(AstNode.Partial n)
    {
        // Desugar partial to a lambda wrapper
        // (partial f a b) with remaining params p1, p2 =>
        // fn(p1, p2) => f(a, b, p1, p2)
        var func = Lower(n.Function);
        var appliedArgs = n.Args.Select(Lower).ToList();

        if (n.ResolvedType is ZType.ZFuncType resultFt)
        {
            var remainingParams = new List<IrParam>();
            var callArgs = new List<IrNode>(appliedArgs);

            for (int i = 0; i < resultFt.Params.Count; i++)
            {
                var pName = $"__p{i}";
                remainingParams.Add(new IrParam(pName, resultFt.Params[i]));
                callArgs.Add(new IrNode.Var(pName) { Type = resultFt.Params[i] });
            }

            var call = new IrNode.Call(func, callArgs) { Type = resultFt.Return };
            var lambdaName = $"__partial_{n.Span.Line}_{n.Span.Column}";
            return new IrNode.FuncDef(lambdaName, remainingParams, resultFt.Return, call, false)
            {
                Type = n.ResolvedType
            };
        }

        return func;
    }

    private IrNode LowerListExpr(AstNode.ListExpr n) =>
        new IrNode.ListNew(n.Elements.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };

    private IrNode LowerVectorExpr(AstNode.VectorExpr n) =>
        new IrNode.VectorNew(n.Elements.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };

    private IrNode LowerMapExpr(AstNode.MapExpr n) =>
        new IrNode.MapNew(n.Entries.Select(e => (Lower(e.Key), Lower(e.Value))).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };

    private IrNode LowerObjectExpr(AstNode.ObjectExpr n)
    {
        var methods = n.Methods.Select(m =>
        {
            var parms = m.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var body = Lower(m.Body);
            var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
            return new IrObjectMethod(m.Name, parms, retType, body);
        }).ToList();

        return new IrNode.ObjectExpr(n.InterfaceNames.ToList(), methods)
        {
            Type = n.ResolvedType ?? new ZType.ZNamedType(n.InterfaceNames[0], [])
        };
    }

    private IrNode LowerImportClr(AstNode.ImportClr n)
    {
        foreach (var import in n.Imports)
        {
            var slashIndex = import.QualifiedName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                var typeName = import.QualifiedName[..slashIndex];
                var methodName = import.QualifiedName[(slashIndex + 1)..];
                _clrImports[import.Alias] = (typeName, methodName);
            }
        }
        foreach (var ns in n.Namespaces)
            _clrNamespaces.Add(ns);
        return new IrNode.UnitConst() { Type = ZType.Unit };
    }

    private IrNode LowerTestCase(AstNode.TestCase n)
    {
        // Sanitize test name to valid C# method name
        var methodName = n.TestName
            .Replace(" ", "_")
            .Replace("-", "_")
            .Replace("'", "")
            .Replace("\"", "")
            .Replace("?", "_q")
            .Replace("!", "_bang");

        // Lower body expressions, then chain them as nested lets with _ bindings
        var loweredBody = LowerBodySequence(n.Body);

        return new IrNode.TestCaseDef(n.TestName, methodName, loweredBody)
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerBodySequence(IReadOnlyList<AstNode> exprs)
    {
        if (exprs.Count == 0)
            return new IrNode.UnitConst() { Type = ZType.Unit };

        if (exprs.Count == 1)
            return Lower(exprs[0]);

        // Chain as nested lets: let [_ e1] (let [_ e2] ... en)
        var result = Lower(exprs[^1]);
        for (int i = exprs.Count - 2; i >= 0; i--)
        {
            result = new IrNode.Let($"__seq_{i}", Lower(exprs[i]), result)
            {
                Type = ZType.Unit
            };
        }
        return result;
    }

    private static IReadOnlyList<ZType> ExtractTypeArgs(ZType? type) => type switch
    {
        ZType.ZNamedType nt => nt.TypeArgs,
        _ => []
    };

    private static IReadOnlyList<IrAttribute>? LowerAttributes(IReadOnlyList<AttributeDecl>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return null;
        return attrs.Select(a => new IrAttribute(a.Name, a.PositionalArgs, a.NamedArgs)).ToList();
    }

    private static bool BodyReferences(AstNode node, string name) => node switch
    {
        AstNode.Name n => n.Value == name,
        AstNode.Apply a =>
            BodyReferences(a.Function, name) || a.Args.Any(arg => BodyReferences(arg, name)),
        AstNode.Let l =>
            BodyReferences(l.Value, name) || BodyReferences(l.Body, name),
        AstNode.If i =>
            BodyReferences(i.Condition, name) || BodyReferences(i.Then, name) || BodyReferences(i.Else, name),
        AstNode.Lambda lam => BodyReferences(lam.Body, name),
        AstNode.Match m =>
            BodyReferences(m.Scrutinee, name) || m.Arms.Any(a => BodyReferences(a.Body, name)),
        _ => false
    };
}

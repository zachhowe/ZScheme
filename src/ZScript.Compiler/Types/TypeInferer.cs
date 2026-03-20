namespace ZScript.Compiler.Types;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;

public sealed class TypeInferer
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Substitution _subst = new();
    private readonly Unifier _unifier;
    private int _nextTypeVar;

    public TypeInferer(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _unifier = new Unifier(_subst, diagnostics);
    }

    public DiagnosticBag Diagnostics => _diagnostics;
    public Substitution Substitution => _subst;

    public ZType FreshVar() => new ZType.ZTypeVar(_nextTypeVar++);

    public ZType Infer(AstNode node, TypeEnv env) => node switch
    {
        AstNode.IntLit n => Assign(n, ZType.Int),
        AstNode.FloatLit n => Assign(n, ZType.Float),
        AstNode.BoolLit n => Assign(n, ZType.Bool),
        AstNode.StringLit n => Assign(n, ZType.String),
        AstNode.UnitLit n => Assign(n, ZType.Unit),
        AstNode.Name n => InferName(n, env),
        AstNode.Let n => InferLet(n, env),
        AstNode.If n => InferIf(n, env),
        AstNode.Lambda n => InferLambda(n, env),
        AstNode.Apply n => InferApply(n, env),
        AstNode.Define n => InferDefine(n, env),
        AstNode.DefineValue n => InferDefineValue(n, env),
        AstNode.Program n => InferProgram(n, env),
        AstNode.Pipe n => InferPipe(n, env),
        AstNode.Partial n => InferPartial(n, env),
        AstNode.Match n => InferMatch(n, env),
        AstNode.RecordDecl n => InferRecordDecl(n, env),
        AstNode.UnionDecl n => InferUnionDecl(n, env),
        AstNode.ListExpr n => InferListExpr(n, env),
        AstNode.VectorExpr n => InferVectorExpr(n, env),
        AstNode.MapExpr n => InferMapExpr(n, env),
        AstNode.Try n => InferTry(n, env),
        AstNode.Propagate n => InferPropagate(n, env),
        AstNode.Catch n => InferCatch(n, env),
        AstNode.ObjectExpr n => InferObjectExpr(n, env),
        AstNode.ClrNew n => InferClrNew(n, env),
        AstNode.ImportClr n => InferImportClr(n, env),
        AstNode.NamespaceDecl n => Assign(n, ZType.Unit),
        AstNode.ModuleDecl n => Assign(n, ZType.Unit),
        AstNode.Import n => Assign(n, ZType.Unit),
        AstNode.Export n => Assign(n, ZType.Unit),
        AstNode.TestCase n => InferTestCase(n, env),
        _ => ReportUnknown(node)
    };

    private ZType Assign(AstNode node, ZType type)
    {
        node.ResolvedType = type;
        return type;
    }

    private ZType InferName(AstNode.Name node, TypeEnv env)
    {
        var type = env.Lookup(node.Value);
        if (type is null)
        {
            _diagnostics.Error($"Undefined variable: '{node.Value}'", node.Span);
            var tv = FreshVar();
            return Assign(node, tv);
        }

        var instantiated = Instantiate(type);
        return Assign(node, instantiated);
    }

    private ZType InferLet(AstNode.Let node, TypeEnv env)
    {
        // Infer the value type
        var valueType = Infer(node.Value, env);

        // Generalize if the value is not an application (value restriction)
        var generalized = Generalize(valueType, env);

        // Extend env with the binding
        var childEnv = env.CreateChild();
        childEnv.Define(node.VarName, generalized);

        // Infer body
        var bodyType = Infer(node.Body, childEnv);
        return Assign(node, bodyType);
    }

    private ZType InferIf(AstNode.If node, TypeEnv env)
    {
        var condType = Infer(node.Condition, env);
        _unifier.Unify(condType, ZType.Bool, node.Condition.Span);

        var thenType = Infer(node.Then, env);
        var elseType = Infer(node.Else, env);
        _unifier.Unify(thenType, elseType, node.Span);

        return Assign(node, thenType);
    }

    private ZType InferLambda(AstNode.Lambda node, TypeEnv env)
    {
        var childEnv = env.CreateChild();
        var paramTypes = new List<ZType>();

        foreach (var param in node.Params)
        {
            var pType = param.TypeAnnotation ?? FreshVar();
            paramTypes.Add(pType);
            childEnv.Define(param.Name, pType);
        }

        var bodyType = Infer(node.Body, childEnv);
        var funcType = new ZType.ZFuncType(paramTypes, bodyType);
        return Assign(node, funcType);
    }

    private ZType InferApply(AstNode.Apply node, TypeEnv env)
    {
        var funcType = Infer(node.Function, env);
        var argTypes = node.Args.Select(a => Infer(a, env)).ToList();

        var retType = FreshVar();
        var expectedFuncType = new ZType.ZFuncType(argTypes, retType);

        _unifier.Unify(funcType, expectedFuncType, node.Span);
        var resolvedRet = _subst.Apply(retType);
        return Assign(node, resolvedRet);
    }

    private ZType InferDefine(AstNode.Define node, TypeEnv env)
    {
        var childEnv = env.CreateChild();
        var paramTypes = new List<ZType>();

        foreach (var param in node.Params)
        {
            var pType = param.TypeAnnotation ?? FreshVar();
            paramTypes.Add(pType);
            childEnv.Define(param.Name, pType);
        }

        // For self-recursion, add the function itself to the environment
        var selfRetType = node.ReturnTypeAnnotation ?? FreshVar();
        var selfType = new ZType.ZFuncType(paramTypes, selfRetType);
        childEnv.Define(node.FnName, selfType);

        var bodyType = Infer(node.Body, childEnv);

        // Unify body type with declared return type
        _unifier.Unify(bodyType, selfRetType, node.Span);

        // Resolve the function type with substitutions
        var resolvedFuncType = _subst.Apply(selfType);
        var generalized = Generalize(resolvedFuncType, env);

        // Register in the outer environment
        env.Define(node.FnName, generalized);
        return Assign(node, resolvedFuncType);
    }

    private ZType InferDefineValue(AstNode.DefineValue node, TypeEnv env)
    {
        var valueType = Infer(node.Value, env);
        var generalized = Generalize(valueType, env);
        env.Define(node.VarName, generalized);
        return Assign(node, valueType);
    }

    private ZType InferProgram(AstNode.Program node, TypeEnv env)
    {
        ZType last = ZType.Unit;
        foreach (var form in node.TopLevelForms)
        {
            last = Infer(form, env);
        }
        return Assign(node, last);
    }

    private ZType InferPipe(AstNode.Pipe node, TypeEnv env)
    {
        // (|> x (f a) (g b)) => (g (f x a) b)
        var current = Infer(node.Initial, env);
        node.Initial.ResolvedType = current;

        foreach (var step in node.Steps)
        {
            if (step is AstNode.Apply apply)
            {
                // Insert current as first argument
                var funcType = Infer(apply.Function, env);
                var allArgTypes = new List<ZType> { current };
                foreach (var arg in apply.Args)
                    allArgTypes.Add(Infer(arg, env));

                var retType = FreshVar();
                _unifier.Unify(funcType, new ZType.ZFuncType(allArgTypes, retType), step.Span);
                current = _subst.Apply(retType);
                step.ResolvedType = current;
            }
            else if (step is AstNode.Name name)
            {
                // Apply as unary function
                var funcType = Infer(name, env);
                var retType = FreshVar();
                _unifier.Unify(funcType, new ZType.ZFuncType([current], retType), step.Span);
                current = _subst.Apply(retType);
            }
            else
            {
                _diagnostics.Error("Pipe step must be a function application or name", step.Span);
            }
        }

        return Assign(node, current);
    }

    private ZType InferPartial(AstNode.Partial node, TypeEnv env)
    {
        var funcType = Infer(node.Function, env);
        var appliedTypes = node.Args.Select(a => Infer(a, env)).ToList();

        if (_subst.Apply(funcType) is ZType.ZFuncType ft)
        {
            if (appliedTypes.Count >= ft.Params.Count)
            {
                _diagnostics.Error("Too many arguments for partial application", node.Span);
                return Assign(node, FreshVar());
            }

            // Unify supplied args with first N params
            for (int i = 0; i < appliedTypes.Count; i++)
                _unifier.Unify(ft.Params[i], appliedTypes[i], node.Span);

            // Remaining params form the new function type
            var remaining = ft.Params.Skip(appliedTypes.Count).ToList();
            var result = new ZType.ZFuncType(remaining, ft.Return);
            return Assign(node, _subst.Apply(result));
        }

        // Function type not yet known — create type vars
        var totalParams = appliedTypes.Count + 1; // at least one remaining
        var allParams = new List<ZType>();
        for (int i = 0; i < totalParams; i++)
            allParams.Add(i < appliedTypes.Count ? appliedTypes[i] : FreshVar());

        var retVar = FreshVar();
        _unifier.Unify(funcType, new ZType.ZFuncType(allParams, retVar), node.Span);

        var remainingAfter = allParams.Skip(appliedTypes.Count).ToList();
        var resultType = new ZType.ZFuncType(remainingAfter, retVar);
        return Assign(node, _subst.Apply(resultType));
    }

    private ZType InferMatch(AstNode.Match node, TypeEnv env)
    {
        var scrutType = Infer(node.Scrutinee, env);
        var resultType = FreshVar();

        foreach (var arm in node.Arms)
        {
            var armEnv = env.CreateChild();
            InferPattern(arm.Pattern, scrutType, armEnv);
            var bodyType = Infer(arm.Body, armEnv);
            _unifier.Unify(bodyType, resultType, arm.Body.Span);
        }

        return Assign(node, _subst.Apply(resultType));
    }

    private void InferPattern(Pattern pattern, ZType expected, TypeEnv env)
    {
        switch (pattern)
        {
            case Pattern.Wildcard w:
                w.ResolvedType = expected;
                break;
            case Pattern.Variable v:
                v.ResolvedType = expected;
                env.Define(v.Name, expected);
                break;
            case Pattern.Literal lit:
                lit.ResolvedType = lit.Value switch
                {
                    int => ZType.Int,
                    float => ZType.Float,
                    bool => ZType.Bool,
                    string => ZType.String,
                    _ => ZType.Unit
                };
                _unifier.Unify(lit.ResolvedType, expected, lit.Span);
                break;
            case Pattern.Constructor ctor:
                // Look up the constructor in the environment
                var ctorType = env.Lookup(ctor.Name);
                if (ctorType is not null)
                {
                    var instantiated = Instantiate(ctorType);
                    if (_subst.Apply(instantiated) is ZType.ZFuncType ft)
                    {
                        _unifier.Unify(ft.Return, expected, ctor.Span);
                        for (int i = 0; i < Math.Min(ctor.Fields.Count, ft.Params.Count); i++)
                        {
                            var fieldEnv = env;
                            InferPattern(ctor.Fields[i], ft.Params[i], fieldEnv);
                        }
                    }
                }
                else
                {
                    // Unknown constructor — just bind sub-patterns as fresh vars
                    foreach (var field in ctor.Fields)
                        InferPattern(field, FreshVar(), env);
                }
                ctor.ResolvedType = expected;
                break;
        }
    }

    private ZType InferRecordDecl(AstNode.RecordDecl node, TypeEnv env)
    {
        // Register the record type and its constructor
        var typeArgs = new List<ZType>();
        var localEnv = env.CreateChild();

        foreach (var tp in node.TypeParams)
        {
            var tv = FreshVar();
            typeArgs.Add(tv);
            localEnv.Define(tp, tv);
        }

        var recordType = new ZType.ZNamedType(node.RecordName, typeArgs);
        var fieldTypes = new List<ZType>();

        foreach (var field in node.Fields)
        {
            var ft = ResolveTypeInEnv(field.TypeAnnotation, localEnv);
            fieldTypes.Add(ft);
        }

        // Constructor: (field types...) -> RecordType
        var ctorType = new ZType.ZFuncType(fieldTypes, recordType);
        var generalized = node.TypeParams.Count > 0 ? Generalize(ctorType, env) : ctorType;
        env.Define(node.RecordName, generalized);

        // Register field accessors: RecordType -> FieldType
        for (int i = 0; i < node.Fields.Count; i++)
        {
            var accessorType = new ZType.ZFuncType([recordType], fieldTypes[i]);
            var genAccessor = node.TypeParams.Count > 0 ? Generalize(accessorType, env) : accessorType;
            env.Define(node.Fields[i].Name, genAccessor);
        }

        return Assign(node, ZType.Unit);
    }

    private ZType InferUnionDecl(AstNode.UnionDecl node, TypeEnv env)
    {
        var typeArgs = new List<ZType>();
        var localEnv = env.CreateChild();

        foreach (var tp in node.TypeParams)
        {
            var tv = FreshVar();
            typeArgs.Add(tv);
            localEnv.Define(tp, tv);
        }

        var unionType = new ZType.ZNamedType(node.UnionName, typeArgs);

        foreach (var @case in node.Cases)
        {
            var fieldTypes = new List<ZType>();
            foreach (var field in @case.Fields)
            {
                var ft = ResolveTypeInEnv(field.TypeAnnotation, localEnv);
                fieldTypes.Add(ft);
            }

            ZType ctorType;
            if (fieldTypes.Count > 0)
                ctorType = new ZType.ZFuncType(fieldTypes, unionType);
            else
                ctorType = unionType;

            var generalized = node.TypeParams.Count > 0 ? Generalize(ctorType, env) : ctorType;
            env.Define(@case.Name, generalized);
        }

        return Assign(node, ZType.Unit);
    }

    private ZType InferListExpr(AstNode.ListExpr node, TypeEnv env)
    {
        var elemType = FreshVar();
        foreach (var elem in node.Elements)
        {
            var t = Infer(elem, env);
            _unifier.Unify(t, elemType, elem.Span);
        }
        var listType = new ZType.ZNamedType("List", [_subst.Apply(elemType)]);
        return Assign(node, listType);
    }

    private ZType InferVectorExpr(AstNode.VectorExpr node, TypeEnv env)
    {
        var elemType = FreshVar();
        foreach (var elem in node.Elements)
        {
            var t = Infer(elem, env);
            _unifier.Unify(t, elemType, elem.Span);
        }
        var vecType = new ZType.ZNamedType("Vector", [_subst.Apply(elemType)]);
        return Assign(node, vecType);
    }

    private ZType InferMapExpr(AstNode.MapExpr node, TypeEnv env)
    {
        var keyType = FreshVar();
        var valType = FreshVar();
        foreach (var (key, value) in node.Entries)
        {
            _unifier.Unify(Infer(key, env), keyType, key.Span);
            _unifier.Unify(Infer(value, env), valType, value.Span);
        }
        var mapType = new ZType.ZNamedType("Map", [_subst.Apply(keyType), _subst.Apply(valType)]);
        return Assign(node, mapType);
    }

    private ZType InferTry(AstNode.Try node, TypeEnv env)
    {
        var bodyType = Infer(node.Body, env);
        return Assign(node, bodyType);
    }

    private ZType InferPropagate(AstNode.Propagate node, TypeEnv env)
    {
        var exprType = Infer(node.Expr, env);
        var okType = FreshVar();
        var errType = FreshVar();
        var expectedResultType = new ZType.ZNamedType("Result", [okType, errType]);
        _unifier.Unify(exprType, expectedResultType, node.Expr.Span);
        return Assign(node, _subst.Apply(okType));
    }

    private ZType InferCatch(AstNode.Catch node, TypeEnv env)
    {
        var bodyType = Infer(node.Body, env);
        var errorType = new ZType.ZNamedType("Error", []);
        var resultType = new ZType.ZNamedType("Result", [bodyType, errorType]);
        return Assign(node, resultType);
    }

    private ZType InferObjectExpr(AstNode.ObjectExpr node, TypeEnv env)
    {
        foreach (var method in node.Methods)
        {
            var methodEnv = env.CreateChild();
            foreach (var param in method.Params)
            {
                var pType = param.TypeAnnotation ?? FreshVar();
                methodEnv.Define(param.Name, pType);
            }
            Infer(method.Body, methodEnv);
        }

        var type = new ZType.ZNamedType(node.InterfaceNames[0], []);
        return Assign(node, type);
    }

    private ZType InferClrNew(AstNode.ClrNew node, TypeEnv env)
    {
        // Infer argument types
        foreach (var arg in node.Args)
            Infer(arg, env);

        // Resolve the CLR type
        var clrType = ClrInterop.FindType(node.TypeName);
        if (clrType is null)
        {
            _diagnostics.Error($"CLR type not found: '{node.TypeName}'", node.Span);
            return Assign(node, FreshVar());
        }

        // Validate a constructor with matching arg count exists
        var ctors = clrType.GetConstructors()
            .Where(c => c.GetParameters().Length == node.Args.Count)
            .ToArray();
        if (ctors.Length == 0)
        {
            _diagnostics.Error(
                $"No constructor on '{node.TypeName}' accepts {node.Args.Count} argument(s)", node.Span);
            return Assign(node, FreshVar());
        }

        return Assign(node, ClrInterop.MapClrTypeToZType(clrType));
    }

    private ZType InferImportClr(AstNode.ImportClr node, TypeEnv env)
    {
        var clr = new ClrInterop(_diagnostics);
        foreach (var import in node.Imports)
        {
            var method = clr.Resolve(import.QualifiedName, import.Span);
            if (method is not null)
            {
                var funcType = ClrInterop.MethodInfoToZFuncType(method);
                env.Define(import.Alias, funcType);
            }
        }
        return Assign(node, ZType.Unit);
    }

    private ZType InferTestCase(AstNode.TestCase node, TypeEnv env)
    {
        foreach (var expr in node.Body)
        {
            Infer(expr, env);
        }
        return Assign(node, ZType.Unit);
    }

    private ZType ResolveTypeInEnv(ZType type, TypeEnv env) => type switch
    {
        ZType.ZNamedType { Name: var name, TypeArgs: { Count: 0 } } =>
            env.Lookup(name) ?? type,
        ZType.ZNamedType nt =>
            new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(t => ResolveTypeInEnv(t, env)).ToList()),
        ZType.ZFuncType ft =>
            new ZType.ZFuncType(
                ft.Params.Select(p => ResolveTypeInEnv(p, env)).ToList(),
                ResolveTypeInEnv(ft.Return, env)),
        _ => type
    };

    private ZType Generalize(ZType type, TypeEnv env)
    {
        var resolved = _subst.Apply(type);
        var freeVars = Substitution.FreeVars(resolved);
        // In a proper implementation we'd subtract env's free vars
        if (freeVars.Count == 0)
            return resolved;
        return new ZType.ZForAllType(freeVars.ToList(), resolved);
    }

    private ZType Instantiate(ZType type)
    {
        if (type is not ZType.ZForAllType forall)
            return type;

        var mapping = new Dictionary<int, ZType>();
        foreach (var bv in forall.BoundVars)
            mapping[bv] = FreshVar();

        return InstantiateBody(forall.Body, mapping);
    }

    private ZType InstantiateBody(ZType type, Dictionary<int, ZType> mapping) => type switch
    {
        ZType.ZTypeVar tv =>
            mapping.TryGetValue(tv.Id, out var replacement) ? replacement : tv,
        ZType.ZFuncType ft =>
            new ZType.ZFuncType(
                ft.Params.Select(p => InstantiateBody(p, mapping)).ToList(),
                InstantiateBody(ft.Return, mapping)),
        ZType.ZNamedType nt =>
            new ZType.ZNamedType(nt.Name,
                nt.TypeArgs.Select(a => InstantiateBody(a, mapping)).ToList()),
        _ => type
    };

    private ZType ReportUnknown(AstNode node)
    {
        _diagnostics.Error($"Cannot type-check node: {node.GetType().Name}", node.Span);
        return ZType.Unit;
    }

    /// <summary>
    /// Resolves all type variables in the entire AST to their final types.
    /// Call this after inference is complete.
    /// </summary>
    public void Resolve(AstNode node)
    {
        if (node.ResolvedType is not null)
            node.ResolvedType = _subst.Apply(node.ResolvedType);

        switch (node)
        {
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms) Resolve(f);
                break;
            case AstNode.Define d:
                foreach (var _ in d.Params) { }
                Resolve(d.Body);
                break;
            case AstNode.DefineValue dv:
                Resolve(dv.Value);
                break;
            case AstNode.Let l:
                Resolve(l.Value);
                Resolve(l.Body);
                break;
            case AstNode.If i:
                Resolve(i.Condition);
                Resolve(i.Then);
                Resolve(i.Else);
                break;
            case AstNode.Lambda lam:
                Resolve(lam.Body);
                break;
            case AstNode.Apply app:
                Resolve(app.Function);
                foreach (var a in app.Args) Resolve(a);
                break;
            case AstNode.Pipe pipe:
                Resolve(pipe.Initial);
                foreach (var s in pipe.Steps) Resolve(s);
                break;
            case AstNode.Match m:
                Resolve(m.Scrutinee);
                foreach (var arm in m.Arms) Resolve(arm.Body);
                break;
            case AstNode.Partial part:
                Resolve(part.Function);
                foreach (var a in part.Args) Resolve(a);
                break;
            case AstNode.Try t:
                Resolve(t.Body);
                break;
            case AstNode.Propagate prop:
                Resolve(prop.Expr);
                break;
            case AstNode.Catch c:
                Resolve(c.Body);
                break;
            case AstNode.ListExpr le:
                foreach (var e in le.Elements) Resolve(e);
                break;
            case AstNode.VectorExpr ve:
                foreach (var e in ve.Elements) Resolve(e);
                break;
            case AstNode.MapExpr me:
                foreach (var (k, v) in me.Entries) { Resolve(k); Resolve(v); }
                break;
            case AstNode.ClrNew cn:
                foreach (var a in cn.Args) Resolve(a);
                break;
            case AstNode.ObjectExpr oe:
                foreach (var m in oe.Methods) Resolve(m.Body);
                break;
            case AstNode.TestCase tc:
                foreach (var e in tc.Body) Resolve(e);
                break;
        }
    }
}

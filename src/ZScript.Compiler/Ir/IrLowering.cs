using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Ir;

using static ClrImportKind;

public sealed class IrLowering
{
    private static readonly HashSet<string> BinaryOps =
        ["+", "-", "*", "/", "%", "=", "!=", "<", ">", "<=", ">=", "and", "or"];

    private static readonly HashSet<string> UnaryOps = ["not"];
    private readonly HashSet<string> _classFieldAccessors = new();
    private readonly HashSet<string> _classMethodAccessors = new();

    private readonly
        Dictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
            IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)> _clrImports = new();

    private readonly List<string> _clrNamespaces = new();
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, List<string>> _recordCtors = new();
    private readonly Dictionary<string, string> _unionCtors = new();


    public IrLowering(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
        IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)> ClrImports => _clrImports;

    public IReadOnlyDictionary<string, string> UnionCtors => _unionCtors;
    public IReadOnlyDictionary<string, List<string>> RecordCtors => _recordCtors;
    public IReadOnlyList<string> ClrNamespaces => _clrNamespaces;

    public void RegisterClrImport(string alias, string typeName, string methodName, int genericArity = 0,
        ClrImportKind kind = Static,
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints = null)
    {
        _clrImports[alias] = (typeName, methodName, genericArity, kind, constraints);
    }

    public void RegisterUnionCtor(string caseName, string unionName)
    {
        _unionCtors[caseName] = unionName;
    }

    public void RegisterRecordCtor(string recordName, List<string> fieldNames)
    {
        _recordCtors[recordName] = fieldNames;
        foreach (var fieldName in fieldNames)
            _classFieldAccessors.Add($"{recordName}/{fieldName}");
    }

    public IrNode Lower(AstNode node)
    {
        return node switch
        {
            AstNode.Program p => LowerProgram(p),
            AstNode.IntLit n => new IrNode.IntConst(n.Value) { Type = ZType.Int },
            AstNode.FloatLit n => new IrNode.FloatConst(n.Value) { Type = ZType.Float },
            AstNode.BoolLit n => new IrNode.BoolConst(n.Value) { Type = ZType.Bool },
            AstNode.StringLit n => new IrNode.StringConst(n.Value) { Type = ZType.String },
            AstNode.UnitLit _ => new IrNode.UnitConst { Type = ZType.Unit },
            AstNode.Name n when _unionCtors.ContainsKey(n.Value) =>
                new IrNode.UnionCaseNew(_unionCtors[n.Value], n.Value, [])
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
            AstNode.ArrayExpr n => LowerArrayExpr(n),
            AstNode.MapExpr n => LowerMapExpr(n),
            AstNode.Try n => Lower(n.Body),
            AstNode.Propagate n => new IrNode.Propagate(Lower(n.Expr), n.Expr.ResolvedType ?? ZType.Unit)
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.Catch n => new IrNode.TryCatch(Lower(n.Body)) { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.ObjectExpr n => LowerObjectExpr(n),
            AstNode.ClassDecl n => LowerClassDecl(n),
            AstNode.InterfaceDecl n => LowerInterfaceDecl(n),
            AstNode.SuperMethodCall n => new IrNode.SuperMethodCall(n.MethodName, n.Args.Select(Lower).ToList())
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.ClrNew n => new IrNode.ClrNew(n.TypeName, n.Args.Select(Lower).ToList())
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.Raise n => new IrNode.Throw(Lower(n.Expr))
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.DefineAsync n => LowerDefineAsync(n),
            AstNode.Await n => new IrNode.Await(Lower(n.Expr))
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.ImportClr n => LowerImportClr(n),
            AstNode.NamespaceDecl _ => new IrNode.UnitConst { Type = ZType.Unit },
            AstNode.ModuleDecl m => m.Body.Count > 0
                ? new IrNode.Seq(m.Body.Select(Lower).ToList()) { Type = ZType.Unit }
                : new IrNode.UnitConst { Type = ZType.Unit },
            AstNode.Import _ => new IrNode.UnitConst { Type = ZType.Unit },
            AstNode.Export _ => new IrNode.UnitConst { Type = ZType.Unit },
            _ => new IrNode.UnitConst { Type = ZType.Unit }
        };
    }

    private IrNode LowerProgram(AstNode.Program p)
    {
        var nodes = new List<IrNode>();
        foreach (var form in p.TopLevelForms)
        {
            var lowered = Lower(form);
            // Flatten nested Seq nodes (e.g. from module bodies) so emitters see a single flat list
            if (lowered is IrNode.Seq nested)
                nodes.AddRange(nested.Nodes);
            else
                nodes.Add(lowered);
        }

        return new IrNode.Seq(nodes) { Type = p.ResolvedType ?? ZType.Unit };
    }

    private IrNode LowerLet(AstNode.Let n)
    {
        return new IrNode.Let(n.VarName, Lower(n.Value), Lower(n.Body))
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerIf(AstNode.If n)
    {
        return new IrNode.If(Lower(n.Condition), Lower(n.Then), Lower(n.Else))
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerApply(AstNode.Apply n)
    {
        // Check for binary operator optimization
        if (n.Function is AstNode.Name name && n.Args.Count == 2 && BinaryOps.Contains(name.Value))
            return new IrNode.BinOp(name.Value, Lower(n.Args[0]), Lower(n.Args[1]))
            {
                Type = n.ResolvedType ?? ZType.Unit
            };

        // Check for unary operator
        if (n.Function is AstNode.Name uname && n.Args.Count == 1 && UnaryOps.Contains(uname.Value))
            return new IrNode.UnaryOp(uname.Value, Lower(n.Args[0]))
            {
                Type = n.ResolvedType ?? ZType.Unit
            };

        // Check for builtin functions (string-append, int->string, etc.)
        if (n.Function is AstNode.Name builtinName)
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
                case "mutable-array->array" when n.Args.Count == 1:
                    return new IrNode.ClrCall(
                        "System.Collections.Immutable.ImmutableArray", "Create",
                        [Lower(n.Args[0])], 1)
                    { Type = n.ResolvedType ?? ZType.Unit };
                case "array->mutable-array" when n.Args.Count == 1:
                    return new IrNode.ClrCall(
                        "System.Linq.Enumerable", "ToArray",
                        [Lower(n.Args[0])], 1)
                    { Type = n.ResolvedType ?? ZType.Unit };
                case "mutable-list->list" when n.Args.Count == 1:
                    return new IrNode.ClrCall(
                        "System.Collections.Immutable.ImmutableList", "CreateRange",
                        [Lower(n.Args[0])], 1)
                    { Type = n.ResolvedType ?? ZType.Unit };
                case "list->mutable-list" when n.Args.Count == 1:
                    return new IrNode.ClrCall(
                        "System.Linq.Enumerable", "ToList",
                        [Lower(n.Args[0])], 1)
                    { Type = n.ResolvedType ?? ZType.Unit };
                case "mutable-map->map" when n.Args.Count == 1:
                    return new IrNode.ClrCall(
                        "System.Collections.Immutable.ImmutableDictionary", "CreateRange",
                        [Lower(n.Args[0])], 2)
                    { Type = n.ResolvedType ?? ZType.Unit };
                case "map->mutable-map" when n.Args.Count == 1:
                    return new IrNode.ClrNew(
                        "System.Collections.Generic.Dictionary",
                        [Lower(n.Args[0])])
                    { Type = n.ResolvedType ?? ZType.Unit };
            }

        // Check for class/interface slash-syntax accessor (ClassName/field or ClassName/method)
        if (n.Function is AstNode.Name slashName && n.Args.Count >= 1)
        {
            if (_classFieldAccessors.Contains(slashName.Value))
            {
                var slashIdx = slashName.Value.IndexOf('/');
                var fieldName = slashName.Value[(slashIdx + 1)..];
                return new IrNode.MethodCall(Lower(n.Args[0]), fieldName, [], true, false)
                {
                    Type = n.ResolvedType ?? ZType.Unit
                };
            }

            if (_classMethodAccessors.Contains(slashName.Value))
            {
                var slashIdx = slashName.Value.IndexOf('/');
                var methodName = slashName.Value[(slashIdx + 1)..];
                var restArgs = n.Args.Skip(1).Select(Lower).ToList();
                return new IrNode.MethodCall(Lower(n.Args[0]), methodName, restArgs, false, false)
                {
                    Type = n.ResolvedType ?? ZType.Unit
                };
            }
        }

        // Check for CLR import call
        if (n.Function is AstNode.Name clrName && _clrImports.TryGetValue(clrName.Value, out var clrInfo))
        {
            if (clrInfo.Kind != Static && n.Args.Count >= 1)
            {
                // Instance member: first arg is receiver, rest are method args
                var receiver = Lower(n.Args[0]);
                var methodArgs = n.Args.Skip(1).Select(Lower).ToList();
                return new IrNode.MethodCall(receiver, clrInfo.MethodName, methodArgs,
                    clrInfo.Kind == InstanceProperty, clrInfo.Kind == InstanceIndexer,
                    clrInfo.Kind == InstancePropertySet,
                    clrInfo.Kind == InstanceIndexerSet)
                {
                    Type = n.ResolvedType ?? ZType.Unit
                };
            }

            return new IrNode.ClrCall(clrInfo.TypeName, clrInfo.MethodName, n.Args.Select(Lower).ToList(),
                clrInfo.GenericArity)
            {
                Type = n.ResolvedType ?? ZType.Unit
            };
        }

        // Check for union constructor call
        if (n.Function is AstNode.Name uName && _unionCtors.TryGetValue(uName.Value, out var unionName))
            return new IrNode.UnionCaseNew(unionName, uName.Value, n.Args.Select(Lower).ToList())
                { Type = n.ResolvedType ?? ZType.Unit };

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

    private IrNode LowerLambda(AstNode.Lambda n)
    {
        var parms = n.ResolvedType is ZType.ZFuncType ft2
            ? n.Params.Select((p, i) =>
                new IrParam(p.Name, i < ft2.Params.Count ? ft2.Params[i] : p.TypeAnnotation ?? ZType.Unit)).ToList()
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
        var funcType = n.ResolvedType as ZType.ZFuncType;
        var parms = n.Params.Select((p, i) =>
        {
            var inferredType = funcType is not null && i < funcType.Params.Count
                ? funcType.Params[i]
                : p.TypeAnnotation ?? ZType.Unit;
            return new IrParam(p.Name, inferredType, LowerAttributes(p.Attributes));
        }).ToList();
        var body = Lower(n.Body);

        var retType = funcType?.Return ?? n.ReturnTypeAnnotation ?? ZType.Unit;
        var isSelfRecursive = BodyReferences(n.Body, n.FnName);

        var typeParams = ExtractFuncTypeParams(n.ResolvedType);
        var irConstraints = RemapDefineConstraints(n.TypeParamConstraints, n.Params, n.ReturnTypeAnnotation, funcType);
        return new IrNode.FuncDef(n.FnName, parms, retType, body, isSelfRecursive,
            typeParams.Count > 0 ? typeParams : null,
            LowerAttributes(n.Attributes),
            TypeParamConstraints: irConstraints)
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerDefineAsync(AstNode.DefineAsync n)
    {
        var asyncFuncType = n.ResolvedType as ZType.ZFuncType;
        var parms = n.Params.Select((p, i) =>
        {
            var inferredType = asyncFuncType is not null && i < asyncFuncType.Params.Count
                ? asyncFuncType.Params[i]
                : p.TypeAnnotation ?? ZType.Unit;
            return new IrParam(p.Name, inferredType, LowerAttributes(p.Attributes));
        }).ToList();
        var body = Lower(n.Body);

        // Unwrap Task<T> to get the inner return type for the IR
        ZType retType;
        if (n.ReturnTypeAnnotation is ZType.ZNamedType { Name: "Task", TypeArgs: [var innerT] })
            retType = innerT;
        else if (n.ReturnTypeAnnotation is ZType.ZNamedType { Name: "Task", TypeArgs: [] })
            retType = ZType.Unit;
        else
            retType = n.ReturnTypeAnnotation ?? (n.ResolvedType is ZType.ZFuncType ft ? ft.Return : ZType.Unit);

        var isSelfRecursive = BodyReferences(n.Body, n.FnName);

        var typeParams = ExtractFuncTypeParams(n.ResolvedType);
        var irConstraints =
            RemapDefineConstraints(n.TypeParamConstraints, n.Params, n.ReturnTypeAnnotation, asyncFuncType);
        return new IrNode.FuncDef(n.FnName, parms, retType, body, isSelfRecursive,
            typeParams.Count > 0 ? typeParams : null,
            LowerAttributes(n.Attributes), true,
            irConstraints)
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerDefineValue(AstNode.DefineValue n)
    {
        return new IrNode.Let(n.VarName, Lower(n.Value), new IrNode.UnitConst { Type = ZType.Unit })
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerRecordDecl(AstNode.RecordDecl n)
    {
        // Create a mapping from ^a-style params to T0-style for C# emission
        var typeParamMap = new Dictionary<string, string>();
        var csTypeParams = new List<string>();
        for (var i = 0; i < n.TypeParams.Count; i++)
        {
            var csName = $"T{i}";
            typeParamMap[n.TypeParams[i]] = csName;
            csTypeParams.Add(csName);
        }

        var fields = n.Fields.Select(f =>
                new IrField(f.Name, RemapTypeParams(f.TypeAnnotation, typeParamMap), LowerAttributes(f.Attributes)))
            .ToList();
        _recordCtors[n.RecordName] = n.Fields.Select(f => f.Name).ToList();
        foreach (var f in n.Fields)
            _classFieldAccessors.Add($"{n.RecordName}/{f.Name}");
        return new IrNode.RecordDecl(n.RecordName, csTypeParams, fields, LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams))
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerUnionDecl(AstNode.UnionDecl n)
    {
        // Create a mapping from ^a-style params to T0-style for C# emission
        var typeParamMap = new Dictionary<string, string>();
        var csTypeParams = new List<string>();
        for (var i = 0; i < n.TypeParams.Count; i++)
        {
            var csName = $"T{i}";
            typeParamMap[n.TypeParams[i]] = csName;
            csTypeParams.Add(csName);
        }

        var cases = n.Cases.Select(c =>
            new IrUnionCase(c.Name,
                c.Fields.Select(f => new IrField(f.Name, RemapTypeParams(f.TypeAnnotation, typeParamMap),
                    LowerAttributes(f.Attributes))).ToList())).ToList();

        // Register union case names for constructor lowering
        foreach (var c in n.Cases)
            _unionCtors[c.Name] = n.UnionName;

        return new IrNode.UnionDecl(n.UnionName, csTypeParams, cases, LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams))
        {
            Type = ZType.Unit
        };
    }

    private static ZType RemapTypeParams(ZType type, Dictionary<string, string> map)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt when map.TryGetValue(nt.Name, out var mapped) =>
                new ZType.ZNamedType(mapped, []),
            ZType.ZNamedType nt => new ZType.ZNamedType(nt.Name,
                nt.TypeArgs.Select(t => RemapTypeParams(t, map)).ToList()),
            ZType.ZFuncType ft => new ZType.ZFuncType(ft.Params.Select(p => RemapTypeParams(p, map)).ToList(),
                RemapTypeParams(ft.Return, map)),
            _ => type
        };
    }

    private IrNode LowerMatch(AstNode.Match n)
    {
        var scrutinee = Lower(n.Scrutinee);
        var arms = n.Arms.Select(a =>
            new IrMatchArm(LowerPattern(a.Pattern), Lower(a.Body))).ToList();
        return new IrNode.Match(scrutinee, arms) { Type = n.ResolvedType ?? ZType.Unit };
    }

    private IrPattern LowerPattern(Pattern p)
    {
        return p switch
        {
            Pattern.Wildcard => new IrPattern.Wildcard(),
            Pattern.Variable v => new IrPattern.Variable(v.Name),
            Pattern.Literal l => new IrPattern.Literal(l.Value),
            Pattern.Constructor c =>
                new IrPattern.Constructor(c.Name, c.Fields.Select(LowerPattern).ToList()),
            _ => new IrPattern.Wildcard()
        };
    }

    private IrNode LowerPipe(AstNode.Pipe n)
    {
        // Desugar: (|> x (f a) (g b)) => (g (f x a) b)
        var current = Lower(n.Initial);

        foreach (var step in n.Steps)
            if (step is AstNode.Apply apply)
            {
                var callArgs = new List<IrNode> { current };
                callArgs.AddRange(apply.Args.Select(Lower));
                current = new IrNode.Call(Lower(apply.Function), callArgs)
                {
                    Type = step.ResolvedType ?? ZType.Unit
                };
            }
            else if (step is AstNode.Name stepName)
            {
                current = new IrNode.Call(Lower(step), [current])
                {
                    Type = step.ResolvedType ?? ZType.Unit
                };
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

            for (var i = 0; i < resultFt.Params.Count; i++)
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

    private IrNode LowerListExpr(AstNode.ListExpr n)
    {
        return new IrNode.ListNew(n.Elements.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerArrayExpr(AstNode.ArrayExpr n)
    {
        return new IrNode.ArrayNew(n.Elements.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

    private IrNode LowerMapExpr(AstNode.MapExpr n)
    {
        return new IrNode.MapNew(n.Entries.Select(e => (Lower(e.Key), Lower(e.Value))).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit
        };
    }

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

        // Lower explicit constructor if present
        IrConstructor? irCtor = null;
        if (n.Constructor is { } ctor)
        {
            var ctorParams = ctor.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var superArgs = ctor.SuperArgs?.Select(Lower).ToList();
            var fieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Lower(fs.Value))).ToList();
            var bodyExprs = ctor.BodyExprs.Select(Lower).ToList();
            irCtor = new IrConstructor(ctorParams, superArgs, fieldSets, bodyExprs);
        }

        var defaultType = n.BaseClassName is not null
            ? new ZType.ZNamedType(n.BaseClassName, [])
            : new ZType.ZNamedType(n.InterfaceNames[0], []);

        return new IrNode.ObjectExpr(n.InterfaceNames.ToList(), methods,
            BaseClassName: n.BaseClassName, Constructor: irCtor)
        {
            Type = n.ResolvedType ?? defaultType
        };
    }

    private IrNode LowerClassDecl(AstNode.ClassDecl n)
    {
        var fields = n.Fields.Select(f => new IrField(f.Name, f.TypeAnnotation, LowerAttributes(f.Attributes)))
            .ToList();

        var methods = n.Methods.Select(m =>
        {
            var parms = m.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var body = Lower(m.Body);
            var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
            return new IrObjectMethod(m.Name, parms, retType, body, LowerAttributes(m.Attributes));
        }).ToList();

        // Register class name so (ClassName args...) lowers to RecordNew
        _recordCtors[n.ClassName] = n.Fields.Select(f => f.Name).ToList();

        // Register slash-syntax accessors for field/method lowering
        foreach (var f in n.Fields)
            _classFieldAccessors.Add($"{n.ClassName}/{f.Name}");
        foreach (var m in n.Methods)
            _classMethodAccessors.Add($"{n.ClassName}/{m.Name}");

        // Lower explicit constructor if present
        IrConstructor? irCtor = null;
        if (n.Constructor is { } ctor)
        {
            var ctorParams = ctor.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var superArgs = ctor.SuperArgs?.Select(Lower).ToList();
            var fieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Lower(fs.Value))).ToList();
            var bodyExprs = ctor.BodyExprs.Select(Lower).ToList();
            irCtor = new IrConstructor(ctorParams, superArgs, fieldSets, bodyExprs);
        }

        return new IrNode.ClassDecl(n.ClassName, n.TypeParams.ToList(), n.InterfaceNames.ToList(),
            fields, methods, IsOpen: n.IsOpen, BaseClassName: n.BaseClassName,
            Constructor: irCtor,
            Attributes: LowerAttributes(n.Attributes),
            TypeParamConstraints: RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams))
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerInterfaceDecl(AstNode.InterfaceDecl n)
    {
        var methods = n.Methods.Select(m =>
        {
            var parms = m.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var retType = m.ReturnTypeAnnotation;
            return new IrInterfaceMethodSignature(m.Name, parms, retType);
        }).ToList();

        // Register slash-syntax accessors for method lowering
        foreach (var m in n.Methods)
            _classMethodAccessors.Add($"{n.InterfaceName}/{m.Name}");

        return new IrNode.InterfaceDecl(n.InterfaceName, n.TypeParams.ToList(),
            n.BaseInterfaceNames.ToList(), methods, LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams))
        {
            Type = ZType.Unit
        };
    }

    private IrNode LowerImportClr(AstNode.ImportClr n)
    {
        foreach (var import in n.Imports)
        {
            // Remap constraint keys from ^k-style to T0-style using type param position
            var remappedConstraints = RemapClrImportConstraints(import);

            if (import.Kind != Static)
            {
                // Instance members use dot-separated: Type.Member
                var dotIndex = import.QualifiedName.LastIndexOf('.');
                if (dotIndex >= 0)
                {
                    var typeName = import.QualifiedName[..dotIndex];
                    var memberName = import.QualifiedName[(dotIndex + 1)..];
                    _clrImports[import.Alias] = (typeName, memberName, import.TypeParams.Count, import.Kind,
                        remappedConstraints);
                }
            }
            else
            {
                var slashIndex = import.QualifiedName.LastIndexOf('/');
                if (slashIndex >= 0)
                {
                    var typeName = import.QualifiedName[..slashIndex];
                    var methodName = import.QualifiedName[(slashIndex + 1)..];
                    _clrImports[import.Alias] = (typeName, methodName, import.TypeParams.Count, Static,
                        remappedConstraints);
                }
            }
        }

        foreach (var ns in n.Namespaces)
            _clrNamespaces.Add(ns);
        return new IrNode.UnitConst { Type = ZType.Unit };
    }

    private static IReadOnlyList<string> ExtractFuncTypeParams(ZType? funcType)
    {
        if (funcType is not ZType.ZFuncType ft) return [];
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        if (freeVars.Count == 0) return [];
        return freeVars.Select((_, i) => $"T{i}").ToList();
    }

    /// <summary>
    ///     Remaps constraint keys from ^k-style (AST) to T0-style (IR/codegen) for CLR imports.
    ///     For import-clr, type params are explicitly listed in order, so ^k at index 0 maps to T0.
    /// </summary>
    /// <summary>
    ///     Remaps constraint keys from ^a-style (AST) to T0-style (IR/codegen) for type declarations
    ///     (records, unions, classes, interfaces) where type params are explicitly listed.
    /// </summary>
    private static IReadOnlyDictionary<string, GenericConstraintKind>? RemapTypeDeclConstraints(
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints,
        IReadOnlyList<string> typeParams)
    {
        if (constraints is not { Count: > 0 }) return null;
        var remapped = new Dictionary<string, GenericConstraintKind>();
        for (var i = 0; i < typeParams.Count; i++)
            if (constraints.TryGetValue(typeParams[i], out var kind))
                remapped[$"T{i}"] = kind;
        return remapped.Count > 0 ? remapped : null;
    }

    private static IReadOnlyDictionary<string, GenericConstraintKind>? RemapClrImportConstraints(ClrImport import)
    {
        if (import.TypeParamConstraints is not { Count: > 0 }) return null;
        var remapped = new Dictionary<string, GenericConstraintKind>();
        for (var i = 0; i < import.TypeParams.Count; i++)
        {
            var paramName = import.TypeParams[i];
            if (import.TypeParamConstraints.TryGetValue(paramName, out var kind))
                remapped[$"T{i}"] = kind;
        }

        return remapped.Count > 0 ? remapped : null;
    }

    /// <summary>
    ///     Remaps constraint keys from ^k-style (AST) to T0-style (IR/codegen) for define forms.
    ///     Walks the annotation and resolved type trees in parallel to discover which ^k maps to which ZTypeVar ID,
    ///     then uses the same sorted ordering as ExtractFuncTypeParams.
    /// </summary>
    private static IReadOnlyDictionary<string, GenericConstraintKind>? RemapDefineConstraints(
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints,
        IReadOnlyList<Param> astParams,
        ZType? returnAnnotation,
        ZType.ZFuncType? funcType)
    {
        if (constraints is not { Count: > 0 } || funcType is null) return null;

        // Build ^k → ZTypeVar ID mapping by walking annotations and resolved types in parallel
        var nameToVarId = new Dictionary<string, int>();
        for (var i = 0; i < astParams.Count && i < funcType.Params.Count; i++)
            if (astParams[i].TypeAnnotation is { } annotation)
                CollectNameToVarMapping(annotation, funcType.Params[i], nameToVarId);
        if (returnAnnotation != null)
            CollectNameToVarMapping(returnAnnotation, funcType.Return, nameToVarId);

        // Build ZTypeVar ID → T{i} mapping using same ordering as ExtractFuncTypeParams
        var freeVars = Substitution.FreeVars(funcType).OrderBy(id => id).ToList();
        var varIdToTi = new Dictionary<int, string>();
        for (var i = 0; i < freeVars.Count; i++)
            varIdToTi[freeVars[i]] = $"T{i}";

        // Remap constraint keys
        var remapped = new Dictionary<string, GenericConstraintKind>();
        foreach (var (paramName, kind) in constraints)
            if (nameToVarId.TryGetValue(paramName, out var varId) && varIdToTi.TryGetValue(varId, out var tiName))
                remapped[tiName] = kind;
        return remapped.Count > 0 ? remapped : null;
    }

    private static void CollectNameToVarMapping(ZType annotation, ZType resolved, Dictionary<string, int> map)
    {
        switch (annotation)
        {
            case ZType.ZNamedType { TypeArgs.Count: 0 } named
                when named.Name.StartsWith('^') && resolved is ZType.ZTypeVar tv:
                map.TryAdd(named.Name, tv.Id);
                break;
            case ZType.ZNamedType na when resolved is ZType.ZNamedType nr && na.TypeArgs.Count == nr.TypeArgs.Count:
                for (var i = 0; i < na.TypeArgs.Count; i++)
                    CollectNameToVarMapping(na.TypeArgs[i], nr.TypeArgs[i], map);
                break;
            case ZType.ZFuncType fa when resolved is ZType.ZFuncType fr && fa.Params.Count == fr.Params.Count:
                for (var i = 0; i < fa.Params.Count; i++)
                    CollectNameToVarMapping(fa.Params[i], fr.Params[i], map);
                CollectNameToVarMapping(fa.Return, fr.Return, map);
                break;
        }
    }

    private static IReadOnlyList<IrAttribute>? LowerAttributes(IReadOnlyList<AttributeDecl>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return null;
        return attrs.Select(a => new IrAttribute(a.Name, a.PositionalArgs, a.NamedArgs)).ToList();
    }

    private static bool BodyReferences(AstNode node, string name)
    {
        return node switch
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
            AstNode.Raise r => BodyReferences(r.Expr, name),
            AstNode.Await a => BodyReferences(a.Expr, name),
            _ => false
        };
    }
}

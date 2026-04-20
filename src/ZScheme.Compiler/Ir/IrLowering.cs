using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

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
            IReadOnlyDictionary<string, GenericConstraintKind>? Constraints,
            IReadOnlyList<ClrInterop.OutParamInfo>? OutParams)> _clrImports = new();

    private readonly List<string> _clrNamespaces = new();
    private readonly DiagnosticBag _diagnostics;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> _outParamsByAlias;
    private readonly Dictionary<string, List<string>> _recordCtors = new();
    private readonly HashSet<string> _valueTypeRecords = new();
    private readonly Dictionary<string, string> _unionCtors = new();


    public IrLowering(DiagnosticBag diagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>>? outParamsByAlias = null)
    {
        _diagnostics = diagnostics;
        _outParamsByAlias = outParamsByAlias
                            ?? new Dictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>>();
    }

    public IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
        IReadOnlyDictionary<string, GenericConstraintKind>? Constraints,
        IReadOnlyList<ClrInterop.OutParamInfo>? OutParams)> ClrImports => _clrImports;

    public IReadOnlyDictionary<string, string> UnionCtors => _unionCtors;
    public IReadOnlyDictionary<string, List<string>> RecordCtors => _recordCtors;
    public IReadOnlyList<string> ClrNamespaces => _clrNamespaces;

    public void RegisterClrImport(string alias, string typeName, string methodName, int genericArity = 0,
        ClrImportKind kind = Static,
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints = null,
        IReadOnlyList<ClrInterop.OutParamInfo>? outParams = null)
    {
        _clrImports[alias] = (typeName, methodName, genericArity, kind, constraints, outParams);
    }

    /// <summary>
    ///     Applies out-param metadata from type inference to already-registered CLR imports.
    /// </summary>
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
            AstNode.NullLit n => new IrNode.NullConst { Type = n.ResolvedType ?? ZType.Unit },
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
            AstNode.TupleNew n => LowerTupleNew(n),
            AstNode.Match n => LowerMatch(n),
            AstNode.Partial n => LowerPartial(n),
            AstNode.ObjectExpr n => LowerObjectExpr(n),
            AstNode.ClassDecl n => LowerClassDecl(n),
            AstNode.InterfaceDecl n => LowerInterfaceDecl(n),
            AstNode.SuperMethodCall n => new IrNode.SuperMethodCall(n.MethodName, n.Args.Select(Lower).ToList())
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.SetField n => new IrNode.SetField(n.FieldName, Lower(n.Value))
                { Type = ZType.Unit },
            AstNode.ClrNew n => LowerClrNew(n),
            AstNode.Raise n => new IrNode.Throw(Lower(n.Expr))
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.DefineAsync n => LowerDefineAsync(n),
            AstNode.Await n => new IrNode.Await(Lower(n.Expr))
                { Type = n.ResolvedType ?? ZType.Unit },
            AstNode.WithHandlers n => LowerWithHandlers(n),
            AstNode.With n => LowerWith(n),
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

    private IrNode LowerWith(AstNode.With n)
    {
        var record = Lower(n.Record);
        var recordType = n.Record.ResolvedType ?? n.ResolvedType ?? record.Type;
        var typeName = recordType is ZType.ZNamedType named ? named.Name : "";
        var updates = n.Updates
            .Select(u => (u.FieldName, Lower(u.Value)))
            .ToList();
        return new IrNode.RecordWith(typeName, record, updates)
        {
            Type = n.ResolvedType ?? recordType
        };
    }

    private IrNode LowerWithHandlers(AstNode.WithHandlers n)
    {
        var body = Lower(n.Body);
        var handlers = n.Handlers.Select(h => new IrHandlerClause(
            h.ExceptionTypeName,
            h.BindingVarName,
            Lower(h.HandlerBody)
        )).ToList();

        return new IrNode.WithHandlers(body, handlers)
        {
            Type = n.ResolvedType ?? ZType.Unit
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
        return new IrNode.Let(n.VarName, Lower(n.Value), Lower(n.Body), n.TypeAnnotation)
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

    private IrNode LowerTupleNew(AstNode.TupleNew n)
    {
        var elements = n.Elements.Select(Lower).ToList();
        return new IrNode.TupleNew(elements) { Type = n.ResolvedType ?? ZType.Unit };
    }

    private IrNode LowerApply(AstNode.Apply n)
    {
        // Check for value/N tuple accessor
        if (n.Function is AstNode.Name tname && tname.Value.StartsWith("value/")
            && int.TryParse(tname.Value["value/".Length..], out var tupleIdx) && n.Args.Count == 1)
            return new IrNode.FieldGet(Lower(n.Args[0]), $"Item{tupleIdx + 1}")
            {
                Type = n.ResolvedType ?? ZType.Unit
            };

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
                case "string->int" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Int32", "Parse", [Lower(n.Args[0])])
                        { Type = n.ResolvedType ?? ZType.Int };
                case "float->int" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToInt32", [Lower(n.Args[0])])
                        { Type = n.ResolvedType ?? ZType.Int };
                case "int->float" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToSingle", [Lower(n.Args[0])])
                        { Type = n.ResolvedType ?? ZType.Float };
                case "double->float" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToSingle", [Lower(n.Args[0])])
                        { Type = n.ResolvedType ?? ZType.Float };
                case "float->double" when n.Args.Count == 1:
                    return new IrNode.ClrCall("System.Convert", "ToDouble", [Lower(n.Args[0])])
                        { Type = n.ResolvedType ?? ZType.Double };
                case "mutable-array->array" when n.Args.Count == 1:
                {
                    var lowered = Lower(n.Args[0]);
                    var elemTypes = ExtractCollectionTypeArgs(lowered.Type, 1);
                    return new IrNode.ClrCall(
                            "System.Collections.Immutable.ImmutableArray", "Create",
                            [lowered], 1, GenericTypeArgs: elemTypes)
                        { Type = n.ResolvedType ?? ZType.Unit };
                }
                case "array->mutable-array" when n.Args.Count == 1:
                {
                    var lowered = Lower(n.Args[0]);
                    var elemTypes = ExtractCollectionTypeArgs(lowered.Type, 1);
                    return new IrNode.ClrCall(
                            "System.Linq.Enumerable", "ToArray",
                            [lowered], 1, GenericTypeArgs: elemTypes)
                        { Type = n.ResolvedType ?? ZType.Unit };
                }
                case "mutable-list->list" when n.Args.Count == 1:
                {
                    var lowered = Lower(n.Args[0]);
                    var elemTypes = ExtractCollectionTypeArgs(lowered.Type, 1);
                    return new IrNode.ClrCall(
                            "System.Collections.Immutable.ImmutableList", "CreateRange",
                            [lowered], 1, GenericTypeArgs: elemTypes)
                        { Type = n.ResolvedType ?? ZType.Unit };
                }
                case "list->mutable-list" when n.Args.Count == 1:
                {
                    var lowered = Lower(n.Args[0]);
                    var elemTypes = ExtractCollectionTypeArgs(lowered.Type, 1);
                    return new IrNode.ClrCall(
                            "System.Linq.Enumerable", "ToList",
                            [lowered], 1, GenericTypeArgs: elemTypes)
                        { Type = n.ResolvedType ?? ZType.Unit };
                }
                case "mutable-map->map" when n.Args.Count == 1:
                {
                    var lowered = Lower(n.Args[0]);
                    var elemTypes = ExtractCollectionTypeArgs(lowered.Type, 2);
                    return new IrNode.ClrCall(
                            "System.Collections.Immutable.ImmutableDictionary", "CreateRange",
                            [lowered], 2, GenericTypeArgs: elemTypes)
                        { Type = n.ResolvedType ?? ZType.Unit };
                }
                case "map->mutable-map" when n.Args.Count == 1:
                    return new IrNode.ClrNew(
                            "System.Collections.Generic.Dictionary",
                            [],
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
                    clrInfo.Kind is InstancePropertySet or InstancePropertyInit,
                    clrInfo.Kind == InstanceIndexerSet,
                    clrInfo.Kind == InstancePropertyInit,
                    clrInfo.OutParams)
                {
                    Type = n.ResolvedType ?? ZType.Unit
                };
            }

            // Extract generic type args from the resolved return type and arg types
            var loweredArgs = n.Args.Select(Lower).ToList();
            IReadOnlyList<ZType>? genericTypeArgs = null;
            if (clrInfo.GenericArity > 0)
            {
                var returnType = n.ResolvedType ?? ZType.Unit;
                genericTypeArgs = ExtractGenericTypeArgsFromTypes(returnType, loweredArgs, clrInfo.GenericArity);
            }

            return new IrNode.ClrCall(clrInfo.TypeName, clrInfo.MethodName, loweredArgs,
                clrInfo.GenericArity, genericTypeArgs, clrInfo.OutParams)
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

        // Check for variadic function call — pack extra args into an array
        if (n.Function.ResolvedType is ZType.ZFuncType { IsVariadic: true } varFt
            || (n.Function.ResolvedType is ZType.ZForAllType { Body: ZType.ZFuncType { IsVariadic: true } innerFt2 }
                && (varFt = innerFt2) != null))
        {
            var fixedCount = varFt.Params.Count - 1;
            var elemType = varFt.Params[^1];
            var fixedArgs = n.Args.Take(fixedCount).Select(Lower).ToList();
            var variadicArgs = n.Args.Skip(fixedCount).Select(Lower).ToList();
            var arrayArg = new IrNode.MutableArrayNew(elemType, variadicArgs)
            {
                Type = new ZType.ZNamedType("Mutable-Array", [elemType])
            };
            fixedArgs.Add(arrayArg);
            return new IrNode.Call(Lower(n.Function), fixedArgs)
            {
                Type = n.ResolvedType ?? ZType.Unit
            };
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
            {
                var inferredType = i < ft2.Params.Count ? ft2.Params[i] : p.TypeAnnotation ?? ZType.Unit;
                if (p.IsVariadic)
                    inferredType = new ZType.ZNamedType("Mutable-Array", [inferredType]);
                return new IrParam(p.Name, inferredType, IsVariadic: p.IsVariadic);
            }).ToList()
            : n.Params.Select(p =>
            {
                var t = p.TypeAnnotation ?? ZType.Unit;
                if (p.IsVariadic)
                    t = new ZType.ZNamedType("Mutable-Array", [t]);
                return new IrParam(p.Name, t, IsVariadic: p.IsVariadic);
            }).ToList();
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
            // Variadic param becomes Mutable-Array[T]
            if (p.IsVariadic)
                inferredType = new ZType.ZNamedType("Mutable-Array", [inferredType]);
            return new IrParam(p.Name, inferredType, LowerAttributes(p.Attributes), p.IsVariadic);
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
            if (p.IsVariadic)
                inferredType = new ZType.ZNamedType("Mutable-Array", [inferredType]);
            return new IrParam(p.Name, inferredType, LowerAttributes(p.Attributes), p.IsVariadic);
        }).ToList();
        var body = Lower(n.Body);

        // Unwrap Task<T> to get the inner return type for the IR
        ZType retType;
        if (n.ReturnTypeAnnotation is ZType.ZNamedType
            { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var innerT] })
            retType = innerT;
        else if (n.ReturnTypeAnnotation is ZType.ZNamedType
                 { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] })
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

    private IrNode LowerClrNew(AstNode.ClrNew n)
    {
        // (new UserRecord args...) — route to the same RecordNew path as a bare ctor call.
        // CLR reflection cannot find user-defined types in the current compilation; we resolve them
        // here using the registered ctor so positional `(new ...)` works for records and structs.
        if (_recordCtors.TryGetValue(n.TypeName, out var fieldNames) && fieldNames.Count == n.Args.Count)
        {
            var fields = fieldNames.Zip(n.Args, (name, arg) => (name, Lower(arg))).ToList();
            return new IrNode.RecordNew(n.TypeName, fields) { Type = n.ResolvedType ?? ZType.Unit };
        }
        return new IrNode.ClrNew(n.TypeName, n.TypeArgs, n.Args.Select(Lower).ToList())
            { Type = n.ResolvedType ?? ZType.Unit };
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
                new IrField(f.Name, RemapTypeParams(f.TypeAnnotation, typeParamMap), LowerAttributes(f.Attributes),
                    IsInit: f.IsInit))
            .ToList();
        _recordCtors[n.RecordName] = n.Fields.Select(f => f.Name).ToList();
        foreach (var f in n.Fields)
            _classFieldAccessors.Add($"{n.RecordName}/{f.Name}");
        if (n.IsValueType)
            _valueTypeRecords.Add(n.RecordName);
        return new IrNode.RecordDecl(n.RecordName, csTypeParams, fields, LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams), n.IsValueType)
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
                    LowerAttributes(f.Attributes), IsInit: f.IsInit)).ToList())).ToList();

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
            ZType.ZNullableType nt => new ZType.ZNullableType(RemapTypeParams(nt.Inner, map)),
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
            Pattern.Tuple t =>
                new IrPattern.Tuple(t.Elements.Select(LowerPattern).ToList()),
            _ => new IrPattern.Wildcard()
        };
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

    /// <summary>
    ///     Extract generic type arguments from a collection type (e.g., List[^a] → [^a]).
    ///     Returns null when the type isn't a named type with enough type args.
    /// </summary>
    private static IReadOnlyList<ZType>? ExtractCollectionTypeArgs(ZType? type, int expectedArity)
    {
        if (type is ZType.ZNamedType { TypeArgs: var typeArgs } && typeArgs.Count >= expectedArity)
            return typeArgs.Take(expectedArity).ToList();
        return null;
    }

    /// <summary>
    ///     Extract generic type arguments from the resolved return type and arg types
    ///     of a CLR call by collecting leaf type variables and primitives.
    /// </summary>
    private static IReadOnlyList<ZType> ExtractGenericTypeArgsFromTypes(
        ZType returnType, IReadOnlyList<IrNode> args, int arity)
    {
        var typeArgs = new List<ZType>();
        CollectTypeArgs(returnType, typeArgs);
        foreach (var arg in args)
            if (arg.Type is not null)
                CollectTypeArgs(arg.Type, typeArgs);
        // Deduplicate by type variable ID while preserving order
        var seen = new HashSet<int>();
        var result = new List<ZType>();
        foreach (var t in typeArgs)
        {
            var id = t switch { ZType.ZTypeVar tv => tv.Id, ZType.ZConstrainedVar cv => cv.Id, _ => -1 };
            if (id >= 0)
            {
                if (seen.Add(id)) result.Add(t);
            }
            else if (!result.Contains(t))
            {
                result.Add(t);
            }
        }

        return result.Count >= arity ? result.Take(arity).ToList() : result;

        static void CollectTypeArgs(ZType type, List<ZType> args)
        {
            switch (type)
            {
                case ZType.ZTypeVar:
                case ZType.ZConstrainedVar:
                case ZType.ZPrimitiveType:
                    args.Add(type);
                    break;
                case ZType.ZNamedType nt:
                    foreach (var ta in nt.TypeArgs) CollectTypeArgs(ta, args);
                    break;
            }
        }
    }

    private IrNode LowerObjectExpr(AstNode.ObjectExpr n)
    {
        var methods = n.Methods.Select(m =>
        {
            var parms = m.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var body = Lower(m.Body);
            var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
            return new IrObjectMethod(m.Name, parms, retType, body, IsAsync: m.IsAsync);
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
            n.BaseClassName, irCtor)
        {
            Type = n.ResolvedType ?? defaultType
        };
    }

    private IrNode LowerClassDecl(AstNode.ClassDecl n)
    {
        var fields = n.Fields.Select(f =>
                new IrField(f.Name, f.TypeAnnotation, LowerAttributes(f.Attributes), f.IsMutable, f.IsInit))
            .ToList();

        var methods = n.Methods.Select(m =>
        {
            var parms = m.Params.Select(p =>
                new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit)).ToList();
            var body = Lower(m.Body);
            var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
            return new IrObjectMethod(m.Name, parms, retType, body, LowerAttributes(m.Attributes),
                m.IsAsync);
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
            fields, methods, n.IsOpen, n.BaseClassName,
            irCtor,
            LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams))
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

            // Look up out-param metadata from type inference
            _outParamsByAlias.TryGetValue(import.Alias, out var outParams);

            if (import.Kind != Static)
            {
                // Instance members: prefer slash-separated (Type/Member), fall back to dot (Type.Member)
                var slashIdx = import.QualifiedName.LastIndexOf('/');
                int splitIndex;
                if (slashIdx >= 0)
                {
                    splitIndex = slashIdx;
                }
                else
                {
                    var dotIndex = import.QualifiedName.LastIndexOf('.');
                    splitIndex = dotIndex;
                }

                if (splitIndex >= 0)
                {
                    var typeName = import.QualifiedName[..splitIndex];
                    var memberName = import.QualifiedName[(splitIndex + 1)..];
                    _clrImports[import.Alias] = (typeName, memberName, import.TypeParams.Count, import.Kind,
                        remappedConstraints, outParams);
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
                        remappedConstraints, outParams);
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

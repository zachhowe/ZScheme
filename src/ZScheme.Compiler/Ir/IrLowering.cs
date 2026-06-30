using System.Reflection;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

using static ClrImportKind;

public sealed class IrLowering
{
    private static readonly ILogger _log = Log.ForContext<IrLowering>();
    private static readonly HashSet<string> BinaryOps =
    [
        "+",
        "-",
        "*",
        "/",
        "%",
        "=",
        "!=",
        "<",
        ">",
        "<=",
        ">=",
        "and",
        "or",
    ];

    private static readonly HashSet<string> UnaryOps = ["not", "-"];
    private readonly HashSet<string> _classFieldAccessors = new();
    private readonly HashSet<string> _classMethodAccessors = new();

    private readonly Dictionary<
        string,
        (
            string TypeName,
            string MethodName,
            int GenericArity,
            ClrImportKind Kind,
            IReadOnlyDictionary<string, GenericConstraintKind>? Constraints,
            IReadOnlyList<ClrInterop.OutParamInfo>? OutParams,
            MethodInfo? ResolvedMethodInfo
        )
    > _clrImports = new();

    private readonly List<string> _clrNamespaces = new();
    private readonly DiagnosticBag _diagnostics;
    private readonly IReadOnlyList<string> _assemblySearchPaths;
    private readonly IReadOnlyDictionary<
        string,
        IReadOnlyList<ClrInterop.OutParamInfo>
    > _outParamsByAlias;
    private readonly Dictionary<string, List<string>> _recordCtors = new();
    private readonly TypeAliasRegistry _typeAliases;
    private readonly Dictionary<string, string> _unionCtors = new();
    private readonly HashSet<string> _valueTypeRecords = new();

    public IrLowering(
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>>? outParamsByAlias =
            null,
        TypeAliasRegistry? typeAliases = null,
        IReadOnlyList<string>? assemblySearchPaths = null
    )
    {
        _diagnostics = diagnostics;
        _assemblySearchPaths = assemblySearchPaths ?? [];
        _outParamsByAlias =
            outParamsByAlias ?? new Dictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>>();
        _typeAliases = typeAliases ?? new TypeAliasRegistry();
    }

    public IReadOnlyDictionary<
        string,
        (
            string TypeName,
            string MethodName,
            int GenericArity,
            ClrImportKind Kind,
            IReadOnlyDictionary<string, GenericConstraintKind>? Constraints,
            IReadOnlyList<ClrInterop.OutParamInfo>? OutParams,
            MethodInfo? ResolvedMethodInfo
        )
    > ClrImports => _clrImports;

    public IReadOnlyDictionary<string, string> UnionCtors => _unionCtors;
    public IReadOnlyDictionary<string, List<string>> RecordCtors => _recordCtors;
    public IReadOnlyList<string> ClrNamespaces => _clrNamespaces;

    private ZType MakeVariadicType(ZType elemType)
    {
        if (_typeAliases.TryGetFirstArrayAliasName(out var arrayName))
            return new ZType.ZNamedType(arrayName!, [elemType]);
        return new ZType.ZNamedType("Clr-Array", [elemType]);
    }

    private static ZType ExtractDelegateReturnType(string? clrDelegateTypeName)
    {
        if (clrDelegateTypeName is null)
            return ZType.Unit;

        var genericOpen = clrDelegateTypeName.IndexOf('<');
        if (genericOpen < 0)
            return ZType.Unit; // System.Action has no type args

        var genericClose = clrDelegateTypeName.LastIndexOf('>');
        if (genericClose <= genericOpen)
            return ZType.Unit;

        var inner = clrDelegateTypeName.Substring(genericOpen + 1, genericClose - genericOpen - 1);
        var args = SplitDelegateTypeArguments(inner);

        if (clrDelegateTypeName.StartsWith("System.Func"))
        {
            // Last type argument is the return type
            if (args.Count == 0)
                return ZType.Unit;
            return ParseTypeToZType(args[^1]);
        }

        // System.Action or unknown delegate — return Unit
        return ZType.Unit;
    }

    private static List<string> SplitDelegateTypeArguments(string inner)
    {
        var args = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(inner.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        args.Add(inner.Substring(start).Trim());
        return args;
    }

    private static ZType ParseTypeToZType(string typeName)
    {
        return typeName switch
        {
            "int" or "Int32" => ZType.Int,
            "long" or "Int64" => ZType.Int,
            "float" or "Double" => ZType.Float,
            "bool" or "Boolean" => ZType.Bool,
            "string" or "String" => ZType.String,
            "unit" or "Unit" => ZType.Unit,
            _ => ZType.Unit, // fallback for unknown types
        };
    }

    /// <summary>
    ///     Marks a named-function argument (an <see cref="IrNode.Var" />) that is passed to a
    ///     delegate-typed CLR parameter with the target delegate type, so the C# emitter can
    ///     wrap it in an adapter lambda cast. The target type is taken from the import's
    ///     declared signature (a <see cref="ZType.ZDelegateType" />) or, failing that, from the
    ///     resolved overload's concrete (non-base) delegate parameter.
    /// </summary>
    private static IrNode CoerceNamedFnToDelegate(IrNode arg, ZType? annotatedParam, Type? clrParam)
    {
        if (arg is not IrNode.Var v || v.ClrDelegateTypeName is not null)
            return arg;

        string? delegateName = null;
        if (annotatedParam is ZType.ZDelegateType dt)
            delegateName = dt.ClrTypeName;
        else if (
            clrParam is not null
            && typeof(Delegate).IsAssignableFrom(clrParam)
            && clrParam != typeof(Delegate)
            && clrParam != typeof(MulticastDelegate)
        )
            delegateName = clrParam.FullName;

        return delegateName is null ? arg : v with { ClrDelegateTypeName = delegateName };
    }

    public void RegisterClrImport(
        string alias,
        string typeName,
        string methodName,
        int genericArity = 0,
        ClrImportKind kind = Static,
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints = null,
        IReadOnlyList<ClrInterop.OutParamInfo>? outParams = null,
        MethodInfo? resolvedMethodInfo = null
    )
    {
        _clrImports[alias] = (
            typeName,
            methodName,
            genericArity,
            kind,
            constraints,
            outParams,
            resolvedMethodInfo
        );
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
            AstNode.IntLit n => new IrNode.IntConst(n.Value) { Type = ZType.Int, Span = n.Span },
            AstNode.FloatLit n => new IrNode.FloatConst(n.Value)
            {
                Type = ZType.Float,
                Span = n.Span,
            },
            AstNode.BoolLit n => new IrNode.BoolConst(n.Value) { Type = ZType.Bool, Span = n.Span },
            AstNode.StringLit n => new IrNode.StringConst(n.Value)
            {
                Type = ZType.String,
                Span = n.Span,
            },
            AstNode.UnitLit u => new IrNode.UnitConst { Type = ZType.Unit, Span = u.Span },
            AstNode.NullLit n => new IrNode.NullConst
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            },
            AstNode.Name n when _unionCtors.ContainsKey(n.Value) => new IrNode.UnionCaseNew(
                _unionCtors[n.Value],
                n.Value,
                []
            )
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            },
            AstNode.Name n => new IrNode.Var(n.Value)
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
                ModuleName = ExtractOverloadModule(n.ResolvedQualifiedName, n.Value),
            },
            AstNode.Let n => LowerLet(n),
            AstNode.Use n => LowerUse(n),
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
            AstNode.SuperMethodCall n => new IrNode.SuperMethodCall(
                n.MethodName,
                n.Args.Select(Lower).ToList()
            )
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            },
            AstNode.SetField n => new IrNode.SetField(n.FieldName, Lower(n.Value))
            {
                Type = ZType.Unit,
                Span = n.Span,
            },
            AstNode.ClrNew n => LowerClrNew(n),
            AstNode.TypeOf n => new IrNode.TypeOf(n.TypeArg)
            {
                Type = new ZType.ZNamedType("System.Type", []),
                Span = n.Span,
            },
            AstNode.Raise n => new IrNode.Throw(Lower(n.Expr))
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            },
            AstNode.DefineAsync n => LowerDefineAsync(n),
            AstNode.Await n => new IrNode.Await(Lower(n.Expr))
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            },
            AstNode.WithHandlers n => LowerWithHandlers(n),
            AstNode.With n => LowerWith(n),
            AstNode.ImportClr n => LowerImportClr(n),
            AstNode.TypeAliasDecl n => new IrNode.TypeAliasDecl(
                n.AliasName,
                n.TypeParams,
                n.ClrTarget,
                n.AssemblyHint,
                n.IsArray
            )
            {
                Type = ZType.Unit,
                Span = n.Span,
            },
            AstNode.NamespaceDecl nd => new IrNode.UnitConst { Type = ZType.Unit, Span = nd.Span },
            AstNode.ModuleDecl m => m.Body.Count > 0
                ? new IrNode.Seq(m.Body.Select(Lower).ToList()) { Type = ZType.Unit, Span = m.Span }
                : new IrNode.UnitConst { Type = ZType.Unit, Span = m.Span },
            AstNode.Import imp => new IrNode.UnitConst { Type = ZType.Unit, Span = imp.Span },
            AstNode.Export exp => new IrNode.UnitConst { Type = ZType.Unit, Span = exp.Span },
            _ => new IrNode.UnitConst { Type = ZType.Unit, Span = node.Span },
        };
    }

    /// <summary>
    ///     Extracts the module-name prefix from a qualified overload-resolved name
    ///     (e.g. "stdlib/list/cons" with bareName "cons" → "stdlib/list"). Returns
    ///     null when no qualified name was set.
    /// </summary>
    private static string? ExtractOverloadModule(string? qualifiedName, string bareName)
    {
        if (qualifiedName is null)
            return null;
        var suffix = "/" + bareName;
        if (qualifiedName.EndsWith(suffix, StringComparison.Ordinal))
            return qualifiedName[..^suffix.Length];
        return null;
    }

    private IrNode LowerWith(AstNode.With n)
    {
        var record = Lower(n.Record);
        var recordType = n.Record.ResolvedType ?? n.ResolvedType ?? record.Type;
        var typeName = recordType is ZType.ZNamedType named ? named.Name : "";
        var updates = n.Updates.Select(u => (u.FieldName, Lower(u.Value))).ToList();
        return new IrNode.RecordWith(typeName, record, updates)
        {
            Type = n.ResolvedType ?? recordType,
            Span = n.Span,
        };
    }

    private IrNode LowerWithHandlers(AstNode.WithHandlers n)
    {
        var body = Lower(n.Body);
        var handlers = n
            .Handlers.Select(h => new IrHandlerClause(
                h.ExceptionTypeName,
                h.BindingVarName,
                Lower(h.HandlerBody)
            ))
            .ToList();

        return new IrNode.WithHandlers(body, handlers)
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
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

        IrNode result = new IrNode.Seq(nodes)
        {
            Type = p.ResolvedType ?? ZType.Unit,
            Span = p.Span,
        };

        // Lower (object ...) anonymous-class expressions into synthesized top-level classes plus
        // construction expressions, so no IrNode.ObjectExpr reaches the post-lowering passes or
        // the emitters. Runs before the beta-reducer so the reducer (and everything downstream)
        // only ever sees the synthesized IrNode.ClassDecl nodes.
        result = new ObjectLifter().Lift(result);

        // Beta-reduce immediately-invoked lambdas (((lambda (x) ...) a)) into let spines so that
        // both backends emit plain locals/statements instead of allocating and invoking a delegate
        // on the spot. Lambdas used as first-class values are left untouched.
        result = new IiffeBetaReducer().Reduce(result);

        // Check for define-async inside let bodies (FuncDef with IsAsync=true inside Let)
        // This pattern is not supported because define-async creates a method definition,
        // not a local binding. References to the function name won't resolve.
        CheckAsyncInLetBodies(result);

        return result;
    }

    private void CheckAsyncInLetBodies(IrNode node)
    {
        switch (node)
        {
            case IrNode.Let let:
                if (let.Value is IrNode.FuncDef funcDef && funcDef.IsAsync)
                {
                    _diagnostics.Error(
                        "'define-async' is not supported inside 'let' bodies. "
                            + "Top-level 'define-async' (at module or class level) is supported. "
                            + "Restructure your code to define async functions at the top level.",
                        let.Span
                    );
                }
                CheckAsyncInLetBodies(let.Value);
                CheckAsyncInLetBodies(let.Body);
                break;

            case IrNode.Use use:
                if (use.Value is IrNode.FuncDef useFunc && useFunc.IsAsync)
                {
                    _diagnostics.Error(
                        "'define-async' is not supported inside 'use' bodies. "
                            + "Top-level 'define-async' (at module or class level) is supported. "
                            + "Restructure your code to define async functions at the top level.",
                        use.Span
                    );
                }
                CheckAsyncInLetBodies(use.Value);
                CheckAsyncInLetBodies(use.Body);
                break;

            case IrNode.Seq seq:
                foreach (var child in seq.Nodes)
                    CheckAsyncInLetBodies(child);
                break;

            case IrNode.ClassDecl classDecl:
                foreach (var method in classDecl.Methods)
                    CheckAsyncInLetBodies(method.Body);
                if (classDecl.Constructor is { } ctor)
                    foreach (var expr in ctor.BodyExprs)
                        CheckAsyncInLetBodies(expr);
                break;
        }
    }

    private IrNode LowerLet(AstNode.Let n)
    {
        return new IrNode.Let(n.VarName, Lower(n.Value), Lower(n.Body), n.TypeAnnotation)
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerUse(AstNode.Use n)
    {
        return new IrNode.Use(n.VarName, Lower(n.Value), Lower(n.Body), n.TypeAnnotation)
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerIf(AstNode.If n)
    {
        return new IrNode.If(Lower(n.Condition), Lower(n.Then), Lower(n.Else))
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerTupleNew(AstNode.TupleNew n)
    {
        var elements = n.Elements.Select(Lower).ToList();
        return new IrNode.TupleNew(elements) { Type = n.ResolvedType ?? ZType.Unit, Span = n.Span };
    }

    private IrNode LowerApply(AstNode.Apply n)
    {
        // Check for value/N tuple accessor
        if (
            n.Function is AstNode.Name tname
            && tname.Value.StartsWith("value/")
            && int.TryParse(tname.Value["value/".Length..], out var tupleIdx)
            && n.Args.Count == 1
        )
            return new IrNode.FieldGet(Lower(n.Args[0]), $"Item{tupleIdx + 1}")
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };

        // 1-arg `(/ x)` → invert: emit (1 / x) using x's resolved type so we get
        // the right literal kind (Int or Float). The companion 1-arg `(- x)` case
        // is handled by the unary-op shortcut below (with `-` in UnaryOps).
        if (n.Function is AstNode.Name { Value: "/" } && n.Args.Count == 1)
        {
            var argType = n.Args[0].ResolvedType ?? n.ResolvedType ?? ZType.Int;
            var lowered = Lower(n.Args[0]);
            IrNode oneLit = argType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Float }
                ? new IrNode.FloatConst(1.0f) { Type = ZType.Float, Span = n.Span }
                : new IrNode.IntConst(1) { Type = ZType.Int, Span = n.Span };
            return new IrNode.BinOp("/", oneLit, lowered)
            {
                Type = n.ResolvedType ?? argType,
                Span = n.Span,
            };
        }

        // Check for binary operator optimization
        if (n.Function is AstNode.Name name && n.Args.Count == 2 && BinaryOps.Contains(name.Value))
            return new IrNode.BinOp(name.Value, Lower(n.Args[0]), Lower(n.Args[1]))
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };

        // Check for unary operator
        if (n.Function is AstNode.Name uname && n.Args.Count == 1 && UnaryOps.Contains(uname.Value))
            return new IrNode.UnaryOp(uname.Value, Lower(n.Args[0]))
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };

        // Check for builtin functions (string-append, int->string, etc.)
        if (n.Function is AstNode.Name builtinName)
            switch (builtinName.Value)
            {
                case "string-append" when n.Args.Count == 2:
                    return new IrNode.BinOp("+", Lower(n.Args[0]), Lower(n.Args[1]))
                    {
                        Type = n.ResolvedType ?? ZType.String,
                        Span = n.Span,
                    };
                case "int->string" when n.Args.Count == 1:
                    return new IrNode.MethodCall(Lower(n.Args[0]), "ToString", [], false, false)
                    {
                        Type = n.ResolvedType ?? ZType.String,
                        Span = n.Span,
                    };
                case "string->int" when n.Args.Count == 1:
                    return BuiltinClrCall(
                        "System.Int32",
                        "Parse",
                        Lower(n.Args[0]),
                        n.ResolvedType ?? ZType.Int,
                        n.Span
                    );
                case "float->int" when n.Args.Count == 1:
                    return BuiltinClrCall(
                        "System.Convert",
                        "ToInt32",
                        Lower(n.Args[0]),
                        n.ResolvedType ?? ZType.Int,
                        n.Span
                    );
                case "int->float" when n.Args.Count == 1:
                case "double->float" when n.Args.Count == 1:
                    return BuiltinClrCall(
                        "System.Convert",
                        "ToSingle",
                        Lower(n.Args[0]),
                        n.ResolvedType ?? ZType.Float,
                        n.Span
                    );
                case "float->double" when n.Args.Count == 1:
                    return BuiltinClrCall(
                        "System.Convert",
                        "ToDouble",
                        Lower(n.Args[0]),
                        n.ResolvedType ?? ZType.Double,
                        n.Span
                    );
                // The 6 collection conversion functions (vector->immutable-vector, vector->mutable-vector,
                // mutable-list->list, list->mutable-list, mutable-hash->hash, hash-copy) live
                // in stdlib (see packages/stdlib/src/{vector,list,hash,mutable/{vector,list,hash}}.zs).
                // They are ordinary stdlib functions and lower through the normal call path.
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
                    Type = n.ResolvedType ?? ZType.Unit,
                    Span = n.Span,
                };
            }

            if (_classMethodAccessors.Contains(slashName.Value))
            {
                var slashIdx = slashName.Value.IndexOf('/');
                var methodName = slashName.Value[(slashIdx + 1)..];
                var restArgs = n.Args.Skip(1).Select(Lower).ToList();
                return new IrNode.MethodCall(Lower(n.Args[0]), methodName, restArgs, false, false)
                {
                    Type = n.ResolvedType ?? ZType.Unit,
                    Span = n.Span,
                };
            }
        }

        // Check for CLR import call
        if (
            n.Function is AstNode.Name clrName
            && _clrImports.TryGetValue(clrName.Value, out var clrInfo)
        )
        {
            if (clrInfo.Kind != Static && n.Args.Count >= 1)
            {
                // Instance member: first arg is receiver, rest are method args
                var receiver = Lower(n.Args[0]);
                var methodArgs = n.Args.Skip(1).Select(Lower).ToList();
                // Coerce named-function args to delegate params (signature param 0 is the
                // receiver, so method arg j aligns with declared param j+1).
                var instSig = (clrName.ResolvedType as ZType.ZFuncType)?.Params;
                methodArgs = methodArgs
                    .Select(
                        (a, j) =>
                            CoerceNamedFnToDelegate(
                                a,
                                instSig is not null && j + 1 < instSig.Count
                                    ? instSig[j + 1]
                                    : null,
                                null
                            )
                    )
                    .ToList();
                // For a plain CLR instance method (not a property/indexer/out-param call) on a
                // non-generic loaded receiver, resolve the overload here so codegen emits the
                // chosen method rather than re-running reflection-based selection. Generic
                // receivers are left to the backend (closing the method on the receiver's type
                // arguments stays backend-specific).
                MethodInfo? instResolved = null;
                if (clrInfo.Kind == Instance && clrInfo.OutParams is not { Count: > 0 })
                {
                    var instInterop = new ClrInterop(
                        _diagnostics,
                        _assemblySearchPaths,
                        _typeAliases
                    );
                    var receiverClr = instInterop.ResolveZLeafToClr(
                        n.Args[0].ResolvedType ?? ZType.Unit
                    );
                    if (receiverClr is { IsGenericType: false })
                        instResolved = instInterop.ResolveInstanceOverloadCallSite(
                            receiverClr,
                            clrInfo.MethodName,
                            new ZType.ZFuncType(
                                methodArgs.Select(a => a.Type ?? ZType.Unit).ToList(),
                                n.ResolvedType ?? ZType.Unit
                            ),
                            n.Span
                        );
                }

                return new IrNode.MethodCall(
                    receiver,
                    clrInfo.MethodName,
                    methodArgs,
                    clrInfo.Kind == InstanceProperty,
                    clrInfo.Kind == InstanceIndexer,
                    clrInfo.Kind is InstancePropertySet or InstancePropertyInit,
                    clrInfo.Kind == InstanceIndexerSet,
                    clrInfo.Kind == InstancePropertyInit,
                    clrInfo.OutParams,
                    instResolved
                )
                {
                    Type = n.ResolvedType ?? ZType.Unit,
                    Span = n.Span,
                };
            }

            // Resolve overload at call site when not pre-resolved by explicit annotation.
            // Uses the resolved function type from type inference to pick the best match
            // among candidates with the same name (signature-directed resolution).
            var resolvedMethodInfo = clrInfo.ResolvedMethodInfo;
            var outParams = clrInfo.OutParams;
            // Resolve the overload at the call site so the backend emits the chosen method
            // rather than re-running its own reflection-based selection. For delegate-bearing
            // calls the full function type (args -> ret) lives on the call target (the import
            // alias) and carries the delegate parameter shape; otherwise synthesize the call
            // signature from the argument types and the call's return type.
            var hasFuncArg = n.Args.Any(a =>
                a.ResolvedType is ZType.ZFuncType or ZType.ZDelegateType
            );
            if (resolvedMethodInfo is null)
            {
                var callSiteInterop = new ClrInterop(
                    _diagnostics,
                    _assemblySearchPaths,
                    _typeAliases
                );
                var resolvedFuncType =
                    hasFuncArg && clrName.ResolvedType is ZType.ZFuncType sigFt
                        ? sigFt
                        : new ZType.ZFuncType(
                            n.Args.Select(a => a.ResolvedType ?? ZType.Unit).ToList(),
                            n.ResolvedType ?? ZType.Unit
                        );
                resolvedMethodInfo = callSiteInterop.ResolveOverloadCallSite(
                    clrInfo.TypeName,
                    clrInfo.MethodName,
                    resolvedFuncType,
                    n.Span
                );
                if (resolvedMethodInfo is not null)
                    outParams = callSiteInterop
                        .MethodInfoToZFuncTypeWithOutParams(resolvedMethodInfo)
                        .OutParams;
            }

            // Resolve generic method at call site using actual argument types,
            // then extract concrete type args from the resolved function type.
            var loweredArgs = n.Args.Select(Lower).ToList();

            // Coerce named-function arguments passed to delegate-typed parameters so the
            // C# emitter wraps them in an adapter lambda cast to the target delegate type
            // (e.g. a handler passed where RequestDelegate is expected). The target type
            // comes from the import's declared signature, or the resolved overload.
            var clrSigParams = (clrName.ResolvedType as ZType.ZFuncType)?.Params;
            var resolvedParams = resolvedMethodInfo?.GetParameters();
            loweredArgs = loweredArgs
                .Select(
                    (a, i) =>
                        CoerceNamedFnToDelegate(
                            a,
                            clrSigParams is not null && i < clrSigParams.Count
                                ? clrSigParams[i]
                                : null,
                            resolvedParams is not null && i < resolvedParams.Length
                                ? resolvedParams[i].ParameterType
                                : null
                        )
                )
                .ToList();
            IReadOnlyList<ZType>? genericTypeArgs = null;
            if (clrInfo.GenericArity > 0)
                genericTypeArgs = ResolveGenericCallSite(
                    clrInfo.TypeName,
                    clrInfo.MethodName,
                    clrInfo.GenericArity,
                    loweredArgs,
                    n.ResolvedType ?? ZType.Unit
                );

            return new IrNode.ClrCall(
                clrInfo.TypeName,
                clrInfo.MethodName,
                loweredArgs,
                clrInfo.GenericArity,
                genericTypeArgs,
                outParams,
                resolvedMethodInfo
            )
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };
        }

        // Check for union constructor call
        if (
            n.Function is AstNode.Name uName
            && _unionCtors.TryGetValue(uName.Value, out var unionName)
        )
            return new IrNode.UnionCaseNew(unionName, uName.Value, n.Args.Select(Lower).ToList())
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };

        // Check for record constructor call
        if (
            n.Function is AstNode.Name rName
            && _recordCtors.TryGetValue(rName.Value, out var fieldNames)
        )
        {
            var fields = fieldNames.Zip(n.Args, (name, arg) => (name, Lower(arg))).ToList();
            return new IrNode.RecordNew(rName.Value, fields)
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };
        }

        // Check for variadic function call — pack extra args into an array
        if (
            n.Function.ResolvedType is ZType.ZFuncType { IsVariadic: true } varFt
            || (
                n.Function.ResolvedType
                    is ZType.ZForAllType { Body: ZType.ZFuncType { IsVariadic: true } innerFt2 }
                && (varFt = innerFt2) != null
            )
        )
        {
            var fixedCount = varFt.Params.Count - 1;
            var elemType = varFt.Params[^1];
            var fixedArgs = n.Args.Take(fixedCount).Select(Lower).ToList();
            var variadicArgs = n.Args.Skip(fixedCount).Select(Lower).ToList();
            // When the variadic call has zero variadic args, no unification has
            // pinned the element's type variable from the call args. Try
            // matching the function's declared return type against the call's
            // resolved return type to recover a concrete element type (e.g.
            // `(list)` used where a `(List Int)` is expected should produce
            // `int[]` rather than `T[]` where T is unresolvable).
            if (
                variadicArgs.Count == 0
                && ContainsFreeTypeVar(elemType)
                && n.ResolvedType is not null
            )
                elemType = ResolveTypeVarsFromShape(elemType, varFt.Return, n.ResolvedType);
            var arrayArg = new IrNode.MutableArrayNew(elemType, variadicArgs)
            {
                Type = MakeVariadicType(elemType),
                Span = n.Span,
            };
            fixedArgs.Add(arrayArg);
            return new IrNode.Call(Lower(n.Function), fixedArgs)
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };
        }

        return new IrNode.Call(Lower(n.Function), n.Args.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerLambda(AstNode.Lambda n)
    {
        var parms = n.ResolvedType is ZType.ZFuncType ft2
            ? n
                .Params.Select(
                    (p, i) =>
                    {
                        var inferredType =
                            i < ft2.Params.Count ? ft2.Params[i] : p.TypeAnnotation ?? ZType.Unit;
                        if (p.IsVariadic)
                            inferredType = MakeVariadicType(inferredType);
                        return new IrParam(p.Name, inferredType, IsVariadic: p.IsVariadic);
                    }
                )
                .ToList()
            : n
                .Params.Select(p =>
                {
                    var t = p.TypeAnnotation ?? ZType.Unit;
                    if (p.IsVariadic)
                        t = MakeVariadicType(t);
                    return new IrParam(p.Name, t, IsVariadic: p.IsVariadic);
                })
                .ToList();
        var body = Lower(n.Body);

        string? clrDelegateTypeName = null;
        ZType retType;
        if (n.ResolvedType is ZType.ZDelegateType delegateType)
        {
            clrDelegateTypeName = delegateType.ClrTypeName;
            retType = ExtractDelegateReturnType(delegateType.ClrTypeName);
        }
        else if (n.ResolvedType is ZType.ZFuncType ft)
        {
            retType = ft.Return;
        }
        else
        {
            retType = ZType.Unit;
        }

        // For now, emit as a FuncDef with a generated name (closure conversion later)
        var name = $"__lambda_{n.Span.Line}_{n.Span.Column}";

        return new IrNode.FuncDef(
            name,
            parms,
            retType,
            body,
            false,
            ClrDelegateTypeName: clrDelegateTypeName
        )
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerDefine(AstNode.Define n)
    {
        var funcType = n.ResolvedType as ZType.ZFuncType;
        var parms = n
            .Params.Select(
                (p, i) =>
                {
                    var inferredType =
                        funcType is not null && i < funcType.Params.Count
                            ? funcType.Params[i]
                            : p.TypeAnnotation ?? ZType.Unit;
                    // Variadic param becomes Clr-Array[T]
                    if (p.IsVariadic)
                        inferredType = MakeVariadicType(inferredType);
                    return new IrParam(
                        p.Name,
                        inferredType,
                        LowerAttributes(p.Attributes),
                        p.IsVariadic
                    );
                }
            )
            .ToList();
        var body = Lower(n.Body);

        var retType = funcType?.Return ?? n.ReturnTypeAnnotation ?? ZType.Unit;
        var isSelfRecursive = BodyReferences(n.Body, n.FnName);

        var typeParams = ExtractFuncTypeParams(n.ResolvedType);
        var irConstraints = RemapDefineConstraints(
            n.TypeParamConstraints,
            n.Params,
            n.ReturnTypeAnnotation,
            funcType
        );
        return new IrNode.FuncDef(
            n.FnName,
            parms,
            retType,
            body,
            isSelfRecursive,
            typeParams.Count > 0 ? typeParams : null,
            LowerAttributes(n.Attributes),
            TypeParamConstraints: irConstraints
        )
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerDefineAsync(AstNode.DefineAsync n)
    {
        var asyncFuncType = n.ResolvedType as ZType.ZFuncType;
        var parms = n
            .Params.Select(
                (p, i) =>
                {
                    var inferredType =
                        asyncFuncType is not null && i < asyncFuncType.Params.Count
                            ? asyncFuncType.Params[i]
                            : p.TypeAnnotation ?? ZType.Unit;
                    if (p.IsVariadic)
                        inferredType = MakeVariadicType(inferredType);
                    return new IrParam(
                        p.Name,
                        inferredType,
                        LowerAttributes(p.Attributes),
                        p.IsVariadic
                    );
                }
            )
            .ToList();
        var body = Lower(n.Body);

        // Unwrap Task<T> to get the inner return type for the IR
        ZType retType;
        if (
            n.ReturnTypeAnnotation is ZType.ZNamedType { TypeArgs: [var innerT] } taskNt
            && _typeAliases.IsTaskName(taskNt.Name)
        )
            retType = innerT;
        else if (
            n.ReturnTypeAnnotation is ZType.ZNamedType { TypeArgs: [] } nonGenericTask
            && _typeAliases.IsTaskName(nonGenericTask.Name)
        )
            retType = ZType.Unit;
        else
            retType =
                n.ReturnTypeAnnotation
                ?? (n.ResolvedType is ZType.ZFuncType ft ? ft.Return : ZType.Unit);

        var isSelfRecursive = BodyReferences(n.Body, n.FnName);

        var typeParams = ExtractFuncTypeParams(n.ResolvedType);
        var irConstraints = RemapDefineConstraints(
            n.TypeParamConstraints,
            n.Params,
            n.ReturnTypeAnnotation,
            asyncFuncType
        );
        return new IrNode.FuncDef(
            n.FnName,
            parms,
            retType,
            body,
            isSelfRecursive,
            typeParams.Count > 0 ? typeParams : null,
            LowerAttributes(n.Attributes),
            true,
            irConstraints
        )
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerDefineValue(AstNode.DefineValue n)
    {
        return new IrNode.Let(
            n.VarName,
            Lower(n.Value),
            new IrNode.UnitConst { Type = ZType.Unit, Span = n.Span }
        )
        {
            Type = ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerClrNew(AstNode.ClrNew n)
    {
        // (new UserRecord args...) — route to the same RecordNew path as a bare ctor call.
        // CLR reflection cannot find user-defined types in the current compilation; we resolve them
        // here using the registered ctor so positional `(new ...)` works for records and structs.
        if (
            _recordCtors.TryGetValue(n.TypeName, out var fieldNames)
            && fieldNames.Count == n.Args.Count
        )
        {
            var fields = fieldNames.Zip(n.Args, (name, arg) => (name, Lower(arg))).ToList();
            return new IrNode.RecordNew(n.TypeName, fields)
            {
                Type = n.ResolvedType ?? ZType.Unit,
                Span = n.Span,
            };
        }

        return new IrNode.ClrNew(n.TypeName, n.TypeArgs, n.Args.Select(Lower).ToList())
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
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

        var fields = n
            .Fields.Select(f => new IrField(
                f.Name,
                RemapTypeParams(f.TypeAnnotation, typeParamMap),
                LowerAttributes(f.Attributes),
                IsInit: f.IsInit
            ))
            .ToList();
        _recordCtors[n.RecordName] = n.Fields.Select(f => f.Name).ToList();
        foreach (var f in n.Fields)
            _classFieldAccessors.Add($"{n.RecordName}/{f.Name}");
        if (n.IsValueType)
            _valueTypeRecords.Add(n.RecordName);
        return new IrNode.RecordDecl(
            n.RecordName,
            csTypeParams,
            fields,
            LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams),
            n.IsValueType
        )
        {
            Type = ZType.Unit,
            Span = n.Span,
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

        var cases = n
            .Cases.Select(c => new IrUnionCase(
                c.Name,
                c.Fields.Select(f => new IrField(
                        f.Name,
                        RemapTypeParams(f.TypeAnnotation, typeParamMap),
                        LowerAttributes(f.Attributes),
                        IsInit: f.IsInit
                    ))
                    .ToList()
            ))
            .ToList();

        // Register union case names for constructor lowering
        foreach (var c in n.Cases)
            _unionCtors[c.Name] = n.UnionName;

        return new IrNode.UnionDecl(
            n.UnionName,
            csTypeParams,
            cases,
            LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams)
        )
        {
            Type = ZType.Unit,
            Span = n.Span,
        };
    }

    private static ZType RemapTypeParams(ZType type, Dictionary<string, string> map)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt
                when map.TryGetValue(nt.Name, out var mapped) => new ZType.ZNamedType(mapped, []),
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(t => RemapTypeParams(t, map)).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(p => RemapTypeParams(p, map)).ToList(),
                RemapTypeParams(ft.Return, map)
            ),
            ZType.ZNullableType nt => new ZType.ZNullableType(RemapTypeParams(nt.Inner, map)),
            _ => type,
        };
    }

    private IrNode LowerMatch(AstNode.Match n)
    {
        var scrutinee = Lower(n.Scrutinee);
        var arms = n
            .Arms.Select(a => new IrMatchArm(LowerPattern(a.Pattern), Lower(a.Body)))
            .ToList();
        return new IrNode.Match(scrutinee, arms)
        {
            Type = n.ResolvedType ?? ZType.Unit,
            Span = n.Span,
        };
    }

    private IrPattern LowerPattern(Pattern p)
    {
        return p switch
        {
            Pattern.Wildcard => new IrPattern.Wildcard(),
            Pattern.Variable v => new IrPattern.Variable(v.Name),
            Pattern.Literal l => new IrPattern.Literal(l.Value),
            Pattern.Constructor c => new IrPattern.Constructor(
                c.Name,
                c.Fields.Select(LowerPattern).ToList()
            ),
            Pattern.Tuple t => new IrPattern.Tuple(t.Elements.Select(LowerPattern).ToList()),
            _ => new IrPattern.Wildcard(),
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
                callArgs.Add(new IrNode.Var(pName) { Type = resultFt.Params[i], Span = n.Span });
            }

            var call = new IrNode.Call(func, callArgs) { Type = resultFt.Return, Span = n.Span };
            var lambdaName = $"__partial_{n.Span.Line}_{n.Span.Column}";
            return new IrNode.FuncDef(lambdaName, remainingParams, resultFt.Return, call, false)
            {
                Type = n.ResolvedType,
                Span = n.Span,
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

    private static bool ContainsFreeTypeVar(ZType type)
    {
        return type switch
        {
            ZType.ZTypeVar => true,
            ZType.ZConstrainedVar => true,
            ZType.ZNamedType nt => nt.TypeArgs.Any(ContainsFreeTypeVar),
            ZType.ZFuncType ft => ft.Params.Any(ContainsFreeTypeVar)
                || ContainsFreeTypeVar(ft.Return),
            ZType.ZNullableType nn => ContainsFreeTypeVar(nn.Inner),
            _ => false,
        };
    }

    /// <summary>
    ///     Walk two types in parallel and record substitutions for any type
    ///     variables found in <paramref name="pattern" /> matched against the
    ///     concrete shape of <paramref name="actual" />. Used to recover the
    ///     element type of an empty variadic call (where no argument exists to
    ///     unify against the variadic parameter) by comparing the function's
    ///     declared return type with the resolved return type at the call site.
    /// </summary>
    private static ZType ResolveTypeVarsFromShape(ZType target, ZType pattern, ZType actual)
    {
        var subst = new Dictionary<int, ZType>();
        CollectShapeSubst(pattern, actual, subst);
        return subst.Count == 0 ? target : SubstituteVars(target, subst);
    }

    private static void CollectShapeSubst(ZType pattern, ZType actual, Dictionary<int, ZType> subst)
    {
        switch (pattern)
        {
            case ZType.ZTypeVar tv:
                subst.TryAdd(tv.Id, actual);
                break;
            case ZType.ZConstrainedVar cv:
                subst.TryAdd(cv.Id, actual);
                break;
            case ZType.ZNamedType pn
                when actual is ZType.ZNamedType an
                    && pn.Name == an.Name
                    && pn.TypeArgs.Count == an.TypeArgs.Count:
                for (var i = 0; i < pn.TypeArgs.Count; i++)
                    CollectShapeSubst(pn.TypeArgs[i], an.TypeArgs[i], subst);
                break;
            case ZType.ZFuncType pf
                when actual is ZType.ZFuncType af && pf.Params.Count == af.Params.Count:
                for (var i = 0; i < pf.Params.Count; i++)
                    CollectShapeSubst(pf.Params[i], af.Params[i], subst);
                CollectShapeSubst(pf.Return, af.Return, subst);
                break;
            case ZType.ZNullableType pnn when actual is ZType.ZNullableType ann:
                CollectShapeSubst(pnn.Inner, ann.Inner, subst);
                break;
        }
    }

    private static ZType SubstituteVars(ZType type, IReadOnlyDictionary<int, ZType> subst)
    {
        return type switch
        {
            ZType.ZTypeVar tv => subst.TryGetValue(tv.Id, out var r) ? r : type,
            ZType.ZConstrainedVar cv => subst.TryGetValue(cv.Id, out var r) ? r : type,
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(a => SubstituteVars(a, subst)).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(p => SubstituteVars(p, subst)).ToList(),
                SubstituteVars(ft.Return, subst),
                ft.IsVariadic
            ),
            ZType.ZNullableType nn => new ZType.ZNullableType(SubstituteVars(nn.Inner, subst)),
            _ => type,
        };
    }

    /// <summary>
    ///     Resolves a generic CLR method call at the call site using actual argument types.
    ///     Finds the matching generic method on the CLR type, then extracts concrete type
    ///     arguments from the resolved function type (which has type vars substituted by
    ///     the type inference unification).
    /// </summary>
    /// <summary>
    ///     Builds a single-argument CLR call for a built-in numeric/string conversion,
    ///     resolving the overload up front so codegen emits the chosen method directly
    ///     (e.g. Convert.ToInt32 has ~19 overloads — the arg type picks the right one).
    /// </summary>
    private IrNode.ClrCall BuiltinClrCall(
        string typeName,
        string methodName,
        IrNode arg,
        ZType returnType,
        SourceSpan span
    )
    {
        var interop = new ClrInterop(_diagnostics, _assemblySearchPaths, _typeAliases);
        var funcType = new ZType.ZFuncType([arg.Type ?? ZType.Unit], returnType);
        var resolved = interop.ResolveOverloadCallSite(typeName, methodName, funcType, span);
        return new IrNode.ClrCall(typeName, methodName, [arg], ResolvedMethodInfo: resolved)
        {
            Type = returnType,
            Span = span,
        };
    }

    private IReadOnlyList<ZType>? ResolveGenericCallSite(
        string typeName,
        string methodName,
        int genericArity,
        IReadOnlyList<IrNode> args,
        ZType resolvedReturnType
    )
    {
        var clr = new ClrInterop(_diagnostics, _assemblySearchPaths);
        var clrType = clr.FindType(typeName);
        if (clrType is null)
            return null;

        var candidates = clrType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m =>
                m.Name == methodName
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == genericArity
            )
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Get actual argument count from lowered IR nodes
        var argCount = args.Count;

        // Score each candidate: prefer overloads where all params are plain generic
        // type parameters and parameter count matches the call's argument count
        var scored = candidates
            .Select(m =>
            {
                var parameters = m.GetParameters();
                var allPlainParams = parameters.All(p => p.ParameterType.IsGenericParameter);
                var paramCountMatch = parameters.Length == argCount;
                return (
                    Method: m,
                    AllPlainParams: allPlainParams,
                    ParamCountMatch: paramCountMatch
                );
            })
            .ToList();

        // Pick the best candidate: prefer param count match + plain params,
        // then param count match, then plain params, then any
        MethodInfo? chosen = null;
        if (argCount > 0)
        {
            chosen = scored.FirstOrDefault(s => s.ParamCountMatch && s.AllPlainParams).Method;
            if (chosen is null)
                chosen = scored.FirstOrDefault(s => s.ParamCountMatch).Method;
        }

        if (chosen is null)
            chosen = scored.FirstOrDefault(s => s.AllPlainParams).Method;
        if (chosen is null)
            chosen = scored.OrderBy(s => s.Method.GetParameters().Length).First().Method;

        // Preferred path: resolve each generic type parameter from the position it
        // occupies in the chosen CLR method's signature. For `Serialize<T>(T, ...)` the
        // type arg comes from the argument; for `Deserialize<T>(string, ...) -> T` it
        // comes from the return type. Derived from the CLR method (not the ZScheme
        // annotation) so it works for CLR aliases imported across module boundaries.
        if (DeriveGenericTypeArgsFromMethod(chosen, args, resolvedReturnType) is { } positional)
            return positional;

        // Heuristic fallback for type params that don't appear directly as a parameter
        // or return type (e.g. nested inside a constructed type).
        return ExtractGenericTypeArgsFromTypes(resolvedReturnType, args, genericArity);
    }

    /// <summary>
    ///     Resolve generic type arguments positionally from the chosen CLR method's
    ///     signature, for the unambiguous cases:
    ///     <list type="bullet">
    ///         <item>the method returns the type parameter directly (e.g.
    ///         <c>Deserialize&lt;T&gt;(string) -> T</c>) → take the resolved return type;</item>
    ///         <item>the type parameter is a parameter directly and does not also appear in
    ///         the return type (e.g. <c>Serialize&lt;T&gt;(T, ...) -> string</c>) → take it from
    ///         that argument.</item>
    ///     </list>
    ///     When a type parameter is nested inside a constructed type, or appears in both a
    ///     parameter and the return (e.g. <c>Create&lt;T&gt;(T) -> ImmutableArray&lt;T&gt;</c>,
    ///     where the intended overload is ambiguous from the CLR method alone), this returns
    ///     null so the caller falls back to the heuristic. Works for CLR aliases imported
    ///     across module boundaries, since it needs only the CLR method and resolved types.
    /// </summary>
    private static IReadOnlyList<ZType>? DeriveGenericTypeArgsFromMethod(
        MethodInfo method,
        IReadOnlyList<IrNode> args,
        ZType returnType
    )
    {
        var genericParams = method.GetGenericArguments();
        var methodParams = method.GetParameters();
        var result = new List<ZType>(genericParams.Length);

        foreach (var gp in genericParams)
        {
            ZType? bound = null;

            // Returned directly → take the resolved return type.
            if (method.ReturnType == gp)
                bound = returnType;

            // Otherwise a parameter directly, and not also somewhere in the return type
            // (which would make the intended overload ambiguous) → take it from that arg.
            if (bound is null && !TypeMentionsParam(method.ReturnType, gp))
                for (var i = 0; i < methodParams.Length && i < args.Count; i++)
                    if (methodParams[i].ParameterType == gp && args[i].Type is { } argType)
                    {
                        bound = argType;
                        break;
                    }

            if (bound is null)
                return null;
            result.Add(bound);
        }

        return result;

        static bool TypeMentionsParam(Type t, Type gp)
        {
            if (t == gp)
                return true;
            if (t.HasElementType)
                return TypeMentionsParam(t.GetElementType()!, gp);
            if (t.IsGenericType)
                foreach (var ga in t.GetGenericArguments())
                    if (TypeMentionsParam(ga, gp))
                        return true;
            return false;
        }
    }

    /// <summary>
    ///     Extract generic type arguments from the resolved return type and arg types
    ///     of a CLR call by prioritizing type variables, then filling with primitives.
    ///     Collects from arg types first so that type args inferred from arguments take
    ///     priority over the return type (which may be a mapped type like Unit for void).
    ///     Heuristic fallback used when no declared signature template is available.
    /// </summary>
    private static IReadOnlyList<ZType> ExtractGenericTypeArgsFromTypes(
        ZType returnType,
        IReadOnlyList<IrNode> args,
        int arity
    )
    {
        var typeVars = new List<ZType>();
        var primitives = new List<ZType>();
        // Arg types first — type args inferred from arguments take priority
        // over the return type (which may be Unit for void, not a real type arg)
        foreach (var arg in args)
            if (arg.Type is not null)
                CollectTypeArgs(arg.Type, typeVars, primitives);
        CollectTypeArgs(returnType, typeVars, primitives);

        // Deduplicate by type variable ID while preserving order
        var seen = new HashSet<int>();
        var result = new List<ZType>();
        foreach (var t in typeVars)
        {
            var id = t switch
            {
                ZType.ZTypeVar tv => tv.Id,
                ZType.ZConstrainedVar cv => cv.Id,
                _ => -1,
            };
            if (id >= 0)
                if (seen.Add(id))
                    result.Add(t);
        }

        // Fill remaining slots with primitives
        foreach (var t in primitives)
            if (result.Count < arity && !result.Contains(t))
                result.Add(t);

        return result.Take(arity).ToList();

        static void CollectTypeArgs(ZType type, List<ZType> typeVars, List<ZType> primitives)
        {
            switch (type)
            {
                case ZType.ZTypeVar:
                case ZType.ZConstrainedVar:
                    typeVars.Add(type);
                    break;
                case ZType.ZPrimitiveType:
                    primitives.Add(type);
                    break;
                case ZType.ZNamedType nt:
                    foreach (var ta in nt.TypeArgs)
                        CollectTypeArgs(ta, typeVars, primitives);
                    break;
            }
        }
    }

    private IrNode LowerObjectExpr(AstNode.ObjectExpr n)
    {
        var methods = n
            .Methods.Select(m =>
            {
                var parms = m
                    .Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit))
                    .ToList();
                var body = Lower(m.Body);
                var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
                return new IrObjectMethod(m.Name, parms, retType, body, IsAsync: m.IsAsync);
            })
            .ToList();

        // Lower explicit constructor if present
        IrConstructor? irCtor = null;
        if (n.Constructor is { } ctor)
        {
            var ctorParams = ctor
                .Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit))
                .ToList();
            var superArgs = ctor.SuperArgs?.Select(Lower).ToList();
            var fieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Lower(fs.Value))).ToList();
            var bodyExprs = ctor.BodyExprs.Select(Lower).ToList();
            irCtor = new IrConstructor(ctorParams, superArgs, fieldSets, bodyExprs);
        }

        var defaultType = n.BaseClassName is not null
            ? new ZType.ZNamedType(n.BaseClassName, [])
            : new ZType.ZNamedType(n.InterfaceNames[0], []);

        return new IrNode.ObjectExpr(n.InterfaceNames.ToList(), methods, n.BaseClassName, irCtor)
        {
            Type = n.ResolvedType ?? defaultType,
            Span = n.Span,
        };
    }

    private IrNode LowerClassDecl(AstNode.ClassDecl n)
    {
        var fields = n
            .Fields.Select(f => new IrField(
                f.Name,
                f.TypeAnnotation,
                LowerAttributes(f.Attributes),
                f.IsMutable,
                f.IsInit
            ))
            .ToList();

        var methods = n
            .Methods.Select(m =>
            {
                var parms = m
                    .Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit))
                    .ToList();
                var body = Lower(m.Body);
                var retType = m.ReturnTypeAnnotation ?? ZType.Unit;
                return new IrObjectMethod(
                    m.Name,
                    parms,
                    retType,
                    body,
                    LowerAttributes(m.Attributes),
                    m.IsAsync
                );
            })
            .ToList();

        // Register class name so (ClassName args...) lowers to RecordNew.
        // Skip when the class has an explicit constructor: the C# emitter uses
        // field names as named arguments, but an explicit ctor's parameter names
        // are user-chosen and need not match the field names. Route those through
        // ClrNew instead, which uses positional arguments.
        if (n.Constructor is null)
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
            var ctorParams = ctor
                .Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit))
                .ToList();
            var superArgs = ctor.SuperArgs?.Select(Lower).ToList();
            var fieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Lower(fs.Value))).ToList();
            var bodyExprs = ctor.BodyExprs.Select(Lower).ToList();
            irCtor = new IrConstructor(ctorParams, superArgs, fieldSets, bodyExprs);
        }

        return new IrNode.ClassDecl(
            n.ClassName,
            n.TypeParams.ToList(),
            n.InterfaceNames.ToList(),
            fields,
            methods,
            n.IsOpen,
            n.BaseClassName,
            irCtor,
            LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams)
        )
        {
            Type = ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerInterfaceDecl(AstNode.InterfaceDecl n)
    {
        var methods = n
            .Methods.Select(m =>
            {
                var parms = m
                    .Params.Select(p => new IrParam(p.Name, p.TypeAnnotation ?? ZType.Unit))
                    .ToList();
                var retType = m.ReturnTypeAnnotation;
                return new IrInterfaceMethodSignature(m.Name, parms, retType);
            })
            .ToList();

        // Register slash-syntax accessors for method lowering
        foreach (var m in n.Methods)
            _classMethodAccessors.Add($"{n.InterfaceName}/{m.Name}");

        return new IrNode.InterfaceDecl(
            n.InterfaceName,
            n.TypeParams.ToList(),
            n.BaseInterfaceNames.ToList(),
            methods,
            LowerAttributes(n.Attributes),
            RemapTypeDeclConstraints(n.TypeParamConstraints, n.TypeParams)
        )
        {
            Type = ZType.Unit,
            Span = n.Span,
        };
    }

    private IrNode LowerImportClr(AstNode.ImportClr n)
    {
        _log.Debug(
            "LowerImportClr: processing {ImportCount} imports: [{ImportAliases}]",
            n.Imports.Count,
            string.Join(", ", n.Imports.Select(i => i.Alias))
        );
        ClrInterop? hintInterop = null;
        foreach (var import in n.Imports)
        {
            // Honor a `:from "Assembly"` hint so the call-site overload resolution
            // below (ResolveOverloadCallSite -> FindType) can locate types whose
            // namespace differs from their assembly file name. Idempotent: a no-op
            // when type inference already loaded the assembly.
            if (import.AssemblyHint is not null)
            {
                hintInterop ??= new ClrInterop(_diagnostics, _assemblySearchPaths, _typeAliases);
                hintInterop.EnsureAssemblyLoaded(import.AssemblyHint, import.Span);
            }

            // Remap constraint keys from ^k-style to T0-style using type param position
            var remappedConstraints = RemapClrImportConstraints(import);
            _log.Debug(
                "LowerImportClr: registering import alias={Alias}, qualName={QualName}, kind={Kind}",
                import.Alias,
                import.QualifiedName,
                import.Kind
            );

            // Look up out-param metadata from type inference
            _outParamsByAlias.TryGetValue(import.Alias, out var outParams);

            if (import.Kind != Static)
            {
                var lastSlash = import.QualifiedName.LastIndexOf('/');
                var lastDot = import.QualifiedName.LastIndexOf('.');
                var splitIndex = lastSlash >= 0 ? Math.Max(lastSlash, lastDot) : lastDot;
                if (splitIndex >= 0)
                {
                    var typeName = import.QualifiedName[..splitIndex];
                    var memberName = import.QualifiedName[(splitIndex + 1)..];
                    _clrImports[import.Alias] = (
                        typeName,
                        memberName,
                        import.TypeParams.Count,
                        import.Kind,
                        remappedConstraints,
                        outParams,
                        null
                    );
                }
            }
            else
            {
                var lastSlash = import.QualifiedName.LastIndexOf('/');
                var lastDot = import.QualifiedName.LastIndexOf('.');
                var splitIndex = lastSlash >= 0 ? Math.Max(lastSlash, lastDot) : lastDot;
                if (splitIndex >= 0)
                {
                    var typeName = import.QualifiedName[..splitIndex];
                    var methodName = import.QualifiedName[(splitIndex + 1)..];
                    _clrImports[import.Alias] = (
                        typeName,
                        methodName,
                        import.TypeParams.Count,
                        Static,
                        remappedConstraints,
                        outParams,
                        null
                    );
                }
            }
        }

        hintInterop?.Dispose();

        foreach (var ns in n.Namespaces)
            _clrNamespaces.Add(ns);
        _log.Debug(
            "LowerImportClr: after processing, _clrImports count={Count}, keys=[{Keys}]",
            _clrImports.Count,
            string.Join(", ", _clrImports.Keys)
        );
        return new IrNode.UnitConst { Type = ZType.Unit, Span = n.Span };
    }

    private static IReadOnlyList<string> ExtractFuncTypeParams(ZType? funcType)
    {
        if (funcType is not ZType.ZFuncType ft)
            return [];
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        if (freeVars.Count == 0)
            return [];
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
        IReadOnlyList<string> typeParams
    )
    {
        if (constraints is not { Count: > 0 })
            return null;
        var remapped = new Dictionary<string, GenericConstraintKind>();
        for (var i = 0; i < typeParams.Count; i++)
            if (constraints.TryGetValue(typeParams[i], out var kind))
                remapped[$"T{i}"] = kind;
        return remapped.Count > 0 ? remapped : null;
    }

    private static IReadOnlyDictionary<string, GenericConstraintKind>? RemapClrImportConstraints(
        ClrImport import
    )
    {
        if (import.TypeParamConstraints is not { Count: > 0 })
            return null;
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
        ZType.ZFuncType? funcType
    )
    {
        if (constraints is not { Count: > 0 } || funcType is null)
            return null;

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
            if (
                nameToVarId.TryGetValue(paramName, out var varId)
                && varIdToTi.TryGetValue(varId, out var tiName)
            )
                remapped[tiName] = kind;
        return remapped.Count > 0 ? remapped : null;
    }

    private static void CollectNameToVarMapping(
        ZType annotation,
        ZType resolved,
        Dictionary<string, int> map
    )
    {
        switch (annotation)
        {
            case ZType.ZNamedType { TypeArgs.Count: 0 } named
                when named.Name.StartsWith('^') && resolved is ZType.ZTypeVar tv:
                map.TryAdd(named.Name, tv.Id);
                break;
            case ZType.ZNamedType na
                when resolved is ZType.ZNamedType nr && na.TypeArgs.Count == nr.TypeArgs.Count:
                for (var i = 0; i < na.TypeArgs.Count; i++)
                    CollectNameToVarMapping(na.TypeArgs[i], nr.TypeArgs[i], map);
                break;
            case ZType.ZFuncType fa
                when resolved is ZType.ZFuncType fr && fa.Params.Count == fr.Params.Count:
                for (var i = 0; i < fa.Params.Count; i++)
                    CollectNameToVarMapping(fa.Params[i], fr.Params[i], map);
                CollectNameToVarMapping(fa.Return, fr.Return, map);
                break;
        }
    }

    private static IReadOnlyList<IrAttribute>? LowerAttributes(IReadOnlyList<AttributeDecl>? attrs)
    {
        if (attrs is null || attrs.Count == 0)
            return null;
        return attrs.Select(a => new IrAttribute(a.Name, a.PositionalArgs, a.NamedArgs)).ToList();
    }

    private static bool BodyReferences(AstNode node, string name)
    {
        return node switch
        {
            AstNode.Name n => n.Value == name,
            AstNode.Apply a => BodyReferences(a.Function, name)
                || a.Args.Any(arg => BodyReferences(arg, name)),
            AstNode.Let l => BodyReferences(l.Value, name) || BodyReferences(l.Body, name),
            AstNode.If i => BodyReferences(i.Condition, name)
                || BodyReferences(i.Then, name)
                || BodyReferences(i.Else, name),
            AstNode.Lambda lam => BodyReferences(lam.Body, name),
            AstNode.Match m => BodyReferences(m.Scrutinee, name)
                || m.Arms.Any(a => BodyReferences(a.Body, name)),
            AstNode.Raise r => BodyReferences(r.Expr, name),
            AstNode.Await a => BodyReferences(a.Expr, name),
            _ => false,
        };
    }
}

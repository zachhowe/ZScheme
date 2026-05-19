using System.Reflection;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

public sealed class TypeInferer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TypeInferer>();

    private readonly IReadOnlyList<string> _assemblySearchPaths;

    // Track class metadata for inheritance resolution
    private readonly Dictionary<string, ClassInfo> _classInfos = new();

    // Track imported class interface info for cross-module subtyping
    private readonly Dictionary<string, IReadOnlyList<string>> _importedClassInterfaces = new();

    // Track out-param metadata for CLR imports (keyed by alias)
    private readonly Dictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> _outParamsByAlias = new();
    private readonly Unifier _unifier;
    private string? _currentBaseClassName; // set during method body inference for super/ calls
    private IReadOnlyList<FieldDecl>? _currentClassFieldDecls; // set during method body inference for set!
    private Dictionary<string, ZType>? _currentTypeVarScope;
    private bool _inAsyncContext;
    private int _nextTypeVar;

    private readonly TypeAliasRegistry? _typeAliases;

    /// <summary>
    /// The fully-qualified name of the module currently being inferred (e.g. <c>"stdlib/list"</c>).
    /// Used to assign qualified names to locally-defined functions when they are registered as
    /// overload candidates so calls can be routed back to the same module's emitted class. Null
    /// for unnamed contexts (REPL, implicit modules), in which case locals are registered under
    /// their bare name.
    /// </summary>
    public string? CurrentModuleName { get; set; }

    public TypeInferer(DiagnosticBag diagnostics, IReadOnlyList<string>? assemblySearchPaths = null,
        TypeAliasRegistry? typeAliases = null)
    {
        Diagnostics = diagnostics;
        _unifier = new Unifier(Substitution, diagnostics, assemblySearchPaths,
            LookupClassInterfaces);
        _assemblySearchPaths = assemblySearchPaths ?? [];
        _typeAliases = typeAliases;
    }

    private ZType MakeVariadicType(ZType elemType) =>
        new ZType.ZNamedType("Mutable-Vector", [elemType]);

    private ZType MakeTaskType(ZType? innerType) =>
        new ZType.ZNamedType("Task", innerType is not null ? [innerType] : []);

    private bool typeAliasesIsTask(string name) =>
        (_typeAliases is not null && _typeAliases.IsTaskName(name)) || name is "Task" or "System.Threading.Tasks.Task";

    private bool typeAliasesIsValueTuple(string name) =>
        (_typeAliases is not null && _typeAliases.IsValueTupleName(name)) || name == "ValueTuple";

    private bool typeAliasesIsMutableVector(string name) =>
        (_typeAliases is not null && _typeAliases.IsMutableVectorName(name)) || name == "Mutable-Vector";

    private ZType MakeTupleType(IReadOnlyList<ZType> elements) =>
        new ZType.ZNamedType("ValueTuple", elements);

    /// <summary>
    ///     Out-param metadata detected during type inference, keyed by import alias.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> OutParamsByAlias => _outParamsByAlias;

    public DiagnosticBag Diagnostics { get; }

    public Substitution Substitution { get; } = new();

    /// <summary>
    ///     Lookup function for the Unifier to check interface relationships
    ///     of ZScheme-defined classes that aren't yet compiled to assemblies.
    /// </summary>
    private IReadOnlyList<string>? LookupClassInterfaces(string className)
    {
        if (_classInfos.TryGetValue(className, out var info))
            return info.InterfaceNames;
        if (_importedClassInterfaces.TryGetValue(className, out var interfaces))
            return interfaces;
        // Try short name (strip namespace prefix) — class names are stored without namespace
        var dotIdx = className.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            var shortName = className[(dotIdx + 1)..];
            if (_classInfos.TryGetValue(shortName, out var info2))
                return info2.InterfaceNames;
            if (_importedClassInterfaces.TryGetValue(shortName, out var interfaces2))
                return interfaces2;
        }

        return null;
    }

    /// <summary>
    ///     Register class interface information from imported/injected modules
    ///     so that cross-module interface subtyping works during unification.
    /// </summary>
    public void RegisterClassInterfaces(IReadOnlyDictionary<string, IReadOnlyList<string>> classInterfaces)
    {
        foreach (var (className, interfaces) in classInterfaces)
        {
            _importedClassInterfaces[className] = interfaces;
            Log.Debug("TypeInferer: registered class '{ClassName}' implementing [{Interfaces}]",
                className, string.Join(", ", interfaces));
        }
    }

    public ZType FreshVar()
    {
        return new ZType.ZTypeVar(_nextTypeVar++);
    }

    public ZType Infer(AstNode node, TypeEnv env)
    {
        return node switch
        {
            AstNode.IntLit n => Assign(n, ZType.Int),
            AstNode.FloatLit n => Assign(n, ZType.Float),
            AstNode.BoolLit n => Assign(n, ZType.Bool),
            AstNode.StringLit n => Assign(n, ZType.String),
            AstNode.UnitLit n => Assign(n, ZType.Unit),
            AstNode.NullLit n => Assign(n, FreshVar()),
            AstNode.Name n => InferName(n, env),
            AstNode.Let n => InferLet(n, env),
            AstNode.If n => InferIf(n, env),
            AstNode.Lambda n => InferLambda(n, env),
            AstNode.Apply n => InferApply(n, env),
            AstNode.Define n => InferDefine(n, env),
            AstNode.DefineValue n => InferDefineValue(n, env),
            AstNode.Program n => InferProgram(n, env),
            AstNode.Partial n => InferPartial(n, env),
            AstNode.Match n => InferMatch(n, env),
            AstNode.RecordDecl n => InferRecordDecl(n, env),
            AstNode.UnionDecl n => InferUnionDecl(n, env),
            AstNode.ObjectExpr n => InferObjectExpr(n, env),
            AstNode.ClassDecl n => InferClassDecl(n, env),
            AstNode.InterfaceDecl n => InferInterfaceDecl(n, env),
            AstNode.SuperMethodCall n => InferSuperMethodCall(n, env),
            AstNode.SetField n => InferSetField(n, env),
            AstNode.ClrNew n => InferClrNew(n, env),
            AstNode.Raise n => InferRaise(n, env),
            AstNode.DefineAsync n => InferDefineAsync(n, env),
            AstNode.Await n => InferAwait(n, env),
            AstNode.TupleNew n => InferTupleNew(n, env),
            AstNode.WithHandlers n => InferWithHandlers(n, env),
            AstNode.With n => InferWith(n, env),
            AstNode.ImportClr n => InferImportClr(n, env),
            AstNode.TypeAliasDecl n => Assign(n, ZType.Unit),
            AstNode.NamespaceDecl n => Assign(n, ZType.Unit),
            AstNode.ModuleDecl n => InferModuleDecl(n, env),
            AstNode.Import n => Assign(n, ZType.Unit),
            AstNode.Export n => Assign(n, ZType.Unit),
            _ => ReportUnknown(node)
        };
    }

    private ZType Assign(AstNode node, ZType type)
    {
        node.ResolvedType = type;
        return type;
    }

    private ZType InferModuleDecl(AstNode.ModuleDecl node, TypeEnv env)
    {
        foreach (var form in node.Body)
            Infer(form, env);
        return Assign(node, ZType.Unit);
    }

    private ZType InferName(AstNode.Name node, TypeEnv env)
    {
        var type = env.Lookup(node.Value);
        if (type is null)
        {
            // Overloaded names: defer resolution to the call site (InferApply)
            // when there are multiple candidates. With exactly one candidate the
            // name is unambiguous and is treated like a regular binding so it
            // can also be used as a value (e.g. `(let [f cons] ...)`).
            var overloads = env.LookupOverloads(node.Value);
            if (overloads is not null)
            {
                node.OverloadCandidates = overloads;
                if (overloads.Candidates.Count == 1)
                {
                    var only = overloads.Candidates[0];
                    node.ResolvedQualifiedName = only.QualifiedName;
                    return Assign(node, Instantiate(only.Type));
                }
                return Assign(node, FreshVar());
            }

            Diagnostics.Error($"Undefined variable: '{node.Value}'", node.Span);
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

        ZType bindType;
        if (node.TypeAnnotation is not null)
        {
            // Resolve annotation and unify — enables upcasting (e.g., MemoryStream → Stream)
            var resolved = ResolveTypeInEnv(node.TypeAnnotation, env);
            _unifier.Unify(valueType, resolved, node.Value.Span);
            bindType = resolved;
        }
        else
        {
            // Value restriction: only generalize syntactic values (lambdas, literals),
            // not applications, to prevent premature polymorphism that breaks type propagation
            bindType = node.Value is AstNode.Apply or AstNode.ClrNew
                ? valueType
                : Generalize(valueType, env);
        }

        // Extend env with the binding
        var childEnv = env.CreateChild();
        childEnv.Define(node.VarName, bindType);

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
        var typeVarScope = new Dictionary<string, ZType>();
        var isVariadic = node.Params.Count > 0 && node.Params[^1].IsVariadic;

        foreach (var param in node.Params)
        {
            var pType = ResolveTypeVarAnnotations(param.TypeAnnotation, typeVarScope) ?? FreshVar();
            paramTypes.Add(pType);
            // Variadic param is bound as Mutable-Vector[T] in the body
            if (param.IsVariadic)
                childEnv.Define(param.Name, MakeVariadicType(pType));
            else
                childEnv.Define(param.Name, pType);
            param.ResolvedType = param.IsVariadic
                ? MakeVariadicType(pType)
                : pType;
        }

        var prevAsyncContext = _inAsyncContext;
        var prevTypeVarScope = _currentTypeVarScope;
        _inAsyncContext = false;
        _currentTypeVarScope = typeVarScope;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;
        _currentTypeVarScope = prevTypeVarScope;
        var funcType = new ZType.ZFuncType(paramTypes, bodyType, isVariadic);
        return Assign(node, funcType);
    }

    private static readonly IReadOnlySet<PrimitiveKind> NumericKinds =
        new HashSet<PrimitiveKind> { PrimitiveKind.Int, PrimitiveKind.Float };

    private ZType InferApply(AstNode.Apply node, TypeEnv env)
    {
        // Special-case 1-arg `(- x)` / `(/ x)` for Scheme-style unary negate / invert.
        // The variadic-operator AST expansion in AstBuilder leaves these alone because
        // a literal `0`/`1` would mistype against `Float`. Infer the arg under a numeric
        // constraint and bypass the binary signature lookup; IR lowering rewrites the
        // call to UnaryOp("-", _) or BinOp("/", oneLit, _) using the resolved type.
        if (node.Function is AstNode.Name { Value: "-" or "/" } unaryName
            && node.Args.Count == 1)
        {
            var unaryArgType = Infer(node.Args[0], env);
            var numVar = new ZType.ZConstrainedVar(_nextTypeVar++, NumericKinds);
            _unifier.Unify(numVar, unaryArgType, node.Span);
            var unaryResolved = Substitution.Apply(unaryArgType);
            unaryName.ResolvedType = new ZType.ZFuncType([unaryResolved], unaryResolved);
            return Assign(node, unaryResolved);
        }

        // Handle value/N tuple accessor
        if (node.Function is AstNode.Name { Value: var fname } && fname.StartsWith("value/")
            && int.TryParse(fname["value/".Length..], out var tupleIdx))
        {
            if (node.Args.Count != 1)
            {
                Diagnostics.Error("Tuple accessor requires exactly 1 argument", node.Span);
                return Assign(node, FreshVar());
            }
           var argType = Infer(node.Args[0], env);
            var resolvedArg = Substitution.Apply(argType);
           if (resolvedArg is ZType.ZNamedType vtAccess && (_typeAliases?.IsValueTupleName(vtAccess.Name) ?? false))
            {
                if (tupleIdx < 0 || tupleIdx >= vtAccess.TypeArgs.Count)
                {
                    Diagnostics.Error(
                        $"Tuple index {tupleIdx} out of range for {vtAccess.TypeArgs.Count}-element tuple",
                        node.Span);
                    return Assign(node, FreshVar());
                }
                return Assign(node, vtAccess.TypeArgs[tupleIdx]);
            }
            // Arg type not yet resolved — return fresh var
            return Assign(node, FreshVar());
        }

        // Overload resolution: when the call target is a bare name with 2+
        // candidates in scope, infer arg types first, then pick the unique
        // candidate whose signature unifies against (argTypes -> freshRet).
        // Speculative unifications are rolled back via Substitution snapshots
        // so failed candidates don't leak constraints. We bypass the normal
        // Infer(node.Function) path to avoid InferName committing prematurely.
        // Local defines participate in the overload set alongside imports
        // (registered by InferDefine), so this fires for "local + import" name
        // collisions even though Lookup would also find the local binding.
        if (node.Function is AstNode.Name overloadName
            && env.LookupOverloads(overloadName.Value) is { Candidates.Count: > 1 } overloadSet)
        {
            overloadName.OverloadCandidates = overloadSet;
            var overloadArgTypes = node.Args.Select(a => Infer(a, env)).ToList();
            var resolvedRet = ResolveOverload(overloadName, overloadSet, overloadArgTypes, node.Span);
            // Synthesize the chosen function type onto the Name so downstream
            // passes (resolution, codegen) see a proper function signature.
            overloadName.ResolvedType = new ZType.ZFuncType(
                overloadArgTypes.Select(t => Substitution.Apply(t)).ToList(),
                Substitution.Apply(resolvedRet));
            return Assign(node, Substitution.Apply(resolvedRet));
        }

        var funcType = Infer(node.Function, env);
        var argTypes = node.Args.Select(a => Infer(a, env)).ToList();

        // Check if the resolved function type is variadic
        var resolved = Substitution.Apply(funcType);
        if (resolved is ZType.ZFuncType { IsVariadic: true } variadicFt)
        {
            var fixedCount = variadicFt.Params.Count - 1;
            if (argTypes.Count < fixedCount)
            {
                Diagnostics.Error(
                    $"Too few arguments: expected at least {fixedCount}, got {argTypes.Count}",
                    node.Span);
                return Assign(node, variadicFt.Return);
            }

            // Unify fixed params
            for (var i = 0; i < fixedCount; i++)
                _unifier.Unify(variadicFt.Params[i], argTypes[i], node.Span);

            // Unify each variadic arg with the element type
            var elemType = variadicFt.Params[^1];
            for (var i = fixedCount; i < argTypes.Count; i++)
                _unifier.Unify(elemType, argTypes[i], node.Span);

            var resolvedRet = Substitution.Apply(variadicFt.Return);
            return Assign(node, resolvedRet);
        }

        var retType = FreshVar();
        var expectedFuncType = new ZType.ZFuncType(argTypes, retType);

        _unifier.Unify(funcType, expectedFuncType, node.Span);
        var resolvedRet2 = Substitution.Apply(retType);
        return Assign(node, resolvedRet2);
    }

    private ZType ResolveOverload(
        AstNode.Name name,
        OverloadSet set,
        IReadOnlyList<ZType> argTypes,
        SourceSpan span)
    {
        var matches = new List<(OverloadCandidate Candidate, IReadOnlyDictionary<int, ZType> Snapshot, ZType ReturnType)>();
        foreach (var candidate in set.Candidates)
        {
            var savepoint = Substitution.Snapshot();
            var scratchDiag = new DiagnosticBag();
            var scratchUnifier = new Unifier(Substitution, scratchDiag);
            var instantiated = Instantiate(candidate.Type);
            var freshRet = FreshVar();
            var expected = new ZType.ZFuncType(argTypes, freshRet);
            var ok = scratchUnifier.Unify(instantiated, expected, span) && !scratchDiag.HasErrors;
            if (ok)
                matches.Add((candidate, Substitution.Snapshot(), Substitution.Apply(freshRet)));
            Substitution.Restore(savepoint);
        }

        if (matches.Count == 0)
        {
            var argList = string.Join(", ", argTypes.Select(t => ZType.Format(Substitution.Apply(t))));
            var candList = string.Join(", ", set.Candidates.Select(c => c.QualifiedName));
            Diagnostics.Error(
                $"No overload of '{name.Value}' matches argument types ({argList}). Candidates: {candList}",
                span);
            return FreshVar();
        }

        if (matches.Count > 1)
        {
            // When all matches produce the same return type at this call site, the
            // candidates are interchangeable — pick the last (matches the legacy
            // last-write-wins behavior so two stdlib modules each exporting e.g.
            // `id : forall a. a -> a` don't silently break user code).
            var firstRet = matches[0].ReturnType;
            var allEquivalent = matches.All(m => m.ReturnType.Equals(firstRet));
            if (!allEquivalent)
            {
                var candList = string.Join(", ", matches.Select(m => m.Candidate.QualifiedName));
                Diagnostics.Error(
                    $"Ambiguous overload of '{name.Value}'; candidates: {candList}. Qualify the call site explicitly.",
                    span);
                return FreshVar();
            }
            var fallback = matches[^1];
            Substitution.Restore(fallback.Snapshot);
            name.ResolvedQualifiedName = fallback.Candidate.QualifiedName;
            return fallback.ReturnType;
        }

        var winner = matches[0];
        Substitution.Restore(winner.Snapshot);
        name.ResolvedQualifiedName = winner.Candidate.QualifiedName;
        return winner.ReturnType;
    }

    private ZType InferDefine(AstNode.Define node, TypeEnv env)
    {
        var childEnv = env.CreateChild();
        var paramTypes = new List<ZType>();
        var typeVarScope = new Dictionary<string, ZType>();
        var isVariadic = node.Params.Count > 0 && node.Params[^1].IsVariadic;

        foreach (var param in node.Params)
        {
            var pType = ResolveTypeVarAnnotations(param.TypeAnnotation, typeVarScope) ?? FreshVar();
            paramTypes.Add(pType);
            // Variadic param is bound as Mutable-Vector[T] in the body
            if (param.IsVariadic)
                childEnv.Define(param.Name, MakeVariadicType(pType));
            else
                childEnv.Define(param.Name, pType);
            param.ResolvedType = param.IsVariadic
                ? MakeVariadicType(pType)
                : pType;
        }

        // For self-recursion, add the function itself to the environment
        var selfRetType = ResolveTypeVarAnnotations(node.ReturnTypeAnnotation, typeVarScope) ?? FreshVar();
        var selfType = new ZType.ZFuncType(paramTypes, selfRetType, isVariadic);
        childEnv.Define(node.FnName, selfType);

        // Pre-register self in the outer overload set so recursive calls inside the body can
        // be resolved by InferApply when the call site has 2+ overload candidates (e.g. when
        // multiple imports collide on the same name). The placeholder type is replaced with
        // the generalized type after body inference.
        var localQname = LocalOverloadQualifiedName(node.FnName);
        env.DefineOrReplaceOverload(node.FnName, localQname, selfType);

        var prevAsyncContext = _inAsyncContext;
        var prevTypeVarScope = _currentTypeVarScope;
        _inAsyncContext = false;
        _currentTypeVarScope = typeVarScope;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;
        _currentTypeVarScope = prevTypeVarScope;

        // Unify body type with declared return type
        _unifier.Unify(bodyType, selfRetType, node.Span);

        // Resolve the function type with substitutions
        var resolvedFuncType = Substitution.Apply(selfType);

        // Drop the pre-body placeholder so its monomorphic type variables don't count
        // against generalization (Generalize subtracts vars still free in env). Without
        // this, the function would be inferred as a non-polymorphic same-module
        // candidate and subsequent calls with different concrete types would fail.
        env.RemoveOverloadCandidate(node.FnName, localQname);

        var generalized = Generalize(resolvedFuncType, env);

        // Register in the outer environment — keep the single-binding entry for value-position
        // uses (e.g. `(let [f local-fn] ...)`) and (re-)add the overload candidate with the
        // generalized type so call-site dispatch sees a properly polymorphic signature.
        env.Define(node.FnName, generalized);
        env.DefineOrReplaceOverload(node.FnName, localQname, generalized);
        return Assign(node, resolvedFuncType);
    }

    private string LocalOverloadQualifiedName(string fnName) =>
        CurrentModuleName is null ? fnName : $"{CurrentModuleName}/{fnName}";

    private ZType InferDefineValue(AstNode.DefineValue node, TypeEnv env)
    {
        var valueType = Infer(node.Value, env);
        var generalized = Generalize(valueType, env);
        env.Define(node.VarName, generalized);
        return Assign(node, valueType);
    }

    private ZType InferProgram(AstNode.Program node, TypeEnv env)
    {
        var last = ZType.Unit;
        foreach (var form in node.TopLevelForms) last = Infer(form, env);
        return Assign(node, last);
    }

    private ZType InferPartial(AstNode.Partial node, TypeEnv env)
    {
        var funcType = Infer(node.Function, env);
        var appliedTypes = node.Args.Select(a => Infer(a, env)).ToList();

        if (Substitution.Apply(funcType) is ZType.ZFuncType ft)
        {
            if (appliedTypes.Count >= ft.Params.Count)
            {
                Diagnostics.Error("Too many arguments for partial application", node.Span);
                return Assign(node, FreshVar());
            }

            // Unify supplied args with first N params
            for (var i = 0; i < appliedTypes.Count; i++)
                _unifier.Unify(ft.Params[i], appliedTypes[i], node.Span);

            // Remaining params form the new function type
            var remaining = ft.Params.Skip(appliedTypes.Count).ToList();
            var result = new ZType.ZFuncType(remaining, ft.Return);
            return Assign(node, Substitution.Apply(result));
        }

        // Function type not yet known — create type vars
        var totalParams = appliedTypes.Count + 1; // at least one remaining
        var allParams = new List<ZType>();
        for (var i = 0; i < totalParams; i++)
            allParams.Add(i < appliedTypes.Count ? appliedTypes[i] : FreshVar());

        var retVar = FreshVar();
        _unifier.Unify(funcType, new ZType.ZFuncType(allParams, retVar), node.Span);

        var remainingAfter = allParams.Skip(appliedTypes.Count).ToList();
        var resultType = new ZType.ZFuncType(remainingAfter, retVar);
        return Assign(node, Substitution.Apply(resultType));
    }

    private ZType InferTupleNew(AstNode.TupleNew node, TypeEnv env)
    {
        var elementTypes = new List<ZType>();
        foreach (var elem in node.Elements)
            elementTypes.Add(Infer(elem, env));
        return Assign(node, MakeTupleType(elementTypes));
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

        return Assign(node, Substitution.Apply(resultType));
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
                    var applied = Substitution.Apply(instantiated);
                    if (applied is ZType.ZFuncType ft)
                    {
                        _unifier.Unify(ft.Return, expected, ctor.Span);
                        for (var i = 0; i < Math.Min(ctor.Fields.Count, ft.Params.Count); i++)
                        {
                            var fieldEnv = env;
                            InferPattern(ctor.Fields[i], ft.Params[i], fieldEnv);
                        }
                    }
                    else
                    {
                        // Nullary constructor (e.g., None) — unify directly with expected
                        _unifier.Unify(applied, expected, ctor.Span);
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
           case Pattern.Tuple tup:
                var resolved = Substitution.Apply(expected);
               if (resolved is ZType.ZNamedType vt && (_typeAliases?.IsValueTupleName(vt.Name) ?? false))
                {
                    if (tup.Elements.Count != vt.TypeArgs.Count)
                        Diagnostics.Error(
                            $"Tuple pattern has {tup.Elements.Count} elements but expected {vt.TypeArgs.Count}",
                            tup.Span);
                    for (var i = 0; i < Math.Min(tup.Elements.Count, vt.TypeArgs.Count); i++)
                        InferPattern(tup.Elements[i], vt.TypeArgs[i], env);
                }
                else
                {
                    var elemTypes = new List<ZType>();
                    foreach (var elem in tup.Elements)
                    {
                        var elemType = FreshVar();
                        InferPattern(elem, elemType, env);
                        elemTypes.Add(elemType);
                    }
                    var tupleType = MakeTupleType(elemTypes);
                    _unifier.Unify(tupleType, expected, tup.Span);
                }
                tup.ResolvedType = expected;
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
        for (var i = 0; i < node.Fields.Count; i++)
        {
            var accessorType = new ZType.ZFuncType([recordType], fieldTypes[i]);
            var genAccessor = node.TypeParams.Count > 0 ? Generalize(accessorType, env) : accessorType;
            env.Define($"{node.RecordName}/{node.Fields[i].Name}", genAccessor);
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

        // Define the union name itself in env for export resolution
        var unionTypeGeneralized = node.TypeParams.Count > 0 ? Generalize(unionType, env) : unionType;
        env.Define(node.UnionName, unionTypeGeneralized);

        return Assign(node, ZType.Unit);
    }

    private ZType InferWithHandlers(AstNode.WithHandlers node, TypeEnv env)
    {
        var bodyType = Infer(node.Body, env);
        var clrInterop = new ClrInterop(Diagnostics, _assemblySearchPaths, _typeAliases);

        // Track previously-accepted handler types so we can flag shadowed
        // handlers. Handlers dispatch in source order (first match wins), so a
        // handler whose exception type is a subtype of (or equal to) an earlier
        // handler's type is unreachable. The C# backend rejects this with
        // CS0160; we surface the same requirement uniformly at the frontend.
        var seenHandlerTypes = new List<(string Name, Type ClrType)>();

        foreach (var handler in node.Handlers)
        {
            // Validate exception type exists and is a System.Exception subclass
            var clrType = clrInterop.FindType(handler.ExceptionTypeName);
            if (clrType is null)
                Diagnostics.Error(
                    $"Exception type '{handler.ExceptionTypeName}' not found",
                    handler.Span);
            else if (!typeof(Exception).IsAssignableFrom(clrType))
                Diagnostics.Error(
                    $"Handler type '{handler.ExceptionTypeName}' must be a System.Exception subclass",
                    handler.Span);
            else
            {
                var shadow = seenHandlerTypes.FirstOrDefault(prev => prev.ClrType.IsAssignableFrom(clrType));
                if (shadow.ClrType is not null)
                    Diagnostics.Error(
                        $"Handler for '{handler.ExceptionTypeName}' is unreachable: a previous handler for '{shadow.Name}' already catches this type. Order handlers most-specific first.",
                        handler.Span);
                else
                    seenHandlerTypes.Add((handler.ExceptionTypeName, clrType));
            }

            // Type the binding variable as the exception type and infer handler body
            var handlerEnv = env.CreateChild();
            var exType = new ZType.ZNamedType(handler.ExceptionTypeName, []);
            handlerEnv.Define(handler.BindingVarName, exType);
            var handlerType = Infer(handler.HandlerBody, handlerEnv);
            _unifier.Unify(handlerType, bodyType, handler.Span);
        }

        return Assign(node, bodyType);
    }

    private ZType InferWith(AstNode.With node, TypeEnv env)
    {
        var recordType = Infer(node.Record, env);
        var resolvedRecord = Substitution.Apply(recordType);

        if (resolvedRecord is not ZType.ZNamedType named)
        {
            Diagnostics.Error(
                $"'with' target must be a record instance, got {resolvedRecord}",
                node.Record.Span);
            foreach (var (_, valueExpr) in node.Updates)
                Infer(valueExpr, env);
            return Assign(node, resolvedRecord);
        }

        foreach (var (fieldName, valueExpr) in node.Updates)
        {
            var accessorKey = $"{named.Name}/{fieldName}";
            var accessorType = env.Lookup(accessorKey);
            if (accessorType is null)
            {
                Diagnostics.Error(
                    $"Record '{named.Name}' has no field '{fieldName}'",
                    valueExpr.Span);
                Infer(valueExpr, env);
                continue;
            }

            var instantiated = Instantiate(accessorType);
            if (instantiated is not ZType.ZFuncType { Params: var accParams, Return: var accReturn }
                || accParams.Count != 1)
            {
                Diagnostics.Error(
                    $"'{accessorKey}' is not a field accessor",
                    valueExpr.Span);
                Infer(valueExpr, env);
                continue;
            }

            // Unify the accessor's record parameter with the resolved record type so that
            // any record type parameters get substituted in the field's return type.
            _unifier.Unify(accParams[0], resolvedRecord, node.Record.Span);
            var valueType = Infer(valueExpr, env);
            _unifier.Unify(valueType, accReturn, valueExpr.Span);
        }

        return Assign(node, resolvedRecord);
    }

    private ZType InferObjectExpr(AstNode.ObjectExpr node, TypeEnv env)
    {
        // Validate base class if present
        var resolvedBaseClass = node.BaseClassName;
        if (resolvedBaseClass is not null)
        {
            if (_classInfos.TryGetValue(resolvedBaseClass, out var baseInfo))
            {
                if (!baseInfo.IsOpen)
                    Diagnostics.Error(
                        $"Cannot inherit from sealed class '{resolvedBaseClass}'. Mark it with #:open to allow subclassing",
                        node.Span);
            }
            else
            {
                Diagnostics.Error($"Base class '{resolvedBaseClass}' not found", node.Span);
                resolvedBaseClass = null;
            }
        }

        // Type-check explicit constructor if present
        if (node.Constructor is { } ctor)
        {
            var ctorEnv = env.CreateChild();
            foreach (var param in ctor.Params)
            {
                var pType = param.TypeAnnotation is not null
                    ? ResolveTypeInEnv(param.TypeAnnotation, env)
                    : FreshVar();
                ctorEnv.Define(param.Name, pType);
            }

            if (ctor.SuperArgs is not null)
            {
                var argTypes = ctor.SuperArgs.Select(arg => Infer(arg, ctorEnv)).ToList();
                UnifySuperArgs(resolvedBaseClass, ctor.SuperArgs, argTypes, env);
            }

            foreach (var expr in ctor.BodyExprs)
                Infer(expr, ctorEnv);
        }

        // Set base class context for super/ calls in methods
        var savedBase = _currentBaseClassName;
        _currentBaseClassName = resolvedBaseClass;

        foreach (var method in node.Methods)
        {
            var methodEnv = env.CreateChild();
            foreach (var param in method.Params)
            {
                var pType = param.TypeAnnotation ?? FreshVar();
                methodEnv.Define(param.Name, pType);
            }

            var bodyType = Infer(method.Body, methodEnv);
            // Without this unification, an unannotated free var in the body (e.g.
            // a pattern-bound variable from an enclosing match where the
            // scrutinee's type-arg never got constrained otherwise) escapes
            // inference unresolved. Downstream IL emit then maps the captured
            // local to `object`, but the method signature still uses the
            // annotated return type — producing a verifier StackUnexpected.
            if (method.ReturnTypeAnnotation is not null)
                _unifier.Unify(bodyType, method.ReturnTypeAnnotation, method.Body.Span);
        }

        _currentBaseClassName = savedBase;

        var typeName = resolvedBaseClass ?? node.InterfaceNames[0];
        var type = new ZType.ZNamedType(typeName, []);
        return Assign(node, type);
    }

    private ZType InferClassDecl(AstNode.ClassDecl node, TypeEnv env)
    {
        var typeArgs = new List<ZType>();
        var localEnv = env.CreateChild();

        foreach (var tp in node.TypeParams)
        {
            var tv = FreshVar();
            typeArgs.Add(tv);
            localEnv.Define(tp, tv);
        }

        var classType = new ZType.ZNamedType(node.ClassName, typeArgs);
        var fieldTypes = new List<ZType>();

        foreach (var field in node.Fields)
        {
            var ft = ResolveTypeInEnv(field.TypeAnnotation, localEnv);
            fieldTypes.Add(ft);
        }

        // Resolve base class if present
        var resolvedBaseClass = node.BaseClassName;
        var inheritedFields = new List<(string Name, ZType Type)>();
        var inheritedMethods = new List<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)>();
        // Collect effective interface names (may include base class name if it's actually an interface)
        var effectiveInterfaceNames = new List<string>(node.InterfaceNames);

        if (resolvedBaseClass is not null)
        {
            // Validate base class exists and is open
            if (_classInfos.TryGetValue(resolvedBaseClass, out var baseInfo))
            {
                if (!baseInfo.IsOpen)
                    Diagnostics.Error(
                        $"Cannot inherit from sealed class '{resolvedBaseClass}'. Mark it with #:open to allow subclassing",
                        node.Span);

                // Detect circular inheritance
                var visited = new HashSet<string> { node.ClassName };
                var current = resolvedBaseClass;
                while (current is not null)
                {
                    if (!visited.Add(current))
                    {
                        Diagnostics.Error($"Circular inheritance detected involving '{node.ClassName}'", node.Span);
                        break;
                    }

                    current = _classInfos.TryGetValue(current, out var info) ? info.BaseClassName : null;
                }

                // Collect all inherited fields (walk entire chain)
                inheritedFields.AddRange(GetAllInheritedFields(resolvedBaseClass));
                inheritedMethods.AddRange(GetAllInheritedMethods(resolvedBaseClass));
            }
            else
            {
                // Base class name is not a known ZScheme class — treat it as an interface
                effectiveInterfaceNames.Insert(0, resolvedBaseClass);
                resolvedBaseClass = null;
            }
        }

        // Constructor type depends on whether there's an explicit constructor
        ZType ctorType;
        if (node.Constructor is { } ctor)
        {
            // Explicit constructor — infer param types
            var ctorEnv = localEnv.CreateChild();
            var ctorParamTypes = new List<ZType>();
            foreach (var param in ctor.Params)
            {
                var pType = param.TypeAnnotation is not null
                    ? ResolveTypeInEnv(param.TypeAnnotation, localEnv)
                    : FreshVar();
                ctorParamTypes.Add(pType);
                ctorEnv.Define(param.Name, pType);
            }

            // Type-check super args if present
            if (ctor.SuperArgs is not null && resolvedBaseClass is not null &&
                _classInfos.TryGetValue(resolvedBaseClass, out var baseCi))
            {
                var argTypes = ctor.SuperArgs.Select(arg => Infer(arg, ctorEnv)).ToList();
                UnifySuperArgs(resolvedBaseClass, ctor.SuperArgs, argTypes, env);
            }

            // Type-check set! expressions
            foreach (var (fieldName, value) in ctor.FieldSets)
            {
                var valType = Infer(value, ctorEnv);
                // Find the field and unify types
                var fieldIdx = node.Fields.ToList().FindIndex(f => f.Name == fieldName);
                if (fieldIdx >= 0)
                    _unifier.Unify(valType, fieldTypes[fieldIdx], value.Span);
                else
                    Diagnostics.Error($"Unknown field '{fieldName}' in constructor set!", value.Span);
            }

            // Type-check body expressions
            foreach (var expr in ctor.BodyExprs)
                Infer(expr, ctorEnv);

            ctorType = new ZType.ZFuncType(ctorParamTypes, classType);
        }
        else if (inheritedFields.Count > 0)
        {
            // Auto-generated composite constructor: base fields + own fields
            var allFieldTypes = inheritedFields.Select(f => f.Type).Concat(fieldTypes).ToList();
            ctorType = new ZType.ZFuncType(allFieldTypes, classType);
        }
        else
        {
            // No inheritance, no explicit constructor: own fields only
            ctorType = new ZType.ZFuncType(fieldTypes, classType);
        }

        var generalizedCtor = node.TypeParams.Count > 0 ? Generalize(ctorType, env) : ctorType;
        env.Define(node.ClassName, generalizedCtor);

        // Field accessors: ClassName/fieldName : ClassType -> FieldType
        for (var i = 0; i < node.Fields.Count; i++)
        {
            var accessorType = new ZType.ZFuncType([classType], fieldTypes[i]);
            var genAccessor = node.TypeParams.Count > 0 ? Generalize(accessorType, env) : accessorType;
            env.Define($"{node.ClassName}/{node.Fields[i].Name}", genAccessor);
        }

        // Also register inherited field accessors under subclass name
        foreach (var (fName, fType) in inheritedFields)
        {
            var accessorType = new ZType.ZFuncType([classType], fType);
            var genAccessor = node.TypeParams.Count > 0 ? Generalize(accessorType, env) : accessorType;
            env.Define($"{node.ClassName}/{fName}", genAccessor);
        }

        // Method accessors: ClassName/methodName : (ClassType, ParamTypes...) -> RetType
        var methodInfos = new List<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)>();

        // Pre-compute every method's parameter types and external return type so that
        // method bodies can reference sibling methods (and themselves) during inference.
        // Async methods' external signature is Task<T> even though the body type is T.
        var methodSignatures = new List<(List<ZType> ParamTypes, ZType ExternalReturnType)>();
        foreach (var method in node.Methods)
        {
            var pTypes = method.Params.Select(p => p.TypeAnnotation ?? FreshVar()).ToList();

            ZType externalRet;
        if (method.IsAsync)
            {
          if (method.ReturnTypeAnnotation is ZType.ZNamedType nt
                    && typeAliasesIsTask(nt.Name))
                    externalRet = method.ReturnTypeAnnotation;
                else if (method.ReturnTypeAnnotation is not null)
                    externalRet = MakeTaskType(method.ReturnTypeAnnotation);
                else
                    externalRet = MakeTaskType(FreshVar());
            }
            else
            {
                externalRet = method.ReturnTypeAnnotation ?? FreshVar();
            }

            methodSignatures.Add((pTypes, externalRet));
        }

        for (var methodIdx = 0; methodIdx < node.Methods.Count; methodIdx++)
        {
            var method = node.Methods[methodIdx];
            var (paramTypes, externalReturnType) = methodSignatures[methodIdx];
            var methodEnv = localEnv.CreateChild();

            // Own fields are in scope within method bodies
            for (var i = 0; i < node.Fields.Count; i++)
                methodEnv.Define(node.Fields[i].Name, fieldTypes[i]);

            // Inherited fields are also in scope
            foreach (var (fName, fType) in inheritedFields)
                methodEnv.Define(fName, fType);

            // Sibling methods (including self) are callable by bare name
            for (var si = 0; si < node.Methods.Count; si++)
            {
                var sib = node.Methods[si];
                var (sibParamTypes, sibReturnType) = methodSignatures[si];
                var sibSigType = new ZType.ZFuncType(sibParamTypes, sibReturnType);
                methodEnv.Define(sib.Name, sibSigType);
            }

            // Inherited methods are also callable by bare name
            foreach (var (mName, mParams, mRet) in inheritedMethods)
                methodEnv.Define(mName, new ZType.ZFuncType(mParams.ToList(), mRet));

            for (var i = 0; i < method.Params.Count; i++)
                methodEnv.Define(method.Params[i].Name, paramTypes[i]);

            // Set class context for super/ calls and set!
            var savedBase = _currentBaseClassName;
            var savedFieldDecls = _currentClassFieldDecls;
            _currentBaseClassName = resolvedBaseClass;
            _currentClassFieldDecls = node.Fields;

            ZType bodyType;
            if (method.IsAsync)
            {
                var prevAsyncContext = _inAsyncContext;
                _inAsyncContext = true;
                bodyType = Infer(method.Body, methodEnv);
                _inAsyncContext = prevAsyncContext;

// For async methods, unwrap Task<T> and unify body with inner type
                if (method.ReturnTypeAnnotation is ZType.ZNamedType { TypeArgs: [var innerT] } taskNt
                    && typeAliasesIsTask(taskNt.Name))
                    _unifier.Unify(bodyType, innerT, method.Body.Span);
                else
                {
                   var isNonGenericTask = method.ReturnTypeAnnotation is ZType.ZNamedType { TypeArgs: [] } ngTask
                         && typeAliasesIsTask(ngTask.Name);
                    if (!isNonGenericTask && method.ReturnTypeAnnotation is not null)
                        _unifier.Unify(bodyType, method.ReturnTypeAnnotation, method.Body.Span);
                }
            }
            else
            {
                bodyType = Infer(method.Body, methodEnv);
                if (method.ReturnTypeAnnotation is not null)
                    _unifier.Unify(bodyType, method.ReturnTypeAnnotation, method.Body.Span);
            }

            _currentBaseClassName = savedBase;
            _currentClassFieldDecls = savedFieldDecls;

            var retType = method.ReturnTypeAnnotation ?? externalReturnType;

            // Register slash-syntax accessor: ClassName/methodName : (ClassType, ParamTypes...) -> RetType
            var allParams = new List<ZType> { classType };
            allParams.AddRange(paramTypes);
            var methodAccessorType = new ZType.ZFuncType(allParams, retType);
            var genMethodAccessor =
                node.TypeParams.Count > 0 ? Generalize(methodAccessorType, env) : methodAccessorType;
            env.Define($"{node.ClassName}/{method.Name}", genMethodAccessor);

            methodInfos.Add((method.Name, paramTypes, retType));
        }

        // Store class info for inheritance resolution by future subclasses
        _classInfos[node.ClassName] = new ClassInfo(
            node.ClassName,
            node.IsOpen,
            resolvedBaseClass,
            effectiveInterfaceNames,
            node.Fields.Select((f, i) => (f.Name, fieldTypes[i])).ToList(),
            methodInfos,
            ctorType);

        return Assign(node, ZType.Unit);
    }

    private List<(string Name, ZType Type)> GetAllInheritedFields(string className)
    {
        var result = new List<(string, ZType)>();
        if (_classInfos.TryGetValue(className, out var info))
        {
            if (info.BaseClassName is not null)
                result.AddRange(GetAllInheritedFields(info.BaseClassName));
            result.AddRange(info.Fields);
        }

        return result;
    }

    private List<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)> GetAllInheritedMethods(
        string className)
    {
        var result = new List<(string, IReadOnlyList<ZType>, ZType)>();
        if (_classInfos.TryGetValue(className, out var info))
        {
            if (info.BaseClassName is not null)
                result.AddRange(GetAllInheritedMethods(info.BaseClassName));
            result.AddRange(info.Methods);
        }

        return result;
    }

    private ZType InferSuperMethodCall(AstNode.SuperMethodCall node, TypeEnv env)
    {
        if (_currentBaseClassName is null)
        {
            Diagnostics.Error("super/ can only be used in a class that extends another class", node.Span);
            return Assign(node, FreshVar());
        }

        var allMethods = GetAllInheritedMethods(_currentBaseClassName);
        var method = allMethods.FirstOrDefault(m => m.Name == node.MethodName);
        if (method == default)
        {
            Diagnostics.Error($"Base class '{_currentBaseClassName}' has no method '{node.MethodName}'", node.Span);
            return Assign(node, FreshVar());
        }

        // Type-check arguments
        for (var i = 0; i < node.Args.Count && i < method.ParamTypes.Count; i++)
        {
            var argType = Infer(node.Args[i], env);
            _unifier.Unify(argType, method.ParamTypes[i], node.Args[i].Span);
        }

        // Infer any remaining args without unification
        for (var i = method.ParamTypes.Count; i < node.Args.Count; i++)
            Infer(node.Args[i], env);

        return Assign(node, method.ReturnType);
    }

    private ZType InferSetField(AstNode.SetField node, TypeEnv env)
    {
        if (_currentClassFieldDecls is null)
        {
            Diagnostics.Error("set! can only be used inside a method body", node.Span);
            return Assign(node, ZType.Unit);
        }

        var fieldDecl = _currentClassFieldDecls.FirstOrDefault(f => f.Name == node.FieldName);
        if (fieldDecl is null)
        {
            Diagnostics.Error($"Unknown field '{node.FieldName}' in set!", node.Span);
            return Assign(node, ZType.Unit);
        }

        if (!fieldDecl.IsMutable)
        {
            Diagnostics.Error(
                $"Cannot set! immutable field '{node.FieldName}'. Mark it with #:mutable to allow mutation", node.Span);
            return Assign(node, ZType.Unit);
        }

        var valType = Infer(node.Value, env);
        _unifier.Unify(valType, fieldDecl.TypeAnnotation, node.Value.Span);

        return Assign(node, ZType.Unit);
    }

    private ZType InferInterfaceDecl(AstNode.InterfaceDecl node, TypeEnv env)
    {
        var typeArgs = new List<ZType>();
        var localEnv = env.CreateChild();

        foreach (var tp in node.TypeParams)
        {
            var tv = FreshVar();
            typeArgs.Add(tv);
            localEnv.Define(tp, tv);
        }

        var ifaceType = new ZType.ZNamedType(node.InterfaceName, typeArgs);

        // Method accessors: InterfaceName/methodName : (InterfaceType, ParamTypes...) -> RetType
        foreach (var method in node.Methods)
        {
            var paramTypes = new List<ZType>();
            foreach (var param in method.Params)
            {
                var pType = param.TypeAnnotation ?? FreshVar();
                paramTypes.Add(ResolveTypeInEnv(pType, localEnv));
            }

            var retType = ResolveTypeInEnv(method.ReturnTypeAnnotation, localEnv);

            var allParams = new List<ZType> { ifaceType };
            allParams.AddRange(paramTypes);
            var methodAccessorType = new ZType.ZFuncType(allParams, retType);
            var genMethodAccessor =
                node.TypeParams.Count > 0 ? Generalize(methodAccessorType, env) : methodAccessorType;
            env.Define($"{node.InterfaceName}/{method.Name}", genMethodAccessor);
        }

        return Assign(node, ZType.Unit);
    }

    private ZType InferClrNew(AstNode.ClrNew node, TypeEnv env)
    {
        // Infer argument types
        var argTypes = new List<ZType>();
        foreach (var arg in node.Args)
            argTypes.Add(Infer(arg, env));

        // Resolve as user-defined record/struct/class constructor first (phase-ordering fix:
        // CLR reflection cannot see types emitted by the current compilation).
        var userCtor = env.Lookup(node.TypeName);
        if (userCtor is not null)
        {
            var instantiated = Instantiate(userCtor);
            var applied = Substitution.Apply(instantiated);
            if (applied is ZType.ZFuncType ft
                && ft.Return is ZType.ZNamedType retNamed
                && retNamed.Name == node.TypeName
                && ft.Params.Count == node.Args.Count)
            {
                for (var i = 0; i < ft.Params.Count; i++)
                    _unifier.Unify(argTypes[i], ft.Params[i], node.Args[i].Span);
                return Assign(node, ft.Return);
            }
        }

        // Resolve type variable annotations in type args (e.g. ^k -> ZTypeVar)
        IReadOnlyList<ZType>? resolvedTypeArgs = null;
        if (_currentTypeVarScope is not null && node.TypeArgs.Count > 0)
            resolvedTypeArgs = node.TypeArgs
                .Select(t => ResolveTypeVarAnnotations(t, _currentTypeVarScope) ?? t).ToList();

        // Resolve the CLR type
        var clr = new ClrInterop(Diagnostics, _assemblySearchPaths, _typeAliases);
        Type? clrType;

        if (node.TypeArgs.Count > 0)
        {
            // Generic: try name as-is, then with backtick arity suffix
            clrType = clr.FindType(node.TypeName)
                      ?? clr.FindType($"{node.TypeName}`{node.TypeArgs.Count}");
            if (clrType is not null && clrType.IsGenericTypeDefinition)
                try
                {
                    var clrTypeArgs = node.TypeArgs
                        .Select(t => IlTypeMapper.MapToClr(t, typeAliases: _typeAliases))
                        .ToArray();
                    clrType = clrType.MakeGenericType(clrTypeArgs);
                }
                catch (Exception ex)
                {
                    Diagnostics.Error(
                        $"Failed to construct generic type '{node.TypeName}': {ex.Message}", node.Span);
                    return Assign(node, FreshVar());
                }
        }
        else
        {
            clrType = clr.FindType(node.TypeName);
        }

        if (clrType is null)
        {
            Diagnostics.Error($"CLR type not found: '{node.TypeName}'", node.Span);
            return Assign(node, FreshVar());
        }

        // Validate a constructor with matching arg count exists
        var ctors = clrType.GetConstructors()
            .Where(c => c.GetParameters().Length == node.Args.Count)
            .ToArray();
        if (ctors.Length == 0)
        {
            Diagnostics.Error(
                $"No constructor on '{node.TypeName}' accepts {node.Args.Count} argument(s)", node.Span);
            return Assign(node, FreshVar());
        }

        // When type args contain type variables, preserve them in the result type
        // (the CLR round-trip through MapClrTypeToZType would lose type vars)
       if (resolvedTypeArgs is not null
            && resolvedTypeArgs.Any(t => Substitution.FreeVars(t).Count > 0))
        {
            var baseZType = clr.MapClrTypeToZType(clrType);
            if (baseZType is ZType.ZNamedType baseNt)
                return Assign(node, new ZType.ZNamedType(baseNt.Name, resolvedTypeArgs.ToList()));
        }

        return Assign(node, clr.MapClrTypeToZType(clrType));
    }

    private ZType InferRaise(AstNode.Raise node, TypeEnv env)
    {
        var exprType = Infer(node.Expr, env);

        // raise requires a System.Exception subclass
        var resolved = Substitution.Apply(exprType);
        if (resolved is ZType.ZNamedType nt && nt.TypeArgs.Count == 0)
        {
       var clrInterop = new ClrInterop(Diagnostics, _assemblySearchPaths, _typeAliases);
            var clrType = clrInterop.FindType(nt.Name);
            if (clrType is not null && !typeof(Exception).IsAssignableFrom(clrType))
                Diagnostics.Error($"'raise' expression must be a System.Exception subclass, got '{nt.Name}'",
                    node.Span);
        }
        else if (resolved is not ZType.ZTypeVar)
        {
            Diagnostics.Error($"'raise' expression must be a System.Exception subclass, got '{resolved}'",
                node.Span);
        }

        // raise never returns, so it can unify with any type
        return Assign(node, FreshVar());
    }

    private ZType InferDefineAsync(AstNode.DefineAsync node, TypeEnv env)
    {
        var childEnv = env.CreateChild();
        var paramTypes = new List<ZType>();
        var typeVarScope = new Dictionary<string, ZType>();
        var isVariadic = node.Params.Count > 0 && node.Params[^1].IsVariadic;

        foreach (var param in node.Params)
        {
            var pType = ResolveTypeVarAnnotations(param.TypeAnnotation, typeVarScope) ?? FreshVar();
            paramTypes.Add(pType);
            if (param.IsVariadic)
                childEnv.Define(param.Name, MakeVariadicType(pType));
            else
                childEnv.Define(param.Name, pType);
            param.ResolvedType = param.IsVariadic
                ? MakeVariadicType(pType)
                : pType;
        }

        // Determine the inner return type (unwrap Task<T> from annotation)
        ZType innerRetType;
var resolvedRetAnnotation = ResolveTypeVarAnnotations(node.ReturnTypeAnnotation, typeVarScope);
      if (resolvedRetAnnotation is ZType.ZNamedType { TypeArgs: [var innerT] } taskNt
            && (typeAliasesIsTask(taskNt.Name)))
            innerRetType = innerT;
      else if (resolvedRetAnnotation is ZType.ZNamedType { TypeArgs: [] } nonGenericTask
                   && (typeAliasesIsTask(nonGenericTask.Name)))
            innerRetType = ZType.Unit;
        else
            innerRetType = resolvedRetAnnotation ?? FreshVar();

        // The full return type is Task<innerRetType>
    var isNonGenericAnn = node.ReturnTypeAnnotation is ZType.ZNamedType { TypeArgs: [] } annNt
            && (typeAliasesIsTask(annNt.Name));
        var taskRetType = innerRetType == ZType.Unit && isNonGenericAnn
            ? MakeTaskType(null)
            : MakeTaskType(innerRetType);

        // For self-recursion, add the function itself to the environment
        var selfType = new ZType.ZFuncType(paramTypes, taskRetType, isVariadic);
        childEnv.Define(node.FnName, selfType);

        var prevAsyncContext = _inAsyncContext;
        var prevTypeVarScope = _currentTypeVarScope;
        _inAsyncContext = true;
        _currentTypeVarScope = typeVarScope;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;
        _currentTypeVarScope = prevTypeVarScope;

        // Unify body type with inner return type (skip for non-generic Task where body is discarded)
        if (!isNonGenericAnn)
            _unifier.Unify(bodyType, innerRetType, node.Span);

        // Resolve the function type with substitutions
        var resolvedFuncType = Substitution.Apply(selfType);
        var generalized = Generalize(resolvedFuncType, env);

        // Register in the outer environment
        env.Define(node.FnName, generalized);
        return Assign(node, resolvedFuncType);
    }

    private ZType InferAwait(AstNode.Await node, TypeEnv env)
    {
        if (!_inAsyncContext)
            Diagnostics.Error("'await' can only be used inside an async function", node.Span);

        var exprType = Infer(node.Expr, env);
        var resolved = Substitution.Apply(exprType);

     if (resolved is ZType.ZNamedType nt && typeAliasesIsTask(nt.Name))
        {
            if (nt.TypeArgs is [var innerType]) return Assign(node, innerType);
            if (nt.TypeArgs is []) return Assign(node, ZType.Unit);
        }

        Diagnostics.Error($"'await' requires a Task expression, got '{resolved}'", node.Span);
        return Assign(node, FreshVar());
    }

     private ZType InferImportClr(AstNode.ImportClr node, TypeEnv env)
    {
        var clr = new ClrInterop(Diagnostics, _assemblySearchPaths, _typeAliases);
        foreach (var import in node.Imports)
        {
            // If an explicit type annotation is provided, use it directly
            if (import.TypeAnnotation is not null)
            {
                var scope = new Dictionary<string, ZType>();
                var resolved = ResolveTypeVarAnnotations(import.TypeAnnotation, scope);
                if (resolved is not null)
                {
                    if (scope.Count > 0)
                    {
                        var varIds = scope.Values
                            .OfType<ZType.ZTypeVar>()
                            .Select(v => v.Id)
                            .ToList();
                        env.Define(import.Alias, new ZType.ZForAllType(varIds, resolved));
                    }
                    else
                    {
                        env.Define(import.Alias, resolved);
                    }
                }

                // For instance and static methods with type annotations, also detect
                // out-params via reflection so callers see the visible (out-stripped)
                // signature exposed by the annotation while emitters still receive the
                // metadata needed to allocate locals and pack the ValueTuple result.
                //
                // Only register out-params when the annotation's return type is a
                // ValueTuple — that's the user-facing signal that they want the
                // out-packed form. Otherwise the user is targeting an overload
                // without out-params (e.g. Dictionary.Remove(TKey) returning Bool,
                // distinct from Remove(TKey, out TValue)).
                if (AnnotationRequestsOutParams(resolved))
                {
                    if (import.Kind == ClrImportKind.Instance)
                    {
                        var outParams = clr.DetectOutParams(import.QualifiedName, import.Span);
                        if (outParams is { Count: > 0 })
                            _outParamsByAlias[import.Alias] = outParams;
                    }
                    else if (import.Kind == ClrImportKind.Static)
                    {
                        var outParams = clr.DetectOutParams(import.QualifiedName, import.Span,
                            BindingFlags.Public | BindingFlags.Static);
                        if (outParams is { Count: > 0 })
                            _outParamsByAlias[import.Alias] = outParams;
                    }
                }

                continue;
            }

            if (import.TypeParams.Count > 0)
            {
                var method = clr.ResolveGeneric(import.QualifiedName, import.TypeParams.Count, import.Span);
                if (method is not null)
                {
                    var varIds = import.TypeParams.Select(_ => _nextTypeVar++).ToList();
                    var funcType = clr.GenericMethodInfoToZFuncType(method, varIds);
                    env.Define(import.Alias, new ZType.ZForAllType(varIds, funcType));
                }
            }
            else
            {
                var method = clr.Resolve(import.QualifiedName, import.Span);
                if (method is not null)
                {
                    var (funcType, outParams) = clr.MethodInfoToZFuncTypeWithOutParams(method);
                    env.Define(import.Alias, funcType);
                    if (outParams.Count > 0)
                        _outParamsByAlias[import.Alias] = outParams;
                }
            }
        }

        return Assign(node, ZType.Unit);
    }

    private bool AnnotationRequestsOutParams(ZType? annotation)
    {
        if (annotation is not ZType.ZFuncType { Return: var ret })
            return false;
    return ret is ZType.ZNamedType { TypeArgs.Count: > 0 } vt && (_typeAliases?.IsValueTupleName(vt.Name) ?? false);
    }

    private ZType? ResolveTypeVarAnnotations(ZType? type, Dictionary<string, ZType> scope)
    {
        if (type is null) return null;
        return type switch
        {
            ZType.ZNamedType { Name: var name, TypeArgs.Count: 0 } when name.StartsWith('^') =>
                scope.TryGetValue(name, out var tv) ? tv : scope[name] = FreshVar(),
            ZType.ZNamedType nt when nt.TypeArgs.Count > 0 =>
                new ZType.ZNamedType(nt.Name,
                    nt.TypeArgs.Select(t => ResolveTypeVarAnnotations(t, scope) ?? t).ToList()),
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => ResolveTypeVarAnnotations(p, scope) ?? p).ToList(),
                    ResolveTypeVarAnnotations(ft.Return, scope) ?? ft.Return),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(ResolveTypeVarAnnotations(nt.Inner, scope) ?? nt.Inner),
            _ => type
        };
    }

    private ZType ResolveTypeInEnv(ZType type, TypeEnv env)
    {
        return type switch
        {
            ZType.ZNamedType { Name: var name, TypeArgs: { Count: 0 } } =>
                env.Lookup(name) ?? type,
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(t => ResolveTypeInEnv(t, env)).ToList()),
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => ResolveTypeInEnv(p, env)).ToList(),
                    ResolveTypeInEnv(ft.Return, env)),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(ResolveTypeInEnv(nt.Inner, env)),
            _ => type
        };
    }

    private ZType Generalize(ZType type, TypeEnv env)
    {
        var resolved = Substitution.Apply(type);
        var freeVars = Substitution.FreeVars(resolved);
        // Subtract type variables that are still free in the surrounding
        // environment — those represent unification variables introduced by
        // an outer scope (e.g. fresh vars from a constructor pattern), and
        // generalizing them here would prevent later unifications from
        // propagating through this binding.
        foreach (var bound in env.AllBoundTypes())
            foreach (var id in Substitution.FreeVars(Substitution.Apply(bound)))
                freeVars.Remove(id);
        if (freeVars.Count == 0)
            return resolved;
        return new ZType.ZForAllType(freeVars.ToList(), resolved);
    }

    // Unify each super-call arg's inferred type with the base class
    // constructor's expected param type. Without this, free type variables
    // in arg expressions (e.g. ^b bound by a `(Right_0 x)` pattern that
    // never constrains ^b) leak past inference and get defaulted to
    // System.Object, after which both backends emit a base(...) call
    // whose argument type mismatches the constructor signature.
    private void UnifySuperArgs(string? baseClassName, IReadOnlyList<AstNode> superArgs,
        IReadOnlyList<ZType> argTypes, TypeEnv env)
    {
        if (baseClassName is null) return;
        var baseCtor = env.Lookup(baseClassName);
        if (baseCtor is null) return;
        var instantiated = Substitution.Apply(Instantiate(baseCtor));
        if (instantiated is not ZType.ZFuncType ft) return;
        if (ft.Params.Count != argTypes.Count) return;
        for (var i = 0; i < argTypes.Count; i++)
            _unifier.Unify(argTypes[i], ft.Params[i], superArgs[i].Span);
    }

    private ZType Instantiate(ZType type)
    {
        if (type is not ZType.ZForAllType forall)
            return type;

        var mapping = new Dictionary<int, ZType>();
        foreach (var bv in forall.BoundVars)
        {
            var constraint = FindConstraint(forall.Body, bv);
            mapping[bv] = constraint is not null
                ? new ZType.ZConstrainedVar(_nextTypeVar++, constraint)
                : FreshVar();
        }

        return InstantiateBody(forall.Body, mapping);
    }

    private static IReadOnlySet<PrimitiveKind>? FindConstraint(ZType type, int varId)
    {
        return type switch
        {
            ZType.ZConstrainedVar cv when cv.Id == varId => cv.AllowedKinds,
            ZType.ZFuncType ft => ft.Params.Select(p => FindConstraint(p, varId))
                .Concat([FindConstraint(ft.Return, varId)])
                .FirstOrDefault(c => c is not null),
            ZType.ZNamedType nt => nt.TypeArgs.Select(a => FindConstraint(a, varId))
                .FirstOrDefault(c => c is not null),
            ZType.ZNullableType nt => FindConstraint(nt.Inner, varId),
            _ => null
        };
    }

    private ZType InstantiateBody(ZType type, Dictionary<int, ZType> mapping)
    {
        return type switch
        {
            ZType.ZConstrainedVar cv =>
                mapping.TryGetValue(cv.Id, out var replacement) ? replacement : cv,
            ZType.ZTypeVar tv =>
                mapping.TryGetValue(tv.Id, out var replacement) ? replacement : tv,
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => InstantiateBody(p, mapping)).ToList(),
                    InstantiateBody(ft.Return, mapping),
                    ft.IsVariadic),
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name,
                    nt.TypeArgs.Select(a => InstantiateBody(a, mapping)).ToList()),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(InstantiateBody(nt.Inner, mapping)),
            _ => type
        };
    }

    private ZType ReportUnknown(AstNode node)
    {
        Diagnostics.Error($"Cannot type-check node: {node.GetType().Name}", node.Span);
        return ZType.Unit;
    }

    private void ResolveParam(Param param)
    {
        if (param.ResolvedType is not null)
            param.ResolvedType = Substitution.Apply(param.ResolvedType);
    }

    /// <summary>
    ///     Resolves all type variables in the entire AST to their final types.
    ///     Call this after inference is complete.
    /// </summary>
    public void Resolve(AstNode node)
    {
        // Use ApplyAndDefault so that any free numeric ZConstrainedVar still
        // hanging around (e.g. arithmetic on a value extracted from a
        // polymorphic union case whose type parameter is otherwise unused)
        // collapses to a concrete primitive type. Without this the codegen
        // type mappers fall through to System.Object and emit IL that fails
        // verification with "expected numeric type, found ref 'object'".
        if (node.ResolvedType is not null)
            node.ResolvedType = Substitution.ApplyAndDefault(node.ResolvedType);

        switch (node)
        {
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms) Resolve(f);
                break;
            case AstNode.ModuleDecl md:
                foreach (var f in md.Body) Resolve(f);
                break;
            case AstNode.Define d:
                foreach (var p in d.Params) ResolveParam(p);
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
                foreach (var p in lam.Params) ResolveParam(p);
                Resolve(lam.Body);
                break;
            case AstNode.Apply app:
                Resolve(app.Function);
                foreach (var a in app.Args) Resolve(a);
                break;
            case AstNode.Name nm when nm.OverloadCandidates is not null && nm.ResolvedQualifiedName is null:
                Diagnostics.Error(
                    $"Cannot use overloaded name '{nm.Value}' as a value; use it in a call site or qualify it (candidates: " +
                    string.Join(", ", nm.OverloadCandidates.QualifiedNames) + ")",
                    nm.Span);
                break;
            case AstNode.Match m:
                Resolve(m.Scrutinee);
                foreach (var arm in m.Arms) Resolve(arm.Body);
                break;
            case AstNode.Partial part:
                Resolve(part.Function);
                foreach (var a in part.Args) Resolve(a);
                break;
            case AstNode.ClrNew cn:
                foreach (var a in cn.Args) Resolve(a);
                break;
            case AstNode.Raise r:
                Resolve(r.Expr);
                break;
            case AstNode.DefineAsync da:
                foreach (var p in da.Params) ResolveParam(p);
                Resolve(da.Body);
                break;
            case AstNode.Await aw:
                Resolve(aw.Expr);
                break;
            case AstNode.ObjectExpr oe:
                foreach (var m in oe.Methods) Resolve(m.Body);
                if (oe.Constructor is { } oeCtor) ResolveConstructor(oeCtor);
                break;
            case AstNode.ClassDecl cd:
                foreach (var m in cd.Methods) Resolve(m.Body);
                if (cd.Constructor is { } cdCtor) ResolveConstructor(cdCtor);
                break;
            case AstNode.WithHandlers wh:
                Resolve(wh.Body);
                foreach (var h in wh.Handlers) Resolve(h.HandlerBody);
                break;
            case AstNode.With w:
                Resolve(w.Record);
                foreach (var (_, valueExpr) in w.Updates) Resolve(valueExpr);
                break;
            case AstNode.TupleNew tn:
                foreach (var elem in tn.Elements) Resolve(elem);
                break;
            case AstNode.SuperMethodCall smc:
                foreach (var a in smc.Args) Resolve(a);
                break;
            case AstNode.SetField sf:
                Resolve(sf.Value);
                break;
            case AstNode.NullLit:
                break;
        }
    }

    private void ResolveConstructor(ConstructorDecl ctor)
    {
        if (ctor.SuperArgs is not null)
            foreach (var a in ctor.SuperArgs)
                Resolve(a);
        foreach (var (_, v) in ctor.FieldSets) Resolve(v);
        foreach (var b in ctor.BodyExprs) Resolve(b);
    }

    private sealed record ClassInfo(
        string Name,
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<(string Name, ZType Type)> Fields,
        IReadOnlyList<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)> Methods,
        ZType ConstructorType);
}

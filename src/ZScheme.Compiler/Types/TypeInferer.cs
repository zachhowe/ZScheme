using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

public sealed class TypeInferer
{
    private readonly IReadOnlyList<string> _assemblySearchPaths;
    private readonly Unifier _unifier;
    private int _nextTypeVar;
    private bool _inAsyncContext;

    // Track class metadata for inheritance resolution
    private readonly Dictionary<string, ClassInfo> _classInfos = new();

    // Track out-param metadata for CLR imports (keyed by alias)
    private readonly Dictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> _outParamsByAlias = new();

    /// <summary>
    ///     Out-param metadata detected during type inference, keyed by import alias.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ClrInterop.OutParamInfo>> OutParamsByAlias => _outParamsByAlias;
    private string? _currentBaseClassName; // set during method body inference for super/ calls
    private IReadOnlyList<FieldDecl>? _currentClassFieldDecls; // set during method body inference for set!

    private sealed record ClassInfo(
        string Name,
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<(string Name, ZType Type)> Fields,
        IReadOnlyList<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)> Methods,
        ZType ConstructorType);

    public TypeInferer(DiagnosticBag diagnostics, IReadOnlyList<string>? assemblySearchPaths = null)
    {
        Diagnostics = diagnostics;
        _unifier = new Unifier(Substitution, diagnostics, assemblySearchPaths);
        _assemblySearchPaths = assemblySearchPaths ?? [];
    }

    public DiagnosticBag Diagnostics { get; }

    public Substitution Substitution { get; } = new();

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
            AstNode.Pipe n => InferPipe(n, env),
            AstNode.Partial n => InferPartial(n, env),
            AstNode.Match n => InferMatch(n, env),
            AstNode.RecordDecl n => InferRecordDecl(n, env),
            AstNode.UnionDecl n => InferUnionDecl(n, env),
            AstNode.Try n => InferTry(n, env),
            AstNode.Propagate n => InferPropagate(n, env),
            AstNode.Catch n => InferCatch(n, env),
            AstNode.ObjectExpr n => InferObjectExpr(n, env),
            AstNode.ClassDecl n => InferClassDecl(n, env),
            AstNode.InterfaceDecl n => InferInterfaceDecl(n, env),
            AstNode.SuperMethodCall n => InferSuperMethodCall(n, env),
            AstNode.SetField n => InferSetField(n, env),
            AstNode.ClrNew n => InferClrNew(n, env),
            AstNode.Raise n => InferRaise(n, env),
            AstNode.DefineAsync n => InferDefineAsync(n, env),
            AstNode.Await n => InferAwait(n, env),
            AstNode.WithHandlers n => InferWithHandlers(n, env),
            AstNode.ImportClr n => InferImportClr(n, env),
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
            // Generalize if the value is not an application (value restriction)
            bindType = Generalize(valueType, env);
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
            // Variadic param is bound as Mutable-Array[T] in the body
            if (param.IsVariadic)
                childEnv.Define(param.Name, new ZType.ZNamedType("Mutable-Array", [pType]));
            else
                childEnv.Define(param.Name, pType);
        }

        var prevAsyncContext = _inAsyncContext;
        _inAsyncContext = false;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;
        var funcType = new ZType.ZFuncType(paramTypes, bodyType, isVariadic);
        return Assign(node, funcType);
    }

    private ZType InferApply(AstNode.Apply node, TypeEnv env)
    {
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
            // Variadic param is bound as Mutable-Array[T] in the body
            if (param.IsVariadic)
                childEnv.Define(param.Name, new ZType.ZNamedType("Mutable-Array", [pType]));
            else
                childEnv.Define(param.Name, pType);
        }

        // For self-recursion, add the function itself to the environment
        var selfRetType = ResolveTypeVarAnnotations(node.ReturnTypeAnnotation, typeVarScope) ?? FreshVar();
        var selfType = new ZType.ZFuncType(paramTypes, selfRetType, isVariadic);
        childEnv.Define(node.FnName, selfType);

        var prevAsyncContext = _inAsyncContext;
        _inAsyncContext = false;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;

        // Unify body type with declared return type
        _unifier.Unify(bodyType, selfRetType, node.Span);

        // Resolve the function type with substitutions
        var resolvedFuncType = Substitution.Apply(selfType);
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
        var last = ZType.Unit;
        foreach (var form in node.TopLevelForms) last = Infer(form, env);
        return Assign(node, last);
    }

    private ZType InferPipe(AstNode.Pipe node, TypeEnv env)
    {
        // (|> x (f a) (g b)) => (g (f x a) b)
        var current = Infer(node.Initial, env);
        node.Initial.ResolvedType = current;

        foreach (var step in node.Steps)
            if (step is AstNode.Apply apply)
            {
                // Insert current as first argument
                var funcType = Infer(apply.Function, env);
                var allArgTypes = new List<ZType> { current };
                foreach (var arg in apply.Args)
                    allArgTypes.Add(Infer(arg, env));

                var retType = FreshVar();
                _unifier.Unify(funcType, new ZType.ZFuncType(allArgTypes, retType), step.Span);
                current = Substitution.Apply(retType);
                step.ResolvedType = current;
            }
            else if (step is AstNode.Name name)
            {
                // Apply as unary function
                var funcType = Infer(name, env);
                var retType = FreshVar();
                _unifier.Unify(funcType, new ZType.ZFuncType([current], retType), step.Span);
                current = Substitution.Apply(retType);
            }
            else
            {
                Diagnostics.Error("Pipe step must be a function application or name", step.Span);
            }

        return Assign(node, current);
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
        return Assign(node, Substitution.Apply(okType));
    }

    private ZType InferCatch(AstNode.Catch node, TypeEnv env)
    {
        var bodyType = Infer(node.Body, env);
        var errorType = new ZType.ZNamedType("ErrorInfo", []);
        var resultType = new ZType.ZNamedType("Result", [bodyType, errorType]);
        return Assign(node, resultType);
    }

    private ZType InferWithHandlers(AstNode.WithHandlers node, TypeEnv env)
    {
        var bodyType = Infer(node.Body, env);
        var clrInterop = new ClrInterop(Diagnostics, _assemblySearchPaths);

        foreach (var handler in node.Handlers)
        {
            // Validate exception type exists and is a System.Exception subclass
            var clrType = clrInterop.FindType(handler.ExceptionTypeName);
            if (clrType is null)
            {
                Diagnostics.Error(
                    $"Exception type '{handler.ExceptionTypeName}' not found",
                    handler.Span);
            }
            else if (!typeof(Exception).IsAssignableFrom(clrType))
            {
                Diagnostics.Error(
                    $"Handler type '{handler.ExceptionTypeName}' must be a System.Exception subclass",
                    handler.Span);
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

    private ZType InferObjectExpr(AstNode.ObjectExpr node, TypeEnv env)
    {
        // Validate base class if present
        string? resolvedBaseClass = node.BaseClassName;
        if (resolvedBaseClass is not null)
        {
            if (_classInfos.TryGetValue(resolvedBaseClass, out var baseInfo))
            {
                if (!baseInfo.IsOpen)
                    Diagnostics.Error(
                        $"Cannot inherit from sealed class '{resolvedBaseClass}'. Mark it with :open to allow subclassing",
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
                foreach (var arg in ctor.SuperArgs)
                    Infer(arg, ctorEnv);

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

            Infer(method.Body, methodEnv);
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
        string? resolvedBaseClass = node.BaseClassName;
        var inheritedFields = new List<(string Name, ZType Type)>();
        var inheritedMethods = new List<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)>();

        if (resolvedBaseClass is not null)
        {
            // Validate base class exists and is open
            if (_classInfos.TryGetValue(resolvedBaseClass, out var baseInfo))
            {
                if (!baseInfo.IsOpen)
                    Diagnostics.Error($"Cannot inherit from sealed class '{resolvedBaseClass}'. Mark it with :open to allow subclassing", node.Span);

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
                // Base class name might actually be an interface (position-based heuristic)
                // If it's not a known class, treat it as an interface instead
                resolvedBaseClass = null;
                // Re-add it to interface names — this is handled in the AST, but we fix up here
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
                // Infer each super arg
                foreach (var arg in ctor.SuperArgs)
                    Infer(arg, ctorEnv);
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
        foreach (var method in node.Methods)
        {
            var methodEnv = localEnv.CreateChild();

            // Own fields are in scope within method bodies
            for (var i = 0; i < node.Fields.Count; i++)
                methodEnv.Define(node.Fields[i].Name, fieldTypes[i]);

            // Inherited fields are also in scope
            foreach (var (fName, fType) in inheritedFields)
                methodEnv.Define(fName, fType);

            var paramTypes = new List<ZType>();
            foreach (var param in method.Params)
            {
                var pType = param.TypeAnnotation ?? FreshVar();
                paramTypes.Add(pType);
                methodEnv.Define(param.Name, pType);
            }

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
                if (method.ReturnTypeAnnotation is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var innerT] })
                    _unifier.Unify(bodyType, innerT, method.Body.Span);
                else if (method.ReturnTypeAnnotation is not (ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] }))
                    if (method.ReturnTypeAnnotation is not null)
                        _unifier.Unify(bodyType, method.ReturnTypeAnnotation, method.Body.Span);
            }
            else
            {
                bodyType = Infer(method.Body, methodEnv);
                if (method.ReturnTypeAnnotation is not null)
                    _unifier.Unify(bodyType, method.ReturnTypeAnnotation, method.Body.Span);
            }

            _currentBaseClassName = savedBase;
            _currentClassFieldDecls = savedFieldDecls;

            var retType = method.ReturnTypeAnnotation ?? bodyType;

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

    private List<(string Name, IReadOnlyList<ZType> ParamTypes, ZType ReturnType)> GetAllInheritedMethods(string className)
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
            Diagnostics.Error($"Cannot set! immutable field '{node.FieldName}'. Mark it with :mutable to allow mutation", node.Span);
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
        foreach (var arg in node.Args)
            Infer(arg, env);

        // Resolve the CLR type
        var clr = new ClrInterop(Diagnostics, _assemblySearchPaths);
        Type? clrType;

        if (node.TypeArgs.Count > 0)
        {
            // Generic: try name as-is, then with backtick arity suffix
            clrType = clr.FindType(node.TypeName)
                      ?? clr.FindType($"{node.TypeName}`{node.TypeArgs.Count}");
            if (clrType is not null && clrType.IsGenericTypeDefinition)
            {
                try
                {
                    var clrTypeArgs = node.TypeArgs
                        .Select(IlTypeMapper.MapToClr)
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

        return Assign(node, ClrInterop.MapClrTypeToZType(clrType));
    }

    private ZType InferRaise(AstNode.Raise node, TypeEnv env)
    {
        var exprType = Infer(node.Expr, env);

        // raise requires a System.Exception subclass
        var resolved = Substitution.Apply(exprType);
        if (resolved is ZType.ZNamedType nt && nt.TypeArgs.Count == 0)
        {
            var clrInterop = new ClrInterop(Diagnostics, _assemblySearchPaths);
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
                childEnv.Define(param.Name, new ZType.ZNamedType("Mutable-Array", [pType]));
            else
                childEnv.Define(param.Name, pType);
        }

        // Determine the inner return type (unwrap Task<T> from annotation)
        ZType innerRetType;
        var resolvedRetAnnotation = ResolveTypeVarAnnotations(node.ReturnTypeAnnotation, typeVarScope);
        if (resolvedRetAnnotation is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var innerT] })
            innerRetType = innerT;
        else if (resolvedRetAnnotation is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] })
            innerRetType = ZType.Unit;
        else
            innerRetType = resolvedRetAnnotation ?? FreshVar();

        // The full return type is Task<innerRetType>
        var taskRetType = innerRetType == ZType.Unit &&
                          node.ReturnTypeAnnotation is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] }
            ? new ZType.ZNamedType("Task", [])
            : new ZType.ZNamedType("Task", [innerRetType]);

        // For self-recursion, add the function itself to the environment
        var selfType = new ZType.ZFuncType(paramTypes, taskRetType, isVariadic);
        childEnv.Define(node.FnName, selfType);

        var prevAsyncContext = _inAsyncContext;
        _inAsyncContext = true;
        var bodyType = Infer(node.Body, childEnv);
        _inAsyncContext = prevAsyncContext;

        // Unify body type with inner return type (skip for non-generic Task where body is discarded)
        var isNonGenericTask = node.ReturnTypeAnnotation is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] };
        if (!isNonGenericTask)
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

        if (resolved is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var innerType] }) return Assign(node, innerType);

        if (resolved is ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] }) return Assign(node, ZType.Unit);

        Diagnostics.Error($"'await' requires a Task expression, got '{resolved}'", node.Span);
        return Assign(node, FreshVar());
    }

    private ZType InferImportClr(AstNode.ImportClr node, TypeEnv env)
    {
        var clr = new ClrInterop(Diagnostics, _assemblySearchPaths);
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

                continue;
            }

            if (import.TypeParams.Count > 0)
            {
                var method = clr.ResolveGeneric(import.QualifiedName, import.TypeParams.Count, import.Span);
                if (method is not null)
                {
                    var varIds = import.TypeParams.Select(_ => _nextTypeVar++).ToList();
                    var funcType = ClrInterop.GenericMethodInfoToZFuncType(method, varIds);
                    env.Define(import.Alias, new ZType.ZForAllType(varIds, funcType));
                }
            }
            else
            {
                var method = clr.Resolve(import.QualifiedName, import.Span);
                if (method is not null)
                {
                    var (funcType, outParams) = ClrInterop.MethodInfoToZFuncTypeWithOutParams(method);
                    env.Define(import.Alias, funcType);
                    if (outParams.Count > 0)
                        _outParamsByAlias[import.Alias] = outParams;
                }
            }
        }

        return Assign(node, ZType.Unit);
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

    /// <summary>
    ///     Resolves all type variables in the entire AST to their final types.
    ///     Call this after inference is complete.
    /// </summary>
    public void Resolve(AstNode node)
    {
        if (node.ResolvedType is not null)
            node.ResolvedType = Substitution.Apply(node.ResolvedType);

        switch (node)
        {
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms) Resolve(f);
                break;
            case AstNode.ModuleDecl md:
                foreach (var f in md.Body) Resolve(f);
                break;
            case AstNode.Define d:
                foreach (var _ in d.Params)
                {
                }

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
            case AstNode.ClrNew cn:
                foreach (var a in cn.Args) Resolve(a);
                break;
            case AstNode.Raise r:
                Resolve(r.Expr);
                break;
            case AstNode.DefineAsync da:
                Resolve(da.Body);
                break;
            case AstNode.Await aw:
                Resolve(aw.Expr);
                break;
            case AstNode.ObjectExpr oe:
                foreach (var m in oe.Methods) Resolve(m.Body);
                break;
            case AstNode.ClassDecl cd:
                foreach (var m in cd.Methods) Resolve(m.Body);
                break;
            case AstNode.NullLit:
                break;
        }
    }
}

using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Eliminates <see cref="IrNode.LetRec" /> by lambda-lifting the group's function bindings
///     to top-level static functions. Runs as the <b>first</b> IR sub-pass, so no other pass and
///     neither backend ever sees a <see cref="IrNode.LetRec" /> — which matters because the IR
///     has no shared visitor and most passes' switches fall through silently rather than failing
///     loudly on an unknown node.
///     <para>
///         Lambda-lifting is what makes mutual recursion expressible at all. Strict by-value
///         capture cannot: <c>f</c> would have to capture <c>g</c> while <c>g</c> captures
///         <c>f</c>. Passing captures as leading parameters and turning intra-group references
///         into direct calls on stable top-level names breaks that knot. It also buys TCO for
///         free — a lifted self-call is a call to the function's own name, which is exactly what
///         <see cref="TailCallLowering" /> rewrites into a loop.
///     </para>
///     <para>
///         Each function binding <c>f</c> becomes <c>__letrec_{id}_f(captures…, params…)</c>.
///         The substitution is live both inside a lifted body and at the original site, so a
///         reference to a group member becomes a direct call to the lifted name with its
///         captures prepended — no delegate is allocated for a member that is only called. Only
///         a member used in <em>value</em> position becomes an <see cref="IrNode.Closure" />.
///         Non-function bindings stay ordinary <c>let</c>s in source order.
///     </para>
///     <para>
///         A lifted function is generic whenever its signature still mentions type variables —
///         because the group sits inside a generic function, or because the binding was
///         generalized locally. Its type parameters are named by
///         <see cref="IrLowering.ExtractFuncTypeParams" />, and both backends already emit
///         explicit type arguments at the call site. The one shape that cannot be expressed is
///         such a member in value position: <see cref="IrNode.Closure" /> has nowhere to carry
///         type arguments, so that stays an error.
///     </para>
///     <para>
///         Capture sets are per-binding and closed transitively over the group's call graph:
///         <c>captures(f) = freeVars(f) ∪ ⋃ captures(g)</c> for every sibling <c>g</c> that
///         <c>f</c> mentions. A mutually-recursive cycle therefore ends up sharing one capture
///         set, while an <c>f</c> that merely calls <c>g</c> does not inherit unrelated
///         captures — which keeps the site's emission order acyclic.
///     </para>
/// </summary>
public sealed class LetrecLifter(DiagnosticBag diagnostics, string? modulePrefix = null)
{
    private readonly List<IrNode.FuncDef> _liftedFunctions = [];

    /// <summary>Every class in the program by name, so a method's instance state can include
    ///     the fields it inherits. Populated by <see cref="Lift" /> before rewriting.</summary>
    private readonly Dictionary<string, IrNode.ClassDecl> _classes = new(StringComparer.Ordinal);
    private int _groupId;

    /// <summary>The top-level static functions produced by lifting, in creation order. The
    ///     wiring step splices these into the program's top-level <see cref="IrNode.Seq" />.</summary>
    public IReadOnlyList<IrNode.FuncDef> LiftedFunctions => _liftedFunctions;

    public IrNode Lift(IrNode node)
    {
        RegisterClasses(node);
        return Rewrite(
            node,
            new Scope(
                [],
                new Dictionary<string, GroupRef>(),
                [],
                new Dictionary<int, GenericConstraintKind>()
            )
        );
    }

    private void RegisterClasses(IrNode node)
    {
        switch (node)
        {
            case IrNode.ClassDecl cd:
                _classes[cd.Name] = cd;
                break;
            case IrNode.Seq seq:
                foreach (var child in seq.Nodes)
                    RegisterClasses(child);
                break;
        }
    }

    /// <summary>The field names a method body can reach by bare name, following the base-class
    ///     chain. A lifted function is a top-level static, so it has no <c>this</c> to read
    ///     them through.</summary>
    private HashSet<string> InstanceState(IrNode.ClassDecl cd)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var current = cd;
        var guard = 0;
        while (current is not null && guard++ < 64)
        {
            foreach (var field in current.Fields)
                names.Add(field.Name);
            current =
                current.BaseClassName is not null
                && _classes.TryGetValue(current.BaseClassName, out var parent)
                    ? parent
                    : null;
        }

        return names;
    }

    /// <summary>
    ///     <paramref name="scope" /> carries three things down the tree: the names bound by
    ///     enclosing <b>local</b> binders (so the group knows which free variables are captures
    ///     rather than globals), the substitutions that are live inside a lifted body or at a
    ///     group's site (so a member reference becomes a direct call), and the enclosing generic
    ///     function's constraints keyed by type-var ID (so a lifted function can restate them).
    ///     Every binder extends the first and drops shadowed names from the second.
    /// </summary>
    private IrNode Rewrite(IrNode node, Scope scope)
    {
        switch (node)
        {
            case IrNode.LetRec letrec:
                return LiftGroup(letrec, scope);

            // A sibling call inside a lifted body: retarget it at the lifted function and
            // prepend that function's captures. Must precede the general Call case.
            case IrNode.Call { Function: IrNode.Var callee } call
                when scope.Substitutions.TryGetValue(callee.Name, out var target):
                return new IrNode.Call(
                    // Stands in for the original callee reference, so it keeps that
                    // reference's span rather than defaulting to SourceSpan.None.
                    new IrNode.Var(target.LiftedName)
                    {
                        Type = target.LiftedType,
                        Span = callee.Span,
                    },
                    [.. target.CaptureArgs, .. call.Args.Select(a => Rewrite(a, scope))]
                )
                {
                    Type = call.Type,
                    Span = call.Span,
                };

            // A group member used as a value: rebuild its closure from the captures, which are
            // in scope as same-named locals at the site and as parameters inside a lifted body.
            case IrNode.Var v when scope.Substitutions.TryGetValue(v.Name, out var target):
                // A generic lifted function cannot become a delegate: IrNode.Closure has no slot
                // for type arguments, so the IL backend would emit a bare `call` against a
                // generic MethodDefinition (invalid IL) and the C# backend would emit a lambda
                // whose inner call has no inferable type arguments. Direct calls are unaffected —
                // both backends instantiate those explicitly.
                if (Substitution.FreeVars(target.LiftedType).Count > 0)
                    diagnostics.Error(
                        $"'{v.Name}' is a recursive local function whose type mentions type "
                            + "variables, and it is used here as a value. Such a function is lifted "
                            + "to a generic top-level static function, which cannot be turned into "
                            + "a delegate. Call it directly instead, or move it to a top-level "
                            + "'define'",
                        v.Span
                    );
                return new IrNode.Closure(target.LiftedName, target.CaptureArgs)
                {
                    Type = v.Type,
                    Span = v.Span,
                };

            case IrNode.FuncDef func:
            {
                var funcScope = scope.Bind(func.Params.Select(p => p.Name));
                // Only a *generic* function introduces type parameters a nested group may need to
                // restate. A plain nested lambda has none of its own, so it must inherit the
                // enclosing constraints rather than clear them.
                if (func.TypeParams is { Count: > 0 })
                    funcScope = funcScope with { ConstraintsByVarId = ConstraintsByVarId(func) };
                return func with { Body = Rewrite(func.Body, funcScope) };
            }

            case IrNode.Closure closure:
                return new IrNode.Closure(
                    closure.LiftedFuncName,
                    closure.CapturedValues.Select(v => Rewrite(v, scope)).ToList()
                )
                {
                    Type = closure.Type,
                    Span = closure.Span,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(n => Rewrite(n, scope)).ToList())
                {
                    Type = seq.Type,
                    Span = seq.Span,
                };

            case IrNode.Let let:
                return new IrNode.Let(
                    let.VarName,
                    Rewrite(let.Value, scope),
                    Rewrite(let.Body, scope.Bind(let.VarName)),
                    let.VarType,
                    let.EmitName
                )
                {
                    Type = let.Type,
                    Span = let.Span,
                };

            case IrNode.Use use:
                return new IrNode.Use(
                    use.VarName,
                    Rewrite(use.Value, scope),
                    Rewrite(use.Body, scope.Bind(use.VarName)),
                    use.VarType
                )
                {
                    Type = use.Type,
                    Span = use.Span,
                };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition, scope),
                    Rewrite(ifNode.Then, scope),
                    Rewrite(ifNode.Else, scope)
                )
                {
                    Type = ifNode.Type,
                    Span = ifNode.Span,
                };

            case IrNode.Call call:
                return new IrNode.Call(
                    Rewrite(call.Function, scope),
                    call.Args.Select(a => Rewrite(a, scope)).ToList()
                )
                {
                    Type = call.Type,
                    Span = call.Span,
                };

            case IrNode.BinOp binop:
                return new IrNode.BinOp(
                    binop.Op,
                    Rewrite(binop.Left, scope),
                    Rewrite(binop.Right, scope)
                )
                {
                    Type = binop.Type,
                    Span = binop.Span,
                };

            case IrNode.UnaryOp unary:
                return new IrNode.UnaryOp(unary.Op, Rewrite(unary.Operand, scope))
                {
                    Type = unary.Type,
                    Span = unary.Span,
                };

            case IrNode.Match match:
                return new IrNode.Match(
                    Rewrite(match.Scrutinee, scope),
                    match
                        .Arms.Select(a => new IrMatchArm(
                            a.Pattern,
                            Rewrite(a.Body, scope.Bind(a.Pattern.BoundNames()))
                        ))
                        .ToList()
                )
                {
                    Type = match.Type,
                    Span = match.Span,
                };

            case IrNode.MethodCall mc:
                return mc with
                {
                    Receiver = Rewrite(mc.Receiver, scope),
                    Args = mc.Args.Select(a => Rewrite(a, scope)).ToList(),
                };

            case IrNode.ClrNew cn:
                return new IrNode.ClrNew(
                    cn.QualifiedTypeName,
                    cn.TypeArgs,
                    cn.Args.Select(a => Rewrite(a, scope)).ToList()
                )
                {
                    Type = cn.Type,
                    Span = cn.Span,
                };

            case IrNode.ClrCall cc:
                return new IrNode.ClrCall(
                    cc.QualifiedTypeName,
                    cc.MethodName,
                    cc.Args.Select(a => Rewrite(a, scope)).ToList(),
                    cc.GenericArity,
                    cc.GenericTypeArgs,
                    cc.OutParams,
                    cc.ResolvedMethodInfo
                )
                {
                    Type = cc.Type,
                    Span = cc.Span,
                };

            case IrNode.TupleNew tn:
                return new IrNode.TupleNew(tn.Elements.Select(e => Rewrite(e, scope)).ToList())
                {
                    Type = tn.Type,
                    Span = tn.Span,
                };

            case IrNode.UnionCaseNew ucn:
                return new IrNode.UnionCaseNew(
                    ucn.UnionName,
                    ucn.CaseName,
                    ucn.Args.Select(a => Rewrite(a, scope)).ToList()
                )
                {
                    Type = ucn.Type,
                    Span = ucn.Span,
                };

            case IrNode.RecordNew rn:
                return new IrNode.RecordNew(
                    rn.TypeName,
                    rn.Fields.Select(f => (f.FieldName, Rewrite(f.Value, scope))).ToList()
                )
                {
                    Type = rn.Type,
                    Span = rn.Span,
                };

            case IrNode.RecordWith rw:
                return new IrNode.RecordWith(
                    rw.TypeName,
                    Rewrite(rw.Record, scope),
                    rw.Updates.Select(u => (u.FieldName, Rewrite(u.Value, scope))).ToList()
                )
                {
                    Type = rw.Type,
                    Span = rw.Span,
                };

            case IrNode.MutableArrayNew man:
                return new IrNode.MutableArrayNew(
                    man.ElementType,
                    man.Elements.Select(e => Rewrite(e, scope)).ToList()
                )
                {
                    Type = man.Type,
                    Span = man.Span,
                };

            case IrNode.FieldGet fg:
                return new IrNode.FieldGet(Rewrite(fg.Record, scope), fg.FieldName)
                {
                    Type = fg.Type,
                    Span = fg.Span,
                };

            case IrNode.Throw thr:
                return new IrNode.Throw(Rewrite(thr.Expr, scope))
                {
                    Type = thr.Type,
                    Span = thr.Span,
                };

            case IrNode.Await aw:
                return new IrNode.Await(Rewrite(aw.Expr, scope)) { Type = aw.Type, Span = aw.Span };

            case IrNode.SetField sf:
                return new IrNode.SetField(sf.FieldName, Rewrite(sf.Value, scope))
                {
                    Type = sf.Type,
                    Span = sf.Span,
                };

            case IrNode.SuperMethodCall smc:
                return new IrNode.SuperMethodCall(
                    smc.MethodName,
                    smc.Args.Select(a => Rewrite(a, scope)).ToList()
                )
                {
                    Type = smc.Type,
                    Span = smc.Span,
                };

            case IrNode.WithHandlers wh:
                return new IrNode.WithHandlers(
                    Rewrite(wh.Body, scope),
                    wh.Handlers.Select(h => new IrHandlerClause(
                            h.ExceptionTypeName,
                            h.BindingVarName,
                            Rewrite(h.HandlerBody, scope.Bind(h.BindingVarName))
                        ))
                        .ToList()
                )
                {
                    Type = wh.Type,
                    Span = wh.Span,
                };

            case IrNode.ClassDecl cd:
                // Unlike ClosureConverter, class bodies are not skipped: a letrec inside a
                // method must still be eliminated, or it would reach codegen as an unknown
                // node. The fields carried down are what the pass checks a group against —
                // a lifted static function cannot read them.
                var fields = InstanceState(cd);
                return cd with
                {
                    Methods =
                    [
                        .. cd.Methods.Select(m =>
                            m with
                            {
                                Body = Rewrite(
                                    m.Body,
                                    scope.Bind(m.Params.Select(p => p.Name)) with
                                    {
                                        InstanceState = fields,
                                    }
                                ),
                            }
                        ),
                    ],
                    Constructor = RewriteConstructor(cd.Constructor, scope, fields),
                };

            default:
                // Leaves (literals, Var, TypeOf) and childless declaration nodes. ObjectExpr is
                // lifted to ClassDecl by ObjectLifter and TcoJump is introduced by
                // TailCallLowering — both after this pass — so neither reaches here.
                return node;
        }
    }

    /// <summary>
    ///     A constructor's super-arguments, field initializers and body are all ordinary
    ///     expressions that can contain a group, so they need the same rewrite as a method body.
    ///     Super-arguments are evaluated before the instance exists, but they are checked
    ///     against the instance state anyway — a field read there is already invalid, and the
    ///     lifter is not the right place to relitigate it.
    /// </summary>
    private IrConstructor? RewriteConstructor(
        IrConstructor? constructor,
        Scope scope,
        HashSet<string> fields
    )
    {
        if (constructor is null)
            return null;

        var ctorScope = scope.Bind(constructor.Params.Select(p => p.Name)) with
        {
            InstanceState = fields,
        };
        return constructor with
        {
            SuperArgs = constructor.SuperArgs is null
                ? null
                : [.. constructor.SuperArgs.Select(a => Rewrite(a, ctorScope))],
            FieldSets =
            [
                .. constructor.FieldSets.Select(f => (f.FieldName, Rewrite(f.Value, ctorScope))),
            ],
            BodyExprs = [.. constructor.BodyExprs.Select(e => Rewrite(e, ctorScope))],
        };
    }

    /// <summary>
    ///     Lifts one group. The site keeps a <c>let</c> only for the non-function bindings; every
    ///     function binding is reached through the substitution instead, so a member that is only
    ///     ever called costs nothing at the site.
    ///     <para>
    ///         A member used in value position materializes an <see cref="IrNode.Closure" /> right
    ///         where it appears, which is safe because <c>LetrecInitializationChecker</c> has
    ///         already rejected any group where a non-function binding could transitively reach a
    ///         later one — so every capture is bound by the time such a closure is built.
    ///     </para>
    /// </summary>
    private IrNode LiftGroup(IrNode.LetRec letrec, Scope scope)
    {
        var functionNames = letrec
            .Bindings.Where(b => b.Value is IrNode.FuncDef)
            .Select(b => b.Name)
            .ToHashSet();
        var valueNames = letrec
            .Bindings.Where(b => b.Value is not IrNode.FuncDef)
            .Select(b => b.Name)
            .ToHashSet();

        // Inside the group every name is in scope, and any same-named substitution from an
        // enclosing lifted body is shadowed by it. Used by the two paths below that lift nothing
        // and so leave every binding as a real local.
        var siteScope = scope.Bind(letrec.Bindings.Select(b => b.Name));

        if (functionNames.Count == 0)
            return BuildSpine(
                letrec.Bindings.Select(b => (b.Name, Rewrite(b.Value, siteScope), b.VarType)),
                Rewrite(letrec.Body, siteScope),
                letrec
            );

        var captures = ComputeCaptures(letrec, functionNames, valueNames, scope);

        var unliftable = Unliftable(letrec, functionNames, scope);
        if (unliftable is not null)
        {
            diagnostics.Error(unliftable, letrec.Span);
            // Emit a plain (non-recursive) spine so the rest of lowering has a well-formed
            // tree to walk; the error above already fails the compilation.
            return BuildSpine(
                letrec.Bindings.Select(b => (b.Name, Rewrite(b.Value, siteScope), b.VarType)),
                Rewrite(letrec.Body, siteScope),
                letrec
            );
        }

        var groupId = _groupId++;
        var lifted = new Dictionary<string, GroupRef>(StringComparer.Ordinal);
        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef func)
                continue;

            var captureVars = captures[binding.Name];
            lifted[binding.Name] = new GroupRef(
                LiftedName(groupId, binding.Name),
                // Inside a lifted body the captures are parameters, so a sibling's closure is
                // rebuilt from same-named locals.
                // captureVars are the Var nodes ComputeCaptures collected from the body, so
                // each already carries a real span — keep it on the rebuilt reference.
                [
                    .. captureVars.Select(v => new IrNode.Var(v.Name)
                    {
                        Type = v.Type,
                        Span = v.Span,
                    }),
                ],
                new ZType.ZFuncType(
                    [.. captureVars.Select(v => v.Type), .. func.Params.Select(p => p.Type)],
                    func.ReturnType
                )
            );
        }

        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef func)
                continue;

            var target = lifted[binding.Name];
            var captureParams = captures[binding.Name]
                .Select(v => new IrParam(v.Name, v.Type))
                .ToList();
            // The lifted function is top-level, so it has no instance context — clearing it
            // also stops a nested group from being blamed for the enclosing class's fields.
            // The enclosing substitutions are kept so a nested group can still reach an outer
            // group's members; the constraints are kept because the same type-var IDs survive.
            var bodyScope = new Scope(
                ClosureConverter.Extend(
                    scope.Locals,
                    captureParams.Concat(func.Params).Select(p => p.Name)
                ),
                Shadow(Merge(scope.Substitutions, lifted), func.Params.Select(p => p.Name)),
                [],
                scope.ConstraintsByVarId
            );

            var typeParams = IrLowering.ExtractFuncTypeParams(target.LiftedType);
            _liftedFunctions.Add(
                func with
                {
                    Name = target.LiftedName,
                    Params = [.. captureParams, .. func.Params],
                    Body = Rewrite(func.Body, bodyScope),
                    // The lifted function is an ordinary static function typed by its own
                    // signature; the delegate type stays on the Closure node at the site.
                    ClrDelegateTypeName = null,
                    IsSelfRecursive = false,
                    Type = target.LiftedType,
                    // Recomputed, never inherited: the lifted signature has the captures
                    // prepended, so its free type vars are generally not the lambda's.
                    TypeParams = typeParams.Count > 0 ? typeParams : null,
                    TypeParamConstraints = RemapLiftedConstraints(
                        target.LiftedType,
                        scope.ConstraintsByVarId
                    ),
                }
            );
        }

        // With the substitutions live at the site, a call to a member is rewritten into a direct
        // call to the lifted name, so only the non-function bindings need a let. The function
        // names must therefore be *removed* from Locals rather than added: they no longer denote
        // anything a nested group could capture, and leaving them there would make one try.
        var siteLocals = new HashSet<string>(scope.Locals, StringComparer.Ordinal);
        siteLocals.ExceptWith(functionNames);
        siteLocals.UnionWith(valueNames);
        var siteSubst = new Scope(
            siteLocals,
            // The group's own function names win over an enclosing group's; its value names are
            // ordinary locals and so must drop any enclosing substitution of the same name.
            Merge(Shadow(scope.Substitutions, valueNames), lifted),
            scope.InstanceState,
            scope.ConstraintsByVarId
        );
        return BuildSpine(
            letrec
                .Bindings.Where(b => b.Value is not IrNode.FuncDef)
                .Select(b => (b.Name, Rewrite(b.Value, siteSubst), b.VarType)),
            Rewrite(letrec.Body, siteSubst),
            letrec
        );
    }

    /// <summary>
    ///     The enclosing generic function's constraints, re-keyed from its <c>T{i}</c> names to
    ///     type-var IDs. The ID is the only identity that survives lifting: the lifted function
    ///     renames the same variables to its own <c>T{j}</c>, at different indices, because its
    ///     signature generally mentions a different subset of them.
    /// </summary>
    private static IReadOnlyDictionary<int, GenericConstraintKind> ConstraintsByVarId(
        IrNode.FuncDef func
    )
    {
        if (
            func.TypeParamConstraints is not { Count: > 0 } constraints
            || func.TypeParams is not { Count: > 0 } typeParams
            || func.Type is not ZType.ZFuncType ft
        )
            return new Dictionary<int, GenericConstraintKind>();

        // Mirrors IrLowering.ExtractFuncTypeParams: T{i} is the i-th smallest free type-var ID.
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        var byVarId = new Dictionary<int, GenericConstraintKind>();
        for (var i = 0; i < freeVars.Count && i < typeParams.Count; i++)
            if (constraints.TryGetValue(typeParams[i], out var kind))
                byVarId[freeVars[i]] = kind;
        return byVarId;
    }

    /// <summary>The other half of <see cref="ConstraintsByVarId" />: type-var IDs back to the
    ///     lifted function's own <c>T{j}</c> names. Only the variables its signature actually
    ///     mentions get an entry.</summary>
    private static IReadOnlyDictionary<string, GenericConstraintKind>? RemapLiftedConstraints(
        ZType liftedType,
        IReadOnlyDictionary<int, GenericConstraintKind> byVarId
    )
    {
        if (byVarId.Count == 0)
            return null;

        var freeVars = Substitution.FreeVars(liftedType).OrderBy(id => id).ToList();
        var remapped = new Dictionary<string, GenericConstraintKind>();
        for (var j = 0; j < freeVars.Count; j++)
            if (byVarId.TryGetValue(freeVars[j], out var kind))
                remapped[$"T{j}"] = kind;
        return remapped.Count > 0 ? remapped : null;
    }

    /// <summary>
    ///     <c>captures(f) = freeVars(f) ∪ ⋃ captures(g)</c> over the siblings <c>f</c> mentions,
    ///     as a least fixpoint. The transitive step is what lets a lifted body rebuild a
    ///     sibling's closure: it is guaranteed to already hold everything that sibling needs.
    /// </summary>
    private static Dictionary<string, List<IrNode.Var>> ComputeCaptures(
        IrNode.LetRec letrec,
        HashSet<string> functionNames,
        HashSet<string> valueNames,
        Scope scope
    )
    {
        var direct = new Dictionary<string, List<IrNode.Var>>(StringComparer.Ordinal);
        var siblings = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef)
                continue;

            // Free variables that are enclosing locals, or non-function members of this group,
            // become captures. Names bound at top level (globals, module functions) are left as
            // free references and resolve there, matching ClosureConverter.
            direct[binding.Name] =
            [
                .. ClosureConverter
                    .CollectFreeVars(binding.Value, functionNames)
                    .SelectMany(v => ThroughSubstitution(v, scope))
                    .Where(v => scope.Locals.Contains(v.Name) || valueNames.Contains(v.Name))
                    .DistinctBy(v => v.Name, StringComparer.Ordinal),
            ];
            siblings[binding.Name] =
            [
                .. ClosureConverter
                    .CollectFreeVars(binding.Value, [])
                    .Select(v => v.Name)
                    .Where(functionNames.Contains),
            ];
        }

        var result = direct.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList(),
            StringComparer.Ordinal
        );

        bool changed;
        do
        {
            changed = false;
            foreach (var name in direct.Keys)
            foreach (var sibling in siblings[name])
            foreach (var capture in result[sibling])
                if (result[name].All(existing => existing.Name != capture.Name))
                {
                    result[name].Add(capture);
                    changed = true;
                }
        } while (changed);

        return result;
    }

    /// <summary>
    ///     What a free variable actually costs this function in captures. A reference to an
    ///     enclosing group's member is not a capture of itself — the member no longer exists as a
    ///     value, and <see cref="Rewrite" /> replaces the reference with a direct call to (or a
    ///     closure over) its lifted function, passing <em>its</em> captures along. Those captures
    ///     therefore have to be reachable from inside this function, so they are what gets
    ///     captured. Anything else stands for itself.
    /// </summary>
    private static IEnumerable<IrNode.Var> ThroughSubstitution(IrNode.Var v, Scope scope)
    {
        return scope.Substitutions.TryGetValue(v.Name, out var target)
            ? target.CaptureArgs.OfType<IrNode.Var>()
            : [v];
    }

    /// <summary>
    ///     The reason this group cannot be lifted, or null when it can. A lifted top-level static
    ///     function has no <c>this</c>, and unlike a plain lambda — which can fall back to the
    ///     backends' own lambda paths — a recursive group has no fallback, so this is an error
    ///     rather than a quiet opt-out.
    ///     <para>
    ///         Type variables are <em>not</em> a reason to refuse: the lifted function declares
    ///         them as its own type parameters and both backends instantiate the call sites
    ///         explicitly. The one shape that remains unrepresentable is such a member in value
    ///         position, which <see cref="Rewrite" /> reports where it occurs.
    ///     </para>
    /// </summary>
    private static string? Unliftable(
        IrNode.LetRec letrec,
        HashSet<string> functionNames,
        Scope scope
    )
    {
        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef)
                continue;

            if (scope.InstanceState.Count == 0)
                continue;

            // A local wins over a same-named field, so check only the names that are not
            // bound locally. ObjectLifter turns each captured local into both a constructor
            // parameter and a field of the synthesized class, and inside that constructor the
            // name refers to the parameter — which lifts perfectly well as a capture.
            var field = ClosureConverter
                .CollectFreeVars(binding.Value, functionNames)
                .FirstOrDefault(v =>
                    !scope.Locals.Contains(v.Name) && scope.InstanceState.Contains(v.Name)
                );
            if (field is not null)
                return $"'letrec' binding '{binding.Name}' reads the field '{field.Name}': a "
                    + "recursive group is lifted to top-level static functions, which have no "
                    + "instance to read fields from. Pass the field in as a parameter instead";
        }

        return null;
    }

    private static IrNode BuildSpine(
        IEnumerable<(string Name, IrNode Value, ZType? VarType)> bindings,
        IrNode body,
        IrNode.LetRec source
    )
    {
        var result = body;
        foreach (var (name, value, varType) in bindings.Reverse())
            result = new IrNode.Let(name, value, result, varType)
            {
                Type = source.Type,
                Span = source.Span,
            };
        return result;
    }

    /// <summary>The emitted name of a lifted function. The module is part of it because group
    ///     ids restart per module while the emitted assembly may hold several — see the call in
    ///     <see cref="IrLowering" />.</summary>
    private string LiftedName(int groupId, string bindingName)
    {
        return modulePrefix is null
            ? $"__letrec_{groupId}_{bindingName}"
            : $"__letrec_{modulePrefix}_{groupId}_{bindingName}";
    }

    /// <summary>Adds <paramref name="inner" />'s entries on top of <paramref name="outer" />'s,
    ///     so a nested group's members shadow a same-named member of an enclosing one.</summary>
    private static Dictionary<string, GroupRef> Merge(
        IReadOnlyDictionary<string, GroupRef> outer,
        Dictionary<string, GroupRef> inner
    )
    {
        var merged = new Dictionary<string, GroupRef>(outer, StringComparer.Ordinal);
        foreach (var (name, target) in inner)
            merged[name] = target;
        return merged;
    }

    private static Dictionary<string, GroupRef> Shadow(
        IReadOnlyDictionary<string, GroupRef> substitutions,
        IEnumerable<string> names
    )
    {
        var shadowed = new HashSet<string>(names, StringComparer.Ordinal);
        return substitutions
            .Where(kv => !shadowed.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>A sibling reference target inside a lifted body.</summary>
    private sealed record GroupRef(
        string LiftedName,
        IReadOnlyList<IrNode> CaptureArgs,
        ZType LiftedType
    );

    private sealed record Scope(
        HashSet<string> Locals,
        IReadOnlyDictionary<string, GroupRef> Substitutions,
        HashSet<string> InstanceState,
        IReadOnlyDictionary<int, GenericConstraintKind> ConstraintsByVarId
    )
    {
        public Scope Bind(string name)
        {
            return Bind([name]);
        }

        public Scope Bind(IEnumerable<string> names)
        {
            var bound = names as IReadOnlyList<string> ?? names.ToList();
            return this with
            {
                Locals = ClosureConverter.Extend(Locals, bound),
                Substitutions =
                    Substitutions.Count == 0 ? Substitutions : Shadow(Substitutions, bound),
            };
        }
    }
}

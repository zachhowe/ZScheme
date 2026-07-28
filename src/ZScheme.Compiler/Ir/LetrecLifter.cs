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
///         Inside a lifted body a reference to a sibling becomes a direct call (or an
///         <see cref="IrNode.Closure" /> in value position); at the original site each function
///         binding becomes a <c>let</c> bound to an <see cref="IrNode.Closure" />, and each
///         non-function binding stays an ordinary <c>let</c>.
///     </para>
///     <para>
///         Capture sets are per-binding and closed transitively over the group's call graph:
///         <c>captures(f) = freeVars(f) ∪ ⋃ captures(g)</c> for every sibling <c>g</c> that
///         <c>f</c> mentions. A mutually-recursive cycle therefore ends up sharing one capture
///         set, while an <c>f</c> that merely calls <c>g</c> does not inherit unrelated
///         captures — which keeps the site's emission order acyclic.
///     </para>
/// </summary>
public sealed class LetrecLifter(DiagnosticBag diagnostics)
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
        return Rewrite(node, new Scope([], new Dictionary<string, GroupRef>(), []));
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
    ///     <paramref name="scope" /> carries two things down the tree: the names bound by
    ///     enclosing <b>local</b> binders (so the group knows which free variables are captures
    ///     rather than globals), and the substitutions that are live inside a lifted body (so a
    ///     sibling reference becomes a direct call). Every binder extends the former and drops
    ///     shadowed names from the latter.
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
                    new IrNode.Var(target.LiftedName) { Type = target.LiftedType },
                    [.. target.CaptureArgs, .. call.Args.Select(a => Rewrite(a, scope))]
                )
                {
                    Type = call.Type,
                    Span = call.Span,
                };

            // A sibling used as a value inside a lifted body: rebuild its closure from the
            // captures the enclosing lifted function already holds as parameters.
            case IrNode.Var v when scope.Substitutions.TryGetValue(v.Name, out var target):
                return new IrNode.Closure(target.LiftedName, target.CaptureArgs)
                {
                    Type = v.Type,
                    Span = v.Span,
                };

            case IrNode.FuncDef func:
                return func with
                {
                    Body = Rewrite(func.Body, scope.Bind(func.Params.Select(p => p.Name))),
                };

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
        // enclosing lifted body is shadowed by it.
        var siteScope = scope.Bind(letrec.Bindings.Select(b => b.Name));

        if (functionNames.Count == 0)
            return BuildSpine(
                letrec.Bindings.Select(b => (b.Name, Rewrite(b.Value, siteScope), b.VarType)),
                Rewrite(letrec.Body, siteScope),
                letrec
            );

        var captures = ComputeCaptures(letrec, functionNames, valueNames, scope);

        var unliftable = Unliftable(letrec, functionNames, captures, scope);
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
                $"__letrec_{groupId}_{binding.Name}",
                // Inside a lifted body the captures are parameters, so a sibling's closure is
                // rebuilt from same-named locals.
                [.. captureVars.Select(v => new IrNode.Var(v.Name) { Type = v.Type })],
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
            var bodyScope = new Scope(
                ClosureConverter.Extend(
                    scope.Locals,
                    captureParams.Concat(func.Params).Select(p => p.Name)
                ),
                Shadow(lifted, func.Params.Select(p => p.Name)),
                []
            );

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
                }
            );
        }

        return BuildSpine(
            OrderedSiteBindings(letrec, lifted, captures, siteScope),
            Rewrite(letrec.Body, siteScope),
            letrec
        );
    }

    /// <summary>
    ///     Emits the group's bindings in an order that never reads an unassigned one.
    ///     Non-function bindings keep their source order (their initializers can have side
    ///     effects); a function binding's closure is materialized lazily, just before the first
    ///     binding that needs it, and any left over are flushed at the end. Building a closure
    ///     is pure, so moving it is unobservable.
    ///     <para>
    ///         This terminates without a cycle check because <c>LetrecInitializationChecker</c>
    ///         has already rejected any group where a non-function binding could transitively
    ///         reach a later one — which is exactly the condition under which a closure's
    ///         captures would not yet be bound here.
    ///     </para>
    /// </summary>
    private List<(string Name, IrNode Value, ZType? VarType)> OrderedSiteBindings(
        IrNode.LetRec letrec,
        Dictionary<string, GroupRef> lifted,
        Dictionary<string, List<IrNode.Var>> captures,
        Scope siteScope
    )
    {
        var ordered = new List<(string, IrNode, ZType?)>();
        var materialized = new HashSet<string>(StringComparer.Ordinal);
        var byName = letrec.Bindings.ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);

        void Materialize(string name)
        {
            if (!materialized.Add(name))
                return;

            var target = lifted[name];
            ordered.Add(
                (
                    name,
                    new IrNode.Closure(
                        target.LiftedName,
                        [.. captures[name].Select(v => new IrNode.Var(v.Name) { Type = v.Type })]
                    )
                    {
                        Type = byName[name].Value.Type,
                        Span = byName[name].Value.Span,
                    },
                    byName[name].VarType
                )
            );
        }

        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is IrNode.FuncDef)
                continue;

            foreach (var free in ClosureConverter.CollectFreeVars(binding.Value, []))
                if (lifted.ContainsKey(free.Name))
                    Materialize(free.Name);

            ordered.Add((binding.Name, Rewrite(binding.Value, siteScope), binding.VarType));
        }

        foreach (var binding in letrec.Bindings)
            if (binding.Value is IrNode.FuncDef)
                Materialize(binding.Name);

        return ordered;
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
                    .Where(v => scope.Locals.Contains(v.Name) || valueNames.Contains(v.Name)),
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
    ///     The reason this group cannot be lifted, or null when it can. Both cases mirror
    ///     <c>ClosureConverter</c>'s own refusals, which exist for the same reason: a lifted
    ///     top-level static function has neither the enclosing function's type parameters nor a
    ///     <c>this</c>. The difference is that a plain lambda can fall back to the backends'
    ///     own lambda paths, and a recursive group has no such fallback — so this is an error
    ///     rather than a quiet opt-out.
    /// </summary>
    private static string? Unliftable(
        IrNode.LetRec letrec,
        HashSet<string> functionNames,
        Dictionary<string, List<IrNode.Var>> captures,
        Scope scope
    )
    {
        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef func)
                continue;

            if (
                Substitution.FreeVars(func.Type).Count > 0
                || captures[binding.Name].Any(v => Substitution.FreeVars(v.Type).Count > 0)
            )
                return "'letrec' is not supported inside a generic function: the group's "
                    + "functions are lifted to top-level static functions, which cannot "
                    + "reference the enclosing function's type parameters";

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

    private static Dictionary<string, GroupRef> Shadow(
        Dictionary<string, GroupRef> substitutions,
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
        HashSet<string> InstanceState
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
                    Substitutions.Count == 0
                        ? Substitutions
                        : Shadow(
                            Substitutions.ToDictionary(
                                kv => kv.Key,
                                kv => kv.Value,
                                StringComparer.Ordinal
                            ),
                            bound
                        ),
            };
        }
    }
}

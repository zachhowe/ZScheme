using ZScheme.Compiler.Codegen;
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

    /// <summary>
    ///     Where a group that needs <c>this</c> puts its lifted members while the enclosing
    ///     <see cref="IrNode.ClassDecl" /> is being rewritten. Non-null only inside that
    ///     rewrite, and drained by it, so a group in one class can never leak a method into
    ///     another. Mirrors <see cref="ObjectLifter" />'s sink for synthesized classes.
    /// </summary>
    private List<IrObjectMethod>? _methodSink;

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

    /// <summary>
    ///     The field names a method body can reach by bare name. A lifted function is a
    ///     top-level static, so it has no <c>this</c> to read them through.
    ///     <para>
    ///         Inherited fields count for a <c>define-class</c> but not for a class lifted from
    ///         an <c>(object …)</c>: that body does not bring the base class's fields into
    ///         bare-name scope (<see cref="Types.TypeInferer" />'s object-expression case binds
    ///         only each method's own parameters), so a bare name colliding with an inherited
    ///         field is a module-level function reference. Counting it as instance state would
    ///         refuse a group that is perfectly liftable. Both emitters draw the same line —
    ///         see the <c>IsObjectLifted</c> guard on <c>_currentClassFields</c>.
    ///     </para>
    /// </summary>
    private HashSet<string> InstanceState(IrNode.ClassDecl cd)
    {
        return cd.IsObjectLifted
            ? new HashSet<string>(cd.Fields.Select(f => f.Name), StringComparer.Ordinal)
            : UpTheBaseChain(cd, c => c.Fields.Select(f => f.Name));
    }

    /// <summary>
    ///     The method names a body can reach by bare name, following the base-class chain.
    ///     <see cref="Types.TypeInferer" /> puts sibling methods (self included) and inherited
    ///     ones in scope unqualified, and both emitters resolve such a name to <c>this.M</c> —
    ///     so a bare <c>(M …)</c> inside a group binding is an instance call, and a lifted static
    ///     has no receiver to make it with.
    ///     <para>
    ///         Unlike <see cref="InstanceState" /> this is not narrowed for an object-lifted
    ///         class, because the emitters do not narrow it either: <c>_currentClassMethods</c>
    ///         carries inherited names whatever the class came from. The set is therefore an
    ///         over-approximation for an <c>(object …)</c>, whose methods are not in each
    ///         other's bare-name scope — but over-approximating only costs a refusal on a shape
    ///         that fails type checking first, whereas under-approximating would let the
    ///         miscompile back in.
    ///     </para>
    /// </summary>
    private HashSet<string> InstanceMethods(IrNode.ClassDecl cd)
    {
        return UpTheBaseChain(cd, c => c.Methods.Select(m => m.Name));
    }

    /// <summary>
    ///     The subset of <see cref="InstanceState" /> a group may capture <em>by value</em>
    ///     instead of being refused: the fields that cannot change after construction.
    ///     <para>
    ///         Such a field is read once at the group's site — which is inside the method, where
    ///         the bare name still resolves to <c>this.Field</c> — and passed in as an ordinary
    ///         leading parameter, exactly what the refusal used to tell the author to do by
    ///         hand. Nothing downstream can tell the difference between that and any other
    ///         capture, so no new IR node, emitter path or traversal is involved. It is also
    ///         cheaper than reaching through an instance would be: one read at the site rather
    ///         than one per iteration of the loop.
    ///     </para>
    ///     <para>
    ///         A <c>#:mutable</c> field is excluded because capturing it would freeze the value
    ///         the loop sees at entry while the source can still observe writes through
    ///         <c>this</c>, and an <c>init</c> field with it — the point is to capture only what
    ///         provably cannot change while the group runs.
    ///     </para>
    /// </summary>
    private HashSet<string> CapturableInstanceState(IrNode.ClassDecl cd)
    {
        var capturable = cd.IsObjectLifted
            ? new HashSet<string>(
                cd.Fields.Where(Immutable).Select(f => f.Name),
                StringComparer.Ordinal
            )
            : UpTheBaseChain(cd, c => c.Fields.Where(Immutable).Select(f => f.Name));

        // A name shadowed by a mutable field anywhere on the chain is not capturable, whatever
        // a base class declared it as.
        capturable.ExceptWith(UpTheBaseChain(cd, c => c.Fields.Where(f => !Immutable(f)).Select(f => f.Name)));
        return capturable;

        static bool Immutable(IrField f) => f is { IsMutable: false, IsInit: false };
    }

    /// <summary>Collects a name set from <paramref name="cd" /> and every class it inherits
    ///     from. The guard bounds a base-class cycle, which the type checker rejects but which
    ///     must not hang the compiler if one ever reaches here.</summary>
    private HashSet<string> UpTheBaseChain(
        IrNode.ClassDecl cd,
        Func<IrNode.ClassDecl, IEnumerable<string>> select
    )
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var current = cd;
        var guard = 0;
        while (current is not null && guard++ < 64)
        {
            foreach (var name in select(current))
                names.Add(name);
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
                // Same shape of limit, different carrier: a group that reaches instance state
                // is hosted on the class as a method, and IrNode.Closure names a top-level
                // static with no slot for a receiver. Calls are unaffected — a bare name in a
                // method body already resolves to `this.M` on both backends.
                else if (target.IsInstanceMethod)
                    diagnostics.Error(
                        $"'{v.Name}' is a recursive local function that reaches the enclosing "
                            + "instance, and it is used here as a value. Such a function is hosted "
                            + "on the class as a private method, which cannot be turned into a "
                            + "delegate. Call it directly instead, or move it to a top-level "
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
                return cc with { Args = cc.Args.Select(a => Rewrite(a, scope)).ToList() };

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
                var methods = InstanceMethods(cd);
                var capturable = CapturableInstanceState(cd);

                // Saved and restored rather than just assigned: a ClassDecl nested inside
                // another class's method body must not drain into its parent's list.
                var savedSink = _methodSink;
                _methodSink = [];
                try
                {
                    var rewritten = cd
                        .Methods.Select(m =>
                            m with
                            {
                                Body = Rewrite(
                                    m.Body,
                                    scope.Bind(m.Params.Select(p => p.Name)) with
                                    {
                                        InstanceState = fields,
                                        InstanceMethods = methods,
                                        CapturableInstanceState = capturable,
                                        InstanceHost = cd,
                                        InInstanceInitializer = false,
                                    }
                                ),
                            }
                        )
                        .ToList();

                    // Appended after the user's own, so their indices are unchanged; both
                    // emitters register every method before emitting any body, so a sibling
                    // call resolves whichever order they appear in.
                    return cd with
                    {
                        Methods = [.. rewritten, .. _methodSink],
                        Constructor = RewriteConstructor(cd.Constructor, scope),
                    };
                }
                finally
                {
                    _methodSink = savedSink;
                }

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
    ///     <para>
    ///         The instance context is <em>not</em> carried in, though: a constructor's scope
    ///         binds only its own parameters (<see cref="Types.TypeInferer" />), so a bare name
    ///         there that collides with a field or a method is the module-level function, not
    ///         <c>this.X</c> — which is exactly how both emitters resolve it. Treating those
    ///         names as instance state would refuse groups that lift perfectly well, blaming
    ///         them for reading something they never named.
    ///     </para>
    /// </summary>
    private IrConstructor? RewriteConstructor(IrConstructor? constructor, Scope scope)
    {
        if (constructor is null)
            return null;

        var ctorScope = scope.Bind(constructor.Params.Select(p => p.Name)) with
        {
            InstanceState = [],
            InstanceMethods = [],
            CapturableInstanceState = [],
            InInstanceInitializer = true,
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

        // A group that reaches instance state cannot be a static function. It can still be a
        // private method of the class it was written in, which reaches fields and siblings by
        // bare name exactly as the method around it does — so the refusal only stands when
        // there is no class to host it on.
        var onInstance = false;
        var staticRefusal = Unliftable(letrec, functionNames, scope);
        if (staticRefusal is not null)
        {
            var instanceRefusal = InstanceHostRefusal(letrec, scope, staticRefusal);
            if (instanceRefusal is null)
            {
                onInstance = true;
            }
            else
            {
                diagnostics.Error(instanceRefusal, letrec.Span);
                // Emit a plain (non-recursive) spine so the rest of lowering has a well-formed
                // tree to walk; the error above already fails the compilation.
                return BuildSpine(
                    letrec.Bindings.Select(b => (b.Name, Rewrite(b.Value, siteScope), b.VarType)),
                    Rewrite(letrec.Body, siteScope),
                    letrec
                );
            }
        }

        var groupId = _groupId++;
        var lifted = new Dictionary<string, GroupRef>(StringComparer.Ordinal);
        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef func)
                continue;

            var captureVars = captures[binding.Name];
            lifted[binding.Name] = new GroupRef(
                HelperName(groupId, binding.Name, onInstance ? scope.InstanceHost : null),
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
                ),
                onInstance
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
            // A static has no instance context — clearing it also stops a nested group from
            // being blamed for the enclosing class's fields. A helper hosted on the class is
            // itself a method of it, so there the context carries straight through and a group
            // nested inside the helper can reach instance state just as this one did.
            // The enclosing substitutions are kept so a nested group can still reach an outer
            // group's members; the constraints are kept because the same type-var IDs survive.
            var bodyScope = new Scope(
                ClosureConverter.Extend(
                    scope.Locals,
                    captureParams.Concat(func.Params).Select(p => p.Name)
                ),
                Shadow(Merge(scope.Substitutions, lifted), func.Params.Select(p => p.Name)),
                onInstance ? scope.InstanceState : [],
                scope.ConstraintsByVarId
            )
            {
                InstanceMethods = onInstance ? scope.InstanceMethods : [],
                CapturableInstanceState = onInstance ? scope.CapturableInstanceState : [],
                InstanceHost = onInstance ? scope.InstanceHost : null,
            };

            var body = Rewrite(func.Body, bodyScope);

            if (onInstance)
            {
                // No TypeParams or TypeParamConstraints: IrObjectMethod has nowhere to put
                // them, which is why InstanceHostRefusal turns a generic group away.
                _methodSink!.Add(
                    new IrObjectMethod(
                        target.LiftedName,
                        [.. captureParams, .. func.Params],
                        func.ReturnType,
                        body,
                        IsAsync: func.IsAsync
                    )
                    {
                        IsSynthesizedHelper = true,
                    }
                );
                continue;
            }

            var typeParams = IrLowering.ExtractFuncTypeParams(target.LiftedType);
            _liftedFunctions.Add(
                func with
                {
                    Name = target.LiftedName,
                    Params = [.. captureParams, .. func.Params],
                    Body = body,
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
        )
        {
            // The site is still wherever the group was written — inside a method body, its
            // instance context is unchanged.
            InstanceMethods = scope.InstanceMethods,
            CapturableInstanceState = scope.CapturableInstanceState,
        };
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
            //
            // A field that cannot change after construction is captured too, and the site reads
            // it through `this` like any other bare name in the method — which is what lets a
            // loop helper inside a method use the instance's state instead of being refused.
            // A local of the same name still wins, exactly as it does at the site.
            direct[binding.Name] =
            [
                .. ClosureConverter
                    .CollectFreeVars(binding.Value, functionNames)
                    .SelectMany(v => ThroughSubstitution(v, scope))
                    .Where(v =>
                        scope.Locals.Contains(v.Name)
                        || valueNames.Contains(v.Name)
                        || scope.CapturableInstanceState.Contains(v.Name)
                    )
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

            // Checked first and unconditionally: `set!` on a field and a `super/` call name
            // their target implicitly, so neither reaches the free-variable set and neither
            // depends on the name sets below. Both are instance-only by construction —
            // IrNode.SetField has no receiver because there is only ever one — so finding
            // either means this group needs a `this`, wherever it was written.
            if (TouchesInstanceImplicitly(binding.Value))
                return $"'letrec' binding '{binding.Name}' assigns a field or calls a 'super/' "
                    + "method: a recursive group is lifted to top-level static functions, which "
                    + "have no instance to reach them through. Move the definition to a "
                    + "top-level 'define', or do the assignment in the enclosing method";

            if (scope.InstanceState.Count == 0 && scope.InstanceMethods.Count == 0)
                continue;

            // A local wins over a same-named field or method, so check only the names that are
            // not bound locally. ObjectLifter turns each captured local into both a constructor
            // parameter and a field of the synthesized class, and inside that constructor the
            // name refers to the parameter — which lifts perfectly well as a capture.
            var free = ClosureConverter
                .CollectFreeVars(binding.Value, functionNames)
                .Where(v => !scope.Locals.Contains(v.Name))
                .ToList();

            // A field that cannot change after construction is captured by value instead
            // (ComputeCaptures), so only the ones whose value the loop could still observe
            // changing are left with nowhere to come from.
            var field = free.FirstOrDefault(v =>
                scope.InstanceState.Contains(v.Name)
                && !scope.CapturableInstanceState.Contains(v.Name)
            );
            if (field is not null)
                return $"'letrec' binding '{binding.Name}' reads the mutable field "
                    + $"'{field.Name}': a recursive group is lifted to top-level static "
                    + "functions, which have no instance to read fields from, and a mutable "
                    + "field cannot be captured by value because the loop would not see a "
                    + "later write. Pass the field in as a parameter instead";

            // A field of the same name wins over a method in both emitters' bare-name
            // resolution, so this runs after the field check — the same precedence they apply.
            var method = free.FirstOrDefault(v => scope.InstanceMethods.Contains(v.Name));
            if (method is not null)
                return $"'letrec' binding '{binding.Name}' calls the method '{method.Name}': a "
                    + "recursive group is lifted to top-level static functions, which have no "
                    + "instance to call methods on. Pass the result in as a parameter instead, "
                    + "or move the definition to a top-level 'define'";

        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="node" /> writes a field or makes a <c>super/</c> call — the
    ///     two IR shapes whose receiver is an implicit <c>this</c> and so cannot appear in
    ///     <see cref="ClosureConverter.CollectFreeVars" />'s result. No field set is consulted:
    ///     <see cref="IrNode.SetField" /> has no receiver because the enclosing instance is the
    ///     only thing it can ever mean.
    ///     <para>
    ///         The default arm answers "no", which is only safe because every arm that can hold
    ///         one of the two is listed. <c>LetrecLifterInstanceScanTests</c> pins that by
    ///         reflection over the <see cref="IrNode" /> hierarchy, so a node kind added later
    ///         fails a test rather than silently escaping the scan.
    ///     </para>
    /// </summary>
    private static bool TouchesInstanceImplicitly(IrNode node)
    {
        bool Any(IEnumerable<IrNode> nodes) => nodes.Any(TouchesInstanceImplicitly);

        return node switch
        {
            IrNode.SetField => true,
            IrNode.SuperMethodCall => true,

            IrNode.Let let => Any([let.Value, let.Body]),
            IrNode.Use use => Any([use.Value, use.Body]),
            IrNode.If i => Any([i.Condition, i.Then, i.Else]),
            IrNode.Seq seq => Any(seq.Nodes),
            IrNode.FuncDef f => TouchesInstanceImplicitly(f.Body),
            IrNode.Match m => Any([m.Scrutinee, .. m.Arms.Select(a => a.Body)]),
            IrNode.WithHandlers wh => Any([wh.Body, .. wh.Handlers.Select(h => h.HandlerBody)]),
            IrNode.Call call => Any([call.Function, .. call.Args]),
            IrNode.BinOp b => Any([b.Left, b.Right]),
            IrNode.UnaryOp u => TouchesInstanceImplicitly(u.Operand),
            IrNode.MethodCall mc => Any([mc.Receiver, .. mc.Args]),
            IrNode.ClrCall cc => Any(cc.Args),
            IrNode.ClrNew cn => Any(cn.Args),
            IrNode.UnionCaseNew ucn => Any(ucn.Args),
            IrNode.TupleNew tn => Any(tn.Elements),
            IrNode.RecordNew rn => Any(rn.Fields.Select(f => f.Value)),
            IrNode.RecordWith rw => Any([rw.Record, .. rw.Updates.Select(u => u.Value)]),
            IrNode.FieldGet fg => TouchesInstanceImplicitly(fg.Record),
            IrNode.MutableArrayNew man => Any(man.Elements),
            IrNode.Throw th => TouchesInstanceImplicitly(th.Expr),
            IrNode.Await aw => TouchesInstanceImplicitly(aw.Expr),
            IrNode.Closure cl => Any(cl.CapturedValues),
            IrNode.LetRec lr => Any([lr.Body, .. lr.Bindings.Select(b => b.Value)]),
            IrNode.ObjectExpr oe => Any(oe.Methods.Select(m => m.Body)),
            // Unreachable in practice — TailCallLowering introduces TcoJump long after this
            // pass — but covered so the scan stays total if that ordering ever changes.
            IrNode.TcoJump tj => Any(tj.NewArgs),

            // Leaves: literals, Var, TypeOf, and the declaration nodes.
            _ => false,
        };
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

    /// <summary>
    ///     <see cref="LiftedName" />, made unique among <paramref name="host" />'s own members
    ///     when the group is hosted on a class. Group ids are unique across the whole pass, so
    ///     two groups never collide with each other and a static and a helper never share a
    ///     name — which matters because the IL backend resolves a bare call against the
    ///     top-level method table before the class's. What is not ruled out is a *source*
    ///     member spelled like a lifted name, which the lexer permits.
    /// </summary>
    private string HelperName(int groupId, string bindingName, IrNode.ClassDecl? host)
    {
        var name = LiftedName(groupId, bindingName);
        if (host is null)
            return name;

        var taken = new HashSet<string>(
            host.Methods.Select(m => NameConverter.SanitizeIdentifier(m.Name))
                .Concat(InstanceState(host).Select(NameConverter.SanitizeIdentifier))
                .Concat(InstanceMethods(host).Select(NameConverter.SanitizeIdentifier)),
            StringComparer.Ordinal
        );

        var candidate = name;
        var suffix = 0;
        while (taken.Contains(NameConverter.SanitizeIdentifier(candidate)))
            candidate = $"{name}_{++suffix}";
        return candidate;
    }

    /// <summary>
    ///     Why the group cannot be hosted on the enclosing class as a private method, or null
    ///     when it can. <paramref name="staticRefusal" /> is what to say when there is no class
    ///     in sight — the group simply needed an instance and there is none.
    /// </summary>
    private static string? InstanceHostRefusal(
        IrNode.LetRec letrec,
        Scope scope,
        string staticRefusal
    )
    {
        if (scope.InstanceHost is null)
            return staticRefusal;

        // A constructor's own scope has no `this` to speak of for a super-argument, and
        // neither emitter has the class's method map live while emitting one, so a call to a
        // helper from there would not resolve.
        if (scope.InInstanceInitializer)
            return staticRefusal;

        foreach (var binding in letrec.Bindings)
        {
            if (binding.Value is not IrNode.FuncDef func)
                continue;

            // IrObjectMethod carries no type parameters and neither emitter writes them on a
            // method, so a group generalized over its own type variables has nowhere to
            // declare them. Annotating the helper's parameter and return types resolves the
            // variables and takes this branch out of play.
            if (Substitution.FreeVars(func.Type).Count > 0)
                return $"'letrec' binding '{binding.Name}' reaches the enclosing instance and "
                    + "its type mentions type variables. Such a group is hosted on the class as "
                    + "a private method, which cannot declare its own type parameters. Annotate "
                    + "the definition's parameter and return types, or move it to a top-level "
                    + "'define'";
        }

        return null;
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
    /// <param name="IsInstanceMethod">
    ///     True when the member was hosted on the enclosing class rather than lifted to a
    ///     static. The call sites need no distinction — a bare name resolves to
    ///     <c>this.M</c> in a method body on both backends, which is exactly what a call to a
    ///     static resolves to at module level — but a member used as a <em>value</em> does:
    ///     <see cref="IrNode.Closure" /> names a top-level static and has no receiver slot.
    /// </param>
    private sealed record GroupRef(
        string LiftedName,
        IReadOnlyList<IrNode> CaptureArgs,
        ZType LiftedType,
        bool IsInstanceMethod = false
    );

    private sealed record Scope(
        HashSet<string> Locals,
        IReadOnlyDictionary<string, GroupRef> Substitutions,
        HashSet<string> InstanceState,
        IReadOnlyDictionary<int, GenericConstraintKind> ConstraintsByVarId
    )
    {
        /// <summary>
        ///     The sibling and inherited method names reachable by bare name here, empty outside
        ///     a class body. An init property rather than a positional parameter so that the two
        ///     scopes standing for "no instance context" — a lifted function's body and the
        ///     top-level scope <see cref="Lift" /> starts from — clear it by construction, the
        ///     same way they pass an empty <see cref="InstanceState" />.
        /// </summary>
        public HashSet<string> InstanceMethods { get; init; } = [];

        /// <summary>
        ///     The subset of <see cref="InstanceState" /> that may be captured by value rather
        ///     than refused — see <see cref="LetrecLifter.CapturableInstanceState" />. Cleared
        ///     alongside the other two wherever there is no instance to read from.
        /// </summary>
        public HashSet<string> CapturableInstanceState { get; init; } = [];

        /// <summary>
        ///     The class whose <c>this</c> is reachable here, or null outside any instance
        ///     body. A group that needs an instance is hosted on it as a private method
        ///     instead of being refused.
        /// </summary>
        public IrNode.ClassDecl? InstanceHost { get; init; }

        /// <summary>
        ///     True inside a constructor. Fields are not in bare-name scope there, and neither
        ///     emitter has the class's method map live while emitting one, so a helper on the
        ///     class is unavailable even though <see cref="InstanceHost" /> is set.
        /// </summary>
        public bool InInstanceInitializer { get; init; }

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

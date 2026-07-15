using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     IR lowering sub-pass that resolves every <see cref="IrPattern.Constructor" /> against
///     the union registry: it attaches the owning union name and each field sub-pattern's
///     concrete <see cref="ZType" /> (after substituting the scrutinee's type arguments). This
///     establishes the invariant that <b>no unresolved constructor pattern reaches the
///     emitters</b>, so neither backend has to re-derive union metadata for itself — the
///     duplicated, historically-divergent resolution the two emitters used to carry.
///
///     It runs as a post-pass over the fully-lowered tree (after all <c>define-union</c> forms
///     have populated the registry), so it resolves patterns whose union is declared later in
///     the source than the <c>match</c> that uses it — a forward reference the language allows.
///
///     It deliberately does <b>not</b> compile matches to decision trees or check
///     exhaustiveness; each backend still emits its own match. The traversal mirrors
///     <see cref="IiffeBetaReducer" />, which runs immediately before this pass, so the two
///     share a recursion set: any node that can contain a nested <c>match</c> is one both
///     passes descend into.
/// </summary>
public sealed class PatternResolver(UnionCaseRegistry registry, TypeAliasRegistry typeAliases)
{
    public IrNode Resolve(IrNode node)
    {
        return Rewrite(node);
    }

    private IrNode Rewrite(IrNode node)
    {
        switch (node)
        {
            case IrNode.Match match:
            {
                var scrutinee = Rewrite(match.Scrutinee);
                var arms = match
                    .Arms.Select(a => new IrMatchArm(
                        AnnotatePattern(a.Pattern, scrutinee.Type),
                        Rewrite(a.Body)
                    ))
                    .ToList();
                return new IrNode.Match(scrutinee, arms)
                {
                    Type = match.Type,
                    IsTailCall = match.IsTailCall,
                    Span = match.Span,
                };
            }

            case IrNode.Let let:
                return new IrNode.Let(
                    let.VarName,
                    Rewrite(let.Value),
                    Rewrite(let.Body),
                    let.VarType,
                    let.EmitName
                )
                {
                    Type = let.Type,
                    IsTailCall = let.IsTailCall,
                    Span = let.Span,
                };

            case IrNode.Use use:
                return new IrNode.Use(
                    use.VarName,
                    Rewrite(use.Value),
                    Rewrite(use.Body),
                    use.VarType
                )
                {
                    Type = use.Type,
                    IsTailCall = use.IsTailCall,
                    Span = use.Span,
                };

            case IrNode.If ifNode:
                return new IrNode.If(
                    Rewrite(ifNode.Condition),
                    Rewrite(ifNode.Then),
                    Rewrite(ifNode.Else)
                )
                {
                    Type = ifNode.Type,
                    IsTailCall = ifNode.IsTailCall,
                    Span = ifNode.Span,
                };

            case IrNode.Call call:
                return new IrNode.Call(Rewrite(call.Function), call.Args.Select(Rewrite).ToList())
                {
                    Type = call.Type,
                    IsTailCall = call.IsTailCall,
                    Span = call.Span,
                };

            case IrNode.Seq seq:
                return new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList())
                {
                    Type = seq.Type,
                    IsTailCall = seq.IsTailCall,
                    Span = seq.Span,
                };

            case IrNode.BinOp binop:
                return new IrNode.BinOp(binop.Op, Rewrite(binop.Left), Rewrite(binop.Right))
                {
                    Type = binop.Type,
                    IsTailCall = binop.IsTailCall,
                    Span = binop.Span,
                };

            case IrNode.UnaryOp unary:
                return new IrNode.UnaryOp(unary.Op, Rewrite(unary.Operand))
                {
                    Type = unary.Type,
                    IsTailCall = unary.IsTailCall,
                    Span = unary.Span,
                };

            case IrNode.FuncDef fd:
                return fd with { Body = Rewrite(fd.Body) };

            case IrNode.Closure closure:
                return new IrNode.Closure(
                    closure.LiftedFuncName,
                    closure.CapturedValues.Select(Rewrite).ToList()
                )
                {
                    Type = closure.Type,
                    IsTailCall = closure.IsTailCall,
                    Span = closure.Span,
                };

            case IrNode.MethodCall mc:
                return mc with
                {
                    Receiver = Rewrite(mc.Receiver),
                    Args = mc.Args.Select(Rewrite).ToList(),
                };

            case IrNode.ClrNew cn:
                return new IrNode.ClrNew(
                    cn.QualifiedTypeName,
                    cn.TypeArgs,
                    cn.Args.Select(Rewrite).ToList()
                )
                {
                    Type = cn.Type,
                    IsTailCall = cn.IsTailCall,
                    Span = cn.Span,
                };

            case IrNode.ClrCall cc:
                return new IrNode.ClrCall(
                    cc.QualifiedTypeName,
                    cc.MethodName,
                    cc.Args.Select(Rewrite).ToList(),
                    cc.GenericArity,
                    cc.GenericTypeArgs,
                    cc.OutParams,
                    cc.ResolvedMethodInfo
                )
                {
                    Type = cc.Type,
                    IsTailCall = cc.IsTailCall,
                    Span = cc.Span,
                };

            case IrNode.TupleNew tn:
                return new IrNode.TupleNew(tn.Elements.Select(Rewrite).ToList())
                {
                    Type = tn.Type,
                    IsTailCall = tn.IsTailCall,
                    Span = tn.Span,
                };

            case IrNode.UnionCaseNew ucn:
                return new IrNode.UnionCaseNew(
                    ucn.UnionName,
                    ucn.CaseName,
                    ucn.Args.Select(Rewrite).ToList()
                )
                {
                    Type = ucn.Type,
                    IsTailCall = ucn.IsTailCall,
                    Span = ucn.Span,
                };

            case IrNode.RecordNew rn:
                return new IrNode.RecordNew(
                    rn.TypeName,
                    rn.Fields.Select(f => (f.FieldName, Rewrite(f.Value))).ToList()
                )
                {
                    Type = rn.Type,
                    IsTailCall = rn.IsTailCall,
                    Span = rn.Span,
                };

            case IrNode.RecordWith rw:
                return new IrNode.RecordWith(
                    rw.TypeName,
                    Rewrite(rw.Record),
                    rw.Updates.Select(u => (u.FieldName, Rewrite(u.Value))).ToList()
                )
                {
                    Type = rw.Type,
                    IsTailCall = rw.IsTailCall,
                    Span = rw.Span,
                };

            case IrNode.MutableArrayNew man:
                return new IrNode.MutableArrayNew(
                    man.ElementType,
                    man.Elements.Select(Rewrite).ToList()
                )
                {
                    Type = man.Type,
                    IsTailCall = man.IsTailCall,
                    Span = man.Span,
                };

            case IrNode.FieldGet fg:
                return new IrNode.FieldGet(Rewrite(fg.Record), fg.FieldName)
                {
                    Type = fg.Type,
                    IsTailCall = fg.IsTailCall,
                    Span = fg.Span,
                };

            case IrNode.Throw thr:
                return new IrNode.Throw(Rewrite(thr.Expr))
                {
                    Type = thr.Type,
                    IsTailCall = thr.IsTailCall,
                    Span = thr.Span,
                };

            case IrNode.Await aw:
                return new IrNode.Await(Rewrite(aw.Expr))
                {
                    Type = aw.Type,
                    IsTailCall = aw.IsTailCall,
                    Span = aw.Span,
                };

            case IrNode.SetField sf:
                return new IrNode.SetField(sf.FieldName, Rewrite(sf.Value))
                {
                    Type = sf.Type,
                    IsTailCall = sf.IsTailCall,
                    Span = sf.Span,
                };

            case IrNode.SuperMethodCall smc:
                return new IrNode.SuperMethodCall(smc.MethodName, smc.Args.Select(Rewrite).ToList())
                {
                    Type = smc.Type,
                    IsTailCall = smc.IsTailCall,
                    Span = smc.Span,
                };

            case IrNode.WithHandlers wh:
                return new IrNode.WithHandlers(
                    Rewrite(wh.Body),
                    wh.Handlers.Select(h => new IrHandlerClause(
                            h.ExceptionTypeName,
                            h.BindingVarName,
                            Rewrite(h.HandlerBody)
                        ))
                        .ToList()
                )
                {
                    Type = wh.Type,
                    IsTailCall = wh.IsTailCall,
                    Span = wh.Span,
                };

            case IrNode.ClassDecl cd:
                return cd with
                {
                    Methods = cd.Methods.Select(m => m with { Body = Rewrite(m.Body) }).ToList(),
                    Constructor = RewriteConstructor(cd.Constructor),
                };

            default:
                // Leaves (literals, Var, TypeOf) and childless declaration nodes (RecordDecl,
                // UnionDecl, TypeAliasDecl, InterfaceDecl). ObjectExpr is already lifted to
                // ClassDecl by ObjectLifter, and TcoJump is introduced later by the C# backend,
                // so neither reaches this pass — matching IiffeBetaReducer's recursion set.
                return node;
        }
    }

    private IrConstructor? RewriteConstructor(IrConstructor? ctor)
    {
        if (ctor is null)
            return null;
        return ctor with
        {
            BodyExprs = ctor.BodyExprs.Select(Rewrite).ToList(),
            FieldSets = ctor.FieldSets.Select(fs => (fs.FieldName, Rewrite(fs.Value))).ToList(),
            SuperArgs = ctor.SuperArgs?.Select(Rewrite).ToList(),
        };
    }

    /// <summary>
    ///     Annotates a pattern against the type of the value it is matched against, recursing
    ///     into constructor fields and tuple elements. Only <see cref="IrPattern.Constructor" />
    ///     carries annotations; wildcards, variables, and literals are returned unchanged. Tuple
    ///     patterns are threaded through (to reach nested constructors) but not themselves
    ///     annotated — tuple element extraction is positional and needs no union metadata.
    /// </summary>
    private IrPattern AnnotatePattern(IrPattern pattern, ZType? scrutineeType)
    {
        switch (pattern)
        {
            case IrPattern.Constructor c:
            {
                var fieldTypes = new List<ZType?>(c.Fields.Count);
                for (var i = 0; i < c.Fields.Count; i++)
                    fieldTypes.Add(registry.FieldType(scrutineeType, c.Name, i));
                var fields = c.Fields.Select((f, i) => AnnotatePattern(f, fieldTypes[i])).ToList();
                return c with
                {
                    Fields = fields,
                    ResolvedUnion = registry.ResolveUnion(scrutineeType, c.Name),
                    FieldTypes = fieldTypes,
                };
            }

            case IrPattern.Tuple t:
            {
                var elemTypes = TupleElementTypes(scrutineeType, t.Elements.Count);
                var elements = t
                    .Elements.Select((e, i) => AnnotatePattern(e, elemTypes?[i]))
                    .ToList();
                return t with { Elements = elements };
            }

            default:
                return pattern;
        }
    }

    /// <summary>
    ///     The element types of a value-tuple scrutinee, or null when the scrutinee is not a
    ///     value tuple of matching arity. Mirrors the C# emitter's tuple-element threading so
    ///     nested constructor patterns inside tuples resolve identically.
    /// </summary>
    private IReadOnlyList<ZType>? TupleElementTypes(ZType? scrutineeType, int arity)
    {
        return
            scrutineeType is ZType.ZNamedType nt
            && typeAliases.IsValueTupleName(nt.Name)
            && nt.TypeArgs.Count == arity
            ? nt.TypeArgs
            : null;
    }
}

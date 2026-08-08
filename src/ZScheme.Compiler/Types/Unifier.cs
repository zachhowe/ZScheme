using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

public sealed class Unifier(
    Substitution subst,
    DiagnosticBag diagnostics,
    IReadOnlyList<string>? assemblySearchPaths = null,
    Func<string, IReadOnlyList<string>?>? classInterfaceLookup = null,
    IReadOnlyList<string>? clrNamespaces = null,
    Func<string, string>? canonicalTypeName = null
)
{
    private readonly IReadOnlyList<string>? _clrNamespaces = clrNamespaces;

    /// <summary>
    ///     Canonical spelling of a CLR type name, so a short name and its fully-qualified form
    ///     compare equal. Identity when the caller supplies none (direct unit tests).
    /// </summary>
    private readonly Func<string, string> _canonical = canonicalTypeName ?? (name => name);

    public bool Unify(ZType a, ZType b, SourceSpan span)
    {
        return UnifyInner(a, b, span, false);
    }

    private bool UnifyInner(ZType a, ZType b, SourceSpan span, bool nested)
    {
        var ta = subst.Apply(a);
        var tb = subst.Apply(b);

        if (ta.Equals(tb))
            return true;

        if (ta is ZType.ZConstrainedVar cva)
            return BindConstrained(cva, tb, span);

        if (tb is ZType.ZConstrainedVar cvb)
            return BindConstrained(cvb, ta, span);

        if (ta is ZType.ZTypeVar tva)
            return Bind(tva.Id, tb, span);

        if (tb is ZType.ZTypeVar tvb)
            return Bind(tvb.Id, ta, span);

        if (ta is ZType.ZFuncType fa && tb is ZType.ZFuncType fb)
        {
            // Prefer pre-apply params/return when available so nested generic-arg
            // recursion can still observe type vars for widening.
            var aParams = (a as ZType.ZFuncType)?.Params ?? fa.Params;
            var bParams = (b as ZType.ZFuncType)?.Params ?? fb.Params;
            var aReturn = (a as ZType.ZFuncType)?.Return ?? fa.Return;
            var bReturn = (b as ZType.ZFuncType)?.Return ?? fb.Return;

            if (!fa.IsVariadic && !fb.IsVariadic)
            {
                if (fa.Params.Count != fb.Params.Count)
                {
                    diagnostics.Error(
                        $"Function arity mismatch: expected {fa.Params.Count} parameters, got {fb.Params.Count}",
                        span
                    );
                    return false;
                }

                for (var i = 0; i < fa.Params.Count; i++)
                    if (!UnifyInner(aParams[i], bParams[i], span, nested))
                        return false;
            }
            else
            {
                // When either side is variadic, unify the common fixed params
                var minCount = Math.Min(fa.Params.Count, fb.Params.Count);
                for (var i = 0; i < minCount; i++)
                    if (!UnifyInner(aParams[i], bParams[i], span, nested))
                        return false;
            }

            return UnifyInner(aReturn, bReturn, span, nested);
        }

        if (ta is ZType.ZNamedType na && tb is ZType.ZNamedType nb)
        {
            // Implicit boxing: any type can be assigned to System.Object / Object.
            // Inside generic-arg recursion, .NET generics are invariant, so silently
            // accepting the mismatch would leave a value-type var bound where Object
            // is required — producing e.g. Dictionary<string, float32> when the method
            // returns Dictionary<string, object>. Widen the originating type-var chain.
            if (nb is { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
            {
                if (nested)
                    WidenVarChainToObject(a);
                return true;
            }

            if (na is { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
            {
                if (nested)
                    WidenVarChainToObject(b);
                return true;
            }

            if (na.Name == nb.Name && na.TypeArgs.Count == nb.TypeArgs.Count)
            {
                // Use the pre-apply args from the original a/b when available so nested
                // recursion can still observe type vars for widening.
                var aArgs = (a as ZType.ZNamedType)?.TypeArgs ?? na.TypeArgs;
                var bArgs = (b as ZType.ZNamedType)?.TypeArgs ?? nb.TypeArgs;
                for (var i = 0; i < na.TypeArgs.Count; i++)
                    if (!UnifyInner(aArgs[i], bArgs[i], span, true))
                        return false;
                return true;
            }

            // CLR subtype check for concrete (non-generic) named types
            if (na.TypeArgs.Count == 0 && nb.TypeArgs.Count == 0 && IsClrSubtype(na.Name, nb.Name))
                return true;

            diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
            return false;
        }

        if (ta is ZType.ZNullableType nta && tb is ZType.ZNullableType ntb)
            return UnifyInner(nta.Inner, ntb.Inner, span, nested);

        // ZDelegateType ↔ ZFuncType: delegate types are function types at runtime.
        // A lambda (ZFuncType) can be passed where a ZDelegateType is expected, but
        // only when their shapes line up. This matters for overload resolution: a
        // `(-> Task)` thunk must match Func<Task> and NOT RequestDelegate (whose
        // Invoke takes an HttpContext), so the right Use/Map overload is selected.
        if (ta is ZType.ZDelegateType dt && tb is ZType.ZFuncType ft)
        {
            if (DelegateMatchesFunc(dt, ft, span))
            {
                PropagateDelegateLeafTypes(dt, ft, span);
                return true;
            }

            diagnostics.Error($"Delegate/function shape mismatch: '{ta}' vs '{tb}'", span);
            return false;
        }

        if (ta is ZType.ZFuncType ft2 && tb is ZType.ZDelegateType dt2)
        {
            if (DelegateMatchesFunc(dt2, ft2, span))
            {
                PropagateDelegateLeafTypes(dt2, ft2, span);
                return true;
            }

            diagnostics.Error($"Delegate/function shape mismatch: '{ta}' vs '{tb}'", span);
            return false;
        }

        // ZDelegateType ↔ ZDelegateType: unify if names match or CLR subtype
        if (ta is ZType.ZDelegateType dta && tb is ZType.ZDelegateType dtb)
        {
            if (dta.ClrTypeName == dtb.ClrTypeName)
                return true;
            // Try CLR subtype check for delegate types
            try
            {
                var silentDiag = new DiagnosticBag();
                var clr = new ClrInterop(silentDiag, assemblySearchPaths);
                var typeA = clr.FindType(dta.ClrTypeName);
                var typeB = clr.FindType(dtb.ClrTypeName);
                if (
                    typeA is not null
                    && typeB is not null
                    && (typeB.IsAssignableFrom(typeA) || typeA.IsAssignableFrom(typeB))
                )
                    return true;
            }
            catch
            { /* ignore reflection errors */
            }
            diagnostics.Error($"Delegate type mismatch: '{ta}' vs '{tb}'", span);
            return false;
        }

        // Implicit T -> T? widening (non-nullable to nullable)
        if (tb is ZType.ZNullableType ntb2 && ta is not ZType.ZNullableType)
            return UnifyInner(ta, ntb2.Inner, span, nested);

        if (ta is ZType.ZNullableType nta2 && tb is not ZType.ZNullableType)
            return UnifyInner(nta2.Inner, tb, span, nested);

        // Implicit boxing: any type can be assigned to System.Object / Object
        if (tb is ZType.ZNamedType { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
        {
            if (nested)
                WidenVarChainToObject(a);
            return true;
        }

        if (ta is ZType.ZNamedType { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
        {
            if (nested)
                WidenVarChainToObject(b);
            return true;
        }

        diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
        return false;
    }

    /// <summary>
    ///     Whether a delegate type's Invoke signature is shape-compatible with a function
    ///     type. The check is on arity (at every level of nesting): the delegate's Invoke
    ///     arity must equal the function's arity, and any function-typed argument must line
    ///     up with a delegate-typed parameter of matching shape. Leaf parameter/return
    ///     types are not compared (their identity is handled elsewhere and would otherwise
    ///     trip over short-vs-fully-qualified alias names). Stays permissive when the
    ///     delegate type cannot be resolved. This is what lets overload resolution
    ///     distinguish a `(-> Task)` thunk (Func&lt;Task&gt;) from a RequestDelegate.
    /// </summary>
    private bool DelegateMatchesFunc(ZType.ZDelegateType dt, ZType.ZFuncType ft, SourceSpan span)
    {
        try
        {
            using var clr = new ClrInterop(new DiagnosticBag(), assemblySearchPaths);
            var delegateType = clr.FindType(dt.ClrTypeName);
            return delegateType is null || DelegateShapeMatches(delegateType, ft);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    ///     After a delegate type and a function type are confirmed shape-compatible,
    ///     push the delegate's concrete Invoke signature types into any <em>unbound</em>
    ///     type-variable leaves of the function type. <see cref="DelegateMatchesFunc" />
    ///     deliberately checks only arity, so without this a lambda body whose type is an
    ///     unconstrained type variable (e.g. a generic-union field whose type parameter is
    ///     pinned down only by the delegate boundary) is never tied to the delegate's
    ///     concrete leaf type. It then defaults to <c>object</c> in codegen while the
    ///     delegate's <c>Invoke</c> expects the value type, producing IL that fails
    ///     verification (StackUnexpected: found <c>object</c>, expected <c>int32</c>).
    ///     Only unbound vars are filled — concrete leaves are left untouched so the
    ///     permissive alias-name behavior documented on <see cref="DelegateMatchesFunc" />
    ///     is preserved. Found by the fuzzer.
    /// </summary>
    private void PropagateDelegateLeafTypes(
        ZType.ZDelegateType dt,
        ZType.ZFuncType ft,
        SourceSpan span
    )
    {
        try
        {
            using var clr = new ClrInterop(new DiagnosticBag(), assemblySearchPaths);
            var delegateType = clr.FindType(dt.ClrTypeName);
            var invoke = delegateType?.GetMethod("Invoke");
            if (invoke is null)
                return;

            var invokeParams = invoke.GetParameters();
            if (invokeParams.Length != ft.Params.Count)
                return;

            for (var i = 0; i < invokeParams.Length; i++)
                UnifyIfLeafVar(ft.Params[i], invokeParams[i].ParameterType, clr, span);

            UnifyIfLeafVar(ft.Return, invoke.ReturnType, clr, span);
        }
        catch
        { /* best-effort: reflection failures leave inference unchanged */
        }
    }

    private void UnifyIfLeafVar(ZType funcLeaf, Type clrLeaf, ClrInterop clr, SourceSpan span)
    {
        var applied = subst.Apply(funcLeaf);
        if (applied is not (ZType.ZTypeVar or ZType.ZConstrainedVar))
            return;
        UnifyInner(applied, clr.MapClrTypeToZType(clrLeaf), span, true);
    }

    private static bool DelegateShapeMatches(Type delegateClrType, ZType.ZFuncType ft)
    {
        if (!typeof(Delegate).IsAssignableFrom(delegateClrType))
            return true; // not actually a delegate — stay permissive
        var invoke = delegateClrType.GetMethod("Invoke");
        if (invoke is null)
            return true;

        var invokeParams = invoke.GetParameters();
        if (invokeParams.Length != ft.Params.Count)
            return false;

        for (var i = 0; i < invokeParams.Length; i++)
            if (ft.Params[i] is ZType.ZFuncType nestedFt)
            {
                // A function-typed argument must map to a concrete delegate parameter of
                // matching shape (this recursion is what distinguishes Func<Task> from
                // RequestDelegate in the next-thunk position of middleware).
                var p = invokeParams[i].ParameterType;
                if (
                    p == typeof(Delegate)
                    || p == typeof(MulticastDelegate)
                    || !typeof(Delegate).IsAssignableFrom(p)
                    || !DelegateShapeMatches(p, nestedFt)
                )
                    return false;
            }

        return true;
    }

    /// <summary>
    ///     Walk the substitution chain starting at <paramref name="original" /> and, if it
    ///     terminates in a concrete type, rebind the terminal id to <c>Object</c>. Used when
    ///     a type var inside a generic-arg slot gets unified against <c>Object</c>: without
    ///     this, the var stays bound to (e.g.) Float and the emitter produces an invariantly
    ///     wrong generic instantiation.
    /// </summary>
    private void WidenVarChainToObject(ZType original)
    {
        if (original is not ZType.ZTypeVar tv)
            return;
        WalkChainWideningToObject(tv.Id);
    }

    private void WalkChainWideningToObject(int id)
    {
        if (!subst.TryGet(id, out var resolved))
            return;
        if (resolved is ZType.ZTypeVar nextTv)
        {
            WalkChainWideningToObject(nextTv.Id);
            return;
        }

        // Terminal binding. Skip Unit (void-like) — nothing sensible to widen.
        if (resolved is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return;
        if (resolved is ZType.ZNamedType { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
            return;

        subst.Add(id, new ZType.ZNamedType("Object", new List<ZType>()));
    }

    private bool Bind(int varId, ZType type, SourceSpan span)
    {
        if (type is ZType.ZTypeVar tv && tv.Id == varId)
            return true;

        if (OccursIn(varId, type))
        {
            diagnostics.Error($"Infinite type: t{varId} occurs in {type}", span);
            return false;
        }

        subst.Add(varId, type);
        return true;
    }

    private bool BindConstrained(ZType.ZConstrainedVar cv, ZType target, SourceSpan span)
    {
        if (target is ZType.ZConstrainedVar other && other.Id == cv.Id)
            return true;

        if (target is ZType.ZTypeVar tv)
        {
            // Propagate constraint: bind the unconstrained var to the constrained var
            subst.Add(tv.Id, cv);
            return true;
        }

        if (target is ZType.ZConstrainedVar otherCv)
        {
            // Intersect constraints
            var intersection = cv.AllowedKinds.Intersect(otherCv.AllowedKinds).ToHashSet();
            if (intersection.Count == 0)
            {
                diagnostics.Error($"No common type between '{cv}' and '{otherCv}'", span);
                return false;
            }

            if (intersection.Count == 1)
            {
                // Resolved to a single concrete type
                var concrete = new ZType.ZPrimitiveType(intersection.First());
                subst.Add(cv.Id, concrete);
                subst.Add(otherCv.Id, concrete);
                return true;
            }

            // Create a new constrained var with the intersection
            var merged = new ZType.ZConstrainedVar(cv.Id, intersection);
            subst.Add(otherCv.Id, merged);
            return true;
        }

        if (target is ZType.ZPrimitiveType pt)
        {
            if (cv.AllowedKinds.Contains(pt.Kind))
            {
                subst.Add(cv.Id, target);
                return true;
            }

            var allowed = string.Join(", ", cv.AllowedKinds.OrderBy(k => k));
            diagnostics.Error(
                $"Type '{pt.Kind}' is not allowed here; expected one of: {allowed}",
                span
            );
            return false;
        }

        if (OccursIn(cv.Id, target))
        {
            diagnostics.Error($"Infinite type: t{cv.Id} occurs in {target}", span);
            return false;
        }

        var allowedKinds = string.Join(", ", cv.AllowedKinds.OrderBy(k => k));
        diagnostics.Error(
            $"Type '{target}' is not allowed here; expected one of: {allowedKinds}",
            span
        );
        return false;
    }

    private bool IsClrSubtype(string nameA, string nameB)
    {
        // Check ZScheme-defined classes first (not yet compiled to assemblies,
        // so CLR reflection won't find them)
        if (classInterfaceLookup is not null)
            if (IsZSchemeSubtype(nameA, nameB) || IsZSchemeSubtype(nameB, nameA))
                return true;

        try
        {
            var silentDiag = new DiagnosticBag();
            var clr = new ClrInterop(silentDiag, assemblySearchPaths);
            var typeA = FindClrType(clr, nameA);
            var typeB = FindClrType(clr, nameB);
            if (typeA is null || typeB is null)
                return false;

            if (typeB.IsAssignableFrom(typeA) || typeA.IsAssignableFrom(typeB))
                return true;

            // Fallback: check by interface name matching (handles cross-assembly type identity)
            if (typeB.IsInterface && typeA.GetInterfaces().Any(i => i.FullName == typeB.FullName))
                return true;
            if (typeA.IsInterface && typeB.GetInterfaces().Any(i => i.FullName == typeA.FullName))
                return true;
        }
        catch
        {
            // Ignore reflection errors during subtype checking
        }

        return false;
    }

    private Type? FindClrType(ClrInterop clr, string name)
    {
        // Try direct resolution first (full CLR type names)
        var type = clr.FindType(name);
        if (type is not null)
            return type;

        // For short names not found directly, try appending CLR namespace prefixes
        // (e.g., "HttpContext" -> "Microsoft.AspNetCore.Http.HttpContext")
        if (_clrNamespaces is not null && !name.Contains('.'))
        {
            foreach (var ns in _clrNamespaces)
            {
                var fullName = $"{ns}.{name}";
                type = clr.FindType(fullName);
                if (type is not null)
                    return type;
            }
        }

        return null;
    }

    /// <summary>
    ///     Check if className implements interfaceName by walking the ZScheme class hierarchy.
    /// </summary>
    private bool IsZSchemeSubtype(string className, string interfaceName)
    {
        var interfaces = classInterfaceLookup!(className);
        if (interfaces is null)
            return false;

        // Compare canonically: a class may declare `: ZWorld.GameServer.NPC.Behaviors.INpcBehavior`
        // while the use site says `INpcBehavior` (or the reverse). Both sides are CLR interface
        // names, so both canonicalize to the same full name.
        var target = _canonical(interfaceName);
        foreach (var declared in interfaces)
            if (declared == interfaceName || _canonical(declared) == target)
                return true;

        // Walk base class chain
        // classInterfaceLookup returns null for unknown classes, so this terminates
        return false;
    }

    private bool OccursIn(int varId, ZType type)
    {
        return type switch
        {
            ZType.ZTypeVar tv => tv.Id == varId,
            ZType.ZConstrainedVar cv => cv.Id == varId,
            ZType.ZFuncType ft => ft.Params.Any(p => OccursIn(varId, p))
                || OccursIn(varId, ft.Return),
            ZType.ZNamedType nt => nt.TypeArgs.Any(a => OccursIn(varId, a)),
            ZType.ZForAllType fa => !fa.BoundVars.Contains(varId) && OccursIn(varId, fa.Body),
            ZType.ZNullableType nt => OccursIn(varId, nt.Inner),
            ZType.ZDelegateType => false,
            _ => false,
        };
    }
}

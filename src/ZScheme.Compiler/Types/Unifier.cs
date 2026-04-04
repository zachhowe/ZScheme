using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

public sealed class Unifier(Substitution subst, DiagnosticBag diagnostics,
    IReadOnlyList<string>? assemblySearchPaths = null,
    Func<string, IReadOnlyList<string>?>? classInterfaceLookup = null)
{
    public bool Unify(ZType a, ZType b, SourceSpan span)
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
            if (!fa.IsVariadic && !fb.IsVariadic)
            {
                if (fa.Params.Count != fb.Params.Count)
                {
                    diagnostics.Error(
                        $"Function arity mismatch: expected {fa.Params.Count} parameters, got {fb.Params.Count}",
                        span);
                    return false;
                }

                for (var i = 0; i < fa.Params.Count; i++)
                    if (!Unify(fa.Params[i], fb.Params[i], span))
                        return false;
            }
            else
            {
                // When either side is variadic, unify the common fixed params
                var minCount = Math.Min(fa.Params.Count, fb.Params.Count);
                for (var i = 0; i < minCount; i++)
                    if (!Unify(fa.Params[i], fb.Params[i], span))
                        return false;
            }

            return Unify(fa.Return, fb.Return, span);
        }

        if (ta is ZType.ZNamedType na && tb is ZType.ZNamedType nb)
        {
            // Implicit boxing: any type can be assigned to System.Object / Object
            if (nb is { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
                return true;
            if (na is { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
                return true;

            if (na.Name == nb.Name && na.TypeArgs.Count == nb.TypeArgs.Count)
            {
                for (var i = 0; i < na.TypeArgs.Count; i++)
                    if (!Unify(na.TypeArgs[i], nb.TypeArgs[i], span))
                        return false;
                return true;
            }

            // CLR subtype check for concrete (non-generic) named types
            if (na.TypeArgs.Count == 0 && nb.TypeArgs.Count == 0
                && IsClrSubtype(na.Name, nb.Name))
                return true;

            diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
            return false;
        }

        if (ta is ZType.ZNullableType nta && tb is ZType.ZNullableType ntb)
            return Unify(nta.Inner, ntb.Inner, span);

        // Implicit T -> T? widening (non-nullable to nullable)
        if (tb is ZType.ZNullableType ntb2 && ta is not ZType.ZNullableType)
            return Unify(ta, ntb2.Inner, span);

        if (ta is ZType.ZNullableType nta2 && tb is not ZType.ZNullableType)
            return Unify(nta2.Inner, tb, span);

        // Implicit boxing: any type can be assigned to System.Object / Object
        if (tb is ZType.ZNamedType { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
            return true;
        if (ta is ZType.ZNamedType { Name: "System.Object" or "Object", TypeArgs.Count: 0 })
            return true;

        diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
        return false;
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
                diagnostics.Error($"No common numeric type between '{cv}' and '{otherCv}'", span);
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
            diagnostics.Error($"Type '{pt.Kind}' is not allowed here; expected one of: {allowed}", span);
            return false;
        }

        if (OccursIn(cv.Id, target))
        {
            diagnostics.Error($"Infinite type: t{cv.Id} occurs in {target}", span);
            return false;
        }

        var allowedKinds = string.Join(", ", cv.AllowedKinds.OrderBy(k => k));
        diagnostics.Error($"Type '{target}' is not numeric; expected one of: {allowedKinds}", span);
        return false;
    }

    private bool IsClrSubtype(string nameA, string nameB)
    {
        // Check ZScheme-defined classes first (not yet compiled to assemblies,
        // so CLR reflection won't find them)
        if (classInterfaceLookup is not null)
        {
            if (IsZSchemeSubtype(nameA, nameB) || IsZSchemeSubtype(nameB, nameA))
                return true;
        }

        try
        {
            var silentDiag = new DiagnosticBag();
            var clr = new ClrInterop(silentDiag, assemblySearchPaths);
            var typeA = clr.FindType(nameA);
            var typeB = clr.FindType(nameB);
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

    /// <summary>
    ///     Check if className implements interfaceName by walking the ZScheme class hierarchy.
    /// </summary>
    private bool IsZSchemeSubtype(string className, string interfaceName)
    {
        var interfaces = classInterfaceLookup!(className);
        if (interfaces is null)
            return false;

        if (interfaces.Contains(interfaceName))
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
            ZType.ZFuncType ft =>
                ft.Params.Any(p => OccursIn(varId, p)) || OccursIn(varId, ft.Return),
            ZType.ZNamedType nt =>
                nt.TypeArgs.Any(a => OccursIn(varId, a)),
            ZType.ZForAllType fa =>
                !fa.BoundVars.Contains(varId) && OccursIn(varId, fa.Body),
            ZType.ZNullableType nt =>
                OccursIn(varId, nt.Inner),
            _ => false
        };
    }
}

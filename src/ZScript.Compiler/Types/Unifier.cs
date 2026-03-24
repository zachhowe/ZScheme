using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Types;

public sealed class Unifier(Substitution subst, DiagnosticBag diagnostics)
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

            return Unify(fa.Return, fb.Return, span);
        }

        if (ta is ZType.ZNamedType na && tb is ZType.ZNamedType nb)
        {
            if (na.Name != nb.Name || na.TypeArgs.Count != nb.TypeArgs.Count)
            {
                diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
                return false;
            }

            for (var i = 0; i < na.TypeArgs.Count; i++)
                if (!Unify(na.TypeArgs[i], nb.TypeArgs[i], span))
                    return false;

            return true;
        }

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
            _ => false
        };
    }
}

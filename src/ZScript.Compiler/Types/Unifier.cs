namespace ZScript.Compiler.Types;

using ZScript.Compiler.Diagnostics;

public sealed class Unifier
{
    private readonly Substitution _subst;
    private readonly DiagnosticBag _diagnostics;

    public Unifier(Substitution subst, DiagnosticBag diagnostics)
    {
        _subst = subst;
        _diagnostics = diagnostics;
    }

    public Substitution Substitution => _subst;

    public bool Unify(ZType a, ZType b, SourceSpan span)
    {
        var ta = _subst.Apply(a);
        var tb = _subst.Apply(b);

        if (ta.Equals(tb))
            return true;

        if (ta is ZType.ZTypeVar tva)
            return Bind(tva.Id, tb, span);

        if (tb is ZType.ZTypeVar tvb)
            return Bind(tvb.Id, ta, span);

        if (ta is ZType.ZFuncType fa && tb is ZType.ZFuncType fb)
        {
            if (fa.Params.Count != fb.Params.Count)
            {
                _diagnostics.Error(
                    $"Function arity mismatch: expected {fa.Params.Count} parameters, got {fb.Params.Count}",
                    span);
                return false;
            }

            for (int i = 0; i < fa.Params.Count; i++)
            {
                if (!Unify(fa.Params[i], fb.Params[i], span))
                    return false;
            }

            return Unify(fa.Return, fb.Return, span);
        }

        if (ta is ZType.ZNamedType na && tb is ZType.ZNamedType nb)
        {
            if (na.Name != nb.Name || na.TypeArgs.Count != nb.TypeArgs.Count)
            {
                _diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
                return false;
            }

            for (int i = 0; i < na.TypeArgs.Count; i++)
            {
                if (!Unify(na.TypeArgs[i], nb.TypeArgs[i], span))
                    return false;
            }

            return true;
        }

        _diagnostics.Error($"Type mismatch: '{ta}' vs '{tb}'", span);
        return false;
    }

    private bool Bind(int varId, ZType type, SourceSpan span)
    {
        if (type is ZType.ZTypeVar tv && tv.Id == varId)
            return true;

        if (OccursIn(varId, type))
        {
            _diagnostics.Error($"Infinite type: t{varId} occurs in {type}", span);
            return false;
        }

        _subst.Add(varId, type);
        return true;
    }

    private bool OccursIn(int varId, ZType type) => type switch
    {
        ZType.ZTypeVar tv => tv.Id == varId,
        ZType.ZFuncType ft =>
            ft.Params.Any(p => OccursIn(varId, p)) || OccursIn(varId, ft.Return),
        ZType.ZNamedType nt =>
            nt.TypeArgs.Any(a => OccursIn(varId, a)),
        ZType.ZForAllType fa =>
            !fa.BoundVars.Contains(varId) && OccursIn(varId, fa.Body),
        _ => false
    };
}

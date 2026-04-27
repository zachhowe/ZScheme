namespace ZScheme.Compiler.Types;

public sealed class Substitution
{
    private readonly Dictionary<int, ZType> _map = new();

    public IReadOnlyDictionary<int, ZType> Mappings => _map;

    public void Add(int typeVarId, ZType type)
    {
        _map[typeVarId] = type;
    }

    public bool TryGet(int typeVarId, out ZType type)
    {
        return _map.TryGetValue(typeVarId, out type!);
    }

    public ZType Apply(ZType type)
    {
        return type switch
        {
            ZType.ZTypeVar tv =>
                _map.TryGetValue(tv.Id, out var resolved)
                    ? Apply(resolved) // chase the chain
                    : tv,
            ZType.ZConstrainedVar cv =>
                _map.TryGetValue(cv.Id, out var resolved)
                    ? Apply(resolved)
                    : cv,
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(Apply).ToList(),
                    Apply(ft.Return),
                    ft.IsVariadic),
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(Apply).ToList()),
            ZType.ZForAllType fa =>
                new ZType.ZForAllType(fa.BoundVars, ApplyShielded(fa.Body, fa.BoundVars)),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(Apply(nt.Inner)),
            _ => type
        };
    }

    private ZType ApplyShielded(ZType type, IReadOnlyList<int> shielded)
    {
        return type switch
        {
            ZType.ZTypeVar tv when shielded.Contains(tv.Id) => tv,
            ZType.ZConstrainedVar cv when shielded.Contains(cv.Id) => cv,
            ZType.ZTypeVar tv =>
                _map.TryGetValue(tv.Id, out var resolved)
                    ? ApplyShielded(resolved, shielded)
                    : tv,
            ZType.ZConstrainedVar cv =>
                _map.TryGetValue(cv.Id, out var resolved)
                    ? ApplyShielded(resolved, shielded)
                    : cv,
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => ApplyShielded(p, shielded)).ToList(),
                    ApplyShielded(ft.Return, shielded),
                    ft.IsVariadic),
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name,
                    nt.TypeArgs.Select(a => ApplyShielded(a, shielded)).ToList()),
            ZType.ZForAllType fa =>
                new ZType.ZForAllType(fa.BoundVars,
                    ApplyShielded(fa.Body, shielded.Concat(fa.BoundVars).ToList())),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(ApplyShielded(nt.Inner, shielded)),
            _ => type
        };
    }

    public void Compose(Substitution other)
    {
        // Apply existing substitution to all new mappings, then merge
        foreach (var (id, type) in other._map) _map[id] = Apply(type);
    }

    /// <summary>
    ///     Returns all free type variable IDs in a type.
    /// </summary>
    public static HashSet<int> FreeVars(ZType type)
    {
        return type switch
        {
            ZType.ZTypeVar tv => [tv.Id],
            ZType.ZConstrainedVar cv => [cv.Id],
            ZType.ZFuncType ft =>
                ft.Params.SelectMany(FreeVars).Concat(FreeVars(ft.Return)).ToHashSet(),
            ZType.ZNamedType nt =>
                nt.TypeArgs.SelectMany(FreeVars).ToHashSet(),
            ZType.ZForAllType fa =>
                FreeVarsForAll(fa),
            ZType.ZNullableType nt =>
                FreeVars(nt.Inner),
            _ => []
        };
    }

    private static HashSet<int> FreeVarsForAll(ZType.ZForAllType fa)
    {
        var fv = FreeVars(fa.Body);
        foreach (var bv in fa.BoundVars) fv.Remove(bv);
        return fv;
    }
}

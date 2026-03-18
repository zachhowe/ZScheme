namespace ZScript.Compiler.Types;

public sealed class Substitution
{
    private readonly Dictionary<int, ZType> _map = new();

    public void Add(int typeVarId, ZType type)
    {
        _map[typeVarId] = type;
    }

    public bool TryGet(int typeVarId, out ZType type) =>
        _map.TryGetValue(typeVarId, out type!);

    public ZType Apply(ZType type) => type switch
    {
        ZType.ZTypeVar tv =>
            _map.TryGetValue(tv.Id, out var resolved)
                ? Apply(resolved) // chase the chain
                : tv,
        ZType.ZFuncType ft =>
            new ZType.ZFuncType(
                ft.Params.Select(Apply).ToList(),
                Apply(ft.Return)),
        ZType.ZNamedType nt =>
            new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(Apply).ToList()),
        ZType.ZForAllType fa =>
            new ZType.ZForAllType(fa.BoundVars, Apply(fa.Body)),
        _ => type
    };

    public void Compose(Substitution other)
    {
        // Apply existing substitution to all new mappings, then merge
        foreach (var (id, type) in other._map)
        {
            _map[id] = Apply(type);
        }
    }

    public IReadOnlyDictionary<int, ZType> Mappings => _map;

    /// <summary>
    /// Returns all free type variable IDs in a type.
    /// </summary>
    public static HashSet<int> FreeVars(ZType type) => type switch
    {
        ZType.ZTypeVar tv => [tv.Id],
        ZType.ZFuncType ft =>
            ft.Params.SelectMany(FreeVars).Concat(FreeVars(ft.Return)).ToHashSet(),
        ZType.ZNamedType nt =>
            nt.TypeArgs.SelectMany(FreeVars).ToHashSet(),
        ZType.ZForAllType fa =>
            FreeVarsForAll(fa),
        _ => []
    };

    private static HashSet<int> FreeVarsForAll(ZType.ZForAllType fa)
    {
        var fv = FreeVars(fa.Body);
        foreach (var bv in fa.BoundVars) fv.Remove(bv);
        return fv;
    }
}

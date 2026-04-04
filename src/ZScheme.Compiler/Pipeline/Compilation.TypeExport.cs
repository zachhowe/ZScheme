using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    /// <summary>
    ///     Converts named types that look like type parameters (single lowercase letters)
    ///     into proper ForAll-wrapped type variables for cross-module use.
    ///     e.g. Fn(a, a) → ForAll([1000], Fn(tv1000, tv1000))
    /// </summary>
    private static ZType GeneralizeForExport(ZType type)
    {
        var typeParamNames = new HashSet<string>();
        CollectTypeParamNames(type, typeParamNames);

        if (typeParamNames.Count == 0)
            return type;

        var nextId = 1000;
        var mapping = new Dictionary<string, int>();
        foreach (var name in typeParamNames.OrderBy(n => n))
            mapping[name] = nextId++;

        var replaced = ReplaceTypeParamNames(type, mapping);
        return new ZType.ZForAllType(mapping.Values.ToList(), replaced);
    }

    private static void CollectTypeParamNames(ZType type, HashSet<string> names)
    {
        switch (type)
        {
            case ZType.ZNamedType { TypeArgs.Count: 0 } nt when IsTypeParamName(nt.Name):
                names.Add(nt.Name);
                break;
            case ZType.ZFuncType ft:
                foreach (var p in ft.Params) CollectTypeParamNames(p, names);
                CollectTypeParamNames(ft.Return, names);
                break;
            case ZType.ZNamedType nt:
                foreach (var a in nt.TypeArgs) CollectTypeParamNames(a, names);
                break;
            case ZType.ZForAllType fa:
                CollectTypeParamNames(fa.Body, names);
                break;
        }
    }

    private static bool IsTypeParamName(string name)
    {
        return name.Length == 1 && char.IsLower(name[0]);
    }

    private static ZType ReplaceTypeParamNames(ZType type, Dictionary<string, int> mapping)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt when mapping.TryGetValue(nt.Name, out var id) =>
                new ZType.ZTypeVar(id),
            ZType.ZFuncType ft =>
                new ZType.ZFuncType(
                    ft.Params.Select(p => ReplaceTypeParamNames(p, mapping)).ToList(),
                    ReplaceTypeParamNames(ft.Return, mapping)),
            ZType.ZNamedType nt =>
                new ZType.ZNamedType(nt.Name, nt.TypeArgs.Select(a => ReplaceTypeParamNames(a, mapping)).ToList()),
            ZType.ZForAllType fa =>
                new ZType.ZForAllType(fa.BoundVars, ReplaceTypeParamNames(fa.Body, mapping)),
            ZType.ZNullableType nt =>
                new ZType.ZNullableType(ReplaceTypeParamNames(nt.Inner, mapping)),
            _ => type
        };
    }
}

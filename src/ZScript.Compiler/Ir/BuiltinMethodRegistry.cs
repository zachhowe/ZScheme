namespace ZScript.Compiler.Ir;

using System.Reflection;
using ZScript.Runtime;

public sealed record CollectionMethodInfo(string CSharpName, bool IsProperty, bool IsIndexer);

public static class BuiltinMethodRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, CollectionMethodInfo>> LazyMethods = new(Build);

    public static IReadOnlyDictionary<string, CollectionMethodInfo> CollectionMethods => LazyMethods.Value;

    private static IReadOnlyDictionary<string, CollectionMethodInfo> Build()
    {
        var dict = new Dictionary<string, CollectionMethodInfo>();
        var types = new[] { typeof(ZsList<>), typeof(ZsVector<>), typeof(ZsMap<,>) };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            foreach (var prop in type.GetProperties(flags))
            {
                var attr = prop.GetCustomAttribute<ZsBuiltinAttribute>();
                if (attr is null) continue;
                var name = attr.IsIndexer ? "" : prop.Name;
                dict[attr.Name] = new CollectionMethodInfo(name, IsProperty: true, attr.IsIndexer);
            }

            foreach (var method in type.GetMethods(flags))
            {
                var attr = method.GetCustomAttribute<ZsBuiltinAttribute>();
                if (attr is null) continue;
                dict[attr.Name] = new CollectionMethodInfo(method.Name, IsProperty: false, IsIndexer: false);
            }
        }

        return dict;
    }
}

namespace ZScript.Runtime;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ZsBuiltinAttribute : Attribute
{
    public string Name { get; }
    public bool IsIndexer { get; init; }
    public ZsBuiltinAttribute(string name) => Name = name;
}

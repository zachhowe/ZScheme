namespace ZScript.Runtime;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class ZsBuiltinAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public bool IsIndexer { get; init; }
}

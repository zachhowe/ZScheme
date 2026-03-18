namespace ZScript.Runtime;

public sealed class ZsUnit
{
    public static readonly ZsUnit Value = new();

    private ZsUnit() { }

    public override string ToString() => "()";

    public override bool Equals(object? obj) => obj is ZsUnit;
    public override int GetHashCode() => 0;
}

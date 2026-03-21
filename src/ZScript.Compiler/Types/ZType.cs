namespace ZScript.Compiler.Types;

public enum PrimitiveKind
{
    Int,
    Long,
    Float,
    Double,
    Byte,
    Char,
    Bool,
    String,
    Unit
}

public abstract record ZType
{
    public sealed record ZTypeVar(int Id) : ZType
    {
        public override string ToString() => $"t{Id}";
    }

    public sealed record ZPrimitiveType(PrimitiveKind Kind) : ZType
    {
        public override string ToString() => Kind.ToString();
    }

    public sealed record ZFuncType(IReadOnlyList<ZType> Params, ZType Return) : ZType
    {
        public override string ToString()
        {
            var pars = string.Join(", ", Params);
            return $"({pars}) -> {Return}";
        }
    }

    public sealed record ZNamedType(string Name, IReadOnlyList<ZType> TypeArgs) : ZType
    {
        public override string ToString()
        {
            if (TypeArgs.Count == 0) return Name;
            var args = string.Join(", ", TypeArgs);
            return $"{Name}<{args}>";
        }
    }

    public sealed record ZForAllType(IReadOnlyList<int> BoundVars, ZType Body) : ZType
    {
        public override string ToString()
        {
            var vars = string.Join(", ", BoundVars.Select(v => $"t{v}"));
            return $"forall {vars}. {Body}";
        }
    }

    public sealed record ZConstrainedVar(int Id, IReadOnlySet<PrimitiveKind> AllowedKinds) : ZType
    {
        public override string ToString()
        {
            var kinds = string.Join("|", AllowedKinds.OrderBy(k => k).Select(k => k.ToString()));
            return $"t{Id}:{{{kinds}}}";
        }
    }

    // Common type instances
    public static readonly ZType Int = new ZPrimitiveType(PrimitiveKind.Int);
    public static readonly ZType Long = new ZPrimitiveType(PrimitiveKind.Long);
    public static readonly ZType Float = new ZPrimitiveType(PrimitiveKind.Float);
    public static readonly ZType Double = new ZPrimitiveType(PrimitiveKind.Double);
    public static readonly ZType Byte = new ZPrimitiveType(PrimitiveKind.Byte);
    public static readonly ZType Char = new ZPrimitiveType(PrimitiveKind.Char);
    public static readonly ZType Bool = new ZPrimitiveType(PrimitiveKind.Bool);
    public static readonly ZType String = new ZPrimitiveType(PrimitiveKind.String);
    public static readonly ZType Unit = new ZPrimitiveType(PrimitiveKind.Unit);
}

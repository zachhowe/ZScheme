using System.Text;

namespace ZScheme.Compiler.Types;

[Flags]
public enum GenericConstraintKind
{
    None = 0,
    NotNull = 1 << 0,
    Struct = 1 << 1,
    Class = 1 << 2,
    New = 1 << 3,
    Unmanaged = 1 << 4,
    Default = 1 << 5,
}

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
    Unit,
}

public abstract record ZType
{
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

    public static ZType Tuple(params ZType[] elements)
    {
        return new ZNamedType("ValueTuple", elements);
    }

    /// <summary>
    ///     Render a type as a human-readable string. Type variables are rendered as
    ///     <c>^a, ^b, ^c, ...</c> — distinct ids get distinct names, and the same
    ///     id reuses its name within the formatted expression.
    /// </summary>
    public static string Format(ZType type)
    {
        var sb = new StringBuilder();
        var names = new Dictionary<int, string>();
        // Pre-seed bound variables of an outer ZForAllType so they get names in
        // declaration order regardless of the order the body mentions them.
        if (type is ZForAllType forall)
            foreach (var id in forall.BoundVars)
                NameForId(id, names);
        AppendTo(sb, type, names);
        return sb.ToString();
    }

    private static void AppendTo(StringBuilder sb, ZType t, Dictionary<int, string> names)
    {
        switch (t)
        {
            case ZTypeVar v:
                sb.Append(NameForId(v.Id, names));
                break;
            case ZPrimitiveType p:
                sb.Append(p.Kind.ToString());
                break;
            case ZFuncType f:
                sb.Append('(');
                for (var i = 0; i < f.Params.Count; i++)
                {
                    AppendTo(sb, f.Params[i], names);
                    if (i == f.Params.Count - 1 && f.IsVariadic)
                        sb.Append("...");
                    sb.Append(' ');
                }

                sb.Append("-> ");
                AppendTo(sb, f.Return, names);
                sb.Append(')');
                break;
            case ZNamedType n:
                if (n.Name == "ValueTuple" && n.TypeArgs.Count > 0)
                {
                    sb.Append('(');
                    for (var i = 0; i < n.TypeArgs.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(" * ");
                        AppendTo(sb, n.TypeArgs[i], names);
                    }

                    sb.Append(')');
                }
                else if (n.TypeArgs.Count == 0)
                {
                    sb.Append(n.Name);
                }
                else
                {
                    sb.Append(n.Name);
                    sb.Append('<');
                    for (var i = 0; i < n.TypeArgs.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        AppendTo(sb, n.TypeArgs[i], names);
                    }

                    sb.Append('>');
                }

                break;
            case ZForAllType fa:
                sb.Append("forall ");
                for (var i = 0; i < fa.BoundVars.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(NameForId(fa.BoundVars[i], names));
                }

                sb.Append(". ");
                AppendTo(sb, fa.Body, names);
                break;
            case ZConstrainedVar c:
                sb.Append(NameForId(c.Id, names));
                sb.Append(":{");
                sb.Append(
                    string.Join("|", c.AllowedKinds.OrderBy(k => k).Select(k => k.ToString()))
                );
                sb.Append('}');
                break;
            case ZNullableType nu:
                AppendTo(sb, nu.Inner, names);
                sb.Append('?');
                break;
            case ZDelegateType dt:
                sb.Append("(delegate ");
                sb.Append(dt.ClrTypeName);
                sb.Append(')');
                break;
            default:
                sb.Append(t.GetType().Name);
                break;
        }
    }

    private static string NameForId(int id, Dictionary<int, string> names)
    {
        if (names.TryGetValue(id, out var existing))
            return existing;
        var name = NameForIndex(names.Count);
        names[id] = name;
        return name;
    }

    private static string NameForIndex(int index)
    {
        var letter = (char)('a' + index % 26);
        var suffix = index / 26;
        return suffix == 0 ? $"^{letter}" : $"^{letter}{suffix}";
    }

    public sealed record ZTypeVar(int Id) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZPrimitiveType(PrimitiveKind Kind) : ZType
    {
        public override string ToString()
        {
            return Kind.ToString();
        }
    }

    public sealed record ZFuncType(
        IReadOnlyList<ZType> Params,
        ZType Return,
        bool IsVariadic = false
    ) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZNamedType(string Name, IReadOnlyList<ZType> TypeArgs) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZForAllType(IReadOnlyList<int> BoundVars, ZType Body) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZConstrainedVar(int Id, IReadOnlySet<PrimitiveKind> AllowedKinds) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZNullableType(ZType Inner) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }

    public sealed record ZDelegateType(string ClrTypeName) : ZType
    {
        public override string ToString()
        {
            return Format(this);
        }
    }
}

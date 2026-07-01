using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Repl;

public static class ReplValueFormatter
{
    public static string Format(object? value, ZType? type = null)
    {
        if (value is null)
            return "null";

        if (type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return "()";

        return value switch
        {
            bool b => b ? "#t" : "#f",
            char c => $"#\\{c}",
            string s => FormatString(s),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable num
                when value is byte or sbyte or short or ushort or int or uint or long or ulong =>
                num.ToString(null, CultureInfo.InvariantCulture),
            ITuple tuple => FormatTuple(tuple),
            IDictionary dict => FormatDictionary(dict),
            IEnumerable seq when IsImmutableCollection(value) => FormatSequence(seq, value),
            _ => FormatObject(value),
        };
    }

    private static bool IsImmutableCollection(object value)
    {
        var ns = value.GetType().Namespace;
        return ns is not null && ns.StartsWith("System.Collections.Immutable");
    }

    private static string FormatString(string s)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var c in s)
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }

        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatTuple(ITuple tuple)
    {
        if (tuple.Length == 0)
            return "()";
        var parts = new string[tuple.Length];
        for (var i = 0; i < tuple.Length; i++)
            parts[i] = Format(tuple[i]);
        return $"({string.Join(", ", parts)})";
    }

    private static string FormatSequence(IEnumerable seq, object original)
    {
        var typeName = original.GetType().Name;
        var isSet =
            typeName.StartsWith("ImmutableHashSet") || typeName.StartsWith("ImmutableSortedSet");
        var open = isSet ? "#{" : "(";
        var close = isSet ? "}" : ")";
        var items = new List<string>();
        foreach (var item in seq)
            items.Add(Format(item));
        return open + string.Join(" ", items) + close;
    }

    private static string FormatDictionary(IDictionary dict)
    {
        var items = new List<string>();
        foreach (DictionaryEntry entry in dict)
            items.Add($"{Format(entry.Key)}: {Format(entry.Value)}");
        return "{" + string.Join(", ", items) + "}";
    }

    // Fallback for arbitrary user types. C# `record` types have a custom ToString;
    // ZScheme-emitted records/unions are plain classes without one, so the default
    // ToString returns the type name. Detect that case and format via reflection.
    private static string FormatObject(object value)
    {
        var defaultText = value.ToString();
        var type = value.GetType();
        if (string.IsNullOrEmpty(defaultText) || defaultText == type.ToString())
            return FormatViaReflection(value, type);
        return defaultText!;
    }

    private static string FormatViaReflection(object value, Type type)
    {
        var typeName = StripGenericArity(type.Name);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .ToList();

        if (props.Count == 0)
            return typeName;

        var parts = new List<string>();
        foreach (var p in props)
        {
            object? val;
            try
            {
                val = p.GetValue(value);
            }
            catch
            {
                continue;
            }

            parts.Add($"{p.Name} = {Format(val)}");
        }

        return $"{typeName} {{ {string.Join(", ", parts)} }}";
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}

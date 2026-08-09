namespace ZScheme.Compiler.Types;

/// <summary>
///     The type-expression spellings that denote a <see cref="ZType.ZPrimitiveType" /> rather than
///     a <see cref="ZType.ZNamedType" />. Each primitive answers both to its ZScheme keyword and to
///     the full name of the CLR type it compiles to, so an interop signature may spell
///     <c>System.Int32</c> alongside its fully-qualified neighbours and still unify with an
///     <c>Int</c> written elsewhere.
///     <para>
///         Aliasing the two spellings onto one <see cref="ZType" /> here, at parse time, is what
///         keeps the rest of the pipeline out of it: <see cref="Unifier" /> has no
///         primitive-to-named bridge, and IR lowering and both emitters only ever see the single
///         representation. That is the same strategy <see cref="TypeNameCanonicalizer" /> uses for
///         named types, one layer down — the split here is in the <em>representation</em>, not in
///         the naming, so canonicalizing a <c>ZNamedType.Name</c> cannot reach it.
///     </para>
///     <para>
///         <c>Symbol</c> is keyword-only: <c>ZSymbol</c> is a runtime type, not a CLR primitive, so
///         there is no second spelling to alias. <c>System.Object</c> is deliberately absent — it is
///         not a primitive, and <see cref="Unifier" /> already has boxing arms that match it in
///         either spelling.
///     </para>
/// </summary>
public static class PrimitiveTypeNames
{
    private static readonly Dictionary<string, ZType> ByName = new(StringComparer.Ordinal)
    {
        ["Int"] = ZType.Int,
        ["System.Int32"] = ZType.Int,
        ["Long"] = ZType.Long,
        ["System.Int64"] = ZType.Long,
        ["Float"] = ZType.Float,
        ["System.Single"] = ZType.Float,
        ["Double"] = ZType.Double,
        ["System.Double"] = ZType.Double,
        ["Byte"] = ZType.Byte,
        ["System.Byte"] = ZType.Byte,
        ["Char"] = ZType.Char,
        ["System.Char"] = ZType.Char,
        ["Bool"] = ZType.Bool,
        ["System.Boolean"] = ZType.Bool,
        ["String"] = ZType.String,
        ["System.String"] = ZType.String,
        ["Unit"] = ZType.Unit,
        ["System.Void"] = ZType.Unit,
        ["Symbol"] = ZType.Symbol,
    };

    /// <summary>
    ///     The primitive <paramref name="name" /> denotes in a type position, or null when it is an
    ///     ordinary named type.
    /// </summary>
    public static ZType? Lookup(string name)
    {
        return ByName.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Whether dropping a namespace qualifier — rewriting <paramref name="qualifiedName" /> to
    ///     <paramref name="shortName" /> — leaves the annotation's type untouched.
    ///     <para>
    ///         A keyword is mapped straight to its primitive without consulting the namespace hints,
    ///         so the rewrite is only safe when the qualified spelling names that same primitive:
    ///         <c>System.Char</c> shortens to <c>Char</c> and keeps its type, while some other
    ///         namespace's <c>Char</c> would be silently replaced by the primitive. Names that are
    ///         not primitives at all are none of this method's business and pass.
    ///     </para>
    /// </summary>
    public static bool ShorteningPreservesType(string qualifiedName, string shortName)
    {
        var shortPrimitive = Lookup(shortName);
        return shortPrimitive is null || shortPrimitive == Lookup(qualifiedName);
    }
}

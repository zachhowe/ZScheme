using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Emit-time metadata about union cases, shared by both backends via
///     <see cref="PatternResolver" />. Owns the <c>"Union.Case"</c> → field-type-template
///     table and the case → owning-union reverse map, and computes the concrete
///     <see cref="ZType" /> a constructor pattern's field sees after substituting the
///     scrutinee's type arguments.
///
///     Populated during IR lowering: from local <c>define-union</c> decls
///     (<see cref="IrLowering" />) and from imported modules' <see cref="IrNode.UnionDecl" />
///     definitions. Holds only <see cref="ZType" /> data — no backend or AsmResolver types —
///     so it is directly unit-testable.
///
///     This is deliberately a *resolution* facility, not a decision-tree compiler: it tells
///     each backend which union a case belongs to and what type each extracted field has, and
///     leaves match *emission* to the backend. Centralizing it removes the duplicated,
///     historically-divergent resolution logic the two emitters used to carry.
/// </summary>
public sealed class UnionCaseRegistry
{
    // "Union.Case" -> (union type params, per-field type templates).
    private readonly Dictionary<
        string,
        (IReadOnlyList<string> TypeParams, IReadOnlyList<ZType> FieldTypes)
    > _caseFields = new();

    // Case name -> owning union name. Used as a fallback when the scrutinee type is a bare
    // type variable (so its name does not resolve a "Union.Case" key directly).
    private readonly Dictionary<string, string> _caseToUnion = new();

    /// <summary>Registers a single union case's field-type templates.</summary>
    public void Register(
        string unionName,
        string caseName,
        IReadOnlyList<string> typeParams,
        IReadOnlyList<ZType> fieldTypes
    )
    {
        _caseFields[$"{unionName}.{caseName}"] = (typeParams, fieldTypes);
        _caseToUnion[caseName] = unionName;
    }

    /// <summary>Registers every case of a union declaration.</summary>
    public void RegisterUnion(IrNode.UnionDecl union)
    {
        foreach (var c in union.Cases)
            Register(union.Name, c.Name, union.TypeParams, c.Fields.Select(f => f.Type).ToList());
    }

    /// <summary>
    ///     Registers a record/struct as a single-case "union" keyed by its own name, so a
    ///     <c>(RecordName field ...)</c> constructor pattern resolves its field types the same
    ///     way a union case does — including literal field sub-patterns, which the IL backend
    ///     only emits a test for when the field type resolves.
    /// </summary>
    public void RegisterRecord(IrNode.RecordDecl record)
    {
        Register(
            record.Name,
            record.Name,
            record.TypeParams,
            record.Fields.Select(f => f.Type).ToList()
        );
    }

    /// <summary>
    ///     The union a constructor pattern's case belongs to, preferring the scrutinee's own
    ///     type name and falling back to the case → union map when the scrutinee is a bare
    ///     type variable. Null when the case name is unknown.
    /// </summary>
    public string? ResolveUnion(ZType? scrutineeType, string caseName)
    {
        if (
            scrutineeType is ZType.ZNamedType named
            && _caseFields.ContainsKey($"{named.Name}.{caseName}")
        )
            return named.Name;
        return _caseToUnion.TryGetValue(caseName, out var union) ? union : null;
    }

    /// <summary>
    ///     The concrete <see cref="ZType" /> the <paramref name="fieldIndex" />th field of a
    ///     constructor pattern sees, after substituting the scrutinee's type arguments into the
    ///     case's field template. Null when the case or field cannot be resolved. When the case
    ///     has type parameters but the scrutinee carries no type arguments (e.g. a bare type
    ///     variable), the unsubstituted template is returned unchanged.
    /// </summary>
    public ZType? FieldType(ZType? scrutineeType, string caseName, int fieldIndex)
    {
        var unionName = ResolveUnion(scrutineeType, caseName);
        if (unionName is null)
            return null;
        if (!_caseFields.TryGetValue($"{unionName}.{caseName}", out var entry))
            return null;
        if (fieldIndex >= entry.FieldTypes.Count)
            return null;

        var template = entry.FieldTypes[fieldIndex];
        var typeArgs = scrutineeType is ZType.ZNamedType nt ? nt.TypeArgs : [];
        if (entry.TypeParams.Count == 0 || typeArgs.Count == 0)
            return template;

        var subst = new Dictionary<string, ZType>();
        for (var i = 0; i < entry.TypeParams.Count && i < typeArgs.Count; i++)
            subst[entry.TypeParams[i]] = typeArgs[i];
        return SubstituteTypeParams(template, subst);
    }

    /// <summary>
    ///     Substitutes type-parameter names for concrete types throughout a <see cref="ZType" />.
    ///     Previously duplicated verbatim in both the C# and IL emitters.
    /// </summary>
    public static ZType SubstituteTypeParams(ZType type, IReadOnlyDictionary<string, ZType> map)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt
                when map.TryGetValue(nt.Name, out var mapped) => mapped,
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(p => SubstituteTypeParams(p, map)).ToList(),
                SubstituteTypeParams(ft.Return, map),
                ft.IsVariadic
            ),
            ZType.ZNullableType nn => new ZType.ZNullableType(SubstituteTypeParams(nn.Inner, map)),
            _ => type,
        };
    }
}

namespace ZScheme.Fuzzer.Generation;

// A declared generic union. TypeParams are the scheme type-vars (e.g. "^a").
// Each constructor carries a name and the list of type-var slots its fields occupy.
//
// IsFieldSelfRecursive[i] is true when field i's type is the union itself
// applied to its type params (e.g. `(FUn_0 ^a)` inside FUn_0's own ctor) —
// these are the slots where match-arm generation can emit a nested ctor
// pattern. When that list is shorter than FieldTypeParams, missing entries
// default to false. Empty/omitted is equivalent to "no self-recursion."
public sealed record UserUnionCtor(
    string Name,
    IReadOnlyList<string> FieldTypeParams,
    IReadOnlyList<bool>? IsFieldSelfRecursive = null
);

public sealed record UserUnionDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<UserUnionCtor> Ctors,
    string Definition
);

// A declared record (or struct) — generic or non-generic. Each field has a name
// and the type-var it occupies (or a ground type name like "Int" for non-generic).
// IsValueType is true when emitted with the `struct` keyword, which produces a
// .NET value type. Accessors and `with`-form syntax are identical for both, so
// downstream consumers (WithExprGenerator, ExprGenerator) treat them uniformly.
public sealed record UserRecordField(string Name, string TypeParam);

public sealed record UserRecordDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<UserRecordField> Fields,
    string Definition,
    bool IsValueType = false
);

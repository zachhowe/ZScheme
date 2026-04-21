namespace ZScheme.Fuzzer.Generation;

// A declared generic union. TypeParams are the scheme type-vars (e.g. "^a").
// Each constructor carries a name and the list of type-var slots its fields occupy.
public sealed record UserUnionCtor(string Name, IReadOnlyList<string> FieldTypeParams);

public sealed record UserUnionDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<UserUnionCtor> Ctors,
    string Definition);

// A declared generic record. Each field has a name and the type-var it occupies.
public sealed record UserRecordField(string Name, string TypeParam);

public sealed record UserRecordDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<UserRecordField> Fields,
    string Definition);

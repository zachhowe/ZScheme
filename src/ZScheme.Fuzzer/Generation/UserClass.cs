namespace ZScheme.Fuzzer.Generation;

public sealed record UserClassDecl(
    string Name,
    IReadOnlyList<UserClassField> Fields,
    IReadOnlyList<ExprType> ConstructorParamTypes,
    string Definition);

public sealed record UserClassField(string Name, bool IsMutable);

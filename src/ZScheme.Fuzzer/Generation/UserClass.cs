namespace ZScheme.Fuzzer.Generation;

public sealed record UserClassDecl(
    string Name,
    IReadOnlyList<UserClassField> Fields,
    IReadOnlyList<ExprType> ConstructorParamTypes,
    IReadOnlyList<UserClassMethod> Methods,
    bool IsOpen,
    string? BaseName,
    IReadOnlyList<string> ImplementedInterfaces,
    string Definition);

public sealed record UserClassField(string Name, bool IsMutable);

// All current methods take Int params and return Int — keeps call sites and
// override compatibility trivial. RetType is kept explicit so future generators
// can introduce other return types without touching call sites.
public sealed record UserClassMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType);

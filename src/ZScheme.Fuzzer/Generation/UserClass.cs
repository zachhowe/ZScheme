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
// can introduce other return types without touching call sites. IsAsync marks
// methods emitted as `(define-async ... : (Task Int) ...)`; ConstructAndCallToInt
// and EmitInstanceImportClrBlock skip those (sync-context call sites can't await).
public sealed record UserClassMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType,
    bool IsAsync = false);

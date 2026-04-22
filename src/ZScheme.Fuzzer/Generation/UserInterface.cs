namespace ZScheme.Fuzzer.Generation;

// A declared interface and the method signatures classes/objects must implement.
// All methods are Int-returning over Int params so implementing classes can reuse
// the existing ExprGenerator without new machinery.
public sealed record UserInterfaceDecl(
    string Name,
    IReadOnlyList<UserInterfaceMethod> Methods,
    string Definition);

public sealed record UserInterfaceMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType);

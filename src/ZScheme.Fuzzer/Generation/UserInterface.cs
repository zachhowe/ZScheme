namespace ZScheme.Fuzzer.Generation;

// A declared interface and the method signatures classes/objects must implement.
// Method params/returns range over the ground ExprTypes (Int-biased, plus
// Bool/Float); implementers dispatch bodies via ExprGenerator.GenTyped.
public sealed record UserInterfaceDecl(
    string Name,
    IReadOnlyList<UserInterfaceMethod> Methods,
    string Definition
);

public sealed record UserInterfaceMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType
);

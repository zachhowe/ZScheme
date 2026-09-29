namespace ZScheme.Fuzzer.Generation;

// A declared interface and the method signatures classes/objects must implement.
// Method params/returns range over the ground ExprTypes (Int-biased, plus
// Bool/Float); implementers dispatch bodies via ExprGenerator.GenTyped.
// BaseNames lists the interfaces this one extends (`(interface IChild : IBase ...)`),
// in declaration order — an implementing class must supply methods for the whole
// transitive chain (see ClassExprGenerator.CollectChainMethods), and inherited
// members get interface accessors of their own (compiler fix: interface
// inheritance used to resolve one edge deep).
public sealed record UserInterfaceDecl(
    string Name,
    IReadOnlyList<UserInterfaceMethod> Methods,
    string Definition,
    IReadOnlyList<string>? BaseNames = null
)
{
    public IReadOnlyList<string> InterfaceBaseNames => BaseNames ?? [];
}

public sealed record UserInterfaceMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType
);

namespace ZScheme.Fuzzer.Generation;

public enum UserFuncKind { Regular, Recursive, HigherOrder, Generic }

public sealed record UserFunc(
    string Name,
    UserFuncKind Kind,
    List<ExprType> ParamTypes,
    string Definition);

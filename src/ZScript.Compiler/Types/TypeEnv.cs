namespace ZScript.Compiler.Types;

public sealed class TypeEnv
{
    private readonly Dictionary<string, ZType> _bindings = new();
    private readonly TypeEnv? _parent;

    public TypeEnv(TypeEnv? parent = null)
    {
        _parent = parent;
    }

    public static TypeEnv CreateRoot()
    {
        var env = new TypeEnv();

        // Arithmetic operators: (Int, Int) -> Int
        var intBinOp = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int);
        env.Define("+", intBinOp);
        env.Define("-", intBinOp);
        env.Define("*", intBinOp);
        env.Define("/", intBinOp);
        env.Define("%", intBinOp);

        // Comparison operators: (Int, Int) -> Bool
        var intCmpOp = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Bool);
        env.Define("=", intCmpOp);
        env.Define("!=", intCmpOp);
        env.Define("<", intCmpOp);
        env.Define(">", intCmpOp);
        env.Define("<=", intCmpOp);
        env.Define(">=", intCmpOp);

        // Boolean operators
        var boolBinOp = new ZType.ZFuncType([ZType.Bool, ZType.Bool], ZType.Bool);
        env.Define("and", boolBinOp);
        env.Define("or", boolBinOp);
        env.Define("not", new ZType.ZFuncType([ZType.Bool], ZType.Bool));

        // String concatenation
        env.Define("string-append", new ZType.ZFuncType([ZType.String, ZType.String], ZType.String));

        // Conversion functions
        env.Define("int->float", new ZType.ZFuncType([ZType.Int], ZType.Float));
        env.Define("float->int", new ZType.ZFuncType([ZType.Float], ZType.Int));
        env.Define("int->string", new ZType.ZFuncType([ZType.Int], ZType.String));

        return env;
    }

    public TypeEnv CreateChild() => new(this);

    public void Define(string name, ZType type)
    {
        _bindings[name] = type;
    }

    public ZType? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var type))
            return type;
        return _parent?.Lookup(name);
    }

    public bool Contains(string name) => Lookup(name) is not null;
}

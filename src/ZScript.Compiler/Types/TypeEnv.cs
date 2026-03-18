namespace ZScript.Compiler.Types;

public sealed record BuiltinCtorInfo(string RuntimeType, string? CaseName);

public sealed class TypeEnv
{
    private readonly Dictionary<string, ZType> _bindings = new();
    private readonly Dictionary<string, BuiltinCtorInfo> _builtinCtors = new();
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

        // Result constructors: Ok : forall [a, e]. (a) -> Result<a, e>
        var okA = new ZType.ZTypeVar(9000);
        var okE = new ZType.ZTypeVar(9001);
        var okResultType = new ZType.ZNamedType("Result", [okA, okE]);
        env.Define("Ok", new ZType.ZForAllType([9000, 9001],
            new ZType.ZFuncType([okA], okResultType)));
        env.DefineBuiltinCtor("Ok", new BuiltinCtorInfo("ZsResult", "Ok"));

        // Err : forall [a, e]. (e) -> Result<a, e>
        var errA = new ZType.ZTypeVar(9002);
        var errE = new ZType.ZTypeVar(9003);
        var errResultType = new ZType.ZNamedType("Result", [errA, errE]);
        env.Define("Err", new ZType.ZForAllType([9002, 9003],
            new ZType.ZFuncType([errE], errResultType)));
        env.DefineBuiltinCtor("Err", new BuiltinCtorInfo("ZsResult", "Err"));

        // Some : forall [a]. (a) -> Option<a>
        var someA = new ZType.ZTypeVar(9004);
        var someOptionType = new ZType.ZNamedType("Option", [someA]);
        env.Define("Some", new ZType.ZForAllType([9004],
            new ZType.ZFuncType([someA], someOptionType)));
        env.DefineBuiltinCtor("Some", new BuiltinCtorInfo("ZsOption", "Some"));

        // None : forall [a]. Option<a> (nullary)
        var noneA = new ZType.ZTypeVar(9005);
        var noneOptionType = new ZType.ZNamedType("Option", [noneA]);
        env.Define("None", new ZType.ZForAllType([9005], noneOptionType));
        env.DefineBuiltinCtor("None", new BuiltinCtorInfo("ZsOption", "None"));

        // Error : (String) -> Error
        var errorType = new ZType.ZNamedType("Error", []);
        env.Define("Error", new ZType.ZFuncType([ZType.String], errorType));
        env.DefineBuiltinCtor("Error", new BuiltinCtorInfo("ZsError", null));

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

    public void DefineBuiltinCtor(string name, BuiltinCtorInfo info) => _builtinCtors[name] = info;

    public BuiltinCtorInfo? LookupBuiltinCtor(string name)
    {
        if (_builtinCtors.TryGetValue(name, out var info)) return info;
        return _parent?.LookupBuiltinCtor(name);
    }
}

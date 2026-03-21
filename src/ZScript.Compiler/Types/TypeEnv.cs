namespace ZScript.Compiler.Types;

public sealed record BuiltinCtorInfo(string RuntimeType, string? CaseName);

public sealed class TypeEnv(TypeEnv? parent = null)
{
    private readonly Dictionary<string, ZType> _bindings = new();
    private readonly Dictionary<string, BuiltinCtorInfo> _builtinCtors = new();

    public static TypeEnv CreateRoot()
    {
        var env = new TypeEnv();

        // Arithmetic operators: forall a:{Int,Float}. (a, a) -> a
        IReadOnlySet<PrimitiveKind> numericKinds = new HashSet<PrimitiveKind> { PrimitiveKind.Int, PrimitiveKind.Float };
        var arithOps = new[] { "+", "-", "*", "/" };
        for (int i = 0; i < arithOps.Length; i++)
        {
            var numVar = new ZType.ZConstrainedVar(9200 + i, numericKinds);
            env.Define(arithOps[i], new ZType.ZForAllType([numVar.Id],
                new ZType.ZFuncType([numVar, numVar], numVar)));
        }

        // Modulo: (Int, Int) -> Int
        env.Define("%", new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int));

        // Ordered comparison operators: forall a:{Int,Float}. (a, a) -> Bool
        var ordOps = new[] { "<", ">", "<=", ">=" };
        for (int i = 0; i < ordOps.Length; i++)
        {
            var cmpVar = new ZType.ZConstrainedVar(9210 + i, numericKinds);
            env.Define(ordOps[i], new ZType.ZForAllType([cmpVar.Id],
                new ZType.ZFuncType([cmpVar, cmpVar], ZType.Bool)));
        }

        // Equality operators: (Int, Int) -> Bool (for now)
        var intCmpOp = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Bool);
        env.Define("=", intCmpOp);
        env.Define("!=", intCmpOp);

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

        // --- Collection method type signatures ---

        // list/head : forall [a]. (List<a>) -> a
        var lhA = new ZType.ZTypeVar(9100);
        env.Define("list/head", new ZType.ZForAllType([9100],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [lhA])], lhA)));

        // list/tail : forall [a]. (List<a>) -> List<a>
        var ltA = new ZType.ZTypeVar(9101);
        var ltList = new ZType.ZNamedType("List", [ltA]);
        env.Define("list/tail", new ZType.ZForAllType([9101],
            new ZType.ZFuncType([ltList], ltList)));

        // list/count : forall [a]. (List<a>) -> Int
        var lcA = new ZType.ZTypeVar(9102);
        env.Define("list/count", new ZType.ZForAllType([9102],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [lcA])], ZType.Int)));

        // list/empty? : forall [a]. (List<a>) -> Bool
        var leA = new ZType.ZTypeVar(9103);
        env.Define("list/empty?", new ZType.ZForAllType([9103],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [leA])], ZType.Bool)));

        // list/cons : forall [a]. (List<a>, a) -> List<a>
        var lcnA = new ZType.ZTypeVar(9104);
        var lcnList = new ZType.ZNamedType("List", [lcnA]);
        env.Define("list/cons", new ZType.ZForAllType([9104],
            new ZType.ZFuncType([lcnList, lcnA], lcnList)));

        // list/append : forall [a]. (List<a>, a) -> List<a>
        var laA = new ZType.ZTypeVar(9105);
        var laList = new ZType.ZNamedType("List", [laA]);
        env.Define("list/append", new ZType.ZForAllType([9105],
            new ZType.ZFuncType([laList, laA], laList)));

        // list/concat : forall [a]. (List<a>, List<a>) -> List<a>
        var lccA = new ZType.ZTypeVar(9106);
        var lccList = new ZType.ZNamedType("List", [lccA]);
        env.Define("list/concat", new ZType.ZForAllType([9106],
            new ZType.ZFuncType([lccList, lccList], lccList)));

        // list/map : forall [a, b]. (List<a>, (a -> b)) -> List<b>
        var lmA = new ZType.ZTypeVar(9107);
        var lmB = new ZType.ZTypeVar(9108);
        env.Define("list/map", new ZType.ZForAllType([9107, 9108],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [lmA]), new ZType.ZFuncType([lmA], lmB)],
                new ZType.ZNamedType("List", [lmB]))));

        // list/filter : forall [a]. (List<a>, (a -> Bool)) -> List<a>
        var lfA = new ZType.ZTypeVar(9109);
        var lfList = new ZType.ZNamedType("List", [lfA]);
        env.Define("list/filter", new ZType.ZForAllType([9109],
            new ZType.ZFuncType([lfList, new ZType.ZFuncType([lfA], ZType.Bool)], lfList)));

        // list/fold : forall [a, b]. (List<a>, b, (b, a -> b)) -> b
        var lfdA = new ZType.ZTypeVar(9110);
        var lfdB = new ZType.ZTypeVar(9111);
        env.Define("list/fold", new ZType.ZForAllType([9110, 9111],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [lfdA]), lfdB, new ZType.ZFuncType([lfdB, lfdA], lfdB)],
                lfdB)));

        // list/nth : forall [a]. (List<a>, Int) -> a
        var lnA = new ZType.ZTypeVar(9112);
        env.Define("list/nth", new ZType.ZForAllType([9112],
            new ZType.ZFuncType([new ZType.ZNamedType("List", [lnA]), ZType.Int], lnA)));

        // vector/count : forall [a]. (Vector<a>) -> Int
        var vcA = new ZType.ZTypeVar(9113);
        env.Define("vector/count", new ZType.ZForAllType([9113],
            new ZType.ZFuncType([new ZType.ZNamedType("Vector", [vcA])], ZType.Int)));

        // vector/empty? : forall [a]. (Vector<a>) -> Bool
        var veA = new ZType.ZTypeVar(9114);
        env.Define("vector/empty?", new ZType.ZForAllType([9114],
            new ZType.ZFuncType([new ZType.ZNamedType("Vector", [veA])], ZType.Bool)));

        // vector/append : forall [a]. (Vector<a>, a) -> Vector<a>
        var vaA = new ZType.ZTypeVar(9115);
        var vaVec = new ZType.ZNamedType("Vector", [vaA]);
        env.Define("vector/append", new ZType.ZForAllType([9115],
            new ZType.ZFuncType([vaVec, vaA], vaVec)));

        // vector/set : forall [a]. (Vector<a>, Int, a) -> Vector<a>
        var vsA = new ZType.ZTypeVar(9116);
        var vsVec = new ZType.ZNamedType("Vector", [vsA]);
        env.Define("vector/set", new ZType.ZForAllType([9116],
            new ZType.ZFuncType([vsVec, ZType.Int, vsA], vsVec)));

        // vector/map : forall [a, b]. (Vector<a>, (a -> b)) -> Vector<b>
        var vmA = new ZType.ZTypeVar(9117);
        var vmB = new ZType.ZTypeVar(9118);
        env.Define("vector/map", new ZType.ZForAllType([9117, 9118],
            new ZType.ZFuncType([new ZType.ZNamedType("Vector", [vmA]), new ZType.ZFuncType([vmA], vmB)],
                new ZType.ZNamedType("Vector", [vmB]))));

        // vector/filter : forall [a]. (Vector<a>, (a -> Bool)) -> Vector<a>
        var vfA = new ZType.ZTypeVar(9119);
        var vfVec = new ZType.ZNamedType("Vector", [vfA]);
        env.Define("vector/filter", new ZType.ZForAllType([9119],
            new ZType.ZFuncType([vfVec, new ZType.ZFuncType([vfA], ZType.Bool)], vfVec)));

        // vector/fold : forall [a, b]. (Vector<a>, b, (b, a -> b)) -> b
        var vfdA = new ZType.ZTypeVar(9120);
        var vfdB = new ZType.ZTypeVar(9121);
        env.Define("vector/fold", new ZType.ZForAllType([9120, 9121],
            new ZType.ZFuncType([new ZType.ZNamedType("Vector", [vfdA]), vfdB, new ZType.ZFuncType([vfdB, vfdA], vfdB)],
                vfdB)));

        // vector/nth : forall [a]. (Vector<a>, Int) -> a
        var vnA = new ZType.ZTypeVar(9122);
        env.Define("vector/nth", new ZType.ZForAllType([9122],
            new ZType.ZFuncType([new ZType.ZNamedType("Vector", [vnA]), ZType.Int], vnA)));

        // map/count : forall [k, v]. (Map<k, v>) -> Int
        var mcK = new ZType.ZTypeVar(9123);
        var mcV = new ZType.ZTypeVar(9124);
        env.Define("map/count", new ZType.ZForAllType([9123, 9124],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mcK, mcV])], ZType.Int)));

        // map/empty? : forall [k, v]. (Map<k, v>) -> Bool
        var meK = new ZType.ZTypeVar(9125);
        var meV = new ZType.ZTypeVar(9126);
        env.Define("map/empty?", new ZType.ZForAllType([9125, 9126],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [meK, meV])], ZType.Bool)));

        // map/keys : forall [k, v]. (Map<k, v>) -> List<k>
        var mkK = new ZType.ZTypeVar(9127);
        var mkV = new ZType.ZTypeVar(9128);
        env.Define("map/keys", new ZType.ZForAllType([9127, 9128],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mkK, mkV])],
                new ZType.ZNamedType("List", [mkK]))));

        // map/values : forall [k, v]. (Map<k, v>) -> List<v>
        var mvK = new ZType.ZTypeVar(9129);
        var mvV = new ZType.ZTypeVar(9130);
        env.Define("map/values", new ZType.ZForAllType([9129, 9130],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mvK, mvV])],
                new ZType.ZNamedType("List", [mvV]))));

        // map/get : forall [k, v]. (Map<k, v>, k) -> Option<v>
        var mgK = new ZType.ZTypeVar(9131);
        var mgV = new ZType.ZTypeVar(9132);
        env.Define("map/get", new ZType.ZForAllType([9131, 9132],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mgK, mgV]), mgK],
                new ZType.ZNamedType("Option", [mgV]))));

        // map/put : forall [k, v]. (Map<k, v>, k, v) -> Map<k, v>
        var mpK = new ZType.ZTypeVar(9133);
        var mpV = new ZType.ZTypeVar(9134);
        var mpMap = new ZType.ZNamedType("Map", [mpK, mpV]);
        env.Define("map/put", new ZType.ZForAllType([9133, 9134],
            new ZType.ZFuncType([mpMap, mpK, mpV], mpMap)));

        // map/remove : forall [k, v]. (Map<k, v>, k) -> Map<k, v>
        var mrK = new ZType.ZTypeVar(9135);
        var mrV = new ZType.ZTypeVar(9136);
        var mrMap = new ZType.ZNamedType("Map", [mrK, mrV]);
        env.Define("map/remove", new ZType.ZForAllType([9135, 9136],
            new ZType.ZFuncType([mrMap, mrK], mrMap)));

        // map/contains-key? : forall [k, v]. (Map<k, v>, k) -> Bool
        var mckK = new ZType.ZTypeVar(9137);
        var mckV = new ZType.ZTypeVar(9138);
        env.Define("map/contains-key?", new ZType.ZForAllType([9137, 9138],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mckK, mckV]), mckK], ZType.Bool)));

        // map/nth : forall [k, v]. (Map<k, v>, k) -> v
        var mnK = new ZType.ZTypeVar(9139);
        var mnV = new ZType.ZTypeVar(9140);
        env.Define("map/nth", new ZType.ZForAllType([9139, 9140],
            new ZType.ZFuncType([new ZType.ZNamedType("Map", [mnK, mnV]), mnK], mnV)));

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
        return parent?.Lookup(name);
    }

    public bool Contains(string name) => Lookup(name) is not null;

    public void DefineBuiltinCtor(string name, BuiltinCtorInfo info) => _builtinCtors[name] = info;

    public BuiltinCtorInfo? LookupBuiltinCtor(string name)
    {
        if (_builtinCtors.TryGetValue(name, out var info)) return info;
        return parent?.LookupBuiltinCtor(name);
    }
}

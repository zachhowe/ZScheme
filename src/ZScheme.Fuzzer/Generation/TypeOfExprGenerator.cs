namespace ZScheme.Fuzzer.Generation;

public sealed class TypeOfExprGenerator
{
    private readonly GeneratorContext _ctx;

    public TypeOfExprGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    public string GenTypeExpr()
    {
        var weights = new List<(int Weight, Func<string> Gen)>();

        weights.Add((3, () => GenPrimitiveTypeExpr()));
        weights.Add((2, () => GenNullableTypeExpr()));
        weights.Add((2, () => GenTupleTypeExpr()));

        if (_ctx.Imports.Contains(StdlibImport.Option))
            weights.Add((2, () => GenStdlibGenericType("Option", ["Int"])));
        if (_ctx.Imports.Contains(StdlibImport.List))
            weights.Add((2, () => GenStdlibGenericType("List", ["Int"])));
        if (_ctx.Imports.Contains(StdlibImport.Result))
            weights.Add((2, () => GenStdlibGenericType("Result", ["Int", "String"])));
        if (_ctx.Imports.Contains(StdlibImport.Vector))
            weights.Add((2, () => GenStdlibGenericType("Vector", ["Int"])));
        if (_ctx.Imports.Contains(StdlibImport.Hash))
            weights.Add((2, () => GenStdlibGenericType("Hash", ["Int", "Int"])));
        if (_ctx.Imports.Contains(StdlibImport.TreeList))
            weights.Add((2, () => GenStdlibGenericType("TreeList", ["Int"])));

        foreach (var r in _ctx.UserRecords)
        {
            if (r.TypeParams.Count == 0)
                weights.Add((1, () => r.Name));
            else
            {
                var args = string.Join(" ", Enumerable.Repeat("Int", r.TypeParams.Count));
                weights.Add((1, () => $"({r.Name} {args})"));
            }
        }

        foreach (var u in _ctx.UserUnions)
        {
            if (u.TypeParams.Count == 0)
                weights.Add((1, () => u.Name));
            else
            {
                var args = string.Join(" ", Enumerable.Repeat("Int", u.TypeParams.Count));
                weights.Add((1, () => $"({u.Name} {args})"));
            }
        }

        foreach (var c in _ctx.UserClasses)
            weights.Add((1, () => c.Name));

        foreach (var i in _ctx.UserInterfaces)
            weights.Add((1, () => i.Name));

        return _ctx.PickWeighted(weights)();
    }

    public string GenTypeOf() => $"(typeof {GenTypeExpr()})";

    // Emits a `typeof` expression in statement position, binding its
    // System.Type result to a fresh name and discarding it (the let body is an
    // Int literal), so the surrounding compute function's Int return type is
    // preserved.
    public string GenTypeOfDiscard()
    {
        var name = _ctx.Fresh();
        return $"(let ([{name} {GenTypeOf()}]) {_ctx.Rng.Next(0, 100)})";
    }

    private string GenPrimitiveTypeExpr()
    {
        var types = new[]
        {
            "Int",
            "Long",
            "Float",
            "Double",
            "Byte",
            "Char",
            "Bool",
            "String",
            "Unit",
        };
        return types[_ctx.Rng.Next(types.Length)];
    }

    private string GenNullableTypeExpr()
    {
        var types = new[] { "Int", "Long", "Float", "Double", "Byte", "Char", "Bool" };
        return $"{types[_ctx.Rng.Next(types.Length)]}?";
    }

    private string GenTupleTypeExpr()
    {
        var types = new[] { "Int", "Long", "Float", "Double", "Byte", "Char", "Bool", "String" };
        var arity = _ctx.Rng.Next(2, 4);
        var tupleTypes = new List<string>();
        for (var i = 0; i < arity; i++)
            tupleTypes.Add(types[_ctx.Rng.Next(types.Length)]);
        return $"({string.Join(" * ", tupleTypes)})";
    }

    private string GenStdlibGenericType(string name, IReadOnlyList<string> typeArgs)
    {
        if (typeArgs.Count == 0)
            return name;
        return $"({name} {string.Join(" ", typeArgs)})";
    }
}

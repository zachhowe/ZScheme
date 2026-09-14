namespace ZScheme.Fuzzer.Generation;

// Emits non-generic structs like:
//   (struct SRec_0 [x : Int] [y : Int])
//   (struct SRec_0 [first : Int] [second : Int] [third : Int])
//
// Generic struct generation lives in UserTypeGenerator alongside generic records
// (where struct vs record is just a keyword flip). This generator covers the
// non-generic value-type path which previously had no fuzzer coverage. The
// resulting decl is added to _ctx.UserRecords so existing consumers
// (WithExprGenerator, GenUserRecordAccess) pick it up uniformly — accessors
// and `with` syntax are identical between record and struct.
public sealed class StructTypeGenerator
{
    private readonly GeneratorContext _ctx;

    public StructTypeGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    public UserRecordDecl GenerateStruct(int index)
    {
        var name = $"SRec_{index}";
        var fieldCount = 2 + _ctx.Rng.Next(2); // 2 or 3 fields

        // ~Half the structs carry one #:mutable field so SetFieldExprGenerator has
        // a value-type target: struct set! exercises the lvalue materialization the
        // record form never reaches.
        var mutableAt = _ctx.Rng.NextDouble() < 0.5 ? _ctx.Rng.Next(fieldCount) : -1;

        var fields = new List<UserRecordField>(fieldCount);
        var defParts = new List<string>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var fieldName =
                fieldCount == 2
                    ? i == 0
                        ? "x"
                        : "y"
                    : $"f{i}";
            var isMutable = i == mutableAt;
            fields.Add(new UserRecordField(fieldName, "Int", isMutable));
            defParts.Add(isMutable ? $"[{fieldName} : Int #:mutable]" : $"[{fieldName} : Int]");
        }

        var def = $"(struct {name} {string.Join(" ", defParts)})";
        return new UserRecordDecl(
            name,
            [], // non-generic
            fields,
            def,
            true
        );
    }
}

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

    public StructTypeGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public UserRecordDecl GenerateStruct(int index)
    {
        var name = $"SRec_{index}";
        var fieldCount = 2 + _ctx.Rng.Next(2); // 2 or 3 fields

        var fields = new List<UserRecordField>(fieldCount);
        var defParts = new List<string>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var fieldName = fieldCount == 2
                ? (i == 0 ? "x" : "y")
                : $"f{i}";
            fields.Add(new UserRecordField(fieldName, "Int"));
            defParts.Add($"[{fieldName} : Int]");
        }

        var def = $"(struct {name} {string.Join(" ", defParts)})";
        return new UserRecordDecl(
            name,
            [], // non-generic
            fields,
            def,
            IsValueType: true);
    }
}

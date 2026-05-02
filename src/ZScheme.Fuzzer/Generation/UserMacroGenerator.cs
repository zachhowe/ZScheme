namespace ZScheme.Fuzzer.Generation;

// Emits a single `define-syntax` macro plus an adjacent invocation of it.
// Currently uses one shape — a `define-dto`-style wrapper around `record` —
// since macro expansion happens at parse time and the resulting AST node is a
// regular RecordDecl, we register the generated record into _ctx.UserRecords
// so existing accessor / `with` generators pick it up uniformly.
//
// Macro and call site are emitted adjacently in ProgramGenerator (no orphan
// macros) so define-syntax phase ordering against the use site is guaranteed
// at the source level.
public sealed class UserMacroGenerator
{
    private readonly GeneratorContext _ctx;

    public UserMacroGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public string GenerateMacroAndUse(out UserRecordDecl generatedRecord)
    {
        var macroName = $"fuzz-mk-rec-{_ctx.Rng.Next(1000)}";
        var recName = $"MRec_{_ctx.Rng.Next(1000)}";
        var fieldCount = 2 + _ctx.Rng.Next(2);
        var fields = new List<UserRecordField>(fieldCount);
        var fieldDecls = new List<string>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var fname = fieldCount == 2 ? (i == 0 ? "x" : "y") : $"f{i}";
            fields.Add(new UserRecordField(fname, "Int"));
            fieldDecls.Add($"[{fname} : Int]");
        }

        // Macro: `(name field ...) -> (record name field ...)` — the simplest
        // shape that exercises define-syntax + syntax-rules + macro expansion
        // into a record decl.
        var macroDef = $"(define-syntax {macroName}\n  (syntax-rules ()\n    [({macroName} name field ...)\n     (record name field ...)]))";
        var useSite = $"({macroName} {recName} {string.Join(" ", fieldDecls)})";

        generatedRecord = new UserRecordDecl(
            recName,
            [], // non-generic
            fields,
            useSite, // Definition is the macro use site, not the expansion
            IsValueType: false);

        return macroDef + "\n\n" + useSite;
    }
}

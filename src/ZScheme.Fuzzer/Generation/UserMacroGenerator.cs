namespace ZScheme.Fuzzer.Generation;

// Emits `define-syntax` macros plus their adjacent invocations / registrations.
// Two flavors:
//
//  * Record-producing macros (e.g. `define-dto`) — expansion is a record decl.
//    The generated record is registered into _ctx.UserRecords so existing
//    accessor / `with` generators pick it up uniformly.
//
//  * Expression macros (e.g. `when`, `let1`, `min2`) — expansion is an Int-valued
//    expression. The macro's name + Int-positional arity are registered into
//    _ctx.MacroIntCallables so ExprGenerator's GenInt emits use sites at random
//    expression positions.
//
// Macro definitions are emitted at the top of the module (well before `compute`)
// so define-syntax phase ordering against the use site is guaranteed at the
// source level.
public sealed class UserMacroGenerator
{
    private readonly GeneratorContext _ctx;

    public UserMacroGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    // Single record-producing macro use site, returned as a complete top-level
    // block (define-syntax + invocation). Caller is responsible for adding the
    // generated record into _ctx.UserRecords.
    public string GenerateMacroAndUse(out UserRecordDecl generatedRecord)
    {
        var macroName = $"fuzz-mk-rec-{_ctx.Rng.Next(1000)}";
        var recName = $"MRec_{_ctx.Rng.Next(1000)}";
        var fieldCount = 2 + _ctx.Rng.Next(2);
        var fields = new List<UserRecordField>(fieldCount);
        var fieldDecls = new List<string>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var fname =
                fieldCount == 2
                    ? i == 0
                        ? "x"
                        : "y"
                    : $"f{i}";
            fields.Add(new UserRecordField(fname, "Int"));
            fieldDecls.Add($"[{fname} : Int]");
        }

        // Macro: `(name field ...) -> (define-record name field ...)` — the simplest
        // shape that exercises define-syntax + syntax-rules + macro expansion
        // into a record decl.
        var macroDef =
            $"(define-syntax {macroName}\n  (syntax-rules ()\n    [({macroName} name field ...)\n     (define-record name field ...)]))";
        var useSite = $"({macroName} {recName} {string.Join(" ", fieldDecls)})";

        generatedRecord = new UserRecordDecl(
            recName,
            [], // non-generic
            fields,
            useSite
        );

        return macroDef + "\n\n" + useSite;
    }

    // Generates 0–N expression-macro definitions and registers their names in
    // _ctx.MacroIntCallables. Returns the concatenated `(define-syntax ...)`
    // block; caller emits it adjacent to other top-level forms. None of these
    // shapes need a co-located use site — ExprGenerator emits invocations at
    // arbitrary Int positions inside `compute` and user funcs.
    public string GenerateExpressionMacros()
    {
        var blocks = new List<string>();
        // Each shape rolls independently. Probabilities chosen to keep the
        // bundle modest in size — 1-2 macros per case is typical.
        if (_ctx.Rng.NextDouble() < 0.55)
            blocks.Add(EmitWhenMacro());
        if (_ctx.Rng.NextDouble() < 0.45)
            blocks.Add(EmitLet1Macro());
        if (_ctx.Rng.NextDouble() < 0.40)
            blocks.Add(EmitMin2Macro());
        if (_ctx.Rng.NextDouble() < 0.35)
            blocks.Add(EmitSumMacro());
        if (_ctx.Rng.NextDouble() < 0.30)
            blocks.Add(EmitLitDispatchMacro());
        if (_ctx.Rng.NextDouble() < 0.25)
            blocks.Add(EmitHygieneMacro());
        return blocks.Count == 0 ? string.Empty : string.Join("\n\n", blocks);
    }

    // (fuzz-when-N cond body) → (if cond body 0). Two Int positions: the cond
    // expands to a Bool / Int (we'll always supply a Bool); the body is Int.
    // Registered with arity = 1 (just the body slot is Int; the cond is Bool
    // and is handled specially in the use-site emitter).
    private string EmitWhenMacro()
    {
        var name = $"fuzz-when-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules ()\n    [({name} cond body)\n     (if cond body 0)]))";
        // Negative arity sentinel here would be cleaner; instead we encode
        // shape via a special name prefix scanned by ExprGenerator. The
        // when/let1/min2 shapes are differentiated in the dispatcher.
        _ctx.MacroIntCallables.Add((name, -1)); // -1 == when shape
        return def;
    }

    // (fuzz-let1-N x v body) → (let* ([x v]) body). Bind a fresh name in the
    // body context. Body is Int; v is Int.
    private string EmitLet1Macro()
    {
        var name = $"fuzz-let1-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules ()\n    [({name} x v body)\n     (let* ([x v]) body)]))";
        _ctx.MacroIntCallables.Add((name, -2)); // -2 == let1 shape
        return def;
    }

    // (fuzz-min2-N a b) → (if (< a b) a b). Two Int args, returns Int.
    private string EmitMin2Macro()
    {
        var name = $"fuzz-min2-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules ()\n    [({name} a b)\n     (if (< a b) a b)]))";
        _ctx.MacroIntCallables.Add((name, 2)); // positive == direct Int arity
        return def;
    }

    // Recursive ellipsis macro: multi-rule, self-expanding sum over 1-4 Int
    // args. Exercises ellipsis patterns AND templates plus recursive expansion
    // (well under MacroExpander's depth cap).
    //   (fuzz-sum-N a)       → a
    //   (fuzz-sum-N a b ...) → (+ a (fuzz-sum-N b ...))
    private string EmitSumMacro()
    {
        var name = $"fuzz-sum-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules ()\n"
            + $"    [({name} a) a]\n"
            + $"    [({name} a b ...) (+ a ({name} b ...))]))";
        _ctx.MacroIntCallables.Add((name, -3)); // -3 == ellipsis-sum shape
        return def;
    }

    // Literal-identifier macro: `plus` / `minus` are syntax-rules literals that
    // must match verbatim at the use site, selecting the rule.
    private string EmitLitDispatchMacro()
    {
        var name = $"fuzz-lit-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules (plus minus)\n"
            + $"    [({name} plus a b) (+ a b)]\n"
            + $"    [({name} minus a b) (- a b)]))";
        _ctx.MacroIntCallables.Add((name, -4)); // -4 == literal-dispatch shape
        return def;
    }

    // Hygiene-stress macro: the template introduces a binding whose name uses
    // the generator's own fresh-name shape (`x0`), adjacent to whatever the
    // caller has in scope. With a textual (non-hygienic) expander both backends
    // read the macro's x0 — divergence would mean the backends disagree on
    // binding resolution through expansion.
    //   (fuzz-hyg-N body) → (let* ([x0 42]) (+ x0 body))
    private string EmitHygieneMacro()
    {
        var name = $"fuzz-hyg-{_ctx.Rng.Next(10000)}";
        var def =
            $"(define-syntax {name}\n  (syntax-rules ()\n"
            + $"    [({name} body) (let* ([x0 42]) (+ x0 body))]))";
        _ctx.MacroIntCallables.Add((name, -5)); // -5 == hygiene shape
        return def;
    }
}

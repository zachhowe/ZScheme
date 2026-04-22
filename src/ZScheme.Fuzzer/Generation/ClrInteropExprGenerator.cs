namespace ZScheme.Fuzzer.Generation;

// Enumerates the fixed palette of CLR bindings the fuzzer may emit.
// All entries are either static methods or :instance-property getters, both
// of which are known-working end-to-end on both backends. :instance methods
// are intentionally excluded (pre-existing IL-codegen issue). The narrow palette
// keeps each binding's call site trivially typeable.
public enum ClrBinding
{
    MathAbsInt,          // (Fn [Int] Int)        — default Math.Abs resolves to Int
    MathMinInt,          // (Fn [Int Int] Int)
    MathMaxInt,          // (Fn [Int Int] Int)
    MathSqrt,            // (Fn [Float] Float)
    MathAbsFloat,        // (Fn [Float] Float)    — explicit annotation disambiguates
    StringIsNullOrEmpty, // (Fn [String] Bool)
    StringLength,        // :instance-property (Fn [String] Int)
}

// Emits `(import-clr ...)` declarations for a random per-case subset of ClrBinding
// entries and provides reducers that invoke each via its alias name. Alias names
// are prefixed `fuzz-` to steer clear of the xN identifier namespace used by the
// rest of the generators.
public sealed class ClrInteropExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ClrInteropExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Per-case: pick a random subset of bindings to emit.
    public void ChooseBindings()
    {
        if (_ctx.Rng.NextDouble() < 0.35) _ctx.EmittedClrBindings.Add(ClrBinding.MathAbsInt);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.EmittedClrBindings.Add(ClrBinding.MathMinInt);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.EmittedClrBindings.Add(ClrBinding.MathMaxInt);
        if (_ctx.Rng.NextDouble() < 0.25) _ctx.EmittedClrBindings.Add(ClrBinding.MathSqrt);
        if (_ctx.Rng.NextDouble() < 0.20) _ctx.EmittedClrBindings.Add(ClrBinding.MathAbsFloat);
        if (_ctx.Rng.NextDouble() < 0.25) _ctx.EmittedClrBindings.Add(ClrBinding.StringIsNullOrEmpty);
        if (_ctx.Rng.NextDouble() < 0.25) _ctx.EmittedClrBindings.Add(ClrBinding.StringLength);
    }

    // Emits the (import-clr ...) block covering all selected bindings, or empty
    // string if no bindings were selected. Sorted by enum order for stable output.
    public string EmitImportBlock()
    {
        if (_ctx.EmittedClrBindings.Count == 0) return string.Empty;
        var ordered = _ctx.EmittedClrBindings.OrderBy(b => (int)b).ToList();
        var lines = new List<string> { "(import-clr" };
        for (var i = 0; i < ordered.Count; i++)
        {
            var form = "  " + BindingFormText(ordered[i]);
            if (i == ordered.Count - 1) form += ")";
            lines.Add(form);
        }
        return string.Join("\n", lines);
    }

    private static string BindingFormText(ClrBinding b) => b switch
    {
        // Explicit annotations are required to pin overload resolution.
        // Bare `System.Math/Abs` defaults to the sbyte/Byte overload, causing
        // a Byte-vs-Int mismatch at every call site.
        ClrBinding.MathAbsInt => "[fuzz-abs-int System.Math/Abs : (Fn [Int] Int)]",
        ClrBinding.MathMinInt => "[fuzz-min-int System.Math/Min : (Fn [Int Int] Int)]",
        ClrBinding.MathMaxInt => "[fuzz-max-int System.Math/Max : (Fn [Int Int] Int)]",
        // System.Math.Sqrt returns Double (64-bit), which maps to ZScheme's Double,
        // not Float (32-bit). Using Double in the annotation and converting call
        // sites with float->double / double->float keeps the types consistent.
        ClrBinding.MathSqrt => "[fuzz-sqrt System.Math/Sqrt : (Fn [Double] Double)]",
        ClrBinding.MathAbsFloat => "[fuzz-abs-flt System.Math/Abs : (Fn [Double] Double)]",
        ClrBinding.StringIsNullOrEmpty =>
            "[fuzz-str-empty? System.String/IsNullOrEmpty : (Fn [String] Bool)]",
        ClrBinding.StringLength =>
            "[fuzz-str-len System.String.Length :instance-property : (Fn [String] Int)]",
        _ => throw new InvalidOperationException($"Unknown binding: {b}")
    };

    public string ReduceMathAbsIntToInt(Scope scope, int depth) =>
        $"(fuzz-abs-int {_exprs.GenInt(scope, depth - 1)})";

    public string ReduceMathMinIntToInt(Scope scope, int depth) =>
        $"(fuzz-min-int {_exprs.GenInt(scope, depth - 1)} {_exprs.GenInt(scope, depth - 1)})";

    public string ReduceMathMaxIntToInt(Scope scope, int depth) =>
        $"(fuzz-max-int {_exprs.GenInt(scope, depth - 1)} {_exprs.GenInt(scope, depth - 1)})";

    public string ReduceStringLengthToInt(Scope scope, int depth) =>
        $"(fuzz-str-len {_exprs.GenString(scope, depth - 1)})";

    // fuzz-sqrt and fuzz-abs-flt both bind to Double overloads (Float-overloads
    // of System.Math.Sqrt / System.Math.Abs don't exist as the default resolution).
    // Wrap the call site with float->double / double->float so the reducer returns
    // Float and slots into GenFloat's weight table.
    public string ReduceMathSqrtToFloat(Scope scope, int depth) =>
        $"(double->float (fuzz-sqrt (float->double {_exprs.GenFloat(scope, depth - 1)})))";

    public string ReduceMathAbsFloatToFloat(Scope scope, int depth) =>
        $"(double->float (fuzz-abs-flt (float->double {_exprs.GenFloat(scope, depth - 1)})))";

    public string ReduceStringIsEmptyToBool(Scope scope, int depth) =>
        $"(fuzz-str-empty? {_exprs.GenString(scope, depth - 1)})";
}

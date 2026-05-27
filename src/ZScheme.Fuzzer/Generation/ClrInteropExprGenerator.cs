namespace ZScheme.Fuzzer.Generation;

// Enumerates the fixed palette of CLR bindings the fuzzer may emit.
// All entries are either static methods or :instance-property getters, both
// of which are known-working end-to-end on both backends. :instance methods
// are intentionally excluded (pre-existing IL-codegen issue). The narrow palette
// keeps each binding's call site trivially typeable.
public enum ClrBinding
{
    MathAbsInt, // (Int -> Int)        — default Math.Abs resolves to Int
    MathMinInt, // (Int Int -> Int)
    MathMaxInt, // (Int Int -> Int)
    MathSqrt, // (Float -> Float)
    MathAbsFloat, // (Float -> Float)    — explicit annotation disambiguates
    StringIsNullOrEmpty, // (String -> Bool)
    StringLength, // :instance-property (String -> Int)
    StringIndexer, // :instance-indexer (String Int -> Char)
    ConvertCharToInt, // (Char -> Int)  — overload-pinned to Char
    ConvertIntToLong, // (Int -> Long)
    ConvertLongToInt, // (Long -> Int)
    ConvertIntToByte, // (Int -> Byte)
    ConvertByteToInt, // (Byte -> Int)

    Int32TryParse // out-param: (String -> (ValueTuple Bool Int)) — exercises the
    // automatic out-parameter → ValueTuple synthesis path.
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

        // String indexer + Char conversion are entangled: indexer returns Char,
        // and the only way to round-trip Char back to Int (no native ZScheme
        // conversion exists) is through Convert.ToInt32(char). Always emit both
        // or neither.
        // String indexer surfaces an IL-backend bug (Indexer not found on
        // System.String). Kept at low probability so the artifact stream still
        // contains the repro shape but isn't dominated by it.
        if (_ctx.Rng.NextDouble() < 0.05)
        {
            _ctx.EmittedClrBindings.Add(ClrBinding.StringIndexer);
            _ctx.EmittedClrBindings.Add(ClrBinding.ConvertCharToInt);
        }

        // Long round-trip: bind Int<->Long pair when emitted. Wide-primitive
        // generator only fires the reducer when both ends are present.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.EmittedClrBindings.Add(ClrBinding.ConvertIntToLong);
            _ctx.EmittedClrBindings.Add(ClrBinding.ConvertLongToInt);
        }

        // Byte round-trip: similar pairing.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.EmittedClrBindings.Add(ClrBinding.ConvertIntToByte);
            _ctx.EmittedClrBindings.Add(ClrBinding.ConvertByteToInt);
        }

        // Int32.TryParse: out-param synthesis. Pre-existing reflection support
        // detects the trailing `out int` and re-shapes the binding's return type
        // as `(ValueTuple Bool Int)`. Reducer consumes via value/0 + value/1.
        if (_ctx.Rng.NextDouble() < 0.20) _ctx.EmittedClrBindings.Add(ClrBinding.Int32TryParse);
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

    private static string BindingFormText(ClrBinding b)
    {
        return b switch
        {
            // Explicit annotations are required to pin overload resolution.
            // Bare `System.Math/Abs` defaults to the sbyte/Byte overload, causing
            // a Byte-vs-Int mismatch at every call site.
            ClrBinding.MathAbsInt => "[fuzz-abs-int System.Math/Abs : (Int -> Int)]",
            ClrBinding.MathMinInt => "[fuzz-min-int System.Math/Min : (Int Int -> Int)]",
            ClrBinding.MathMaxInt => "[fuzz-max-int System.Math/Max : (Int Int -> Int)]",
            // System.Math.Sqrt returns Double (64-bit), which maps to ZScheme's Double,
            // not Float (32-bit). Using Double in the annotation and converting call
            // sites with float->double / double->float keeps the types consistent.
            ClrBinding.MathSqrt => "[fuzz-sqrt System.Math/Sqrt : (Double -> Double)]",
            ClrBinding.MathAbsFloat => "[fuzz-abs-flt System.Math/Abs : (Double -> Double)]",
            ClrBinding.StringIsNullOrEmpty =>
                "[fuzz-str-empty? System.String/IsNullOrEmpty : (String -> Bool)]",
            ClrBinding.StringLength =>
                "[fuzz-str-len System.String.Length :instance-property : (String -> Int)]",
            ClrBinding.StringIndexer =>
                "[fuzz-str-char System.String.Item :instance-indexer : (String Int -> Char)]",
            ClrBinding.ConvertCharToInt =>
                "[fuzz-char-to-int System.Convert/ToInt32 : (Char -> Int)]",
            ClrBinding.ConvertIntToLong =>
                "[fuzz-int-to-long System.Convert/ToInt64 : (Int -> Long)]",
            ClrBinding.ConvertLongToInt =>
                "[fuzz-long-to-int System.Convert/ToInt32 : (Long -> Int)]",
            ClrBinding.ConvertIntToByte =>
                "[fuzz-int-to-byte System.Convert/ToByte : (Int -> Byte)]",
            ClrBinding.ConvertByteToInt =>
                "[fuzz-byte-to-int System.Convert/ToInt32 : (Byte -> Int)]",
            // Out-param: TryParse(string, out int) → (ValueTuple Bool Int).
            // The compiler's reflection layer detects the trailing `out int` and
            // synthesizes the tuple return; the binding annotation reflects the
            // post-synthesis shape so type inference accepts call sites that read
            // value/0 / value/1 from the result.
            ClrBinding.Int32TryParse =>
                "[fuzz-try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))]",
            _ => throw new InvalidOperationException($"Unknown binding: {b}")
        };
    }

    public string ReduceMathAbsIntToInt(Scope scope, int depth)
    {
        return $"(fuzz-abs-int {_exprs.GenInt(scope, depth - 1)})";
    }

    public string ReduceMathMinIntToInt(Scope scope, int depth)
    {
        return $"(fuzz-min-int {_exprs.GenInt(scope, depth - 1)} {_exprs.GenInt(scope, depth - 1)})";
    }

    public string ReduceMathMaxIntToInt(Scope scope, int depth)
    {
        return $"(fuzz-max-int {_exprs.GenInt(scope, depth - 1)} {_exprs.GenInt(scope, depth - 1)})";
    }

    public string ReduceStringLengthToInt(Scope scope, int depth)
    {
        return $"(fuzz-str-len {_exprs.GenString(scope, depth - 1)})";
    }

    // Round-trips through Char: index a non-empty string then convert the Char
    // back to Int. Picks index 0 to guarantee bounds-safety regardless of
    // the runtime string's length.
    public string ReduceStringIndexerToInt(Scope scope, int depth)
    {
        return $"(fuzz-char-to-int (fuzz-str-char {_exprs.GenString(scope, depth - 1)} 0))";
    }

    // fuzz-sqrt and fuzz-abs-flt both bind to Double overloads (Float-overloads
    // of System.Math.Sqrt / System.Math.Abs don't exist as the default resolution).
    // Wrap the call site with float->double / double->float so the reducer returns
    // Float and slots into GenFloat's weight table.
    public string ReduceMathSqrtToFloat(Scope scope, int depth)
    {
        return $"(double->float (fuzz-sqrt (float->double {_exprs.GenFloat(scope, depth - 1)})))";
    }

    public string ReduceMathAbsFloatToFloat(Scope scope, int depth)
    {
        return $"(double->float (fuzz-abs-flt (float->double {_exprs.GenFloat(scope, depth - 1)})))";
    }

    public string ReduceStringIsEmptyToBool(Scope scope, int depth)
    {
        return $"(fuzz-str-empty? {_exprs.GenString(scope, depth - 1)})";
    }

    // (value/1 (fuzz-try-parse <string>)) — Int when parse succeeds.
    // String input is steered toward digit-strings so the success branch fires
    // most of the time. value/1's read on a failed-parse default is `default(int)`,
    // which is also a valid Int, so divergence between backends is structural.
    public string ReduceTryParseToInt(Scope scope, int depth)
    {
        var s = _ctx.Rng.NextDouble() < 0.65
            ? $"\"{_ctx.Rng.Next(0, 10000)}\""
            : _exprs.GenString(scope, depth - 1);
        return $"(value/1 (fuzz-try-parse {s}))";
    }

    // (if (value/0 (fuzz-try-parse <string>)) ...) — Bool reducer over the parse-success flag.
    public string ReduceTryParseSuccessToBool(Scope scope, int depth)
    {
        var s = _ctx.Rng.NextDouble() < 0.5
            ? $"\"{_ctx.Rng.Next(0, 10000)}\""
            : _exprs.GenString(scope, depth - 1);
        return $"(value/0 (fuzz-try-parse {s}))";
    }
}

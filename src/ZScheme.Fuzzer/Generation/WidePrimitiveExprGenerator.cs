namespace ZScheme.Fuzzer.Generation;

// Generates expressions whose evaluation passes through a Long, Byte, or Char
// intermediate before reducing to Int. Long/Byte/Char have no literal forms in
// AstNode and no native ZScheme conversion helpers, so the only way to reach
// them is via CLR `Convert` calls.
//
// The bindings themselves (`fuzz-int-to-long`, `fuzz-long-to-int`, etc.) are
// emitted by ClrInteropExprGenerator's import-block. This generator just
// composes round-trip reducers that wrap an Int sub-expression with a
// (Convert.ToWide (Convert.ToInt32 _)) chain.
//
// Char goes through StringIndexer rather than a literal-int->char path because
// `System.Convert.ToChar(Int32)` resolution issues haven't been validated in
// the IL backend; using the indexer keeps Char values strictly value-from-
// string-runtime.
public sealed class WidePrimitiveExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public WidePrimitiveExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool LongAvailable =>
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertIntToLong)
        && _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertLongToInt);

    public bool ByteAvailable =>
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertIntToByte)
        && _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertByteToInt);

    // Genuine 64-bit arithmetic requires the Int64 Math overloads. ChooseBindings
    // emits them as a set with the Int<->Long conversion pair, so checking one
    // Math-Long member is sufficient; guard the conversions too for safety.
    public bool LongArithAvailable =>
        LongAvailable
        && _ctx.EmittedClrBindings.Contains(ClrBinding.MathMaxLong)
        && _ctx.EmittedClrBindings.Contains(ClrBinding.MathMinLong)
        && _ctx.EmittedClrBindings.Contains(ClrBinding.MathAbsLong)
        && _ctx.EmittedClrBindings.Contains(ClrBinding.MathBigMul);

    // Round-trips an Int sub-expression through Long: (long->int (int->long e)).
    // Exercises Long-typed intermediate codegen.
    public string ReduceLongRoundTripToInt(Scope scope, int depth)
    {
        return $"(fuzz-long-to-int (fuzz-int-to-long {_exprs.GenInt(scope, depth - 1)}))";
    }

    // Round-trips a small literal through Byte: (byte->int (int->byte n)).
    // Convert.ToByte throws OverflowException for values outside [0,255], so
    // the input is a small constant in [0,255] rather than an arbitrary Int
    // sub-expression — keeps the round-trip total without risking runtime
    // divergence between backends.
    public string ReduceByteRoundTripToInt(Scope scope, int depth)
    {
        var n = _ctx.Rng.Next(0, 256);
        return $"(fuzz-byte-to-int (fuzz-int-to-byte {n}))";
    }

    // Genuine Long arithmetic reducers. Each builds Long intermediates from
    // Int sub-expressions (via fuzz-int-to-long), runs a 64-bit operation, then
    // narrows back to Int (via fuzz-long-to-int), so the compute : Int contract
    // holds. Both backends compute the identical Int64 value, so DiffExec agrees.

    private string ToLong(Scope scope, int depth)
    {
        return $"(fuzz-int-to-long {_exprs.GenInt(scope, depth - 1)})";
    }

    public string ReduceLongMaxToInt(Scope scope, int depth)
    {
        return $"(fuzz-long-to-int (fuzz-max-long {ToLong(scope, depth)} {ToLong(scope, depth)}))";
    }

    public string ReduceLongMinToInt(Scope scope, int depth)
    {
        return $"(fuzz-long-to-int (fuzz-min-long {ToLong(scope, depth)} {ToLong(scope, depth)}))";
    }

    // |(long)int| always fits in Int64, so no overflow — narrows back cleanly.
    public string ReduceLongAbsToInt(Scope scope, int depth)
    {
        return $"(fuzz-long-to-int (fuzz-abs-long {ToLong(scope, depth)}))";
    }

    // Genuine 64-bit multiply of two Ints then narrow — exercises the 64-bit
    // product plus the Int64->Int32 narrowing conversion.
    public string ReduceBigMulToInt(Scope scope, int depth)
    {
        return $"(fuzz-long-to-int (fuzz-big-mul {_exprs.GenInt(scope, depth - 1)} {_exprs.GenInt(scope, depth - 1)}))";
    }

    // 64-bit equality via the fully-polymorphic `=` (no CLR binding needed);
    // reduced to Int through an if. Gated on the plain LongAvailable pair.
    public string ReduceLongEqToInt(Scope scope, int depth)
    {
        return $"(if (= {ToLong(scope, depth)} {ToLong(scope, depth)}) 1 0)";
    }
}

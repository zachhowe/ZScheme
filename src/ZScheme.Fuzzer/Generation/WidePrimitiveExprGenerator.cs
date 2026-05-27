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
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertIntToLong) &&
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertLongToInt);

    public bool ByteAvailable =>
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertIntToByte) &&
        _ctx.EmittedClrBindings.Contains(ClrBinding.ConvertByteToInt);

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
}

namespace ZScheme.Fuzzer.Generation.Stdlib;

// Bundles every per-module stdlib generator under one field so ExprGenerator
// holds a single reference instead of one per stdlib module. ProgramGenerator
// constructs this once and registers it via ExprGenerator.SetStdlibGenerators.
public sealed class StdlibGenerators
{
    public StdlibOptionGenerator Option { get; }
    public StdlibTreeListGenerator TreeList { get; }
    public StdlibResultGenerator Result { get; }
    public StdlibVectorGenerator Vector { get; }
    public StdlibHashGenerator Hash { get; }
    public StdlibStringGenerator String { get; }
    public StdlibMathGenerator Math { get; }
    public StdlibCoreGenerator Core { get; }
    public StdlibCondGenerator Cond { get; }
    public StdlibPipeGenerator Pipe { get; }
    public StdlibListGenerator List { get; }
    public StdlibConcurrentCollectionGenerator Concurrent { get; }
    public StdlibMutableCollectionGenerator Mutable { get; }
    public StdlibErrorGenerator Error { get; }

    public StdlibGenerators(GeneratorContext ctx, ExprGenerator exprs)
    {
        Option = new StdlibOptionGenerator(ctx, exprs);
        TreeList = new StdlibTreeListGenerator(ctx, exprs);
        Result = new StdlibResultGenerator(ctx, exprs);
        Vector = new StdlibVectorGenerator(ctx, exprs);
        Hash = new StdlibHashGenerator(ctx, exprs);
        String = new StdlibStringGenerator(ctx, exprs);
        Math = new StdlibMathGenerator(ctx, exprs);
        Core = new StdlibCoreGenerator(ctx, exprs);
        Cond = new StdlibCondGenerator(ctx, exprs);
        Pipe = new StdlibPipeGenerator(ctx, exprs);
        List = new StdlibListGenerator(ctx, exprs);
        Concurrent = new StdlibConcurrentCollectionGenerator(ctx, exprs);
        Mutable = new StdlibMutableCollectionGenerator(ctx, exprs);
        Error = new StdlibErrorGenerator(ctx, exprs);
    }
}

namespace ZScheme.Fuzzer.Generation;

// Each entry maps to a `(import stdlib/<name>)` declaration.
// ProgramGenerator emits the actual `(import ...)` lines based on which entries
// land in `GeneratorContext.Imports` per case. Per-module reducer logic lives
// in `Generation/Stdlib/Stdlib<Module>Generator.cs`.
public enum StdlibImport
{
    Option,
    TreeList,
    Result,
    Vector,
    Hash,
    String,
    Math,
    Core,
    Cond,
    Pipe,
    List,
    ConcurrentQueue,
    ConcurrentStack,
    ConcurrentBag,
    ConcurrentDictionary,
    MutableVector,
    MutableTreeList,
    MutableHash,
    Error,
    Control,
    Catch,
}

// Per-case selector that randomly enables a subset of stdlib imports.
// All reducer logic has moved out to per-module files; this class is now
// only responsible for the gate decisions.
public sealed class StdlibImportGenerator
{
    private readonly GeneratorContext _ctx;

    public StdlibImportGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    public void ChooseImports()
    {
        if (_ctx.Rng.NextDouble() < 0.6)
            _ctx.Imports.Add(StdlibImport.Option);
        if (_ctx.Rng.NextDouble() < 0.5)
            _ctx.Imports.Add(StdlibImport.TreeList);
        if (_ctx.Rng.NextDouble() < 0.4)
            _ctx.Imports.Add(StdlibImport.Result);
        if (_ctx.Rng.NextDouble() < 0.5)
            _ctx.Imports.Add(StdlibImport.Vector);
        if (_ctx.Rng.NextDouble() < 0.35)
            _ctx.Imports.Add(StdlibImport.Hash);
        if (_ctx.Rng.NextDouble() < 0.30)
            _ctx.Imports.Add(StdlibImport.String);
        if (_ctx.Rng.NextDouble() < 0.30)
            _ctx.Imports.Add(StdlibImport.Math);
        if (_ctx.Rng.NextDouble() < 0.20)
            _ctx.Imports.Add(StdlibImport.Core);
        if (_ctx.Rng.NextDouble() < 0.30)
            _ctx.Imports.Add(StdlibImport.Cond);
        if (_ctx.Rng.NextDouble() < 0.30)
            _ctx.Imports.Add(StdlibImport.Pipe);
        if (_ctx.Rng.NextDouble() < 0.25)
        {
            _ctx.Imports.Add(StdlibImport.List);
            // Half the list-importing programs also get the partner modules so
            // the cross-representation conversion reducers
            // (list<->vector/treelist) can fire.
            if (_ctx.Rng.NextDouble() < 0.5)
            {
                _ctx.Imports.Add(StdlibImport.Vector);
                _ctx.Imports.Add(StdlibImport.TreeList);
            }
        }

        // Concurrent collections — independent gates, mid-frequency. Each
        // brings a CLR `import-clr` block under the hood, so keep the per-case
        // probability moderate to avoid bloating every program.
        if (_ctx.Rng.NextDouble() < 0.20)
            _ctx.Imports.Add(StdlibImport.ConcurrentQueue);
        if (_ctx.Rng.NextDouble() < 0.20)
            _ctx.Imports.Add(StdlibImport.ConcurrentStack);
        if (_ctx.Rng.NextDouble() < 0.18)
            _ctx.Imports.Add(StdlibImport.ConcurrentBag);
        if (_ctx.Rng.NextDouble() < 0.18)
            _ctx.Imports.Add(StdlibImport.ConcurrentDictionary);

        // Mutable collections — constructors come from the immutable
        // counterpart, so force-add the dependency when the mutable variant
        // fires. Probabilities mirror the immutable ones so the joint rate
        // stays sensible.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.MutableVector);
            _ctx.Imports.Add(StdlibImport.Vector);
        }

        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.MutableTreeList);
            _ctx.Imports.Add(StdlibImport.TreeList);
        }

        if (_ctx.Rng.NextDouble() < 0.18)
        {
            _ctx.Imports.Add(StdlibImport.MutableHash);
            _ctx.Imports.Add(StdlibImport.Hash);
            // hash-ref returns Option; ensure Option is available so future
            // Option-aware reducers can chain with it.
            _ctx.Imports.Add(StdlibImport.Option);
        }

        // Error module — exposes Error (record) + make-error helper. Always pulls
        // Option since Error's inner field is `(Option Error)`.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.Error);
            _ctx.Imports.Add(StdlibImport.Option);
        }

        // Control (when/unless macros). The observable effect shape needs a
        // mutable vector, so pull that (and its immutable dependency) most of
        // the time; the pure Unit-branch shape works without it.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.Control);
            if (_ctx.Rng.NextDouble() < 0.7)
            {
                _ctx.Imports.Add(StdlibImport.MutableVector);
                _ctx.Imports.Add(StdlibImport.Vector);
            }
        }

        // Catch macro — its expansion references Err/Error/None/__ex-message
        // at the use site, so the partner modules are mandatory.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.Catch);
            _ctx.Imports.Add(StdlibImport.Result);
            _ctx.Imports.Add(StdlibImport.Error);
            _ctx.Imports.Add(StdlibImport.Option);
        }
    }
}

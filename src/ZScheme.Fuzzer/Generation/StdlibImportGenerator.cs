namespace ZScheme.Fuzzer.Generation;

// Each entry maps to a `(import stdlib/<name>)` declaration.
// ProgramGenerator emits the actual `(import ...)` lines based on which entries
// land in `GeneratorContext.Imports` per case. Per-module reducer logic lives
// in `Generation/Stdlib/Stdlib<Module>Generator.cs`.
public enum StdlibImport
{
    Option,
    List,
    Result,
    Array,
    Map,
    String,
    Math,
    Core,
    Cond,
    Pipe,
    Slist,
    ConcurrentQueue,
    ConcurrentStack,
    ConcurrentBag,
    ConcurrentDictionary,
    MutableArray,
    MutableList,
    MutableMap,
    Error,
}

// Per-case selector that randomly enables a subset of stdlib imports.
// All reducer logic has moved out to per-module files; this class is now
// only responsible for the gate decisions.
public sealed class StdlibImportGenerator
{
    private readonly GeneratorContext _ctx;

    public StdlibImportGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public void ChooseImports()
    {
        if (_ctx.Rng.NextDouble() < 0.6)  _ctx.Imports.Add(StdlibImport.Option);
        if (_ctx.Rng.NextDouble() < 0.5)  _ctx.Imports.Add(StdlibImport.List);
        if (_ctx.Rng.NextDouble() < 0.4)  _ctx.Imports.Add(StdlibImport.Result);
        if (_ctx.Rng.NextDouble() < 0.5)  _ctx.Imports.Add(StdlibImport.Array);
        if (_ctx.Rng.NextDouble() < 0.35) _ctx.Imports.Add(StdlibImport.Map);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.Imports.Add(StdlibImport.String);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.Imports.Add(StdlibImport.Math);
        if (_ctx.Rng.NextDouble() < 0.20) _ctx.Imports.Add(StdlibImport.Core);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.Imports.Add(StdlibImport.Cond);
        if (_ctx.Rng.NextDouble() < 0.30) _ctx.Imports.Add(StdlibImport.Pipe);
        if (_ctx.Rng.NextDouble() < 0.25) _ctx.Imports.Add(StdlibImport.Slist);

        // Concurrent collections — independent gates, mid-frequency. Each
        // brings a CLR `import-clr` block under the hood, so keep the per-case
        // probability moderate to avoid bloating every program.
        if (_ctx.Rng.NextDouble() < 0.20) _ctx.Imports.Add(StdlibImport.ConcurrentQueue);
        if (_ctx.Rng.NextDouble() < 0.20) _ctx.Imports.Add(StdlibImport.ConcurrentStack);
        if (_ctx.Rng.NextDouble() < 0.18) _ctx.Imports.Add(StdlibImport.ConcurrentBag);
        if (_ctx.Rng.NextDouble() < 0.18) _ctx.Imports.Add(StdlibImport.ConcurrentDictionary);

        // Mutable collections — constructors come from the immutable
        // counterpart, so force-add the dependency when the mutable variant
        // fires. Probabilities mirror the immutable ones so the joint rate
        // stays sensible.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.MutableArray);
            _ctx.Imports.Add(StdlibImport.Array);
        }
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.MutableList);
            _ctx.Imports.Add(StdlibImport.List);
        }
        if (_ctx.Rng.NextDouble() < 0.18)
        {
            _ctx.Imports.Add(StdlibImport.MutableMap);
            _ctx.Imports.Add(StdlibImport.Map);
            // mutable-map/get returns Option, and the dispatch path includes
            // option/some? in stdlib's own implementation; ensure Option is
            // available so future Option-aware reducers can chain with it.
            _ctx.Imports.Add(StdlibImport.Option);
        }

        // Error module — exposes ErrorInfo (record) + Error helper. Always pulls
        // Option since ErrorInfo's cause field is `(Option ErrorInfo)`.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            _ctx.Imports.Add(StdlibImport.Error);
            _ctx.Imports.Add(StdlibImport.Option);
        }
    }
}

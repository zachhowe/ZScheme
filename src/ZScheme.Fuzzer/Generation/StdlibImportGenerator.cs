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
    }
}

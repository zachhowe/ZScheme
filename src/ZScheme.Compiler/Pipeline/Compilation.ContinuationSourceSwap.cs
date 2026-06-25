using Serilog;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    /// <summary>
    ///     Returns true if any user-level continuation operator appears in the parsed s-expressions.
    ///     Operators recognized: <c>call/cc</c>, <c>reset</c>, <c>shift</c>, <c>prompt</c>,
    ///     <c>control</c>, <c>call/comp</c>. The check is purely lexical so it can run before
    ///     macro expansion / type inference / IR lowering — which is what the cross-assembly
    ///     continuation source-swap needs (see <see cref="MaybeSwapPrecompiledForSource"/>).
    /// </summary>
    private static bool SExprUsesContinuationOps(IReadOnlyList<SExpr> sexprs)
    {
        foreach (var s in sexprs)
            if (Walk(s))
                return true;
        return false;

        static bool Walk(SExpr expr)
        {
            switch (expr)
            {
                case SExpr.SList { Items: var items }:
                    if (
                        items.Count > 0
                        && items[0] is SExpr.Atom { Kind: TokenKind.Symbol } head
                        && IsContinuationKeyword(head.Text)
                    )
                        return true;
                    foreach (var item in items)
                        if (Walk(item))
                            return true;
                    return false;
                case SExpr.BracketList { Items: var items }:
                    foreach (var item in items)
                        if (Walk(item))
                            return true;
                    return false;
                default:
                    return false;
            }
        }
    }

    private static bool IsContinuationKeyword(string text) =>
        text is "call/cc" or "reset" or "shift" or "prompt" or "control" or "call/comp";

    /// <summary>
    ///     If user code uses any continuation operator AND a precompiled package in the cache
    ///     ships its sources alongside the .dll (because the package was built with
    ///     <c>(bundle-source true)</c>), prefer source compilation for that package so its
    ///     functions are wrapped with <c>ContinuationTransform</c> and can safely participate
    ///     in cross-assembly continuation capture.
    ///
    ///     The mechanism is intentionally module-level: we register the bundled source dir as a
    ///     <c>PackagePaths</c> entry. The existing source-compile path then handles the package
    ///     end-to-end. Selectively recompiling specific functions would require re-running type
    ///     inference per-symbol against the rest of the package's exports — far more invasive
    ///     and brittle than just letting the compiler do its normal job over the whole module.
    /// </summary>
    private void MaybeSwapPrecompiledForSource(IReadOnlyList<SExpr> sexprs)
    {
        if (!SExprUsesContinuationOps(sexprs))
            return;

        TryRegisterBundledSource("zscheme-stdlib");

        // Explicit precompiled package paths can also be swapped if metadata.json indicates
        // bundled source.
        foreach (var dllPath in _options.PrecompiledPackagePaths.ToList())
            TryRegisterExplicitBundledSource(dllPath);
    }

    private void TryRegisterBundledSource(string packageName)
    {
        var package = _packageCache.TryLoadLatest(packageName);
        if (package?.PackageDir is null || package.ModuleSourcePaths is null)
            return;

        var prefix = package.ImportPrefix;
        if (prefix is null)
        {
            Log.Debug(
                "ContinuationSourceSwap: package {Name} has bundled source but no import-prefix; skipping",
                packageName
            );
            return;
        }

        if (_options.PackagePaths.ContainsKey(prefix))
            return; // user already overrode with their own source path

        var srcDir = Path.Combine(package.PackageDir, "src");
        if (!Directory.Exists(srcDir))
            return;

        _options.PackagePaths[prefix] = srcDir;
        Log.Information(
            "ContinuationSourceSwap: routing {Prefix} through bundled source at {SrcDir} (continuation operators detected in user code)",
            prefix,
            srcDir
        );
    }

    private void TryRegisterExplicitBundledSource(string dllPath)
    {
        var metadataPath = Path.ChangeExtension(dllPath, ".metadata.json");
        if (!File.Exists(metadataPath))
            return;

        var json = File.ReadAllText(metadataPath);
        var pkg = MetadataSerializer.Deserialize(json, dllPath);
        if (pkg?.PackageDir is null || pkg.ModuleSourcePaths is null || pkg.ImportPrefix is null)
            return;

        if (_options.PackagePaths.ContainsKey(pkg.ImportPrefix))
            return;

        var srcDir = Path.Combine(pkg.PackageDir, "src");
        if (!Directory.Exists(srcDir))
            return;

        _options.PackagePaths[pkg.ImportPrefix] = srcDir;
        // Drop the explicit precompiled reference so we don't load both copies.
        _options.PrecompiledPackagePaths.Remove(dllPath);
        Log.Information(
            "ContinuationSourceSwap: routing {Prefix} through bundled source at {SrcDir} (continuation operators detected in user code)",
            pkg.ImportPrefix,
            srcDir
        );
    }
}

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZScheme.Fuzzer.Runtime;

namespace ZScheme.Fuzzer.Oracles;

public static class DifferentialExecOracle
{
    public const string Name = "diffexec";

    public static OracleResult Run(CompiledArtifacts artifacts, string scratchDir, TimeSpan timeout)
    {
        _ = timeout;
        if (artifacts.CsResult is null || artifacts.IlResult is null)
            return OracleResult.Fail(Name, "missing compiled artifacts");

        Directory.CreateDirectory(scratchDir);

        // Also save to disk for artifact dump on failure.
        File.WriteAllBytes(Path.Combine(scratchDir, "il.dll"), artifacts.IlResult.OutputBytes);

        var (csOk, csBytes, csEmitDetails) = EmitCSharpBinary(artifacts.CsResult.CsOutput);
        if (!csOk || csBytes is null)
            return OracleResult.Fail(Name, "Roslyn failed to compile C# output", csEmitDetails);

        File.WriteAllBytes(Path.Combine(scratchDir, "cs.dll"), csBytes);

        int ilResult;
        int csResult;
        string ilError = "";
        string csError = "";

        // Prefer the main module's class when scanning for `Compute`. Aux modules
        // and stdlib modules share the same assembly and sometimes happen to define
        // non-fuzz-relevant static methods; pinning to the expected class name
        // avoids picking the wrong one.
        var expectedClass = ExpectedMainModuleClassName(artifacts.Program.ModuleName);

        var ilCtx = new CollectibleLoadContext();
        try
        {
            ilResult = InvokeCompute(ilCtx, artifacts.IlResult.OutputBytes, expectedClass, out ilError);
        }
        catch (Exception ex)
        {
            return OracleResult.Fail(Name, "exception invoking IL Compute()", ex.ToString());
        }
        finally { ilCtx.Unload(); }

        var csCtx = new CollectibleLoadContext();
        try
        {
            csResult = InvokeCompute(csCtx, csBytes, expectedClass, out csError);
        }
        catch (Exception ex)
        {
            return OracleResult.Fail(Name, "exception invoking C# Compute()", ex.ToString());
        }
        finally { csCtx.Unload(); }

        if (ilError.Length > 0 || csError.Length > 0)
        {
            return OracleResult.Fail(Name, "Compute() invocation errored",
                $"[IL] {ilError}\n[CS] {csError}");
        }

        if (ilResult != csResult)
        {
            return OracleResult.Fail(Name,
                $"Compute() return diverged (IL={ilResult}, CS={csResult})",
                null);
        }

        return OracleResult.Ok(Name);
    }

    private static int InvokeCompute(
        AssemblyLoadContext ctx, byte[] assemblyBytes, string expectedClass, out string error)
    {
        error = "";
        using var ms = new MemoryStream(assemblyBytes);
        var asm = ctx.LoadFromStream(ms);

        MethodInfo? compute = null;
        // First pass: look for Compute on the specific main-module class.
        foreach (var t in asm.GetTypes())
        {
            if (t.Name != expectedClass) continue;
            compute = t.GetMethod("Compute",
                BindingFlags.Public | BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (compute is not null) break;
        }
        // Fallback: legacy "first type with Compute" lookup, preserved in case the
        // name-convention assumption is ever wrong for a given emitter version.
        if (compute is null)
        {
            foreach (var t in asm.GetTypes())
            {
                var mi = t.GetMethod("Compute",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (mi is not null)
                {
                    compute = mi;
                    break;
                }
            }
        }

        if (compute is null)
        {
            error = $"Compute method not found in assembly. Types: [{string.Join(",", asm.GetTypes().Select(t => t.FullName))}]";
            return 0;
        }

        var result = compute.Invoke(null, null);
        if (result is int i) return i;
        error = $"Compute returned non-int: {result?.GetType().Name ?? "null"}";
        return 0;
    }

    // Mirrors NameConverter.ClassNameFromModuleName without depending on the
    // compiler's internal API: PascalCase the module name, replace `/` and `-`
    // with `_`-boundaries, then suffix with "Module".
    private static string ExpectedMainModuleClassName(string moduleName)
    {
        // The module names we emit are of form `fuzz_<hex>`, and NameConverter
        // converts that to `Fuzz_<hex>Module` in the emitter output.
        var parts = moduleName.Split(new[] { '/', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p[1..]);
            sb.Append('_');
        }
        if (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        sb.Append("Module");
        return sb.ToString();
    }

    private static (bool Ok, byte[]? Bytes, string Details) EmitCSharpBinary(string csSource)
    {
        var tree = CSharpSyntaxTree.ParseText(csSource);
        var refs = ReferenceAssemblyResolver.ReferenceDlls
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"fuzz-cs-{Guid.NewGuid():N}",
            syntaxTrees: [tree],
            references: refs,
            options: options);

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);
        if (!emitResult.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Roslyn emit failed:");
            foreach (var d in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine("  " + d);
            sb.AppendLine("--- C# source ---");
            sb.AppendLine(csSource);
            return (false, null, sb.ToString());
        }

        return (true, ms.ToArray(), "");
    }

    private sealed class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext() : base(isCollectible: true) { }
    }
}

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
        if (artifacts.CsResult is null || artifacts.IlResult is null)
            return OracleResult.Fail(Name, "missing compiled artifacts");

        Directory.CreateDirectory(scratchDir);

        // Also save to disk for artifact dump on failure.
        File.WriteAllBytes(Path.Combine(scratchDir, "il.dll"), artifacts.IlResult.OutputBytes);

        var (csOk, csBytes, csEmitDetails) = EmitCSharpBinary(artifacts.CsResult.CsOutput);
        if (!csOk || csBytes is null)
            return OracleResult.Fail(Name, "Roslyn failed to compile C# output", csEmitDetails);

        File.WriteAllBytes(Path.Combine(scratchDir, "cs.dll"), csBytes);

        // Prefer the main module's class when scanning for `Compute`. Aux modules
        // and stdlib modules share the same assembly and sometimes happen to define
        // non-fuzz-relevant static methods; pinning to the expected class name
        // avoids picking the wrong one.
        var expectedClass = ExpectedMainModuleClassName(artifacts.Program.ModuleName);

        var (ilOutcome, ilError, ilTimedOut) = TryInvokeComputeWithTimeout(
            artifacts.IlResult.OutputBytes,
            expectedClass,
            timeout
        );
        var (csOutcome, csError, csTimedOut) = TryInvokeComputeWithTimeout(
            csBytes,
            expectedClass,
            timeout
        );

        // A timeout means Compute() didn't return within the budget — a genuine
        // finding (broken TCO / non-termination), and previously a worker hang.
        // Report it rather than blocking; one-side-times-out is a strong divergence
        // signal, both-time-out still gets surfaced instead of hanging.
        if (ilTimedOut || csTimedOut)
            return OracleResult.Fail(
                Name,
                "Compute() timed out (possible non-termination / broken TCO)",
                $"[IL] {(ilTimedOut ? $"timed out after {timeout.TotalSeconds:0.#}s" : "completed")}\n"
                    + $"[CS] {(csTimedOut ? $"timed out after {timeout.TotalSeconds:0.#}s" : "completed")}"
            );

        // Surface lookup/return-type errors directly (not user-program runtime errors).
        if (ilError.Length > 0 || csError.Length > 0)
            return OracleResult.Fail(
                Name,
                "Compute() invocation errored",
                $"[IL] {ilError}\n[CS] {csError}"
            );

        // Both returned a value: compare values.
        if (ilOutcome.Value is int ilVal && csOutcome.Value is int csVal)
        {
            if (ilVal != csVal)
                return OracleResult.Fail(
                    Name,
                    $"Compute() return diverged (IL={ilVal}, CS={csVal})"
                );
            return OracleResult.Ok(Name);
        }

        // Both threw: compare exception type + message. If they match, the program
        // simply has the same observable runtime behavior under both backends —
        // not a compiler bug.
        if (ilOutcome.Exception is not null && csOutcome.Exception is not null)
        {
            // Unwrap a single-inner AggregateException defensively. With
            // GetAwaiter().GetResult() the inner exception is already rethrown
            // unwrapped, but if either backend ever surfaces .Wait() / .Result
            // semantics this keeps the comparison apples-to-apples.
            var ilEx = UnwrapAggregate(ilOutcome.Exception);
            var csEx = UnwrapAggregate(csOutcome.Exception);
            var ilType = ilEx.GetType().FullName ?? "";
            var csType = csEx.GetType().FullName ?? "";
            var ilMsg = ilEx.Message ?? "";
            var csMsg = csEx.Message ?? "";
            if (ilType == csType && ilMsg == csMsg)
                return OracleResult.Ok(Name);
            return OracleResult.Fail(
                Name,
                "Compute() exceptions diverged",
                $"[IL] {ilType}: {ilMsg}\n[CS] {csType}: {csMsg}\n\n[IL stack]\n{ilOutcome.Exception}\n\n[CS stack]\n{csOutcome.Exception}"
            );
        }

        // One side threw; the other did not — definitely a divergence.
        return OracleResult.Fail(
            Name,
            "Compute() outcome diverged (one threw, one returned)",
            $"[IL] {(ilOutcome.Exception is null ? $"returned {ilOutcome.Value}" : $"threw {ilOutcome.Exception.GetType().Name}: {ilOutcome.Exception.Message}")}\n"
                + $"[CS] {(csOutcome.Exception is null ? $"returned {csOutcome.Value}" : $"threw {csOutcome.Exception.GetType().Name}: {csOutcome.Exception.Message}")}\n\n"
                + $"[IL stack]\n{ilOutcome.Exception}\n\n[CS stack]\n{csOutcome.Exception}"
        );
    }

    // Runs TryInvokeCompute on a dedicated background thread and abandons it if it
    // does not finish within `timeout`. On timeout the thread (and its collectible
    // AssemblyLoadContext) is leaked — it cannot be safely aborted or unloaded
    // mid-run — but IsBackground keeps it from blocking process exit. Timeouts are
    // expected to be rare because generation is constructed to terminate, so the
    // leak is bounded; the win is reporting a hang instead of stalling a worker.
    private static (InvokeOutcome Outcome, string Error, bool TimedOut) TryInvokeComputeWithTimeout(
        byte[] assemblyBytes,
        string expectedClass,
        TimeSpan timeout
    )
    {
        InvokeOutcome outcome = default;
        var error = "";
        var thread = new Thread(() =>
        {
            (outcome, error) = TryInvokeCompute(assemblyBytes, expectedClass);
        })
        {
            IsBackground = true,
            Name = "diffexec-compute",
        };
        thread.Start();
        if (!thread.Join(timeout))
            return (default, "", true);
        // Join returned true → the write in the thread happens-before this read.
        return (outcome, error, false);
    }

    private static (InvokeOutcome Outcome, string Error) TryInvokeCompute(
        byte[] assemblyBytes,
        string expectedClass
    )
    {
        var ctx = new CollectibleLoadContext();
        try
        {
            var raw = InvokeComputeRaw(ctx, assemblyBytes, expectedClass, out var err);
            if (err.Length > 0)
                return (default, err);
            return (new InvokeOutcome(raw, null), "");
        }
        catch (TargetInvocationException tie)
        {
            return (new InvokeOutcome(null, tie.InnerException ?? tie), "");
        }
        catch (Exception ex)
        {
            return (new InvokeOutcome(null, ex), "");
        }
        finally
        {
            ctx.Unload();
        }
    }

    private static object? InvokeComputeRaw(
        AssemblyLoadContext ctx,
        byte[] assemblyBytes,
        string expectedClass,
        out string error
    )
    {
        error = "";
        using var ms = new MemoryStream(assemblyBytes);
        var asm = ctx.LoadFromStream(ms);

        MethodInfo? compute = null;
        // First pass: look for Compute on the specific main-module class.
        foreach (var t in asm.GetTypes())
        {
            if (t.Name != expectedClass)
                continue;
            compute = t.GetMethod(
                "Compute",
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null
            );
            if (compute is not null)
                break;
        }

        // Fallback: legacy "first type with Compute" lookup, preserved in case the
        // name-convention assumption is ever wrong for a given emitter version.
        if (compute is null)
            foreach (var t in asm.GetTypes())
            {
                var mi = t.GetMethod(
                    "Compute",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null
                );
                if (mi is not null)
                {
                    compute = mi;
                    break;
                }
            }

        if (compute is null)
        {
            error =
                $"Compute method not found in assembly. Types: [{string.Join(",", asm.GetTypes().Select(t => t.FullName))}]";
            return null;
        }

        var result = compute.Invoke(null, null);
        if (result is int)
            return result;
        // Async compute returns Task<int>. Block synchronously on the awaiter so
        // a faulted task rethrows its inner exception unwrapped, matching the
        // sync-throw shape the exception comparison path expects.
        if (result is Task<int> taskOfInt)
            return taskOfInt.GetAwaiter().GetResult();
        if (result is Task)
        {
            error = "Compute returned non-generic Task — fuzzer should only emit Task<Int>";
            return null;
        }

        error = $"Compute returned non-int: {result?.GetType().Name ?? "null"}";
        return null;
    }

    private static Exception UnwrapAggregate(Exception ex)
    {
        return ex is AggregateException agg && agg.InnerExceptions.Count == 1
            ? agg.InnerExceptions[0]
            : ex;
    }

    // Mirrors NameConverter.ClassNameFromModuleName without depending on the
    // compiler's internal API: PascalCase the module name, replace `/` and `-`
    // with `_`-boundaries, then suffix with "Module".
    private static string ExpectedMainModuleClassName(string moduleName)
    {
        // The module names we emit are of form `fuzz_<hex>`, and NameConverter
        // converts that to `Fuzz_<hex>Module` in the emitter output.
        var parts = moduleName.Split(new[] { '/', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0)
                continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1)
                sb.Append(p[1..]);
            sb.Append('_');
        }

        if (sb.Length > 0 && sb[^1] == '_')
            sb.Length--;
        sb.Append("Module");
        return sb.ToString();
    }

    private static (bool Ok, byte[]? Bytes, string Details) EmitCSharpBinary(string csSource)
    {
        var tree = CSharpSyntaxTree.ParseText(csSource);
        var refs = ReferenceAssemblyResolver
            .ReferenceDlls.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable
        );

        var compilation = CSharpCompilation.Create(
            $"fuzz-cs-{Guid.NewGuid():N}",
            [tree],
            refs,
            options
        );

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);
        if (!emitResult.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Roslyn emit failed:");
            foreach (
                var d in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            )
                sb.AppendLine("  " + d);
            sb.AppendLine("--- C# source ---");
            sb.AppendLine(csSource);
            return (false, null, sb.ToString());
        }

        return (true, ms.ToArray(), "");
    }

    private readonly record struct InvokeOutcome(object? Value, Exception? Exception);

    private sealed class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext()
            : base(true) { }
    }
}

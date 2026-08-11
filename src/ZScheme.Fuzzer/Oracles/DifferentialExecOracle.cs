using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZScheme.Fuzzer.Runtime;

namespace ZScheme.Fuzzer.Oracles;

public static class DifferentialExecOracle
{
    public const string Name = "diffexec";

    /// <param name="outOfProcess">
    ///     Run each <c>Compute()</c> in a child process instead of on a background thread. Costs
    ///     two process spawns per case, and buys the one thing the in-process path cannot do:
    ///     surviving a <see cref="StackOverflowException" />, which is uncatchable and would
    ///     otherwise kill the fuzzer host and every parallel worker with it. Required for
    ///     deep-recursion (broken-TCO) probing; off for ordinary runs.
    /// </param>
    public static OracleResult Run(
        CompiledArtifacts artifacts,
        string scratchDir,
        TimeSpan timeout,
        bool outOfProcess = false
    )
    {
        if (artifacts.CsResult is null || artifacts.IlResult is null)
            return OracleResult.Fail(Name, "missing compiled artifacts");

        Directory.CreateDirectory(scratchDir);

        // Also save to disk for artifact dump on failure — and, out-of-process, because the
        // child loads them from exactly these paths.
        var ilPath = Path.Combine(scratchDir, "il.dll");
        File.WriteAllBytes(ilPath, artifacts.IlResult.OutputBytes);

        var (csOk, csBytes, csEmitDetails) = EmitCSharpBinary(artifacts.CsResult.CsOutput);
        if (!csOk || csBytes is null)
            return OracleResult.Fail(Name, "Roslyn failed to compile C# output", csEmitDetails);

        var csPath = Path.Combine(scratchDir, "cs.dll");
        File.WriteAllBytes(csPath, csBytes);

        // Prefer the main module's class when scanning for `Compute`. Aux modules
        // and stdlib modules share the same assembly and sometimes happen to define
        // non-fuzz-relevant static methods; pinning to the expected class name
        // avoids picking the wrong one.
        var expectedClass = ExpectedMainModuleClassName(artifacts.Program.ModuleName);

        var il = outOfProcess
            ? ExecOutOfProcess(ilPath, expectedClass, timeout)
            : ExecInProcess(artifacts.IlResult.OutputBytes, expectedClass, timeout);
        var cs = outOfProcess
            ? ExecOutOfProcess(csPath, expectedClass, timeout)
            : ExecInProcess(csBytes, expectedClass, timeout);

        return Compare(il, cs, timeout);
    }

    /// <summary>
    ///     Runs one already-compiled assembly's <c>Compute()</c> out-of-process and renders the
    ///     outcome for display. Exists for the repro runner, which executes a saved artifact
    ///     directly and must survive it the same way the deep-recursion oracle does — a repro of
    ///     a stack-overflow finding would otherwise kill the tool inspecting it.
    /// </summary>
    public static string DescribeOutOfProcess(
        byte[] assemblyBytes,
        string moduleName,
        string scratchDir,
        TimeSpan timeout
    )
    {
        Directory.CreateDirectory(scratchDir);
        var path = Path.Combine(scratchDir, "describe.dll");
        File.WriteAllBytes(path, assemblyBytes);

        var outcome = ExecOutOfProcess(path, ExpectedMainModuleClassName(moduleName), timeout);
        if (outcome.TimedOut)
            return $"timed out after {timeout.TotalSeconds:0.#}s";
        if (outcome.Error.Length > 0)
            return $"could not run: {outcome.Error}";
        return Describe(outcome);
    }

    private static OracleResult Compare(ExecOutcome il, ExecOutcome cs, TimeSpan timeout)
    {
        // A timeout means Compute() didn't return within the budget — a genuine
        // finding (broken TCO / non-termination), and previously a worker hang.
        // Report it rather than blocking; one-side-times-out is a strong divergence
        // signal, both-time-out still gets surfaced instead of hanging.
        if (il.TimedOut || cs.TimedOut)
            return OracleResult.Fail(
                Name,
                "Compute() timed out (possible non-termination / broken TCO)",
                $"[IL] {(il.TimedOut ? $"timed out after {timeout.TotalSeconds:0.#}s" : "completed")}\n"
                    + $"[CS] {(cs.TimedOut ? $"timed out after {timeout.TotalSeconds:0.#}s" : "completed")}"
            );

        // Surface lookup/return-type errors directly (not user-program runtime errors).
        if (il.Error.Length > 0 || cs.Error.Length > 0)
            return OracleResult.Fail(
                Name,
                "Compute() invocation errored",
                $"[IL] {il.Error}\n[CS] {cs.Error}"
            );

        // Stack overflow on exactly one backend is the TCO-regression signal this whole
        // out-of-process path exists to catch: the same source ran in constant stack on one
        // backend and blew the stack on the other. Both overflowing is agreement — the
        // generator deliberately emits non-tail recursive shapes, which are *supposed* to
        // overflow at depth on both.
        if (il.StackOverflowed || cs.StackOverflowed)
        {
            if (il.StackOverflowed && cs.StackOverflowed)
                return OracleResult.Ok(Name);
            return OracleResult.Fail(
                Name,
                "Compute() stack overflowed on one backend (broken TCO)",
                $"[IL] {Describe(il)}\n[CS] {Describe(cs)}"
            );
        }

        // Both returned a value: compare values.
        if (il.Value is { } ilVal && cs.Value is { } csVal)
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
        if (il.Exception is not null && cs.Exception is not null)
        {
            if (il.Exception.Type == cs.Exception.Type && il.Exception.Message == cs.Exception.Message)
                return OracleResult.Ok(Name);
            return OracleResult.Fail(
                Name,
                "Compute() exceptions diverged",
                $"[IL] {il.Exception.Type}: {il.Exception.Message}\n"
                    + $"[CS] {cs.Exception.Type}: {cs.Exception.Message}\n\n"
                    + $"[IL stack]\n{il.Exception.Detail}\n\n[CS stack]\n{cs.Exception.Detail}"
            );
        }

        // One side threw; the other did not — definitely a divergence.
        return OracleResult.Fail(
            Name,
            "Compute() outcome diverged (one threw, one returned)",
            $"[IL] {Describe(il)}\n[CS] {Describe(cs)}\n\n"
                + $"[IL stack]\n{il.Exception?.Detail}\n\n[CS stack]\n{cs.Exception?.Detail}"
        );
    }

    private static string Describe(ExecOutcome o)
    {
        if (o.StackOverflowed)
            return "stack overflowed";
        if (o.Exception is not null)
            return $"threw {o.Exception.Type}: {o.Exception.Message}";
        return $"returned {o.Value?.ToString() ?? "nothing"}";
    }

    /// <summary>
    ///     Runs <c>Compute()</c> in a child process. The child cannot report a stack overflow
    ///     itself — it is aborted mid-flight — so that verdict is read off the exit code and
    ///     stderr instead.
    /// </summary>
    private static ExecOutcome ExecOutOfProcess(
        string assemblyPath,
        string expectedClass,
        TimeSpan timeout
    )
    {
        var (exe, prefix) = ChildCommand();
        var result = ProcessRunner.Run(
            exe,
            [.. prefix, ExecChild.Flag, assemblyPath, expectedClass],
            timeout
        );

        if (result.TimedOut)
            return ExecOutcome.Timeout();

        // A .NET stack overflow aborts the process: SIGABRT (exit 134) on Unix, and the runtime
        // prints "Stack overflow." before dying on every platform. Check both so this does not
        // silently stop working off-Linux.
        if (result.ExitCode == 134 || result.Stderr.Contains("Stack overflow", StringComparison.Ordinal))
            return ExecOutcome.Overflowed();

        var line = result
            .Stdout.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && l[0] == '{');
        if (line is null)
            return ExecOutcome.Failed(
                $"exec child produced no verdict (exit={result.ExitCode})\n"
                    + $"--- stdout ---\n{result.Stdout}\n--- stderr ---\n{result.Stderr}"
            );

        ChildResult? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(line, ChildResultContext.Default.ChildResult);
        }
        catch (JsonException ex)
        {
            return ExecOutcome.Failed($"exec child verdict unparseable: {ex.Message}\n{line}");
        }

        return parsed switch
        {
            null => ExecOutcome.Failed($"exec child verdict was null: {line}"),
            { Kind: "ret", Value: { } v } => ExecOutcome.Returned(v),
            { Kind: "exc" } e => ExecOutcome.Threw(
                new ExceptionInfo(e.ExceptionType ?? "", e.Message ?? "", e.Detail ?? "")
            ),
            { Kind: "error" } e => ExecOutcome.Failed(e.Error ?? "unspecified exec child error"),
            _ => ExecOutcome.Failed($"exec child verdict unrecognized: {line}"),
        };
    }

    /// <summary>
    ///     Re-execs this same binary. Under an apphost <c>ProcessPath</c> is the fuzzer itself;
    ///     when the process is the shared <c>dotnet</c> host instead, the managed dll has to lead
    ///     the argument list.
    /// </summary>
    private static (string Exe, string[] Prefix) ChildCommand()
    {
        var exe = Environment.ProcessPath;
        var isSharedHost =
            exe is null
            || string.Equals(
                Path.GetFileNameWithoutExtension(exe),
                "dotnet",
                StringComparison.OrdinalIgnoreCase
            );

        if (!isSharedHost)
            return (exe!, []);

        var managed = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(managed))
            throw new InvalidOperationException(
                "Cannot locate this assembly to re-exec for out-of-process execution."
            );
        return (exe ?? FuzzEnv.DotnetPath, [managed]);
    }

    // Runs TryInvokeCompute on a dedicated background thread and abandons it if it
    // does not finish within `timeout`. On timeout the thread (and its collectible
    // AssemblyLoadContext) is leaked — it cannot be safely aborted or unloaded
    // mid-run — but IsBackground keeps it from blocking process exit. Timeouts are
    // expected to be rare because generation is constructed to terminate, so the
    // leak is bounded; the win is reporting a hang instead of stalling a worker.
    //
    // A StackOverflowException is *not* survivable here — it would take the whole fuzzer down —
    // which is what ExecOutOfProcess exists for.
    private static ExecOutcome ExecInProcess(
        byte[] assemblyBytes,
        string expectedClass,
        TimeSpan timeout
    )
    {
        var outcome = ExecOutcome.Failed("");
        var thread = new Thread(() =>
        {
            outcome = TryInvokeCompute(assemblyBytes, expectedClass);
        })
        {
            IsBackground = true,
            Name = "diffexec-compute",
        };
        thread.Start();
        if (!thread.Join(timeout))
            return ExecOutcome.Timeout();
        // Join returned true → the write in the thread happens-before this read.
        return outcome;
    }

    private static ExecOutcome TryInvokeCompute(byte[] assemblyBytes, string expectedClass)
    {
        var ctx = new CollectibleLoadContext();
        try
        {
            var raw = InvokeComputeRaw(ctx, assemblyBytes, expectedClass, out var err);
            if (err.Length > 0)
                return ExecOutcome.Failed(err);
            return ExecOutcome.Returned((int)raw!);
        }
        catch (TargetInvocationException tie)
        {
            return ExecOutcome.Threw(Describe(tie.InnerException ?? tie));
        }
        catch (Exception ex)
        {
            return ExecOutcome.Threw(Describe(ex));
        }
        finally
        {
            ctx.Unload();
        }

        // Unwrap a single-inner AggregateException defensively. With GetAwaiter().GetResult()
        // the inner exception is already rethrown unwrapped, but if either backend ever
        // surfaces .Wait() / .Result semantics this keeps the comparison apples-to-apples.
        static ExceptionInfo Describe(Exception ex)
        {
            var unwrapped = UnwrapAggregate(ex);
            return new ExceptionInfo(
                unwrapped.GetType().FullName ?? "",
                unwrapped.Message ?? "",
                unwrapped.ToString()
            );
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

    /// <summary>A thrown exception reduced to the three things the comparison needs.</summary>
    /// <remarks>
    ///     Strings rather than a live <see cref="Exception" /> because the out-of-process path
    ///     only ever has strings — keeping one shape means both paths feed the same comparison.
    /// </remarks>
    private sealed record ExceptionInfo(string Type, string Message, string Detail);

    /// <summary>
    ///     How one backend's <c>Compute()</c> finished. At most one of <c>Value</c>,
    ///     <c>Exception</c>, <c>StackOverflowed</c> and <c>TimedOut</c> is set; <c>Error</c> means
    ///     the harness could not run it at all, which is reported separately from a program that
    ///     ran and failed.
    /// </summary>
    private sealed record ExecOutcome(
        int? Value,
        ExceptionInfo? Exception,
        bool StackOverflowed,
        bool TimedOut,
        string Error
    )
    {
        public static ExecOutcome Returned(int value) => new(value, null, false, false, "");

        public static ExecOutcome Threw(ExceptionInfo info) => new(null, info, false, false, "");

        public static ExecOutcome Overflowed() => new(null, null, true, false, "");

        public static ExecOutcome Timeout() => new(null, null, false, true, "");

        public static ExecOutcome Failed(string error) => new(null, null, false, false, error);
    }

    private sealed class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext()
            : base(true) { }
    }
}

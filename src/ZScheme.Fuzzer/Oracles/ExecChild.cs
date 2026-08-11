using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZScheme.Fuzzer.Oracles;

/// <summary>
///     The child half of the out-of-process execution oracle: loads one compiled assembly,
///     invokes its <c>Compute()</c>, and writes the outcome to stdout as a single JSON line.
/// </summary>
/// <remarks>
///     This exists for one reason: a <see cref="StackOverflowException" /> cannot be caught, so a
///     generated program that overflows takes down whatever process runs it. In-process that is
///     the fuzzer itself — every parallel worker dies with it — which is why deep recursion could
///     not be generated at all. Out here the overflow kills only the child, and the parent reads
///     it off the exit code as an ordinary verdict.
///     <para>
///         The child is the fuzzer binary re-executed with <c>--exec-child</c> rather than a
///         separate host program, so <c>ZScheme.Runtime.dll</c> already sits beside it and
///         resolves through the default load context exactly as it does in-process — no
///         runtimeconfig, no assembly copying, and no <c>main</c> in the generated program.
///     </para>
/// </remarks>
public static class ExecChild
{
    /// <summary>Marks the re-exec. Not a documented flag — the parent passes it to itself.</summary>
    public const string Flag = "--exec-child";

    public static int Run(string assemblyPath, string expectedClass)
    {
        ChildResult result;
        try
        {
            result = Invoke(assemblyPath, expectedClass);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            result = FromException(tie.InnerException);
        }
        catch (Exception ex)
        {
            result = FromException(ex);
        }

        // One line, stdout only: the parent takes the last non-empty line, so a stray write from
        // the loaded program cannot be mistaken for the verdict as long as ours comes last.
        Console.Out.WriteLine(JsonSerializer.Serialize(result, ChildResultContext.Default.ChildResult));
        Console.Out.Flush();
        return 0;
    }

    private static ChildResult Invoke(string assemblyPath, string expectedClass)
    {
        // Loaded into the default context, not a collectible one: the process exits after a
        // single Compute(), so there is nothing to unload and nothing to leak.
        var asm = Assembly.LoadFrom(assemblyPath);
        var compute = FindCompute(asm, expectedClass);
        if (compute is null)
            return new ChildResult(
                "error",
                Error: "Compute method not found in assembly. Types: ["
                    + string.Join(",", asm.GetTypes().Select(t => t.FullName))
                    + "]"
            );

        var raw = compute.Invoke(null, null);
        return raw switch
        {
            int i => new ChildResult("ret", Value: i),
            // Async compute returns Task<int>. Block on the awaiter so a faulted task rethrows
            // its inner exception unwrapped, matching the sync-throw shape the parent compares.
            Task<int> t => new ChildResult("ret", Value: t.GetAwaiter().GetResult()),
            Task => new ChildResult(
                "error",
                Error: "Compute returned non-generic Task — fuzzer should only emit Task<Int>"
            ),
            _ => new ChildResult(
                "error",
                Error: $"Compute returned non-int: {raw?.GetType().Name ?? "null"}"
            ),
        };
    }

    private static MethodInfo? FindCompute(Assembly asm, string expectedClass)
    {
        // Prefer the main module's class; fall back to any type with a matching Compute, as the
        // in-process path does, in case the name convention ever drifts.
        foreach (var t in asm.GetTypes())
            if (t.Name == expectedClass && ComputeOn(t) is { } hit)
                return hit;

        foreach (var t in asm.GetTypes())
            if (ComputeOn(t) is { } hit)
                return hit;

        return null;

        static MethodInfo? ComputeOn(Type t) =>
            t.GetMethod(
                "Compute",
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null
            );
    }

    private static ChildResult FromException(Exception ex)
    {
        var unwrapped =
            ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex;
        return new ChildResult(
            "exc",
            ExceptionType: unwrapped.GetType().FullName ?? "",
            Message: unwrapped.Message ?? "",
            Detail: unwrapped.ToString()
        );
    }
}

/// <summary>One <c>Compute()</c> outcome, serialized across the process boundary.</summary>
public sealed record ChildResult(
    string Kind,
    int? Value = null,
    string? ExceptionType = null,
    string? Message = null,
    string? Detail = null,
    string? Error = null
);

[JsonSerializable(typeof(ChildResult))]
internal sealed partial class ChildResultContext : JsonSerializerContext;

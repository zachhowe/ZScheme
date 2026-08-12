using System.Collections;
using System.Runtime.InteropServices;

namespace ZScheme.Zsup;

/// <summary>
///     Unix process replacement. Handing off with <c>execve</c> instead of spawning a child means
///     there is no second process to wait on, signals and job control reach the real program
///     directly, exit status is whatever it returns, and <c>ps</c> shows <c>zs</c> rather than the
///     shim.
/// </summary>
internal static partial class NativeExec
{
    /// <summary>
    ///     <c>execve</c>, not <c>execvp</c>: the caller already holds an absolute path, and a PATH
    ///     search could re-find the shim itself and loop forever.
    /// </summary>
    /// <remarks>
    ///     The environment is passed explicitly rather than relying on <c>execv</c> inheriting
    ///     <c>environ</c>. On Unix .NET keeps its own managed copy of the environment and never
    ///     calls <c>setenv</c>, so anything set through <see cref="Environment.SetEnvironmentVariable" />
    ///     would be invisible to the new process image.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int Execve(IntPtr path, IntPtr[] argv, IntPtr[] envp);

    /// <summary>
    ///     Replaces the current process with <paramref name="executablePath" />. Only returns if the
    ///     handoff failed; the return value is then the errno from <c>execve</c>.
    /// </summary>
    /// <param name="argv0">
    ///     The name the child sees as its own argv[0] — the target's name, so its diagnostics and
    ///     usage text read correctly rather than saying "zsup".
    /// </param>
    /// <param name="extraEnvironment">Variables to add to, or override in, the inherited environment.</param>
    internal static int Exec(
        string executablePath,
        string argv0,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> extraEnvironment
    )
    {
        // Anything buffered here would be lost the moment the process image is replaced.
        Console.Out.Flush();
        Console.Error.Flush();

        var pathPtr = IntPtr.Zero;
        // Both argv and envp are NULL-terminated char*[], which LibraryImport cannot marshal for us.
        var argv = new IntPtr[args.Count + 2];
        var environment = BuildEnvironment(extraEnvironment);
        var envp = new IntPtr[environment.Count + 1];

        try
        {
            pathPtr = Marshal.StringToCoTaskMemUTF8(executablePath);

            argv[0] = Marshal.StringToCoTaskMemUTF8(argv0);
            for (var i = 0; i < args.Count; i++)
                argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);
            argv[^1] = IntPtr.Zero;

            for (var i = 0; i < environment.Count; i++)
                envp[i] = Marshal.StringToCoTaskMemUTF8(environment[i]);
            envp[^1] = IntPtr.Zero;

            Execve(pathPtr, argv, envp);

            // execve only returns on failure.
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            // Only reached when execve failed; on success the whole address space is gone.
            if (pathPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPtr);
            foreach (var p in argv)
                if (p != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(p);
            foreach (var p in envp)
                if (p != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Builds the <c>KEY=VALUE</c> list, with the extras replacing any inherited entry.</summary>
    private static List<string> BuildEnvironment(IReadOnlyDictionary<string, string> extras)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                merged[key] = value;
        }

        foreach (var (key, value) in extras)
            merged[key] = value;

        return [.. merged.Select(kv => $"{kv.Key}={kv.Value}")];
    }
}

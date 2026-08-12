using System.Diagnostics;
using System.Runtime.Versioning;
using ZScheme.Toolchain;

namespace ZScheme.Zsup;

/// <summary>
///     The shim half of <c>zsup</c>: resolves the selected toolchain and hands off to its real
///     <c>zs</c> or <c>zs-lsp</c>.
/// </summary>
internal static class ShimRunner
{
    /// <summary>Exit code for "command found but not executable", matching shell convention.</summary>
    private const int CannotExecute = 126;

    /// <summary>Exit code for "command not found", matching shell convention.</summary>
    private const int NotFound = 127;

    /// <param name="toolName">Either <c>zs</c> or <c>zs-lsp</c>.</param>
    internal static int Run(string toolName, string[] args)
    {
        var registry = new ToolchainRegistry(ZSchemeHome.GetHome());
        var resolution = new ToolchainResolver(registry).Resolve(
            Environment.GetEnvironmentVariable(ZSchemeHome.VersionEnvironmentVariable),
            Directory.GetCurrentDirectory()
        );

        if (resolution is not ToolchainResolution.Resolved resolved)
        {
            Console.Error.WriteLine(ResolutionErrorFormatter.Format(resolution));
            return NotFound;
        }

        var toolchain = resolved.Toolchain;
        var target = toolchain.GetExecutablePath(toolName);

        if (!File.Exists(target))
        {
            Console.Error.WriteLine($"error: toolchain '{toolchain.Name}' has no {toolName}");
            Console.Error.WriteLine($"note: expected it at {target}");
            Console.Error.WriteLine(
                toolchain.IsLinked
                    ? $"help: check that {toolchain.Dir} is a ZScheme build output directory"
                    : $"help: run `zsup install {toolchain.Name} --force` to repair the installation"
            );
            return NotFound;
        }

        return Launch(target, toolName, args, toolchain);
    }

    private static int Launch(
        string target,
        string toolName,
        string[] args,
        InstalledToolchain toolchain
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            var errno = NativeExec.Exec(target, toolName, args, ChildEnvironment(toolchain));
            Console.Error.WriteLine(
                $"error: failed to execute {target}: {new System.ComponentModel.Win32Exception(errno).Message}"
            );
            return CannotExecute;
        }

        var psi = new ProcessStartInfo(target)
        {
            // Direct CreateProcess, which is what lets the child inherit our console handles.
            // RedirectStandardInput/Output/Error are deliberately left at their false defaults:
            // zs-lsp speaks JSON-RPC over stdio and `zs repl` reads the console directly, so
            // interposing a pipe here would corrupt both.
            UseShellExecute = false,
        };

        // ArgumentList quotes each element correctly; building a single string would break on
        // paths containing spaces.
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        foreach (var (key, value) in ChildEnvironment(toolchain))
            psi.Environment[key] = value;

        // Joined before the child exists, so the child is inside the job from the moment it is
        // created and so is anything it spawns while starting up. The handle is held for the rest of
        // this process's life on purpose -- see the class remarks.
        var job = WindowsJobObject.TryCreate();
        var contained = job is not null && job.TryAssignCurrentProcess();

        using var child = Process.Start(psi);
        if (child is null)
        {
            Console.Error.WriteLine($"error: failed to start {target}");
            return CannotExecute;
        }

        // Where this process could not join the job, assigning the child still gets the child itself
        // reaped; only the startup window reopens. A job that could not be created at all is the
        // documented tolerated degradation and stays quiet, but one that exists and refuses both
        // assignments reaps nothing -- and an editor leaving a zs-lsp behind on every restart is
        // invisible unless it is said out loud.
        if (job is not null && !contained && !TryContain(job, child))
            ZsupHelpers.Warn($"could not tie {toolName} to this process; it may outlive the shim");

        // Ctrl+C reaches the child too, since it shares our console process group. Cancelling the
        // shim's own handling keeps it waiting, so the shell does not print a prompt over output
        // from a child that still owns the console.
        ConsoleCancelEventHandler onCancel = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += onCancel;
        try
        {
            child.WaitForExit();
            return child.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    ///     Fallback for a shim that could not join the job itself: assigns the running child to it.
    /// </summary>
    /// <remarks>
    ///     A child that has already exited counts as contained, not as a failure — there is nothing
    ///     left to reap, and reading <see cref="Process.Handle" /> for one throws rather than
    ///     returning a dead handle.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool TryContain(WindowsJobObject job, Process child)
    {
        try
        {
            return job.TryAssign(child.Handle);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    ///     Variables the child gets on top of the inherited environment.
    /// </summary>
    private static Dictionary<string, string> ChildEnvironment(InstalledToolchain toolchain)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZSCHEME_TOOLCHAIN"] = toolchain.Name,
        };

        // A linked developer build reports the same compiler version as the released toolchain of
        // that version, so by default it would read and write the same cache/pkg/<version>
        // directory. A dev build that changes the metadata format would then silently poison the
        // released toolchain's cache, so linked toolchains get their own cache root.
        //
        // Blank counts as unset, not as an override: the compiler normalizes an empty
        // ZSCHEME_CACHE_DIR straight back to <home>/cache, so an `is null` test here would skip the
        // isolation and hand the dev build the released cache after all.
        if (
            toolchain.IsLinked
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ZSchemeHome.CacheDirEnvironmentVariable)
            )
        )
            environment[ZSchemeHome.CacheDirEnvironmentVariable] = ZSchemeHome.GetLinkedCacheRoot(
                toolchain.Name
            );

        return environment;
    }
}

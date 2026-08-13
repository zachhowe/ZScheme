using System.Diagnostics;
using System.Reflection;

namespace ZScheme.Zsup.Tests;

/// <summary>
///     Runs the built <c>zsup</c> as a child process against a scratch home.
/// </summary>
/// <remarks>
///     The command classes read <c>ZSCHEME_HOME</c> through <c>ZSchemeHome.GetHome()</c> with no
///     injectable override, and the things worth asserting about them — the exit code, what lands on
///     stdout versus stderr — are process-level anyway. A child process gets its own environment, so
///     nothing here touches the test runner's, which is the same reason every other test in this
///     repo takes an explicit home instead of exporting one.
/// </remarks>
internal static class ZsupProcess
{
    /// <param name="ExitCode">What the command returned.</param>
    /// <param name="Stdout">Everything written to standard output.</param>
    /// <param name="Stderr">Everything written to standard error.</param>
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>The <c>zsup</c> apphost sitting beside the test assembly.</summary>
    internal static string Executable =>
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            OperatingSystem.IsWindows() ? "zsup.exe" : "zsup"
        );

    /// <param name="home">Passed as ZSCHEME_HOME.</param>
    /// <param name="environment">
    ///     Extra variables for the child. A null value removes an inherited one, which is how a test
    ///     makes sure the runner's own ZSCHEME_VERSION cannot reach the command.
    /// </param>
    /// <param name="executable">
    ///     Which zsup to run, when it matters where it lives — <c>self uninstall</c>'s Windows
    ///     refusal turns on whether the running binary is inside the home. Defaults to the one
    ///     beside the tests.
    /// </param>
    internal static Result Run(
        string home,
        string[] args,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        string? executable = null
    )
    {
        executable ??= Executable;

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(Executable)!,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        psi.Environment["ZSCHEME_HOME"] = home;
        // Inherited from the runner it would silently outrank whatever the test is selecting.
        psi.Environment.Remove("ZSCHEME_VERSION");

        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {executable}");

        // Both pipes drained concurrently: reading one to the end while the other fills its buffer
        // deadlocks.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);

        return new Result(process.ExitCode, stdout.Result, stderr.Result);
    }
}

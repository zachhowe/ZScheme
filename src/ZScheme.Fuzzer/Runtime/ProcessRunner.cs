using System.Diagnostics;
using System.Text;

namespace ZScheme.Fuzzer.Runtime;

public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Elapsed);

    public static Result Run(
        string exe,
        IEnumerable<string> args,
        TimeSpan timeout,
        IDictionary<string, string>? env = null,
        string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdoutBuf) stdoutBuf.AppendLine(e.Data); } };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) { lock (stderrBuf) stderrBuf.AppendLine(e.Data); } };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var timedOut = false;
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit();
        }
        proc.WaitForExit();
        sw.Stop();

        return new Result(proc.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString(), timedOut, sw.Elapsed);
    }
}

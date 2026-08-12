namespace ZScheme.Zsup;

internal static class ZsupHelpers
{
    /// <summary>Writes a message to stderr and returns the failure exit code.</summary>
    internal static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>Writes a multi-line message (as produced by the resolution formatter) to stderr.</summary>
    internal static int Error(params string[] lines)
    {
        foreach (var line in lines)
            Console.Error.WriteLine(line);

        return 1;
    }

    /// <summary>Writes an advisory to stderr without failing the command.</summary>
    internal static void Warn(string message)
    {
        Console.Error.WriteLine($"warning: {message}");
    }

    /// <summary>
    ///     Deletes a downloaded file if it is there, ignoring a failure to do so.
    /// </summary>
    /// <remarks>
    ///     Every caller is cleaning up an archive it no longer needs, either after the install it
    ///     fed has committed or alongside the error that rejected it. In both positions the delete
    ///     is the least important thing happening, so it must not become the reported outcome --
    ///     an antivirus scanner still holding a freshly written file is routine on Windows. A
    ///     leftover is swept by <c>ToolchainInstaller</c> on a later run and is never trusted in
    ///     the meantime: the next download overwrites it and hashes what it wrote.
    /// </remarks>
    internal static void TryDeleteDownload(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swept later.
        }
    }
}

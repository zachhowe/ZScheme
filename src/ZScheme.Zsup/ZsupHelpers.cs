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
}

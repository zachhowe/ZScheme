namespace ZScript.Compiler.Package.NuGet;

internal static class TfmSelector
{
    private static readonly string[] Precedence =
    [
        "net10.0",
        "net9.0",
        "net8.0",
        "net7.0",
        "net6.0",
        "netstandard2.1",
        "netstandard2.0",
        "netstandard1.6",
        "netstandard1.5",
        "netstandard1.4",
        "netstandard1.3",
        "netstandard1.2",
        "netstandard1.1",
        "netstandard1.0"
    ];

    public static string? SelectBestTfm(IEnumerable<string> availableTfms)
    {
        var set = new HashSet<string>(availableTfms, StringComparer.OrdinalIgnoreCase);
        foreach (var tfm in Precedence)
            if (set.Contains(tfm))
                return tfm;
        return null;
    }
}

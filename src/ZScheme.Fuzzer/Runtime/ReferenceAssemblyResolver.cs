using System.Runtime.InteropServices;

namespace ZScheme.Fuzzer.Runtime;

public static class ReferenceAssemblyResolver
{
    private static readonly Lazy<string> _sharedFrameworkDir = new(ResolveSharedFrameworkDir);
    private static readonly Lazy<IReadOnlyList<string>> _referenceDlls = new(LoadReferenceDlls);

    public static string SharedFrameworkDir => _sharedFrameworkDir.Value;
    public static IReadOnlyList<string> ReferenceDlls => _referenceDlls.Value;

    private static string ResolveSharedFrameworkDir()
    {
        var dir = RuntimeEnvironment.GetRuntimeDirectory();
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"Shared framework directory not found: {dir}");
        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IReadOnlyList<string> LoadReferenceDlls()
    {
        return Directory.EnumerateFiles(SharedFrameworkDir, "*.dll")
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return !name.Contains(".Native.", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }
}

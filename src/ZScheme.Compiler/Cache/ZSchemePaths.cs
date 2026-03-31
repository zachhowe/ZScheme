namespace ZScheme.Compiler.Cache;

public static class ZSchemePaths
{
    public static string GetPackageCacheRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme", "cache", "pkg");
    }
}

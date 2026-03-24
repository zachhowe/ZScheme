namespace ZScript.Compiler.Cache;

public static class ZScriptPaths
{
    public static string GetPackageCacheRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscript", "cache", "pkg");
    }
}

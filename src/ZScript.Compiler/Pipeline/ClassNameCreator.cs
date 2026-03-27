namespace ZScript.Compiler.Pipeline;

public static class ClassNameCreator
{
    public static string ClassNameFromModuleName(string moduleName)
    {
        return string.Concat(
            moduleName.Split('/', '-')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Module";
    }
}

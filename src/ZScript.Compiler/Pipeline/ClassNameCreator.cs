namespace ZScript.Compiler.Pipeline;

public static class ClassNameCreator
{
    public static string ClassNameFromModuleName(string moduleName) =>
        Codegen.NameConverter.ClassNameFromModuleName(moduleName);
}

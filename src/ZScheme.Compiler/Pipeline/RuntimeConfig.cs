namespace ZScheme.Compiler.Pipeline;

/// <summary>
///     Generates the <c>runtimeconfig.json</c> content for an IL executable.
/// </summary>
public static class RuntimeConfig
{
    /// <summary>
    ///     Builds a <c>runtimeconfig.json</c> for an IL executable. With no declared shared
    ///     frameworks this emits a single <c>Microsoft.NETCore.App</c> framework. When the package
    ///     declares frameworks (e.g. <c>Microsoft.AspNetCore.App</c>, which transitively includes
    ///     the base runtime) those are emitted as a <c>frameworks</c> array so the host loads the
    ///     matching shared framework at launch. Versions use the running runtime's major.minor.0
    ///     and rely on roll-forward to the installed patch.
    /// </summary>
    public static string Generate(IReadOnlyList<string> frameworkReferences)
    {
        var version = Environment.Version;
        var tfm = $"net{version.Major}.{version.Minor}";
        var runtimeVersion = $"{version.Major}.{version.Minor}.0";

        if (frameworkReferences.Count == 0)
            return $$"""
                {
                  "runtimeOptions": {
                    "tfm": "{{tfm}}",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "{{runtimeVersion}}"
                    }
                  }
                }
                """;

        var entries = string.Join(
            ",\n",
            frameworkReferences
                .Distinct()
                .Select(id =>
                    $$"""
                            {
                              "name": "{{id}}",
                              "version": "{{runtimeVersion}}"
                            }
                        """
                )
        );
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "{{tfm}}",
                "frameworks": [
            {{entries}}
                ]
              }
            }
            """;
    }
}

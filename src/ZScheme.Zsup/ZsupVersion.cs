using System.Reflection;

namespace ZScheme.Zsup;

/// <summary>
///     The version of this <c>zsup</c> binary, from <c>Directory.Build.props</c> via the generated
///     assembly attributes. zsup ships in lockstep with the compiler, so this is also the version of
///     the release its assets were built alongside.
/// </summary>
internal static class ZsupVersion
{
    internal static string Value { get; } = Build();

    /// <summary>The version without the <c>+sha</c> suffix.</summary>
    internal static string Base { get; } = Strip(Value);

    private static string Build()
    {
        return typeof(ZsupVersion)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "unknown";
    }

    private static string Strip(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}

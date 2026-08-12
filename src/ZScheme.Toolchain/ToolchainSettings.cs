using System.Text.Json.Serialization;

namespace ZScheme.Toolchain;

/// <summary>Contents of <c>~/.zscheme/settings.json</c>.</summary>
public sealed class ToolchainSettings
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("defaultToolchain")]
    public string? DefaultToolchain { get; set; }
}

/// <summary>Metadata written into each installed toolchain as <c>toolchain.json</c>.</summary>
public sealed class ToolchainMetadata
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The name this toolchain is installed under.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    ///     The compiler version of the payload, which keys the shared package cache. Usually equal
    ///     to <see cref="Name" />, but not when a toolchain is installed under a different name.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("rid")]
    public string? Rid { get; set; }

    [JsonPropertyName("installedAt")]
    public string? InstalledAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

/// <summary>
///     Source-generated serialization context. Reflection-based JSON is disabled project-wide
///     (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>) so that a reflection-based call
///     fails the build rather than a user's Native AOT binary at runtime.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ToolchainSettings))]
[JsonSerializable(typeof(ToolchainMetadata))]
internal sealed partial class ToolchainJsonContext : JsonSerializerContext;

using System.Security.Cryptography;

namespace ZScheme.Toolchain;

/// <summary>SHA-256 verification against a release's <c>SHA256SUMS</c> file.</summary>
public static class Checksums
{
    /// <summary>Name of the aggregate checksum file published with each release.</summary>
    public const string FileName = "SHA256SUMS";

    /// <summary>Lower-case hex digest of a file.</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    ///     Parses GNU coreutils <c>sha256sum</c> output: <c>&lt;hex&gt;  &lt;filename&gt;</c>, where the
    ///     second separator may instead be <c>*</c> for a binary-mode entry.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var split = line.IndexOf(' ');
            if (split <= 0)
                continue;

            var digest = line[..split];
            // Skip the separator run, plus the '*' binary marker if present.
            var name = line[(split + 1)..].TrimStart(' ', '*').Trim();

            if (digest.Length == 64 && name.Length > 0)
                result[name] = digest.ToLowerInvariant();
        }

        return result;
    }

    /// <summary>Looks up one file's expected digest, or <c>null</c> when it is not listed.</summary>
    public static string? Find(string sumsContent, string fileName)
    {
        return Parse(sumsContent).TryGetValue(fileName, out var digest) ? digest : null;
    }

    /// <summary>
    ///     Throws when <paramref name="path" /> does not hash to <paramref name="expected" />.
    /// </summary>
    public static void Verify(string path, string expected)
    {
        var actual = ComputeSha256(path);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidDataException(
            $"checksum mismatch for {Path.GetFileName(path)}"
                + $"{Environment.NewLine}  expected {expected.ToLowerInvariant()}"
                + $"{Environment.NewLine}    actual {actual}"
        );
    }
}

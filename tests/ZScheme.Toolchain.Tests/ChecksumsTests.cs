using System.Security.Cryptography;
using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ChecksumsTests
{
    [Fact]
    public void ComputeSha256_MatchesTheKnownDigest()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.bin");
        File.WriteAllText(path, "hello");

        var expected = Convert.ToHexStringLower(SHA256.HashData("hello"u8.ToArray()));

        Assert.Equal(expected, Checksums.ComputeSha256(path));
    }

    [Fact]
    public void Parse_ReadsTheCoreutilsTwoSpaceForm()
    {
        var content =
            "aa"
            + new string('0', 62)
            + "  zscheme-0.4.0-linux-x64.tar.gz\n"
            + "bb"
            + new string('0', 62)
            + "  zscheme-0.4.0-win-x64.zip\n";

        var parsed = Checksums.Parse(content);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("aa" + new string('0', 62), parsed["zscheme-0.4.0-linux-x64.tar.gz"]);
        Assert.Equal("bb" + new string('0', 62), parsed["zscheme-0.4.0-win-x64.zip"]);
    }

    [Fact]
    public void Parse_ReadsTheBinaryStarForm()
    {
        var digest = "cc" + new string('0', 62);

        var parsed = Checksums.Parse($"{digest} *zscheme-0.4.0-win-x64.zip\n");

        Assert.Equal(digest, parsed["zscheme-0.4.0-win-x64.zip"]);
    }

    [Fact]
    public void Parse_TolerantOfBlankLinesCommentsAndCrlf()
    {
        var digest = "dd" + new string('0', 62);
        var content = $"# a comment\r\n\r\n{digest}  asset.zip\r\n";

        var parsed = Checksums.Parse(content);

        Assert.Single(parsed);
        Assert.Equal(digest, parsed["asset.zip"]);
    }

    [Fact]
    public void Parse_IgnoresMalformedLines()
    {
        var parsed = Checksums.Parse("not-a-digest  asset.zip\njustonetoken\n");

        Assert.Empty(parsed);
    }

    [Fact]
    public void Parse_NormalizesDigestCase()
    {
        var parsed = Checksums.Parse("AA" + new string('0', 62) + "  asset.zip");

        Assert.Equal("aa" + new string('0', 62), parsed["asset.zip"]);
    }

    [Fact]
    public void Find_ReturnsNullForAnUnlistedAsset()
    {
        var content = "ee" + new string('0', 62) + "  other.zip";

        Assert.Null(Checksums.Find(content, "asset.zip"));
    }

    [Fact]
    public void Verify_PassesOnAMatch()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.bin");
        File.WriteAllText(path, "payload");

        Checksums.Verify(path, Checksums.ComputeSha256(path).ToUpperInvariant());
    }

    [Fact]
    public void Verify_ReportsBothDigestsOnAMismatch()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.bin");
        File.WriteAllText(path, "payload");
        var wrong = "ff" + new string('0', 62);

        var error = Assert.Throws<InvalidDataException>(() => Checksums.Verify(path, wrong));

        Assert.Contains("checksum mismatch for f.bin", error.Message);
        Assert.Contains(wrong, error.Message);
        Assert.Contains(Checksums.ComputeSha256(path), error.Message);
    }
}

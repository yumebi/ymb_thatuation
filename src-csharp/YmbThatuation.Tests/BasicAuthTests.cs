using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class BasicAuthTests
{
    [Fact]
    public void Describe_Https_ShowsHostWithoutWarning()
    {
        var info = BasicAuth.Describe("https://staging.example.com/admin", "Staging area");
        Assert.Equal("staging.example.com", info.Host);
        Assert.False(info.Insecure);
        Assert.Equal("Staging area", info.SiteMessage);
    }

    [Fact]
    public void Describe_Http_IsInsecure_AndShowsNonDefaultPort()
    {
        var info = BasicAuth.Describe("http://192.168.0.10:8080/", null);
        Assert.Equal("192.168.0.10:8080", info.Host);
        Assert.True(info.Insecure);
        Assert.Null(info.SiteMessage);
    }

    [Fact]
    public void Describe_InternationalizedHost_IsShownAsPunycode()
    {
        // 見た目の似た別のサイトに見せかけられないよう、xn-- の形で出す(キリル文字の "а" を使った偽の apple)
        var info = BasicAuth.Describe("https://аpple.example/", null);
        Assert.StartsWith("xn--", info.Host);
    }

    [Fact]
    public void Describe_UnparseableUri_HasEmptyHost()
    {
        Assert.Equal("", BasicAuth.Describe("not a url", null).Host);
        Assert.Equal("", BasicAuth.Describe(null, null).Host);
    }

    [Theory]
    [InlineData("Basic realm=\"Staging only\"", "Staging only")]
    [InlineData("basic REALM = \"Admin area\", charset=\"UTF-8\"", "Admin area")]
    [InlineData("Staging only", "Staging only")]
    public void SanitizeMessage_ExtractsRealmFromHeader(string challenge, string expected)
    {
        Assert.Equal(expected, BasicAuth.SanitizeMessage(challenge));
    }

    [Fact]
    public void SanitizeMessage_RemovesControlCharsAndTruncates()
    {
        Assert.Equal("ab", BasicAuth.SanitizeMessage("a\r\nb"));
        Assert.Null(BasicAuth.SanitizeMessage("   "));
        Assert.Null(BasicAuth.SanitizeMessage("\r\n"));
        var longText = new string('x', 500);
        var cut = BasicAuth.SanitizeMessage(longText)!;
        Assert.True(cut.Length <= 101);
        Assert.EndsWith("…", cut);
    }

    [Theory]
    [InlineData("https://site.example/a", "site.example", "about:blank", true)]
    [InlineData("https://site.example/a", null, "https://site.example/", true)]
    [InlineData("https://SITE.example/a", "site.example", null, true)]
    [InlineData("https://other.example/img.png", "site.example", "https://site.example/", false)]
    [InlineData("https://site.example/a", null, null, true)]       // まだ何も開いていない: 最初の移動
    [InlineData("not a url", "site.example", null, false)]
    [InlineData(null, null, null, false)]
    public void IsPromptAllowed_OnlyForTheSiteBeingViewed(string? request, string? navHost, string? source, bool expected)
    {
        Assert.Equal(expected, BasicAuth.IsPromptAllowed(request, navHost, source));
    }
}

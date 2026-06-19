using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class ExtensionsServiceTests
{
    private const string ValidId = "abcdefghijklmnopabcdefghijklmnop";

    [Fact]
    public void ParseExtensionId_PlainId_ReturnsItself()
    {
        Assert.Equal(ValidId, ExtensionsService.ParseExtensionId(ValidId));
    }

    [Fact]
    public void ParseExtensionId_CwsUrl_ExtractsId()
    {
        var url = $"https://chrome.google.com/webstore/detail/some-extension/{ValidId}";
        Assert.Equal(ValidId, ExtensionsService.ParseExtensionId(url));
    }

    [Fact]
    public void ParseExtensionId_TooShort_ReturnsNull()
    {
        Assert.Null(ExtensionsService.ParseExtensionId("abcdefgh"));
    }

    [Fact]
    public void ParseExtensionId_OutsideApRange_ReturnsNull()
    {
        var invalid = "qrstuvwxyzqrstuvwxyzqrstuvwxyzqr";
        Assert.Null(ExtensionsService.ParseExtensionId(invalid));
    }

    [Fact]
    public void ParseExtensionId_UppercaseLetters_ReturnsNull()
    {
        var upper = ValidId.ToUpperInvariant();
        Assert.Null(ExtensionsService.ParseExtensionId(upper));
    }
}

using System.IO;
using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class ExtensionsServiceTests
{
    private const string ValidId = "abcdefghijklmnopabcdefghijklmnop";

    private static ExtensionsService NewService(out string appDataDir)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ymb-thatuation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new ConfigStore(dir);
        appDataDir = dir;
        return new ExtensionsService(store);
    }

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

    [Fact]
    public void ResetExtensionState_TraversalId_Throws()
    {
        var svc = NewService(out _);
        Action act = () => svc.ResetExtensionState("..\\..\\config");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void ResetExtensionState_InvalidChars_Throws()
    {
        var svc = NewService(out _);
        Action act = () => svc.ResetExtensionState("a/b/c");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void ResetExtensionState_ValidId_NoThrow_WhenProfileMissing()
    {
        var svc = NewService(out _);
        svc.ResetExtensionState(ValidId); // プロファイルが無ければ何もしない
    }

    [Fact]
    public void RemoveExtension_OutsideExtensionsDir_Throws()
    {
        var svc = NewService(out _);
        var outside = Path.Combine(Path.GetTempPath(), "not-extensions-dir");
        Action act = () => svc.RemoveExtension(outside);
        Assert.Throws<InvalidOperationException>(act);
    }
}

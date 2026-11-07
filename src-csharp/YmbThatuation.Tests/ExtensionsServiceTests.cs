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
    public void RemoveExtension_ExtensionsDirItself_Throws()
    {
        var svc = NewService(out _);
        Assert.Throws<InvalidOperationException>(() => svc.RemoveExtension(svc.ExtensionsDir()));
        Assert.Throws<InvalidOperationException>(() => svc.RemoveExtension(svc.ExtensionsDir() + Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(svc.ExtensionsDir()));
    }

    [Fact]
    public void RemoveExtension_DeletesFolder_AndDropsItFromEveryInstance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ymb-thatuation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new ConfigStore(dir);
        var svc = new ExtensionsService(store);
        var extPath = Path.Combine(svc.ExtensionsDir(), ValidId);
        Directory.CreateDirectory(extPath);
        File.WriteAllText(Path.Combine(extPath, "manifest.json"), "{}");
        store.Update(c =>
        {
            c.Instances[0].EnabledExtensions = new List<string> { ValidId, "keep-me" };
            c.Instances[1].EnabledExtensions = new List<string> { ValidId.ToUpperInvariant() };
        });

        svc.RemoveExtension(extPath);

        Assert.False(Directory.Exists(extPath));
        var after = new ConfigStore(dir).Get();
        Assert.Equal(new[] { "keep-me" }, after.Instances[0].EnabledExtensions);
        Assert.Empty(after.Instances[1].EnabledExtensions);
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

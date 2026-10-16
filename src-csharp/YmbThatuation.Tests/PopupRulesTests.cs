using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class PopupRulesTests
{
    [Fact]
    public void NoRequestedSize_UsesDefault()
    {
        var (w, h) = PopupSizing.Resolve(null, null, 1200, 800);
        Assert.Equal(PopupSizing.DefaultWidth, w);
        Assert.Equal(PopupSizing.DefaultHeight, h);
    }

    [Fact]
    public void RequestedSize_IsUsed()
    {
        var (w, h) = PopupSizing.Resolve(450, 600, 1200, 800);
        Assert.Equal(450, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void TooSmallRequest_IsEnlargedToMinimum()
    {
        var (w, h) = PopupSizing.Resolve(100, 50, 1200, 800);
        Assert.Equal(PopupSizing.MinWidth, w);
        Assert.Equal(PopupSizing.MinHeight, h);
    }

    [Fact]
    public void HugeRequest_IsLimitedToOwnerWindow()
    {
        var (w, h) = PopupSizing.Resolve(5000, 4000, 1200, 800);
        Assert.Equal(1160, w);   // 本体ウインドウより 40 小さい
        Assert.Equal(760, h);
    }

    [Fact]
    public void SmallOwner_NeverShrinksBelowMinimum()
    {
        var (w, h) = PopupSizing.Resolve(null, null, 200, 150);
        Assert.True(w >= PopupSizing.MinWidth && h >= PopupSizing.MinHeight);
    }

    [Fact]
    public void UnknownOwner_DoesNotLimit()
    {
        var (w, _) = PopupSizing.Resolve(2000, 700, null, null);
        Assert.Equal(2000, w);
    }

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/auth?x=1", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("\\\\server\\share\\a.exe", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenableUri_OnlyHttpAndHttps(string? uri, bool expected)
    {
        Assert.Equal(expected, PopupWindow.IsOpenableUri(uri));
    }

    [Theory]
    [InlineData("https://slack.com/signin", true)]
    [InlineData("about:blank", true)]
    [InlineData("ABOUT:BLANK", true)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("data:text/html,<script>1</script>", false)]
    [InlineData("javascript:void(0)", false)]
    public void IsAllowedNavigation_HttpHttpsAndBlankOnly(string uri, bool expected)
    {
        Assert.Equal(expected, PopupWindow.IsAllowedNavigation(uri));
    }
}

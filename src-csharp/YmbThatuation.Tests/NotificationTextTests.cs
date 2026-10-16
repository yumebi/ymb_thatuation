using System.Windows.Media;
using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class NotificationTextTests
{
    private static readonly Dictionary<string, string> Ja = new()
    {
        ["notify.unread"] = "未読 {count} 件",
        ["notify.multi.title"] = "{n} 件のサービスに新着",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["notify.unread"] = "{count} unread",
        ["notify.multi.title"] = "New activity in {n} services",
    };

    [Fact]
    public void SingleService_UsesNameAsTitleAndCountAsBody()
    {
        var (title, body, id) = NotificationText.Build([new UnreadNotice("s1", "Slack", 3)], Ja);

        Assert.Equal("Slack", title);
        Assert.Equal("未読 3 件", body);
        Assert.Equal("s1", id);
    }

    [Fact]
    public void SingleService_FollowsTheLanguage()
    {
        var (_, body, _) = NotificationText.Build([new UnreadNotice("s1", "Slack", 3)], En);
        Assert.Equal("3 unread", body);
    }

    [Fact]
    public void MultipleServices_AreCombinedIntoOne()
    {
        var items = new[]
        {
            new UnreadNotice("s1", "Slack", 3),
            new UnreadNotice("g1", "Gmail (仕事)", 12),
        };

        var (title, body, id) = NotificationText.Build(items, Ja);

        Assert.Equal("2 件のサービスに新着", title);
        Assert.Equal("Slack (3), Gmail (仕事) (12)", body);
        Assert.Equal("s1", id);   // クリックで開くのは先頭のサービス
    }

    [Fact]
    public void MissingTranslation_FallsBackToJapaneseDefault()
    {
        var (_, body, _) = NotificationText.Build([new UnreadNotice("s1", "Slack", 7)], new Dictionary<string, string>());
        Assert.Equal("未読 7 件", body);
    }

    [Fact]
    public void LongText_IsTruncatedToBalloonLimits()
    {
        var longName = new string('あ', 200);
        var (title, _, _) = NotificationText.Build([new UnreadNotice("s1", longName, 1)], Ja);
        Assert.Equal(NotificationText.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title);

        var many = Enumerable.Range(1, 60).Select(i => new UnreadNotice($"s{i}", $"サービス{i}", (uint)i)).ToList();
        var (_, body, _) = NotificationText.Build(many, Ja);
        Assert.True(body.Length <= NotificationText.MaxBodyLength);
    }

    [Fact]
    public void NoItems_Throws()
    {
        Assert.Throws<ArgumentException>(() => NotificationText.Build([], Ja));
    }
}

public class TitleBarThemeTests
{
    [Theory]
    [InlineData("#1e1f24", true)]    // dark
    [InlineData("#0a0e1a", true)]    // midnight
    [InlineData("#eef0f3", false)]   // light
    [InlineData("#fdf6e3", false)]   // solarized-light
    public void IsDark_FollowsBrightness(string hex, bool expected)
    {
        Assert.Equal(expected, TitleBarTheme.IsDark(TitleBarTheme.ParseColor(hex)));
    }

    [Fact]
    public void ToColorRef_IsBlueGreenRedOrder()
    {
        // COLORREF は 0x00BBGGRR
        Assert.Equal(0x332211, TitleBarTheme.ToColorRef(Color.FromRgb(0x11, 0x22, 0x33)));
    }

    [Fact]
    public void AllPaletteBarColors_AreParsable()
    {
        foreach (var name in new[] { "dark", "light", "slate", "midnight", "forest", "solarized-dark",
                                     "solarized-light", "high-contrast", "sepia", "ocean" })
        {
            var theme = ThemePalette.Get(name);
            TitleBarTheme.ParseColor(theme.Bar);
            TitleBarTheme.ParseColor(theme.Text);
            TitleBarTheme.ParseColor(theme.Border);
        }
    }
}

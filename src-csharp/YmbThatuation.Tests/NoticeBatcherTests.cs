using YmbThatuation.Services;
using Xunit;

namespace YmbThatuation.Tests;

public class NoticeBatcherTests
{
    private static readonly Dictionary<string, string> NoText = new();

    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        public Func<DateTime> Func => () => Now;
    }

    [Fact]
    public void UnreadOnly_IsReturnedAsIs_AndLatestCountReplacesOlder()
    {
        var b = new NoticeBatcher();
        Assert.True(b.AddUnread(new UnreadNotice("a", "Slack", 1)));
        Assert.True(b.AddUnread(new UnreadNotice("a", "Slack", 3)));
        var (web, unread) = b.Drain();
        Assert.Empty(web);
        Assert.Equal(3u, Assert.Single(unread).Count);
        Assert.False(b.HasPending);
    }

    [Fact]
    public void WebNotice_DropsTheUnreadNoticeOfTheSameService_InTheSameBatch()
    {
        var b = new NoticeBatcher();
        b.AddUnread(new UnreadNotice("a", "Slack", 2));
        b.AddUnread(new UnreadNotice("b", "Gmail", 5));
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        var (web, unread) = b.Drain();
        Assert.Single(web);
        Assert.Equal("b", Assert.Single(unread).Id);   // Slack は Web 通知があるので件数の通知は出さない
    }

    [Fact]
    public void UnreadNotice_IsSuppressed_ShortlyAfterAWebNoticeForThatService()
    {
        var clock = new Clock();
        var b = new NoticeBatcher(clock.Func);
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        b.Drain();

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(b.AddUnread(new UnreadNotice("a", "Slack", 1)));   // タイトルの更新が少し遅れて来ても、二重にしない

        clock.Now += NoticeBatcher.SuppressUnreadFor;
        Assert.True(b.AddUnread(new UnreadNotice("a", "Slack", 2)));    // 時間が経てば、また通知できる
    }

    [Fact]
    public void SuppressionIsPerService()
    {
        var b = new NoticeBatcher();
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        Assert.True(b.AddUnread(new UnreadNotice("b", "Gmail", 1)));
    }

    [Fact]
    public void IdenticalWebNotices_AreCollapsed()
    {
        var b = new NoticeBatcher();
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello again"));
        Assert.Equal(2, b.Drain().Web.Count);
    }

    [Fact]
    public void Remove_DiscardsPendingNoticesOfThatService()
    {
        var b = new NoticeBatcher();
        b.AddWeb(new WebNotice("a", "Slack", "Alice", "hello"));
        b.AddUnread(new UnreadNotice("b", "Gmail", 1));
        b.Remove("a");
        var (web, unread) = b.Drain();
        Assert.Empty(web);
        Assert.Single(unread);
    }

    // ---- 文言 ----

    [Fact]
    public void SingleWebNotice_UsesServiceAndTitle_AndTheBody()
    {
        var (title, body, id) = NotificationText.BuildBatch(
            [new WebNotice("a", "Slack", "Alice", "明日のミーティングですが")], [], NoText);
        Assert.Equal("Slack · Alice", title);
        Assert.Equal("明日のミーティングですが", body);
        Assert.Equal("a", id);
    }

    [Fact]
    public void WebNoticeWithoutTitle_UsesTheServiceName()
    {
        var (title, _, _) = NotificationText.BuildBatch([new WebNotice("a", "Gmail", "  ", "body")], [], NoText);
        Assert.Equal("Gmail", title);
    }

    [Fact]
    public void OnlyUnread_BehavesAsBefore()
    {
        var (title, body, id) = NotificationText.BuildBatch([], [new UnreadNotice("a", "Gmail", 3)], NoText);
        Assert.Equal("Gmail", title);
        Assert.Equal("未読 3 件", body);
        Assert.Equal("a", id);
    }

    [Fact]
    public void Several_ShowsCount_AndOneLinePerNotice()
    {
        var (title, body, id) = NotificationText.BuildBatch(
            [new WebNotice("a", "Slack", "Alice", "first line" + "\n" + "second line"), new WebNotice("b", "Teams", "Bob", "")],
            [new UnreadNotice("c", "Gmail", 4)], NoText);
        Assert.Equal("3 件の新着", title);
        Assert.Equal("Slack: Alice — first line" + "\n" + "Teams: Bob" + "\n" + "Gmail (4)", body);
        Assert.Equal("a", id);
    }

    [Fact]
    public void LongTexts_AreTruncatedToTheBalloonLimits()
    {
        var (title, body, _) = NotificationText.BuildBatch(
            [new WebNotice("a", "Slack", new string('t', 200), new string('b', 1000))], [], NoText);
        Assert.Equal(NotificationText.MaxTitleLength, title.Length);
        Assert.Equal(NotificationText.MaxBodyLength, body.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void Nothing_Throws()
    {
        Assert.Throws<ArgumentException>(() => NotificationText.BuildBatch([], [], NoText));
    }
}

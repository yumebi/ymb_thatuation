using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class UnreadNotificationTrackerTests
{
    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private static (UnreadNotificationTracker Tracker, Clock Clock) Create()
    {
        var clock = new Clock();
        return (new UnreadNotificationTracker(() => clock.Now), clock);
    }

    [Fact]
    public void FirstUnread_Notifies()
    {
        var (t, _) = Create();
        Assert.True(t.OnCountChanged("slack", 1));
    }

    [Fact]
    public void SameCount_DoesNotNotifyAgain()
    {
        var (t, _) = Create();
        Assert.True(t.OnCountChanged("slack", 3));
        Assert.False(t.OnCountChanged("slack", 3));
    }

    [Fact]
    public void Increase_Notifies()
    {
        var (t, clock) = Create();
        Assert.True(t.OnCountChanged("slack", 1));
        clock.Advance(5);
        Assert.True(t.OnCountChanged("slack", 2));
    }

    [Fact]
    public void ZeroNeverNotifies()
    {
        var (t, _) = Create();
        t.OnCountChanged("slack", 2);
        Assert.False(t.OnCountChanged("slack", 0));
    }

    [Fact]
    public void ReadEverything_ThenNewMessageAfterAWhile_Notifies()
    {
        var (t, clock) = Create();
        t.OnCountChanged("gchat", 3);
        clock.Advance(5);
        t.OnCountChanged("gchat", 0);       // すべて既読
        clock.Advance(60);                  // 0のまま落ち着いた
        Assert.True(t.OnCountChanged("gchat", 1));
    }

    [Fact]
    public void ZeroFlicker_DoesNotRenotifyTheSameCount()
    {
        // SPAがタイトルを書き換える間だけ(N)が消えて0に見え、すぐ戻る場合。
        var (t, clock) = Create();
        Assert.True(t.OnCountChanged("gchat", 3));
        clock.Advance(2);
        t.OnCountChanged("gchat", 0);
        clock.Advance(2);
        Assert.False(t.OnCountChanged("gchat", 3));
    }

    [Fact]
    public void PartialRead_ThenNewMessage_Notifies()
    {
        // 以前は通知されなかったケース: 3件 → 一部を読んで2件 → 落ち着く → 新着で3件。
        var (t, clock) = Create();
        Assert.True(t.OnCountChanged("slack", 3));
        clock.Advance(10);
        Assert.False(t.OnCountChanged("slack", 2));
        clock.Advance(120);                 // 2件のまま落ち着いた
        Assert.True(t.OnCountChanged("slack", 3));
    }

    [Fact]
    public void BriefDip_ThenBack_DoesNotRenotify()
    {
        // 3 → 2 → 3 が短時間で起きた場合は、タイトルの揺れとみなして通知しない。
        var (t, clock) = Create();
        Assert.True(t.OnCountChanged("slack", 3));
        clock.Advance(3);
        t.OnCountChanged("slack", 2);
        clock.Advance(3);
        Assert.False(t.OnCountChanged("slack", 3));
    }

    [Fact]
    public void ServicesAreTrackedIndependently()
    {
        var (t, _) = Create();
        Assert.True(t.OnCountChanged("slack", 1));
        Assert.True(t.OnCountChanged("gmail", 1));
        Assert.False(t.OnCountChanged("slack", 1));
    }

    [Fact]
    public void Remove_ForgetsTheService()
    {
        var (t, _) = Create();
        t.OnCountChanged("slack", 5);
        t.Remove("slack");
        Assert.True(t.OnCountChanged("slack", 1));
    }
}

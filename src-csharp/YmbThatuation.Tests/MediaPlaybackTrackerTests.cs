using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class MediaPlaybackTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingRecorded_IsNotPlaying()
    {
        Assert.False(new MediaPlaybackTracker().IsPlaying("slack", T0));
    }

    [Fact]
    public void OneFramePlaying_IsPlaying()
    {
        var t = new MediaPlaybackTracker();
        t.Update("slack", "huddle", true, T0);
        Assert.True(t.IsPlaying("slack", T0));
    }

    [Fact]
    public void AnotherFrameReportingNotPlaying_DoesNotOverrideACall()
    {
        // 以前は、通話のフレームのあとに別のフレームが「再生中でない」を送ると、上書きされていた
        var t = new MediaPlaybackTracker();
        t.Update("slack", "huddle", true, T0);
        t.Update("slack", "main", false, T0.AddSeconds(1));
        Assert.True(t.IsPlaying("slack", T0.AddSeconds(2)));
    }

    [Fact]
    public void WhenTheOnlyPlayingFrameStops_IsNotPlaying()
    {
        var t = new MediaPlaybackTracker();
        t.Update("slack", "huddle", true, T0);
        t.Update("slack", "huddle", false, T0.AddSeconds(5));
        Assert.False(t.IsPlaying("slack", T0.AddSeconds(5)));
    }

    [Fact]
    public void WithoutHeartbeat_PlayingExpires()
    {
        // フレームが閉じられても、「再生中」が残り続けないようにする
        var t = new MediaPlaybackTracker();
        t.Update("slack", "huddle", true, T0);
        Assert.True(t.IsPlaying("slack", T0 + MediaPlaybackTracker.Expiry - TimeSpan.FromSeconds(1)));
        Assert.False(t.IsPlaying("slack", T0 + MediaPlaybackTracker.Expiry));
    }

    [Fact]
    public void Heartbeat_KeepsPlayingAlive()
    {
        var t = new MediaPlaybackTracker();
        t.Update("slack", "huddle", true, T0);
        t.Update("slack", "huddle", true, T0.AddSeconds(15));
        Assert.True(t.IsPlaying("slack", T0.AddSeconds(30)));
    }

    [Fact]
    public void ServicesAreIndependent_AndRemoveClearsOne()
    {
        var t = new MediaPlaybackTracker();
        t.Update("slack", "f", true, T0);
        t.Update("youtube", "f", true, T0);
        t.Remove("slack");
        Assert.False(t.IsPlaying("slack", T0));
        Assert.True(t.IsPlaying("youtube", T0));
    }
}

namespace YmbThatuation.Services;

/// <summary>
/// サービスごとの「音声・通話の再生中」を、フレーム(iframe を含む)単位で覚えて、1つにまとめる。
/// 以前は、フレームが送る「再生中か」を、そのまま1つの値に上書きしていた。
/// 通話のフレームが「再生中」を送ったあとに、別のフレームが「再生中でない」を送ると、
/// 通話中なのに「再生中でない」になり、メモリ抑圧や自動スリープの対象になっていた。
/// - 1つでも再生中のフレームがあれば、そのサービスは再生中。
/// - 再生中のフレームは、数秒おきに「まだ再生中」と知らせてくる。
///   一定の時間(Expiry)、知らせが無いフレームは、閉じたものとして数えない
///   (ページが閉じられたり、固まったりしても、「再生中」のまま残らないようにする)。
/// タイマーのスレッドと UI のスレッドの両方から呼ばれるので、ロックで守る。
/// </summary>
public sealed class MediaPlaybackTracker
{
    /// <summary>この時間、知らせが無い「再生中」は、終わったものとして扱う。</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, DateTime>> _playingFrames = new();

    /// <summary>フレームの再生状態を記録する。</summary>
    public void Update(string instanceId, string frameId, bool playing, DateTime now)
    {
        lock (_gate)
        {
            if (playing)
            {
                if (!_playingFrames.TryGetValue(instanceId, out var frames))
                {
                    frames = new Dictionary<string, DateTime>();
                    _playingFrames[instanceId] = frames;
                }
                frames[frameId] = now;
            }
            else if (_playingFrames.TryGetValue(instanceId, out var frames))
            {
                frames.Remove(frameId);
                if (frames.Count == 0) _playingFrames.Remove(instanceId);
            }
        }
    }

    /// <summary>そのサービスのどこかで、再生中か。</summary>
    public bool IsPlaying(string instanceId, DateTime now)
    {
        lock (_gate)
        {
            if (!_playingFrames.TryGetValue(instanceId, out var frames)) return false;
            foreach (var key in frames.Where(kv => now - kv.Value >= Expiry).Select(kv => kv.Key).ToList())
            {
                frames.Remove(key);
            }
            if (frames.Count == 0)
            {
                _playingFrames.Remove(instanceId);
                return false;
            }
            return true;
        }
    }

    /// <summary>サービスを閉じた(スリープ・削除した)ときに、記録を消す。</summary>
    public void Remove(string instanceId)
    {
        lock (_gate)
        {
            _playingFrames.Remove(instanceId);
        }
    }
}

namespace YmbThatuation.Services;

/// <summary>
/// サービスごとの未読件数の推移から「新着として通知すべきか」を判定する。
///
/// 通知の基準値(Baseline)は、通知済みの最大件数を覚えておくためのもの。
/// 件数が基準値を超えたときだけ通知し、同じ件数では繰り返さない。
///
/// 基準値は「その件数で一定時間(StableFor)落ち着いていた」場合にだけ下げる。
/// - 既読にして件数が減り、そのまま落ち着いた後の新着は、基準値が下がっているので通知される。
///   (以前は、一部を既読にした後に新着が来て件数が元に戻ると、通知されなかった)
/// - Google Chat等のSPAがタイトルを頻繁に書き換えて件数が一瞬だけ減る場合は、
///   落ち着く前に戻るため基準値は下がらず、同じ件数での再通知は起きない。
/// </summary>
public sealed class UnreadNotificationTracker
{
    /// <summary>この時間より長く続いた件数は「落ち着いていた」とみなす。</summary>
    public static readonly TimeSpan StableFor = TimeSpan.FromSeconds(15);

    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, State> _states = new();

    public UnreadNotificationTracker(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    private sealed class State
    {
        public uint Current;
        public uint Baseline;
        public DateTime ChangedAt;
    }

    /// <summary>未読件数が変化したときに呼ぶ。通知すべきならtrueを返す。</summary>
    public bool OnCountChanged(string id, uint count)
    {
        var now = _now();
        if (!_states.TryGetValue(id, out var state))
        {
            state = new State { ChangedAt = now };
            _states[id] = state;
        }

        if (count == state.Current) return false;

        var previous = state.Current;
        var previousLasted = now - state.ChangedAt;
        state.Current = count;
        state.ChangedAt = now;

        // 直前の件数が長く続いていたなら、その件数が「現在の既読状態」なので基準値を下げる。
        if (previousLasted >= StableFor && previous < state.Baseline)
        {
            state.Baseline = previous;
        }

        if (count == 0) return false;

        if (count > state.Baseline)
        {
            state.Baseline = count;
            return true;
        }
        return false;
    }

    /// <summary>サービスの削除・スリープ時に状態を捨てる。</summary>
    public void Remove(string id) => _states.Remove(id);
}

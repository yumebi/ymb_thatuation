namespace YmbThatuation.Services;

/// <summary>サービス自身が出した Web 通知(送信者・本文つき)1件分。</summary>
public sealed record WebNotice(string Id, string ServiceName, string Title, string Body, bool Silent = false);

/// <summary>
/// 通知を少しの間溜めて、1つにまとめるための入れ物(タイマーや画面から切り離した純粋なロジック)。
///
/// 通知には2種類ある。
/// - Web 通知: サービス自身が出す(Slack の「◯◯さんからのメッセージ」など)。送信者と本文が分かる。
/// - 未読件数: ページのタイトルの「(3)」などの増加から作る。件数しか分からないが、
///   Web 通知を出さないサービスでも通知できる。
///
/// 同じ新着で両方が来ると二重に通知されてしまうので、Web 通知があるサービスの未読件数の通知は出さない。
/// 先に未読件数が溜まっていても、同じ回に Web 通知が届けば未読件数のほうを捨てる。
/// </summary>
public sealed class NoticeBatcher
{
    /// <summary>Web 通知を出したサービスの、未読件数による通知を抑える時間。タイトルの更新は少し遅れることがある。</summary>
    public static readonly TimeSpan SuppressUnreadFor = TimeSpan.FromSeconds(5);

    private readonly Func<DateTime> _now;
    private readonly List<WebNotice> _web = new();
    private readonly List<UnreadNotice> _unread = new();
    private readonly Dictionary<string, DateTime> _lastWebAt = new();

    public NoticeBatcher(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool HasPending => _web.Count > 0 || _unread.Count > 0;

    /// <summary>Web 通知を溜める。同じ内容(同じサービス・題名・本文)の重複は無視する。</summary>
    public void AddWeb(WebNotice notice)
    {
        _lastWebAt[notice.Id] = _now();
        if (_web.Any(n => n.Id == notice.Id && n.Title == notice.Title && n.Body == notice.Body))
            return;
        _web.Add(notice);
    }

    /// <summary>
    /// 未読件数の通知を溜める。直前に Web 通知を出したサービスのものは抑えて false を返す。
    /// 同じサービスの通知は、新しい件数で置き換える。
    /// </summary>
    public bool AddUnread(UnreadNotice notice)
    {
        if (_lastWebAt.TryGetValue(notice.Id, out var at) && _now() - at < SuppressUnreadFor)
            return false;

        var index = _unread.FindIndex(n => n.Id == notice.Id);
        if (index >= 0) _unread[index] = notice;
        else _unread.Add(notice);
        return true;
    }

    /// <summary>サービスの削除・スリープ時に、溜めていた通知を捨てる。</summary>
    public void Remove(string id)
    {
        _web.RemoveAll(n => n.Id == id);
        _unread.RemoveAll(n => n.Id == id);
        _lastWebAt.Remove(id);
    }

    /// <summary>溜めた通知を取り出して空にする。Web 通知があるサービスの未読件数の通知は除く。</summary>
    public (IReadOnlyList<WebNotice> Web, IReadOnlyList<UnreadNotice> Unread) Drain()
    {
        var web = _web.ToList();
        var webIds = web.Select(n => n.Id).ToHashSet();
        var unread = _unread.Where(n => !webIds.Contains(n.Id)).ToList();
        _web.Clear();
        _unread.Clear();
        return (web, unread);
    }
}

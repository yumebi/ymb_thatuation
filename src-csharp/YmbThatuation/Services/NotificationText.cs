namespace YmbThatuation.Services;

/// <summary>通知の対象になった1サービス分の情報。</summary>
public sealed record UnreadNotice(string Id, string Name, uint Count);

/// <summary>
/// 通知バルーンの文言を組み立てる。
/// 短時間に複数のサービスが通知しても、バルーンは1つしか出せず後のものが前のものを
/// 置き換えてしまうため、まとめて1つの通知にする(呼び出し側で少し待って集める)。
/// 文言は言語ファイルのキーから取り、無い場合は日本語の既定値を使う。
/// </summary>
public static class NotificationText
{
    // Windowsのバルーン通知の上限(タイトル63文字、本文255文字)。
    public const int MaxTitleLength = 63;
    public const int MaxBodyLength = 255;

    /// <returns>タイトル、本文、クリックしたときに開くサービスのId。</returns>
    public static (string Title, string Body, string PrimaryId) Build(
        IReadOnlyList<UnreadNotice> items, IReadOnlyDictionary<string, string> translations)
    {
        if (items.Count == 0) throw new ArgumentException("通知対象がありません。", nameof(items));

        if (items.Count == 1)
        {
            var item = items[0];
            var body = Format(translations, "notify.unread", "未読 {count} 件", ("count", item.Count.ToString()));
            return (Truncate(item.Name, MaxTitleLength), Truncate(body, MaxBodyLength), item.Id);
        }

        var title = Format(translations, "notify.multi.title", "{n} 件のサービスに新着", ("n", items.Count.ToString()));
        var list = string.Join(", ", items.Select(i => $"{i.Name} ({i.Count})"));
        return (Truncate(title, MaxTitleLength), Truncate(list, MaxBodyLength), items[0].Id);
    }

    public static string Format(
        IReadOnlyDictionary<string, string> translations, string key, string fallback,
        params (string Name, string Value)[] values)
    {
        var text = translations.TryGetValue(key, out var found) ? found : fallback;
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value);
        }
        return text;
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}

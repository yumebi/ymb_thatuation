using YmbThatuation.Models;

namespace YmbThatuation.Services;

public static class Recipes
{
    // Google はWebView系UAのログインを弾くことがあるため素のChrome UAを名乗る
    public const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, string> Urls = new()
    {
        ["gmail"] = "https://mail.google.com",
        ["googlecalendar"] = "https://calendar.google.com",
        ["googlechat"] = "https://chat.google.com/",
        ["youtube"] = "https://www.youtube.com",
        ["messenger"] = "https://www.messenger.com",
        ["notion"] = "https://www.notion.so",
        ["chatwork"] = "https://www.chatwork.com",
        ["slack"] = "https://app.slack.com/client",
        ["discord"] = "https://discord.com/app",
        ["deckblue"] = "https://deck.blue",
        ["misskey"] = "https://misskey.io",
        ["x"] = "https://x.com",
        ["teams"] = "https://teams.microsoft.com",
        ["whatsapp"] = "https://web.whatsapp.com",
        ["telegram"] = "https://web.telegram.org/a/",
        ["linkedin"] = "https://www.linkedin.com",
        ["instagram"] = "https://www.instagram.com",
        ["reddit"] = "https://www.reddit.com",
        ["trello"] = "https://trello.com",
        ["github"] = "https://github.com",
        ["twitch"] = "https://www.twitch.tv",
        ["zoom"] = "https://app.zoom.us/wc/home",
        ["googledrive"] = "https://drive.google.com",
        ["googlekeep"] = "https://keep.google.com",
        ["threads"] = "https://www.threads.net",
        ["linear"] = "https://linear.app",
    };

    private static readonly Dictionary<string, string> Letters = new()
    {
        ["gmail"] = "G",
        ["chatwork"] = "C",
        ["slack"] = "S",
        ["discord"] = "D",
        ["messenger"] = "M",
        ["googlecalendar"] = "Ca",
        ["notion"] = "N",
        ["youtube"] = "Y",
        ["deckblue"] = "B",
        ["misskey"] = "Mk",
        ["x"] = "X",
        ["teams"] = "T",
        ["googlechat"] = "Gc",
        ["whatsapp"] = "Wa",
        ["telegram"] = "Tg",
        ["linkedin"] = "Li",
        ["instagram"] = "Ig",
        ["reddit"] = "R",
        ["trello"] = "Tr",
        ["github"] = "Gh",
        ["twitch"] = "Tw",
        ["zoom"] = "Z",
        ["googledrive"] = "Gd",
        ["googlekeep"] = "Gk",
        ["threads"] = "@",
        ["linear"] = "Ln",
    };

    private static readonly HashSet<string> ChromeUaDefaultRecipes = new()
    {
        "gmail", "googlecalendar", "googlechat", "youtube", "messenger", "notion",
        "whatsapp", "linkedin", "instagram", "googledrive", "googlekeep", "threads",
    };

    public static string ResolveUrl(InstanceCfg cfg)
    {
        if (cfg.Recipe == "generic")
        {
            if (!string.IsNullOrEmpty(cfg.Url)) return cfg.Url;
            throw new InvalidOperationException("generic レシピには url が必要です");
        }
        if (Urls.TryGetValue(cfg.Recipe, out var url)) return url;
        throw new InvalidOperationException($"不明なレシピ: {cfg.Recipe}");
    }

    public static bool DefaultChromeUa(string recipe) => ChromeUaDefaultRecipes.Contains(recipe);

    public static string Letter(InstanceCfg cfg)
    {
        if (Letters.TryGetValue(cfg.Recipe, out var letter)) return letter;
        return cfg.Name.Length > 0 ? cfg.Name[0].ToString() : "?";
    }
}

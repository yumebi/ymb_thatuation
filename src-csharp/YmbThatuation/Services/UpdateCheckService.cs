using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YmbThatuation.Services;

/// <summary>
/// GitHub の最新リリース(Releases API)を見て、本体の新バージョン有無を確認する。
/// version.json ではなく公開済みのリリースを見るのは、リリースに失敗しても version.json だけ
/// 先に更新されて、存在しない版を案内してしまうのを避けるため。
/// </summary>
public class UpdateCheckService
{
    private const string Repo = "yumebi/ymb_thatuation";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        // GitHub API は User-Agent が必須。応答が無いまま固まらないよう、短めに打ち切る
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YmbThatuation-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        var current = CurrentVersion;
        try
        {
            var json = await Http.GetStringAsync(GitHubRelease.ApiUrl(Repo));
            var release = GitHubRelease.Parse(json, Repo);
            var latest = release?.Version ?? current;

            return new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                UpdateAvailable = release is not null && GitHubRelease.IsNewer(latest, current),
                ReleaseUrl = release?.Url,
            };
        }
        catch
        {
            return new UpdateCheckResult { CurrentVersion = current, LatestVersion = current, UpdateAvailable = false };
        }
    }

}

public class UpdateCheckResult
{
    [JsonPropertyName("current_version")]
    public string CurrentVersion { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("update_available")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("release_url")]
    public string? ReleaseUrl { get; set; }
}

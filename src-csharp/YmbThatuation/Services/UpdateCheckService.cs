using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YmbThatuation.Services;

/// <summary>
/// GitHubリポジトリのversion.json(raw.githubusercontent.com)を見て、本体の新バージョン有無を確認する。
/// </summary>
public class UpdateCheckService
{
    private const string VersionUrl =
        "https://raw.githubusercontent.com/yumebi/ymb_thatuation/master/version.json";

    private static readonly HttpClient Http = new();
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
            var json = await Http.GetStringAsync(VersionUrl);
            var remote = JsonSerializer.Deserialize<RemoteVersion>(json, JsonOptions);
            var latest = remote?.Version ?? current;
            var updateAvailable = Version.TryParse(latest, out var latestV)
                && Version.TryParse(current, out var currentV)
                && latestV > currentV;

            return new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                UpdateAvailable = updateAvailable,
                ReleaseUrl = remote?.Url,
            };
        }
        catch
        {
            return new UpdateCheckResult { CurrentVersion = current, LatestVersion = current, UpdateAvailable = false };
        }
    }

    private class RemoteVersion
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
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

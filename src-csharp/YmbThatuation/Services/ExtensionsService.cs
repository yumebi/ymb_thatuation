using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using YmbThatuation.Models;

namespace YmbThatuation.Services;

/// <summary>
/// 展開済みChrome拡張の管理(スキャン/CWSインストール/更新/削除/拡張状態リセット)。
/// Tauri版main.rsのextensions関連コマンド相当。
/// </summary>
public class ExtensionsService
{
    private static readonly HttpClient Http = new();

    private readonly ConfigStore _configStore;

    public ExtensionsService(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    public string ExtensionsDir()
    {
        var dir = Path.Combine(_configStore.AppDataDir, "extensions");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// manifest.json内の "__MSG_key__" 形式の文字列を
    /// _locales/&lt;default_locale&gt;/messages.json で解決する。
    /// </summary>
    private static string ResolveExtensionI18n(string extDir, JsonElement json, string raw)
    {
        if (!raw.StartsWith("__MSG_") || !raw.EndsWith("__"))
        {
            return raw;
        }
        var key = raw[6..^2];

        var defaultLocale = json.TryGetProperty("default_locale", out var localeEl) && localeEl.ValueKind == JsonValueKind.String
            ? localeEl.GetString() ?? "en"
            : "en";

        var messagesPath = Path.Combine(extDir, "_locales", defaultLocale, "messages.json");
        if (!File.Exists(messagesPath))
        {
            return raw;
        }

        JsonElement messages;
        try
        {
            messages = JsonDocument.Parse(File.ReadAllText(messagesPath)).RootElement;
        }
        catch
        {
            return raw;
        }

        if (!messages.TryGetProperty(key, out var entry))
        {
            var lower = key.ToLowerInvariant();
            entry = default;
            var found = false;
            foreach (var prop in messages.EnumerateObject())
            {
                if (prop.Name.ToLowerInvariant() == lower)
                {
                    entry = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return raw;
        }

        if (!entry.TryGetProperty("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.String)
        {
            return raw;
        }

        var result = messageEl.GetString() ?? raw;
        if (entry.TryGetProperty("placeholders", out var placeholders) && placeholders.ValueKind == JsonValueKind.Object)
        {
            foreach (var ph in placeholders.EnumerateObject())
            {
                if (ph.Value.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var needle = $"${ph.Name.ToUpperInvariant()}$";
                    result = result.Replace(needle, contentEl.GetString());
                }
            }
        }
        return result;
    }

    /// <summary>
    /// extensions/ 直下の「manifest.jsonを含むフォルダ」を展開済み拡張として列挙する。
    /// </summary>
    public List<ExtensionInfo> ScanExtensions()
    {
        var result = new List<ExtensionInfo>();
        var dir = ExtensionsDir();
        foreach (var path in Directory.EnumerateDirectories(dir))
        {
            var manifestPath = Path.Combine(path, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            JsonElement json;
            try
            {
                json = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
            }
            catch
            {
                continue;
            }

            var fallback = Path.GetFileName(path);
            var name = json.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? fallback
                : fallback;
            var description = json.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString() ?? ""
                : "";
            var version = json.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String
                ? verEl.GetString() ?? "?"
                : "?";
            var manifestVersion = json.TryGetProperty("manifest_version", out var mvEl) && mvEl.ValueKind == JsonValueKind.Number
                ? mvEl.GetUInt64()
                : 2UL;

            result.Add(new ExtensionInfo
            {
                Path = path,
                Name = ResolveExtensionI18n(path, json, name),
                Description = ResolveExtensionI18n(path, json, description),
                Version = version,
                ManifestVersion = manifestVersion,
            });
        }
        return result;
    }

    /// <summary>CWSのURLまたは生IDから拡張ID(32文字のa-p)を抜き出す。</summary>
    public static string? ParseExtensionId(string input)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(input.Trim(), "[A-Za-z]+"))
        {
            var token = m.Value;
            if (token.Length == 32 && token.All(c => c >= 'a' && c <= 'p'))
            {
                return token;
            }
        }
        return null;
    }

    /// <summary>CRX3バイナリからZIP部分を取り出す(マジック"Cr24" + ヘッダ長はオフセット8のu32LE)。</summary>
    private static byte[] CrxToZip(byte[] buf)
    {
        if (buf.Length < 12 || buf[0] != 'C' || buf[1] != 'r' || buf[2] != '2' || buf[3] != '4')
        {
            throw new InvalidOperationException("CRXファイルではありません");
        }
        var headerSize = BitConverter.ToUInt32(buf, 8);
        var offset = 12 + (int)headerSize;
        if (offset > buf.Length)
        {
            throw new InvalidOperationException("CRXヘッダが壊れています");
        }
        return buf[offset..];
    }

    /// <summary>CWSから指定IDのCRXをダウンロードしてdestに展開する。</summary>
    private static async Task FetchAndExtractCrxAsync(string id, string dest)
    {
        var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=137.0.0.0&acceptformat=crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";

        byte[] crx;
        try
        {
            crx = await Http.GetByteArrayAsync(url);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"ダウンロード失敗(IDが正しいか確認してください): {e.Message}");
        }

        var zip = CrxToZip(crx);

        Directory.CreateDirectory(dest);
        using (var zipStream = new MemoryStream(zip))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(dest, overwriteFiles: true);
        }

        if (!File.Exists(Path.Combine(dest, "manifest.json")))
        {
            throw new InvalidOperationException("展開結果にmanifest.jsonがありません");
        }
    }

    public async Task<string> InstallExtensionFromCwsAsync(string idOrUrl)
    {
        var id = ParseExtensionId(idOrUrl)
            ?? throw new InvalidOperationException("拡張IDが見つかりません。CWSのURLか32文字のIDを入力してください");

        var dest = Path.Combine(ExtensionsDir(), id);
        if (Directory.Exists(dest))
        {
            Directory.Delete(dest, recursive: true);
        }
        try
        {
            await FetchAndExtractCrxAsync(id, dest);
        }
        catch
        {
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }
            throw;
        }
        return id;
    }

    /// <summary>"1.2.3" 形式のバージョン文字列を比較可能な数値列に変換する。</summary>
    private static List<ulong> VersionTuple(string v)
    {
        return v.Split('.').Select(p => ulong.TryParse(p, out var n) ? n : 0UL).ToList();
    }

    private static int CompareVersionTuples(List<ulong> a, List<ulong> b)
    {
        var len = Math.Max(a.Count, b.Count);
        for (var i = 0; i < len; i++)
        {
            var av = i < a.Count ? a[i] : 0UL;
            var bv = i < b.Count ? b[i] : 0UL;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    /// <summary>
    /// CWSからインストール済みの拡張(フォルダ名=32文字のCWS拡張ID)について、
    /// 最新版をダウンロードしバージョンが上がっていれば差し替える。
    /// </summary>
    public async Task<List<string>> UpdateExtensionsAsync()
    {
        var dir = ExtensionsDir();
        var updated = new List<string>();

        foreach (var path in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 32 || !name.All(c => c >= 'a' && c <= 'p'))
            {
                continue;
            }

            var manifestPath = Path.Combine(path, "manifest.json");
            JsonElement json;
            try
            {
                json = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
            }
            catch
            {
                continue;
            }

            var currentVersion = json.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String
                ? verEl.GetString() ?? "0"
                : "0";
            var extName = ResolveExtensionI18n(path, json,
                json.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? name
                    : name);

            var tmpDest = Path.Combine(dir, $"{name}.update-tmp");
            if (Directory.Exists(tmpDest))
            {
                Directory.Delete(tmpDest, recursive: true);
            }
            try
            {
                await FetchAndExtractCrxAsync(name, tmpDest);
            }
            catch
            {
                if (Directory.Exists(tmpDest))
                {
                    Directory.Delete(tmpDest, recursive: true);
                }
                continue;
            }

            var newVersion = "";
            try
            {
                var newJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmpDest, "manifest.json"))).RootElement;
                if (newJson.TryGetProperty("version", out var newVerEl) && newVerEl.ValueKind == JsonValueKind.String)
                {
                    newVersion = newVerEl.GetString() ?? "";
                }
            }
            catch
            {
                // ignore
            }

            if (CompareVersionTuples(VersionTuple(newVersion), VersionTuple(currentVersion)) > 0)
            {
                Directory.Delete(path, recursive: true);
                Directory.Move(tmpDest, path);
                updated.Add($"{extName} ({currentVersion} → {newVersion})");
            }
            else
            {
                Directory.Delete(tmpDest, recursive: true);
            }
        }
        return updated;
    }

    public void RemoveExtension(string path)
    {
        var dir = ExtensionsDir();
        var fullDir = Path.GetFullPath(dir);
        var fullTarget = Path.GetFullPath(path);
        if (!fullTarget.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拡張機能フォルダ外は削除できません");
        }
        Directory.Delete(fullTarget, recursive: true);
    }

    public void OpenExtensionsDir()
    {
        System.Diagnostics.Process.Start("explorer.exe", ExtensionsDir());
    }

    /// <summary>
    /// 拡張機能のオンボーディング/初期設定状態をリセットし、次回起動時に
    /// 「インストール直後」と同じ状態に戻す(1Password等の再設定対応)。
    /// </summary>
    public void ResetExtensionState(string id)
    {
        var profile = Path.Combine(_configStore.AppDataDir, "webview2", "EBWebView", $"WV2Profile_{id}");
        if (!Directory.Exists(profile)) return;

        foreach (var name in new[]
        {
            "Extension State",
            "Local Extension Settings",
            "Sync Extension Settings",
            "Extension Rules",
            "Extension Scripts",
            "Managed Extension Settings",
        })
        {
            var p = Path.Combine(profile, name);
            if (Directory.Exists(p))
            {
                Directory.Delete(p, recursive: true);
            }
        }

        var idb = Path.Combine(profile, "IndexedDB");
        if (Directory.Exists(idb))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(idb))
            {
                if (Path.GetFileName(entry).StartsWith("chrome-extension_"))
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
            }
        }
    }
}

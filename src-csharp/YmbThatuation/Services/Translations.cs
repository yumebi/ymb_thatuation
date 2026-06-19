using System.IO;
using System.Text.Json;

namespace YmbThatuation.Services;

/// <summary>
/// wwwroot/lang/&lt;language&gt;.json を読み込む共通ヘルパー。
/// IPCの get_translations とネイティブUI(コンテキストメニュー・トレイ)で共用する。
/// </summary>
public static class Translations
{
    public static Dictionary<string, string> Load(string wwwrootDir, string language)
    {
        var path = Path.Combine(wwwrootDir, "lang", $"{language}.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(wwwrootDir, "lang", "ja.json");
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
    }
}

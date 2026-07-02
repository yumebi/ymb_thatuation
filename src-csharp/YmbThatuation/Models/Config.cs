using System.Text.Json.Serialization;

namespace YmbThatuation.Models;

public class InstanceCfg
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("recipe")]
    public string Recipe { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("keep_awake")]
    public bool KeepAwake { get; set; }

    [JsonPropertyName("chatwork_token")]
    public string? ChatworkToken { get; set; }

    [JsonPropertyName("custom_icon")]
    public string? CustomIcon { get; set; }

    [JsonPropertyName("chrome_ua")]
    public bool? ChromeUa { get; set; }

    [JsonPropertyName("force_renavigate")]
    public bool ForceRenavigate { get; set; }

    [JsonPropertyName("notify_muted")]
    public bool NotifyMuted { get; set; }
}

public class Settings
{
    [JsonPropertyName("sleep_after_minutes")]
    public ulong SleepAfterMinutes { get; set; } = 15;

    [JsonPropertyName("close_to_tray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("start_minimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; }

    [JsonPropertyName("notifications")]
    public bool Notifications { get; set; } = true;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "ja";

    [JsonPropertyName("notification_sound")]
    public bool NotificationSound { get; set; } = true;

    [JsonPropertyName("notification_sound_path")]
    public string NotificationSoundPath { get; set; } = "";

    [JsonPropertyName("staggered_startup")]
    public bool StaggeredStartup { get; set; } = true;

    [JsonPropertyName("startup_delay_seconds")]
    public uint StartupDelaySeconds { get; set; } = 8;

    [JsonPropertyName("keyboard_shortcuts_enabled")]
    public bool KeyboardShortcutsEnabled { get; set; }

    [JsonPropertyName("active_ring_style")]
    public string ActiveRingStyle { get; set; } = "rainbow";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("url_bar_enabled")]
    public bool UrlBarEnabled { get; set; }

    [JsonPropertyName("unread_action")]
    public string UnreadAction { get; set; } = "pulse";

    /// <summary>
    /// force_renavigate(真っ白画面の再読み込みキック)がC#移植版で未実装だった期間に
    /// 作られたconfigに対する一度きりの移行フラグ。既にtrueなら再適用しない(ユーザーが
    /// 個別に無効化した設定を勝手に戻さないため)。ConfigStore.Load参照。
    /// </summary>
    [JsonPropertyName("force_renavigate_migrated")]
    public bool ForceRenavigateMigrated { get; set; }
}

public class Config
{
    [JsonPropertyName("settings")]
    public Settings Settings { get; set; } = new();

    [JsonPropertyName("instances")]
    public List<InstanceCfg> Instances { get; set; } = new();

    public static Config CreateDefault()
    {
        InstanceCfg Inst(string id, string recipe, string name, string color) => new()
        {
            Id = id,
            Recipe = recipe,
            Name = name,
            Color = color,
        };

        return new Config
        {
            Settings = new Settings(),
            Instances = new List<InstanceCfg>
            {
                Inst("gmail-work", "gmail", "Gmail (仕事)", "#5b8def"),
                Inst("gmail-personal", "gmail", "Gmail (個人)", "#41b883"),
                Inst("chatwork-a", "chatwork", "Chatwork (A)", "#5b8def"),
                Inst("chatwork-b", "chatwork", "Chatwork (B)", "#e8a33d"),
            },
        };
    }
}

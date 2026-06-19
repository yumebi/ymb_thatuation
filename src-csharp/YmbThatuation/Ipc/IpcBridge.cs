using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using YmbThatuation.Models;
using YmbThatuation.Services;

namespace YmbThatuation.Ipc;

/// <summary>
/// ui/ のJSから chrome.webview.hostObjects.ymb.InvokeAsync(cmd, argsJson) で呼び出される
/// IPCディスパッチャ。Tauriの invoke("cmd", args) 相当(Phase1コマンドのみ実装)。
/// </summary>
[ComVisible(true)]
public class IpcBridge
{
    private readonly ConfigStore _configStore;
    private readonly InstanceManager _instanceManager;
    private readonly string _wwwrootDir;

    public IpcBridge(ConfigStore configStore, InstanceManager instanceManager, string wwwrootDir)
    {
        _configStore = configStore;
        _instanceManager = instanceManager;
        _wwwrootDir = wwwrootDir;
    }

    public async Task<string> InvokeAsync(string command, string argsJson)
    {
        try
        {
            var args = string.IsNullOrEmpty(argsJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)
                  ?? new Dictionary<string, JsonElement>();

            object? result = command switch
            {
                "get_config" => _configStore.Get(),
                "get_translations" => GetTranslations(args),
                "ui_state" => _instanceManager.GetUiState(),
                "select_instance" => await SelectInstance(args),
                "activate" => await Activate(args),
                "sleep_service" => await Sleep(args),
                "reload_service" => await Reload(args),
                "open_settings" => await OpenSettings(args),
                "add_instance" => AddInstance(args),
                "update_instance" => UpdateInstance(args),
                "remove_instance" => await RemoveInstance(args),
                "move_instance" => MoveInstance(args),
                "update_settings" => UpdateSettings(args),
                "show_context_menu" => await ShowContextMenu(args),
                "close_context_menu" => null,
                "host_memory_mb" => _instanceManager.Memory?.HostMemMb ?? 0UL,
                "apps_memory_mb" => _instanceManager.Memory?.AppsMemMb ?? 0UL,
                "refresh_memory_now" => RefreshMemoryNow(),
                "get_extensions" => _instanceManager.Extensions?.ScanExtensions() ?? new List<ExtensionInfo>(),
                "install_extension_from_cws" => await InstallExtensionFromCws(args),
                "update_extensions" => await UpdateExtensions(),
                "remove_extension" => RemoveExtension(args),
                "open_extensions_dir" => OpenExtensionsDir(),
                "reset_extension_state" => await ResetExtensionState(args),
                "restart_app" => RestartApp(),
                "export_settings" => ExportSettings(),
                "import_settings" => ImportSettings(),
                "select_notification_sound" => SelectNotificationSound(),
                "reset_notification_sound" => ResetNotificationSound(),
                "test_notification_sound" => TestNotificationSound(),
                "get_app_version" => GetAppVersion(),
                _ => throw new InvalidOperationException($"未実装のコマンド: {command}"),
            };

            return JsonSerializer.Serialize(result);
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { __error__ = e.Message });
        }
    }

    private static string GetId(Dictionary<string, JsonElement> args) => GetString(args, "id");

    private static string GetString(Dictionary<string, JsonElement> args, string key)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? "";
        }
        throw new InvalidOperationException($"{key} が指定されていません");
    }

    private static string? GetOptionalString(Dictionary<string, JsonElement> args, string key)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private static bool GetBool(Dictionary<string, JsonElement> args, string key, bool fallback = false)
    {
        if (args.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
        {
            return el.GetBoolean();
        }
        return fallback;
    }

    private static bool? GetOptionalBool(Dictionary<string, JsonElement> args, string key)
    {
        if (args.TryGetValue(key, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
        {
            return el.GetBoolean();
        }
        return null;
    }

    private Dictionary<string, string> GetTranslations(Dictionary<string, JsonElement> args)
    {
        var language = args.TryGetValue("language", out var langEl) ? langEl.GetString() ?? "ja" : "ja";
        return Translations.Load(_wwwrootDir, language);
    }

    private async Task<object?> SelectInstance(Dictionary<string, JsonElement> args)
    {
        await _instanceManager.SelectInstanceAsync(GetId(args));
        return null;
    }

    private async Task<object?> Activate(Dictionary<string, JsonElement> args)
    {
        await _instanceManager.ActivateAsync(GetId(args));
        return null;
    }

    private async Task<object?> Sleep(Dictionary<string, JsonElement> args)
    {
        await _instanceManager.SleepAsync(GetId(args));
        return null;
    }

    private async Task<object?> Reload(Dictionary<string, JsonElement> args)
    {
        await _instanceManager.ReloadAsync(GetId(args));
        return null;
    }

    private async Task<object?> OpenSettings(Dictionary<string, JsonElement> args)
    {
        var editId = GetOptionalString(args, "editId");
        await _instanceManager.OpenSettingsAsync(editId);
        return null;
    }

    private object? AddInstance(Dictionary<string, JsonElement> args)
    {
        var recipe = GetString(args, "recipe");
        var cfg = new InstanceCfg
        {
            Id = $"{recipe}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Recipe = recipe,
            Name = GetString(args, "name"),
            Color = GetString(args, "color"),
            Url = GetOptionalString(args, "url"),
        };
        Recipes.ResolveUrl(cfg); // レシピ妥当性チェック
        _configStore.Update(c => c.Instances.Add(cfg));
        return null;
    }

    private object? UpdateInstance(Dictionary<string, JsonElement> args)
    {
        var id = GetId(args);
        var name = GetString(args, "name");
        var color = GetString(args, "color");
        var url = GetOptionalString(args, "url");
        var keepAwake = GetBool(args, "keepAwake");
        var chatworkToken = GetOptionalString(args, "chatworkToken");
        var customIcon = GetOptionalString(args, "customIcon");
        var chromeUa = GetOptionalBool(args, "chromeUa");
        var forceRenavigate = GetBool(args, "forceRenavigate");
        var notifyMuted = GetBool(args, "notifyMuted");

        _configStore.Update(c =>
        {
            var inst = c.Instances.FirstOrDefault(i => i.Id == id)
                ?? throw new InvalidOperationException($"インスタンスが見つかりません: {id}");
            inst.Name = name;
            inst.Color = color;
            inst.KeepAwake = keepAwake;
            if (inst.Recipe == "generic")
            {
                inst.Url = string.IsNullOrEmpty(url) ? null : url;
            }
            if (inst.Recipe == "chatwork")
            {
                inst.ChatworkToken = string.IsNullOrEmpty(chatworkToken) ? null : chatworkToken;
            }
            inst.CustomIcon = string.IsNullOrEmpty(customIcon) ? null : customIcon;
            inst.ChromeUa = chromeUa;
            inst.ForceRenavigate = forceRenavigate;
            inst.NotifyMuted = notifyMuted;
        });
        return null;
    }

    private async Task<object?> RemoveInstance(Dictionary<string, JsonElement> args)
    {
        var id = GetId(args);
        await _instanceManager.RemoveInstanceAsync(id);
        _configStore.Update(c => c.Instances.RemoveAll(i => i.Id == id));
        return null;
    }

    private object? MoveInstance(Dictionary<string, JsonElement> args)
    {
        var id = GetId(args);
        var delta = args.TryGetValue("delta", out var deltaEl) ? deltaEl.GetInt32() : 0;
        _configStore.Update(c =>
        {
            var pos = c.Instances.FindIndex(i => i.Id == id);
            if (pos < 0) throw new InvalidOperationException($"インスタンスが見つかりません: {id}");
            var newPos = pos + delta;
            if (newPos < 0 || newPos >= c.Instances.Count) return;
            (c.Instances[pos], c.Instances[newPos]) = (c.Instances[newPos], c.Instances[pos]);
        });
        return null;
    }

    private async Task<object?> ShowContextMenu(Dictionary<string, JsonElement> args)
    {
        var id = GetId(args);
        var alive = GetBool(args, "alive");
        var x = args.TryGetValue("x", out var xEl) ? xEl.GetDouble() : 0;
        var y = args.TryGetValue("y", out var yEl) ? yEl.GetDouble() : 0;
        await _instanceManager.ShowContextMenuAsync(id, alive, x, y);
        return null;
    }

    private async Task<object?> InstallExtensionFromCws(Dictionary<string, JsonElement> args)
    {
        var idOrUrl = GetString(args, "idOrUrl");
        if (_instanceManager.Extensions == null) throw new InvalidOperationException("拡張機能サービスが未初期化です");
        return await _instanceManager.Extensions.InstallExtensionFromCwsAsync(idOrUrl);
    }

    private async Task<object?> UpdateExtensions()
    {
        if (_instanceManager.Extensions == null) return new List<string>();
        return await _instanceManager.Extensions.UpdateExtensionsAsync();
    }

    private object? RemoveExtension(Dictionary<string, JsonElement> args)
    {
        var path = GetString(args, "path");
        _instanceManager.Extensions?.RemoveExtension(path);
        return null;
    }

    private object? OpenExtensionsDir()
    {
        _instanceManager.Extensions?.OpenExtensionsDir();
        return null;
    }

    private async Task<object?> ResetExtensionState(Dictionary<string, JsonElement> args)
    {
        var id = GetId(args);
        if (_instanceManager.IsAlive(id))
        {
            await _instanceManager.SleepAsync(id);
        }
        _instanceManager.Extensions?.ResetExtensionState(id);
        return null;
    }

    private object? RefreshMemoryNow()
    {
        _instanceManager.Memory?.Refresh(_instanceManager.GetBrowserProcessIds());
        return null;
    }

    private object? UpdateSettings(Dictionary<string, JsonElement> args)
    {
        _configStore.Update(c =>
        {
            if (args.TryGetValue("sleepAfterMinutes", out var sleepEl))
            {
                c.Settings.SleepAfterMinutes = (ulong)sleepEl.GetInt64();
            }
            c.Settings.CloseToTray = GetBool(args, "closeToTray", c.Settings.CloseToTray);
            c.Settings.StartMinimized = GetBool(args, "startMinimized", c.Settings.StartMinimized);
            c.Settings.Autostart = GetBool(args, "autostart", c.Settings.Autostart);
            c.Settings.Notifications = GetBool(args, "notifications", c.Settings.Notifications);
            c.Settings.Language = GetOptionalString(args, "language") ?? c.Settings.Language;
            c.Settings.NotificationSound = GetBool(args, "notificationSound", c.Settings.NotificationSound);
            c.Settings.StaggeredStartup = GetBool(args, "staggeredStartup", c.Settings.StaggeredStartup);
            if (args.TryGetValue("startupDelaySeconds", out var delayEl))
            {
                c.Settings.StartupDelaySeconds = (uint)delayEl.GetInt64();
            }
        });
        AutostartService.SetEnabled(_configStore.Get().Settings.Autostart);
        return null;
    }

    /// <summary>本体のバージョン文字列を返す(設定画面「本体」セクション表示用)。</summary>
    private static string GetAppVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "?";
    }

    /// <summary>本体を再起動する(Tauri版のrestart_app相当)。</summary>
    private object? RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            System.Diagnostics.Process.Start(exePath);
        }
        _instanceManager.Tray?.Quit();
        return null;
    }

    /// <summary>設定(config.json相当)をJSONファイルへ書き出す。ログイン情報は対象外。</summary>
    private object? ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON|*.json",
            FileName = "ymb-thatuation-settings.json",
        };
        if (dialog.ShowDialog() != true) return false;

        var json = JsonSerializer.Serialize(_configStore.Get(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json);
        return true;
    }

    /// <summary>JSONファイルから設定を読み込み、現在の設定を置き換える(反映には再起動が必要)。</summary>
    private object? ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog() != true) return false;

        var json = File.ReadAllText(dialog.FileName);
        var imported = JsonSerializer.Deserialize<Config>(json)
            ?? throw new InvalidOperationException("設定ファイルの読み込みに失敗しました");
        _configStore.Replace(imported);
        return true;
    }

    /// <summary>通知音用のカスタム音声ファイル(wav/mp3)を選択し、設定に保存する。</summary>
    private object? SelectNotificationSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.wav;*.mp3" };
        if (dialog.ShowDialog() != true) return null;

        _configStore.Update(c => c.Settings.NotificationSoundPath = dialog.FileName);
        return dialog.FileName;
    }

    /// <summary>通知音をWindows標準に戻す。</summary>
    private object? ResetNotificationSound()
    {
        _configStore.Update(c => c.Settings.NotificationSoundPath = "");
        return null;
    }

    /// <summary>通知音をテスト再生する。</summary>
    private object? TestNotificationSound()
    {
        _instanceManager.Tray?.PlayNotificationSound();
        return null;
    }
}

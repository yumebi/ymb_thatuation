using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YmbThatuation.Ipc;
using YmbThatuation.Models;

namespace YmbThatuation.Services;

/// <summary>
/// 各サービスのWebView2(ProfileNameでプロファイル分離)の生成・破棄・表示切替を管理する。
/// Tauri版のactivate/select_instance/sleep_service/hide_others相当。
/// </summary>
public class InstanceManager
{
    private const string SettingsKey = "__settings";

    /// <summary>
    /// audio/video要素の再生状態を監視し、変化時にwindow.chrome.webview.postMessageで
    /// ホストへ通知する。再生中のサービスを自動スリープの対象外にするために使う。
    /// </summary>
    private const string MediaPlaybackScript = @"(function(){
  if (window.__ymbMediaScanStarted) return;
  window.__ymbMediaScanStarted = true;
  function isAnyPlaying() {
    var els = document.querySelectorAll('audio, video');
    for (var i = 0; i < els.length; i++) {
      if (!els[i].paused && !els[i].ended && els[i].currentTime > 0) return true;
    }
    return false;
  }
  var last = null;
  function report() {
    var playing = isAnyPlaying();
    if (playing === last) return;
    last = playing;
    try { window.chrome.webview.postMessage({ type: 'media', playing: playing }); } catch (e) {}
  }
  document.addEventListener('play', report, true);
  document.addEventListener('pause', report, true);
  document.addEventListener('ended', report, true);
  setInterval(report, 5000);
  report();
})();";

    private readonly Grid _contentHost;
    private readonly WebView2 _welcomeWebView;
    private readonly WebView2 _sidebarWebView;
    private readonly CoreWebView2Environment _environment;
    private readonly ConfigStore _configStore;
    private readonly string _wwwrootDir;
    private readonly string _virtualHost;
    private readonly Dictionary<string, WebView2> _webviews = new();
    private readonly Dictionary<string, uint> _unread = new();
    private readonly Dictionary<string, DateTime> _hiddenSince = new();
    private readonly Dictionary<string, bool> _mediaPlaying = new();
    private WebView2? _settingsWebView;
    private System.Threading.Timer? _backgroundTimer;
    private int _backgroundTick;

    public string? ActiveId { get; private set; }
    public string? PendingWakeId { get; private set; }
    public IpcBridge? Bridge { get; set; }
    public TrayService? Tray { get; set; }
    public MemoryService? Memory { get; set; }
    public ExtensionsService? Extensions { get; set; }
    public UpdateCheckService? UpdateCheck { get; set; }

    public InstanceManager(Grid contentHost, WebView2 welcomeWebView, WebView2 sidebarWebView, CoreWebView2Environment environment, ConfigStore configStore, string wwwrootDir, string virtualHost)
    {
        _contentHost = contentHost;
        _welcomeWebView = welcomeWebView;
        _sidebarWebView = sidebarWebView;
        _environment = environment;
        _configStore = configStore;
        _wwwrootDir = wwwrootDir;
        _virtualHost = virtualHost;
    }

    public bool IsAlive(string id) => _webviews.ContainsKey(id);

    public Dictionary<string, uint> GetBrowserProcessIds()
    {
        var result = new Dictionary<string, uint>();
        foreach (var (id, webview) in _webviews)
        {
            try
            {
                if (webview.CoreWebView2 != null)
                {
                    result[id] = (uint)webview.CoreWebView2.BrowserProcessId;
                }
            }
            catch (InvalidOperationException)
            {
                // ブラウザプロセスがクラッシュ後、CoreWebView2が無効になっている場合はスキップ。
            }
        }
        return result;
    }

    public List<UiInstance> GetUiState()
    {
        var config = _configStore.Get();
        var result = new List<UiInstance>();
        foreach (var inst in config.Instances)
        {
            var alive = IsAlive(inst.Id);
            result.Add(new UiInstance
            {
                Id = inst.Id,
                Name = inst.Name,
                Color = inst.Color,
                Letter = Recipes.Letter(inst),
                Recipe = inst.Recipe,
                CustomIcon = inst.CustomIcon,
                Alive = alive,
                Active = alive ? ActiveId == inst.Id : PendingWakeId == inst.Id,
                Unread = _unread.GetValueOrDefault(inst.Id, 0u),
            });
        }
        return result;
    }

    public async Task ActivateAsync(string id)
    {
        if (PendingWakeId == id) PendingWakeId = null;

        if (!_webviews.TryGetValue(id, out var webview))
        {
            var config = _configStore.Get();
            var inst = config.Instances.FirstOrDefault(i => i.Id == id)
                ?? throw new InvalidOperationException($"インスタンスが見つかりません: {id}");

            webview = new WebView2();
            _contentHost.Children.Add(webview);

            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = id;
            await webview.EnsureCoreWebView2Async(_environment, options);

            var useChromeUa = inst.ChromeUa ?? Recipes.DefaultChromeUa(inst.Recipe);
            if (useChromeUa)
            {
                webview.CoreWebView2.Settings.UserAgent = Recipes.ChromeUserAgent;
            }

            await webview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(UnreadParser.InjectedScript);
            webview.CoreWebView2.DocumentTitleChanged += (_, _) =>
                OnTitleChanged(id, webview.CoreWebView2.DocumentTitle);

            await webview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MediaPlaybackScript);
            webview.CoreWebView2.WebMessageReceived += (_, e) => OnWebMessageReceived(id, e);

            webview.CoreWebView2.ContextMenuRequested += (_, e) => OnContextMenuRequested(e);
            webview.CoreWebView2.ProcessFailed += (_, e) => OnProcessFailed(id, e);
            webview.CoreWebView2.NewWindowRequested += (_, e) => OnNewWindowRequested(e);

            await LoadExtensionsIntoAsync(webview);

            webview.Source = new Uri(Recipes.ResolveUrl(inst));
            _webviews[id] = webview;
        }

        HideOthers(id);
        ActiveId = id;
        webview.Visibility = Visibility.Visible;
    }

    /// <summary>展開済みChrome拡張をWebView2プロファイルに読み込む。Tauri版のload_extensions_into相当。</summary>
    private async Task LoadExtensionsIntoAsync(WebView2 webview)
    {
        if (Extensions == null) return;
        foreach (var ext in Extensions.ScanExtensions())
        {
            try
            {
                await webview.CoreWebView2.Profile.AddBrowserExtensionAsync(ext.Path);
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"[ext] failed {ext.Path}: {e.Message}");
            }
        }
    }

    public async Task SelectInstanceAsync(string id)
    {
        if (IsAlive(id))
        {
            await ActivateAsync(id);
            return;
        }

        HideOthers(null);
        PendingWakeId = id;
        _welcomeWebView.Visibility = Visibility.Visible;
    }

    public Task SleepAsync(string id)
    {
        if (_webviews.Remove(id, out var webview))
        {
            _contentHost.Children.Remove(webview);
            webview.Dispose();
        }
        ClearUnread(id);
        _mediaPlaying.Remove(id);
        if (PendingWakeId == id) PendingWakeId = null;
        if (ActiveId == id)
        {
            ActiveId = null;
            _welcomeWebView.Visibility = Visibility.Visible;
        }
        return Task.CompletedTask;
    }

    public Task ReloadAsync(string id)
    {
        if (_webviews.TryGetValue(id, out var webview))
        {
            webview.CoreWebView2.Reload();
        }
        return Task.CompletedTask;
    }

    public Task RemoveInstanceAsync(string id)
    {
        if (_webviews.Remove(id, out var webview))
        {
            _contentHost.Children.Remove(webview);
            webview.Dispose();
        }
        ClearUnread(id);
        _mediaPlaying.Remove(id);
        if (PendingWakeId == id) PendingWakeId = null;
        if (ActiveId == id) ActiveId = null;
        return Task.CompletedTask;
    }

    private void ClearUnread(string id)
    {
        if (_unread.Remove(id))
        {
            Tray?.UpdateOverlayBadge(_unread.Values.Sum(v => (int)v));
        }
    }

    /// <summary>MediaPlaybackScriptからのpostMessageを受け、再生中状態を記録する。</summary>
    private void OnWebMessageReceived(string id, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
            if (json.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "media"
                && json.TryGetProperty("playing", out var playingEl))
            {
                _mediaPlaying[id] = playingEl.GetBoolean();
            }
        }
        catch (JsonException)
        {
            // 不正な形式のメッセージは無視。
        }
    }

    private void OnTitleChanged(string id, string title)
    {
        var count = UnreadParser.Parse(title);
        var prev = _unread.GetValueOrDefault(id, 0u);
        if (count == prev) return;

        _unread[id] = count;
        Tray?.UpdateOverlayBadge(_unread.Values.Sum(v => (int)v));

        var config = _configStore.Get();
        var inst = config.Instances.FirstOrDefault(i => i.Id == id);
        if (config.Settings.Notifications && count > prev && inst?.NotifyMuted != true)
        {
            Tray?.ShowNotification(inst?.Name ?? id, $"未読 {count} 件");
            Tray?.PlayNotificationSound();
        }
    }

    /// <summary>
    /// ブラウザプロセスクラッシュ(新規ポップアップの鍵マーククリック等)時に、
    /// 無効化されたWebViewを破棄してインスタンスを再生成可能な状態に戻す。
    /// </summary>
    private void OnProcessFailed(string id, CoreWebView2ProcessFailedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[process-failed] {id}: {e.ProcessFailedKind}");

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
            || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // ページ単体のクラッシュ/応答なしは、Controller自体は有効なため再読み込みで復旧する。
            if (_webviews.TryGetValue(id, out var renderFailedWebview))
            {
                renderFailedWebview.CoreWebView2.Reload();
            }
            return;
        }

        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;

        if (_webviews.Remove(id, out var webview))
        {
            _contentHost.Children.Remove(webview);
            webview.Dispose();
        }
        if (ActiveId == id)
        {
            ActiveId = null;
            _welcomeWebView.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// target="_blank"等の新規ウインドウ要求は、WebView2既定ポップアップ(自前アドレスバー、
    /// 鍵マーククリックでブラウザプロセスがクラッシュする)も自前WebView2ポップアップ
    /// (EnsureCoreWebView2Asyncがハングする)も問題があるため、デフォルトブラウザで開く。
    /// </summary>
    private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenInExternalBrowser(e.Uri);
    }

    /// <summary>
    /// ページ側JSが任意文字列を渡せる(window.open/リンクhref)ため、http/https以外
    /// (ローカルパスやUNCパス等)はShellExecuteで実行されないよう拒否する。
    /// </summary>
    public static void OpenInExternalBrowser(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;

        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>
    /// サービスページ内のリンク右クリック時、既定の「リンクを新しいウインドウ/タブで開く」を
    /// 「デフォルトブラウザで開く」1項目に置き換える(NewWindowRequestedの挙動と一致させ、
    /// 「開く」系の項目が複数並ぶのを防ぐ)。
    /// </summary>
    private void OnContextMenuRequested(CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (string.IsNullOrEmpty(target.LinkUri)) return;

        for (var i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            var name = e.MenuItems[i].Name;
            if (name == "openLinkInNewWindow" || name == "openLinkInNewTab")
            {
                e.MenuItems.RemoveAt(i);
            }
        }

        var config = _configStore.Get();
        var t = Translations.Load(_wwwrootDir, config.Settings.Language);
        var label = t.GetValueOrDefault("ctxmenu.open_in_browser", "デフォルトブラウザで開く");

        var item = _environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => OpenInExternalBrowser(target.LinkUri);
        e.MenuItems.Insert(0, item);
    }

    public async Task ShowContextMenuAsync(string id, bool alive, double x, double y)
    {
        var config = _configStore.Get();
        var t = Translations.Load(_wwwrootDir, config.Settings.Language);

        var menu = new ContextMenu
        {
            PlacementTarget = _sidebarWebView,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = x,
            VerticalOffset = y,
        };

        if (alive)
        {
            menu.Items.Add(MakeMenuItem(t, "ctxmenu.sleep", () => SleepAsync(id)));
            menu.Items.Add(MakeMenuItem(t, "ctxmenu.reload", () => ReloadAsync(id)));
        }
        else
        {
            menu.Items.Add(MakeMenuItem(t, "ctxmenu.wake_up", () => ActivateAsync(id)));
        }
        menu.Items.Add(MakeMenuItem(t, "ctxmenu.edit", () => OpenSettingsAsync(id)));

        menu.IsOpen = true;
        await Task.CompletedTask;
    }

    private static MenuItem MakeMenuItem(Dictionary<string, string> t, string key, Func<Task> action)
    {
        var item = new MenuItem { Header = t.GetValueOrDefault(key, key) };
        item.Click += async (_, _) => await action();
        return item;
    }

    public async Task OpenSettingsAsync(string? editId)
    {
        if (_settingsWebView == null)
        {
            _settingsWebView = new WebView2();
            _contentHost.Children.Add(_settingsWebView);

            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "settings";
            await _settingsWebView.EnsureCoreWebView2Async(_environment, options);
            _settingsWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                _virtualHost, _wwwrootDir, CoreWebView2HostResourceAccessKind.Allow);
            if (Bridge != null)
            {
                _settingsWebView.CoreWebView2.AddHostObjectToScript("ymb", Bridge);
            }

            var page = editId != null ? $"settings.html?edit={Uri.EscapeDataString(editId)}" : "settings.html";
            _settingsWebView.Source = new Uri($"https://{_virtualHost}/{page}");
        }
        else if (editId != null)
        {
            await _settingsWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.startEditById && window.startEditById({JsonSerializer.Serialize(editId)})");
        }

        HideOthers(SettingsKey);
        _settingsWebView.Visibility = Visibility.Visible;
    }

    private void HideOthers(string? keep)
    {
        foreach (var (otherId, webview) in _webviews)
        {
            if (otherId == keep)
            {
                webview.Visibility = Visibility.Visible;
                _hiddenSince.Remove(otherId);
            }
            else
            {
                webview.Visibility = Visibility.Collapsed;
                _hiddenSince.TryAdd(otherId, DateTime.UtcNow);
            }
        }
        _welcomeWebView.Visibility = keep == null ? Visibility.Visible : Visibility.Collapsed;
        if (_settingsWebView != null)
        {
            _settingsWebView.Visibility = keep == SettingsKey ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 10秒ごとにメモリ使用量を集計し、60秒ごとに自動スリープ判定を行う。
    /// Tauri版のspawn_background相当。
    /// </summary>
    public void StartBackgroundTimer()
    {
        _backgroundTimer = new System.Threading.Timer(_ => OnBackgroundTick(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void OnBackgroundTick()
    {
        Dictionary<string, uint>? browserProcessIds = null;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            browserProcessIds = GetBrowserProcessIds());
        Memory?.Refresh(browserProcessIds ?? new());

        if (++_backgroundTick % 6 != 0) return;

        var settings = _configStore.Get().Settings;
        if (settings.SleepAfterMinutes == 0) return;

        var keepAwakeIds = _configStore.Get().Instances
            .Where(i => i.KeepAwake)
            .Select(i => i.Id)
            .ToHashSet();

        var limit = TimeSpan.FromMinutes(settings.SleepAfterMinutes);
        var now = DateTime.UtcNow;
        var expired = _hiddenSince
            .Where(kv => !keepAwakeIds.Contains(kv.Key) && !_mediaPlaying.GetValueOrDefault(kv.Key)
                && now - kv.Value >= limit)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in expired)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[auto-sleep] {id}");
                _ = SleepAsync(id);
                _hiddenSince.Remove(id);
            });
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    /// <summary>
    /// Ctrl+1〜9でサービスを切り替えるショートカット。サービスページ内にフォーカスが
    /// あっても効くようにするため、各サービスのページにも注入する(無効時の判定は
    /// SelectByIndexAsync側で行うため、ここでは常に注入してpostMessageするだけ)。
    /// </summary>
    private const string ShortcutScript = @"(function(){
  if (window.__ymbShortcutScanStarted) return;
  window.__ymbShortcutScanStarted = true;
  document.addEventListener('keydown', function(e){
    if (!e.ctrlKey || e.altKey || e.metaKey) return;
    var n = parseInt(e.key, 10);
    if (n >= 1 && n <= 9) {
      try { window.chrome.webview.postMessage({ type: 'shortcut', digit: n }); } catch (_) {}
    }
  }, true);
})();";

    private readonly Grid _contentHost;
    private readonly WebView2 _welcomeWebView;
    private readonly WebView2 _sidebarWebView;
    private readonly CoreWebView2Environment _environment;
    private readonly ConfigStore _configStore;
    private readonly string _wwwrootDir;
    private readonly string _virtualHost;
    private readonly Dictionary<string, WebView2> _webviews = new();
    private readonly Dictionary<string, Grid> _containers = new();
    private readonly Dictionary<string, System.Windows.Controls.TextBox> _urlBars = new();
    private readonly Dictionary<string, RowDefinition> _urlBarRows = new();
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

            var container = new Grid();
            var urlBarRow = new RowDefinition
            {
                Height = config.Settings.UrlBarEnabled ? GridLength.Auto : new GridLength(0),
            };
            container.RowDefinitions.Add(urlBarRow);
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var homeUrl = Recipes.ResolveUrl(inst);

            webview = new WebView2();
            Grid.SetRow(webview, 1);
            var urlBarUi = BuildUrlBar(id, homeUrl, out var urlBar, out var reloadBtn);
            container.Children.Add(urlBarUi);
            container.Children.Add(webview);
            _contentHost.Children.Add(container);
            _containers[id] = container;
            _urlBars[id] = urlBar;
            _urlBarRows[id] = urlBarRow;

            try
            {
                var options = _environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = id;
                await webview.EnsureCoreWebView2Async(_environment, options);
                webview.CoreWebView2.Profile.DefaultDownloadFolderPath = PathUtil.GetDownloadsFolder();

                var useChromeUa = inst.ChromeUa ?? Recipes.DefaultChromeUa(inst.Recipe);
                if (useChromeUa)
                {
                    webview.CoreWebView2.Settings.UserAgent = Recipes.ChromeUserAgent;
                }

                await webview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(UnreadParser.InjectedScript);
                webview.CoreWebView2.DocumentTitleChanged += (_, _) =>
                    OnTitleChanged(id, webview.CoreWebView2.DocumentTitle);

                await webview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MediaPlaybackScript);
                await webview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
                webview.CoreWebView2.WebMessageReceived += (_, e) => OnWebMessageReceived(id, e);

                webview.CoreWebView2.ContextMenuRequested += (_, e) => OnContextMenuRequested(e);
                webview.CoreWebView2.ProcessFailed += (_, e) => OnProcessFailed(id, e);
                webview.CoreWebView2.NewWindowRequested += (_, e) => OnNewWindowRequested(e);
                webview.CoreWebView2.PermissionRequested += (_, e) => OnPermissionRequested(e);
                webview.CoreWebView2.DownloadStarting += (_, e) => OnDownloadStarting(e);
                webview.CoreWebView2.SourceChanged += (_, _) => urlBar.Text = webview.Source.ToString();
                webview.CoreWebView2.NavigationStarting += (_, _) =>
                {
                    reloadBtn.Content = "✕";
                    if (reloadBtn.Tag is string[] labels) reloadBtn.ToolTip = labels[1];
                };
                webview.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    reloadBtn.Content = "⟳";
                    if (reloadBtn.Tag is string[] labels) reloadBtn.ToolTip = labels[0];
                };
            }
            catch
            {
                // WebView2生成の途中(EnsureCoreWebView2Async等)で失敗した場合、
                // _webviews[id]は未登録のままだが_containers[id]等は登録済みのため、
                // 呼び出し元の再試行時に古いcontainer/webviewがvisual treeとメモリに
                // 残留してしまう(orphan leak)。ここで確実に後始末してから再スローする。
                _containers.Remove(id);
                _urlBars.Remove(id);
                _urlBarRows.Remove(id);
                _contentHost.Children.Remove(container);
                webview.Dispose();
                throw;
            }

            await LoadExtensionsIntoAsync(webview);

            webview.Source = new Uri(homeUrl);
            urlBar.Text = webview.Source.ToString();
            _webviews[id] = webview;

            if (inst.ForceRenavigate)
            {
                ScheduleForceRenavigate(id, webview, homeUrl);
            }
        }

        HideOthers(id);
        ActiveId = id;
        _containers[id].Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 初回ナビゲーションがabout:blankのまま固まって画面が真っ白になることがある
    /// (YouTube Music等)サービス向けの再ナビゲートキック。設定で有効化されている
    /// 場合のみ、2秒待って明示的に再度Navigateし直す(Tauri版のforce_renavigate相当)。
    /// このサービスが既にスリープ/削除/別サービスに切り替わっていた場合は何もしない。
    /// </summary>
    private void ScheduleForceRenavigate(string id, WebView2 webview, string homeUrl)
    {
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!_webviews.TryGetValue(id, out var current) || current != webview) return;
                try
                {
                    webview.CoreWebView2.Navigate(homeUrl);
                }
                catch
                {
                    // ブラウザプロセスが既に無効化されている等は無視(他の復旧経路に任せる)。
                }
            });
        });
    }

    /// <summary>
    /// URLバー(戻る/進む/ホーム/再読込(読込中は中止)/外部ブラウザ+アドレス欄)のUIを構築する。
    /// 設定でOFFの場合は行の高さを0にして非表示にする(ActivateAsync/ApplyUrlBarVisibility参照)。
    /// ボタンの配色は現在のテーマ(ThemePalette)に合わせる。
    /// </summary>
    private Grid BuildUrlBar(string id, string homeUrl, out System.Windows.Controls.TextBox urlBar, out System.Windows.Controls.Button reloadBtn)
    {
        var config = _configStore.Get();
        var theme = ThemePalette.Get(config.Settings.Theme);
        var t = Translations.Load(_wwwrootDir, config.Settings.Language);
        var barBg = ToBrush(theme.Bar);
        var btnBg = ToBrush(theme.Button);
        var btnHoverBg = ToBrush(theme.ButtonHover);
        var fg = ToBrush(theme.Text);
        var borderBrush = ToBrush(theme.Border);

        var bar = new Grid { Background = barBg, Height = 30 };
        for (var i = 0; i < 5; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var backBtn = new System.Windows.Controls.Button { Content = "◀", ToolTip = t.GetValueOrDefault("urlbar.back", "戻る") };
        var fwdBtn = new System.Windows.Controls.Button { Content = "▶", ToolTip = t.GetValueOrDefault("urlbar.forward", "進む") };
        var homeBtn = new System.Windows.Controls.Button { Content = "⌂", ToolTip = t.GetValueOrDefault("urlbar.home", "ホーム") };
        var reloadButton = new System.Windows.Controls.Button { Content = "⟳" };
        var externalBtn = new System.Windows.Controls.Button { Content = "↗", ToolTip = t.GetValueOrDefault("urlbar.open_external", "外部ブラウザで開く") };

        var reloadLabel = t.GetValueOrDefault("urlbar.reload", "再読み込み");
        var stopLabel = t.GetValueOrDefault("urlbar.stop", "読み込みを中止");
        reloadButton.ToolTip = reloadLabel;
        reloadButton.Tag = new[] { reloadLabel, stopLabel };
        var textBox = new System.Windows.Controls.TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(4, 4, 4, 4),
            Background = btnBg,
            Foreground = fg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = fg,
        };

        var col = 0;
        foreach (var btn in new[] { backBtn, fwdBtn, homeBtn, reloadButton, externalBtn })
        {
            StyleFlatButton(btn, btnBg, btnHoverBg, fg);
            Grid.SetColumn(btn, col++);
            bar.Children.Add(btn);
        }
        Grid.SetColumn(textBox, col);
        bar.Children.Add(textBox);

        backBtn.Click += (_, _) =>
        {
            if (_webviews.TryGetValue(id, out var wv) && wv.CoreWebView2.CanGoBack) wv.CoreWebView2.GoBack();
        };
        fwdBtn.Click += (_, _) =>
        {
            if (_webviews.TryGetValue(id, out var wv) && wv.CoreWebView2.CanGoForward) wv.CoreWebView2.GoForward();
        };
        homeBtn.Click += (_, _) =>
        {
            if (_webviews.TryGetValue(id, out var wv)) wv.CoreWebView2.Navigate(homeUrl);
        };
        reloadButton.Click += (_, _) =>
        {
            if (!_webviews.TryGetValue(id, out var wv)) return;
            if ((string)reloadButton.Content == "✕") wv.CoreWebView2.Stop();
            else wv.CoreWebView2.Reload();
        };
        externalBtn.Click += (_, _) =>
        {
            if (_webviews.TryGetValue(id, out var wv)) OpenInExternalBrowser(wv.Source?.ToString());
        };
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (!_webviews.TryGetValue(id, out var wv)) return;
            var text = textBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (!text.Contains("://")) text = "https://" + text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                wv.CoreWebView2.Navigate(uri.ToString());
            }
        };

        urlBar = textBox;
        reloadBtn = reloadButton;
        return bar;
    }

    /// <summary>Buttonの既定の立体的なWin32風枠を、テーマ配色のフラットな角丸ボタンに置き換える。</summary>
    private static void StyleFlatButton(System.Windows.Controls.Button btn, System.Windows.Media.Brush bg, System.Windows.Media.Brush hoverBg, System.Windows.Media.Brush fg)
    {
        btn.Background = bg;
        btn.Foreground = fg;
        btn.BorderThickness = new Thickness(0);
        btn.Cursor = System.Windows.Input.Cursors.Hand;
        btn.FontSize = 13;
        btn.Width = 32;
        btn.Template = FlatButtonTemplate;
        btn.MouseEnter += (_, _) => btn.Background = hoverBg;
        btn.MouseLeave += (_, _) => btn.Background = bg;
    }

    private static ControlTemplate? _flatButtonTemplate;

    private static ControlTemplate FlatButtonTemplate => _flatButtonTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        @"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""Button"">
            <Border Background=""{TemplateBinding Background}"" CornerRadius=""4"" Margin=""2"">
              <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/>
            </Border>
          </ControlTemplate>");

    private static System.Windows.Media.Brush ToBrush(string hex) =>
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>設定変更時、生存中の各サービスのURLバー表示/非表示を即時反映する。</summary>
    public void ApplyUrlBarVisibility()
    {
        var enabled = _configStore.Get().Settings.UrlBarEnabled;
        foreach (var row in _urlBarRows.Values)
        {
            row.Height = enabled ? GridLength.Auto : new GridLength(0);
        }
    }

    /// <summary>
    /// アプリ終了時に呼ぶ。生存中の全WebView2(サイドバー/待機画面/設定画面/各サービス)を
    /// 明示的に破棄する。WPFのWindowが閉じてもWebView2のCoreWebView2Controllerは
    /// 自動Disposeされず、msedgewebview2.exeの孤児プロセスが残留することがあるため。
    /// </summary>
    public void DisposeAll()
    {
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;

        foreach (var webview in _webviews.Values)
        {
            webview.Dispose();
        }
        _webviews.Clear();
        _containers.Clear();
        _urlBars.Clear();
        _urlBarRows.Clear();

        _settingsWebView?.Dispose();
        _settingsWebView = null;

        _welcomeWebView.Dispose();
        _sidebarWebView.Dispose();
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
            if (_containers.Remove(id, out var container))
            {
                _contentHost.Children.Remove(container);
            }
            _urlBars.Remove(id);
            _urlBarRows.Remove(id);
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
            if (_containers.Remove(id, out var container))
            {
                _contentHost.Children.Remove(container);
            }
            _urlBars.Remove(id);
            _urlBarRows.Remove(id);
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
            if (!json.TryGetProperty("type", out var typeEl)) return;
            switch (typeEl.GetString())
            {
                case "media":
                    if (json.TryGetProperty("playing", out var playingEl))
                    {
                        _mediaPlaying[id] = playingEl.GetBoolean();
                    }
                    break;
                case "shortcut":
                    if (json.TryGetProperty("digit", out var digitEl))
                    {
                        _ = SelectByIndexAsync(digitEl.GetInt32());
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // 不正な形式のメッセージは無視。
        }
    }

    /// <summary>
    /// Ctrl+1〜9ショートカット用。設定で無効化されていれば何もしない。
    /// 1始まりのインデックスでconfig.Instancesの該当サービスを選択/起動する。
    /// </summary>
    public Task SelectByIndexAsync(int oneBasedIndex)
    {
        var config = _configStore.Get();
        if (!config.Settings.KeyboardShortcutsEnabled) return Task.CompletedTask;

        var inst = config.Instances.ElementAtOrDefault(oneBasedIndex - 1);
        return inst == null ? Task.CompletedTask : SelectInstanceAsync(inst.Id);
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
            if (_containers.Remove(id, out var container))
            {
                _contentHost.Children.Remove(container);
            }
            _urlBars.Remove(id);
            _urlBarRows.Remove(id);
            webview.Dispose();
        }
        if (ActiveId == id)
        {
            ActiveId = null;
            _welcomeWebView.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// target="_blank"等の新規ウインドウ要求への対応。
    /// SlackハドルのようにJSがwindow.open()の戻り値(ウインドウ参照)を同期的に使う
    /// フロー(about:blankの空ポップアップを開いて後からJSで操作するパターン)では、
    /// 外部ブラウザに逃がすとwindow.open()がnullを返してJS側が例外停止してしまう上、
    /// e.NewWindowへの割り当て自体もこのアプリの環境ではハングすることを確認済み。
    /// そのため、about:blank(URI無し)の場合はイベントを一切ハンドルせず、WebView2の
    /// 既定ポップアップ作成に任せる(鍵マーク操作によるクラッシュはGetBrowserProcessIds
    /// のtry-catchとOnProcessFailedのBrowserProcessExited処理で被害を抑えている)。
    /// 実URLへの遷移(OAuth等)は引き続き外部ブラウザで開く。
    /// </summary>
    private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri) || e.Uri == "about:blank")
        {
            return;
        }

        e.Handled = true;
        OpenInExternalBrowser(e.Uri);
    }

    /// <summary>
    /// 既定のWebView2はダウンロード中/完了のUIを一切出さないため、画面右下に
    /// 進捗表示ウインドウを出す(クリックでフォルダを開く、完了後しばらくして自動で消える)。
    /// </summary>
    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        var op = e.DownloadOperation;
        var fileName = System.IO.Path.GetFileName(op.ResultFilePath);
        var win = new DownloadNotificationWindow(fileName, op.ResultFilePath);
        win.SetCancelAction(() => op.Cancel());
        win.Show();

        op.BytesReceivedChanged += (_, _) =>
            win.Dispatcher.Invoke(() => win.UpdateProgress(op.BytesReceived, op.TotalBytesToReceive));

        op.StateChanged += (_, _) =>
            win.Dispatcher.Invoke(() =>
            {
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        win.SetCompleted();
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        win.SetInterrupted();
                        break;
                }
            });
    }

    /// <summary>
    /// カメラ/マイクは既定では権限バーが出るだけで、枠なしウインドウだと見落とされ
    /// Slackハドル等の通話機能が無反応に見える原因になるため自動許可する。
    /// Web標準通知は独自の未読監視/トレイ通知と重複するため拒否する。
    /// </summary>
    private void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        switch (e.PermissionKind)
        {
            case CoreWebView2PermissionKind.Camera:
            case CoreWebView2PermissionKind.Microphone:
                e.State = CoreWebView2PermissionState.Allow;
                break;
            case CoreWebView2PermissionKind.Notifications:
                e.State = CoreWebView2PermissionState.Deny;
                break;
            default:
                break;
        }
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
        foreach (var (otherId, container) in _containers)
        {
            if (otherId == keep)
            {
                container.Visibility = Visibility.Visible;
                _hiddenSince.Remove(otherId);
            }
            else
            {
                container.Visibility = Visibility.Collapsed;
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

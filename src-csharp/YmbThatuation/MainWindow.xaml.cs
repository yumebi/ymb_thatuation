using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YmbThatuation.Ipc;
using YmbThatuation.Services;

namespace YmbThatuation;

public partial class MainWindow : Window
{
    private const string VirtualHost = "app.ymb-thatuation.local";

    private ConfigStore? _configStore;
    private InstanceManager? _instanceManager;
    private TrayService? _tray;
    private WindowStateService? _windowState;
    private uint _browserProcessId;
    private System.Windows.Threading.DispatcherTimer? _welcomeTimer;
    private string? _welcomePendingId;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _configStore = new ConfigStore();

        // 再インストール/バージョン更新でexeパスが変わった場合、レジストリRunキーが
        // 古いパスのまま残って自動起動が効かなくなることがあるため、有効時は毎回
        // 現在のexeパスで再登録して自己修復する。
        if (_configStore.Get().Settings.Autostart)
        {
            AutostartService.SetEnabled(true);
        }

        _windowState = new WindowStateService(_configStore);
        var lastActiveId = _windowState.Restore(this);

        var webview2DataDir = Path.Combine(_configStore.AppDataDir, "webview2");
        var environmentOptions = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            // 常駐メモリ削減のため、レンダラープロセス数の上限を絞り、クロスオリジンiframe
            // ごとのプロセス分割(サイトアイソレーション)を無効化する。対象は自分で追加した
            // 信頼済みサービスのみのため、セキュリティ分離が緩むリスクは許容範囲と判断。
            AdditionalBrowserArguments = "--renderer-process-limit=4 --disable-features=SitePerProcess",
        };
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: webview2DataDir, options: environmentOptions);

        // サイドバー
        var sidebarOptions = environment.CreateCoreWebView2ControllerOptions();
        sidebarOptions.ProfileName = "sidebar";
        await SidebarWebView.EnsureCoreWebView2Async(environment, sidebarOptions);
        SidebarWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        _browserProcessId = (uint)SidebarWebView.CoreWebView2.BrowserProcessId;

        // 待機画面。常駐WebView2を1つ減らすため、WebView2ではなくネイティブWPFで描画する。
        var welcomePanel = BuildWelcomePanel(_configStore, wwwroot, out var welcomeTitle, out var welcomeBody, out var welcomeWakeBtn);
        ContentHost.Children.Add(welcomePanel);

        _instanceManager = new InstanceManager(ContentHost, welcomePanel, SidebarWebView, environment, _configStore, wwwroot, VirtualHost);
        var bridge = new IpcBridge(_configStore, _instanceManager, wwwroot);
        _instanceManager.Bridge = bridge;

        _tray = new TrayService(this, _configStore, wwwroot);
        _instanceManager.Tray = _tray;
        _instanceManager.Memory = new MemoryService();
        _instanceManager.Extensions = new ExtensionsService(_configStore);
        _instanceManager.UpdateCheck = new UpdateCheckService();

        SidebarWebView.CoreWebView2.AddHostObjectToScript("ymb", bridge);

        SidebarWebView.Source = new Uri($"https://{VirtualHost}/index.html");

        StartWelcomeTimer(welcomeTitle, welcomeBody, welcomeWakeBtn);
        _instanceManager.StartBackgroundTimer();
        _ = RestoreLastActiveAsync(_instanceManager, _configStore, lastActiveId);
        _ = CheckExtensionUpdatesAsync(_instanceManager, _configStore, _tray);
        _ = CheckAppUpdateAsync(_instanceManager, _configStore, _tray);

        if (_configStore.Get().Settings.StartMinimized)
        {
            Hide();
        }
    }

    /// <summary>
    /// 待機画面(旧welcome.html相当)をネイティブWPFで構築する。常駐WebView2を1つ減らし、
    /// メモリ使用量を抑えるための実装(ロゴ/タイトル/説明文/起床ボタンのみの簡素な画面のため
    /// WebView2化する必要が無い)。テーマ配色はThemePalette(URLバーと同じ表)から取得する。
    /// </summary>
    private static Grid BuildWelcomePanel(ConfigStore configStore, string wwwrootDir,
        out TextBlock titleText, out TextBlock bodyText, out System.Windows.Controls.Button wakeButton)
    {
        var theme = ThemePalette.Get(configStore.Get().Settings.Theme);
        var bg = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(theme.Bar));
        var fg = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(theme.Text));
        var muted = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(theme.ButtonHover));

        var root = new Grid { Background = bg };
        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(stack);

        var logo = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Background = new LinearGradientBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5b8def"),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#41b883"),
                new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)),
        };
        var dots = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var hex in new[] { "#5b8def", "#e8a33d", "#41b883" })
        {
            dots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(3, 0, 3, 0),
                Fill = System.Windows.Media.Brushes.White,
            });
        }
        logo.Child = dots;
        stack.Children.Add(logo);

        titleText = new TextBlock
        {
            Text = "YMB Thatuation",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        stack.Children.Add(titleText);

        var t = Translations.Load(wwwrootDir, configStore.Get().Settings.Language);
        bodyText = new TextBlock
        {
            Foreground = muted,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        SetWelcomeBody(bodyText, t.GetValueOrDefault("welcome.body", ""));
        stack.Children.Add(bodyText);

        wakeButton = new System.Windows.Controls.Button
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(22, 10, 22, 10),
            Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3fae5c")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        stack.Children.Add(wakeButton);

        return root;
    }

    /// <summary>"welcome.body"の"&lt;br&gt;"を改行として反映する(元のHTML版と同じ表示)。</summary>
    private static void SetWelcomeBody(TextBlock target, string html)
    {
        target.Inlines.Clear();
        var lines = html.Split("<br>");
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) target.Inlines.Add(new LineBreak());
            target.Inlines.Add(new Run(lines[i]));
        }
    }

    /// <summary>
    /// 300ms間隔でPendingWakeIdを監視し、待機画面のタイトル/本文と起床ボタンの表示を切り替える。
    /// 旧welcome.htmlのJS(invoke("ui_state")を300msごとにポーリング)と同じ挙動をネイティブに移植。
    /// </summary>
    private void StartWelcomeTimer(TextBlock titleText, TextBlock bodyText, System.Windows.Controls.Button wakeButton)
    {
        var t = Translations.Load(Path.Combine(AppContext.BaseDirectory, "wwwroot"), _configStore!.Get().Settings.Language);

        wakeButton.Click += (_, _) =>
        {
            if (_welcomePendingId != null)
            {
                _ = _instanceManager?.ActivateAsync(_welcomePendingId);
            }
        };

        _welcomeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _welcomeTimer.Tick += (_, _) =>
        {
            var pendingId = _instanceManager?.PendingWakeId;
            if (pendingId == _welcomePendingId) return;
            _welcomePendingId = pendingId;

            var pendingInst = pendingId != null
                ? _configStore.Get().Instances.FirstOrDefault(i => i.Id == pendingId)
                : null;

            if (pendingInst != null)
            {
                titleText.Visibility = Visibility.Collapsed;
                bodyText.Visibility = Visibility.Collapsed;
                wakeButton.Content = t.GetValueOrDefault("welcome.wake_up", "{name} のスリープを解除する")
                    .Replace("{name}", pendingInst.Name);
                wakeButton.Visibility = Visibility.Visible;
            }
            else
            {
                titleText.Visibility = Visibility.Visible;
                bodyText.Visibility = Visibility.Visible;
                wakeButton.Visibility = Visibility.Collapsed;
            }
        };
        _welcomeTimer.Start();
    }

    /// <summary>
    /// 起動5秒後に拡張機能の更新を確認し、更新があれば通知する。
    /// Tauri版のspawn_extension_update_check相当。
    /// </summary>
    private static async Task CheckExtensionUpdatesAsync(InstanceManager instanceManager, ConfigStore configStore, TrayService? tray)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (instanceManager.Extensions == null) return;

        List<string> updated;
        try
        {
            updated = await instanceManager.Extensions.UpdateExtensionsAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"[ext-update] failed: {e.Message}");
            return;
        }
        if (updated.Count == 0) return;

        if (configStore.Get().Settings.Notifications)
        {
            tray?.ShowNotification("拡張機能を更新しました", string.Join("\n", updated));
            tray?.PlayNotificationSound();
        }
    }

    /// <summary>
    /// 起動8秒後に本体の新バージョンを確認し、あれば通知する。
    /// </summary>
    private static async Task CheckAppUpdateAsync(InstanceManager instanceManager, ConfigStore configStore, TrayService? tray)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (instanceManager.UpdateCheck == null) return;

        UpdateCheckResult result;
        try
        {
            result = await instanceManager.UpdateCheck.CheckAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"[app-update] failed: {e.Message}");
            return;
        }
        if (!result.UpdateAvailable) return;

        if (configStore.Get().Settings.Notifications)
        {
            tray?.ShowNotification(
                "新しいバージョンがあります",
                $"v{result.LatestVersion} (現在 v{result.CurrentVersion})。クリックでダウンロードページを開きます",
                () => InstanceManager.OpenInExternalBrowser(result.ReleaseUrl));
            tray?.PlayNotificationSound();
        }
    }

    /// <summary>
    /// keep_awakeサービスの起動完了後、前回終了時にアクティブだったサービスを表示する。
    /// keep_awake起動シーケンスの中で他のサービスが表示状態を奪ってしまうため、
    /// 最後にもう一度アクティブ化して画面に出す。
    /// </summary>
    private static async Task RestoreLastActiveAsync(InstanceManager instanceManager, ConfigStore configStore, string? lastActiveId)
    {
        await StartKeepAwakeSequenceAsync(instanceManager, configStore);
        if (lastActiveId != null && configStore.Get().Instances.Any(i => i.Id == lastActiveId))
        {
            await ActivateWithRetryAsync(instanceManager, lastActiveId);
        }
    }

    /// <summary>
    /// 起動時、「スリープさせない」設定のサービスを起動する。
    /// 設定の「順次起動」が有効な場合は指定秒数間隔で順次起動し
    /// (一斉起動によるWebView2プロセス同時生成の負荷を避ける)、
    /// 無効な場合は全インスタンスを同時に起動する。
    /// Tauri版のspawn_keep_awake_startup相当。
    /// </summary>
    private static async Task StartKeepAwakeSequenceAsync(InstanceManager instanceManager, ConfigStore configStore)
    {
        var ids = configStore.Get().Instances.Where(i => i.KeepAwake).Select(i => i.Id).ToList();
        var settings = configStore.Get().Settings;

        if (!settings.StaggeredStartup)
        {
            await Task.WhenAll(ids.Select(id => ActivateWithRetryAsync(instanceManager, id)));
            return;
        }

        var delay = TimeSpan.FromSeconds(settings.StartupDelaySeconds);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            await ActivateWithRetryAsync(instanceManager, ids[i]);
        }
    }

    /// <summary>
    /// 再起動直後等、他のサービスのWebView2プロセス生成と競合して一時的に失敗することがあるため、
    /// 1回だけ間を置いて再試行する。ここで例外を握りつぶさないと、逐次起動ループでは1つの
    /// 失敗が後続の全サービスの起動を止めてしまう(_ = RestoreLastActiveAsync(...)は
    /// fire-and-forgetのため、例外はどこにも表示されず原因が分からなくなる)。
    /// </summary>
    private static async Task ActivateWithRetryAsync(InstanceManager instanceManager, string id)
    {
        try
        {
            await instanceManager.ActivateAsync(id);
            return;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"[startup-activate] {id} failed, retrying: {e.Message}");
        }

        await Task.Delay(1000);
        try
        {
            await instanceManager.ActivateAsync(id);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"[startup-activate] {id} failed again, giving up: {e.Message}");
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_configStore == null || !_configStore.Get().Settings.KeyboardShortcutsEnabled) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var index = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => (int)e.Key - (int)Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => (int)e.Key - (int)Key.NumPad1 + 1,
            _ => 0,
        };
        if (index == 0 || _instanceManager == null) return;
        _ = _instanceManager.SelectByIndexAsync(index);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _windowState?.Save(this, _instanceManager?.ActiveId);

        if (_tray == null || _tray.IsExiting)
        {
            _tray?.Dispose();
            _instanceManager?.DisposeAll();
            WaitForBrowserProcessExit();
            return;
        }

        if (_configStore != null && _configStore.Get().Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _tray.Dispose();
            _instanceManager?.DisposeAll();
            WaitForBrowserProcessExit();
        }
    }

    /// <summary>
    /// WebView2のDispose()はブラウザプロセスへ終了要求を送るだけで、実際の終了は非同期。
    /// アプリ本体が先に終了するとmsedgewebview2.exeが孤児プロセスとして残るため、
    /// 共有環境のブラウザプロセス本体が終了するまで(タイムアウト付きで)待つ。
    /// </summary>
    private void WaitForBrowserProcessExit()
    {
        if (_browserProcessId == 0) return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)_browserProcessId);
            process.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            // 既に終了している。
        }
    }
}

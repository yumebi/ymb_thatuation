using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
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
    private System.Windows.Threading.DispatcherTimer? _saveTimer;
    private string? _lastActiveId;
    private bool _stateRestored;
    private bool _startHidden;
    private WindowState _lastNonMinimizedState = WindowState.Normal;

    public MainWindow()
    {
        // 位置の復元と最小化起動の判断を表示の前に行うため、設定の読み込みはここで行う。
        _configStore = new ConfigStore();
        _windowState = new WindowStateService(_configStore);

        InitializeComponent();
        ApplyTheme();

        // 「最小化で起動」: 表示してからHide()すると一瞬ウインドウが見えてしまう。
        // タスクバーにも出さず・アクティブにもせず最小化の状態で作り、WebView2の初期化
        // (Loadedで走る)が済んでから隠す。
        _startHidden = _configStore.Get().Settings.StartMinimized;
        if (_startHidden)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
        }

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastNonMinimizedState = WindowState;
            ScheduleSave();
        };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        SidebarWebView.SizeChanged += (_, _) => ScheduleSave();

        // ログオフ・シャットダウン時はClosingが呼ばれないことがあるため、ここでも保存する。
        if (System.Windows.Application.Current is { } app)
        {
            app.SessionEnding += (_, _) => SaveWindowState();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // 再インストール/バージョン更新でexeパスが変わった場合、レジストリRunキーが
        // 古いパスのまま残って自動起動が効かなくなることがあるため、有効時は毎回
        // 現在のexeパスで再登録して自己修復する。
        if (_configStore.Get().Settings.Autostart)
        {
            AutostartService.SetEnabled(true);
        }

        var lastActiveId = _lastActiveId;

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
        _instanceManager.Popup = new PopupManager();
        var bridge = new IpcBridge(_configStore, _instanceManager, wwwroot);
        _instanceManager.Bridge = bridge;
        bridge.ThemeChanged += () => Dispatcher.BeginInvoke(ApplyTheme);

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

        FinishHiddenStartup();
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
            var t = Translations.LoadOrEmpty(Path.Combine(AppContext.BaseDirectory, "wwwroot"), configStore.Get().Settings.Language);
            tray?.ShowNotification(
                NotificationText.Format(t, "notify.ext_updated", "拡張機能を更新しました"),
                string.Join("\n", updated));
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
            var t = Translations.LoadOrEmpty(Path.Combine(AppContext.BaseDirectory, "wwwroot"), configStore.Get().Settings.Language);
            tray?.ShowNotification(
                NotificationText.Format(t, "notify.update.title", "新しいバージョンがあります"),
                NotificationText.Format(
                    t, "notify.update.body", "v{latest} (現在 v{current})。クリックでダウンロードページを開きます",
                    ("latest", result.LatestVersion?.ToString() ?? ""), ("current", result.CurrentVersion?.ToString() ?? "")),
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
        SaveWindowState();
        _saveTimer?.Stop();

        if (_tray == null || _tray.IsExiting)
        {
            // 終了処理(WebView2のプロセス終了待ち)は最大数秒かかる。先に隠して、固まって見えないようにする。
            HideNow();
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
            HideNow();
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

    /// <summary>
    /// HWNDの作成後・初回表示の前に呼ばれる。位置の復元とタイトルバーの配色をここで済ませるので、
    /// 既定の位置に一瞬出てから動く、ということが起きない。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var restored = _windowState!.Apply(this);
        _lastActiveId = restored.LastActiveId;
        if (restored.Maximized) _lastNonMinimizedState = WindowState.Maximized;

        ApplyTheme();

        if (_startHidden)
        {
            WindowState = WindowState.Minimized;
        }
        else if (restored.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        _stateRestored = true;
    }

    /// <summary>ウインドウの背景とタイトルバーを、現在のテーマの配色にする。</summary>
    public void ApplyTheme()
    {
        if (_configStore == null) return;
        var theme = ThemePalette.Get(_configStore.Get().Settings.Theme);
        Background = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Bar));
        TitleBarTheme.Apply(this, theme);
    }

    /// <summary>
    /// 移動・サイズ変更の後、少し待ってから位置を保存する。終了時だけの保存だと、
    /// 強制終了や再起動で直前の位置が失われる。
    /// </summary>
    private void ScheduleSave()
    {
        if (!_stateRestored) return;
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveWindowState();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveWindowState()
    {
        if (!_stateRestored) return;
        _windowState?.Save(this, _instanceManager?.ActiveId);
    }

    /// <summary>
    /// トレイや通知、2つ目の起動から呼ばれて、ウインドウを前面に出す。
    /// 最小化から戻すときは、最小化する前の状態(最大化など)に戻す。
    /// </summary>
    public void RestoreFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastNonMinimizedState;
        Activate();
        // 他のアプリが前面にあるとActivateだけでは前に出ないことがある。Topmostを一瞬切り替えて確実に出す。
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>WPFの状態を変えずに、ウインドウだけを画面から消す(Closing中でも呼べる)。</summary>
    private void HideNow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Win32.ShowWindow(hwnd, Win32.SW_HIDE);
    }

    /// <summary>「最小化で起動」の後始末。WebView2の初期化が済んだので、ウインドウを隠して通常の状態に戻す。</summary>
    private void FinishHiddenStartup()
    {
        if (!_startHidden) return;
        _startHidden = false;
        Hide();
        ShowInTaskbar = true;
        ShowActivated = true;
        WindowState = _lastNonMinimizedState;
    }
}

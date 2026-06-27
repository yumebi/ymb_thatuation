using System.ComponentModel;
using System.IO;
using System.Windows;
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
        };
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: webview2DataDir, options: environmentOptions);

        // サイドバー
        var sidebarOptions = environment.CreateCoreWebView2ControllerOptions();
        sidebarOptions.ProfileName = "sidebar";
        await SidebarWebView.EnsureCoreWebView2Async(environment, sidebarOptions);
        SidebarWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        // 待機画面
        var welcomeWebView = new WebView2();
        ContentHost.Children.Add(welcomeWebView);
        var welcomeOptions = environment.CreateCoreWebView2ControllerOptions();
        welcomeOptions.ProfileName = "welcome";
        await welcomeWebView.EnsureCoreWebView2Async(environment, welcomeOptions);
        welcomeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        _instanceManager = new InstanceManager(ContentHost, welcomeWebView, SidebarWebView, environment, _configStore, wwwroot, VirtualHost);
        var bridge = new IpcBridge(_configStore, _instanceManager, wwwroot);
        _instanceManager.Bridge = bridge;

        _tray = new TrayService(this, _configStore, wwwroot);
        _instanceManager.Tray = _tray;
        _instanceManager.Memory = new MemoryService();
        _instanceManager.Extensions = new ExtensionsService(_configStore);
        _instanceManager.UpdateCheck = new UpdateCheckService();

        SidebarWebView.CoreWebView2.AddHostObjectToScript("ymb", bridge);
        welcomeWebView.CoreWebView2.AddHostObjectToScript("ymb", bridge);

        SidebarWebView.Source = new Uri($"https://{VirtualHost}/index.html");
        welcomeWebView.Source = new Uri($"https://{VirtualHost}/welcome.html");

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
            await instanceManager.ActivateAsync(lastActiveId);
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
            await Task.WhenAll(ids.Select(instanceManager.ActivateAsync));
            return;
        }

        var delay = TimeSpan.FromSeconds(settings.StartupDelaySeconds);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            await instanceManager.ActivateAsync(ids[i]);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _windowState?.Save(this, _instanceManager?.ActiveId);

        if (_tray == null || _tray.IsExiting)
        {
            _tray?.Dispose();
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
        }
    }
}

using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace YmbThatuation.Services;

/// <summary>
/// サービス内の window.open() / target="_blank" で開かれる新規ウインドウを、アプリ内の別ウインドウで表示する。
///
/// 親のサービスと「同じプロファイル」で WebView2 を作り、NewWindow に割り当てる。
/// これにより、親のログイン状態(Cookie)が引き継がれ、window.opener・postMessage・window.close() が
/// 通常のブラウザと同じように動く(「Googleでログイン」などの弾窓がそのまま完了する)。
///
/// 以前は、親とは別のプロファイルで作った WebView2 を、親と同じウインドウの上に重ねていた。
/// - 別プロファイルは NewWindow に割り当てられない(ArgumentException)ため、window.opener が失われた。
/// - WebView2(HWND)の上に重ねた WPF の要素は描画されないため、暗幕と閉じるボタンが見えなかった。
/// - さらに、重ねる処理自体がキャストの誤りで必ず例外になり、実際には動いていなかった。
/// </summary>
public sealed class PopupManager
{
    private readonly Action<string?> _openExternal;
    private readonly List<PopupWindow> _windows = new();

    /// <param name="openExternal">ポップアップを作れなかったときに URL を開く処理(既定は既定ブラウザ)。</param>
    public PopupManager(Action<string?>? openExternal = null)
    {
        _openExternal = openExternal ?? InstanceManager.OpenInExternalBrowser;
    }

    /// <summary>表示中のポップアップの数(テスト用)。</summary>
    public int OpenCount => _windows.Count;

    /// <summary>
    /// NewWindowRequested の中から呼ぶ。deferral はイベントハンドラ内で取得して渡す
    /// (取得しないと、非同期で WebView2 を作る間にイベントが完了してしまう)。
    /// 失敗した場合は、URL があれば外部ブラウザで開く。
    /// </summary>
    /// <param name="profileName">親の WebView2 と同じ ProfileName。</param>
    public async Task HandleAsync(
        CoreWebView2NewWindowRequestedEventArgs e,
        CoreWebView2Deferral deferral,
        CoreWebView2Environment environment,
        string profileName,
        ThemeColors theme,
        Window? owner)
    {
        PopupWindow? popup = null;
        try
        {
            var size = PopupSizing.Resolve(
                e.WindowFeatures.HasSize ? (double?)e.WindowFeatures.Width : null,
                e.WindowFeatures.HasSize ? (double?)e.WindowFeatures.Height : null,
                owner?.ActualWidth, owner?.ActualHeight);

            popup = new PopupWindow(theme, owner, size.Width, size.Height);
            popup.Show();   // WebView2 の初期化には HWND が必要

            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = profileName;
            await popup.WebView.EnsureCoreWebView2Async(environment, options);

            popup.Attach(_openExternal);
            e.NewWindow = popup.WebView.CoreWebView2;
            e.Handled = true;

            _windows.Add(popup);
            var created = popup;
            popup.Closed += (_, _) => _windows.Remove(created);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[popup] failed: {ex.GetType().Name}: {ex.Message}");
            popup?.CloseQuietly();
            if (PopupWindow.IsOpenableUri(e.Uri))
            {
                e.Handled = true;
                _openExternal(e.Uri);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void DisposeAll()
    {
        foreach (var window in _windows.ToList())
        {
            window.CloseQuietly();
        }
        _windows.Clear();
    }
}

/// <summary>ポップアップの大きさの決め方(純粋関数)。</summary>
public static class PopupSizing
{
    public const double DefaultWidth = 520;
    public const double DefaultHeight = 680;
    public const double MinWidth = 320;
    public const double MinHeight = 240;

    /// <summary>
    /// ページが指定した大きさ(window.open の features)を使い、無ければ既定の大きさにする。
    /// 本体ウインドウより大きくならないようにし、小さすぎる指定は最小値まで広げる。
    /// </summary>
    public static (double Width, double Height) Resolve(
        double? requestedWidth, double? requestedHeight, double? ownerWidth, double? ownerHeight)
    {
        var width = requestedWidth is > 0 ? requestedWidth.Value : DefaultWidth;
        var height = requestedHeight is > 0 ? requestedHeight.Value : DefaultHeight;

        var maxWidth = ownerWidth is > 0 ? Math.Max(MinWidth, ownerWidth.Value - 40) : double.MaxValue;
        var maxHeight = ownerHeight is > 0 ? Math.Max(MinHeight, ownerHeight.Value - 40) : double.MaxValue;

        return (Math.Clamp(width, MinWidth, maxWidth), Math.Clamp(height, MinHeight, maxHeight));
    }
}

/// <summary>ポップアップを表示する、通常のトップレベルウインドウ(本体がオーナー)。</summary>
public sealed class PopupWindow : Window
{
    public WebView2 WebView { get; } = new();

    private bool _closed;

    internal PopupWindow(ThemeColors theme, Window? owner, double width, double height)
    {
        Width = width;
        Height = height;
        MinWidth = PopupSizing.MinWidth;
        MinHeight = PopupSizing.MinHeight;
        Title = "YMB Thatuation";
        Background = new System.Windows.Media.SolidColorBrush(TitleBarTheme.ParseColor(theme.Bar));
        ShowInTaskbar = false;
        Content = WebView;

        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        SourceInitialized += (_, _) => TitleBarTheme.Apply(this, theme);
        Closed += (_, _) =>
        {
            _closed = true;
            WebView.Dispose();
        };
    }

    /// <summary>WebView2 の初期化後に、タイトルの表示・自動クローズ・遷移の制限を設定する。</summary>
    internal void Attach(Action<string?> openExternal)
    {
        var core = WebView.CoreWebView2;

        // ポップアップの中でさらに新しいウインドウを開く連鎖は作らない。外部ブラウザへ。
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsOpenableUri(e.Uri)) openExternal(e.Uri);
        };

        // ページ自身の window.close()(OAuth の完了後など)で閉じる。
        core.WindowCloseRequested += (_, _) => CloseQuietly();

        // http/https 以外(file:// など)へは遷移させない。
        core.NavigationStarting += (_, e) =>
        {
            if (!IsAllowedNavigation(e.Uri)) e.Cancel = true;
        };

        core.DocumentTitleChanged += (_, _) => UpdateTitle();
        core.SourceChanged += (_, _) => UpdateTitle();
        core.ProcessFailed += (_, _) => CloseQuietly();
    }

    private void UpdateTitle()
    {
        if (_closed || WebView.CoreWebView2 is not { } core) return;

        var host = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
        var title = core.DocumentTitle;
        // どのサイトのウインドウか分かるよう、ホスト名を必ず出す(なりすまし対策)。
        Title = string.IsNullOrEmpty(host) ? "YMB Thatuation"
            : string.IsNullOrWhiteSpace(title) || title == host ? host
            : $"{host} - {title}";
    }

    public void CloseQuietly()
    {
        if (_closed) return;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 閉じている途中など。無視する。
        }
    }

    /// <summary>外部ブラウザで開いてよい URL か(http/https のみ)。</summary>
    public static bool IsOpenableUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>ポップアップ内で遷移してよい URL か(http/https と about:blank)。</summary>
    public static bool IsAllowedNavigation(string? uri) =>
        IsOpenableUri(uri) || string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase);
}

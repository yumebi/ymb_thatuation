using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
// WinForms(System.Drawing)も有効なプロジェクトのため、WPF 側の型を明示する
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

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

public enum PopupAddressKind
{
    /// <summary>https。</summary>
    Secure,
    /// <summary>http(暗号化されていない)。</summary>
    Insecure,
    /// <summary>about:blank など、上記以外。</summary>
    Other,
}

/// <summary>ポップアップのアドレス行に出す内容(純粋関数)。</summary>
public static class PopupAddress
{
    public sealed record Info(string Text, PopupAddressKind Kind);

    /// <summary>
    /// 表示する URL と、接続の種類を決める。
    /// 国際化ドメイン名は、見た目が似た別のサイトに見せかけられないよう、Punycode のまま(AbsoluteUri)出す。
    /// 空・解釈できない文字列は、そのまま(空は about:blank 扱い)出す。
    /// </summary>
    public static Info Describe(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return new Info("about:blank", PopupAddressKind.Other);

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return new Info(uri, PopupAddressKind.Other);

        var kind = parsed.Scheme == Uri.UriSchemeHttps ? PopupAddressKind.Secure
            : parsed.Scheme == Uri.UriSchemeHttp ? PopupAddressKind.Insecure
            : PopupAddressKind.Other;
        return new Info(WithPunycodeHost(parsed), kind);
    }

    /// <summary>
    /// AbsoluteUri は国際化ドメイン名を元の文字のまま返す。ホスト部分だけ Punycode(IdnHost)に置き換える。
    /// 例: キリル文字の "а" を使った偽の apple.com は、xn-- で始まる形で見える。
    /// </summary>
    private static string WithPunycodeHost(Uri parsed)
    {
        var absolute = parsed.AbsoluteUri;
        if (string.IsNullOrEmpty(parsed.Host) || parsed.Host == parsed.IdnHost) return absolute;

        var authority = parsed.GetLeftPart(UriPartial.Authority);
        if (!absolute.StartsWith(authority, StringComparison.OrdinalIgnoreCase)) return absolute;

        var hostIndex = authority.IndexOf(parsed.Host, StringComparison.OrdinalIgnoreCase);
        if (hostIndex < 0) return absolute;

        var punyAuthority = authority.Substring(0, hostIndex) + parsed.IdnHost
            + authority.Substring(hostIndex + parsed.Host.Length);
        return punyAuthority + absolute.Substring(authority.Length);
    }
}

/// <summary>ポップアップを表示する、通常のトップレベルウインドウ(本体がオーナー)。</summary>
public sealed class PopupWindow : Window
{
    public WebView2 WebView { get; } = new();

    private readonly ThemeColors _theme;
    private readonly TextBlock _lockIcon = new();
    private readonly System.Windows.Controls.TextBox _address = new();
    private bool _closed;

    internal PopupWindow(ThemeColors theme, Window? owner, double width, double height)
    {
        _theme = theme;
        Width = width;
        Height = height;
        MinWidth = PopupSizing.MinWidth;
        MinHeight = PopupSizing.MinHeight;
        Title = "YMB Thatuation";
        Background = new System.Windows.Media.SolidColorBrush(TitleBarTheme.ParseColor(theme.Bar));
        ShowInTaskbar = false;
        Content = BuildLayout(theme);

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

    /// <summary>
    /// 上段に読み取り専用のアドレス行、下段に WebView2 を置く。
    /// WebView2(HWND)の上には重ねず、行を分けて並べる(重ねた WPF 要素は描画されないため)。
    /// どのサイトのウインドウか、URL 全体を見て確かめられるようにする(OAuth の弾窓でのなりすまし対策)。
    /// </summary>
    private Grid BuildLayout(ThemeColors theme)
    {
        var text = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Text));
        var field = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Button));
        var border = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Border));

        _lockIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");
        _lockIcon.FontSize = 13;
        _lockIcon.VerticalAlignment = VerticalAlignment.Center;
        _lockIcon.Margin = new Thickness(0, 0, 8, 0);
        _lockIcon.Foreground = text;

        _address.IsReadOnly = true;
        _address.BorderThickness = new Thickness(0);
        _address.Background = Brushes.Transparent;
        _address.Foreground = text;
        _address.FontSize = 12;
        _address.VerticalContentAlignment = VerticalAlignment.Center;
        _address.Padding = new Thickness(0, 2, 0, 2);
        _address.Text = "";

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_lockIcon, 0);
        Grid.SetColumn(_address, 1);
        bar.Children.Add(_lockIcon);
        bar.Children.Add(_address);

        var addressRow = new Border
        {
            Background = field,
            BorderBrush = border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 4, 10, 4),
            Child = bar,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(addressRow, 0);
        Grid.SetRow(WebView, 1);
        root.Children.Add(addressRow);
        root.Children.Add(WebView);

        UpdateAddress();
        return root;
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
        core.SourceChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateAddress();
        };
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

    private void UpdateAddress()
    {
        if (_closed) return;

        var source = WebView.CoreWebView2?.Source;
        var info = PopupAddress.Describe(source);
        _address.Text = info.Text;
        _address.ToolTip = info.Text;
        _lockIcon.Text = info.Kind switch
        {
            PopupAddressKind.Secure => "\uE72E",     // 鍵
            PopupAddressKind.Insecure => "\uE7BA",   // 警告
            _ => "\uE774",                           // 地球
        };
        _lockIcon.Foreground = info.Kind == PopupAddressKind.Insecure
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0x9A, 0x00))
            : new SolidColorBrush(TitleBarTheme.ParseColor(_theme.Text));
        _lockIcon.ToolTip = info.Kind switch
        {
            PopupAddressKind.Secure => "暗号化された接続(https)",
            PopupAddressKind.Insecure => "暗号化されていない接続(http)。パスワードなどを入力しないでください",
            _ => null,
        };
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

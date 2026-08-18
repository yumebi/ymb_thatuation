using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace YmbThatuation.Services;

/// <summary>
/// OAuth等の新規ウインドウ要求をアプリ内オーバーレイで完結させる。
/// e.NewWindowへの割り当てはハングするため使用せず、独自にWebView2コントロールを
/// 作成してContentHost上にオーバーレイ表示する。
/// ナビゲーション先が親サービスのドメインに戻ったら(=OAuth完了)自動で閉じる。
/// </summary>
public sealed class PopupManager
{
    private readonly List<PopupSession> _sessions = new();

    /// <summary>
    /// ポップアップを表示する。親サービスのドメインにOAuthのリダイレクト先が
    /// 戻ったら自動で閉じる。
    /// </summary>
    public async Task ShowAsync(
        string parentInstanceId,
        string parentHost,
        string uri,
        CoreWebView2Environment environment,
        System.Windows.Controls.Grid contentHost,
        ThemeColors theme)
    {
        var session = new PopupSession
        {
            ParentInstanceId = parentInstanceId,
            ParentHost = parentHost,
        };

        var overlay = BuildOverlay(session);
        var panel = BuildPanel(session, theme);
        overlay.Children.Add(panel);
        contentHost.Children.Add(overlay);
        session.Overlay = overlay;

        var webview = new WebView2();
        session.WebView = webview;
        System.Windows.Controls.Grid.SetRow(panel, 0);
        var webviewContainer = (System.Windows.Controls.StackPanel)panel.Children[0];
        webviewContainer.Children.Add(webview);

        try
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = $"popup-{parentInstanceId}-{DateTime.UtcNow.Ticks}";
            await webview.EnsureCoreWebView2Async(environment, options);

            webview.CoreWebView2.NavigationStarting += (_, e) => OnNavigationStarting(session, e);
            webview.CoreWebView2.SourceChanged += (_, _) => OnSourceChanged(session, contentHost);
            webview.CoreWebView2.ProcessFailed += (_, _) => Close(session, contentHost);

            webview.CoreWebView2.Navigate(uri);
            _sessions.Add(session);
        }
        catch (Exception)
        {
            Close(session, contentHost);
        }
    }

    private void OnNavigationStarting(PopupSession session, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed)) return;

        if (IsSameDomain(parsed.Host, session.ParentHost))
        {
            e.Cancel = true;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (session.Overlay?.Parent is System.Windows.Controls.Grid contentHost)
                    Close(session, contentHost);
            });
        }
    }

    private void OnSourceChanged(PopupSession session, System.Windows.Controls.Grid contentHost)
    {
        if (session.WebView?.CoreWebView2 == null) return;
        var sourceStr = session.WebView.CoreWebView2.Source;
        if (string.IsNullOrEmpty(sourceStr)) return;
        if (!Uri.TryCreate(sourceStr, UriKind.Absolute, out var sourceUri)) return;

        var host = sourceUri.Host;
        if (string.IsNullOrEmpty(host)) return;

        if (IsSameDomain(host, session.ParentHost))
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                Close(session, contentHost));
        }
    }

    /// <summary>
    /// 2つのホストが同一ドメインかどうかを判定する。
    /// "app.slack.com" と "slack.com" を同一とみなす等、サブドメインの前方一致で判断する。
    /// </summary>
    private static bool IsSameDomain(string a, string b)
    {
        a = a.ToLowerInvariant().TrimEnd('.');
        b = b.ToLowerInvariant().TrimEnd('.');
        return a == b || a.EndsWith("." + b) || b.EndsWith("." + a);
    }

    private void Close(PopupSession session, System.Windows.Controls.Grid contentHost)
    {
        if (session.Closed) return;
        session.Closed = true;

        session.WebView?.Dispose();
        if (session.Overlay != null)
        {
            contentHost.Children.Remove(session.Overlay);
        }
        _sessions.Remove(session);
    }

    public void DisposeAll()
    {
        foreach (var session in _sessions)
        {
            session.WebView?.Dispose();
        }
        _sessions.Clear();
    }

    /// <summary>
    /// 半透明暗幕を構築する。クリックでポップアップを閉じる。
    /// </summary>
    private System.Windows.Controls.Grid BuildOverlay(PopupSession session)
    {
        var overlay = new System.Windows.Controls.Grid
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(128, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
        };

        overlay.MouseLeftButtonDown += (_, _) =>
        {
            if (overlay.Parent is System.Windows.Controls.Grid contentHost)
                Close(session, contentHost);
        };

        return overlay;
    }

    /// <summary>
    /// タイトルバー(ドメイン + 閉じるボタン) + WebView2コンテナを構築する。
    /// </summary>
    private System.Windows.Controls.Grid BuildPanel(PopupSession session, ThemeColors theme)
    {
        var barBg = ToBrush(theme.Bar);
        var btnHoverBg = ToBrush(theme.ButtonHover);
        var fg = ToBrush(theme.Text);

        var panel = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(40, 30, 40, 30),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
        };
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            { Height = System.Windows.GridLength.Auto });
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var titleBar = new System.Windows.Controls.Grid
        {
            Background = barBg,
            Height = 32,
        };
        titleBar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            { Width = System.Windows.GridLength.Auto });

        var domainText = new System.Windows.Controls.TextBlock
        {
            Text = ExtractDomain(session.ParentHost),
            Foreground = fg,
            FontSize = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(10, 0, 0, 0),
            Opacity = 0.7,
        };
        System.Windows.Controls.Grid.SetColumn(domainText, 0);
        titleBar.Children.Add(domainText);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "\u2715",
            Foreground = fg,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Width = 32,
            Height = 32,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 14,
        };
        closeBtn.Template = InstanceManager.FlatButtonTemplate;
        closeBtn.MouseEnter += (_, _) => closeBtn.Background = btnHoverBg;
        closeBtn.MouseLeave += (_, _) => closeBtn.Background = System.Windows.Media.Brushes.Transparent;
        closeBtn.Click += (_, _) =>
        {
            if (session.Overlay?.Parent is System.Windows.Controls.Grid contentHost)
                Close(session, contentHost);
        };
        System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(closeBtn);

        System.Windows.Controls.Grid.SetRow(titleBar, 0);
        panel.Children.Add(titleBar);

        var webviewContainer = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
        };
        System.Windows.Controls.Grid.SetRow(webviewContainer, 1);
        panel.Children.Add(webviewContainer);

        return panel;
    }

    private static string ExtractDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        return string.Join('.', parts[^2..]);
    }

    private static System.Windows.Media.Brush ToBrush(string hex) =>
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private sealed class PopupSession
    {
        public string ParentInstanceId { get; init; } = "";
        public string ParentHost { get; init; } = "";
        public WebView2? WebView { get; set; }
        public System.Windows.Controls.Grid? Overlay { get; set; }
        public bool Closed { get; set; }
    }
}

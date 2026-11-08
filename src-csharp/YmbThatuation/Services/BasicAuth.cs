using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
// WinForms(System.Drawing)も有効なプロジェクトのため、WPF 側の型を明示する
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace YmbThatuation.Services;

/// <summary>認証の窓に出す内容。</summary>
/// <param name="Host">認証を求めているサイト(ホスト名とポート)。国際化ドメイン名は Punycode のまま。</param>
/// <param name="Insecure">暗号化されていない接続(http)か。</param>
/// <param name="SiteMessage">サイトが出した文(realm など)。サイトが自由に書けるので、別の行に、短くして出す。</param>
public sealed record BasicAuthInfo(string Host, bool Insecure, string? SiteMessage);

/// <summary>
/// Basic 認証(WWW-Authenticate: Basic)の要求に、ユーザー名とパスワードの入力窓で答える。
/// 既定の WebView2 は入力窓を出さないため、認証が必要なページが、白紙のまま止まっていた。
/// - 入力窓には、どのサイトが求めているか(ホスト名)を必ず出す。http のときは警告を出す。
/// - 今開いているページと別のホストからの要求(ページの中の画像など)では、窓を出さない。
///   (別のサイトが、見ているページのふりをして、認証を求めるのを防ぐ。ブラウザと同じ考え方)
/// - パスワードは保存しない。入力を取り消すと、認証を断る。
/// </summary>
public static class BasicAuth
{
    private const int MaxSiteMessageLength = 100;

    /// <summary>窓に出す内容を決める(純粋関数)。</summary>
    public static BasicAuthInfo Describe(string? requestUri, string? challenge)
    {
        var host = "";
        var insecure = false;
        if (Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            // 見た目の似た別のサイトに見せかけられないよう、国際化ドメイン名は Punycode のまま出す
            host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            insecure = uri.Scheme == Uri.UriSchemeHttp;
        }
        return new BasicAuthInfo(host, insecure, SanitizeMessage(challenge));
    }

    /// <summary>
    /// サイトが出した文(realm)を取り出し、制御文字を除き、長すぎる分を切る。空なら null。
    /// 要求のヘッダー(例: Basic realm="Staging only")が来たときは、realm の中身だけを出す。
    /// </summary>
    public static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var realm = System.Text.RegularExpressions.Regex.Match(message, @"realm\s*=\s*""([^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (realm.Success) message = realm.Groups[1].Value;
        var clean = new string(message.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= MaxSiteMessageLength ? clean : clean.Substring(0, MaxSiteMessageLength) + "…";
    }

    /// <summary>
    /// 入力窓を出してよい要求か(純粋関数)。
    /// 要求先のホストが、これから開くページ(navigationHost)か、いま開いているページ(currentSource)と同じなら出す。
    /// どちらも無い(まだ何も開いていない)ときは、最初の移動の要求なので、出す。
    /// </summary>
    public static bool IsPromptAllowed(string? requestUri, string? navigationHost, string? currentSource)
    {
        var requestHost = HostOf(requestUri);
        if (string.IsNullOrEmpty(requestHost)) return false;

        var currentHost = HostOf(currentSource);
        if (string.IsNullOrEmpty(navigationHost) && string.IsNullOrEmpty(currentHost)) return true;

        return string.Equals(requestHost, navigationHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestHost, currentHost, StringComparison.OrdinalIgnoreCase);
    }

    public static string HostOf(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? parsed.IdnHost
            : "";

    /// <summary>WebView2 に、Basic 認証の要求への対応をつなぐ。</summary>
    /// <param name="ask">入力窓を出して、(ユーザー名, パスワード)を返す。取り消されたら null。テストでは差し替える。</param>
    public static void Attach(
        CoreWebView2 core,
        Func<Window?> ownerProvider,
        Func<ThemeColors> themeProvider,
        Func<BasicAuthInfo, Window?, ThemeColors, (string UserName, string Password)?>? ask = null)
    {
        ask ??= (info, owner, theme) => BasicAuthDialog.Ask(owner, theme, info);
        string? navigationHost = null;

        // これから開くページのホスト(リダイレクトでも更新される)
        core.NavigationStarting += (_, e) =>
        {
            var host = HostOf(e.Uri);
            if (!string.IsNullOrEmpty(host)) navigationHost = host;
        };

        core.BasicAuthenticationRequested += (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (!IsPromptAllowed(e.Uri, navigationHost, core.Source))
                {
                    e.Cancel = true;
                    return;
                }
                var cred = ask(Describe(e.Uri, e.Challenge), ownerProvider(), themeProvider());
                if (cred is null)
                {
                    e.Cancel = true;
                }
                else
                {
                    e.Response.UserName = cred.Value.UserName;
                    e.Response.Password = cred.Value.Password;
                }
            }
            catch (Exception ex)
            {
                // ユーザー名・パスワードは、ログに出さない
                Debug.WriteLine($"[basic-auth] failed: {ex.GetType().Name}");
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
    }
}

/// <summary>ユーザー名とパスワードを入れる、小さな窓(本体や、ポップアップがオーナー)。</summary>
public sealed class BasicAuthDialog : Window
{
    private readonly System.Windows.Controls.TextBox _user = new();
    private readonly PasswordBox _pass = new();

    private BasicAuthDialog(Window? owner, ThemeColors theme, BasicAuthInfo info)
    {
        var bar = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Bar));
        var text = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Text));
        var field = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Button));
        var border = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Border));
        var accent = new SolidColorBrush(TitleBarTheme.ParseColor(theme.Accent));

        Title = "認証が必要です";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = bar;
        Foreground = text;
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this, theme);
        ContentRendered += (_, _) =>
        {
            Activate();
            _user.Focus();
        };

        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };

        var head = new TextBlock
        {
            Text = $"{info.Host} がユーザー名とパスワードを求めています",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = text,
        };
        panel.Children.Add(head);

        if (info.Insecure)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暗号化されていない接続(http)です。入力した内容が、途中で盗み見られる恐れがあります。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x9A, 0x00)),
            });
        }

        if (!string.IsNullOrEmpty(info.SiteMessage))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"サイトからのメッセージ: {info.SiteMessage}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12,
                Opacity = 0.75,
                Foreground = text,
            });
        }

        panel.Children.Add(Label("ユーザー名", text));
        StyleInput(_user, field, text, border, accent);
        _user.Name = "BasicAuthUser";
        panel.Children.Add(_user);

        panel.Children.Add(Label("パスワード", text));
        StyleInput(_pass, field, text, border, accent);
        _pass.Name = "BasicAuthPassword";
        panel.Children.Add(_pass);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = MakeButton("ログイン", accent, Brushes.White, border);
        ok.Name = "BasicAuthOk";
        ok.IsDefault = true;
        ok.Click += (_, _) => DialogResult = true;
        var cancel = MakeButton("キャンセル", field, text, border);
        cancel.Name = "BasicAuthCancel";
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static TextBlock Label(string text, System.Windows.Media.Brush foreground) => new()
    {
        Text = text,
        Margin = new Thickness(0, 12, 0, 4),
        FontSize = 12,
        Foreground = foreground,
    };

    private static void StyleInput(System.Windows.Controls.Control input, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg, System.Windows.Media.Brush border, System.Windows.Media.Brush caret)
    {
        input.Background = bg;
        input.Foreground = fg;
        input.BorderBrush = border;
        input.BorderThickness = new Thickness(1);
        input.Padding = new Thickness(6, 5, 6, 5);
        input.FontSize = 13;
        input.FontFamily = new FontFamily("Segoe UI");
    }

    private static System.Windows.Controls.Button MakeButton(string label, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg, System.Windows.Media.Brush border) => new()
    {
        Content = label,
        Background = bg,
        Foreground = fg,
        BorderBrush = border,
        Padding = new Thickness(16, 6, 16, 6),
        MinWidth = 84,
    };

    /// <summary>窓を出して、(ユーザー名, パスワード)を返す。キャンセルされたら null。</summary>
    public static (string UserName, string Password)? Ask(Window? owner, ThemeColors theme, BasicAuthInfo info)
    {
        var dlg = new BasicAuthDialog(owner, theme, info);
        try
        {
            return dlg.ShowDialog() == true ? (dlg._user.Text, dlg._pass.Password) : null;
        }
        finally
        {
            dlg._pass.Clear();
        }
    }
}

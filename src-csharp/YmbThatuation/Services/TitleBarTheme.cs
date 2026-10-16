using System.Windows;
using System.Windows.Interop;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace YmbThatuation.Services;

/// <summary>
/// ウインドウのタイトルバーをアプリのテーマ配色に合わせる。
/// 何もしないと、ダークテーマでもタイトルバーだけ白い(システム設定の色)ままになる。
/// ダークモードの指定はWindows 10 2004以降、キャプション色などの指定はWindows 11以降で有効。
/// 非対応の環境では何も起きない(例外は握りつぶす)。
/// </summary>
public static class TitleBarTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void Apply(Window window, ThemeColors theme)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var bar = ParseColor(theme.Bar);
            var dark = IsDark(bar) ? 1 : 0;
            Win32.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            var caption = ToColorRef(bar);
            Win32.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            var text = ToColorRef(ParseColor(theme.Text));
            Win32.DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            var border = ToColorRef(ParseColor(theme.Border));
            Win32.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or FormatException)
        {
            // 古いWindows等で未対応。見た目が変わらないだけなので無視する。
        }
    }

    public static MediaColor ParseColor(string hex) => (MediaColor)MediaColorConverter.ConvertFromString(hex);

    /// <summary>相対輝度が低い色なら暗い色とみなす。</summary>
    public static bool IsDark(MediaColor c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;

    /// <summary>DWMが使うCOLORREF(0x00BBGGRR)に変換する。</summary>
    public static int ToColorRef(MediaColor c) => c.R | (c.G << 8) | (c.B << 16);
}

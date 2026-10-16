using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace YmbThatuation.Services;

/// <summary>
/// ファイルダウンロードの進捗を画面右下に表示する。既定のWebView2はダウンロード中/完了の
/// UIを一切出さないため、完了したのかどうかが分からない問題への対処。
/// クリックでダウンロード先フォルダを開ける(進行中でも可)。進行中は右上の✕でキャンセルできる。
///
/// 表示するモニタは「本体ウインドウがあるモニタ」(本体が見えていなければマウスのあるモニタ)。
/// 位置は物理ピクセルで決めるので、DPIの異なるモニタが混在しても正しい位置と大きさになる。
/// クリックしても本体のフォーカスを奪わず、Alt+Tabにも出ない。
/// </summary>
public class DownloadNotificationWindow : Window
{
    private const double WindowWidth = 320;
    private const double WindowHeight = 64;
    private const double Gap = 8;
    private const double EdgeMargin = 16;

    private static readonly List<DownloadNotificationWindow> Active = new();

    private readonly string _filePath;
    private readonly string _doneText;
    private readonly string _failedText;
    private readonly TextBlock _nameText;
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly TextBlock _cancelButton;
    private Action? _onCancel;
    private bool _finished;
    private bool _closed;

    public DownloadNotificationWindow(
        string fileName, string filePath, ThemeColors theme, IReadOnlyDictionary<string, string> translations)
    {
        _filePath = filePath;
        _doneText = NotificationText.Format(translations, "download.done", "ダウンロード完了 ・ クリックでフォルダを開く");
        _failedText = NotificationText.Format(translations, "download.failed", "ダウンロードがキャンセル/失敗しました");

        var text = Brush(theme.Text);
        var muted = Brush(theme.Text);
        muted.Opacity = 0.65;

        Width = WindowWidth;
        Height = WindowHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brush(theme.Bar);
        BorderBrush = Brush(theme.Border);
        BorderThickness = new Thickness(1);

        var panel = new StackPanel { Margin = new Thickness(12, 8, 32, 8) };
        _nameText = new TextBlock
        {
            Foreground = text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = fileName,
        };
        _progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 6,
            Margin = new Thickness(0, 6, 0, 4),
            Minimum = 0,
            Maximum = 100,
            Foreground = Brush(theme.Accent),
            Background = Brush(theme.Button),
            BorderThickness = new Thickness(0),
        };
        _statusText = new TextBlock
        {
            Foreground = muted,
            FontSize = 11,
            Text = NotificationText.Format(translations, "download.progress", "ダウンロード中…"),
        };
        panel.Children.Add(_nameText);
        panel.Children.Add(_progressBar);
        panel.Children.Add(_statusText);

        _cancelButton = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _cancelButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _onCancel?.Invoke();
        };

        var grid = new Grid();
        grid.Children.Add(panel);
        grid.Children.Add(_cancelButton);
        Content = grid;

        MouseLeftButtonUp += (_, _) => OpenFolder();

        // 表示される前に位置を確定させる(既定位置に一瞬出てから動く、を防ぐ)。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExStyle(hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            Active.Add(this);
            RelayoutAll();
        };
    }

    public void SetCancelAction(Action onCancel) => _onCancel = onCancel;

    public void UpdateProgress(long received, ulong? total)
    {
        if (total is > 0)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = Math.Min(100, received * 100.0 / total.Value);
            _statusText.Text = $"{received / 1024.0 / 1024.0:F1} MB / {total.Value / 1024.0 / 1024.0:F1} MB";
        }
        else
        {
            _progressBar.IsIndeterminate = true;
            _statusText.Text = $"{received / 1024.0 / 1024.0:F1} MB";
        }
    }

    public void SetCompleted()
    {
        _finished = true;
        _cancelButton.Visibility = Visibility.Collapsed;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _statusText.Text = _doneText;
        _ = AutoCloseAfterAsync();
    }

    public void SetInterrupted()
    {
        _finished = true;
        _cancelButton.Visibility = Visibility.Collapsed;
        _statusText.Text = _failedText;
        _ = AutoCloseAfterAsync();
    }

    private async Task AutoCloseAfterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        CloseSelf();
    }

    private void OpenFolder()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        psi.ArgumentList.Add($"/select,\"{_filePath}\"");
        Process.Start(psi);
        if (_finished) CloseSelf();
    }

    private void CloseSelf()
    {
        if (_closed) return;
        _closed = true;
        Active.Remove(this);
        Close();
        RelayoutAll();
    }

    /// <summary>通知を出すモニタの作業領域(物理ピクセル)。</summary>
    private static System.Drawing.Rectangle TargetWorkArea()
    {
        var main = System.Windows.Application.Current?.MainWindow;
        if (main is not DownloadNotificationWindow && main is { IsVisible: true } && main.WindowState != WindowState.Minimized)
        {
            var hwnd = new WindowInteropHelper(main).Handle;
            if (hwnd != IntPtr.Zero) return Forms.Screen.FromHandle(hwnd).WorkingArea;
        }
        return Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
    }

    /// <summary>表示中の通知を、対象モニタの右下から上に向かって積み直す。</summary>
    private static void RelayoutAll()
    {
        var area = TargetWorkArea();
        for (var i = 0; i < Active.Count; i++)
        {
            Active[i].PlaceAt(area, i);
        }
    }

    private void PlaceAt(System.Drawing.Rectangle area, int index)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先に目的のモニタ上へ移してDPI倍率を確定させる(別DPIのモニタへ動くと大きさが換算し直される)。
        Win32.SetWindowPos(hwnd, IntPtr.Zero, area.Right - 40, area.Bottom - 40, 10, 10,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        var scale = Win32.GetScale(hwnd);
        var width = (int)Math.Round(WindowWidth * scale);
        var height = (int)Math.Round(WindowHeight * scale);
        var margin = (int)Math.Round(EdgeMargin * scale);
        var gap = (int)Math.Round(Gap * scale);

        var x = area.Right - width - margin;
        var y = area.Bottom - height - margin - (height + gap) * index;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private static SolidColorBrush Brush(string hex) => new(TitleBarTheme.ParseColor(hex));
}

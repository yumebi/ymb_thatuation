using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YmbThatuation.Services;

/// <summary>
/// ファイルダウンロードの進捗を画面右下に表示する。既定のWebView2はダウンロード中/完了の
/// UIを一切出さないため、完了したのかどうかが分からない問題への対処。
/// </summary>
public class DownloadNotificationWindow : Window
{
    private const double WindowWidth = 320;
    private const double WindowHeight = 64;
    private const double Gap = 8;
    private const double EdgeMargin = 16;

    private static readonly List<DownloadNotificationWindow> Active = new();

    private readonly TextBlock _nameText;
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private string? _completedFilePath;
    private bool _closed;

    public DownloadNotificationWindow(string fileName)
    {
        Width = WindowWidth;
        Height = WindowHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x26, 0x2b));

        var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        _nameText = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
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
        };
        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8a, 0x8c, 0x94)),
            FontSize = 11,
            Text = "ダウンロード中…",
        };
        panel.Children.Add(_nameText);
        panel.Children.Add(_progressBar);
        panel.Children.Add(_statusText);
        Content = panel;

        MouseLeftButtonUp += (_, _) => OpenFolderIfCompleted();
        Loaded += (_, _) => Reposition();
    }

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

    public void SetCompleted(string filePath)
    {
        _completedFilePath = filePath;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _statusText.Text = "ダウンロード完了 ・ クリックでフォルダを開く";
        _ = AutoCloseAfterAsync();
    }

    public void SetInterrupted()
    {
        _statusText.Text = "ダウンロードに失敗しました";
        _ = AutoCloseAfterAsync();
    }

    private async Task AutoCloseAfterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        CloseSelf();
    }

    private void OpenFolderIfCompleted()
    {
        if (_completedFilePath == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_completedFilePath}\"") { UseShellExecute = true });
        CloseSelf();
    }

    private void Reposition()
    {
        Active.Add(this);
        RelayoutAll();
    }

    private void CloseSelf()
    {
        if (_closed) return;
        _closed = true;
        Active.Remove(this);
        Close();
        RelayoutAll();
    }

    private static void RelayoutAll()
    {
        var area = SystemParameters.WorkArea;
        for (var i = 0; i < Active.Count; i++)
        {
            Active[i].Left = area.Right - WindowWidth - EdgeMargin;
            Active[i].Top = area.Bottom - WindowHeight - EdgeMargin - (WindowHeight + Gap) * i;
        }
    }
}

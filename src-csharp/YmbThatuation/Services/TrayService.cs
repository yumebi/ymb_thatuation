using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Forms = System.Windows.Forms;

namespace YmbThatuation.Services;

/// <summary>
/// システムトレイアイコン・デスクトップ通知・通知音・タスクバーの未読バッジを管理する。
/// Tauri版のTrayIconBuilder/update_overlay_badge/play_notification_sound相当。
/// </summary>
public class TrayService : IDisposable
{
    private readonly Window _window;
    private readonly ConfigStore _configStore;
    private readonly Forms.NotifyIcon _notifyIcon;

    public bool IsExiting { get; private set; }

    public TrayService(Window window, ConfigStore configStore, string wwwrootDir)
    {
        _window = window;
        _configStore = configStore;

        var t = Translations.Load(wwwrootDir, configStore.Get().Settings.Language);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = true,
            Text = "YMB Thatuation",
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(t.GetValueOrDefault("tray.show", "Show"), null, (_, _) => ShowWindow());
        menu.Items.Add(t.GetValueOrDefault("tray.quit", "Quit"), null, (_, _) => Quit());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
        IsExiting = true;
        _window.Close();
    }

    public void UpdateOverlayBadge(int total)
    {
        _window.TaskbarItemInfo ??= new TaskbarItemInfo();
        _window.TaskbarItemInfo.Overlay = total > 0 ? CreateBadge() : null;
    }

    private static ImageSource CreateBadge()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe0, 0x5d, 0x5d));
            dc.DrawEllipse(brush, null, new System.Windows.Point(size / 2.0, size / 2.0), size / 2.0 - 1, size / 2.0 - 1);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    public void ShowNotification(string title, string body)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = body;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void PlayNotificationSound()
    {
        var settings = _configStore.Get().Settings;
        if (!settings.NotificationSound) return;

        var path = settings.NotificationSoundPath;
        Task.Run(() =>
        {
            const uint SND_NODEFAULT = 0x0002;
            const uint SND_ALIAS = 0x00010000;
            const uint SND_FILENAME = 0x00020000;

            // SND_ASYNCはスレッドプール上のスレッド終了時に再生が途切れることがあるため、
            // Task.Run内でSND_SYNC(同期)再生する。
            if (string.IsNullOrEmpty(path))
            {
                PlaySound("Notification.Default", IntPtr.Zero, SND_NODEFAULT | SND_ALIAS);
            }
            else
            {
                PlaySound(path, IntPtr.Zero, SND_NODEFAULT | SND_FILENAME);
            }
        });
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr hmod, uint flags);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

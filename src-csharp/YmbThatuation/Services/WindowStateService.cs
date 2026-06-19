using System.IO;
using System.Text.Json;
using System.Windows;

namespace YmbThatuation.Services;

/// <summary>
/// メインウインドウの位置・サイズ・最大化状態をwindow-state.jsonへ保存/復元する。
/// Tauri版のtauri-plugin-window-state相当。
/// </summary>
public class WindowStateService
{
    private readonly string _path;

    public WindowStateService(ConfigStore configStore)
    {
        _path = Path.Combine(configStore.AppDataDir, "config", "window-state.json");
    }

    public void Restore(Window window)
    {
        WindowGeometry? geometry;
        try
        {
            geometry = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(_path));
        }
        catch
        {
            return;
        }
        if (geometry == null) return;

        if (geometry.Width > 0 && geometry.Height > 0)
        {
            window.Width = geometry.Width;
            window.Height = geometry.Height;
        }
        if (geometry.Left.HasValue && geometry.Top.HasValue)
        {
            // モニタ構成が保存時と変わっている(取り外し等)場合、保存値をそのまま使うと
            // 画面外に出て操作不能になりうるため、現在の仮想スクリーン範囲内に収める。
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;
            var maxLeft = Math.Max(screenLeft, screenLeft + SystemParameters.VirtualScreenWidth - geometry.Width);
            var maxTop = Math.Max(screenTop, screenTop + SystemParameters.VirtualScreenHeight - geometry.Height);

            window.Left = Math.Clamp(geometry.Left.Value, screenLeft, maxLeft);
            window.Top = Math.Clamp(geometry.Top.Value, screenTop, maxTop);
        }
        if (geometry.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        if (window is MainWindow main && geometry.SidebarWidth is { } sidebarWidth)
        {
            var min = main.SidebarColumn.MinWidth;
            var max = main.SidebarColumn.MaxWidth;
            main.SidebarColumn.Width = new GridLength(Math.Clamp(sidebarWidth, min, max));
        }
    }

    public void Save(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        var geometry = new WindowGeometry
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Left = bounds.Left,
            Top = bounds.Top,
            Maximized = window.WindowState == WindowState.Maximized,
            SidebarWidth = window is MainWindow main ? main.SidebarColumn.Width.Value : null,
        };

        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(geometry));
    }

    private class WindowGeometry
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public bool Maximized { get; set; }
        public double? SidebarWidth { get; set; }
    }
}

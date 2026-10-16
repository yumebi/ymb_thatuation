using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace YmbThatuation.Services;

/// <summary>
/// メインウインドウの位置・サイズ・最大化状態をwindow-state.jsonへ保存/復元する。
/// Tauri版のtauri-plugin-window-state相当。
///
/// 位置はWin32のGetWindowPlacementで物理ピクセルのまま保存する。WPFのLeft/Topは
/// DIP(論理ピクセル)で、DPIの異なるモニタが混在すると換算がずれるため使わない。
/// 復元時は現在のモニタ構成に合わせて補正する(WindowPlacementMath.Fit)。
/// </summary>
public class WindowStateService
{
    private readonly string _path;

    public WindowStateService(ConfigStore configStore)
    {
        _path = Path.Combine(configStore.AppDataDir, "config", "window-state.json");
    }

    /// <summary>復元した結果。</summary>
    public sealed record Restored(string? LastActiveId, bool Maximized);

    /// <summary>
    /// 位置とサイズを復元する。HWNDが作られた後・初回表示の前(SourceInitialized)に呼ぶ。
    /// 表示前に位置を決めるので、既定位置に一瞬出てから動く、というちらつきが起きない。
    /// </summary>
    public Restored Apply(MainWindow window)
    {
        var geometry = Read();
        if (geometry == null) return new Restored(null, false);

        var hwnd = new WindowInteropHelper(window).Handle;
        var desired = ToDesiredRect(geometry);
        if (desired is { } rect && hwnd != IntPtr.Zero)
        {
            var scale = Win32.GetScale(hwnd);
            var fitted = WindowPlacementMath.Fit(
                rect, WorkAreas(), (int)(window.MinWidth * scale), (int)(window.MinHeight * scale));

            // 別DPIのモニタへ動かすと、WM_DPICHANGEDで大きさが換算し直されることがある。
            // 2回目で保存したピクセル値そのものに揃える。
            for (var i = 0; i < 2; i++)
            {
                Win32.SetWindowPos(hwnd, IntPtr.Zero, fitted.X, fitted.Y, fitted.Width, fitted.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            }
        }

        if (geometry.SidebarWidth is { } sidebarWidth)
        {
            var min = window.SidebarColumn.MinWidth;
            var max = window.SidebarColumn.MaxWidth;
            window.SidebarColumn.Width = new System.Windows.GridLength(Math.Clamp(sidebarWidth, min, max));
        }

        return new Restored(geometry.LastActiveId, geometry.Maximized);
    }

    public void Save(MainWindow window, string? activeId)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var placement = new Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (!Win32.GetWindowPlacement(hwnd, ref placement)) return;

        // rcNormalPositionは「作業領域の左上を原点とする座標」なので、スクリーン座標に直す。
        var origin = Forms.Screen.PrimaryScreen?.WorkingArea.Location ?? default;
        var normal = placement.rcNormalPosition;
        var x = normal.Left + origin.X;
        var y = normal.Top + origin.Y;
        var width = normal.Right - normal.Left;
        var height = normal.Bottom - normal.Top;
        if (width <= 0 || height <= 0) return;

        // 最大化していたウインドウを最小化した場合も「最大化」として扱う。
        var maximized = placement.showCmd == Win32.SW_SHOWMAXIMIZED
            || (placement.showCmd == Win32.SW_SHOWMINIMIZED
                && (placement.flags & Win32.WPF_RESTORETOMAXIMIZED) != 0);

        var scale = Win32.GetScale(hwnd);
        var geometry = new WindowGeometry
        {
            PxX = x,
            PxY = y,
            PxWidth = width,
            PxHeight = height,
            // 旧バージョンが読む用。DIPに換算した値を残しておく。
            Width = width / scale,
            Height = height / scale,
            Left = x / scale,
            Top = y / scale,
            Maximized = maximized,
            SidebarWidth = window.SidebarColumn.Width.Value,
            LastActiveId = activeId,
        };

        Write(geometry);
    }

    private WindowGeometry? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>途中で強制終了されても壊れないよう、一時ファイルに書いてから置き換える。</summary>
    private void Write(WindowGeometry geometry)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(geometry));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // 保存に失敗しても本体の動作は止めない。
        }
    }

    /// <summary>保存された値から、復元したい物理ピクセルの矩形を求める。</summary>
    private static PxRect? ToDesiredRect(WindowGeometry g)
    {
        if (g.PxWidth is > 0 && g.PxHeight is > 0 && g.PxX is { } px && g.PxY is { } py)
        {
            return new PxRect(px, py, g.PxWidth.Value, g.PxHeight.Value);
        }

        // 旧形式(DIP)。旧バージョンはシステムDPI基準だったので、システムDPIの倍率で換算する。
        if (g.Width > 0 && g.Height > 0 && g.Left.HasValue && g.Top.HasValue)
        {
            var scale = Win32.GetDpiForSystem() / 96.0;
            return new PxRect(
                (int)Math.Round(g.Left.Value * scale), (int)Math.Round(g.Top.Value * scale),
                (int)Math.Round(g.Width * scale), (int)Math.Round(g.Height * scale));
        }
        return null;
    }

    private static List<PxRect> WorkAreas() =>
        Forms.Screen.AllScreens
            .Select(s => new PxRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
            .ToList();

    private class WindowGeometry
    {
        // 物理ピクセル(新形式)。
        public int? PxX { get; set; }
        public int? PxY { get; set; }
        public int? PxWidth { get; set; }
        public int? PxHeight { get; set; }

        // DIP(旧形式。旧バージョンとの互換のために書き続ける)。
        public double Width { get; set; }
        public double Height { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }

        public bool Maximized { get; set; }
        public double? SidebarWidth { get; set; }
        public string? LastActiveId { get; set; }
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace YmbThatuation;

/// <summary>
/// Interaction logic for App.xaml
/// 二重起動防止(Tauri版のtauri-plugin-single-instance相当)。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "jp.yumebi.thatuation-cs.single-instance";
    private const string WindowTitle = "YMB Thatuation";
    private const int SW_RESTORE = 9;

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var createdNew = false;
        for (var i = 0; i < 30; i++)
        {
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (createdNew) break;

            // restart_app直後は旧プロセスがMutex解放中の可能性があるため、少し待って再試行する。
            _mutex.Dispose();
            Thread.Sleep(200);
        }

        if (!createdNew)
        {
            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            // この時点ではDispatcherのRun()が始まっていないため、Shutdown()では
            // プロセスが終了せず、ウインドウの無いプロセスが残ってしまう。
            // Environment.Exit()で確実に終了させる。
            Environment.Exit(0);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        base.OnStartup(e);
    }

    /// <summary>
    /// 新規ウインドウ(WebView2既定ポップアップ)の鍵アイコン操作等、UIスレッドで発生する
    /// 未処理例外で本体全体が落ちるのを防ぐ。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("unhandled", e.Exception.ToString());
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteLog("fatal", e.ExceptionObject.ToString() ?? "(null)");
    }

    /// <summary>
    /// デバッガ無しのリリース実行時はDebug.WriteLineが見えないため、未処理例外は
    /// %APPDATA%\jp.yumebi.thatuation-cs\logs\crash.log にも残す。
    /// </summary>
    private static void WriteLog(string kind, string detail)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "jp.yumebi.thatuation-cs", "logs");
            Directory.CreateDirectory(logDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{kind}] {detail}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "crash.log"), line);
        }
        catch
        {
            // ログ出力自体の失敗で本処理に影響を与えない。
        }
        System.Diagnostics.Debug.WriteLine($"[{kind}] {detail}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

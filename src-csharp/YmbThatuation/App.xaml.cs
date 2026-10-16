using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using YmbThatuation.Services;

namespace YmbThatuation;

/// <summary>
/// Interaction logic for App.xaml
/// 二重起動防止(Tauri版のtauri-plugin-single-instance相当)。
/// </summary>
public partial class App : System.Windows.Application
{
    // 保存先を環境変数で切り替えている場合(動作確認用)は、本番の起動と二重起動扱いにならないよう名前を分ける。
    private static readonly string InstanceSuffix = DataDirSuffix();
    private static readonly string MutexName = "jp.yumebi.thatuation-cs.single-instance" + InstanceSuffix;
    private static readonly string ActivateEventName = "jp.yumebi.thatuation-cs.activate" + InstanceSuffix;
    private const string WindowTitle = "YMB Thatuation";
    private const int SW_RESTORE = 9;

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    private static string DataDirSuffix()
    {
        var dir = Environment.GetEnvironmentVariable(ConfigStore.DataDirEnvVar);
        if (string.IsNullOrWhiteSpace(dir)) return "";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return "." + Convert.ToHexString(hash)[..12];
    }

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
            // 先に起動している本体へ「前面に出して」と知らせる。本体側(WPF)の状態と食い違わず、
            // 最大化も維持される。タイトルでウインドウを探す方法より確実。
            if (!SignalFirstInstance())
            {
                // 通知の仕組みを持たない旧バージョンが動いている場合は、従来の方法で前面に出す。
                var hwnd = FindWindow(null, WindowTitle);
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
            }

            // この時点ではDispatcherのRun()が始まっていないため、Shutdown()では
            // プロセスが終了せず、ウインドウの無いプロセスが残ってしまう。
            // Environment.Exit()で確実に終了させる。
            Environment.Exit(0);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        base.OnStartup(e);

        StartActivationListener();

        // StartupUriではなくコードで表示する。位置の復元や最小化起動の判断を、表示の前に
        // MainWindowの中で済ませるため。
        var window = new YmbThatuation.MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>2つ目の起動から「前面に出して」と通知を受ける待ち受けを始める。</summary>
    private void StartActivationListener()
    {
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateEvent = activateEvent;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    activateEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Dispatcher.BeginInvoke(() => (MainWindow as YmbThatuation.MainWindow)?.RestoreFromTray());
            }
        })
        {
            IsBackground = true,
            Name = "activate-listener",
        };
        thread.Start();
    }

    private static bool SignalFirstInstance()
    {
        try
        {
            // 2つ目のプロセスはユーザー操作で起動された直後なので、前面化の権限を持っている。
            // それを先に起動していた本体にも許可して、前面に出られるようにする。
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
            return handle.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
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

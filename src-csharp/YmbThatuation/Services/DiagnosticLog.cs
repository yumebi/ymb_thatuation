using System.IO;

namespace YmbThatuation.Services;

/// <summary>
/// 着信音が鳴らない問題の原因を調べるための診断ログ。
/// %APPDATA%\jp.yumebi.thatuation-cs\logs\diagnostic.log に追記する。
/// 大きくなりすぎないよう、上限に達したら diagnostic.log.old に切り替える(古い .old は上書き)。
/// 書き込みの失敗で本処理に影響を与えない。
/// </summary>
public sealed class DiagnosticLog
{
    public static DiagnosticLog Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "jp.yumebi.thatuation-cs", "logs", "diagnostic.log"),
        2 * 1024 * 1024);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _lock = new();

    public DiagnosticLog(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    public void Write(string kind, string detail)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes)
                {
                    File.Move(_path, _path + ".old", true);
                }
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{kind}] {detail}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // ログ出力自体の失敗で本処理に影響を与えない。
        }
    }

    /// <summary>ログに載せる文字列を、改行を除いて、指定の長さに切り詰める。</summary>
    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}

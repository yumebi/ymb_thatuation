using System.IO;

namespace YmbThatuation.Services;

public static class PathUtil
{
    private const string DownloadsFolderGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary>
    /// OSのダウンロードフォルダを取得する。OneDriveでのリダイレクト等、ユーザーが既定の
    /// 「ダウンロード」フォルダを変更している場合も反映するため、Environment.SpecialFolderには
    /// 無いこのフォルダをシェルフォルダレジストリから取得する。
    /// 各サービスWebViewのDefaultDownloadFolderPathと「ダウンロードフォルダを開く」ボタンの
    /// 両方でこの関数を使うことで、実際の保存先と開くフォルダを一致させる。
    /// </summary>
    public static string GetDownloadsFolder()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
            if (key?.GetValue(DownloadsFolderGuid) is string path && !string.IsNullOrEmpty(path))
            {
                return Environment.ExpandEnvironmentVariables(path);
            }
        }
        catch
        {
            // レジストリ取得に失敗した場合は既定値にフォールバックする。
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}

namespace YmbThatuation.Services;

/// <summary>
/// インスタンス表示対象としてWebViewに読み込んでもよいURLの判定。
/// 設定JSON(import_settings)やJS侧的实例URLを素通しでWebViewにNavigateすると、
/// file:// や独自スキーム etc. でローカル資源/外部ハンドラを開けてしまうため、
/// http/https 以外はここで拒否する。
/// </summary>
public static class SecurityLimits
{
    /// <summary>
    /// インスタンスのURLとして安全か。http/https のみ許可し、
    /// 埋め込み資格情報(userinfo)を持つURLも拒否する。
    /// </summary>
    public static bool IsAllowedInstanceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;

        return true;
    }
}

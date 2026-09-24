namespace LivePhotoConvert.Core.External.Tools;

/// <summary>
/// 下载地址变换：加速前缀只改变取数路径，校验值始终取自清单，因此换镜像不会削弱完整性保证。
/// </summary>
public static class ToolDownloadUrls
{
    public const string GithubPrefix = "https://github.com/";

    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>规范化加速前缀；空白、无效或等同直连时返回 null。</summary>
    public static string? NormalizeMirrorPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !IsHttpUrl(prefix))
        {
            return null;
        }

        var normalized = prefix.Trim();
        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized.Equals(GithubPrefix, StringComparison.OrdinalIgnoreCase) ? null : normalized;
    }

    /// <summary>
    /// 给 GitHub 地址套上加速前缀；原地址若已带其他代理前缀会先剥离，避免多层代理。
    /// </summary>
    public static string ApplyMirror(string githubUrl, string? mirrorPrefix)
    {
        var index = githubUrl.IndexOf(GithubPrefix, StringComparison.OrdinalIgnoreCase);
        var raw = index >= 0 ? githubUrl[index..] : githubUrl;
        var prefix = NormalizeMirrorPrefix(mirrorPrefix);
        return prefix is null ? raw : prefix + raw;
    }
}

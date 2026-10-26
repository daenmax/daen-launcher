using System.Text.RegularExpressions;

namespace DaenLauncher.Services;

/// <summary>
/// 网址类项目的地址分类与规范化（统一维护，避免各处硬编码判断）。
/// 需求：网址项目除 http/https 外，还支持 ftp、mailto:、tel:、sms: 等格式。
/// </summary>
public static class LauncherItemPathKinds
{
    /// <summary>能抓取站点图标（favicon）的协议</summary>
    private static readonly string[] FaviconSchemes = ["http", "https", "ftp", "ftps"];

    /// <summary>"网址"类型允许的协议（协议类型 = 其余任意 scheme:）</summary>
    private static readonly string[] UrlSchemes = ["http", "https", "ftp", "ftps", "mailto", "tel", "sms"];

    /// <summary>scheme 格式：字母开头，可含数字/+/-/.</summary>
    private static readonly Regex SchemeRegex =
        new(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):", RegexOptions.Compiled);

    /// <summary>不带协议的裸域名（如 example.com、www.example.com:8080/path）</summary>
    private static readonly Regex BareDomainRegex = new(
        @"^(?:localhost|(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,})(?::\d{1,5})?(?:[/?#].*)?$",
        RegexOptions.Compiled);

    /// <summary>取地址的 scheme（小写）；没有 scheme 返回空字符串</summary>
    public static string GetScheme(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var match = SchemeRegex.Match(path.Trim());
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
    }

    /// <summary>是否可以用 favicon 抓取（http/https/ftp/ftps 且有主机名）</summary>
    public static bool SupportsFavicon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var scheme = GetScheme(path);
        if (!FaviconSchemes.Contains(scheme)) return false;
        return Uri.TryCreate(path.Trim(), UriKind.Absolute, out var uri) &&
               !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>是否属于"网址"类型（http/https/ftp/mailto/tel/sms）</summary>
    public static bool IsUrlLike(string? path) => UrlSchemes.Contains(GetScheme(path));

    /// <summary>
    /// 规范化用户输入的网址：裸域名补 https://，去掉首尾空白（mailto: 等保持原样）。
    /// </summary>
    public static string NormalizeUrlInput(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (GetScheme(trimmed).Length > 0) return trimmed;
        // 没有协议：裸域名（含 localhost）补 https://，其他保持原样交给校验
        return BareDomainRegex.IsMatch(trimmed) ? "https://" + trimmed : trimmed;
    }

    /// <summary>
    /// 校验"网址"类型的输入是否合法（需求：支持 mailto: 等更多格式）。
    /// </summary>
    public static bool IsValidUrlInput(string text)
    {
        var normalized = NormalizeUrlInput(text);
        if (normalized.Length == 0) return false;

        var scheme = GetScheme(normalized);
        if (scheme.Length == 0) return false;

        // mailto: / tel: / sms: 只要求冒号后有内容（可带多个收件人、主题等）
        if (scheme is "mailto" or "tel" or "sms")
        {
            return normalized.Length > scheme.Length + 1;
        }

        // 网络地址：必须是绝对地址且有主机名
        return scheme is "http" or "https" or "ftp" or "ftps" &&
               Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>
    /// 校验"协议"类型的输入（scheme: 形式，且不是网址类协议，
    /// 避免同一个地址既能当网址又能当协议）。
    /// </summary>
    public static bool IsValidProtocolInput(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        var scheme = GetScheme(trimmed);
        if (scheme.Length == 0) return false;
        return !UrlSchemes.Contains(scheme);
    }
}

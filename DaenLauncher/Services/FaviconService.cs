using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DaenLauncher.Services;

/// <summary>
/// 网址图标（favicon）下载服务（第五十二轮需求：网址类型项目自动联网取图标）。
/// 抓取策略（按顺序尝试，任一成功即返回 PNG 字节）：
/// ① https://主机/favicon.ico　② http://主机/favicon.ico（部分站点只有 http）
/// ③ 解析首页 HTML 里的 &lt;link rel="icon"&gt; 声明再下载（图标不在默认位置的站点）。
/// 所有请求都带超时（默认 8 秒），失败返回 null（界面显示占位图标，下次启动重试）。
/// </summary>
public static class FaviconService
{
    /// <summary>单次请求超时（秒）。网址可能多步尝试，总耗时 = 步数 × 超时</summary>
    private const int RequestTimeoutSeconds = 8;

    /// <summary>重编码后的图标最大边长（favicon 有时是几百像素的大图，压到 128 足够界面用）</summary>
    private const int MaxIconSize = 128;

    /// <summary>请求 UA：部分网站拒绝默认的 HttpClient UA</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>HttpClient（统一走代理配置，需求；ProxyService 共享连接池，配置变化立即生效）</summary>
    private static HttpClient Http => ProxyService.CreateHttpClient(TimeSpan.FromSeconds(RequestTimeoutSeconds));

    /// <summary>
    /// 下载网址的图标并重编码为 PNG 字节。失败返回 null。
    /// </summary>
    public static async Task<byte[]?> DownloadPngAsync(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            // 规范化为绝对地址（用户可能只填了 www.example.com）
            var normalized = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : "https://" + url.TrimStart('/');
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri)) return null;

            // ①/② 默认位置：https 优先，失败退 http
            var (png, reachable) = await TryDownloadAsync($"{baseUri.Scheme}://{baseUri.Authority}/favicon.ico");
            if (png == null && reachable)
            {
                (png, reachable) = await TryDownloadAsync($"http://{baseUri.Authority}/favicon.ico");
            }

            // ③ 解析首页 HTML 的 <link rel="icon"> 声明（站点可达才有意义，连不上就直接放弃）
            if (png == null && reachable)
            {
                (png, _) = await TryDownloadFromHtmlAsync(baseUri);
            }
            return png;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载一个 URL 并解码成 PNG 字节。reachable = 站点是否可达
    /// （连不上时调用方可以直接放弃后续尝试，省掉无意义的超时等待）</summary>
    private static async Task<(byte[]? Png, bool Reachable)> TryDownloadAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, true);

            var bytes = await response.Content.ReadAsByteArrayAsync();
            return (EncodeToPng(bytes), true);
        }
        catch
        {
            // 超时/连接失败
            return (null, false);
        }
    }

    /// <summary>下载首页 HTML，解析 &lt;link rel="icon"&gt; 的地址并下载图标</summary>
    private static async Task<(byte[]? Png, bool Reachable)> TryDownloadFromHtmlAsync(Uri baseUri)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, true);

            // 只读前 256KB，够找到 <link> 声明（避免超大页面浪费流量）
            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[256 * 1024];
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            var html = new string(buffer, 0, read);

            var iconUrl = FindIconLinkUrl(html, baseUri);
            if (iconUrl == null) return (null, true);
            return await TryDownloadAsync(iconUrl);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>从 HTML 里找图标声明，解析成绝对 URL；找不到返回 null。
    /// 优先 apple-touch-icon（通常尺寸最大最好看），其次任意 rel 含 icon 的 link。</summary>
    private static string? FindIconLinkUrl(string html, Uri baseUri)
    {
        // 收集所有 <link> 标签里 rel 含 icon 的 href
        var candidates = new List<string>();
        foreach (System.Text.RegularExpressions.Match tag in
                 System.Text.RegularExpressions.Regex.Matches(
                     html, @"<link\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var tagText = tag.Value;
            var rel = GetAttribute(tagText, "rel");
            if (rel == null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;

            var href = GetAttribute(tagText, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            // 相对地址转绝对地址（/favicon.ico、//cdn.xxx/a.png、icon.png 等）
            if (Uri.TryCreate(baseUri, href.Trim(), out var absolute))
            {
                candidates.Add(absolute.ToString());
                // apple-touch-icon 一般是最大尺寸的高清图，找到就直接用
                if (rel.Contains("apple-touch", StringComparison.OrdinalIgnoreCase))
                {
                    return absolute.ToString();
                }
            }
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>从标签文本里取属性值（href="..." / href='...' / href=...）</summary>
    private static string? GetAttribute(string tag, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            tag, $@"{name}\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[2].Success ? match.Groups[2].Value
             : match.Groups[3].Success ? match.Groups[3].Value
             : match.Groups[4].Value;
    }

    /// <summary>把下载到的字节解码成图片并重编码为 PNG；超过 MaxIconSize 的缩小。
    /// .ico 文件用 Icon 类选最接近 32px 的帧，其他格式走 GDI+ 通用解码。</summary>
    private static byte[]? EncodeToPng(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 8) return null;

            using var bitmap = IsIcoFile(bytes) ? DecodeIco(bytes) : DecodeImage(bytes);
            if (bitmap == null) return null;

            // 大图缩小到 MaxIconSize 以内（等比）
            var width = bitmap.Width;
            var height = bitmap.Height;
            var scale = Math.Min(1.0, (double)MaxIconSize / Math.Max(width, height));
            if (scale < 1.0)
            {
                width = Math.Max(1, (int)(width * scale));
                height = Math.Max(1, (int)(height * scale));
            }

            // 画到新位图再存 PNG（顺带把非 32bpp 格式统一掉）
            using var target = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(bitmap, 0, 0, width, height);
            }
            using var ms = new MemoryStream();
            target.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            // 下载到的不是有效图片（比如错误页 HTML）
            return null;
        }
    }

    /// <summary>ICO 文件头：00 00 01 00（保留字 0 + 类型 1=图标）</summary>
    private static bool IsIcoFile(byte[] bytes)
    {
        return bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;
    }

    /// <summary>解码 ICO：选最接近 32px 的帧（GDI+ 默认取第一帧，常常只有 16px 太糊）</summary>
    private static Bitmap? DecodeIco(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var icon = new Icon(ms, 32, 32);
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解码 PNG/JPG/WebP 等常规格式（GDI+ 支持的都行）</summary>
    private static Bitmap? DecodeImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var image = Image.FromStream(ms);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }
}

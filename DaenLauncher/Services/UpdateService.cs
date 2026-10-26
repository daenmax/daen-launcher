using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>一个发布版本（标签 + 更新说明 + 下载直链）</summary>
public sealed class ReleaseInfo
{
    /// <summary>版本标签（保留 v 前缀，如 v1.6.6）</summary>
    public string Tag { get; set; } = "";

    /// <summary>更新说明（Markdown/HTML 原文）</summary>
    public string Body { get; set; } = "";

    /// <summary>exe 下载直链</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>去掉 v 前缀的版本号文本</summary>
    public string VersionText => Tag.TrimStart('v', 'V');
}

/// <summary>检查更新的结果</summary>
public sealed class UpdateCheckResult
{
    /// <summary>检查是否成功（失败时 ErrorMessage 有原因）</summary>
    public bool Success { get; set; }

    /// <summary>失败原因（网络错误等）</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>版本列表（按版本号从新到旧排序，最多 10 条）</summary>
    public List<ReleaseInfo> Releases { get; set; } = new();

    /// <summary>最新版本（没有数据时为 null）</summary>
    public ReleaseInfo? Latest => Releases.Count > 0 ? Releases[0] : null;

    /// <summary>是否存在比当前版本更新的版本</summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    /// 需要展示的更新记录：只保留比当前版本新的版本（从新到旧）。
    /// 说明：需求原话是"10 条里找不到当前版本就展示全部 10 条"，但那会把比当前版本更老的
    /// 记录也列出来（镜像缓存导致列表里没有当前版本时尤其明显）。按版本号过滤能同时满足
    /// 两种情况：当前版本在列表里 → 只显示它之后的；当前版本比这 10 条都老 → 10 条全显示。
    /// </summary>
    public List<ReleaseInfo> GetUpdatesSinceCurrent()
    {
        var current = UpdateService.ParseVersion(AppInfoService.Version);
        var newer = Releases
            .Where(r => UpdateService.ParseVersion(r.VersionText) > current)
            .ToList();
        return newer.Count > 0 ? newer : Releases.ToList();
    }
}

/// <summary>更新完成后重启的类型（决定重启后托盘提示的文案）</summary>
public enum UpdateApplyKind
{
    /// <summary>用户在"关于"页手动更新</summary>
    Manual,

    /// <summary>启动时自动检查 + 自动下载更新</summary>
    Auto
}

/// <summary>
/// 应用更新服务（需求-应用更新）：
/// - 检查：两个渠道（GitHub 原始 / GitHub 代理），都走设置里的代理配置；
/// - 下载：静默下载到临时目录，带进度（关于页弹窗用）；
/// - 应用：写一个等待进程退出的批处理，复制新 exe、重新启动、删除自己；
/// - 重启后提示：更新前写 data\update\pending.json，新版本启动时读它弹托盘提示并删除。
/// 单文件发布下 Environment.ProcessPath 就是真实 exe 路径，可直接自替换。
/// </summary>
public sealed class UpdateService
{
    /// <summary>全局单例</summary>
    public static UpdateService Instance { get; } = new();

    /// <summary>GitHub 仓库（owner/repo）</summary>
    private const string GitHubRepository = "daenmax/daen-launcher";

    /// <summary>GitHub 代理前缀（需求：更新渠道 = GitHub 代理）</summary>
    private const string GitHubProxyPrefix = "https://gh-proxy.org/";

    /// <summary>GitHub API 发布列表地址（最多返回 30 条，我们只取前 10 条）</summary>
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/" + GitHubRepository + "/releases";

    /// <summary>GitHub 发布页（"原始"渠道解析版本用；API 不可用时兜底）</summary>
    private const string ReleasesPageUrl =
        "https://github.com/" + GitHubRepository + "/releases";

    /// <summary>GitHub 发布 Atom 订阅（含更新说明，原始渠道优先用它）</summary>
    private const string ReleasesAtomUrl =
        "https://github.com/" + GitHubRepository + "/releases.atom";

    /// <summary>下载 exe 的文件名（发布里固定这个名字）</summary>
    private const string AssetFileName = "DaenLauncher.exe";

    /// <summary>只取最近多少个版本（需求）</summary>
    private const int MaxReleaseCount = 10;

    /// <summary>检查更新请求超时</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>下载超时（大文件可能很慢，用较宽松的上限）</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>更新标记文件（重启后据此弹"更新完成"提示）</summary>
    private static string PendingMarkerPath => Path.Combine(DataPathService.UpdateDir, "pending.json");

    /// <summary>请求 JSON 时的 UA（GitHub API 要求带 UA）</summary>
    private const string UserAgent = "DaenLauncher-Updater";

    // ===== 下载状态（需求：静默下载时打开"关于"页也要能看到进度）=====

    /// <summary>是否正在下载更新（后台静默下载 / 关于页手动下载都算）</summary>
    public bool IsDownloading { get; private set; }

    /// <summary>当前下载进度（0~1；-1 = 文件总大小未知，界面显示不确定进度条）</summary>
    public double DownloadProgress { get; private set; } = -1;

    /// <summary>正在下载的目标版本（如 1.6.9）</summary>
    public string DownloadingVersion { get; private set; } = "";

    /// <summary>下载状态变化时触发（进度/开始/结束）。可能在后台线程触发，订阅方自行切回 UI 线程。</summary>
    public event Action? DownloadStateChanged;

    /// <summary>更新下载状态并通知订阅者（任何线程调用）</summary>
    private void PublishDownloadState(bool downloading, double progress, string version)
    {
        IsDownloading = downloading;
        DownloadProgress = progress;
        DownloadingVersion = version;
        DownloadStateChanged?.Invoke();
    }

    private UpdateService()
    {
    }

    #region 检查更新

    /// <summary>
    /// 检查新版本（按设置的更新渠道 + 代理配置）。任何异常都转成 Success=false，不抛出。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        var channel = SettingsService.Instance.Settings.UpdateChannel;
        try
        {
            List<ReleaseInfo> releases;
            var source = "";
            if (channel == UpdateChannel.GitHubProxy)
            {
                // 代理渠道：优先用需求指定的 GitHub API（经 gh-proxy 中转）。
                // GitHub API 对匿名请求有速率限制（代理出口是共享 IP，容易被限流），
                // 失败/被限流时退回"客户端自己配置的代理直连 API"，最后再试 Atom。
                releases = await TryFetchAsync(() => FetchFromApiAsync(
                    GitHubProxyPrefix + ReleasesApiUrl, useProxyDownloadPrefix: true));
                if (releases.Count > 0) source = "gh-proxy-api";
                if (releases.Count == 0)
                {
                    // 代理接口偶尔被 GitHub 限流：再用客户端自己配置的代理直连 API 试一次
                    releases = await TryFetchAsync(() => FetchFromApiAsync(ReleasesApiUrl, useProxyDownloadPrefix: true));
                    if (releases.Count > 0) source = "direct-api";
                }
                if (releases.Count == 0)
                {
                    // 最后退回 Atom（部分网络下 GitHub 网页可直连）
                    releases = await TryFetchAsync(() => FetchFromAtomAsync(ReleasesAtomUrl));
                    if (releases.Count > 0) source = "direct-atom";
                }
            }
            else
            {
                // 原始渠道：优先 Atom（含更新说明），失败再退回网页/API
                releases = await TryFetchAsync(() => FetchFromAtomAsync(ReleasesAtomUrl));
                if (releases.Count > 0) source = "atom";
                if (releases.Count == 0)
                {
                    releases = await TryFetchAsync(() => FetchFromReleasesPageAsync(ReleasesPageUrl));
                    if (releases.Count > 0) source = "releases-page";
                }
                if (releases.Count == 0)
                {
                    releases = await TryFetchAsync(() => FetchFromApiAsync(ReleasesApiUrl, useProxyDownloadPrefix: false));
                    if (releases.Count > 0) source = "api";
                }
            }

            // 所有来源都拿不到：按"检查失败"处理（不能当成已是最新版本）
            if (releases.Count == 0)
            {
                WriteCheckLog(channel, "none", releases);
                return new UpdateCheckResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Tr("About.Update.NetworkError")
                };
            }

            // 说明缺失时补一次（见方法注释：镜像缓存旧响应 / 网页兜底没有说明）
            await FillMissingBodiesAsync(releases);

            // 代理渠道：下载直链也统一走代理（直连 github.com 下载在国内经常失败）
            if (channel == UpdateChannel.GitHubProxy)
            {
                foreach (var release in releases)
                {
                    release.DownloadUrl = WithProxyPrefix(release.DownloadUrl);
                }
            }

            // 按版本号从新到旧排序，只保留前 10 条
            releases = releases
                .Where(r => ParseVersion(r.VersionText) > new Version(0, 0, 0))
                .OrderByDescending(r => ParseVersion(r.VersionText))
                .Take(MaxReleaseCount)
                .ToList();

            var current = ParseVersion(AppInfoService.Version);
            var latest = releases.Count > 0 ? ParseVersion(releases[0].VersionText) : null;
            WriteCheckLog(channel, source, releases);
            return new UpdateCheckResult
            {
                Success = true,
                Releases = releases,
                HasUpdate = latest != null && latest > current
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 给缺更新说明的版本补内容（只针对"比当前版本新"的版本，避免每次检查都多一次请求）。
    /// 触发场景：
    /// ① gh-proxy 缓存了发布早期的响应，那时 release 还没填更新说明；
    /// ② 走了 Releases 网页兜底（旧实现只解析版本号）。
    /// 补全来源：直连 GitHub 的 Atom 订阅（含完整说明）。
    /// </summary>
    private static async Task FillMissingBodiesAsync(List<ReleaseInfo> releases)
    {
        var current = ParseVersion(AppInfoService.Version);
        var newer = releases.Where(r => ParseVersion(r.VersionText) > current).ToList();
        if (newer.Count == 0 || newer.All(r => !string.IsNullOrWhiteSpace(r.Body)))
        {
            return;
        }

        var atom = await TryFetchAsync(() => FetchFromAtomAsync(ReleasesAtomUrl, TimeSpan.FromSeconds(10)));
        if (atom.Count == 0) return;

        foreach (var release in newer)
        {
            if (!string.IsNullOrWhiteSpace(release.Body)) continue;
            var match = atom.FirstOrDefault(a =>
                ParseVersion(a.VersionText) == ParseVersion(release.VersionText));
            if (match != null && !string.IsNullOrWhiteSpace(match.Body))
            {
                release.Body = match.Body;
            }
        }
    }

    /// <summary>
    /// 记录最近一次检查的来源和各版本说明长度（data\update\last-check.log）。
    /// 更新弹窗"只有版本号没有内容"时，用它快速判断是哪个来源、body 是否为空。
    /// </summary>
    private static void WriteCheckLog(UpdateChannel channel, string source, List<ReleaseInfo> releases)
    {
        try
        {
            Directory.CreateDirectory(DataPathService.UpdateDir);
            var builder = new StringBuilder();
            builder.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} channel={channel} source={source} current={AppInfoService.Version}");
            foreach (var release in releases)
            {
                builder.AppendLine($"  {release.Tag} bodyLen={(release.Body ?? "").Length} url={release.DownloadUrl}");
            }
            File.WriteAllText(Path.Combine(DataPathService.UpdateDir, "last-check.log"),
                builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 日志失败不影响检查
        }
    }

    /// <summary>执行一次抓取，异常时返回空列表（让检查流程可以继续尝试下一个来源）</summary>
    private static async Task<List<ReleaseInfo>> TryFetchAsync(Func<Task<List<ReleaseInfo>>> fetch)
    {
        try
        {
            return await fetch();
        }
        catch
        {
            return new List<ReleaseInfo>();
        }
    }

    /// <summary>通过 GitHub API 取发布列表（JSON）</summary>
    /// <param name="url">API 地址（代理渠道带代理前缀）</param>
    /// <param name="useProxyDownloadPrefix">下载直链是否也加代理前缀（国内直连 GitHub 下载很慢/被墙）</param>
    private static async Task<List<ReleaseInfo>> FetchFromApiAsync(string url, bool useProxyDownloadPrefix)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var client = ProxyService.CreateHttpClient(CheckTimeout);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<ReleaseInfo>();

        var result = new List<ReleaseInfo>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var tag = element.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var body = element.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";

            // 下载直链：优先取第一个资产（需求），没有资产时按固定规则拼
            var downloadUrl = "";
            if (element.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array &&
                assets.GetArrayLength() > 0)
            {
                var first = assets[0];
                if (first.TryGetProperty("browser_download_url", out var assetUrl))
                {
                    downloadUrl = assetUrl.GetString() ?? "";
                }
            }
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = BuildDownloadUrl(tag);
            }
            if (useProxyDownloadPrefix)
            {
                downloadUrl = WithProxyPrefix(downloadUrl);
            }

            result.Add(new ReleaseInfo { Tag = tag, Body = body, DownloadUrl = downloadUrl });
        }
        return result;
    }

    /// <summary>原始渠道：解析 GitHub Releases 的 Atom 订阅（标题 = 版本，content = 更新说明）</summary>
    /// <param name="url">订阅地址（代理渠道带代理前缀）</param>
    /// <param name="timeout">超时（补全说明时用较短的超时，避免拖慢检查）</param>
    private static async Task<List<ReleaseInfo>> FetchFromAtomAsync(string url, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var client = ProxyService.CreateHttpClient(timeout ?? CheckTimeout);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";

        var result = new List<ReleaseInfo>();
        foreach (var entry in doc.Descendants(atom + "entry"))
        {
            var tag = entry.Element(atom + "title")?.Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var body = entry.Element(atom + "content")?.Value ?? "";
            result.Add(new ReleaseInfo
            {
                Tag = tag,
                Body = body,
                DownloadUrl = BuildDownloadUrl(tag)
            });
        }
        return result;
    }

    /// <summary>
    /// 原始渠道兜底：从 GitHub 发布网页里抓版本标签和更新说明。
    /// 说明块是页面里 class 含 markdown-body 的 div，按"标签之后、下一个标签之前"归属到对应版本。
    /// </summary>
    private static async Task<List<ReleaseInfo>> FetchFromReleasesPageAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var client = ProxyService.CreateHttpClient(CheckTimeout);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // 发布页里每个版本都有 /releases/tag/<tag> 链接（按页面顺序 = 从新到旧），取每个标签第一次出现的位置
        var tags = Regex.Matches(html, @"/releases/tag/([^""'<>\s/]+)")
            .Cast<Match>()
            .Select(m => (Tag: Uri.UnescapeDataString(m.Groups[1].Value), Index: m.Index))
            .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Index)
            .Take(MaxReleaseCount)
            .ToList();

        // 更新说明块（同一页里按顺序排列，每个版本一个）
        var bodies = Regex.Matches(html,
                @"<div\b[^>]*class=""[^""]*\bmarkdown-body\b[^""]*""[^>]*>(.*?)</div>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Select(m => (Html: m.Groups[1].Value, Index: m.Index))
            .OrderBy(x => x.Index)
            .ToList();

        var result = new List<ReleaseInfo>();
        for (var i = 0; i < tags.Count; i++)
        {
            var start = tags[i].Index;
            var end = i + 1 < tags.Count ? tags[i + 1].Index : int.MaxValue;
            var body = bodies.FirstOrDefault(b => b.Index > start && b.Index < end).Html ?? "";
            result.Add(new ReleaseInfo
            {
                Tag = tags[i].Tag,
                Body = body,
                DownloadUrl = BuildDownloadUrl(tags[i].Tag)
            });
        }
        return result;
    }

    /// <summary>按需求规则拼下载直链（release 里固定上传 DaenLauncher.exe）</summary>
    private static string BuildDownloadUrl(string tag) =>
        $"https://github.com/{GitHubRepository}/releases/download/{tag}/{AssetFileName}";

    /// <summary>给 github.com 的下载直链加代理前缀（已经是代理地址则原样返回）</summary>
    private static string WithProxyPrefix(string url) =>
        url.StartsWith(GitHubProxyPrefix, StringComparison.OrdinalIgnoreCase)
            ? url
            : GitHubProxyPrefix + url;

    /// <summary>
    /// 解析版本号（"v1.6.6" / "1.6.6-beta" / "release-1.6.6" → 1.6.6）。
    /// 解析失败返回 0.0.0（排序时会沉底）。
    /// </summary>
    public static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Version(0, 0, 0);
        var match = Regex.Match(text, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return new Version(0, 0, 0);
        var major = int.Parse(match.Groups[1].Value);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        return new Version(major, minor, build, revision);
    }

    #endregion

    #region 更新说明纯文本化

    /// <summary>
    /// 把 Markdown / HTML 更新说明转成纯文本（弹窗只展示纯文本，需求）。
    /// 处理：HTML 标签、图片、链接、标题符号、加粗/斜体、代码块、引用、分隔线；
    /// 重点处理 Atom 返回的 HTML（GitHub 会把 "1. xxx" 的纯文本渲染成 &lt;ol&gt;&lt;li&gt;）：
    /// - 标签之间的源码换行要先压掉，否则每一项后面会多出一个空行；
    /// - 有序列表要还原成 "1. "、"2. " 编号（直接删 &lt;li&gt; 会像"序号丢了"）。
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // HTML 标签之间的格式换行（<li>a</li>\n<li>b</li>、</p>\n<p>）不是正文内容，先压掉；
        // <br> 后面的源码换行同理（否则 <br> + 源码换行 = 两个换行 = 空行）
        text = Regex.Replace(text, @">\s*\n\s*<", "><");
        text = Regex.Replace(text, @"<br\s*/?>\s*\n\s*", "<br>", RegexOptions.IgnoreCase);

        // 代码块：去掉围栏，保留内容
        text = Regex.Replace(text, @"```[^\n]*\n?", "");
        // HTML 图片整体去掉
        text = Regex.Replace(text, @"<img\b[^>]*>", "", RegexOptions.IgnoreCase);

        // 有序/无序列表：<li> 还原成 "1. " / "• "
        text = Regex.Replace(text, @"<ol\b[^>]*>(.*?)</ol>", match =>
        {
            var index = 0;
            return Regex.Replace(match.Groups[1].Value, @"<li\b[^>]*>",
                _ => $"{++index}. ", RegexOptions.IgnoreCase);
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<ul\b[^>]*>(.*?)</ul>",
            match => Regex.Replace(match.Groups[1].Value, @"<li\b[^>]*>", "• ", RegexOptions.IgnoreCase),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // 不在 ol/ul 里的游离 <li>
        text = Regex.Replace(text, @"<li\b[^>]*>", "• ", RegexOptions.IgnoreCase);

        // 换行标签 / 块级标签收尾
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");

        // Markdown 图片 ![alt](url) → 去掉
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        // Markdown 链接 [text](url) → text
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");

        // 行级修饰
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            // 分隔线
            if (Regex.IsMatch(line.Trim(), @"^([-*_]\s*){3,}$"))
            {
                lines.Add("");
                continue;
            }
            // 标题：去掉开头的 #
            line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
            // 引用：去掉开头的 >
            line = Regex.Replace(line, @"^\s*>\s?", "");
            // 列表符号统一成 "• "
            line = Regex.Replace(line, @"^\s*[-*+]\s+", "• ");
            // 去掉行内加粗/斜体/删除线/行内代码标记
            line = line.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "");
            lines.Add(line);
        }

        // 压缩连续空行（最多保留一个空行）
        var builder = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                if (!blank && builder.Length > 0) builder.Append('\n');
                blank = true;
                continue;
            }
            blank = false;
            builder.Append(line.TrimStart()).Append('\n');
        }
        return builder.ToString().Trim();
    }

    /// <summary>构建"当前版本之后"的全部更新记录纯文本（形如 "v1.6.6\n1.xxx\n\nv1.6.5\n..."）</summary>
    public static string BuildReleaseNotesText(UpdateCheckResult result)
    {
        var builder = new StringBuilder();
        foreach (var release in result.GetUpdatesSinceCurrent())
        {
            var tag = release.Tag.StartsWith('v') || release.Tag.StartsWith('V')
                ? release.Tag
                : "v" + release.Tag;
            builder.AppendLine(tag);
            var notes = ToPlainText(release.Body);
            // 某个版本说明为空时明确写出来（否则弹窗里只剩一个版本号，看起来像 bug）
            builder.AppendLine(notes.Length > 0
                ? notes
                : LocalizationService.Tr("About.Update.NoNotes"));
            builder.AppendLine();
        }
        return builder.ToString().Trim();
    }

    #endregion

    #region 下载与应用

    /// <summary>
    /// 下载更新文件到临时目录，返回本地 exe 路径。
    /// 同时维护全局下载状态（IsDownloading/DownloadProgress），后台静默下载时
    /// "设置-关于"页可以实时显示进度（需求）。
    /// </summary>
    /// <param name="progress">进度回调（0~1；未知总大小时不回调）</param>
    /// <param name="token">取消令牌</param>
    /// <param name="targetVersion">目标版本号（仅用于界面显示，如 1.6.9）</param>
    public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken token,
        string targetVersion = "")
    {
        PublishDownloadState(true, -1, targetVersion);
        try
        {
            // 下载专用 client：不限制整体超时（大文件），用 CancellationToken 控制
            using var client = ProxyService.CreateHttpClient(Timeout.InfiniteTimeSpan);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(DownloadTimeout);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            var targetDir = Path.Combine(Path.GetTempPath(), "DaenLauncher", "update");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, AssetFileName + ".new");

            // 全局状态只在进度变化 ≥1% 时发布一次，避免每 80KB 都刷一次界面
            var lastPublished = -1.0;

            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token))
            await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, linked.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    received += read;
                    if (total > 0)
                    {
                        var value = (double)received / total;
                        progress?.Report(value);
                        if (value - lastPublished >= 0.01)
                        {
                            lastPublished = value;
                            PublishDownloadState(true, value, targetVersion);
                        }
                    }
                }
            }

            // 简单校验：必须是一个 PE 可执行文件（避免把错误页 HTML 当成 exe 替换掉自己）
            ValidateDownloadedExe(targetPath);
            PublishDownloadState(false, 1, targetVersion);
            return targetPath;
        }
        catch
        {
            PublishDownloadState(false, -1, targetVersion);
            throw;
        }
    }

    /// <summary>校验下载结果确实是 exe（MZ 文件头）</summary>
    private static void ValidateDownloadedExe(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 1024 * 100)
        {
            throw new InvalidDataException("下载的文件过小，不是有效的程序文件。");
        }
        var header = new byte[2];
        if (stream.Read(header, 0, 2) != 2 || header[0] != 'M' || header[1] != 'Z')
        {
            throw new InvalidDataException("下载的文件不是有效的程序文件。");
        }
    }

    /// <summary>
    /// 用下载好的 exe 替换当前程序并重启。
    /// 做法：写一个批处理，它等当前进程退出 → 复制覆盖 → 启动新程序 → 删除自己。
    /// </summary>
    public void ApplyAndRestart(string downloadedExePath, UpdateApplyKind kind, ReleaseInfo? target)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(downloadedExePath))
        {
            throw new FileNotFoundException("找不到当前程序或下载的更新文件。");
        }

        // 先确认程序所在目录可写：不可写时直接报错，避免"关了软件却起不来"
        var exeDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDir)) throw new UnauthorizedAccessException(
            LocalizationService.Tr("About.Update.DirNotWritable"));
        try
        {
            var probe = Path.Combine(exeDir, ".daen_update_write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            throw new UnauthorizedAccessException(
                LocalizationService.Tr("About.Update.DirNotWritable"));
        }

        // 重启后提示用的标记（新版本启动时读取并删除）
        WritePendingMarker(kind, target);

        // 批处理：等到本进程真正退出再覆盖，否则文件被占用会复制失败
        var scriptPath = Path.Combine(Path.GetDirectoryName(downloadedExePath)!, "apply-update.cmd");
        var processId = Environment.ProcessId;
        var script = string.Join("\r\n", new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            ":wait",
            $"tasklist /nh /fi \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul",
            "if not errorlevel 1 (",
            // 用 ping 做延时：无控制台窗口时 timeout 命令可能直接失败
            "  ping -n 2 127.0.0.1 >nul",
            "  goto wait",
            ")",
            $"copy /y \"{downloadedExePath}\" \"{exePath}\" >nul",
            $"del \"{downloadedExePath}\" >nul",
            $"start \"\" \"{exePath}\"",
            "del \"%~f0\""
        });
        // 不带 BOM：cmd.exe 遇到 UTF-8 BOM 会把第一行当成非法命令输出错误
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        // 隐藏窗口启动批处理（cmd /c 执行，不闪黑框）
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        App.Instance.ExitApplication();
    }

    /// <summary>写重启后提示标记</summary>
    private static void WritePendingMarker(UpdateApplyKind kind, ReleaseInfo? target)
    {
        try
        {
            Directory.CreateDirectory(DataPathService.UpdateDir);
            var marker = new PendingUpdateMarker
            {
                Kind = kind,
                FromVersion = AppInfoService.Version,
                ToVersion = target?.VersionText ?? ""
            };
            File.WriteAllText(PendingMarkerPath,
                JsonSerializer.Serialize(marker), Encoding.UTF8);
        }
        catch
        {
            // 标记写失败不影响更新本身（只是少了重启后的提示）
        }
    }

    /// <summary>
    /// 启动时调用：如果上次是"更新后重启"，弹托盘提示并删除标记。
    /// 版本对不上（说明复制没成功）就只删标记，不误报"更新完成"。
    /// </summary>
    public void ShowPendingResultIfAny()
    {
        try
        {
            if (!File.Exists(PendingMarkerPath)) return;
            var json = File.ReadAllText(PendingMarkerPath);
            File.Delete(PendingMarkerPath);

            var marker = JsonSerializer.Deserialize<PendingUpdateMarker>(json);
            if (marker == null) return;
            if (!string.IsNullOrEmpty(marker.ToVersion) &&
                ParseVersion(marker.ToVersion) != ParseVersion(AppInfoService.Version))
            {
                return;
            }

            var loc = LocalizationService.Instance;
            var title = loc.T("Update.Tray.DoneTitle");
            var message = loc.T(marker.Kind == UpdateApplyKind.Auto
                ? "Update.Tray.AutoDoneMessage"
                : "Update.Tray.DoneMessage");

            // 托盘图标是异步创建的，延迟几秒再弹，避免通知丢失
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                App.Instance.ShowTrayNotification(title, message);
            });
        }
        catch
        {
            // 标记损坏/删除失败不影响运行
        }
    }

    #endregion

    #region 启动时静默检查

    /// <summary>
    /// 启动时的静默检查（需求）：
    /// - 勾选"自动检查版本更新"才执行；
    /// - 没新版本/检查失败：什么都不做；
    /// - 有新版本且勾选"自动下载更新"：静默下载 → 替换 → 重启（不提前提示）；
    /// - 否则：托盘气泡提示"发现新版本"。
    /// </summary>
    public async Task RunStartupCheckAsync()
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            if (!settings.AutoCheckUpdate) return;

            // 稍等几秒，避免和启动时的磁盘/网络初始化抢资源
            await Task.Delay(3000);

            var result = await CheckAsync();
            if (!result.Success || !result.HasUpdate || result.Latest == null) return;

            if (settings.AutoDownloadUpdate)
            {
                try
                {
                    var path = await DownloadAsync(result.Latest.DownloadUrl, null,
                        CancellationToken.None, result.Latest.VersionText);
                    App.Instance.UiDispatcherQueueSafe(() =>
                        ApplyAndRestart(path, UpdateApplyKind.Auto, result.Latest));
                }
                catch
                {
                    // 自动下载失败：静默处理，下次启动再试（需求：失败无事发生）
                }
                return;
            }

            // 只提示，不下载
            var loc = LocalizationService.Instance;
            var title = loc.T("Update.Tray.FoundTitle");
            var message = string.Format(loc.T("Update.Tray.FoundMessage"),
                AppInfoService.Version, result.Latest.VersionText);
            App.Instance.ShowTrayNotification(title, message);
        }
        catch
        {
            // 静默检查任何异常都不打扰用户
        }
    }

    #endregion

    /// <summary>重启后提示标记的数据结构</summary>
    private sealed class PendingUpdateMarker
    {
        public UpdateApplyKind Kind { get; set; } = UpdateApplyKind.Manual;

        /// <summary>更新前的版本（仅记录，便于排查）</summary>
        public string FromVersion { get; set; } = "";

        /// <summary>更新后应该是的版本（版本对不上就不提示）</summary>
        public string ToVersion { get; set; } = "";
    }
}

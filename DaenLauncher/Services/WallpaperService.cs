using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 必应每日壁纸服务：
/// - 从两个数据来源（官方 HPImageArchive / 第三方 biturl）获取今日壁纸元数据；
/// - 按"数据下载"域名 + "壁纸尺寸"后缀拼出图片直链并下载到缓存目录；
/// - 通过 SystemParametersInfo(SPI_SETDESKWALLPAPER) 更换桌面壁纸；
/// - 每天的更换情况记录在 data\wallpaper\record.json（"每日自动更换"据此判断今天是否已换过）；
/// - 勾选"保存壁纸文件到本地"后，每次更换成功都会把文件另存到设置的目录。
/// </summary>
public sealed class WallpaperService
{
    /// <summary>全局单例</summary>
    public static WallpaperService Instance { get; } = new();

    private WallpaperService()
    {
    }

    /// <summary>HttpClient（统一走代理配置，需求；ProxyService 共享连接池，配置变化立即生效）</summary>
    private static HttpClient Http => ProxyService.CreateHttpClient(TimeSpan.FromSeconds(20));

    /// <summary>请求 User-Agent（部分站点对空 UA 拒绝服务）</summary>
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DaenLauncher";

    /// <summary>自动更换失败后的重试次数（开机时网络可能未就绪）</summary>
    private const int AutoChangeMaxAttempts = 3;

    /// <summary>自动更换重试间隔（毫秒）</summary>
    private const int AutoChangeRetryDelayMs = 20_000;

    /// <summary>更换记录存储：data\wallpaper\record.json（复用 JsonStore 的原子写/备份）</summary>
    private readonly JsonStore<WallpaperRecord> _store =
        new(Path.Combine(DataPathService.WallpaperDir, WallpaperConstants.RecordFileName));

    /// <summary>内存中的更换记录（Initialize 时从磁盘加载）</summary>
    private WallpaperRecord _record = new();

    /// <summary>防止"开机自动更换"和窗口手动按钮同时触发时重复下载/更换</summary>
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>当前更换记录（壁纸窗口启动时用于秒显上次结果）</summary>
    public WallpaperRecord Record => _record;

    /// <summary>
    /// 从磁盘重新加载更换记录（数据页删除/导入 wallpaper 数据后调用），
    /// 避免内存里的旧记录之后被写回磁盘。
    /// 注意：这里故意不触发"每日自动更换"（那只在应用启动时做一次）。
    /// </summary>
    public void ReloadRecord()
    {
        _store.Reload();
        _record = _store.Load();
    }

    /// <summary>
    /// 解析"壁纸文件保存目录"的实际路径：设置里留空 = 默认 data\wallpaper\image。
    /// 留空而不直接存默认路径，是为了数据目录回退（LocalAppData）时默认位置仍然正确。
    /// </summary>
    public static string ResolveSaveDir(string configured)
    {
        return string.IsNullOrWhiteSpace(configured)
            ? DataPathService.WallpaperImageDir
            : configured.Trim();
    }

    /// <summary>
    /// 应用启动时调用（App.OnLaunchedCore）：加载更换记录；
    /// 若开启"每日自动更换"且今天还没换过，后台自动获取并更换（需求）。
    /// </summary>
    public void Initialize()
    {
        _store.Reload();
        _record = _store.Load();

        var settings = SettingsService.Instance.Settings;
        if (settings.WallpaperAutoChangeDaily)
        {
            _ = AutoChangeIfNeededAsync();
        }
    }

    /// <summary>
    /// "每日自动更换"：今天已更换过（记录里有今天的日期）就直接跳过；
    /// 否则获取并更换，失败后等待重试（开机时网络往往还没就绪）。
    /// </summary>
    public async Task AutoChangeIfNeededAsync()
    {
        var today = DateTime.Now.ToString(WallpaperConstants.RecordDateFormat, CultureInfo.InvariantCulture);
        if (_record.LastChangeDate == today)
        {
            return; // 今天已经更换过了，不再更换（需求）
        }

        for (var attempt = 1; attempt <= AutoChangeMaxAttempts; attempt++)
        {
            var result = await ApplyWallpaperAsync();
            if (result.Success)
            {
                return;
            }
            if (attempt < AutoChangeMaxAttempts)
            {
                await Task.Delay(AutoChangeRetryDelayMs);
            }
        }
        LogError("AutoChange", "多次重试后仍然失败，本次开机放弃自动更换。");
    }

    /// <summary>
    /// 完整的更换流程：获取元数据 → 下载图片 → 更换桌面壁纸 → 更新记录 → 按需保存到本地。
    /// 手动按钮和开机自动更换共用；内部有信号量防止并发执行。
    /// </summary>
    public async Task<WallpaperApplyResult> ApplyWallpaperAsync()
    {
        await _applyLock.WaitAsync();
        try
        {
            var settings = SettingsService.Instance.Settings;

            // 1. 获取今日壁纸元数据 + 拼直链
            var info = await FetchInfoAsync(settings);

            // 2. 下载到缓存目录（同一天同尺寸已下载过则直接复用）
            var cacheFile = await DownloadImageAsync(info);

            // 3. 按设置的显示模式更换桌面壁纸（拉伸/适应/填充/平铺/居中/跨区）
            SetDesktopWallpaper(cacheFile, settings.WallpaperStyle);

            // 4. 记录今天的更换情况（需求：数据目录里记录每天的更换情况）
            _record.LastChangeDate = DateTime.Now.ToString(
                WallpaperConstants.RecordDateFormat, CultureInfo.InvariantCulture);
            _record.EndDate = info.EndDate;
            _record.Copyright = info.Copyright;
            _record.ImageUrl = info.ImageUrl;
            _record.SizeText = info.SizeText;
            _record.AppliedAt = DateTime.Now.ToString(WallpaperConstants.AppliedAtFormat);
            _store.Save(_record);

            // 5. 按需保存壁纸文件到本地目录（需求；手动/自动更换后都保存）
            if (settings.WallpaperSaveLocal)
            {
                SaveLocalCopy(cacheFile, info, settings);
            }

            // 6. 弹出系统 Toast 通知（需求：显示图片、日期、图片描述）
            ToastService.ShowWallpaperChanged(
                LocalizationService.Tr("Notification.WallpaperChanged"),
                FormatDateForDisplay(info.EndDate),
                info.Copyright,
                cacheFile);

            // 7. 清理缓存目录：只保留刚设置的这张（旧日期/旧尺寸的缓存不再需要）
            CleanupStaleCache(cacheFile);

            return WallpaperApplyResult.Ok(info);
        }
        catch (Exception ex)
        {
            LogError("Apply", ex.ToString());
            return WallpaperApplyResult.Fail(ex.Message);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// 获取今日壁纸元数据（不下载图片、不换壁纸）。
    /// 窗口打开展示数据时用：拿到日期/描述/直链后由窗口自行决定是否下载显示。
    /// </summary>
    public async Task<WallpaperInfo> FetchInfoAsync(AppSettings settings)
    {
        return settings.WallpaperSource == WallpaperConstants.SourceBiturl
            ? await FetchFromBiturlAsync(settings)
            : await FetchFromOfficialAsync(settings);
    }

    /// <summary>下载壁纸图片到缓存目录，返回本地文件路径；已存在则直接复用。</summary>
    public async Task<string> DownloadImageAsync(WallpaperInfo info)
    {
        Directory.CreateDirectory(DataPathService.WallpaperCacheDir);
        var cacheFile = Path.Combine(DataPathService.WallpaperCacheDir, info.FileName);
        if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0)
        {
            return cacheFile; // 同一天同尺寸已下载过，不重复下载
        }

        var bytes = await Http.GetByteArrayAsync(info.ImageUrl);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("下载的壁纸文件为空。");
        }
        await File.WriteAllBytesAsync(cacheFile, bytes);

        // 展示用下载也会留下文件：清理"既不是当前桌面壁纸（记录里那张）、也不是本次下载"的旧缓存，
        // 保证 cache 目录最多只有两张（当前壁纸 + 今日展示图），不会随日期无限堆积
        CleanupStaleCache(cacheFile, GetRecordCacheFile());

        return cacheFile;
    }

    /// <summary>日期显示格式化：yyyyMMdd → 按语言文件的格式串显示（zh：2026年10月06日，需求）</summary>
    public static string FormatDateForDisplay(string yyyyMMdd)
    {
        if (DateTime.TryParseExact(yyyyMMdd, WallpaperConstants.RecordDateFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString(LocalizationService.Tr("Wallpaper.DateFormat"));
        }
        return yyyyMMdd; // 格式不符合就原样显示，不抛异常
    }

    #region 数据来源

    /// <summary>
    /// 官方接口：GET HPImageArchive.aspx → images[0] 里取 urlbase/copyright/enddate，
    /// 直链 = 数据下载域名 + urlbase + 尺寸后缀（需求）。
    /// </summary>
    private static async Task<WallpaperInfo> FetchFromOfficialAsync(AppSettings settings)
    {
        var json = await HttpGetStringAsync(WallpaperConstants.OfficialApiUrl);
        using var doc = JsonDocument.Parse(json);
        var image = doc.RootElement.GetProperty("images")[0];

        var urlBase = image.GetProperty("urlbase").GetString() ?? "";
        var copyright = image.GetProperty("copyright").GetString() ?? "";
        var endDate = image.GetProperty("enddate").GetString() ?? "";
        var startDate = image.GetProperty("startdate").GetString() ?? "";

        var url = GetHostUrl(settings.WallpaperDownloadHost)
                  + urlBase
                  + GetSizeSuffix(settings.WallpaperSize);

        return new WallpaperInfo
        {
            StartDate = startDate,
            EndDate = endDate,
            Copyright = copyright,
            ImageUrl = url,
            SizeText = GetSizeText(settings.WallpaperSize)
        };
    }

    /// <summary>
    /// 第三方 biturl 接口：GET bing.biturl.top → url/copyright/end_date，
    /// 直链 = 域名前缀替换 + 尺寸后缀替换（需求）。
    /// </summary>
    private static async Task<WallpaperInfo> FetchFromBiturlAsync(AppSettings settings)
    {
        // resolution 参数：1080P 传 1920、4K 传 UHD（返回的 url 尺寸后缀反正会被替换）
        var resolution = settings.WallpaperSize == WallpaperConstants.Size4K
            ? WallpaperConstants.BiturlResolution4K
            : WallpaperConstants.BiturlResolution1080P;
        var apiUrl = string.Format(
            CultureInfo.InvariantCulture, WallpaperConstants.BiturlApiUrlFormat, resolution);

        var json = await HttpGetStringAsync(apiUrl);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var rawUrl = root.GetProperty("url").GetString() ?? "";
        var copyright = root.GetProperty("copyright").GetString() ?? "";
        var endDate = root.GetProperty("end_date").GetString() ?? "";
        var startDate = root.GetProperty("start_date").GetString() ?? "";

        // 1) 提取路径：从 "/th?id=" 截到结尾，丢掉原始域名（这样换域名前缀才干净）
        var mark = rawUrl.IndexOf(WallpaperConstants.ImagePathMark, StringComparison.Ordinal);
        if (mark < 0)
        {
            throw new InvalidOperationException($"biturl 返回的地址不是必应壁纸直链：{rawUrl}");
        }
        var path = rawUrl[mark..];

        // 2) 尺寸后缀替换：去掉最后一个 "_" 后面的旧尺寸（_1920x1080.jpg / _UHD.jpg），拼上新尺寸
        //    （OHR 图片 id 本身带下划线，但尺寸一定是最后一段）
        var lastUnderscore = path.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            path = path[..lastUnderscore];
        }

        var url = GetHostUrl(settings.WallpaperDownloadHost)
                  + path
                  + GetSizeSuffix(settings.WallpaperSize);

        return new WallpaperInfo
        {
            StartDate = startDate,
            EndDate = endDate,
            Copyright = copyright,
            ImageUrl = url,
            SizeText = GetSizeText(settings.WallpaperSize)
        };
    }

    #endregion

    #region 小工具

    /// <summary>带 UA 的 GET，返回响应文本（非成功状态码直接抛异常）</summary>
    private static async Task<string> HttpGetStringAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>"数据下载"设置 → 图片直链域名前缀（通用/中国/全球，需求）</summary>
    private static string GetHostUrl(string hostSetting)
    {
        return hostSetting switch
        {
            WallpaperConstants.HostChina => WallpaperConstants.UrlChina,
            WallpaperConstants.HostGlobal => WallpaperConstants.UrlGlobal,
            _ => WallpaperConstants.UrlGeneral
        };
    }

    /// <summary>"壁纸尺寸"设置 → 图片直链后缀（1080P = _1920x1080.jpg，4K = _UHD.jpg，需求）</summary>
    private static string GetSizeSuffix(string sizeSetting)
    {
        return sizeSetting == WallpaperConstants.Size4K
            ? WallpaperConstants.Suffix4K
            : WallpaperConstants.Suffix1080P;
    }

    /// <summary>"壁纸尺寸"设置 → 文件名里的尺寸文字（1080P / 4K）</summary>
    private static string GetSizeText(string sizeSetting)
    {
        return sizeSetting == WallpaperConstants.Size4K
            ? WallpaperConstants.Size4K
            : WallpaperConstants.Size1080P;
    }

    /// <summary>"壁纸显示模式"设置 → 注册表 WallpaperStyle/TileWallpaper 两个值（需求：拉伸/适应/填充/平铺/居中/跨区）</summary>
    private static (string WallpaperStyle, string TileWallpaper) GetStyleRegistryValues(string styleSetting)
    {
        return styleSetting switch
        {
            WallpaperConstants.StyleFit => (WallpaperConstants.RegistryStyleFit, WallpaperConstants.TileOff),
            WallpaperConstants.StyleFill => (WallpaperConstants.RegistryStyleFill, WallpaperConstants.TileOff),
            WallpaperConstants.StyleTile => (WallpaperConstants.RegistryStyleCenterOrTile, WallpaperConstants.TileOn),
            WallpaperConstants.StyleCenter => (WallpaperConstants.RegistryStyleCenterOrTile, WallpaperConstants.TileOff),
            WallpaperConstants.StyleSpan => (WallpaperConstants.RegistryStyleSpan, WallpaperConstants.TileOff),
            _ => (WallpaperConstants.RegistryStyleStretch, WallpaperConstants.TileOff) // 拉伸（默认）
        };
    }

    /// <summary>记录里当前桌面壁纸对应的缓存文件路径（没换过壁纸返回 null）</summary>
    private string? GetRecordCacheFile()
    {
        if (string.IsNullOrEmpty(_record.EndDate))
        {
            return null;
        }
        return Path.Combine(DataPathService.WallpaperCacheDir, $"{_record.EndDate}_{_record.SizeText}.jpg");
    }

    /// <summary>
    /// 清理缓存目录：删除 keepFiles 之外的所有文件（旧日期/旧尺寸的缓存）。
    /// 单个文件被占用（如正被系统/预览使用）就跳过，下次清理再删。
    /// </summary>
    private static void CleanupStaleCache(params string?[] keepFiles)
    {
        try
        {
            var keepSet = keepFiles
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => Path.GetFullPath(f!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(DataPathService.WallpaperCacheDir))
            {
                if (keepSet.Contains(Path.GetFullPath(file)))
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 文件被占用：跳过，等下次清理
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>把缓存文件按"日期_尺寸.jpg"另存到设置的目录（需求命名规则）</summary>
    private static void SaveLocalCopy(string cacheFile, WallpaperInfo info, AppSettings settings)
    {
        var dir = ResolveSaveDir(settings.WallpaperSaveDir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, info.FileName);
        if (!string.Equals(Path.GetFullPath(cacheFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(cacheFile, target, overwrite: true);
        }
    }

    /// <summary>写错误日志到 data\crash.log（不影响界面）</summary>
    private static void LogError(string tag, string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataPathService.DataRoot, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Wallpaper[{tag}]: {message}\n\n");
        }
        catch { /* 日志写不进去就忽略 */ }
    }

    #endregion

    #region 换桌面壁纸（Win32）

    private const uint SpiSetDeskWallpaper = 0x0014;         // SPI_SETDESKWALLPAPER
    private const uint SpiUpdateIniFile = 0x0001;            // SPIF_UPDATEINIFILE
    private const uint SpiSendChange = 0x0002;               // SPIF_SENDCHANGE（立即广播生效）

    /// <summary>桌面显示方式所在的注册表键</summary>
    private const string DesktopRegistryKey = @"Control Panel\Desktop";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

    /// <summary>
    /// 把本地图片文件设置为桌面壁纸：
    /// 先按设置的显示模式改注册表（拉伸/适应/填充/平铺/居中/跨区），再调用 SPI_SETDESKWALLPAPER 立即生效。
    /// </summary>
    private static void SetDesktopWallpaper(string imageFilePath, string styleSetting)
    {
        var (wallpaperStyle, tileWallpaper) = GetStyleRegistryValues(styleSetting);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopRegistryKey, writable: true);
            key?.SetValue("WallpaperStyle", wallpaperStyle, Microsoft.Win32.RegistryValueKind.String);
            key?.SetValue("TileWallpaper", tileWallpaper, Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // 显示方式改不了不影响换壁纸（个别受限环境）
        }

        if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, imageFilePath, SpiUpdateIniFile | SpiSendChange))
        {
            throw new InvalidOperationException(
                $"设置桌面壁纸失败（SystemParametersInfo 错误码 {Marshal.GetLastWin32Error()}）");
        }
    }

    #endregion
}

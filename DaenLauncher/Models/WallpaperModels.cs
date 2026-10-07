using System.Text.Json.Serialization;

namespace DaenLauncher.Models;

/// <summary>
/// 必应每日壁纸相关常量（需求禁止硬编码：接口地址、尺寸后缀、来源标记等全部集中在这里）。
/// </summary>
public static class WallpaperConstants
{
    // ===== 壁纸尺寸（设置的取值 = 文件名里的尺寸文字，如 20261005_1080P.jpg） =====

    /// <summary>1080P 尺寸标记</summary>
    public const string Size1080P = "1080P";

    /// <summary>4K 尺寸标记</summary>
    public const string Size4K = "4K";

    /// <summary>1080P 对应的图片直链后缀</summary>
    public const string Suffix1080P = "_1920x1080.jpg";

    /// <summary>4K 对应的图片直链后缀</summary>
    public const string Suffix4K = "_UHD.jpg";

    // ===== 数据来源（元数据接口） =====

    /// <summary>必应官方接口（HPImageArchive.aspx）</summary>
    public const string SourceOfficial = "official";

    /// <summary>第三方 biturl 接口（bing.biturl.top）</summary>
    public const string SourceBiturl = "biturl";

    // ===== 数据下载（图片直链的域名前缀，对应设置项：通用/中国/全球） =====

    /// <summary>通用：www.bing.com</summary>
    public const string HostGeneral = "general";

    /// <summary>中国：cn.bing.com</summary>
    public const string HostChina = "china";

    /// <summary>全球：global.bing.com</summary>
    public const string HostGlobal = "global";

    /// <summary>通用域名</summary>
    public const string UrlGeneral = "https://www.bing.com";

    /// <summary>中国域名</summary>
    public const string UrlChina = "https://cn.bing.com";

    /// <summary>全球域名</summary>
    public const string UrlGlobal = "https://global.bing.com";

    // ===== 接口地址 =====

    /// <summary>官方接口（idx=0 表示今天，n=1 取一张，mkt 中文）</summary>
    public const string OfficialApiUrl =
        "https://cn.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN";

    /// <summary>第三方 biturl 接口（{0} = resolution 参数，1080P 传 1920、4K 传 UHD）</summary>
    public const string BiturlApiUrlFormat =
        "https://bing.biturl.top/?resolution={0}&format=json&index=0&mkt=zh-CN";

    /// <summary>biturl 接口 1080P 的 resolution 参数值</summary>
    public const string BiturlResolution1080P = "1920";

    /// <summary>biturl 接口 4K 的 resolution 参数值</summary>
    public const string BiturlResolution4K = "UHD";

    /// <summary>必应图片直链路径的公共特征（域名后面必然以 /th?id=OHR. 开头）</summary>
    public const string ImagePathMark = "/th?id=";

    // ===== 壁纸显示模式（设置的取值，需求顺序：拉伸、适应、填充、平铺、居中、跨区，默认拉伸） =====

    /// <summary>拉伸（铺满整屏，比例可能变形）</summary>
    public const string StyleStretch = "stretch";

    /// <summary>适应（等比完整显示，两侧可能有黑边）</summary>
    public const string StyleFit = "fit";

    /// <summary>填充（等比铺满，超出部分裁掉）</summary>
    public const string StyleFill = "fill";

    /// <summary>平铺（重复平铺）</summary>
    public const string StyleTile = "tile";

    /// <summary>居中（原始大小居中）</summary>
    public const string StyleCenter = "center";

    /// <summary>跨区（多显示器横跨）</summary>
    public const string StyleSpan = "span";

    // ===== 上面各模式对应的注册表值（HKCU\Control Panel\Desktop） =====

    /// <summary>WallpaperStyle：拉伸</summary>
    public const string RegistryStyleStretch = "2";

    /// <summary>WallpaperStyle：适应</summary>
    public const string RegistryStyleFit = "6";

    /// <summary>WallpaperStyle：填充</summary>
    public const string RegistryStyleFill = "10";

    /// <summary>WallpaperStyle：平铺/居中（都是 0，靠 TileWallpaper 区分）</summary>
    public const string RegistryStyleCenterOrTile = "0";

    /// <summary>WallpaperStyle：跨区</summary>
    public const string RegistryStyleSpan = "22";

    /// <summary>TileWallpaper：平铺开</summary>
    public const string TileOn = "1";

    /// <summary>TileWallpaper：平铺关</summary>
    public const string TileOff = "0";

    // ===== 本地文件 =====

    /// <summary>更换记录文件名（data\wallpaper\record.json，记录每天的更换情况，需求）</summary>
    public const string RecordFileName = "record.json";

    /// <summary>缓存子目录名 data\wallpaper\cache（下载的壁纸文件：设置桌面壁纸 + 窗口显示用）</summary>
    public const string CacheDirName = "cache";

    /// <summary>默认的"保存壁纸文件到本地"目录名 data\wallpaper\image（需求）</summary>
    public const string DefaultImageDirName = "image";

    /// <summary>日期格式 yyyyMMdd（与接口的 enddate/end_date 一致，便于"今天是否已更换"的比较）</summary>
    public const string RecordDateFormat = "yyyyMMdd";

    /// <summary>endate 字段的完整时间戳格式（记录里展示用）</summary>
    public const string AppliedAtFormat = "yyyy-MM-dd HH:mm:ss";
}

/// <summary>
/// 一次壁纸抓取的结果：元数据（日期/描述）+ 按设置拼好的图片直链 + 本地文件名。
/// </summary>
public sealed class WallpaperInfo
{
    /// <summary>壁纸起始日期（yyyyMMdd，官方接口的 startdate / biturl 的 start_date）</summary>
    public string StartDate { get; init; } = "";

    /// <summary>壁纸生效日期（yyyyMMdd，官方接口的 enddate / biturl 的 end_date，保存文件名用）</summary>
    public string EndDate { get; init; } = "";

    /// <summary>图片描述（copyright 字段）</summary>
    public string Copyright { get; init; } = "";

    /// <summary>壁纸图片直链（已按"数据下载"域名 + "壁纸尺寸"后缀拼好）</summary>
    public string ImageUrl { get; init; } = "";

    /// <summary>尺寸标记（1080P / 4K，本地文件名用）</summary>
    public string SizeText { get; init; } = WallpaperConstants.Size1080P;

    /// <summary>本地文件名：enddate_尺寸.jpg（如 20261005_1080P.jpg，需求命名规则）</summary>
    [JsonIgnore]
    public string FileName => $"{EndDate}_{SizeText}.jpg";
}

/// <summary>
/// 壁纸更换记录，保存到 data\wallpaper\record.json。
/// LastChangeDate 用于"每日自动更换"判断今天是否已经换过（需求：数据目录里记录每天的更换情况）。
/// </summary>
public sealed class WallpaperRecord
{
    /// <summary>最近一次成功更换壁纸的日期（yyyyMMdd）</summary>
    public string LastChangeDate { get; set; } = "";

    /// <summary>当前壁纸的生效日期（yyyyMMdd，对应桌面上正在使用的壁纸）</summary>
    public string EndDate { get; set; } = "";

    /// <summary>当前壁纸的图片描述（copyright）</summary>
    public string Copyright { get; set; } = "";

    /// <summary>当前壁纸的图片直链</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>当前壁纸的尺寸标记（1080P / 4K，缓存文件名用）</summary>
    public string SizeText { get; set; } = WallpaperConstants.Size1080P;

    /// <summary>最近一次更换的完整时间（仅展示用）</summary>
    public string AppliedAt { get; set; } = "";
}

/// <summary>更换壁纸的结果（窗口按钮反馈用）</summary>
public sealed record WallpaperApplyResult(bool Success, WallpaperInfo? Info, string Error)
{
    /// <summary>成功结果</summary>
    public static WallpaperApplyResult Ok(WallpaperInfo info) => new(true, info, "");

    /// <summary>失败结果（Error 是给用户看的原因摘要）</summary>
    public static WallpaperApplyResult Fail(string error) => new(false, null, error);
}

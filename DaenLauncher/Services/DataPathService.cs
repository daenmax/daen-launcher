using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 数据路径服务：所有数据和配置都存放在 exe 同级目录的 data 文件夹下
/// （需求-数据存储第1条：优先 exe 同级，不可写或无权限则回退 %LocalAppData%）。
/// </summary>
public static class DataPathService
{
    /// <summary>data 根目录</summary>
    public static string DataRoot { get; private set; } = "";

    /// <summary>是否使用了回退目录（LocalAppData）</summary>
    public static bool IsFallback { get; private set; }

    /// <summary>配置目录 data\config</summary>
    public static string ConfigDir => Path.Combine(DataRoot, "config");

    /// <summary>语言目录 data\language</summary>
    public static string LanguageDir => Path.Combine(DataRoot, "language");

    /// <summary>图标目录 data\icon</summary>
    public static string IconDir => Path.Combine(DataRoot, "icon");

    /// <summary>上传图标目录 data\icon\system</summary>
    public static string IconSystemDir => Path.Combine(IconDir, "system");

    /// <summary>启动器数据目录 data\launcher</summary>
    public static string LauncherDir => Path.Combine(DataRoot, "launcher");

    /// <summary>待办数据目录 data\todo</summary>
    public static string TodoDir => Path.Combine(DataRoot, "todo");

    /// <summary>随手记数据目录 data\note</summary>
    public static string NoteDir => Path.Combine(DataRoot, "note");

    /// <summary>剪贴板数据目录 data\clipboard</summary>
    public static string ClipboardDir => Path.Combine(DataRoot, "clipboard");

    /// <summary>必应壁纸数据目录 data\wallpaper（需求）</summary>
    public static string WallpaperDir => Path.Combine(DataRoot, "wallpaper");

    /// <summary>壁纸缓存目录 data\wallpaper\cache（下载的壁纸文件：设置桌面壁纸 + 窗口显示用）</summary>
    public static string WallpaperCacheDir => Path.Combine(WallpaperDir, WallpaperConstants.CacheDirName);

    /// <summary>壁纸默认保存目录 data\wallpaper\image（"保存壁纸文件到本地"的默认位置，需求）</summary>
    public static string WallpaperImageDir => Path.Combine(WallpaperDir, WallpaperConstants.DefaultImageDirName);

    /// <summary>项目图标缓存目录 data\icon\cache</summary>
    public static string IconCacheDir => Path.Combine(IconDir, "cache");

    /// <summary>
    /// 初始化数据根目录：优先 exe 同级\data，创建失败（只读/无权限）则回退 %LocalAppData%\DaenLauncher\data。
    /// 必须在 App 启动最开始调用。
    /// </summary>
    public static void Initialize()
    {
        // exe 所在目录：注意单文件发布时 AppContext.BaseDirectory 是解压临时目录，
        // 必须用 Environment.ProcessPath 才能拿到 exe 真实同级目录
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var primaryRoot = Path.Combine(exeDir, "data");

        if (TryEnsureDirectory(primaryRoot))
        {
            DataRoot = primaryRoot;
            IsFallback = false;
        }
        else
        {
            var fallbackRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DaenLauncher", "data");
            Directory.CreateDirectory(fallbackRoot);
            DataRoot = fallbackRoot;
            IsFallback = true;
        }

        // 预创建所有子目录
        foreach (var dir in new[]
                 {
                     ConfigDir, LanguageDir, IconSystemDir, IconCacheDir,
                     LauncherDir, TodoDir, NoteDir, ClipboardDir,
                     WallpaperDir, WallpaperCacheDir, WallpaperImageDir
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>尝试创建目录并验证可写</summary>
    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // 验证可写：写一个临时探测文件
            var probe = Path.Combine(path, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>程序名称（用于互斥体、事件名等，固定常量）</summary>
    public const string AppId = "DaenLauncher";
}

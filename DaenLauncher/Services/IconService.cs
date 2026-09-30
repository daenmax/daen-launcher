using System.Collections.Frozen;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DaenLauncher.Services;

/// <summary>
/// emoji 图标目录（需求：内置固定一批 emoji，需要分类：笑脸、动物、食物等）。
/// 图标选择器的第一个 Tab 展示。
/// </summary>
public static class EmojiCatalog
{
    /// <summary>emoji 分组：组名 -> emoji 列表</summary>
    public static readonly FrozenDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["笑脸"] = new[]
        {
            "😀","😃","😄","😁","😆","😅","🤣","😂","🙂","🙃",
            "😉","😊","😇","🥰","😍","🤩","😘","😗","😚","😙",
            "😋","😛","😜","🤪","😝","🤑","🤗","🤭","🤫","🤔",
            "🤐","🤨","😐","😑","😶","😏","😒","🙄","😬","😮‍💨",
            "😌","😔","😪","🤤","😴","😷","🤒","🤕","🤢","🥵"
        },
        ["手势"] = new[]
        {
            "👍","👎","👌","🤌","🤏","✌️","🤞","🤟","🤘","🤙",
            "👈","👉","👆","👇","☝️","👋","🤚","🖐️","✋","🖖",
            "👏","🙌","🤲","🤝","🙏","✍️","💪","🦾","🫶","🫰"
        },
        ["动物"] = new[]
        {
            "🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐨","🐯",
            "🦁","🐮","🐷","🐸","🐵","🙈","🙉","🙊","🐔","🐧",
            "🐦","🐤","🦆","🦅","🦉","🦇","🐺","🐗","🐴","🦄",
            "🐝","🐛","🦋","🐌","🐞","🐜","🐢","🐍","🦎","🐙"
        },
        ["食物"] = new[]
        {
            "🍏","🍎","🍐","🍊","🍋","🍌","🍉","🍇","🍓","🫐",
            "🍈","🍒","🍑","🥭","🍍","🥥","🥝","🍅","🍆","🥑",
            "🥦","🥬","🥒","🌶️","🌽","🥕","🧄","🧅","🥔","🍠",
            "🥐","🍞","🥖","🧀","🥚","🍳","🥞","🧇","🥓","🍔"
        },
        ["自然"] = new[]
        {
            "🌍","🌎","🌏","🌐","🌑","🌒","🌓","🌔","🌕","🌖",
            "🌙","⭐","🌟","✨","⚡","☄️","🔥","💧","🌊","❄️",
            "🌈","☀️","⛅","☁️","🌧️","⛈️","🌪️","🌸","🌹","🌺",
            "🌻","🌼","🌷","🌱","🌲","🌳","🌴","🌵","🌾","🍀"
        },
        ["物品"] = new[]
        {
            "💻","🖥️","⌨️","🖱️","💾","💿","📀","📱","☎️","📞",
            "🔋","🔌","💡","🔦","🕯️","🧯","🛒","🎁","🎈","🎉",
            "🎊","🎯","🎮","🕹️","🎲","🧩","🎨","🎭","🎤","🎧",
            "🎵","🎶","🎹","🥁","🎸","🎺","📚","📖","📝","📁"
        },
        ["符号"] = new[]
        {
            "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔",
            "❣️","💕","💞","💓","💗","💖","💘","💝","💯","💢",
            "💥","💫","💦","💨","🕳️","💬","💭","🗯️","♻️","✅",
            "❌","❎","➕","➖","➗","⚠️","🚫","⛔","🔒","🔑"
        }
    }.ToFrozenDictionary();

    /// <summary>判断图标值是不是 emoji（单字符或组合 emoji，不含路径分隔符和扩展名）</summary>
    public static bool IsEmoji(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return false;
        return !icon.Contains('.') && !icon.Contains('/') && !icon.Contains('\\');
    }
}

/// <summary>
/// 图标服务：解析分类/子分类图标（emoji 或 png 文件名）为 UI 元素所需的 ImageSource。
/// 存储规则（需求）：
/// - emoji：直接存 emoji 字符本身；
/// - 上传的 png：保存到 data\icon\system，用 10 位秒级时间戳命名，存储文件名；
/// - group_ 文件夹里的 png：直接引用，存储图片文件名。
/// </summary>
public static class IconService
{
    /// <summary>图标 ImageSource 缓存（文件名 -> 位图），避免列表滚动/刷新时反复读盘</summary>
    private static readonly Dictionary<string, BitmapImage> ImageCache = new();

    /// <summary>
    /// 把内嵌的应用 ico（多尺寸：16~256px）释放到 data\icon\cache\app.ico，
    /// 供 AppWindow.SetIcon（任务栏/标题栏）使用。返回 ico 文件路径；失败返回 null。
    /// 注意：缓存文件会和内嵌资源做字节比对，不一致（换 logo 升级后的残留旧文件）就重新释放，
    /// 否则升级后任务栏/标题栏会一直显示旧图标。
    /// </summary>
    public static string? EnsureAppIconExtracted()
    {
        try
        {
            var icoPath = System.IO.Path.Combine(DataPathService.IconCacheDir, "app.ico");

            // ico 只有几十 KB，每次启动读一遍内嵌字节开销可忽略
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("DaenLauncher.Assets.logo.logo_图标组.ico");
            if (stream == null) return null;
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            // 缓存文件不存在或内容和内嵌资源不一致（旧版本残留）时重写
            var needsWrite = true;
            if (System.IO.File.Exists(icoPath))
            {
                var cached = System.IO.File.ReadAllBytes(icoPath);
                needsWrite = cached.Length != bytes.Length || !cached.AsSpan().SequenceEqual(bytes);
            }
            if (needsWrite)
            {
                System.IO.File.WriteAllBytes(icoPath, bytes);
            }
            return System.IO.File.Exists(icoPath) ? icoPath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成 10 位秒级 Unix 时间戳文件名</summary>
    public static string GenerateTimestampFileName()
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return seconds.ToString(); // 10 位（到 2286 年）
    }

    /// <summary>
    /// 解析图标值（emoji 或 文件名）为 ImageSource；emoji 返回 null（调用方应显示 emoji 文本）。
    /// 查找顺序：data\icon\system\文件名 -> data\icon\group_*\文件名。
    /// </summary>
    public static async Task<ImageSource?> ResolveImageAsync(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon) || EmojiCatalog.IsEmoji(icon)) return null;

        // 安全：只取文件名，防止路径穿越
        var fileName = Path.GetFileName(icon);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        lock (ImageCache)
        {
            if (ImageCache.TryGetValue(fileName, out var cached)) return cached;
        }

        ImageSource? result = null;

        var systemPath = Path.Combine(DataPathService.IconSystemDir, fileName);
        if (File.Exists(systemPath))
        {
            result = await LoadBitmapAsync(systemPath);
        }
        else
        {
            // 在所有 group_ 文件夹里找
            try
            {
                foreach (var dir in Directory.GetDirectories(DataPathService.IconDir, "group_*"))
                {
                    var path = Path.Combine(dir, fileName);
                    if (File.Exists(path))
                    {
                        result = await LoadBitmapAsync(path);
                        break;
                    }
                }
            }
            catch
            {
                // 目录读取失败
            }
        }

        if (result != null)
        {
            lock (ImageCache)
            {
                ImageCache[fileName] = (BitmapImage)result;
            }
        }
        return result;
    }

    /// <summary>从文件加载 BitmapImage（异步，避免卡 UI）</summary>
    public static async Task<BitmapImage> LoadBitmapAsync(string filePath)
    {
        var bitmap = new BitmapImage();
        var bytes = await File.ReadAllBytesAsync(filePath);
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    /// <summary>从嵌入资源加载 BitmapImage（资源名如 "DaenLauncher.Assets.logo.logo_64.png"）</summary>
    public static async Task<BitmapImage?> LoadEmbeddedAsync(string resourceName)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取所有 group_ 图标文件夹：文件夹名（去掉 group_ 前缀）-> 文件夹路径。
    /// 例如 "group_水果系列" -> ("水果系列", 路径)。
    /// </summary>
    public static List<(string Name, string Path)> GetGroupFolders()
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var dir in Directory.GetDirectories(DataPathService.IconDir, "group_*"))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("group_", StringComparison.Ordinal))
                {
                    name = name["group_".Length..];
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add((name, dir));
                }
            }
        }
        catch
        {
            // 目录读取失败
        }
        return result;
    }

    /// <summary>获取 system 目录下所有上传的图标文件名</summary>
    public static List<string> GetSystemIcons()
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(DataPathService.IconSystemDir, "*.png"))
            {
                result.Add(Path.GetFileName(file));
            }
        }
        catch
        {
            // 目录读取失败
        }
        return result;
    }

    /// <summary>
    /// 保存上传的图标 png 到 data\icon\system，10 位秒级时间戳命名，返回存储的文件名。
    /// </summary>
    public static string SaveUploadedImage(byte[] pngBytes)
    {
        var fileName = GenerateTimestampFileName() + ".png";
        var target = Path.Combine(DataPathService.IconSystemDir, fileName);
        File.WriteAllBytes(target, pngBytes);
        return fileName;
    }
}

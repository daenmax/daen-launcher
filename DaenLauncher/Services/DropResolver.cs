using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 拖拽解析服务（需求-主窗口布局4）：
/// 支持拖拽 exe、快捷方式(.lnk 解析完整路径)、文件夹、文件、url文件、浏览器链接等，
/// 一个或多个，解析成 LauncherItem 添加到当前子分类。
/// </summary>
public static class DropResolver
{
    /// <summary>
    /// 从拖拽数据包解析出待添加的项目列表。
    /// </summary>
    public static async Task<List<LauncherItem>> ResolveAsync(Windows.ApplicationModel.DataTransfer.DataPackageView package)
    {
        var items = new List<LauncherItem>();

        try
        {
            // 1. 浏览器拖进来的链接
            if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
            {
                var uri = await package.GetWebLinkAsync();
                if (!string.IsNullOrWhiteSpace(uri.ToString()))
                {
                    items.Add(new LauncherItem
                    {
                        Name = uri.Host,
                        Path = uri.ToString(),
                        Type = LauncherItemType.Url
                    });
                }
            }

            // 2. 文件/文件夹
            if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var storageItems = await package.GetStorageItemsAsync();
                foreach (var storage in storageItems)
                {
                    var item = ResolveStorageItem(storage);
                    if (item != null) items.Add(item);
                }
            }
        }
        catch
        {
            // 解析失败的项目直接跳过
        }

        return items;
    }

    /// <summary>解析单个存储项（文件或文件夹）</summary>
    private static LauncherItem? ResolveStorageItem(Windows.Storage.IStorageItem storage)
    {
        try
        {
            var path = storage.Path;
            var name = storage.Name;

            // 虚拟项（如从开始菜单拖出的 UWP 应用）没有本地路径
            if (string.IsNullOrWhiteSpace(path))
            {
                // 尝试当作 UWP 应用处理（AppsFolder 项）
                return new LauncherItem
                {
                    Name = name,
                    Path = name,
                    Type = LauncherItemType.Uwp
                };
            }

            if (storage is Windows.Storage.IStorageFolder)
            {
                return new LauncherItem { Name = name, Path = path, Type = LauncherItemType.Folder };
            }

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            // 快捷方式文件（.lnk / .url）统一走 ResolveShortcutFile（拖拽和"添加项目"共用）
            if (ext == ".lnk" || ext == ".url")
            {
                return ResolveShortcutFile(path);
            }

            if (ext == ".exe")
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(name),
                    Path = path,
                    Type = LauncherItemType.Exe
                };
            }

            // 其他文件
            return new LauncherItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(name),
                Path = path,
                Type = LauncherItemType.File
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析一个快捷方式文件（.lnk 或 .url）为项目。
    /// 供拖拽（ResolveStorageItem）和右键"添加项目-快捷方式"两处复用。
    /// 解析失败的 .lnk 直接按 lnk 本身启动（Shell 可以执行 lnk）。
    /// </summary>
    public static LauncherItem? ResolveShortcutFile(string path)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            // .url 文件：INI 文本解析出 URL 和图标声明
            if (ext == ".url")
            {
                var shortcut = LnkResolver.ReadInternetShortcut(path);
                if (shortcut.Url == null) return null;

                // 地址是 http/https 才算"网址"，其他（如 steam://rungameid/993090）是"协议"。
                // 之前一律标"网址"是 BUG：网址类型会联网抓 favicon，steam:// 永远抓不到
                // （第五十四轮修复；图标则优先用 .url 里声明的专属图标，如 Steam 游戏图标）
                var iconFile = shortcut.IconFile != null && File.Exists(shortcut.IconFile)
                    ? shortcut.IconFile
                    : null;
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = shortcut.Url,
                    Type = ClassifyUrl(shortcut.Url),
                    IconFile = iconFile
                };
            }

            if (ext != ".lnk") return null;

            var resolved = LnkResolver.Resolve(path);
            if (resolved == null)
            {
                // 解析失败：直接用 lnk 本身启动（Shell 可以执行 lnk）
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    Type = LauncherItemType.Lnk
                };
            }

            var target = resolved.Value.Target;
            var args = resolved.Value.Arguments;

            // 目标是网址
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = target,
                    Type = LauncherItemType.Url
                };
            }

            // 目标是特殊协议（如 steam:// 等）
            var protocolIndex = target.IndexOf("://", StringComparison.Ordinal);
            if (protocolIndex > 0 && protocolIndex < 20 &&
                !target.Contains('\\') && !File.Exists(target))
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = target,
                    Type = LauncherItemType.Protocol
                };
            }

            // 目标是 UWP 应用
            if (target.StartsWith(LauncherItemPaths.UwpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = target,
                    Type = LauncherItemType.Uwp
                };
            }

            // 目标是 exe
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Edge/Chrome 等浏览器的桌面快捷方式自带 --single-argument 等内部参数，
                // 对用户是噪音，过滤掉
                args = StripBrowserNoiseArguments(args);

                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = target,
                    Type = LauncherItemType.Exe,
                    Arguments = args
                };
            }

            // 目标是非 exe 的本地文件（.bat/.cmd/.ps1/.msc 等）：
            // 也要用"目标路径"而不是快捷方式本身，否则复制路径/创建桌面快捷方式/
            // 打开所在位置全都指向 .lnk（需求-BUG：拖入 .bat 快捷方式后路径是 .lnk）。
            // 类型沿用"文件"，启动走 ShellExecute（系统会按扩展名关联执行）。
            if (File.Exists(target))
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = target,
                    Type = LauncherItemType.File,
                    Arguments = args
                };
            }

            // 其他目标（如 LNK 指向的文件已被删除，或指向 .lnk/.url 的二级快捷方式）：
            // 目标文件不存在时保留快捷方式本身，保证仍可点击启动
            if (!Directory.Exists(target))
            {
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    Type = LauncherItemType.Lnk
                };
            }

            // 目标是个文件夹
            return new LauncherItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = target,
                Type = LauncherItemType.Folder
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断地址是普通网址（http/https）还是特殊协议（steam:// 等）。
    /// .url 文件里声明的地址直接可信，不需要 .lnk 目标那套存在性检查。</summary>
    private static LauncherItemType ClassifyUrl(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? LauncherItemType.Url
            : LauncherItemType.Protocol;
    }

    /// <summary>
    /// 过滤浏览器快捷方式里的内部参数（对用户是噪音）。
    /// Edge/Chrome 的桌面快捷方式自带 --single-argument 等，删除后要合并多余空格。
    /// </summary>
    private static string StripBrowserNoiseArguments(string arguments)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            arguments, @"--single-argument", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }
}

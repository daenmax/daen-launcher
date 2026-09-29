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

            if (ext == ".lnk")
            {
                var resolved = LnkResolver.Resolve(path);
                if (resolved == null)
                {
                    // 解析失败：直接用 lnk 本身启动（Shell 可以执行 lnk）
                    return new LauncherItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
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
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
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
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
                        Path = target,
                        Type = LauncherItemType.Protocol
                    };
                }

                // 目标是 UWP 应用
                if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    return new LauncherItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
                        Path = target,
                        Type = LauncherItemType.Uwp
                    };
                }

                // 目标是 exe
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Edge/Chrome 等浏览器的桌面快捷方式自带 --single-argument 等内部参数，
                    // 对用户是噪音，过滤掉
                    args = System.Text.RegularExpressions.Regex.Replace(
                        args, @"--single-argument", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

                    return new LauncherItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
                        Path = target,
                        Type = LauncherItemType.Exe,
                        Arguments = args
                    };
                }

                // 其他目标按 lnk 本身处理
                return new LauncherItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(name),
                    Path = path,
                    Type = LauncherItemType.Lnk
                };
            }

            if (ext == ".url")
            {
                var url = LnkResolver.TryReadUrlFile(path);
                if (url != null)
                {
                    return new LauncherItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(name),
                        Path = url,
                        Type = LauncherItemType.Url
                    };
                }
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
}

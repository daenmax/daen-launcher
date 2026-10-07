using System.IO.Compression;

namespace DaenLauncher.Services;

/// <summary>
/// 数据导出/导入/删除服务（需求-数据页）。
/// 数据项定义：软件配置(config、language、icon)、启动器(launcher)、待办(todo)、随手记(note)、
/// 剪贴板(clipboard)、必应壁纸(wallpaper)。
/// </summary>
public static class DataService
{
    /// <summary>数据项 key -> 子目录名（相对 data 根）</summary>
    public static readonly (string Key, string[] Folders)[] DataItems =
    {
        ("config", new[] { "config", "language", "icon" }),
        ("launcher", new[] { "launcher" }),
        ("todo", new[] { "todo" }),
        ("note", new[] { "note" }),
        ("clipboard", new[] { "clipboard" }),
        ("wallpaper", new[] { "wallpaper" })
    };

    /// <summary>某个数据项的文件夹是否存在于本地</summary>
    public static bool ExistsLocally(string key)
    {
        var folders = DataItems.First(d => d.Key == key).Folders;
        return folders.Any(f => Directory.Exists(Path.Combine(DataPathService.DataRoot, f)));
    }

    /// <summary>
    /// 导出：根据勾选的数据项打包 data 文件夹为 zip。
    /// zipPath 一般是 用户选择的保存路径（文件名 yyyyMMdd_HHmmss.zip）。
    /// </summary>
    public static void Export(string zipPath, IEnumerable<string> selectedKeys)
    {
        var selectedFolders = selectedKeys
            .SelectMany(k => DataItems.First(d => d.Key == k).Folders)
            .ToHashSet();

        var fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip";
        var finalPath = zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? zipPath : zipPath + ".zip";

        // 注意：系统的"另存为"对话框会预先创建一个空的 zip 文件，
        // 而 ZipFile.Open(path, ZipArchiveMode.Create) 内部用 FileMode.CreateNew，
        // 遇到已存在的文件会抛 IOException（表现为"导出失败"）。
        // 所以这里手动打开文件流（FileMode.Create 覆盖已有文件）再创建 ZipArchive。
        var stream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);        foreach (var folder in selectedFolders)
        {
            var dirPath = Path.Combine(DataPathService.DataRoot, folder);
            if (!Directory.Exists(dirPath)) continue;

            // zip 内路径统一用 data\ 前缀，导入时按此识别
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(DataPathService.DataRoot, file);
                archive.CreateEntryFromFile(file, "data/" + relative.Replace('\\', '/'));
            }
        }
    }

    /// <summary>
    /// 解析 zip 里包含哪些数据项（用于导入前的勾选界面，zip 里没有的项置灰）。
    /// </summary>
    public static HashSet<string> AnalyzeZip(string zipPath)
    {
        var result = new HashSet<string>();
        using var archive = ZipFile.OpenRead(zipPath);
        var prefixes = archive.Entries
            .Select(e => e.FullName)
            .Where(n => n.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            .Select(n => n["data/".Length..].Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, folders) in DataItems)
        {
            if (folders.Any(f => prefixes.Contains(f)))
            {
                result.Add(key);
            }
        }
        return result;
    }

    /// <summary>
    /// 导入逻辑（需求）：
    /// 1. 先备份整个 data 文件夹到同级目录（带日期）；
    /// 2. 根据选择删除本地对应文件夹；
    /// 3. 从 zip 解压覆盖进来。
    /// </summary>
    public static void Import(string zipPath, IEnumerable<string> selectedKeys)
    {
        // 1. 备份 data 目录
        var backupDir = DataPathService.DataRoot + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (Directory.Exists(DataPathService.DataRoot))
        {
            CopyDirectory(DataPathService.DataRoot, backupDir);
        }

        // 2. 删除本地对应文件夹
        foreach (var key in selectedKeys)
        {
            foreach (var folder in DataItems.First(d => d.Key == key).Folders)
            {
                var dirPath = Path.Combine(DataPathService.DataRoot, folder);
                if (Directory.Exists(dirPath))
                {
                    Directory.Delete(dirPath, true);
                }
            }
        }

        // 3. 解压覆盖
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目跳过

            var relative = entry.FullName["data/".Length..];
            var targetPath = Path.Combine(DataPathService.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar));

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(targetPath, true);
        }
    }

    /// <summary>
    /// 删除勾选的数据文件夹（需求：二次确认后删除）。
    /// </summary>
    public static void DeleteData(IEnumerable<string> selectedKeys)
    {
        foreach (var key in selectedKeys)
        {
            foreach (var folder in DataItems.First(d => d.Key == key).Folders)
            {
                var dirPath = Path.Combine(DataPathService.DataRoot, folder);
                if (Directory.Exists(dirPath))
                {
                    try { Directory.Delete(dirPath, true); } catch { /* 删除失败跳过 */ }
                }
            }
        }
    }

    /// <summary>递归复制目录</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destPath = Path.Combine(target, relative);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(file, destPath, true);
        }
    }
}

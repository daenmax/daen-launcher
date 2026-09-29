using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DaenLauncher.Services;

/// <summary>
/// 项目图标提取服务：从 exe/lnk/文件/文件夹提取图标并缓存为 png（data\icon\cache）。
/// UWP/Url/协议项目没有本地图标，返回 null（UI 显示通用占位图标）。
/// </summary>
public static class ItemIconService
{
    #region Win32 SHGetFileInfo

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    #endregion

    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    /// <summary>
    /// 获取项目图标的 BitmapImage。缓存文件按项目 Id 存放在 data\icon\cache。
    /// </summary>
    public static async Task<ImageSource?> GetIconAsync(DaenLauncher.Models.LauncherItem item)
    {
        try
        {
            // UWP / URL / 协议：无本地图标
            if (item.Type is DaenLauncher.Models.LauncherItemType.Uwp
                or DaenLauncher.Models.LauncherItemType.Url
                or DaenLauncher.Models.LauncherItemType.Protocol)
            {
                return null;
            }

            var cacheFile = Path.Combine(DataPathService.IconCacheDir, item.Id + ".png");
            var metaFile = cacheFile + ".meta";
            if (File.Exists(cacheFile) && IsCacheValid(item, metaFile))
            {
                return await IconService.LoadBitmapAsync(cacheFile);
            }

            // 提取图标：Shell 图标提取器要求 STA COM（线程池 MTA 线程上会返回成功但拿不到图标），
            // 所以用专用 STA 线程执行
            byte[]? pngBytes = null;
            var thread = new Thread(() => { pngBytes = ExtractIconPng(item.Path); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (pngBytes == null) return null;

            await CacheLock.WaitAsync();
            try
            {
                await File.WriteAllBytesAsync(cacheFile, pngBytes);
                // 记录缓存对应的源路径，用于缓存有效性判断
                await File.WriteAllTextAsync(metaFile, item.Path ?? "");
            }
            finally
            {
                CacheLock.Release();
            }

            return await IconService.LoadBitmapAsync(cacheFile);
        }
        catch (Exception ex)
        {
            // 诊断：图标加载失败写日志（不阻塞 UI）
            try
            {
                File.AppendAllText(Path.Combine(DataPathService.DataRoot, "crash.log"),
                    $"{DateTime.Now:HH:mm:ss} icon load failed [{item.Name}] {item.Path}: {ex.Message}\n");
            }
            catch { /* 忽略 */ }
            return null;
        }
    }

    /// <summary>删除项目图标缓存</summary>
    public static void DeleteCache(DaenLauncher.Models.LauncherItem item)
    {
        try
        {
            var cacheFile = Path.Combine(DataPathService.IconCacheDir, item.Id + ".png");
            if (File.Exists(cacheFile)) File.Delete(cacheFile);
            var metaFile = cacheFile + ".meta";
            if (File.Exists(metaFile)) File.Delete(metaFile);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>缓存有效性：meta 里记录的源路径与当前一致</summary>
    private static bool IsCacheValid(DaenLauncher.Models.LauncherItem item, string metaFile)
    {
        try
        {
            if (!File.Exists(metaFile)) return false;
            return File.ReadAllText(metaFile).Equals(item.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 SHGetFileInfo 提取图标并转为 PNG 字节</summary>
    private static byte[]? ExtractIconPng(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                LogExtract($"path not found: {path}");
                return null;
            }

            var info = new SHFILEINFOW();
            var result = SHGetFileInfo(path, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                LogExtract($"SHGetFileInfo failed for {path} (result={result})");
                return null;
            }

            try
            {
                using var icon = Icon.FromHandle(info.hIcon);
                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            LogExtract($"exception for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>图标提取诊断日志</summary>
    private static void LogExtract(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataPathService.DataRoot, "crash.log"),
                $"{DateTime.Now:HH:mm:ss} extract: {message}\n");
        }
        catch { /* 忽略 */ }
    }
}

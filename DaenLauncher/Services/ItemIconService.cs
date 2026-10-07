using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
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

    // ===== UWP/协议图标提取的 Win32 互操作 =====

    /// <summary>IShellItemImageFactory 接口 GUID</summary>
    private static readonly Guid ShellItemImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // SIIGBF 标志（IShellItemImageFactory.GetImage 的行为开关）
    private const uint SIIGBF_BIGGERSIZEOK = 0x1; // 允许返回更大的图（再由 UI 缩小，更清晰）
    private const uint SIIGBF_ICONONLY = 0x4;     // 只要图标（不要缩略图）

    // AssocQueryString 参数常量
    private const uint ASSOCF_NONE = 0;
    private const uint ASSOCSTR_EXECUTABLE = 2; // 查询关联的可执行文件
    private const uint DIB_RGB_COLORS = 0;      // GetDIBits：颜色表用 RGB
    private const uint BI_RGB = 0;              // 无压缩位图

    /// <summary>
    /// shell 项的图标/缩略图工厂。注意：原生签名是 GetImage(SIZE size, SIIGBF flags, HBITMAP* phbm)，
    /// SIZE 是 8 字节结构体，x64 ABI 下按"单个寄存器"整体传值——必须声明成结构体参数，
    /// 拆成两个 int 会占两个寄存器导致参数错位崩溃（第五十一轮实测踩坑）。
    /// </summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Width;
        public int Height;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    /// <summary>查询文件/协议关联信息（如协议默认处理程序的 exe 路径）</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint str, string pszAssoc,
        string? pszExtra, [Out] StringBuilder pszOut, ref uint pcchOut);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cb, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[]? bits, ref BITMAPINFO info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    #endregion

    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    /// <summary>
    /// 获取项目图标的 BitmapImage。缓存文件按项目 Id 存放在 data\icon\cache。
    /// </summary>
    public static async Task<ImageSource?> GetIconAsync(DaenLauncher.Models.LauncherItem item)
    {
        try
        {
            var cacheFile = Path.Combine(DataPathService.IconCacheDir, item.Id + ".png");
            var metaFile = cacheFile + ".meta";
            if (File.Exists(cacheFile) && IsCacheValid(item, metaFile))
            {
                return await IconService.LoadBitmapAsync(cacheFile);
            }

            byte[]? pngBytes;
            if (item.Type == DaenLauncher.Models.LauncherItemType.Url)
            {
                // 网址：联网取站点 favicon（第五十二轮需求）
                if (HasFailedFavicon(item.Path)) return null;
                pngBytes = await FaviconService.DownloadPngAsync(item.Path);
                if (pngBytes == null)
                {
                    // 本次会话内不再重试（面板重建会反复触发，避免每个网址反复超时），下次启动重试
                    MarkFailedFavicon(item.Path);
                    return null;
                }
            }
            else
            {
                // 提取图标：Shell 图标提取器要求 STA COM（线程池 MTA 线程上会返回成功但拿不到图标），
                // 所以用专用 STA 线程执行
                pngBytes = null;
                var thread = new Thread(() =>
                {
                    // 项目带外部图标源（如 Steam 游戏 .url 里声明的专属图标）时优先用它，
                    // 文件已消失则回退按类型提取默认图标
                    pngBytes = !string.IsNullOrEmpty(item.IconFile) && File.Exists(item.IconFile)
                        ? ExtractExternalIconPng(item.IconFile)
                        : item.Type switch
                        {
                            // UWP/商店应用：从 shell:AppsFolder 项提取应用图标
                            DaenLauncher.Models.LauncherItemType.Uwp => ExtractUwpIconPng(item.Path),
                            // 协议：提取协议默认处理程序（如 steam:// → Steam）的图标
                            DaenLauncher.Models.LauncherItemType.Protocol => ExtractProtocolIconPng(item.Path),
                            _ => ExtractIconPng(item.Path)
                        };
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                if (pngBytes == null) return null;
            }

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

    // ===== 会话内 favicon 失败记录 =====

    /// <summary>本次会话中 favicon 获取失败的网址：面板重建会反复触发图标加载，
    /// 失败过的不再重复联网（避免每个失败网址反复等超时），下次启动自动重试</summary>
    private static readonly HashSet<string> FailedFaviconUrls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FailedFaviconLock = new();

    private static bool HasFailedFavicon(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (FailedFaviconLock)
        {
            return FailedFaviconUrls.Contains(url);
        }
    }

    private static void MarkFailedFavicon(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (FailedFaviconLock)
        {
            FailedFaviconUrls.Add(url);
        }
    }

    /// <summary>清除网址的会话内 favicon 失败记录（刷新图标时调用，允许立即重试联网）</summary>
    public static void ClearFailedFavicon(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (FailedFaviconLock)
        {
            FailedFaviconUrls.Remove(url);
        }
    }

    /// <summary>
    /// 强制刷新项目图标（需求：右键"刷新图标"，支持所有类型）：
    /// 清掉磁盘缓存（网址类型还会清会话内失败记录，允许立即重新联网），
    /// 然后按类型重新提取并写入缓存。返回是否成功拿到新图标。
    /// </summary>
    public static async Task<bool> RefreshIconAsync(DaenLauncher.Models.LauncherItem item)
    {
        DeleteCache(item);
        ClearFailedFavicon(item.Path);
        return await GetIconAsync(item) != null;
    }

    /// <summary>
    /// 获取 UWP 应用图标 PNG 字节（按 AUMID 磁盘缓存，文件名 uwp_应用ID.png）。
    /// 供"添加项目-UWP 应用"选择器列表显示图标；主面板项目图标走 GetIconAsync（按项目 Id 缓存）。
    /// </summary>
    public static async Task<byte[]?> GetUwpAppIconPngBytesAsync(string aumid)
    {
        var cacheFile = Path.Combine(DataPathService.IconCacheDir, UwpIconCacheName(aumid));
        try
        {
            if (File.Exists(cacheFile))
            {
                return await File.ReadAllBytesAsync(cacheFile);
            }
        }
        catch
        {
            // 读缓存失败当作没有，走重新提取
        }

        return await Task.Run(() =>
        {
            // Shell 图标提取要求 STA 线程（与 GetIconAsync 同款守卫）
            byte[]? png = null;
            var thread = new Thread(() => png = ExtractUwpIconPng(aumid));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (png == null) return null;

            try
            {
                File.WriteAllBytes(cacheFile, png);
            }
            catch
            {
                // 缓存写失败不影响本次返回
            }
            return png;
        });
    }

    /// <summary>AUMID 转成安全的缓存文件名（uwp_应用ID.png，非法字符替换为下划线）</summary>
    private static string UwpIconCacheName(string aumid)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder("uwp_");
        foreach (var c in aumid)
        {
            builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
        }
        builder.Append(".png");
        return builder.ToString();
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

    /// <summary>UWP 应用图标的请求尺寸（Shell 自动挑最合适的资源再缩放，大图缩到小尺寸更清晰）</summary>
    private const int UwpIconRequestSize = 256;

    /// <summary>
    /// 提取外部图标源文件的图标（item.IconFile，如 Steam 游戏 .url 里声明的专属图标）。
    /// 位图类文件（png/jpg/bmp/gif）直接解码；.ico/exe 等走 SHGetFileInfo 提取。
    /// </summary>
    private static byte[]? ExtractExternalIconPng(string iconPath)
    {
        var ext = Path.GetExtension(iconPath).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
        {
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(iconPath));
                using var image = Image.FromStream(ms);
                using var bitmap = new Bitmap(image);
                using var output = new MemoryStream();
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                LogExtract($"external icon decode failed for {iconPath}: {ex.Message}");
                return null;
            }
        }
        // .ico 等其他格式走 Shell 图标提取
        return ExtractIconPng(iconPath);
    }

    /// <summary>
    /// 提取 UWP/商店应用图标（path 形如 shell:AppsFolder\包家族名!应用ID）。
    /// SHCreateItemFromParsingName 把路径变成 shell 项，再用 IShellItemImageFactory.GetImage
    /// 拿应用图标位图（这是系统给商店应用建快捷方式时用的同款 API）。失败返回 null。
    /// </summary>
    private static byte[]? ExtractUwpIconPng(string path)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            // 路径规范化：没带前缀但含 "!" 的（老数据只存了应用ID）按 AUMID 补全前缀
            var shellPath = path.StartsWith(DaenLauncher.Models.LauncherItemPaths.UwpPrefix,
                StringComparison.OrdinalIgnoreCase)
                ? path
                : path.Contains('!')
                    ? DaenLauncher.Models.LauncherItemPaths.UwpPrefix + path
                    : path;

            SHCreateItemFromParsingName(shellPath, IntPtr.Zero, ShellItemImageFactoryIid,
                out var factory);
            var hr = factory.GetImage(
                new SIZE { Width = UwpIconRequestSize, Height = UwpIconRequestSize },
                SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                LogExtract($"UWP GetImage failed for {shellPath} (hr=0x{hr:X})");
                return null;
            }
            return HBitmapToPngBytes(hBitmap);
        }
        catch (Exception ex)
        {
            LogExtract($"UWP icon exception for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }

    /// <summary>
    /// 提取协议图标：AssocQueryString 查协议默认处理程序的 exe（steam:// → Steam），
    /// 再提取该 exe 的图标。没有 exe 处理程序的协议（如 ms-settings:）返回 null（显示占位图标）。
    /// 注意：一个协议只对应一个处理程序，所以同一协议的不同地址（不同 Steam 游戏）图标相同。
    /// </summary>
    private static byte[]? ExtractProtocolIconPng(string path)
    {
        try
        {
            var colon = path.IndexOf(':');
            if (colon <= 0) return null;
            var scheme = path[..colon].Trim();
            if (scheme.Length == 0) return null;

            var buffer = new StringBuilder(1040);
            var length = (uint)buffer.Capacity;
            var hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, scheme, "open",
                buffer, ref length);
            if (hr != 0)
            {
                LogExtract($"AssocQueryString('{scheme}') failed (hr=0x{hr:X})");
                return null;
            }
            var handlerExe = buffer.ToString().TrimEnd('\0');
            if (string.IsNullOrEmpty(handlerExe) || !File.Exists(handlerExe)) return null;

            // 复用 SHGetFileInfo 提取处理程序图标（与其他 exe 项目观感一致）
            return ExtractIconPng(handlerExe);
        }
        catch (Exception ex)
        {
            LogExtract($"protocol icon exception for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// HBITMAP → PNG 字节：GetDIBits 把 32bpp 位数据整块拷出（保留透明通道），
    /// 再写入 32bppArgb 位图存 PNG。全部 alpha=0 的图视为"没有透明通道"，统一置为不透明
    /// （否则这种图标会整体透明消失）。
    /// </summary>
    private static byte[]? HBitmapToPngBytes(IntPtr hBitmap)
    {
        try
        {
            if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            var width = bm.bmWidth;
            var height = Math.Abs(bm.bmHeight);
            if (width <= 0 || height <= 0) return null;

            var stride = ((width * 32 + 31) / 32) * 4;
            var pixels = new byte[stride * height];
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // 负高度 = 自上而下的行序（与 GDI+ 一致）
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;
            bmi.bmiHeader.biSizeImage = (uint)pixels.Length;

            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(screenDc, hBitmap, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            // alpha 修复：扫描所有像素的 alpha 字节，全为 0 → 置为不透明
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }
            if (!hasAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }

            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);

            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            LogExtract($"hbitmap convert exception: {ex.Message}");
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

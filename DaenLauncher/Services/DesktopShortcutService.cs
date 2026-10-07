using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 桌面快捷方式创建服务（需求：项目右键"创建桌面快捷方式"，在桌面生成快捷方式）。
/// 分类型处理：
/// - Exe/Lnk/File/Folder：WScript.Shell COM 创建 .lnk（与 LnkResolver 同款反射方式，编组可靠）
/// - Url/Protocol：写 .url 文件（InternetShortcut INI 格式，系统的"网址快捷方式"就是这种文件）
/// - Uwp：IShellLinkW.SetIDList（SHParseDisplayName 解析 shell:AppsFolder\应用ID 得到 pidl，
///   这是给商店应用建快捷方式的标准做法，快捷方式会自动带上应用图标）；
///   失败时回退为 explorer.exe 中转启动的 .lnk
/// </summary>
public static class DesktopShortcutService
{
    /// <summary>文件名兜底值（项目名清掉非法字符后为空时使用，仅作文件名）</summary>
    private const string FallbackFileName = "Shortcut";

    /// <summary>文件名最大长度（超长截断，避免触发 Windows 路径长度限制）</summary>
    private const int MaxFileNameLength = 80;

    /// <summary>
    /// 在当前用户的桌面创建项目快捷方式。返回是否成功（成功时桌面会出现对应的 .lnk/.url）。
    /// </summary>
    public static bool CreateOnDesktop(LauncherItem item)
    {
        // WScript.Shell / ShellLink 都是 COM 组件，统一放到 STA 线程执行（与 LnkResolver 同款守卫）
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return CreateCore(item);

        var result = false;
        var thread = new Thread(() => result = CreateCore(item));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static bool CreateCore(LauncherItem item)
    {
        try
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Name))
            {
                return false;
            }

            // 桌面目录（GetFolderPath 会正确处理 OneDrive 重定向的桌面）
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) return false;

            var fileName = SanitizeFileName(item.Name);
            switch (item.Type)
            {
                case LauncherItemType.Url:
                case LauncherItemType.Protocol:
                    // 网址/协议：创建 .url 网址快捷方式
                    return WriteInternetShortcut(GetUniquePath(desktop, fileName, ".url"), item.Path);

                case LauncherItemType.Uwp:
                {
                    // UWP/商店应用：pidl 快捷方式（自动带应用图标），失败退回 explorer 中转
                    var lnkPath = GetUniquePath(desktop, fileName, ".lnk");
                    return CreateUwpLnk(item.Path, lnkPath) || CreateExplorerFallbackLnk(item.Path, lnkPath);
                }

                default:
                    // Exe/Lnk/File/Folder：普通 .lnk 快捷方式
                    return CreateLnkViaWScript(item.Path, item.Arguments ?? "",
                        GetUniquePath(desktop, fileName, ".lnk"));
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 WScript.Shell 创建 .lnk（TargetPath/Arguments/WorkingDirectory 由 COM 正确编组）</summary>
    private static bool CreateLnkViaWScript(string targetPath, string arguments, string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            shortcut = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            if (shortcut == null) return false;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                new object[] { targetPath });

            if (!string.IsNullOrEmpty(arguments))
            {
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { arguments });
            }

            // 工作目录设为目标所在目录（很多程序依赖工作目录找配置/资源）
            var workingDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            {
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { workingDir });
            }

            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            TryReleaseComObject(shortcut);
            TryReleaseComObject(shell);
        }
    }

    /// <summary>
    /// 给 UWP 应用创建 .lnk：把 shell:AppsFolder\应用ID 解析成 pidl 后 SetIDList 保存。
    /// 这是"从开始菜单把应用拖到桌面"时系统用的同款方式，快捷方式自动带应用图标。
    /// </summary>
    private static bool CreateUwpLnk(string uwpPath, string shortcutPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            // 1) 直接解析项目存储的路径（shell:AppsFolder\应用ID）
            var hr = SHParseDisplayName(uwpPath, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                // 2) 兜底：换成 "::{AppsFolder GUID}\应用ID" 的解析名形式再试
                var appId = uwpPath.StartsWith(LauncherItemPaths.UwpPrefix, StringComparison.OrdinalIgnoreCase)
                    ? uwpPath[LauncherItemPaths.UwpPrefix.Length..]
                    : uwpPath;
                pidl = IntPtr.Zero;
                hr = SHParseDisplayName($"{ShellConstants.AppsFolderMoniker}\\{appId}",
                    IntPtr.Zero, out pidl, 0, out _);
                if (hr != 0 || pidl == IntPtr.Zero) return false;
            }

            var link = (LnkResolver.IShellLinkW)new LnkResolver.ShellLink();
            link.SetIDList(pidl);
            ((LnkResolver.IPersistFile)link).Save(shortcutPath, fRemember: true);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>UWP 快捷方式的兜底方案：用 explorer.exe 中转启动 shell:AppsFolder 路径</summary>
    private static bool CreateExplorerFallbackLnk(string uwpPath, string shortcutPath)
    {
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer)) return false;
        return CreateLnkViaWScript(explorer, $"\"{uwpPath}\"", shortcutPath);
    }

    /// <summary>写 .url 网址快捷方式（InternetShortcut INI 格式）。
    /// 先转成规范绝对 URI，非 ASCII 字符自动百分号转义（.url 按 ANSI 解析更稳）。</summary>
    private static bool WriteInternetShortcut(string shortcutPath, string url)
    {
        try
        {
            var normalized = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                normalized = uri.AbsoluteUri;
            }
            var content = "[InternetShortcut]" + Environment.NewLine +
                          "URL=" + normalized + Environment.NewLine;
            File.WriteAllText(shortcutPath, content);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清理文件名非法字符 + 截断长度，空结果用兜底名</summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (Array.IndexOf(invalidChars, c) < 0) builder.Append(c);
        }
        var result = builder.ToString().Trim();
        if (result.Length > MaxFileNameLength) result = result[..MaxFileNameLength].Trim();
        return string.IsNullOrWhiteSpace(result) ? FallbackFileName : result;
    }

    /// <summary>桌面路径不覆盖同名文件：存在时追加" (2)"、" (3)"…</summary>
    private static string GetUniquePath(string directory, string fileName, string extension)
    {
        var candidate = Path.Combine(directory, fileName + extension);
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName} ({counter}){extension}");
            counter++;
        }
        return candidate;
    }

    private static void TryReleaseComObject(object? obj)
    {
        try
        {
            if (obj != null && Marshal.IsComObject(obj))
            {
                Marshal.ReleaseComObject(obj);
            }
        }
        catch
        {
            // 释放失败忽略
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);
}

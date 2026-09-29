using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DaenLauncher.Services;

/// <summary>
/// .lnk 快捷方式解析器。
/// 需求：拖拽的是软件快捷方式时，解析出完整路径使用；网址快捷方式解析出 url 使用。
/// 实现：优先用 WScript.Shell COM（成熟可靠，避免手写 IShellLink 互操作的编码问题），
/// 不可用时回退到手写 IShellLinkW；.url 文件按 INI 文本解析。
/// </summary>
public static class LnkResolver
{
    #region 手写 IShellLinkW 互操作（回退方案）

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        void GetWorkingDirectory([Out] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private const uint STGM_READ = 0x00000000;

    #endregion

    /// <summary>
    /// 解析 .lnk 文件。返回 (目标路径或URL, 命令行参数)；解析失败返回 null。
    /// </summary>
    public static (string Target, string Arguments)? Resolve(string lnkPath)
    {
        // WScript.Shell 是单元线程对象，确保在 STA 线程上调用
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ResolveCore(lnkPath);
        }

        (string, string)? result = null;
        var thread = new Thread(() => { result = ResolveCore(lnkPath); });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static (string Target, string Arguments)? ResolveCore(string lnkPath)
    {
        // 1) WScript.Shell（首选，可靠）
        var viaWScript = ResolveViaWScript(lnkPath);
        if (viaWScript != null) return viaWScript;

        // 2) 手写 IShellLinkW（回退）
        var viaShellLink = ResolveViaShellLink(lnkPath);
        if (viaShellLink != null) return viaShellLink;

        // 3) .url 文本解析（兜底）
        var url = TryReadUrlFile(lnkPath);
        if (url != null) return (url, "");

        return null;
    }

    /// <summary>用 WScript.Shell COM 解析（TargetPath/Arguments 由系统正确编组，无乱码）</summary>
    private static (string Target, string Arguments)? ResolveViaWScript(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            shortcut = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) return null;

            var target = (shortcut.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, shortcut, null) as string)?.Trim() ?? "";

            var arguments = (shortcut.GetType().InvokeMember("Arguments",
                BindingFlags.GetProperty, null, shortcut, null) as string)?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(target)) return null;
            return (target, arguments);
        }
        catch
        {
            return null;
        }
        finally
        {
            TryReleaseComObject(shortcut);
            TryReleaseComObject(shell);
        }
    }

    /// <summary>手写 IShellLinkW 解析（回退方案）</summary>
    private static (string Target, string Arguments)? ResolveViaShellLink(string lnkPath)
    {
        try
        {
            var shellLink = (IShellLinkW)new ShellLink();
            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(lnkPath, STGM_READ);

            var pathBuffer = new StringBuilder(1040);
            shellLink.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, 0);
            var target = pathBuffer.ToString().TrimEnd('\0');

            var argsBuffer = new StringBuilder(1024);
            shellLink.GetArguments(argsBuffer, argsBuffer.Capacity);
            var arguments = argsBuffer.ToString().TrimEnd('\0');

            if (string.IsNullOrWhiteSpace(target)) return null;
            return (target, arguments);
        }
        catch
        {
            return null;
        }
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

    /// <summary>解析 .url 文件（INI 格式，[InternetShortcut] URL=...）</summary>
    public static string? TryReadUrlFile(string filePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = trimmed[4..].Trim();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }
        }
        catch
        {
            // 读取失败
        }
        return null;
    }
}

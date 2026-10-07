using System.Runtime.InteropServices;
using System.Text;

namespace DaenLauncher.Services;

/// <summary>
/// 系统 Toast 通知服务（解包应用发 Windows 通知）。
/// 原理：解包应用发通知需要一个已注册的 AUMID（Application User Model ID），
/// 这里用两层保障：
/// ① 注册表 HKCU\Software\Classes\AppUserModelId\&lt;AUMID&gt; 注册显示名和图标；
/// ② 在用户开始菜单创建带 AUMID 属性的快捷方式（发通知的标准可靠做法，
///    缺失时自动重建，用户删掉也不影响壁纸功能本身）。
/// 通知模板 ToastGeneric：大图（hero）+ 标题 + 日期 + 描述。
/// </summary>
public static class ToastService
{
    /// <summary>通知用的 AUMID（Company.Product 形式，固定常量）</summary>
    private const string Aumid = "DaenMax.DaenLauncher";

    /// <summary>开始菜单快捷方式文件名（带 AUMID 属性，通知身份用）</summary>
    private const string ShortcutFileName = "Daen Launcher.lnk";

    /// <summary>快捷方式确保标志：进程内只创建一次，避免重复 IO</summary>
    private static bool _aumidEnsured;

    /// <summary>
    /// 发"壁纸已更换"通知：大图 = 壁纸文件，文字 = 标题 / 日期 / 图片描述。
    /// 任何失败（通知被系统策略禁用等）只写日志，绝不影响换壁纸主流程。
    /// </summary>
    public static void ShowWallpaperChanged(string title, string dateText, string copyright, string imagePath)
    {
        try
        {
            EnsureAumidRegistered();

            // 文件路径转 file:/// URI（Toast 的图片必须用 URI，中文/空格/井号会自动转义）
            var imageUri = new Uri(imagePath).AbsoluteUri;

            // ToastGeneric 模板：hero 大图铺顶部，下面三行文字
            var xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<image placement=\"hero\" src=\"{EscapeXmlAttribute(imageUri)}\"/>" +
                $"<text>{EscapeXml(title)}</text>" +
                $"<text>{EscapeXml(dateText)}</text>" +
                $"<text>{EscapeXml(copyright)}</text>" +
                "</binding></visual>" +
                "</toast>";

            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            var toast = new Windows.UI.Notifications.ToastNotification(doc);
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
        }
    }

    /// <summary>注册 AUMID（注册表 + 开始菜单快捷方式，幂等可重复调用）</summary>
    private static void EnsureAumidRegistered()
    {
        // ① 注册表注册：显示名 + 图标（部分 Windows 版本仅凭此即可发通知）
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + Aumid);
            key.SetValue("DisplayName", AppInfoService.Config.AppName,
                Microsoft.Win32.RegistryValueKind.String);
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue("IconUri", exePath, Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch
        {
            // 注册表不可写不阻塞，还有快捷方式那条路
        }

        // ② 开始菜单快捷方式（带 AUMID 属性，已存在则跳过；进程内只检查一次）
        if (_aumidEnsured)
        {
            return;
        }
        _aumidEnsured = true;
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }
            var programsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(programsDir);
            var shortcutPath = Path.Combine(programsDir, ShortcutFileName);
            if (!File.Exists(shortcutPath))
            {
                ShortcutHelper.CreateShortcutWithAumid(shortcutPath, exePath, Aumid);
            }
        }
        catch
        {
            // 快捷方式创建失败不阻塞，还有注册表那条路
        }
    }

    /// <summary>XML 文本转义（copyright 里可能带 &amp; &lt; 引号等字符）</summary>
    private static string EscapeXml(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>XML 属性值转义（同上，单引号也转义）</summary>
    private static string EscapeXmlAttribute(string text) => EscapeXml(text).Replace("'", "&apos;");

    /// <summary>写日志到 data\crash.log（通知失败排查用，不影响界面）</summary>
    private static void WriteLog(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataPathService.DataRoot, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Toast: {message}\n\n");
        }
        catch { /* 日志写不进去就忽略 */ }
    }
}

/// <summary>
/// 快捷方式创建（IShellLink + IPropertyStore 设置 System.AppUserModel.ID 属性）。
/// WScript.Shell 做不了"给快捷方式设 AUMID"，必须走 COM 属性存储。
/// </summary>
internal static class ShortcutHelper
{
    /// <summary>PKEY_AppUserModel_ID（快捷方式上登记 AUMID 的属性键）</summary>
    private static readonly PROPERTYKEY PKeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    /// <summary>VT_LPWSTR（PROPVARIANT 里表示"以 NUL 结尾的宽字符串"的类型码）</summary>
    private const ushort VtLpwstr = 31;

    /// <summary>创建 .lnk 快捷方式并在其属性里写入 AUMID</summary>
    public static void CreateShortcutWithAumid(string shortcutPath, string targetPath, string aumid)
    {
        var persistFile = (IPersistFile)new ShellLinkClass();
        var shellLink = (IShellLinkW)persistFile;
        shellLink.SetPath(targetPath);
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");

        // 把 AUMID 写进快捷方式的属性存储（通知身份的关键一步）
        var store = (IPropertyStore)shellLink;
        var key = PKeyAppUserModelId;
        var value = new PROPVARIANT { vt = VtLpwstr };
        var stringPointer = IntPtr.Zero;
        try
        {
            stringPointer = Marshal.StringToCoTaskMemUni(aumid);
            value.pointerValue = stringPointer;
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            if (stringPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(stringPointer);
            }
        }

        persistFile.Save(shortcutPath, fRemember: true);
    }

    // ===== COM 互操作声明（IShellLinkW / IPersistFile / IPropertyStore 标准样板） =====

    /// <summary>ShellLink COM 组件类（CLSID_ShellLink；不能标 sealed，否则到接口的 cast 无法编译）</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// 最小化 PROPVARIANT：只用到 VT_LPWSTR（指针在偏移 8）。
    /// 尾部补到 24 字节（x64 上 PROPVARIANT 的真实大小），传给 COM 才安全。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr pointerValue;

        [FieldOffset(16)]
        public long tailPadding;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out IntPtr pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }
}

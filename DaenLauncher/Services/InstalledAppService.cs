using System.Runtime.InteropServices;
using System.Text;

namespace DaenLauncher.Services;

/// <summary>
/// 系统 UWP/微软商店应用枚举服务（需求：添加项目-UWP 应用时，列出当前系统安装的商店应用）。
/// 原理：枚举 shell 命名空间的"应用程序"文件夹（FOLDERID_AppsFolder，即 shell:AppsFolder），
/// 每个应用的"解析名"就是 AUMID（Application User Model ID，形如
/// B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App），恰好与项目存储格式 shell:AppsFolder\AUMID
/// 对应，启动和创建快捷方式都能直接使用。
/// 过滤规则：解析名含 "!" 才是 UWP/商店应用（AUMID 固定为"包家族名!应用ID"格式；
/// 普通桌面程序虽然也出现在 AppsFolder 里，但解析名没有 "!"）。
/// </summary>
public static class InstalledAppService
{
    /// <summary>单个已安装应用（显示名 + AUMID + 列表图标）。
    /// 图标由"添加项目"选择器异步加载，加载完通过 PropertyChanged 通知列表行刷新。</summary>
    public sealed class InstalledUwpApp : System.ComponentModel.INotifyPropertyChanged
    {
        public InstalledUwpApp(string name, string aumid)
        {
            Name = name;
            Aumid = aumid;
        }

        /// <summary>显示名</summary>
        public string Name { get; }

        /// <summary>应用 ID（AUMID，形如 包家族名!应用ID）</summary>
        public string Aumid { get; }

        private Microsoft.UI.Xaml.Media.ImageSource? _iconSource;

        /// <summary>列表行图标（后台加载完成后设置，UI 自动刷新）</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? IconSource
        {
            get => _iconSource;
            set
            {
                _iconSource = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(IconSource)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>进程内缓存：应用列表在系统装/卸软件前不会变，避免每次打开选择器都重新枚举</summary>
    private static List<InstalledUwpApp>? _cache;

    /// <summary>
    /// 获取系统安装的 UWP/商店应用列表（显示名升序）。结果进程内缓存。
    /// </summary>
    public static async Task<List<InstalledUwpApp>> GetInstalledAppsAsync()
    {
        if (_cache != null) return _cache;

        // Shell COM 枚举放到 STA 线程执行（与 LnkResolver/ItemIconService 同款守卫），不阻塞 UI
        var list = await Task.Run(() => RunOnStaThread(EnumerateCore));

        // 去重 + 按显示名排序（忽略大小写，符合文件列表的常规排序习惯）
        list = list
            .DistinctBy(a => a.Aumid, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _cache = list;
        return list;
    }

    /// <summary>枚举 AppsFolder 子项。任何失败都返回已收集到的部分（或空列表）。</summary>
    private static List<InstalledUwpApp> EnumerateCore()
    {
        var result = new List<InstalledUwpApp>();

        try
        {
            // 1) 取 AppsFolder 的入口路径。多级回退（实测 FOLDERID_AppsFolder 在某些受限
            //    上下文会返回 E_FAIL，而 shell:AppsFolder 可以直接被 shell 解析器解析）：
            //    ① SHGetKnownFolderPath（标准 API，形如 "::{1e87508d-...}"）
            //    ② "shell:AppsFolder"（shell URI 形式）
            //    ③ "::{GUID}"（moniker 形式）
            string? appsFolderPath = null;
            var hr = SHGetKnownFolderPath(ShellConstants.AppsFolderGuid, 0, IntPtr.Zero, out var pathPtr);
            if (hr == 0 && pathPtr != IntPtr.Zero)
            {
                appsFolderPath = Marshal.PtrToStringUni(pathPtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
            appsFolderPath ??= TryParseEntryPath("shell:AppsFolder")
                            ?? TryParseEntryPath(ShellConstants.AppsFolderMoniker);
            if (string.IsNullOrEmpty(appsFolderPath)) return result;

            // 2) 解析为 pidl，并绑定到它的 IShellFolder 接口
            hr = SHParseDisplayName(appsFolderPath, IntPtr.Zero, out var pidlRoot, 0, out _);
            if (hr != 0 || pidlRoot == IntPtr.Zero) return result;

            try
            {
                hr = SHBindToObject(IntPtr.Zero, pidlRoot, IntPtr.Zero, ShellFolderIid, out var folder);
                if (hr != 0 || folder == null) return result;

                // 3) 枚举所有子项
                folder.EnumObjects(IntPtr.Zero,
                    ShellConstants.ShContfFolders | ShellConstants.ShContfNonFolders,
                    out var enumIds);
                if (enumIds == null) return result;

                while (enumIds.Next(1, out var pidl, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var parsingName = GetDisplayName(folder, pidl, ShellConstants.ShGdnForParsing);
                        // 解析名含 "!" 才是 UWP/商店应用（桌面程序没有）
                        if (string.IsNullOrEmpty(parsingName) || !parsingName.Contains('!')) continue;

                        var displayName = GetDisplayName(folder, pidl, ShellConstants.ShGdnNormal);
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = parsingName;
                        result.Add(new InstalledUwpApp(displayName, parsingName));
                    }
                    catch
                    {
                        // 单个子项解析失败直接跳过，不影响整体
                    }
                    finally
                    {
                        if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidlRoot);
            }
        }
        catch
        {
            // 枚举整体失败：返回已收集到的部分
        }

        return result;
    }

    /// <summary>验证一个 AppsFolder 入口路径能否被 shell 解析，可以则原样返回（后续再解析成 pidl）</summary>
    private static string? TryParseEntryPath(string entryPath)
    {
        var hr = SHParseDisplayName(entryPath, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return null;
        Marshal.FreeCoTaskMem(pidl);
        return entryPath;
    }

    /// <summary>取子项的显示名/解析名（STRRET 结构用 StrRetToBuf 转成字符串，内部会释放原字符串）</summary>
    private static string GetDisplayName(IShellFolder folder, IntPtr pidl, uint flags)
    {
        folder.GetDisplayNameOf(pidl, flags, out var strRet);
        var buffer = new StringBuilder(1040);
        StrRetToBuf(ref strRet, pidl, buffer, (uint)buffer.Capacity);
        return buffer.ToString().TrimEnd('\0');
    }

    /// <summary>当前线程不是 STA 时，把方法放到新建的 STA 线程上执行（Shell COM 组件的惯例要求）</summary>
    private static T RunOnStaThread<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return func();

        var result = default(T)!;
        var thread = new Thread(() => result = func());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    #region COM 互操作（IShellFolder / IEnumIDList / STRRET 标准样板）

    /// <summary>IShellFolder 接口的 GUID</summary>
    private static readonly Guid ShellFolderIid = new("000214E6-0000-0000-C000-000000000046");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, in Guid riidResult,
        out IShellFolder ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBuf(ref STRRET pstr, IntPtr pidl, StringBuilder pszBuf, uint cchBuf);

    /// <summary>shell 文件夹对象。注意：COM 接口声明必须与原生 vtable 完全一致（方法顺序、
    /// 数量都不能少），漏掉中间方法会把 GetDisplayNameOf 调到错误函数指针上直接崩溃。</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IEnumIDList ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, in Guid riidResult, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, in Guid riidResult, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, in Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwnd, uint cidl, IntPtr[] apidl, in Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    /// <summary>pidl 枚举器</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        void Reset();
        void Clone(out IEnumIDList ppenum);
    }

    /// <summary>GetDisplayNameOf 的返回结构（uType 后是联合体，StrRetToBuf 负责转换）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct STRRET
    {
        public uint uType;
        public IntPtr pOleStr;
    }

    #endregion
}

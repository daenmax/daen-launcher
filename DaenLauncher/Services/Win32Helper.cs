using System.Runtime.InteropServices;

namespace DaenLauncher.Services;

/// <summary>
/// Win32 API 封装：窗口子类化（处理 WM_HOTKEY 等）、隐藏系统标题栏按钮、光标位置等。
/// </summary>
public static class Win32Helper
{
    #region 常量

    public const int GWL_STYLE = -16;
    public const long WS_SYSMENU = 0x00080000;
    public const long WS_CAPTION = 0x00C00000;

    public const int WM_HOTKEY = 0x0312;

    // RegisterHotKey 修饰键
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_WIN = 0x0008;
    public const int MOD_NOREPEAT = 0x4000;

    #endregion

    #region P/Invoke

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        uint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    public static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    public const uint WM_SETICON = 0x0080;
    public const uint WM_GETICON = 0x007F;
    public static readonly IntPtr ICON_SMALL = IntPtr.Zero;
    public const IntPtr ICON_BIG = (IntPtr)1;

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    #endregion

    public delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, nint wParam, nint lParam,
        nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>热键回调：收到 WM_HOTKEY 时触发</summary>
    public static event Action? HotkeyPressed;

    private static readonly Dictionary<long, SUBCLASSPROC> SubclassProcs = new();
    private static long _nextSubclassId = 1;

    /// <summary>
    /// 子类化窗口，拦截消息。返回拦截器 id（用于移除）。
    /// </summary>
    public static long SubclassWindow(IntPtr hWnd, Func<uint, nint, nint, bool> handler)
    {
        var id = _nextSubclassId++;
        SUBCLASSPROC proc = (hWnd2, uMsg, wParam, lParam, uIdSubclass, dwRefData) =>
        {
            if (handler(uMsg, wParam, lParam)) return IntPtr.Zero; // 已处理
            return DefSubclassProc(hWnd2, uMsg, wParam, lParam);
        };
        SubclassProcs[id] = proc; // 防止委托被 GC
        SetWindowSubclass(hWnd, proc, (uint)id, 0);
        return id;
    }

    /// <summary>移除子类化</summary>
    public static void UnsubclassWindow(IntPtr hWnd, long id)
    {
        if (SubclassProcs.Remove(id, out var proc))
        {
            RemoveWindowSubclass(hWnd, proc, (uint)id);
        }
    }

    /// <summary>注册全局热键（WM_HOTKEY 发到 hWnd）</summary>
    public static bool RegisterHotKeyId(IntPtr hWnd, int id, uint modifiers, uint vk)
    {
        return RegisterHotKey(hWnd, id, modifiers, vk);
    }

    /// <summary>注销全局热键</summary>
    public static void UnregisterHotKeyId(IntPtr hWnd, int id)
    {
        UnregisterHotKey(hWnd, id);
    }

    /// <summary>
    /// 隐藏系统标题栏的最小化/最大化/关闭按钮（去掉 WS_SYSMENU）。
    /// 用于实现"自绘按钮"的需求（需求-主窗口布局1）。
    /// </summary>
    public static void HideCaptionButtons(IntPtr hWnd)
    {
        var style = GetWindowLongPtr(hWnd, GWL_STYLE);
        style &= ~WS_SYSMENU;
        SetWindowLongPtr(hWnd, GWL_STYLE, style);
    }

    /// <summary>处理 WM_HOTKEY 消息（供子类化处理器调用）</summary>
    public static bool HandleHotkeyMessage(uint uMsg)
    {
        if (uMsg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke();
            return true;
        }
        return false;
    }
}

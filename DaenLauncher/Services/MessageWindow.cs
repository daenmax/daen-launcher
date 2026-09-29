using System.Runtime.InteropServices;

namespace DaenLauncher.Services;

/// <summary>
/// 经典 Win32 消息窗口：用于接收全局热键 WM_HOTKEY。
/// 注意：WinUI 3 的窗口过程无法用 comctl32 子类化可靠拦截 WM_HOTKEY，
/// 所以注册热键到自己创建的传统窗口上，收到消息后切回 UI 线程处理。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "DaenLauncher_HotkeyWnd";

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private WndProcDelegate? _proc; // 保活，防止被 GC
    private IntPtr _hwnd;

    /// <summary>收到消息（在创建线程即 UI 线程上触发）</summary>
    public event Action<uint, IntPtr, IntPtr>? MessageReceived;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int w, int h,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public IntPtr Handle => _hwnd;

    /// <summary>创建消息窗口（必须在 UI 线程调用）</summary>
    public void Create()
    {
        if (_hwnd != IntPtr.Zero) return;

        _proc = WndProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName
        };
        RegisterClassW(ref wc);

        // HWND_MESSAGE：message-only 窗口，不显示、不进任务栏
        _hwnd = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        MessageReceived?.Invoke(msg, wParam, lParam);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}

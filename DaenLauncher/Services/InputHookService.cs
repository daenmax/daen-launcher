using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DaenLauncher.Services;

/// <summary>
/// 全局输入钩子服务（低级钩子 WH_MOUSE_LL / WH_KEYBOARD_LL）：
/// 用于实现"显示和隐藏"触发方式（需求-启动器2）：
/// - 鼠标侧键（后退/前进）单击
/// - 按 Ctrl 键两次 / 按 Alt 键两次
/// 注意：钩子回调必须快速返回，处理逻辑都调度到主线程。
/// </summary>
public sealed class InputHookService : IDisposable
{
    #region P/Invoke

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion

    /// <summary>触发显示/隐藏切换的回调（主线程）</summary>
    public event Action? ToggleRequested;

    // 双击检测阈值（毫秒）与按键
    private const int DoubleTapWindowMs = 500;
    // 注意：低级键盘钩子收到的是左/右区分的键码，不是通用键码
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private LowLevelHookProc? _mouseProc;
    private LowLevelHookProc? _keyboardProc;

    // 双击检测状态
    private uint _lastCtrlTime;
    private uint _lastAltTime;

    private uint LastInputTick => (uint)Environment.TickCount;

    /// <summary>根据设置更新钩子（只装需要的钩子）</summary>
    public void ApplySettings(DaenLauncher.Models.AppSettings settings)
    {
        bool needMouseHook = settings.TriggerSideButton || settings.TriggerMiddleButton;
        bool needKeyboardHook = settings.TriggerCtrlTwice || settings.TriggerAltTwice;

        if (needMouseHook && _mouseHook == IntPtr.Zero) InstallMouseHook();
        if (!needMouseHook && _mouseHook != IntPtr.Zero) RemoveMouseHook();

        if (needKeyboardHook && _keyboardHook == IntPtr.Zero) InstallKeyboardHook();
        if (!needKeyboardHook && _keyboardHook != IntPtr.Zero) RemoveKeyboardHook();
    }

    private void InstallMouseHook()
    {
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private void InstallKeyboardHook()
    {
        _keyboardProc = KeyboardHookProc;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && App.UiDispatcherQueue != null)
        {
            var msg = wParam.ToInt32();
            var settings = SettingsService.Instance.Settings;
            bool hit = false;

            if (msg == WM_XBUTTONDOWN && settings.TriggerSideButton)
            {
                // XButton1=后退(0x0001)，XButton2=前进(0x0002)，存于 mouseData 高16位
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var button = (data.mouseData >> 16) & 0xFFFF;
                bool isBack = button == 0x0001;
                bool isForward = button == 0x0002;
                if (settings.SideButton == DaenLauncher.Models.SideMouseButton.Back && isBack) hit = true;
                if (settings.SideButton == DaenLauncher.Models.SideMouseButton.Forward && isForward) hit = true;
            }
            else if (msg == WM_MBUTTONDOWN && settings.TriggerMiddleButton)
            {
                hit = true;
            }

            if (hit)
            {
                App.UiDispatcherQueue.TryEnqueue(() => ToggleRequested?.Invoke());
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && App.UiDispatcherQueue != null)
        {
            var msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var settings = SettingsService.Instance.Settings;
                bool hit = false;

                if ((data.vkCode == VK_CONTROL || data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL)
                    && settings.TriggerCtrlTwice)
                {
                    var now = LastInputTick;
                    if (now - _lastCtrlTime <= DoubleTapWindowMs && _lastCtrlTime != 0) hit = true;
                    _lastCtrlTime = now;
                }
                else if ((data.vkCode == VK_MENU || data.vkCode == VK_LMENU || data.vkCode == VK_RMENU)
                    && settings.TriggerAltTwice)
                {
                    var now = LastInputTick;
                    if (now - _lastAltTime <= DoubleTapWindowMs && _lastAltTime != 0) hit = true;
                    _lastAltTime = now;
                }

                if (hit)
                {
                    // 重置，防止连续多次触发
                    _lastCtrlTime = 0;
                    _lastAltTime = 0;
                    App.UiDispatcherQueue.TryEnqueue(() => ToggleRequested?.Invoke());
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        RemoveMouseHook();
        RemoveKeyboardHook();
    }
}

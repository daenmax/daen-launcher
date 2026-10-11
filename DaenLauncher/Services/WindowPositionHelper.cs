using DaenLauncher.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DaenLauncher.Services;

/// <summary>
/// 窗口显示位置计算（主窗口 + 全部附属功能窗口共用，禁止各窗口各写一套）。
///
/// 关键点（多显示器的坑）：
/// 1) 虚拟桌面坐标系里，副屏在主屏左侧/上方时坐标是负数 —— 判断"是否记录过位置"
///    必须用哨兵值 <see cref="AppSettings.WindowPositionNotSet"/>，不能写 x &gt;= 0；
/// 2) 钳制（防止超出屏幕）必须用"目标坐标所在那块显示器"的工作区，
///    用窗口当前所在显示器会把副屏坐标硬拉回主屏，表现为"永远回到主屏中央"；
/// 3) 各显示器缩放可能不同，尺寸换算要用目标显示器的 DPI。
/// </summary>
public static class WindowPositionHelper
{
    /// <summary>
    /// 按显示策略计算左上角坐标（物理像素）。
    /// </summary>
    /// <param name="windowHandle">窗口句柄（用于定位"窗口当前所在显示器"）</param>
    /// <param name="showPosition">显示位置策略</param>
    /// <param name="logicalWidth">窗口宽度（逻辑像素，来自设置）</param>
    /// <param name="logicalHeight">窗口高度（逻辑像素，来自设置）</param>
    /// <param name="lastX">上次位置 X（物理像素；哨兵值 = 未记录）</param>
    /// <param name="lastY">上次位置 Y（物理像素；哨兵值 = 未记录）</param>
    public static PointInt32 Compute(IntPtr windowHandle, ShowPosition showPosition,
        double logicalWidth, double logicalHeight, int lastX, int lastY)
    {
        // "上次位置"：先按记录的坐标找到那块显示器，再按它的工作区/缩放钳制与换算
        if (showPosition == ShowPosition.LastPosition &&
            lastX != AppSettings.WindowPositionNotSet &&
            lastY != AppSettings.WindowPositionNotSet)
        {
            return AtLastLocation(logicalWidth, logicalHeight, lastX, lastY);
        }

        // 其余策略都以"窗口当前所在显示器"为基准（首次显示时即主显示器）
        var area = CurrentWorkArea(windowHandle);
        var scale = GetWindowDpiScale(windowHandle);
        var width = (int)(logicalWidth * scale);
        var height = (int)(logicalHeight * scale);

        return showPosition switch
        {
            ShowPosition.TopLeft => new PointInt32(area.X, area.Y),
            ShowPosition.TopRight => new PointInt32(area.X + area.Width - width, area.Y),
            ShowPosition.BottomLeft => new PointInt32(area.X, area.Y + area.Height - height),
            ShowPosition.BottomRight => new PointInt32(area.X + area.Width - width, area.Y + area.Height - height),
            ShowPosition.FollowMouse => FollowMouse(area, width, height),
            // Center 以及"上次位置但从未记录过"：都退回居中
            _ => Center(area, width, height)
        };
    }

    /// <summary>在"上次位置"坐标所在的显示器上恢复位置（负数坐标、跨屏缩放都正确）</summary>
    private static PointInt32 AtLastLocation(double logicalWidth, double logicalHeight, int lastX, int lastY)
    {
        var target = DisplayArea.GetFromPoint(new PointInt32(lastX, lastY), DisplayAreaFallback.Nearest);
        var area = target.WorkArea;

        // 用目标显示器的缩放换算尺寸（各屏缩放可能不同）
        var scale = Win32Helper.GetDpiScaleForPoint(lastX, lastY);
        var width = (int)(logicalWidth * scale);
        var height = (int)(logicalHeight * scale);

        // 屏幕被拔掉/分辨率变化时钳制回该显示器工作区内
        return new PointInt32(
            Math.Clamp(lastX, area.X, Math.Max(area.X, area.X + area.Width - width)),
            Math.Clamp(lastY, area.Y, Math.Max(area.Y, area.Y + area.Height - height)));
    }

    /// <summary>跟随鼠标：以鼠标为中心，并防止超出鼠标所在显示器的工作区</summary>
    private static PointInt32 FollowMouse(Windows.Graphics.RectInt32 fallbackArea, int width, int height)
    {
        Win32Helper.GetCursorPos(out var cursor);
        // 鼠标可能在任何一块屏幕上，按鼠标所在显示器的工作区钳制
        var area = DisplayArea.GetFromPoint(
            new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;

        var x = cursor.X - width / 2;
        var y = cursor.Y - height / 2;
        return new PointInt32(
            Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height)));
    }

    private static PointInt32 Center(Windows.Graphics.RectInt32 area, int width, int height)
    {
        return new PointInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2);
    }

    /// <summary>窗口当前所在显示器的工作区</summary>
    private static Windows.Graphics.RectInt32 CurrentWorkArea(IntPtr windowHandle)
    {
        return DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle),
            DisplayAreaFallback.Nearest).WorkArea;
    }

    /// <summary>窗口当前所在显示器的缩放系数（窗口未移动时即主显示器）</summary>
    private static double GetWindowDpiScale(IntPtr windowHandle)
    {
        try
        {
            var dpi = Win32Helper.GetDpiForWindow(windowHandle);
            return dpi <= 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 按记忆的"窗口客户区逻辑尺寸"恢复窗口大小（主窗口/设置窗口/随手记窗口共用）。
    ///
    /// 为什么要在这里换算（用户实测：手动调宽 991 → 重启变 975）：
    /// - Window.SizeChanged 保存的是 **XAML 客户区** 的逻辑像素尺寸；
    /// - AppWindow.Resize 设置的是 **含边框的外框** 的物理像素尺寸；
    /// - 两者相差一个窗口边框（100% 缩放下宽 16px、高 8px），把保存值直接传给 Resize，
    ///   每次重启窗口都会缩小一个边框差。这里补上边框差并换算 DPI，保存什么尺寸就恢复什么尺寸。
    /// </summary>
    public static void ApplyClientSize(AppWindow appWindow, IntPtr windowHandle,
        double logicalWidth, double logicalHeight)
    {
        var scale = GetWindowDpiScale(windowHandle);

        // 边框差 = 外框尺寸 − 客户区尺寸（按当前窗口实测，不同主题/缩放下值不同）
        var frameDx = 0;
        var frameDy = 0;
        try
        {
            var okOuter = Win32Helper.GetWindowRect(windowHandle, out var outer);
            var okClient = Win32Helper.GetClientRect(windowHandle, out var client);
            if (okOuter && okClient)
            {
                frameDx = outer.Right - outer.Left - (client.Right - client.Left);
                frameDy = outer.Bottom - outer.Top - (client.Bottom - client.Top);
            }
        }
        catch
        {
            // 量不出边框差就按 0 处理（退化为旧行为，比给一个离谱值安全）
        }

        // 边框差是物理像素，正常恒 ≥ 0；防御负值（量测异常时按 0）
        if (frameDx < 0) frameDx = 0;
        if (frameDy < 0) frameDy = 0;

        var width = (int)Math.Round(logicalWidth * scale) + frameDx;
        var height = (int)Math.Round(logicalHeight * scale) + frameDy;
        appWindow.Resize(new SizeInt32(Math.Max(1, width), Math.Max(1, height)));
    }
}

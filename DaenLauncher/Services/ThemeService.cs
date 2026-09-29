using DaenLauncher.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher.Services;

/// <summary>
/// 窗口材质服务（需求-外观2）：亚克力、云母，切换后立即生效。
/// 使用 WinUI 3 现代的 SystemBackdrop 包装类。
/// </summary>
public static class BackdropService
{
    /// <summary>
    /// 给窗口应用材质。返回是否成功应用（应用成功后根元素背景必须清空，否则不透明背景会盖住材质）。
    /// </summary>
    public static bool Apply(Window window, BackdropKind kind)
    {
        try
        {
            switch (kind)
            {
                case BackdropKind.Mica:
                    if (MicaController.IsSupported())
                    {
                        window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base };
                        ClearRootBackground(window);
                        return true;
                    }
                    // 不支持云母就回退亚克力
                    goto case BackdropKind.Acrylic;

                case BackdropKind.Acrylic:
                    if (DesktopAcrylicController.IsSupported())
                    {
                        window.SystemBackdrop = new DesktopAcrylicBackdrop();
                        ClearRootBackground(window);
                        return true;
                    }
                    // 不支持就无材质
                    window.SystemBackdrop = null;
                    return false;
            }
        }
        catch
        {
            window.SystemBackdrop = null;
        }
        return false;
    }

    /// <summary>清除根元素的不透明背景，让材质透出来</summary>
    private static void ClearRootBackground(Window window)
    {
        if (window.Content is Microsoft.UI.Xaml.Controls.Panel root)
        {
            root.Background = null;
        }
    }
}

/// <summary>
/// 主题服务（需求-外观1）：跟随系统/浅色/深色，用 rootElement.RequestedTheme 实现，切换立即生效。
/// </summary>
public static class ThemeService
{
    /// <summary>把设置里的主题应用到窗口根元素</summary>
    public static void Apply(FrameworkElement rootElement, ThemeMode mode)
    {
        rootElement.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default // 跟随系统
        };
    }

    /// <summary>
    /// 给系统标题栏按钮（右上角）应用透明背景 + 按主题配色（参考 DeskBox）。
    /// 必须在 RequestedTheme 应用之后调用（读取 ActualTheme）。
    /// </summary>
    public static void ApplyCaptionButtonColors(Microsoft.UI.Windowing.AppWindow appWindow, FrameworkElement rootElement)
    {
        try
        {
            var isDark = rootElement.ActualTheme == ElementTheme.Dark;
            var titleBar = appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Microsoft.UI.ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            titleBar.ButtonPressedBackgroundColor = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Microsoft.UI.ColorHelper.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            titleBar.ButtonInactiveForegroundColor =
                isDark ? Microsoft.UI.ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                       : Microsoft.UI.ColorHelper.FromArgb(0x80, 0x00, 0x00, 0x00);
        }
        catch
        {
            // 配色失败不影响功能
        }
    }
}

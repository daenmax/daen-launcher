using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DaenLauncher.Services;

/// <summary>
/// 窗口按键辅助：统一实现"按 Esc 隐藏窗口"（所有窗口都要支持，不需要设置项）。
///
/// 实现说明：
/// - 用 PreviewKeyDown（隧道事件）挂在窗口根元素上，保证焦点在输入框等控件里时也能收到；
/// - 若当前有弹出层（ComboBox 下拉、MenuFlyout、ContentDialog 等）打开，则跳过，
///   让弹出层自己用 Esc 关闭，避免"先隐藏了整个窗口"的意外行为。
/// </summary>
public static class WindowKeyHelper
{
    /// <summary>为窗口根元素启用 Esc 隐藏窗口</summary>
    /// <param name="root">窗口内容根元素（各窗口的 RootGrid）</param>
    /// <param name="hide">隐藏动作（一般写 () => appWindow.Hide()，需要落盘的窗口先落盘）</param>
    public static void EnableEscapeToHide(FrameworkElement root, Action hide)
    {
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;

            // 有弹出层时交给弹出层处理（下拉框/菜单/对话框的 Esc 语义是"关掉自己"）
            if (IsPopupOpen(root)) return;

            hide();
            e.Handled = true;
        }),
        handledEventsToo: true);
    }

    /// <summary>当前窗口的 XamlRoot 上是否有打开的弹出层</summary>
    public static bool IsPopupOpen(FrameworkElement root)
    {
        var xamlRoot = root.XamlRoot;
        if (xamlRoot == null) return false;

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            // 悬停提示（ToolTip）不算：它只是鼠标停留产生的，不应影响 Esc 隐藏窗口
            if (popup.Child is ToolTip) continue;
            return true;
        }
        return false;
    }
}

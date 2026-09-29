using System.Drawing;
using System.Reflection;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DaenLauncher.Services;

/// <summary>
/// 托盘服务（需求第3条）：
/// - 双击托盘图标：显示/隐藏主窗口；
/// - 右键托盘图标：WinUI 风格菜单（有动画）：显示/隐藏软件、设置、关于我们、退出；
/// - 点击设置打开设置窗口；点击关于我们打开设置窗口并选中"关于"页；点击退出完全退出。
/// 参考 DeskBox：用 1x1 隐藏窗口承载 TaskbarIcon，MenuFlyout 用代码构建。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int TrayMenuItemWidth = 160;
    private const string HostWindowClassName = "DaenLauncher_TrayHost";

    private Window? _hostWindow;
    private TaskbarIcon? _trayIcon;
    private MenuFlyout? _menuFlyout;

    /// <summary>图标创建重试计数</summary>
    private const int MaxIconCreateAttempts = 10;

    public void Initialize()
    {
        // 1x1 隐藏窗口作为托盘图标的 XAML 宿主
        _hostWindow = new Window();
        _hostWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        _hostWindow.AppWindow.IsShownInSwitchers = false;

        var grid = new Grid();
        _hostWindow.Content = grid;

        // 代码构建 WinUI 右键菜单（有系统自带的弹出动画）
        _menuFlyout = BuildMenu();
        _menuFlyout.ShouldConstrainToRootBounds = false;

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = App.GetDisplayTitle(),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            MenuActivation = PopupActivationMode.None, // 右键菜单手动显示，保证位置正确
            NoLeftClickDelay = true,
            ContextFlyout = _menuFlyout
        };

        _trayIcon.DoubleClickCommand = new RelayCommand(App.Instance.ToggleMainWindow);
        _trayIcon.RightClickCommand = new RelayCommand(ShowMenuAtCursor);

        grid.Children.Add(_trayIcon);
        _hostWindow.Activate();
        _hostWindow.AppWindow.Hide();

        // 托盘图标创建（开机自启时 explorer 可能还没就绪，重试）
        _ = CreateIconWithRetryAsync();
    }

    private async Task CreateIconWithRetryAsync()
    {
        var icon = LoadEmbeddedIcon();
        for (var attempt = 1; attempt <= MaxIconCreateAttempts; attempt++)
        {
            try
            {
                if (_trayIcon == null) return;
                if (icon != null) _trayIcon.Icon = icon;
                _trayIcon.ForceCreate();
                return;
            }
            catch
            {
                if (attempt == MaxIconCreateAttempts) return;
                await Task.Delay(2000);
            }
        }
    }

    /// <summary>从嵌入资源加载 ico 图标</summary>
    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("DaenLauncher.Assets.logo.logo_图标组.ico");
            if (stream == null) return null;
            return new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>构建托盘右键菜单（WinUI MenuFlyout，自带动画）</summary>
    private MenuFlyout BuildMenu()
    {
        var flyout = new MenuFlyout();
        var loc = LocalizationService.Instance;

        // 显示/隐藏软件
        var showHideItem = new MenuFlyoutItem
        {
            Text = loc.T("Tray.ShowHide"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "", FontSize = 16 } // View：眼睛
        };
        showHideItem.Click += (_, _) =>
        {
            _menuFlyout?.Hide();
            App.Instance.ToggleMainWindow();
        };
        flyout.Items.Add(showHideItem);

        // 设置
        var settingsItem = new MenuFlyoutItem
        {
            Text = loc.T("Tray.Settings"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "", FontSize = 16 } // Setting：齿轮
        };
        settingsItem.Click += (_, _) =>
        {
            _menuFlyout?.Hide();
            App.Instance.ShowSettingsWindow();
        };
        flyout.Items.Add(settingsItem);

        // 关于我们
        var aboutItem = new MenuFlyoutItem
        {
            Text = loc.T("Tray.About"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "", FontSize = 16 } // Info：信息
        };
        aboutItem.Click += (_, _) =>
        {
            _menuFlyout?.Hide();
            App.Instance.ShowAboutPage();
        };
        flyout.Items.Add(aboutItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // 退出
        var exitItem = new MenuFlyoutItem
        {
            Text = loc.T("Tray.Exit"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "", FontSize = 16 } // PowerButton：电源
        };
        exitItem.Click += (_, _) =>
        {
            _menuFlyout?.Hide();
            App.Instance.ExitApplication();
        };
        flyout.Items.Add(exitItem);

        return flyout;
    }

    /// <summary>在托盘图标附近（鼠标位置）显示右键菜单</summary>
    private void ShowMenuAtCursor()
    {
        if (_trayIcon == null) return;
        if (Win32Helper.GetCursorPos(out var cursor))
        {
            _trayIcon.ShowContextMenu(new System.Drawing.Point(cursor.X, cursor.Y));
        }
    }

    /// <summary>刷新菜单文字（语言切换后）</summary>
    public void RefreshLocalization()
    {
        if (_menuFlyout == null) return;
        var loc = LocalizationService.Instance;
        var items = _menuFlyout.Items.OfType<MenuFlyoutItem>().ToList();
        // 顺序：显示/隐藏、设置、关于、退出（分隔线不是 MenuFlyoutItem）
        if (items.Count >= 4)
        {
            items[0].Text = loc.T("Tray.ShowHide");
            items[1].Text = loc.T("Tray.Settings");
            items[2].Text = loc.T("Tray.About");
            items[3].Text = loc.T("Tray.Exit");
        }
        if (_trayIcon != null) _trayIcon.ToolTipText = App.GetDisplayTitle();
    }

    /// <summary>刷新托盘提示文字（自定义标题确认后由 App 调用，需求-BUG3）</summary>
    public void RefreshTooltip()
    {
        if (_trayIcon != null) _trayIcon.ToolTipText = App.GetDisplayTitle();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _hostWindow?.Close();
        _hostWindow = null;
    }
}

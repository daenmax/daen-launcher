using DaenLauncher.Services;
using DaenLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DaenLauncher;

/// <summary>
/// 设置窗口：单例、独立窗口、关闭 = 隐藏（需求-设置窗口开头）。
/// 左侧导航（汉堡折叠/展开，NavigationView 自带动画），右侧内容页。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>当前设置窗口实例（单例，供设置页访问窗口级操作）</summary>
    public static SettingsWindow? CurrentInstance { get; private set; }

    private IntPtr _hWnd;
    private AppWindow _appWindow = null!;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    public SettingsWindow()
    {
        InitializeComponent();
        CurrentInstance = this;

        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Settings.Title");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

        // 关闭 = 隐藏（需求：设置窗口关闭只关闭自己，不影响主窗口）
        _appWindow.Closing += (_, e) =>
        {
            if (!App.IsExiting)
            {
                e.Cancel = true;
                _appWindow.Hide();
            }
        };

        // 任务栏图标：按尺寸创建句柄后 WM_SETICON
        try
        {
            var iconPath = IconService.EnsureAppIconExtracted();
            if (iconPath != null)
            {
                using var fs = File.OpenRead(iconPath);
                _titleBarIconSmall = new System.Drawing.Icon(fs, 16, 16);
                fs.Position = 0;
                _titleBarIconBig = new System.Drawing.Icon(fs, 32, 32);
                Win32Helper.SendMessage(_hWnd, Win32Helper.WM_SETICON,
                    Win32Helper.ICON_SMALL, _titleBarIconSmall.Handle);
                Win32Helper.SendMessage(_hWnd, Win32Helper.WM_SETICON,
                    Win32Helper.ICON_BIG, _titleBarIconBig.Handle);
                Win32Helper.SetClassLongPtr(_hWnd, Win32Helper.GCLP_HICON, _titleBarIconBig.Handle);
                Win32Helper.SetClassLongPtr(_hWnd, Win32Helper.GCLP_HICONSM, _titleBarIconSmall.Handle);
            }
        }
        catch { /* 图标设置失败不影响运行 */ }

        BackdropService.Apply(this, SettingsService.Instance.Settings.Backdrop);
        ThemeService.Apply(RootGrid, SettingsService.Instance.Settings.Theme);
        // 系统标题栏按钮配色（参考 DeskBox：透明背景 + 按深浅色着色）
        ThemeService.ApplyCaptionButtonColors(_appWindow, RootGrid);

        _ = LoadTitleBarIconAsync();
        BuildNavMenu();
        ApplyLocalization();
        LocalizationService.LanguageChanged += ApplyLocalization;

        // 窗口尺寸（记住上次大小，需求-优化1）
        var settings = SettingsService.Instance.Settings;
        _appWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)settings.SettingsWindowWidth, (int)settings.SettingsWindowHeight));

        SizeChanged += (_, args) =>
        {
            settings.SettingsWindowWidth = args.Size.Width;
            settings.SettingsWindowHeight = args.Size.Height;
            SettingsService.Instance.Save();
        };
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（主窗口隐藏不影响设置窗口的显隐状态）</summary>
    public void ActivateAndBringToFront()
    {
        _appWindow.Show(true);
        Activate();
    }

    /// <summary>构建左侧导航（需求-设置窗口2）</summary>
    private void BuildNavMenu()
    {
        Nav.MenuItems.Clear();

        AddNavItem("General", "DaenLauncher.Assets.Icons.常规_64.png");
        AddNavItem("Appearance", "DaenLauncher.Assets.Icons.外观_64.png");
        AddNavItem("Data", "DaenLauncher.Assets.Icons.数据备份和导入_64.png");
        AddNavItem("Launcher", "DaenLauncher.Assets.Icons.启动器_64.png");
        AddNavItem("Todo", "DaenLauncher.Assets.Icons.待办_64.png");
        AddNavItem("Note", "DaenLauncher.Assets.Icons.随记_64.png");
        AddNavItem("Clipboard", "DaenLauncher.Assets.Icons.剪贴板_64.png");
        AddNavItem("About", "DaenLauncher.Assets.Icons.关于_64.png");

        // 默认选中"常规"
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>添加一个导航项</summary>
    private void AddNavItem(string tag, string iconResource)
    {
        var item = new NavigationViewItem
        {
            Tag = tag,
            Content = LocalizationService.Instance.T("Settings." + tag)
        };
        _ = SetNavIconAsync(item, iconResource);
        Nav.MenuItems.Add(item);
    }

    private static async Task SetNavIconAsync(NavigationViewItem item, string resourceName)
    {
        var bitmap = await IconService.LoadEmbeddedAsync(resourceName);
        if (bitmap != null)
        {
            item.Icon = new ImageIcon { Source = bitmap, Width = 22, Height = 22 };
        }
    }

    /// <summary>导航切换</summary>
    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        NavigateToPage(tag);
    }

    /// <summary>导航到指定页（记录当前页，语言切换时重建）</summary>
    private string _currentPageTag = "General";

    private void NavigateToPage(string tag)
    {
        _currentPageTag = tag;
        var content = tag switch
        {
            "General" => (UIElement)new GeneralPage(),
            "Appearance" => new AppearancePage(),
            "Data" => new DataPage(),
            "Launcher" => new LauncherPage(),
            "Todo" => new TodoPage(),
            "Note" or "Clipboard" => new PlaceholderPage(tag),
            "About" => new AboutPage(),
            _ => new GeneralPage()
        };
        ContentFrame.Content = content;
    }

    /// <summary>托盘"关于我们"：打开设置窗口并选到关于页（需求第3条）</summary>
    public void NavigateToAbout()
    {
        foreach (var menuItem in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string)menuItem.Tag == "About")
            {
                Nav.SelectedItem = menuItem;
                return;
            }
        }
    }

    /// <summary>重建当前设置页（恢复默认值后让控件回位）</summary>
    public void RebuildCurrentPage()
    {
        NavigateToPage(_currentPageTag);
    }

    /// <summary>刷新窗口标题（自定义标题确认后由 App 调用，需求-BUG3）</summary>
    public void RefreshTitle()
    {
        ApplyLocalization();
    }

    /// <summary>应用界面文字（语言切换时刷新）</summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        TitleBarText.Text = App.GetDisplayTitle() + " " + loc.T("Settings.Title");
        Title = TitleBarText.Text;

        foreach (var menuItem in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (menuItem.Tag is string tag)
            {
                menuItem.Content = loc.T("Settings." + tag);
            }
        }

        // 语言切换后重建当前页，让页面上所有控件文字（按钮/复选框/下拉框）都更新
        NavigateToPage(_currentPageTag);
    }
}

/// <summary>可本地化页面接口</summary>
public interface ILocalizable
{
    void ApplyLocalization();
}

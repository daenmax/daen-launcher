using DaenLauncher.Models;
using DaenLauncher.Services;
using DaenLauncher.Views.Tools;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher;

/// <summary>
/// 常用工具窗口（单例，关闭 = 隐藏）。
/// 三栏布局：左侧分类导航（加解密类/文本处理/格式化/其他常用）→
/// 中间工具面板 → 右侧当前分类的子功能竖向导航。
/// 每个工具的界面只构建一次并缓存（切换回来时输入内容还在）。
/// </summary>
public sealed partial class ToolsWindow : Window
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private IntPtr _hWnd;
    private AppWindow _appWindow = null!;
    private OverlappedPresenter? _presenter;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    /// <summary>窗口位置/尺寸保存防抖计时器（"上次位置"和尺寸记忆用）</summary>
    private readonly DispatcherTimer _savePositionTimer;

    // ===== 界面状态 =====

    /// <summary>当前选中的分类</summary>
    private ToolRegistry.Category _currentCategory = ToolRegistry.Categories[0];

    /// <summary>当前选中的工具</summary>
    private ToolDefinition? _currentTool;

    /// <summary>分类按钮（分类 id -> 按钮），更新选中态用</summary>
    private readonly Dictionary<string, Button> _categoryButtons = new();

    /// <summary>右侧子功能导航按钮（工具 id -> 按钮）</summary>
    private readonly Dictionary<string, Button> _toolButtons = new();

    /// <summary>已构建的工具界面缓存（工具 id -> 界面），切换工具时不丢输入内容</summary>
    private readonly Dictionary<string, UIElement> _toolPages = new();

    public ToolsWindow()
    {
        InitializeComponent();

        // ===== 窗口基础设置（与剪贴板窗口一致：关闭 = 隐藏） =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = _appWindow.Presenter as OverlappedPresenter;

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Tools");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        _appWindow.Closing += (_, e) =>
        {
            // 关闭（隐藏/退出）前把窗口位置/尺寸立即落盘
            SavePositionNow();
            if (!App.IsExiting)
            {
                e.Cancel = true;
                _appWindow.Hide();
            }
        };

        // 记录窗口位置/尺寸（防抖保存；计时器必须先于 Changed 订阅创建）
        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _savePositionTimer.Tick += SavePositionTimer_Tick;
        _appWindow.Changed += AppWindow_Changed;

        // 任务栏/标题栏图标（与剪贴板窗口同款）
        try
        {
            var iconPath = IconService.EnsureAppIconExtracted();
            if (iconPath != null) _appWindow.SetIcon(iconPath);
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
            }
        }
        catch { /* 图标设置失败不影响运行 */ }

        BackdropService.Apply(this, _settings.Backdrop);
        ThemeService.Apply(RootGrid, _settings.Theme);
        ThemeService.ApplyCaptionButtonColors(_appWindow, RootGrid);

        // 记忆的窗口尺寸（逻辑像素 → 物理像素）
        ApplyRememberedSize();

        // 应用锁定尺寸设置
        ApplyBehaviorSettings();

        _ = LoadTitleBarIconAsync();
        ApplyLocalization();

        // 联动：语言切换后重建导航和工具界面文字
        LocalizationService.LanguageChanged += ApplyLocalization;
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（由 App.ShowToolsWindow 调用，每次显示都会执行）</summary>
    public void ActivateAndBringToFront()
    {
        ApplyBehaviorSettings();

        if (!_appWindow.IsVisible)
        {
            ComputeShowPosition();
            _appWindow.Show();
        }
        else
        {
            _appWindow.Show(true);
        }
        Activate();

        // 每次显示都重新应用主题（设置窗口里可能刚切换过）
        ThemeService.Apply(RootGrid, _settings.Theme);
    }

    /// <summary>快捷键显示/隐藏切换（默认 Alt+5）</summary>
    public void ToggleViaHotkey()
    {
        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }
        else
        {
            ActivateAndBringToFront();
        }
    }

    /// <summary>应用窗口行为设置（永远置顶 / 锁定尺寸，设置页修改后即时生效）</summary>
    public void ApplyBehaviorSettings()
    {
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _settings.ToolsAlwaysOnTop;
            _presenter.IsResizable = !_settings.ToolsLockSize;
        }
    }

    #region 窗口位置/尺寸

    /// <summary>按记忆的尺寸设置窗口大小（逻辑像素 → 物理像素）</summary>
    private void ApplyRememberedSize()
    {
        var scale = GetDpiScale();
        var width = (int)(_settings.ToolsWindowWidth * scale);
        var height = (int)(_settings.ToolsWindowHeight * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    /// <summary>按设置的显示位置定位窗口（与剪贴板窗口同款）</summary>
    private void ComputeShowPosition()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
            DisplayAreaFallback.Nearest);
        var wa = displayArea.WorkArea;

        var scale = GetDpiScale();
        var width = (int)(_settings.ToolsWindowWidth * scale);
        var height = (int)(_settings.ToolsWindowHeight * scale);

        int x, y;
        switch (_settings.ToolsShowPosition)
        {
            case ShowPosition.TopLeft:
                x = wa.X; y = wa.Y;
                break;
            case ShowPosition.TopRight:
                x = wa.X + wa.Width - width; y = wa.Y;
                break;
            case ShowPosition.BottomLeft:
                x = wa.X; y = wa.Y + wa.Height - height;
                break;
            case ShowPosition.BottomRight:
                x = wa.X + wa.Width - width; y = wa.Y + wa.Height - height;
                break;
            case ShowPosition.LastPosition:
                // 上次位置：用记录的坐标（物理像素）；没记录过则退回屏幕中央
                if (_settings.ToolsLastWindowX >= 0 && _settings.ToolsLastWindowY >= 0)
                {
                    x = _settings.ToolsLastWindowX;
                    y = _settings.ToolsLastWindowY;
                }
                else
                {
                    x = wa.X + (wa.Width - width) / 2;
                    y = wa.Y + (wa.Height - height) / 2;
                }
                break;
            case ShowPosition.FollowMouse:
                Win32Helper.GetCursorPos(out var cursor);
                x = cursor.X - width / 2;
                y = cursor.Y - height / 2;
                break;
            case ShowPosition.Center:
            default:
                x = wa.X + (wa.Width - width) / 2;
                y = wa.Y + (wa.Height - height) / 2;
                break;
        }

        // 防止超出屏幕边缘
        if (x < wa.X) x = wa.X;
        if (y < wa.Y) y = wa.Y;
        if (x + width > wa.X + wa.Width) x = wa.X + wa.Width - width;
        if (y + height > wa.Y + wa.Height) y = wa.Y + wa.Height - height;

        _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>窗口位置/尺寸变化：记录到设置（防抖），供"上次位置"和尺寸记忆使用</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        _settings.ToolsLastWindowX = sender.Position.X;
        _settings.ToolsLastWindowY = sender.Position.Y;
        // 尺寸记忆：物理像素 → 逻辑像素（AppWindow 没有 Width/Height，用 Size）
        var scale = GetDpiScale();
        if (scale > 0)
        {
            _settings.ToolsWindowWidth = Math.Round(sender.Size.Width / scale);
            _settings.ToolsWindowHeight = Math.Round(sender.Size.Height / scale);
        }
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void SavePositionTimer_Tick(object? sender, object e)
    {
        _savePositionTimer.Stop();
        SettingsService.Instance.Save();
    }

    /// <summary>立即把待保存的窗口位置/尺寸写盘（隐藏/退出前调用）</summary>
    private void SavePositionNow()
    {
        if (_savePositionTimer.IsEnabled)
        {
            _savePositionTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    /// <summary>窗口 DPI 缩放</summary>
    private double GetDpiScale()
    {
        try
        {
            return Win32Helper.GetDpiForWindow(_hWnd) / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    #endregion

    /// <summary>刷新窗口标题（自定义软件标题变化后由 App.RefreshAllTitles 调用，联动需求）</summary>
    public void RefreshTitle()
    {
        TitleBarText.Text = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Tools");
        Title = TitleBarText.Text;
    }

    #region 导航与工具面板

    /// <summary>应用界面文字（首次加载和语言切换时调用：重建导航 + 清空工具缓存）</summary>
    private void ApplyLocalization()
    {
        RefreshTitle();
        // 语言变了，工具界面里的文字要全部重建（清缓存，重新 Build）
        _toolPages.Clear();
        _currentTool = null;
        BuildCategoryNav();
        SelectCategory(_currentCategory.Id);
    }

    /// <summary>构建左侧分类导航（四个分类按钮，选中态 = 左侧蓝色竖条 + 浅背景，同设置窗口导航）</summary>
    private void BuildCategoryNav()
    {
        CategoryPanel.Children.Clear();
        _categoryButtons.Clear();
        foreach (var category in ToolRegistry.Categories)
        {
            var button = BuildNavButton(LocalizationService.Tr(category.NameKey));
            button.Click += (_, _) => SelectCategory(category.Id);
            _categoryButtons[category.Id] = button;
            CategoryPanel.Children.Add(button);
        }
    }

    /// <summary>切换分类：更新左侧选中态 + 重建右侧子功能导航</summary>
    private void SelectCategory(string categoryId)
    {
        _currentCategory = ToolRegistry.FindCategory(categoryId) ?? ToolRegistry.Categories[0];
        UpdateButtonSelectedState(_categoryButtons, categoryId);
        BuildToolNav();
    }

    /// <summary>构建右侧子功能竖向导航（当前分类的全部工具，样式与左侧分类一致）</summary>
    private void BuildToolNav()
    {
        ToolNavPanel.Children.Clear();
        _toolButtons.Clear();
        foreach (var tool in _currentCategory.Tools)
        {
            var button = BuildNavButton(LocalizationService.Tr(tool.NameKey));
            button.Click += (_, _) => SelectTool(tool);
            _toolButtons[tool.Id] = button;
            ToolNavPanel.Children.Add(button);
        }

        // 默认选中该分类的第一个工具（缓存里已有选中的工具则保持）
        var keep = _currentTool != null &&
                   _currentCategory.Tools.Any(t => t.Id == _currentTool.Id) &&
                   _toolPages.ContainsKey(_currentTool.Id);
        SelectTool(keep ? _currentTool! : _currentCategory.Tools[0]);
    }

    /// <summary>
    /// 构建导航按钮：内容 = [蓝色竖条指示器] + 文字（模仿设置窗口 NavigationView 的选中指示条）。
    /// 竖条常驻（未选中时透明），避免选中时文字左右跳动。
    /// </summary>
    private static Button BuildNavButton(string text)
    {
        var indicator = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // 两列布局：竖条占第一列，文字占第二列（同一个单元格里会重叠，竖条会压住第一个字）
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(indicator, 0);
        Grid.SetColumn(label, 1);
        content.Children.Add(indicator);
        content.Children.Add(label);
        return new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
    }

    /// <summary>选中一个工具：更新右侧选中态 + 显示工具面板（有缓存用缓存）</summary>
    private void SelectTool(ToolDefinition tool)
    {
        _currentTool = tool;
        UpdateButtonSelectedState(_toolButtons, tool.Id);
        ToolTitle.Text = LocalizationService.Tr(tool.NameKey);

        if (!_toolPages.TryGetValue(tool.Id, out var page))
        {
            page = tool.Create();
            _toolPages[tool.Id] = page;
        }
        ToolHost.Content = page;
    }

    /// <summary>
    /// 更新一组导航按钮的选中态：
    /// 选中 = 左侧蓝色竖条（系统强调色）+ 浅色背景 + 加粗文字。
    /// </summary>
    private static void UpdateButtonSelectedState(Dictionary<string, Button> buttons, string selectedId)
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        foreach (var (id, button) in buttons)
        {
            var selected = id == selectedId;
            button.Background = Application.Current.Resources[selected
                ? "SubtleFillColorSecondaryBrush"
                : "SubtleFillColorTransparentBrush"] as Brush;
            if (button.Content is not Grid content)
            {
                continue;
            }
            foreach (var child in content.Children)
            {
                if (child is Border indicator)
                {
                    indicator.Background = selected
                        ? new SolidColorBrush(accent)
                        : new SolidColorBrush(Colors.Transparent);
                }
                else if (child is TextBlock text)
                {
                    text.FontWeight = selected
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal;
                }
            }
        }
    }

    #endregion
}

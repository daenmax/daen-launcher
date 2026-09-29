using DaenLauncher.Controls;
using DaenLauncher.Dialogs;
using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace DaenLauncher;

/// <summary>
/// 主窗口：自定义标题栏 + 左侧分类列表 + 右侧项目面板。
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppWindow _appWindow = null!;
    private IntPtr _hWnd;
    private OverlappedPresenter? _presenter;

    // 窗口图标句柄（WM_SETICON 设置后需保持存活，防止被 GC 回收）
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    // 分类列表数据（ObservableCollection 用于拖动排序）
    private readonly System.Collections.ObjectModel.ObservableCollection<LauncherCategory> _categories = new();
    private readonly DispatcherTimer _saveSizeTimer;
    private readonly DispatcherTimer _savePositionTimer;

    // 拖动分隔条状态
    private bool _isDraggingSplitter;

    // ===== 项目拖动（原生拖放，参考 DeskBox 文件格子的做法）=====
    // 原理：Button.CanDrag 开启后，按住拖动会启动系统拖拽，
    // 系统自动生成"被拖按钮"的内容快照跟随鼠标移动（即"鼠标拽着图标"的动画），
    // 不再需要手写指针捕获和浮动图标。
    // 内部拖放通过 DataPackage.Properties 里的标记识别，与外部文件/网址拖放区分开。
    private const string ItemDragMarkerProperty = "DaenLauncher.InternalItemDrag";
    private const string ItemDragMarkerValue = "DaenLauncher.ItemDrag.v1";

    // 拖动中源按钮的透明度：变淡表示"正在被拖走"，拖动结束后恢复
    private const double DragSourceOpacity = 0.45;

    // 当前拖拽会话的项目（DragStarting 记录，Drop/DropCompleted 清空）
    private LauncherItem? _dragItem;
    private LauncherSubCategory? _dragItemSub;
    private Button? _dragButton;

    // 指针级拖拽启动状态。注意：WinUI 3 的 Button（ButtonBase 类控件）会吞掉指针输入，
    // 系统"按住自动拖动"检测永远不会触发（微软 Q&A 确认的设计限制），
    // 参考 DeskBox 的做法：手动检测拖动阈值，超过后调用 StartDragAsync 主动发起原生拖拽
    private Button? _pressedItemButton;
    private Windows.Foundation.Point _itemDragStartPoint;
    private bool _isStartingItemDrag;

    // 拖拽启动后短时间内屏蔽 Click（防止拖完松手误启动项目）
    private DateTime _suppressClickUntil = DateTime.MinValue;

    // 拖放/拖动目标：Tab 模式记录当前选中的 Tab；卡片模式记录指针悬停的卡片
    private LauncherSubCategory? _currentTabSub;
    private LauncherSubCategory? _dropHoverSub;

    // 当前面板的子分类 -> 项目容器映射（拖放新增项目时直接追加，避免整面板重建）
    private readonly Dictionary<Guid, Panel> _itemsPanelBySub = new();

    // 子分类 -> 可视化元素映射（卡片 Border / Tab 项），用于跨子分类拖动命中检测
    private readonly List<(LauncherSubCategory Sub, FrameworkElement Element)> _panelTargets = new();

    public MainWindow()
    {
        InitializeComponent();

        // ===== 窗口基础设置 =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = _appWindow.Presenter as OverlappedPresenter;

        Title = AppInfoService.Config.AppName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

        if (_presenter != null)
        {
            _presenter.IsMinimizable = false;
            _presenter.IsMaximizable = false;
        }

        // 关闭 = 隐藏（需求-布局1：点击关闭按钮隐藏窗口，不退出进程和托盘）
        _appWindow.Closing += (_, e) =>
        {
            if (!App.IsExiting)
            {
                e.Cancel = true;
                HideWindow();
            }
            else
            {
                // 真正退出前把窗口位置落盘（"上次位置"显示策略依赖它）
                SaveWindowPositionNow();
            }
        };

        // 记录窗口位置（拖动/移动后防抖保存，"上次位置"显示策略用）
        _appWindow.Changed += AppWindow_Changed;

        // 全局热键注册到 App 的消息窗口（WinUI 主窗口收不到 WM_HOTKEY）
        // 这里无需额外处理，App.OnLaunchedCore -> SetupHotkeys 已完成

        // 任务栏图标：必须在窗口显示前同步设置（显示后设置不生效）
        var appIconPath = IconService.EnsureAppIconExtracted();
        if (appIconPath != null) _appWindow.SetIcon(appIconPath); else _appWindow.SetIcon(@"C:\Windows\System32\shell32.dll");

        // ===== 材质与主题 =====
        BackdropService.Apply(this, SettingsService.Instance.Settings.Backdrop);
        ThemeService.Apply(RootGrid, SettingsService.Instance.Settings.Theme);
        // 系统标题栏按钮配色（参考 DeskBox：透明背景 + 按深浅色着色）
        ThemeService.ApplyCaptionButtonColors(_appWindow, RootGrid);

        // ===== 窗口大小与位置 =====
        var settings = SettingsService.Instance.Settings;
        _appWindow.Resize(new SizeInt32((int)settings.WindowWidth, (int)settings.WindowHeight));
        LeftColumn.Width = new GridLength(settings.LeftPaneWidth);
        ApplyLockSize(settings.LockSize);
        ApplyAlwaysOnTop(settings.AlwaysOnTop);
        this.SizeChanged += MainWindow_SizeChanged;
        // Tick 只订阅一次（缩放窗口时 SizeChanged 高频触发，反复订阅会叠加导致异常）
        _saveSizeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _saveSizeTimer.Tick += SaveSizeTimer_Tick;

        // 位置保存防抖计时器（拖动窗口时会高频触发位置变化）
        _savePositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _savePositionTimer.Tick += SavePositionTimer_Tick;

        // ===== 初始化界面 =====
        _ = LoadTitleBarIconAsync();
        _ = LoadPlaceholderIconsAsync();
        ApplyLocalization();
        LoadCategories();

        // 分隔条：悬停时显示左右调整光标，提示可拖拽（优化项）
        Splitter.PointerEntered += (_, _) => Splitter.SetSplitterCursor(true);
        Splitter.PointerExited += (_, _) => Splitter.SetSplitterCursor(false);

        // 语言切换后刷新
        LocalizationService.LanguageChanged += ApplyLocalization;
    }

    #region 窗口行为

    /// <summary>窗口句柄</summary>
    public IntPtr WindowHandle => _hWnd;

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    private async Task LoadPlaceholderIconsAsync()
    {
        TodoIcon.Source = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.Icons.待办_64.png");
        NoteIcon.Source = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.Icons.随记_64.png");
        ClipboardIcon.Source = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.Icons.剪贴板_64.png");
    }

    /// <summary>按设置的显示位置显示主窗口（需求-启动器6）</summary>
    public void ShowAtConfiguredPosition()
    {
        var settings = SettingsService.Instance.Settings;
        ApplyAlwaysOnTop(settings.AlwaysOnTop);

        if (!_appWindow.IsVisible)
        {
            ComputeShowPosition(settings);
            _appWindow.Show();
            _appWindow.IsShownInSwitchers = true;
        }
        else
        {
            // 已显示：激活置前
            _appWindow.Show(true);
        }

        ApplyWindowIcon();
    }

    /// <summary>设置窗口/任务栏图标：按尺寸分别创建句柄后 WM_SETICON + 窗口类图标</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var iconPath = IconService.EnsureAppIconExtracted();
            if (iconPath == null) return;

            // ICON_SMALL 必须是 16x16、ICON_BIG 是 32x32，
            // 传 256px 大图句柄会被任务栏拒绝（回退默认图标）
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
        catch { /* 图标设置失败不影响运行 */ }
    }

    /// <summary>计算显示位置（跟随鼠标时以鼠标为中心并防止超出屏幕）</summary>
    private void ComputeShowPosition(AppSettings settings)
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var wa = displayArea.WorkArea;

        // 尺寸：物理像素换算（AppWindow 用物理像素，设置里存逻辑像素）
        var scale = GetDpiScale();
        var width = (int)(settings.WindowWidth * scale);
        var height = (int)(settings.WindowHeight * scale);

        int x, y;
        switch (settings.ShowPosition)
        {
            case ShowPosition.Center:
                x = wa.X + (wa.Width - width) / 2;
                y = wa.Y + (wa.Height - height) / 2;
                break;
            case ShowPosition.LastPosition:
                // 上次位置：用记录的坐标（物理像素）；没记录过则退回居中
                if (settings.LastWindowX >= 0 && settings.LastWindowY >= 0)
                {
                    x = settings.LastWindowX;
                    y = settings.LastWindowY;
                    // 防止上次的位置超出当前屏幕（如换了分辨率/接了外接屏）
                    if (x < wa.X) x = wa.X;
                    if (y < wa.Y) y = wa.Y;
                    if (x + width > wa.X + wa.Width) x = wa.X + wa.Width - width;
                    if (y + height > wa.Y + wa.Height) y = wa.Y + wa.Height - height;
                }
                else
                {
                    x = wa.X + (wa.Width - width) / 2;
                    y = wa.Y + (wa.Height - height) / 2;
                }
                break;
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
            case ShowPosition.FollowMouse:
            default:
                Win32Helper.GetCursorPos(out var cursor);
                x = cursor.X - width / 2;
                y = cursor.Y - height / 2;
                // 防止超出屏幕边缘
                if (x < wa.X) x = wa.X;
                if (y < wa.Y) y = wa.Y;
                if (x + width > wa.X + wa.Width) x = wa.X + wa.Width - width;
                if (y + height > wa.Y + wa.Height) y = wa.Y + wa.Height - height;
                break;
        }

        _appWindow.Move(new PointInt32(x, y));
    }

    private double GetDpiScale()
    {
        try
        {
            // 通过 Win32 获取窗口 DPI
            var dpiValue = Win32Helper.GetDpiForWindow(_hWnd);
            return dpiValue / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>显示/隐藏切换</summary>
    public void ToggleVisibility()
    {
        if (_appWindow.IsVisible)
        {
            HideWindow();
        }
        else
        {
            ShowAtConfiguredPosition();
        }
    }

    /// <summary>隐藏窗口（不退出进程）。隐藏前把当前位置落盘（"上次位置"用）</summary>
    public void HideWindow()
    {
        SaveWindowPositionNow();
        _appWindow.Hide();
    }

    /// <summary>窗口位置变化：记录到设置（防抖），供"上次位置"显示策略使用</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        var settings = SettingsService.Instance.Settings;
        settings.LastWindowX = sender.Position.X;
        settings.LastWindowY = sender.Position.Y;
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void SavePositionTimer_Tick(object? sender, object e)
    {
        _savePositionTimer.Stop();
        SettingsService.Instance.Save();
    }

    /// <summary>立即把待保存的窗口位置写盘（隐藏/退出前调用，防止防抖期间丢数据）</summary>
    private void SaveWindowPositionNow()
    {
        if (_savePositionTimer.IsEnabled)
        {
            _savePositionTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    /// <summary>应用置顶设置</summary>
    private void ApplyAlwaysOnTop(bool onTop)
    {
        if (_presenter != null) _presenter.IsAlwaysOnTop = onTop;
    }

    /// <summary>应用锁定尺寸设置</summary>
    private void ApplyLockSize(bool lockSize)
    {
        if (_presenter != null) _presenter.IsResizable = !lockSize;
    }

    /// <summary>窗口大小变化：保存到设置（防抖）</summary>
    private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        var settings = SettingsService.Instance.Settings;
        // 仅在未锁定尺寸时记录
        if (!settings.LockSize)
        {
            settings.WindowWidth = args.Size.Width;
            settings.WindowHeight = args.Size.Height;
            ScheduleSaveSettings();
        }
    }

    private void ScheduleSaveSettings()
    {
        _saveSizeTimer.Stop();
        _saveSizeTimer.Start();
    }

    private void SaveSizeTimer_Tick(object? sender, object e)
    {
        _saveSizeTimer.Stop();
        SettingsService.Instance.Save();
    }

    #endregion

    #region 标题栏按钮

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ShowSettingsWindow();
    }

    /// <summary>底部功能入口占位按钮（待办/随手记/剪贴板，需求-布局3）</summary>
    private void PlaceholderButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowMessageDialog(LocalizationService.Tr("Main.UnderDevelopment"),
            LocalizationService.Tr("Dialog.Info"));
    }

    #endregion

    #region 本地化

    /// <summary>应用界面文字（首次加载和语言切换时调用）</summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        var title = App.GetDisplayTitle();
        TitleBarText.Text = title;
        Title = title;
        ToolTipService.SetToolTip(SettingsButton, loc.T("Main.SettingsTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsButton, loc.T("Main.SettingsTooltip"));
        ToolTipService.SetToolTip(TodoButton, loc.T("Main.Todo"));
        ToolTipService.SetToolTip(NoteButton, loc.T("Main.Note"));
        ToolTipService.SetToolTip(ClipboardButton, loc.T("Main.Clipboard"));

        // 语言切换后重建面板，让 Tab 头/卡片头等动态文字也更新
        RebuildPanel();
    }

    #endregion

    #region 分类列表

    /// <summary>加载分类到左侧列表</summary>
    private void LoadCategories()
    {
        _categories.Clear();
        foreach (var category in LauncherDataService.Instance.Data.Categories)
        {
            _categories.Add(category);
        }
        CategoryListView.ItemsSource = _categories;
        if (_categories.Count > 0)
        {
            CategoryListView.SelectedIndex = 0;
        }
        RebuildPanel();
    }

    /// <summary>保存分类顺序（拖动排序后同步回数据，需求-说明3）</summary>
    private void SaveCategoryOrder()
    {
        var list = LauncherDataService.Instance.Data.Categories;
        list.Clear();
        foreach (var category in _categories)
        {
            list.Add(category);
        }
        LauncherDataService.Instance.Save();
    }

    private void CategoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RebuildPanel();
    }

    /// <summary>渲染分类项（XAML 模板 + 动态设置图标和文字大小，大小跟随设置）</summary>
    private void CategoryListView_ContainerContentChanging(ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is not LauncherCategory category) return;
        var settings = SettingsService.Instance.Settings;

        var iconHost = FindTemplateElement<ContentControl>(args.ItemContainer, "IconHost");
        var nameText = FindTemplateElement<TextBlock>(args.ItemContainer, "NameText");
        if (iconHost == null) return;

        // 图标（0 = 不显示）
        if (settings.CategoryIconSize > 0)
        {
            iconHost.Width = settings.CategoryIconSize;
            iconHost.Height = settings.CategoryIconSize;
            iconHost.Visibility = Visibility.Visible;
            iconHost.Content = CreateIconElement(category.Icon, settings.CategoryIconSize);
        }
        else
        {
            iconHost.Content = null;
            iconHost.Visibility = Visibility.Collapsed;
        }

        // 文字（0 = 不显示）
        if (nameText != null)
        {
            nameText.FontSize = settings.CategoryTextSize;
            nameText.Visibility = settings.CategoryTextSize > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 图标位置（左边/右边）+ 整体对齐（居左/居中/居右，需求-优化2）
        if (iconHost.Parent is StackPanel row)
        {
            var index = row.Children.IndexOf(iconHost);
            var textIndex = nameText != null ? row.Children.IndexOf(nameText) : -1;
            var wantIndex = settings.CategoryIconPosition == CategoryIconPosition.Right ? 1 : 0;
            if (textIndex >= 0 && index != wantIndex && index >= 0 && textIndex >= 0)
            {
                row.Children.RemoveAt(index);
                row.Children.Insert(wantIndex, iconHost);
            }
            row.HorizontalAlignment = settings.CategoryAlignment switch
            {
                ItemHorizontalAlignment.Center => HorizontalAlignment.Center,
                ItemHorizontalAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
        }
    }

    /// <summary>创建图标元素：emoji 返回文本，png 文件名返回图片（带缓存）</summary>
    private static UIElement? CreateIconElement(string? icon, double size)
    {
        if (string.IsNullOrEmpty(icon)) return null;

        if (EmojiCatalog.IsEmoji(icon))
        {
            return new TextBlock
            {
                Text = icon,
                FontSize = Math.Max(12, size - 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var image = new Image { Stretch = Stretch.Uniform };
        _ = LoadIconIntoImageAsync(image, icon);
        return image;
    }

    /// <summary>异步把 png 图标加载进 Image（IconService 内部有缓存）</summary>
    private static async Task LoadIconIntoImageAsync(Image image, string icon)
    {
        var source = await IconService.ResolveImageAsync(icon);
        if (source != null) image.Source = source;
    }

    /// <summary>在可视化树中按名字查找模板内元素</summary>
    private static T? FindTemplateElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target && target.Name == name) return target;
            var result = FindTemplateElement<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>分类列表右键菜单（需求-说明4/5）</summary>
    private void CategoryListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var itemContainer = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        var menu = new MenuFlyout();
        var loc = LocalizationService.Instance;

        if (itemContainer != null &&
            CategoryListView.ItemFromContainer(itemContainer) is LauncherCategory category)
        {
            // 选中右键的分类
            CategoryListView.SelectedItem = category;

            // 编辑分类
            var editItem = new MenuFlyoutItem { Text = loc.T("Main.Category.Edit") };
            editItem.Click += (_, _) => _ = ShowCategoryEditDialog(category);
            menu.Items.Add(editItem);

            // 向上移动
            var upItem = new MenuFlyoutItem { Text = loc.T("Main.Category.MoveUp"), IsEnabled = _categories.IndexOf(category) > 0 };
            upItem.Click += (_, _) =>
            {
                var index = _categories.IndexOf(category);
                _categories.Move(index, index - 1);
                SaveCategoryOrder();
            };
            menu.Items.Add(upItem);

            // 向下移动
            var downItem = new MenuFlyoutItem { Text = loc.T("Main.Category.MoveDown"), IsEnabled = _categories.IndexOf(category) < _categories.Count - 1 };
            downItem.Click += (_, _) =>
            {
                var index = _categories.IndexOf(category);
                _categories.Move(index, index + 1);
                SaveCategoryOrder();
            };
            menu.Items.Add(downItem);

            // 删除当前分类
            var deleteItem = new MenuFlyoutItem { Text = loc.T("Main.Category.Delete") };
            deleteItem.Click += (_, _) => _ = DeleteCategory(category);
            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            // 新建子分类
            var newSubItem = new MenuFlyoutItem { Text = loc.T("Main.SubCategory.New") };
            newSubItem.Click += (_, _) => _ = ShowSubCategoryEditDialog(category, null);
            menu.Items.Add(newSubItem);

            // 管理子分类
            var manageSubItem = new MenuFlyoutItem { Text = loc.T("Main.SubCategory.Manage") };
            manageSubItem.Click += (_, _) => _ = ShowManageSubCategoriesDialog(category);
            menu.Items.Add(manageSubItem);
        }
        else
        {
            // 右键空白处：新建分类（需求-说明4）
            var newItem = new MenuFlyoutItem { Text = loc.T("Main.Category.New") };
            newItem.Click += (_, _) => _ = ShowCategoryEditDialog(null);
            menu.Items.Add(newItem);
        }

        menu.ShowAt(CategoryListView, e.GetPosition(CategoryListView));
    }

    /// <summary>删除分类（二次确认）</summary>
    private async Task DeleteCategory(LauncherCategory category)
    {
        var loc = LocalizationService.Instance;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = loc.T("Main.Category.DeleteConfirmTitle"),
            Content = string.Format(loc.T("Main.Category.DeleteConfirmContent"), category.Name),
            PrimaryButtonText = loc.T("Dialog.Delete"),
            CloseButtonText = loc.T("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        LauncherDataService.Instance.Data.Categories.Remove(category);
        LauncherDataService.Instance.Save();
        _categories.Remove(category);

        if (_categories.Count > 0)
        {
            CategoryListView.SelectedIndex = 0;
        }
        else
        {
            RebuildPanel();
        }
    }

    /// <summary>拖动排序完成后保存</summary>
    private void CategoryListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        SaveCategoryOrder();
    }

    #endregion

    /// <summary>查找可视化树祖先节点</summary>
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    #region 分隔条拖动（需求-说明1）

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SettingsService.Instance.Settings.LockSize) return;
        _isDraggingSplitter = true;
        Splitter.CapturePointer(e.Pointer);
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        var position = e.GetCurrentPoint(PanelHost).Position;
        // PanelHost 从分隔条右侧开始，需要加上分隔条与左栏的偏移
        var rootPosition = e.GetCurrentPoint(RootGrid).Position;
        var newWidth = Math.Clamp(rootPosition.X - 8, 120, RootGrid.ActualWidth - 300);
        LeftColumn.Width = new GridLength(newWidth);
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        _isDraggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
        var settings = SettingsService.Instance.Settings;
        settings.LeftPaneWidth = LeftColumn.Width.Value;
        SettingsService.Instance.Save();
    }

    #endregion

    #region 项目面板

    /// <summary>刷新分类列表和面板（显示参数变化、数据修改后调用）</summary>
    public void RefreshMainPanel()
    {
        RefreshCategoryList();
        RebuildPanel();
    }

    /// <summary>只重建右侧项目面板（布局/尺寸等设置变化时用，左侧分类列表不动，需求-BUG1）</summary>
    public void RefreshPanelOnly()
    {
        RebuildPanel();
    }

    /// <summary>数据被外部变更（删除数据/导入）后，从磁盘重新加载全部数据并刷新界面</summary>
    public void ReloadData()
    {
        LoadCategories();
    }

    /// <summary>应用窗口行为设置（置顶/锁定尺寸），不重建面板</summary>
    public void ApplyBehaviorSettings()
    {
        var settings = SettingsService.Instance.Settings;
        ApplyAlwaysOnTop(settings.AlwaysOnTop);
        ApplyLockSize(settings.LockSize);
    }

    /// <summary>刷新窗口标题（自定义标题变化后调用）</summary>
    public void RefreshTitle()
    {
        var title = App.GetDisplayTitle();
        TitleBarText.Text = title;
        Title = title;
    }

    /// <summary>重建右侧项目面板（Tab 或 卡片风格，需求-布局4）。
    /// 注意：只重建面板本身，不能修改 _categories（SelectionChanged 事件中禁止集合修改）；
    /// 会尽量保持当前选中的子分类（Tab 选中项）不变。</summary>
    private void RebuildPanel()
    {
        PanelHost.Children.Clear();
        _itemsPanelBySub.Clear();
        _panelTargets.Clear();
        _dropHoverSub = null;

        if (CategoryListView.SelectedItem is not LauncherCategory category)
        {
            return;
        }

        if (category.SubCategories.Count == 0) return;

        // 保持上次选中的子分类（Tab 模式重建后仍选中原来的 Tab）
        if (_currentTabSub == null || !category.SubCategories.Contains(_currentTabSub))
        {
            _currentTabSub = category.SubCategories[0];
        }

        PanelHost.Children.Add(
            SettingsService.Instance.Settings.SubCategoryStyle == SubCategoryStyle.Tab
                ? BuildTabPanel(category)
                : BuildCardPanel(category));
    }

    /// <summary>整个项目面板区域作为拖放目标（需求-布局4：支持拖拽多个到当前子分类）。
    /// 同时也接收"内部项目拖动"（拖图标排序）经过 Tab 头/卡片/空白区时的 DragOver。</summary>
    private void PanelHost_DragOver(object sender, DragEventArgs e)
    {
        // 内部项目拖动：接受移动，拖到 Tab 头/卡片上松手即可把项目移过去
        if (IsInternalItemDrag(e) && _dragItem != null)
        {
            e.Handled = true;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            // 只保留跟随鼠标的图标快照，隐藏系统默认的"移动"角标和文字（更干净）
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            return;
        }

        // 外部拖放（从资源管理器等拖入文件/网址）
        e.Handled = true;
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems) ||
            e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
    }

    /// <summary>拖放落地：解析拖入内容并添加到目标子分类（悬停的卡片 > 当前 Tab > 第一个子分类）。
    /// 注意：不重建整个面板，直接把新项目按钮追加到目标容器，Tab 选中状态保持不变。</summary>
    private async void PanelHost_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CategoryListView.SelectedItem is not LauncherCategory category) return;

        // ===== 内部项目拖动落到 Tab 头/卡片/空白区（项目面板上的 Drop 已由 ItemPanel_Drop 处理）=====
        if (IsInternalItemDrag(e))
        {
            // 已被子面板处理过（状态已清空）就不再重复处理
            if (_dragItem == null) return;

            // 用指针位置实时命中 Tab 头/卡片，确定目标子分类
            var pointer = e.GetPosition(PanelHost);
            LauncherSubCategory? internalTargetSub = null;
            foreach (var (candidate, element) in _panelTargets)
            {
                var bounds = element.TransformToVisual(PanelHost).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (bounds.Contains(pointer))
                {
                    internalTargetSub = candidate;
                    break;
                }
            }
            internalTargetSub ??= _dropHoverSub ?? _currentTabSub;
            if (internalTargetSub == null || !category.SubCategories.Contains(internalTargetSub))
            {
                internalTargetSub = category.SubCategories.FirstOrDefault();
            }
            if (internalTargetSub == null) return;

            if (internalTargetSub == _dragItemSub)
            {
                // 放回原子分类：拖动过程中的实时重排已生效，把 UI 顺序写回数据即可
                if (_dragButton?.Parent is Panel sourcePanel) CommitPanelOrder(sourcePanel);
                LauncherDataService.Instance.Save();
            }
            else
            {
                // 跨子分类移动：从原子分类移除，追加到目标子分类末尾
                _dragItemSub?.Items.Remove(_dragItem);
                internalTargetSub.Items.Add(_dragItem);
                LauncherDataService.Instance.Save();
                RebuildPanel();
            }
            ResetItemDrag();
            return;
        }

        // ===== 外部拖放（从资源管理器等拖入文件/网址）=====
        // 优先用指针位置实时命中（比 DragEnter 悬停记录更可靠）
        var extPointer = e.GetPosition(PanelHost);
        LauncherSubCategory? targetSub = null;
        foreach (var (candidate, element) in _panelTargets)
        {
            var bounds = element.TransformToVisual(PanelHost).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Contains(extPointer))
            {
                targetSub = candidate;
                break;
            }
        }
        // 实时命中失败再退回悬停记录/当前 Tab/第一个
        targetSub ??= _dropHoverSub ?? _currentTabSub;
        if (targetSub == null || !category.SubCategories.Contains(targetSub))
        {
            targetSub = category.SubCategories.FirstOrDefault();
        }
        if (targetSub == null) return;

        // 目标容器和指针位置要在 await 之前确定（DragEventArgs 在 await 之后不可再用）
        var settings = SettingsService.Instance.Settings;
        var panelIsNew = false;
        if (!_itemsPanelBySub.TryGetValue(targetSub.Id, out var panel))
        {
            panelIsNew = true;
            if (settings.ItemLayout == ItemLayoutMode.Grid)
            {
                panel = new UniformWrapPanel();
            }
            else
            {
                // 列表：固定 1 列，每行铺满面板宽度（与 BuildItemsHost 一致）
                panel = new UniformWrapPanel { FixedColumns = 1 };
            }
            _itemsPanelBySub[targetSub.Id] = panel;
        }

        var pointerInPanel = e.GetPosition(panel);

        var items = await DropResolver.ResolveAsync(e.DataView);
        if (items.Count == 0) return;

        // 计算插入位置（需求-优化3）：找中心离鼠标最近的项目，
        // 鼠标在其右/下则插到它后面，否则插到它前面；面板不在可视树上时追加到末尾
        var insertIndex = panel.Children.Count;
        if (!panelIsNew && panel.Parent != null)
        {
            Button? nearest = null;
            double bestDist = double.MaxValue;
            foreach (var child in panel.Children)
            {
                if (child is not Button b || b.Tag is not LauncherItem) continue;
                var bounds = b.TransformToVisual(panel).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));
                var cx = bounds.X + bounds.Width / 2;
                var cy = bounds.Y + bounds.Height / 2;
                var dx = cx - pointerInPanel.X;
                var dy = cy - pointerInPanel.Y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = b;
                }
            }
            if (nearest != null)
            {
                var idx = panel.Children.IndexOf(nearest);
                var nb = nearest.TransformToVisual(panel).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, nearest.ActualWidth, nearest.ActualHeight));
                // 列表（固定单列）看垂直方向，平铺看水平方向
                var verticalFlow = panel is UniformWrapPanel { FixedColumns: 1 };
                var after = verticalFlow
                    ? pointerInPanel.Y > nb.Y + nb.Height / 2
                    : pointerInPanel.X > nb.X + nb.Width / 2;
                insertIndex = after ? idx + 1 : idx;
            }
        }

        foreach (var item in items)
        {
            targetSub.Items.Insert(Math.Min(insertIndex, targetSub.Items.Count), item);
            panel.Children.Insert(Math.Min(insertIndex, panel.Children.Count),
                BuildItemButton(targetSub, item, panel));
            insertIndex++;
        }
        LauncherDataService.Instance.Save();
        _dropHoverSub = null;
    }

    /// <summary>Tab 风格面板</summary>
    private FrameworkElement BuildTabPanel(LauncherCategory category)
    {
        var tabView = new TabView
        {
            Background = null,
            IsAddTabButtonVisible = false,
            TabWidthMode = TabViewWidthMode.SizeToContent
        };

        TabViewItem? selectedTab = null;
        foreach (var sub in category.SubCategories)
        {
            var tab = new TabViewItem
            {
                Header = BuildSubCategoryHeader(sub),
                IsClosable = false,
                // 内容包一层透明 Grid，作为该 Tab 的拖放/悬停区域
                Content = BuildTabContent(category, sub)
            };
            tabView.TabItems.Add(tab);
            // 跨子分类拖动目标：Tab 头（把项目拖到另一个 Tab 上即可移动过去）
            _panelTargets.Add((sub, tab));
            if (sub == _currentTabSub) selectedTab = tab;
        }

        // 保持当前选中的子分类（Tab 选中项）
        tabView.SelectionChanged += (_, _) =>
        {
            var index = tabView.SelectedIndex;
            if (index >= 0 && index < category.SubCategories.Count)
            {
                _currentTabSub = category.SubCategories[index];
            }
        };
        tabView.SelectedItem = selectedTab ?? (tabView.TabItems.Count > 0 ? tabView.TabItems[0] : null);
        if (tabView.SelectedItem is TabViewItem st && category.SubCategories.Count > 0)
        {
            var idx = tabView.TabItems.IndexOf(st);
            if (idx >= 0) _currentTabSub = category.SubCategories[idx];
        }

        return tabView;
    }

    /// <summary>Tab 内容容器（项目区 + 拖放/拖动悬停区域）</summary>
    private Grid BuildTabContent(LauncherCategory category, LauncherSubCategory sub)
    {
        var grid = new Grid { Background = null, MinHeight = 320, AllowDrop = true };
        var itemsHost = BuildItemsHost(sub);
        // 项目区必须包一层 ScrollViewer，内容超出窗口高度时才能滚动（并出现纵向滚动条）
        var scrollViewer = new ScrollViewer
        {
            Content = itemsHost,
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 横向必须禁用滚动：否则内容会以"无限宽"测量，等宽面板拿不到视口宽度，
            // 列表模式的按钮就没法铺满整行（BUG：悬停高亮只有内容那么宽）
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 内容水平撑满，列表模式的按钮才能铺满整行
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        grid.Children.Add(scrollViewer);

        // 整个 Tab 内容区悬停时记录拖放目标子分类
        grid.DragEnter += (_, _) => _dropHoverSub = sub;
        grid.DragLeave += (_, _) => { if (_dropHoverSub == sub) _dropHoverSub = null; };

        return grid;
    }

    /// <summary>卡片风格面板</summary>
    private FrameworkElement BuildCardPanel(LauncherCategory category)
    {
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 同 Tab 风格：横向禁用滚动，卡片内容才能撑满视口宽度（否则收缩到内容宽）
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(4, 8, 8, 8)
        };

        var stack = new StackPanel { Spacing = 12 };
        scrollViewer.Content = stack;

        foreach (var sub in category.SubCategories)
        {
            // 用样式资源创建卡片（ThemeResource 会跟随深浅色，见 App.xaml）
            var card = new Border { Style = (Style)Application.Current.Resources["SettingsCardBorderStyle"] };
            card.Padding = new Thickness(12);
            // 必须开启 AllowDrop，DragEnter 才会在卡片上触发（否则拖放目标识别不到）
            card.AllowDrop = true;

            var cardStack = new StackPanel { Spacing = 8 };
            cardStack.Children.Add(BuildSubCategoryHeader(sub));
            cardStack.Children.Add(BuildItemsHost(sub));
            card.Child = cardStack;
            stack.Children.Add(card);

            // 卡片整体（含头部）作为该子分类的悬停/拖放区域：精准识别拖到了哪张卡片
            card.DragEnter += (_, _) => _dropHoverSub = sub;
            card.DragLeave += (_, _) => { if (_dropHoverSub == sub) _dropHoverSub = null; };
            // 跨子分类拖动目标：把项目拖到另一张卡片上即可移动过去
            _panelTargets.Add((sub, card));
        }

        return scrollViewer;
    }

    /// <summary>子分类头部（图标 + 名称，大小跟随设置）</summary>
    private Grid BuildSubCategoryHeader(LauncherSubCategory sub)
    {
        var settings = SettingsService.Instance.Settings;
        var header = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        if (settings.SubCategoryIconSize > 0)
        {
            var iconHost = new ContentControl
            {
                Width = settings.SubCategoryIconSize,
                Height = settings.SubCategoryIconSize,
                Content = CreateIconElement(sub.Icon, settings.SubCategoryIconSize)
            };
            panel.Children.Add(iconHost);
        }

        if (settings.SubCategoryTextSize > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = sub.Name,
                FontSize = settings.SubCategoryTextSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        header.Children.Add(panel);
        return header;
    }

    /// <summary>构建某个子分类的项目容器（平铺/列表 + 拖动排序）。
    /// 注意：文件拖放统一在 PanelHost 上处理（整块面板区域都可作为拖放目标）。</summary>
    private Panel BuildItemsHost(LauncherSubCategory sub)
    {
        var settings = SettingsService.Instance.Settings;

        Panel itemsPanel;
        if (settings.ItemLayout == ItemLayoutMode.Grid)
        {
            // 平铺：等宽换行面板——每行数量只由窗口宽度决定，列对齐、格子等宽（需求-BUG1）
            itemsPanel = new UniformWrapPanel();
        }
        else
        {
            // 列表：等宽面板固定 1 列——每行按钮必然铺满面板宽度（悬停高亮铺满整行），
            // 行高统一、图标列对齐，不受文字宽度影响（需求-BUG2/优化3）。
            // 间距由项目自身 Margin 控制（跟随设置）
            itemsPanel = new UniformWrapPanel { FixedColumns = 1 };
        }

        foreach (var item in sub.Items)
        {
            itemsPanel.Children.Add(BuildItemButton(sub, item, itemsPanel));
        }

        _itemsPanelBySub[sub.Id] = itemsPanel;

        // 项目拖动改用原生拖放（按钮上按住拖动即可，系统自带"图标跟随鼠标"动画）。
        // 面板作为内部拖放的落点：DragOver 实时重排，Drop 把顺序写回数据。
        // 注意：外部文件拖放的 DragOver/Drop 不在这里处理（保持未处理状态，
        // 事件会冒泡给 PanelHost 统一处理，与原来的行为一致）
        itemsPanel.AllowDrop = true;
        itemsPanel.DragOver += ItemPanel_DragOver;
        itemsPanel.Drop += ItemPanel_Drop;

        // 卡片/Tab 内容区悬停时记录拖放目标子分类（整个 PanelHost 都是拖放目标）
        itemsPanel.DragEnter += (_, _) => _dropHoverSub = sub;
        itemsPanel.DragLeave += (_, _) => { if (_dropHoverSub == sub) _dropHoverSub = null; };

        return itemsPanel;
    }

    /// <summary>按钮上按下左键：记录拖拽发起的候选（按钮 + 起点），此时还不知道是点击还是拖动</summary>
    private void ItemButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SettingsService.Instance.Settings.LockIcons) return;
        if (sender is not Button button || button.Tag is not LauncherItem) return;
        if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;

        _pressedItemButton = button;
        _itemDragStartPoint = e.GetCurrentPoint(button).Position;
    }

    /// <summary>按钮上移动：超过拖动阈值就调用 StartDragAsync 主动发起原生拖拽。
    /// 这是 Button 上唯一可靠的拖拽发起方式（系统自动检测在 Button 上不生效）。
    /// StartDragAsync 一返回即发起完成，后续 DragOver/Drop 走原生拖放流程。</summary>
    private async void ItemButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isStartingItemDrag ||
            sender is not Button button ||
            !ReferenceEquals(button, _pressedItemButton)) return;

        var point = e.GetCurrentPoint(button);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedItemButton = null;
            return;
        }

        var dx = point.Position.X - _itemDragStartPoint.X;
        var dy = point.Position.Y - _itemDragStartPoint.Y;
        if (dx * dx + dy * dy < 25) return; // 未超过阈值（DeskBox 同款 5px）

        _isStartingItemDrag = true;
        e.Handled = true;
        try
        {
            await button.StartDragAsync(point);
            // 拖拽会话结束（await 到落下/取消才返回），短时间屏蔽 Click 防误启动
            _suppressClickUntil = DateTime.Now + TimeSpan.FromMilliseconds(400);
        }
        catch
        {
            // 拖拽发起失败（极少见）不影响本次点击行为
        }
        finally
        {
            _isStartingItemDrag = false;
            _pressedItemButton = null;
        }
    }

    /// <summary>按钮上松开：未发起拖拽的按下到此结束（正常点击会触发 Click）</summary>
    private void ItemButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _pressedItemButton)) _pressedItemButton = null;
    }

    /// <summary>指针捕获丢失：发起拖拽时系统接管指针，或按下后指针被抢走，清除候选状态</summary>
    private void ItemButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStartingItemDrag) _pressedItemButton = null;
    }

    /// <summary>原生拖拽开始（拖动越过阈值后 StartDragAsync 触发）：
    /// 记录拖动会话、标记拖拽数据，并把源按钮变淡表示"正在被拖走"。
    /// 系统会自动生成按钮内容的快照跟随鼠标（"鼠标拽着图标"的动画）。</summary>
    private void ItemButton_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        // 锁定图标开关：运行时判断，取消拖拽（点击启动不受影响）
        if (SettingsService.Instance.Settings.LockIcons)
        {
            args.Cancel = true;
            return;
        }
        if (sender is not Button button || button.Tag is not LauncherItem item) return;

        _dragItem = item;
        _dragItemSub = FindOwnerSub(item);
        _dragButton = button;
        button.Opacity = DragSourceOpacity;

        // 标记 + 文本：DragOver/Drop 通过标记识别"这是自己项目的拖动"，
        // 与外部文件/网址拖放区分开；SetText 让拖拽数据不为空。
        // 注意：WinUI 3 的 DragStartingEventArgs 没有 AllowedOperation 属性（UWP 才有），
        // 要通过 Data.RequestedOperation 设置允许的操作（参考 DeskBox 的写法）
        args.Data.Properties[ItemDragMarkerProperty] = ItemDragMarkerValue;
        args.Data.SetText(item.Name);
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    /// <summary>原生拖拽结束（松手/取消/拖到窗口外都会触发）：
    /// 恢复源按钮透明度并清理拖动会话。顺序调整的落库在 Drop 处理里完成。</summary>
    private void ItemButton_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (sender is Button button) button.Opacity = 1.0;
        ResetItemDrag();
    }

    /// <summary>根据项目反查它当前所在的子分类</summary>
    private LauncherSubCategory? FindOwnerSub(LauncherItem item)
    {
        if (CategoryListView.SelectedItem is not LauncherCategory category) return null;
        foreach (var sub in category.SubCategories)
        {
            if (sub.Items.Contains(item)) return sub;
        }
        return null;
    }

    /// <summary>项目面板 DragOver：内部拖动 → 实时重排并接受移动；
    /// 外部文件拖放 → 保持未处理，事件冒泡给 PanelHost 统一处理。</summary>
    private void ItemPanel_DragOver(object sender, DragEventArgs e)
    {
        if (!IsInternalItemDrag(e)) return;
        e.Handled = true;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        // 只保留跟随鼠标的图标快照，隐藏系统默认的"移动"角标和文字（更干净）
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;

        // 拖动按钮就在本面板时：按指针位置实时重排，其余项目给它让位
        if (sender is Panel panel && _dragButton != null && panel.Children.Contains(_dragButton))
        {
            MoveDraggedButtonByPosition(panel, e.GetPosition(panel));
        }
    }

    /// <summary>项目面板 Drop：内部拖动 → 同面板把顺序落库 / 跨面板移动项目；
    /// 外部文件拖放 → 保持未处理，事件冒泡给 PanelHost 统一处理。</summary>
    private void ItemPanel_Drop(object sender, DragEventArgs e)
    {
        if (!IsInternalItemDrag(e)) return;
        if (_dragItem == null || sender is not Panel panel) return;
        e.Handled = true;

        if (ReferenceEquals(_dragButton?.Parent, panel))
        {
            // 同面板放下：拖动过程中的实时重排已排好 UI 顺序，这里写回数据
            CommitPanelOrder(panel);
            LauncherDataService.Instance.Save();
        }
        else
        {
            // 跨子分类移动：从原子分类移除，追加到目标子分类末尾
            var targetSub = FindSubByPanel(panel);
            if (targetSub == null) return;
            _dragItemSub?.Items.Remove(_dragItem);
            targetSub.Items.Add(_dragItem);
            LauncherDataService.Instance.Save();
            RebuildPanel();
        }
        ResetItemDrag();
    }

    /// <summary>按指针位置实时移动被拖按钮（最近中心算法：拖到哪个项目旁边就插到哪）。
    /// 平铺（水平流）看指针在最近项目中心的左/右；列表（垂直流）看上/下。
    /// 拖动过程中只调整 UI 顺序，数据顺序在松手时统一落库（见 ItemPanel_Drop）。</summary>
    private void MoveDraggedButtonByPosition(Panel itemsPanel, Windows.Foundation.Point position)
    {
        if (_dragButton == null || !itemsPanel.Children.Contains(_dragButton)) return;

        // 找中心离指针最近的其他项目
        Button? nearest = null;
        double bestDist = double.MaxValue;
        foreach (var child in itemsPanel.Children)
        {
            if (child is not Button b || b == _dragButton || b.Tag is not LauncherItem) continue;
            var bounds = b.TransformToVisual(itemsPanel).TransformBounds(
                new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));
            var cx = bounds.X + bounds.Width / 2;
            var cy = bounds.Y + bounds.Height / 2;
            var dx = cx - position.X;
            var dy = cy - position.Y;
            var dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = b;
            }
        }
        if (nearest == null) return;

        // 指针在最近项目中心的哪一侧决定插到前面还是后面
        var nb = nearest.TransformToVisual(itemsPanel).TransformBounds(
            new Windows.Foundation.Rect(0, 0, nearest.ActualWidth, nearest.ActualHeight));
        var pdx = position.X - (nb.X + nb.Width / 2);
        var pdy = position.Y - (nb.Y + nb.Height / 2);
        // 平铺（等宽换行面板，水平流）看水平方向；列表（固定单列，垂直流）看垂直方向
        var isVerticalFlow = itemsPanel is UniformWrapPanel { FixedColumns: 1 };
        var after = isVerticalFlow ? pdy > 0 : pdx > 0;

        var nearestIndex = itemsPanel.Children.IndexOf(nearest);
        var draggedIndex = itemsPanel.Children.IndexOf(_dragButton);
        var targetIndex = after ? nearestIndex + 1 : nearestIndex;
        if (targetIndex > draggedIndex) targetIndex--; // 移除自身后索引左移
        if (targetIndex == draggedIndex) return; // 位置没变就不动，避免反复重排

        itemsPanel.Children.Remove(_dragButton);
        itemsPanel.Children.Insert(Math.Min(targetIndex, itemsPanel.Children.Count), _dragButton);
    }

    /// <summary>重置项目拖动会话状态</summary>
    private void ResetItemDrag()
    {
        _dragItem = null;
        _dragItemSub = null;
        _dragButton = null;
    }

    /// <summary>构建一个项目按钮（图标+文字，按设置的尺寸和文字位置）</summary>
    private Button BuildItemButton(LauncherSubCategory sub, LauncherItem item, Panel itemsPanel)
    {
        var settings = SettingsService.Instance.Settings;
        var isListMode = settings.ItemLayout == ItemLayoutMode.List;

        var button = new Button
        {
            Tag = item,
            Padding = new Thickness(6),
            // Toolkit WrapPanel 没有间距属性，用项目外边距实现横向/纵向间距（总间距平分到两侧）
            Margin = new Thickness(
                settings.ItemHorizontalSpacing / 2, settings.ItemVerticalSpacing / 2,
                settings.ItemHorizontalSpacing / 2, settings.ItemVerticalSpacing / 2),
            Background = null,
            BorderThickness = new Thickness(1),
            BorderBrush = null,
            CornerRadius = new CornerRadius(6),
            // 按钮铺满所在格子（平铺：等宽格子；列表：整行），
            // 格子内图标+文字的对齐由 HorizontalContentAlignment 控制（"图标和文字位置"设置，平铺/列表通用）
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = settings.ItemContentAlignment switch
            {
                ItemHorizontalAlignment.Center => HorizontalAlignment.Center,
                ItemHorizontalAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            },
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, string.IsNullOrEmpty(item.Remark) ? item.Name : $"{item.Name}\n{item.Remark}");

        // 内容块的对齐（居左/居中/居右，跟随"图标和文字位置"设置）
        var contentAlignment = settings.ItemContentAlignment switch
        {
            ItemHorizontalAlignment.Center => HorizontalAlignment.Center,
            ItemHorizontalAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };

        var content = new StackPanel
        {
            Orientation = settings.ItemTextPosition == ItemTextPosition.Below
                ? Orientation.Vertical
                : Orientation.Horizontal,
            Spacing = 4,
            // 内容块收缩到实际大小，由按钮的 HorizontalContentAlignment 定位到格子的左/中/右
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var iconHost = BuildItemIconHost(item, settings.ItemIconSize);
        // 文字在下布局时，图标在块内的对齐必须显式跟随设置：
        // 列表模式 + 居左时图标贴行首，每行图标列对齐，不受文字长短影响（需求-BUG2）。
        // 注意：WinUI 里固定宽度的子元素用默认对齐会被居中放置，必须显式设置
        iconHost.HorizontalAlignment = contentAlignment;

        var text = BuildItemText(item, settings, isListMode, contentAlignment);

        // 文字位置：下/左/右（需求-启动器16）
        if (settings.ItemTextPosition == ItemTextPosition.Left)
        {
            if (text != null) content.Children.Add(text);
            content.Children.Add(iconHost);
        }
        else if (settings.ItemTextPosition == ItemTextPosition.Right)
        {
            content.Children.Add(iconHost);
            if (text != null) content.Children.Add(text);
        }
        else
        {
            content.Children.Add(iconHost);
            if (text != null) content.Children.Add(text);
        }

        // 内容在按钮里垂直居中（平铺格子有统一高度、列表整行也比内容高）
        content.VerticalAlignment = VerticalAlignment.Center;

        button.Content = content;

        // ===== 点击启动（需求-说明5）；拖动结束后短时间内不触发 =====
        if (settings.ItemActivateMode == ItemActivateMode.SingleClick)
        {
            button.Click += (_, _) => LaunchItem(item);
        }
        else
        {
            button.DoubleTapped += (_, _) => LaunchItem(item);
        }

        // ===== 右键菜单（需求-说明6）=====
        button.RightTapped += (s, e) => ShowItemContextMenu(button, item, sub);

        // ===== 拖动排序（需求-布局4，原生拖放动画）=====
        // CanDrag 开启后按住拖动即可原生拖拽……但 Button 会吞掉指针输入，
        // 系统不会自动发起拖拽，所以还要手动检测阈值后调 StartDragAsync（见下方指针事件）。
        // DragStarting 里做锁定图标判断和会话记录，DragOver/Drop 由所在面板处理
        //（见 ItemPanel_DragOver / ItemPanel_Drop），结束或取消时 DropCompleted 清理状态。
        button.CanDrag = true;
        button.DragStarting += ItemButton_DragStarting;
        button.DropCompleted += ItemButton_DropCompleted;
        // 指针事件用于发起拖拽：Button 类处理会把它们标记为 Handled，
        // 必须 AddHandler(handledEventsToo:true) 才能收到
        button.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ItemButton_PointerPressed), true);
        button.AddHandler(Microsoft.UI.Xaml.UIElement.PointerMovedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ItemButton_PointerMoved), true);
        button.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ItemButton_PointerReleased), true);
        button.PointerCaptureLost += ItemButton_PointerCaptureLost;

        _ = LoadItemIconAsync(item, iconHost, settings.ItemIconSize);

        return button;
    }

    /// <summary>文字块（文字大小可为 0 = 不显示；列表模式不限制宽度换行）</summary>
    private FrameworkElement? BuildItemText(LauncherItem item, AppSettings settings, bool isListMode,
        HorizontalAlignment blockAlignment)
    {
        if (settings.ItemTextSize <= 0) return null;
        return new TextBlock
        {
            Text = item.Name,
            FontSize = settings.ItemTextSize,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = isListMode ? TextAlignment.Left : TextAlignment.Center,
            MaxLines = Math.Max(1, settings.ItemTextMaxLines),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = isListMode ? double.PositiveInfinity : Math.Max(50, settings.ItemIconSize * 1.8),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = blockAlignment
        };
    }

    /// <summary>图标容器（图标大小可为 0 = 不显示）</summary>
    private static Grid BuildItemIconHost(LauncherItem item, double iconSize)
    {
        if (iconSize <= 0) return new Grid { Width = 0, Height = 0 };
        var host = new Grid { Width = iconSize, Height = iconSize };
        // 异步加载真实图标，加载前先显示占位
        return host;
    }

    /// <summary>加载项目图标（文件图标提取，失败显示占位图标）</summary>
    private static async Task LoadItemIconAsync(LauncherItem item,
        Grid iconHost, double iconSize)
    {
        if (iconSize <= 0) return;

        // 启动时检测到路径失效的项目：显示"项目无法找到"图标（需求-优化1）
        if (item.IsMissing)
        {
            var missingIcon = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.Icons.项目无法找到.png");
            if (missingIcon != null)
            {
                iconHost.Children.Add(new Image { Source = missingIcon, Stretch = Stretch.Uniform });
                return;
            }
        }

        var icon = await ItemIconService.GetIconAsync(item);
        if (icon != null)
        {
            iconHost.Children.Add(new Image { Source = icon, Stretch = Stretch.Uniform });
        }
        else
        {
            // 占位图标：Url/协议/Uwp 用链接图标，其他用文档图标
            var glyph = item.Type switch
            {
                LauncherItemType.Url or LauncherItemType.Protocol or LauncherItemType.Uwp => "\uE71B",
                LauncherItemType.Folder => "\uE8B7",
                _ => "\uE7C3"
            };
            iconHost.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = Math.Min(iconSize, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    /// <summary>把面板里按钮的当前顺序写回子分类数据（拖动排序落库）。
    /// 数量不一致时不动数据，避免面板与数据不同步时丢项目。</summary>
    private void CommitPanelOrder(Panel itemsPanel)
    {
        if (_dragItemSub == null) return;
        var ordered = itemsPanel.Children.OfType<Button>()
            .Where(b => b.Tag is LauncherItem)
            .Select(b => (LauncherItem)b.Tag)
            .ToList();
        if (ordered.Count != _dragItemSub.Items.Count) return;
        _dragItemSub.Items.Clear();
        _dragItemSub.Items.AddRange(ordered);
    }

    /// <summary>根据项目容器反查它所属的子分类</summary>
    private LauncherSubCategory? FindSubByPanel(Panel panel)
    {
        if (CategoryListView.SelectedItem is not LauncherCategory category) return null;
        foreach (var sub in category.SubCategories)
        {
            if (_itemsPanelBySub.TryGetValue(sub.Id, out var p) && ReferenceEquals(p, panel)) return sub;
        }
        return null;
    }

    /// <summary>判断当前拖放是否为本应用内部的"项目排序拖动"
    /// （DragStarting 时打上的标记，用于和外部文件/网址拖放区分）</summary>
    private static bool IsInternalItemDrag(DragEventArgs e)
    {
        return e.DataView.Properties.TryGetValue(ItemDragMarkerProperty, out var value) &&
               value as string == ItemDragMarkerValue;
    }

    /// <summary>启动项目（需求：项目启动后按设置隐藏/显示软件）；拖拽结束后的松手不触发</summary>
    private void LaunchItem(LauncherItem item)
    {
        if (DateTime.Now < _suppressClickUntil) return;

        var success = LauncherRunner.Start(item);
        if (success)
        {
            if (SettingsService.Instance.Settings.AfterLaunch == AfterLaunchBehavior.Hide)
            {
                HideWindow();
            }
        }
        else
        {
            _ = ShowMessageDialog(
                string.Format(LocalizationService.Tr("Main.LaunchFailed"), item.Name),
                LocalizationService.Tr("Dialog.Error"));
        }
    }

    /// <summary>项目右键菜单（需求-说明6）</summary>
    private void ShowItemContextMenu(Button button, LauncherItem item, LauncherSubCategory sub)
    {
        var menu = new MenuFlyout();
        var loc = LocalizationService.Instance;

        // 以管理员身份运行
        var adminItem = new MenuFlyoutItem { Text = loc.T("Main.Item.RunAsAdmin") };
        adminItem.Click += (_, _) => LauncherRunner.Start(item, asAdmin: true);
        menu.Items.Add(adminItem);

        // 打开所在位置
        var locationItem = new MenuFlyoutItem { Text = loc.T("Main.Item.OpenLocation") };
        locationItem.Click += (_, _) => LauncherRunner.OpenContainingFolder(item);
        menu.Items.Add(locationItem);

        // 打开资源管理器菜单（功能暂未开放，保留入口）
        var shellMenuItem = new MenuFlyoutItem { Text = loc.T("Main.Item.ShellMenu") };
        shellMenuItem.Click += (_, _) =>
        {
            _ = ShowMessageDialog(LocalizationService.Tr("Main.UnderDevelopment"),
                LocalizationService.Tr("Dialog.Info"));
        };
        menu.Items.Add(shellMenuItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // 复制完整路径
        var copyItem = new MenuFlyoutItem { Text = loc.T("Main.Item.CopyPath") };
        copyItem.Click += (_, _) => LauncherRunner.CopyFullPath(item);
        menu.Items.Add(copyItem);

        // 删除项目
        var deleteItem = new MenuFlyoutItem { Text = loc.T("Main.Item.Delete") };
        deleteItem.Click += (_, _) =>
        {
            sub.Items.Remove(item);
            ItemIconService.DeleteCache(item);
            LauncherDataService.Instance.Save();
            RebuildPanel();
        };
        menu.Items.Add(deleteItem);

        // 编辑项目
        var editItem = new MenuFlyoutItem { Text = loc.T("Main.Item.Edit") };
        editItem.Click += (_, _) => _ = ShowItemEditDialog(item);
        menu.Items.Add(editItem);

        menu.ShowAt(button, new Windows.Foundation.Point(0, button.ActualHeight));
    }

    #endregion

    #region 对话框（在 Dialogs.cs 中实现，这里只是桥接）

    /// <summary>新建/编辑分类弹窗</summary>
    private async Task ShowCategoryEditDialog(LauncherCategory? category)
    {
        var result = await Dialogs.CategoryEditDialog.ShowAsync(Content.XamlRoot, category);
        if (result == null) return;

        if (category == null)
        {
            // 新建：自动带上一个默认子分类（避免新分类是空的）
            var newCategory = new LauncherCategory
            {
                Name = result.Value.Name,
                Icon = result.Value.Icon,
                SubCategories =
                {
                    new LauncherSubCategory { Name = LocalizationService.Tr("Main.DefaultSubCategory") }
                }
            };
            LauncherDataService.Instance.Data.Categories.Add(newCategory);
            _categories.Add(newCategory);
            CategoryListView.SelectedItem = newCategory;
        }
        else
        {
            // 编辑
            category.Name = result.Value.Name;
            category.Icon = result.Value.Icon;
            RefreshCategoryList();
        }
        LauncherDataService.Instance.Save();
        RebuildPanel();
    }

    /// <summary>刷新分类列表渲染（编辑名称/图标后调用）</summary>
    private void RefreshCategoryList()
    {
        var selected = CategoryListView.SelectedItem;
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categories;
        CategoryListView.SelectedItem = selected;
    }

    /// <summary>新建/编辑子分类弹窗</summary>
    private async Task ShowSubCategoryEditDialog(LauncherCategory category, LauncherSubCategory? sub)
    {
        var result = await Dialogs.SubCategoryEditDialog.ShowAsync(Content.XamlRoot, sub);
        if (result == null) return;

        if (sub == null)
        {
            var newSub = new LauncherSubCategory { Name = result.Value.Name, Icon = result.Value.Icon };
            category.SubCategories.Add(newSub);
        }
        else
        {
            sub.Name = result.Value.Name;
            sub.Icon = result.Value.Icon;
        }
        LauncherDataService.Instance.Save();
        RebuildPanel();
    }

    /// <summary>管理子分类弹窗</summary>
    private async Task ShowManageSubCategoriesDialog(LauncherCategory category)
    {
        await Dialogs.ManageSubCategoriesDialog.ShowAsync(Content.XamlRoot, category, this);
    }

    /// <summary>编辑项目弹窗</summary>
    private async Task ShowItemEditDialog(LauncherItem item)
    {
        var changed = await Dialogs.ItemEditDialog.ShowAsync(Content.XamlRoot, item);
        if (!changed) return;
        LauncherDataService.Instance.Save();
        // 路径可能刚被改好/改坏，立即重新评估失效状态（正常显示与"项目无法找到"图标切换）
        item.IsMissing = LauncherDataService.IsItemMissing(item);
        ItemIconService.DeleteCache(item);
        RebuildPanel();
    }

    /// <summary>通用提示弹窗</summary>
    public async Task ShowMessageDialog(string message, string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = LocalizationService.Tr("Dialog.Ok"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    #endregion
}

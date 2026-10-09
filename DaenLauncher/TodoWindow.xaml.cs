using DaenLauncher.Models;
using DaenLauncher.Services;
using DaenLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher;

/// <summary>
/// 待办窗口（单例，关闭 = 隐藏）。
/// 从上到下：标题栏（右侧同步刷新按钮）→ 标签栏（全部/今天/重要/已完成）→
/// 颜色选择（过滤 + 新任务默认颜色）→ 添加任务 → 任务列表 → 右下角"清除已完成"。
/// 云同步开启时：窗口打开/点刷新先从云端拉取；写操作（增删改）会先校验 token 再推送到云端；
/// 云端获取失败时所有写操作禁用（需求）。
/// </summary>
public sealed partial class TodoWindow : Window
{
    /// <summary>待办窗口默认宽度（逻辑像素，窗口尺寸固定不可调）</summary>
    private const double DefaultWidth = 420;

    /// <summary>待办窗口默认高度（逻辑像素，窗口尺寸固定不可调）</summary>
    private const double DefaultHeight = 640;

    private readonly TodoService _todo = TodoService.Instance;
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private IntPtr _hWnd;
    private AppWindow _appWindow = null!;
    private OverlappedPresenter? _presenter;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    /// <summary>窗口位置保存防抖计时器（拖动窗口时高频触发，"上次位置"用）</summary>
    private readonly DispatcherTimer _savePositionTimer;

    // ===== 界面状态 =====

    /// <summary>当前标签页</summary>
    private TodoFilterTab _currentTab = TodoFilterTab.All;

    /// <summary>当前颜色过滤（TodoColors.None = 不过滤）</summary>
    private string _colorFilter = TodoColors.None;

    /// <summary>防抖：同步/写入操作进行中时禁止重复触发</summary>
    private bool _busy;

    /// <summary>颜色过滤按钮（标记 -> 按钮），用于更新选中态</summary>
    private readonly Dictionary<string, Button> _colorButtons = new();

    /// <summary>标签页按钮（页 -> 按钮），用于更新选中态</summary>
    private readonly Dictionary<TodoFilterTab, Button> _tabButtons = new();

    public TodoWindow()
    {
        InitializeComponent();

        // ===== 窗口基础设置（与设置窗口一致：关闭 = 隐藏） =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = _appWindow.Presenter as OverlappedPresenter;

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Todo");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

        _appWindow.Closing += (_, e) =>
        {
            // 关闭（隐藏/退出）前把窗口位置立即落盘（"上次位置"依赖）
            SavePositionNow();
            if (!App.IsExiting)
            {
                e.Cancel = true;
                _appWindow.Hide();
            }
        };

        // 记录窗口位置（拖动/移动后防抖保存，"上次位置"显示策略用）
        // 注意：计时器必须先于 Changed 订阅创建，防止事件先到时用到 null
        _savePositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _savePositionTimer.Tick += SavePositionTimer_Tick;
        _appWindow.Changed += AppWindow_Changed;

        // 任务栏/标题栏图标
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

        // 默认尺寸：窄而高的便签风格
        _appWindow.Resize(new Windows.Graphics.SizeInt32((int)DefaultWidth, (int)DefaultHeight));

        // 应用置顶/锁定尺寸设置
        ApplyBehaviorSettings();

        _ = LoadTitleBarIconAsync();
        ApplyLocalization();
        BuildTabs();
        BuildColorButtons();
        RefreshList();

        LocalizationService.LanguageChanged += ApplyLocalization;
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（由 App.ShowTodoWindow 调用，每次显示都会执行）</summary>
    public void ActivateAndBringToFront()
    {
        ApplyBehaviorSettings();

        if (!_appWindow.IsVisible)
        {
            // 从隐藏到显示：按设置的显示位置定位（参考主窗口）
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

        // 云同步状态可能在上次显示后被修改：重算"云同步"按钮可见性 + 自动刷新（需求）
        OnSyncSettingsChanged();
    }

    /// <summary>快捷键显示/隐藏切换（默认 Alt+2）</summary>
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

    /// <summary>应用待办窗口行为设置（永远置顶/锁定尺寸，设置页修改后即时生效）</summary>
    public void ApplyBehaviorSettings()
    {
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _settings.TodoAlwaysOnTop;
            _presenter.IsResizable = !_settings.TodoLockSize;
        }
    }

    /// <summary>按设置的显示位置定位窗口（参考主窗口实现）</summary>
    private void ComputeShowPosition()
    {
        // 位置计算统一走 WindowPositionHelper（多显示器/负数坐标/各屏缩放都在那里处理）
        var point = WindowPositionHelper.Compute(_hWnd, _settings.TodoShowPosition,
            DefaultWidth, DefaultHeight,
            _settings.TodoLastWindowX, _settings.TodoLastWindowY);
        _appWindow.Move(point);
    }

    /// <summary>窗口位置变化：记录到设置（防抖），供"上次位置"显示策略使用（与主窗口同款）</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        _settings.TodoLastWindowX = sender.Position.X;
        _settings.TodoLastWindowY = sender.Position.Y;
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void SavePositionTimer_Tick(object? sender, object e)
    {
        _savePositionTimer.Stop();
        SettingsService.Instance.Save();
    }

    /// <summary>立即把待保存的窗口位置写盘（隐藏/退出前调用，防止防抖期间丢数据）</summary>
    private void SavePositionNow()
    {
        if (_savePositionTimer.IsEnabled)
        {
            _savePositionTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    /// <summary>窗口 DPI 缩放（AppWindow 物理像素换算用）</summary>
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

    /// <summary>刷新窗口标题（自定义软件标题变化后由 App.RefreshAllTitles 调用，联动需求）</summary>
    public void RefreshTitle()
    {
        var loc = LocalizationService.Instance;
        TitleBarText.Text = App.GetDisplayTitle() + " " + loc.T("Main.Todo");
        Title = TitleBarText.Text;
    }

    #region 本地化

    /// <summary>应用界面文字（首次加载和语言切换时调用）</summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        RefreshTitle();
        ToolTipService.SetToolTip(SyncButton, loc.T("Todo.SyncRefreshTooltip"));
        // 快捷添加按钮（行内输入框模式）
        AddButtonText.Text = loc.T("Todo.QuickAdd");
        AddTextBox.PlaceholderText = loc.T("Todo.QuickAddHint");
        // 底部按钮
        SyncButtonText.Text = loc.T("Todo.SyncRefresh");
        ClearCompletedText.Text = loc.T("Todo.ClearCompleted");
        ToolTipService.SetToolTip(ClearCompletedButton, loc.T("Todo.ClearCompleted"));
        AddTaskButtonText.Text = loc.T("Todo.AddTask");

        // 标签页文字
        foreach (var (tab, button) in _tabButtons)
        {
            button.Content = new TextBlock { Text = loc.T(TabTextKey(tab)) };
        }
        UpdateTabVisuals();
        RefreshList();
    }

    #endregion

    #region 标签栏

    /// <summary>构建标签栏（全部 / 今天 / 重要 / 已完成，需求）</summary>
    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        _tabButtons.Clear();
        foreach (TodoFilterTab tab in Enum.GetValues<TodoFilterTab>())
        {
            var button = new Button
            {
                Content = new TextBlock { Text = LocalizationService.Tr(TabTextKey(tab)) },
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = tab
            };
            button.Click += (_, _) => SelectTab(tab);
            _tabButtons[tab] = button;
            TabPanel.Children.Add(button);
        }
        UpdateTabVisuals();
    }

    private static string TabTextKey(TodoFilterTab tab) => tab switch
    {
        TodoFilterTab.Today => "Todo.Tab.Today",
        TodoFilterTab.Important => "Todo.Tab.Important",
        TodoFilterTab.Completed => "Todo.Tab.Completed",
        _ => "Todo.Tab.All"
    };

    /// <summary>切换标签页并刷新列表</summary>
    private void SelectTab(TodoFilterTab tab)
    {
        _currentTab = tab;
        UpdateTabVisuals();
        RefreshList();
    }

    /// <summary>更新标签页的选中态（选中 = 浅色背景 + 加粗文字）</summary>
    private void UpdateTabVisuals()
    {
        foreach (var (tab, button) in _tabButtons)
        {
            var selected = tab == _currentTab;
            button.Background = Application.Current.Resources[selected
                ? "SubtleFillColorSecondaryBrush"
                : "SubtleFillColorTransparentBrush"] as Brush;
            if (button.Content is TextBlock text)
            {
                text.FontWeight = selected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    #endregion

    #region 颜色选择

    /// <summary>构建颜色选择行：8 个颜色小按钮（参考 DeskBox 的颜色过滤）。
    /// 点击 = 按颜色过滤列表，再点一次 = 取消过滤；过滤中的颜色也是新任务的默认颜色。</summary>
    private void BuildColorButtons()
    {
        ColorPanel.Children.Clear();
        _colorButtons.Clear();
        foreach (var marker in TodoColors.All)
        {
            var colorHex = TodoColors.GetHex(marker);
            var button = new Button
            {
                Width = 28,
                Height = 26,
                MinWidth = 28,
                MinHeight = 26,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = Application.Current.Resources["SubtleFillColorTransparentBrush"] as Brush,
                Tag = marker
            };
            button.Content = new Border
            {
                Width = 14,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ColorConverter(colorHex))
            };
            button.Click += (_, _) => ToggleColorFilter(marker);
            _colorButtons[marker] = button;
            ColorPanel.Children.Add(button);
        }
        UpdateColorVisuals();
    }

    /// <summary>十六进制色值（#RRGGBB）转 Color</summary>
    private static Windows.UI.Color ColorConverter(string hex)
    {
        return Microsoft.UI.ColorHelper.FromArgb(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16),
            255);
    }

    /// <summary>点击颜色按钮：切换过滤状态</summary>
    private void ToggleColorFilter(string marker)
    {
        _colorFilter = _colorFilter == marker ? TodoColors.None : marker;
        UpdateColorVisuals();
        RefreshList();
    }

    /// <summary>更新颜色按钮的选中态（选中 = 描边高亮）</summary>
    private void UpdateColorVisuals()
    {
        foreach (var (marker, button) in _colorButtons)
        {
            button.BorderBrush = marker == _colorFilter
                ? Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"] as Brush
                : Application.Current.Resources["SubtleFillColorTransparentBrush"] as Brush;
        }
    }

    #endregion

    #region 任务列表

    /// <summary>按当前标签页 + 颜色过滤取可见任务</summary>
    private List<TodoItem> GetVisibleItems()
    {
        var today = DateTime.Today;
        IEnumerable<TodoItem> query = _todo.Data.Items;
        query = _currentTab switch
        {
            // 全部：未完成的（已完成的内容只出现在"已完成"页，需求）
            TodoFilterTab.All => query.Where(i => !i.IsCompleted),
            // 今天：未完成且（今天截止或已过期）
            TodoFilterTab.Today => query.Where(i => !i.IsCompleted && i.DueDate != null && i.DueDate.Value.Date <= today),
            // 重要：未完成且标记重要
            TodoFilterTab.Important => query.Where(i => !i.IsCompleted && i.IsImportant),
            TodoFilterTab.Completed => query.Where(i => i.IsCompleted),
            _ => query
        };
        if (_colorFilter != TodoColors.None)
        {
            query = query.Where(i => i.Color == _colorFilter);
        }
        return query.ToList();
    }

    /// <summary>重建任务列表（数据变化后调用）</summary>
    private void RefreshList()
    {
        ListPanel.Children.Clear();
        var items = GetVisibleItems();

        if (items.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr("Todo.Empty"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
                Opacity = 0.6,
                Tag = "Todo.Empty"
            });
        }
        else
        {
            foreach (var item in items)
            {
                ListPanel.Children.Add(BuildItemRow(item));
            }
        }

        // 底部统计 + 清除已完成按钮状态
        var total = _todo.Data.Items.Count;
        var completed = _todo.Data.Items.Count(i => i.IsCompleted);
        CountText.Text = string.Format(LocalizationService.Tr("Todo.ItemsCount"), total);
        ClearCompletedButton.IsEnabled = completed > 0;
        ClearCompletedButton.Opacity = completed > 0 ? 1.0 : 0.5;
    }

    /// <summary>构建一条任务行：勾选框 + 颜色条 + 内容（+截止日期）+ 悬停操作（重要/删除）</summary>
    private Border BuildItemRow(TodoItem item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 6, 8),
            // Subtle 是半透明叠加画刷，深浅色下都和窗口底色融合（Card 灰底在深色模式下不协调）
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };

        // 重要任务：底色用设置里选择的重要底色（设置-待办），低不透明度保证文字清晰
        if (item.IsImportant && _settings.TodoImportantColor != TodoColors.None)
        {
            var hex = TodoColors.GetHex(_settings.TodoImportantColor);
            row.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                34,
                Convert.ToByte(hex[1..3], 16),
                Convert.ToByte(hex[3..5], 16),
                Convert.ToByte(hex[5..7], 16)));
        }

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ===== 勾选框（点击切换完成状态） =====
        var checkBox = new CheckBox
        {
            IsChecked = item.IsCompleted,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var itemRef = item;
        checkBox.Click += (_, _) => ToggleCompleted(itemRef, checkBox.IsChecked == true);
        Grid.SetColumn(checkBox, 0);
        grid.Children.Add(checkBox);

        // ===== 颜色条（无颜色时不显示） =====
        if (item.Color != TodoColors.None)
        {
            var colorBar = new Border
            {
                Width = 3,
                Height = 20,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(ColorConverter(TodoColors.GetHex(item.Color)))
            };
            Grid.SetColumn(colorBar, 1);
            grid.Children.Add(colorBar);
        }

        // ===== 内容：文字 + 截止日期小字 =====
        var contentStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var text = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            TextDecorations = item.TextDecorations,
            Opacity = item.IsCompleted ? 0.55 : 1.0
        };
        contentStack.Children.Add(text);

        if (item.DueDate != null && !item.IsCompleted)
        {
            var isOverdue = item.DueDate.Value.Date < DateTime.Today;
            var isToday = item.DueDate.Value.Date == DateTime.Today;
            var dueText = new TextBlock
            {
                FontSize = 12,
                Opacity = isOverdue ? 1.0 : 0.7,
                Foreground = isOverdue
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 90, 70))
                    : Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
                Text = isToday ? LocalizationService.Tr("Todo.Due.Today")
                    : isOverdue ? LocalizationService.Tr("Todo.Due.Overdue")
                    : string.Format(LocalizationService.Tr("Todo.Due.On"), item.DueDate.Value.ToString("yyyy/M/d"))
            };
            contentStack.Children.Add(dueText);
        }

        // 备注预览（一行，超长省略）
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            contentStack.Children.Add(new TextBlock
            {
                Text = item.Notes,
                FontSize = 12,
                Opacity = 0.6,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(contentStack, 2);
        grid.Children.Add(contentStack);

        // ===== 悬停操作按钮（重要 / 删除） =====
        var actionHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false
        };
        actionHost.Children.Add(MakeIconButton(
            item.IsImportant ? "\uE735" : "\uE734", // 实心星 / 空心星
            LocalizationService.Tr(item.IsImportant ? "Todo.Unimportant" : "Todo.Important"),
            () => ToggleImportant(itemRef)));
        actionHost.Children.Add(MakeIconButton(
            "\uE74D", // 垃圾桶
            LocalizationService.Tr("Todo.Delete"),
            () => DeleteItem(itemRef)));
        Grid.SetColumn(actionHost, 3);
        grid.Children.Add(actionHost);

        row.Child = grid;

        // 悬停显示操作按钮
        row.PointerEntered += (_, _) => { actionHost.Opacity = 1; actionHost.IsHitTestVisible = true; };
        row.PointerExited += (_, _) => { actionHost.Opacity = 0; actionHost.IsHitTestVisible = false; };

        // 双击编辑
        row.DoubleTapped += (_, _) => _ = EditItemAsync(itemRef);

        // 右键菜单：编辑 / 重要 / 删除
        var menu = new MenuFlyout();
        var editItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr("Todo.Edit"),
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        editItem.Click += (_, _) => _ = EditItemAsync(itemRef);
        var importantItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr(item.IsImportant ? "Todo.Unimportant" : "Todo.Important"),
            Icon = new FontIcon { Glyph = item.IsImportant ? "\uE735" : "\uE734" }
        };
        importantItem.Click += (_, _) => ToggleImportant(itemRef);
        var deleteItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr("Todo.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => DeleteItem(itemRef);
        menu.Items.Add(editItem);
        menu.Items.Add(importantItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(deleteItem);
        row.ContextFlyout = menu;

        return row;
    }

    /// <summary>构建小图标按钮（悬停操作 / 颜色行通用）</summary>
    private static Button MakeIconButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        button.Content = new FontIcon
        {
            Glyph = glyph,
            FontSize = 13,
            FontFamily = new FontFamily(FluentGlyphs.FontFamilyName)
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    #endregion

    #region 写操作（本地 + 云同步推送）

    /// <summary>是否可以执行写操作（云同步开启时必须已从云端获取成功；需求：获取失败则一切写操作禁止）</summary>
    private bool EnsureCanWrite()
    {
        if (_busy) return false;
        if (!TodoService.IsCloudSyncEnabled) return true;
        if (_todo.CloudAvailable) return true;

        ShowSyncError("Todo.SyncFailed", null);
        return false;
    }

    /// <summary>
    /// 写操作统一入口：先改本地数据并落盘、刷新列表，云同步开启时推送到云端。
    /// 推送失败仅提示（本地已保存，下次同步以云端为准）。
    /// </summary>
    private async Task RunWriteOperationAsync(Action applyLocal)
    {
        if (!EnsureCanWrite()) return;
        _busy = true;
        try
        {
            applyLocal();
            // 本地模式：立即写 todo.json；云同步模式：不落本地，只推云端（两套数据独立）
            _todo.Persist();
            RefreshList();

            if (TodoService.IsCloudSyncEnabled)
            {
                var (ok, errorKey, raw) = await _todo.PushToCloudAsync();
                if (!ok)
                {
                    ShowSyncError(errorKey ?? "Todo.SyncFailed", raw);
                }
                else
                {
                    SyncInfoBar.IsOpen = false;
                }
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>切换完成状态</summary>
    private void ToggleCompleted(TodoItem item, bool completed)
    {
        _ = RunWriteOperationAsync(() =>
        {
            item.IsCompleted = completed;
            item.CompletedAt = completed ? DateTime.Now : null;
        });
    }

    /// <summary>切换重要标记</summary>
    private void ToggleImportant(TodoItem item)
    {
        _ = RunWriteOperationAsync(() => item.IsImportant = !item.IsImportant);
    }

    /// <summary>删除任务</summary>
    private void DeleteItem(TodoItem item)
    {
        _ = RunWriteOperationAsync(() => _todo.Data.Items.Remove(item));
    }

    /// <summary>清除全部已完成任务（右下角按钮，需求）</summary>
    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunWriteOperationAsync(() =>
            _todo.Data.Items.RemoveAll(i => i.IsCompleted));
    }

    /// <summary>底部"添加任务"按钮（清除已完成右边）：弹出新增待办弹窗，可填写全部信息</summary>
    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _ = AddTodoDialogAsync();
    }

    /// <summary>添加任务按钮：切换到行内输入框</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureCanWrite()) return;
        AddButton.Visibility = Visibility.Collapsed;
        AddTextBox.Visibility = Visibility.Visible;
        AddTextBox.Text = "";
        AddTextBox.Focus(FocusState.Programmatic);
    }

    /// <summary>行内输入框回车 = 添加任务（颜色取当前过滤色，方便连续添加同色任务）</summary>
    private void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            var text = AddTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                ExitAddMode();
                return;
            }
            _ = RunWriteOperationAsync(() =>
            {
                _todo.Data.Items.Add(new TodoItem
                {
                    Text = text,
                    Color = _colorFilter,
                    CreatedAt = DateTime.Now
                });
            });
            AddTextBox.Text = "";
            AddTextBox.Focus(FocusState.Programmatic);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ExitAddMode();
        }
    }

    /// <summary>输入框失焦：恢复"添加任务"按钮（短暂延迟避免回车添加时提前触发）</summary>
    private void AddTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ExitAddMode();
    }

    /// <summary>退出行内编辑模式，恢复"添加任务"按钮</summary>
    private void ExitAddMode()
    {
        AddTextBox.Text = "";
        AddTextBox.Visibility = Visibility.Collapsed;
        AddButton.Visibility = Visibility.Visible;
    }

    /// <summary>待办编辑弹窗的字段集合（新增/编辑弹窗共用）</summary>
    private sealed class TodoEditorFields
    {
        public TextBox TextBox = null!;
        public TextBox NotesBox = null!;
        public CalendarDatePicker DatePicker = null!;
        public string SelectedColor = TodoColors.None;
    }

    /// <summary>本周六（当天是周六则返回今天）</summary>
    private static DateTime ThisWeekSaturday()
    {
        var today = DateTime.Today;
        var diff = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(diff);
    }

    /// <summary>下周一（本周一 - 下周日区间的下一个周一，当天是周一则返回 7 天后）</summary>
    private static DateTime NextWeekMonday()
    {
        var today = DateTime.Today;
        var diff = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (diff == 0) diff = 7;
        return today.AddDays(diff);
    }

    /// <summary>
    /// 构建待办编辑弹窗的内容（新增/编辑共用）：
    /// 内容 + 备注（默认 5 行高）+ 截止日期（含 今天/明天/本周六/下周一/清除 快捷按钮）+ 颜色。
    /// </summary>
    private (StackPanel Content, TodoEditorFields Fields) BuildTodoEditor(
        string text, string notes, DateTime? due, string color)
    {
        var fields = new TodoEditorFields { SelectedColor = color };
        var content = new StackPanel { Spacing = 10 };

        // ===== 内容 =====
        content.Children.Add(new TextBlock { Text = LocalizationService.Tr("Todo.Content"), Tag = "Todo.Content" });
        fields.TextBox = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxLength = 2000
        };
        content.Children.Add(fields.TextBox);

        // ===== 备注（需求：输入框默认 5 行的高度） =====
        content.Children.Add(new TextBlock { Text = LocalizationService.Tr("Todo.Notes"), Tag = "Todo.Notes" });
        fields.NotesBox = new TextBox
        {
            Text = notes,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 118, // 约 5 行
            MaxLength = 2000
        };
        // 附加属性不能写在对象初始化器里，这里单独设置：备注超长时出现纵向滚动条
        fields.NotesBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        content.Children.Add(fields.NotesBox);

        // ===== 截止日期 + 快捷日期按钮（需求：今天/明天/本周六/下周一） =====
        content.Children.Add(new TextBlock { Text = LocalizationService.Tr("Todo.DueDate"), Tag = "Todo.DueDate" });
        fields.DatePicker = new CalendarDatePicker
        {
            Date = due,
            FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday
        };
        content.Children.Add(fields.DatePicker);

        var quickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddQuickDate(string key, DateTime? value)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = LocalizationService.Tr(key), FontSize = 12 },
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(5)
            };
            button.Click += (_, _) => fields.DatePicker.Date = value;
            quickRow.Children.Add(button);
        }
        AddQuickDate("Todo.Due.Quick.Today", DateTime.Today);
        AddQuickDate("Todo.Due.Quick.Tomorrow", DateTime.Today.AddDays(1));
        AddQuickDate("Todo.Due.Quick.Saturday", ThisWeekSaturday());
        AddQuickDate("Todo.Due.Quick.Monday", NextWeekMonday());
        // 清除截止日期（CalendarDatePicker 本身无法取消已选日期）
        AddQuickDate("Todo.DueDate.Clear", null);
        content.Children.Add(quickRow);

        // ===== 颜色（含"无颜色"选项） =====
        content.Children.Add(new TextBlock { Text = LocalizationService.Tr("Todo.Color"), Tag = "Todo.Color" });
        var colorPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void AddColorOption(string marker)
        {
            var button = new Button
            {
                Width = 30,
                Height = 26,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent)
            };
            if (marker == TodoColors.None)
            {
                // "无颜色"选项：显示文字
                button.Content = new TextBlock
                {
                    Text = LocalizationService.Tr("Todo.Color.None"),
                    FontSize = 11
                };
            }
            else
            {
                button.Content = new Border
                {
                    Width = 14,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(ColorConverter(TodoColors.GetHex(marker)))
                };
            }
            button.Tag = marker;
            button.Click += (_, _) =>
            {
                fields.SelectedColor = marker;
                foreach (var child in colorPanel.Children.OfType<Button>())
                {
                    var tag = child.Tag as string ?? TodoColors.None;
                    child.BorderBrush = tag == fields.SelectedColor
                        ? Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"] as Brush
                        : Application.Current.Resources["SubtleFillColorTransparentBrush"] as Brush;
                }
            };
            // 初始选中态
            if (marker == fields.SelectedColor)
            {
                button.BorderBrush = Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"] as Brush;
            }
            colorPanel.Children.Add(button);
        }
        AddColorOption(TodoColors.None);
        foreach (var marker in TodoColors.All)
        {
            AddColorOption(marker);
        }
        content.Children.Add(colorPanel);

        return (content, fields);
    }

    /// <summary>新增待办弹窗（底部"添加任务"按钮打开，可一次性填写全部信息）</summary>
    private async Task AddTodoDialogAsync()
    {
        if (!EnsureCanWrite()) return;

        // 新任务默认颜色 = 当前的颜色过滤色（和快捷添加行为一致）
        var (content, fields) = BuildTodoEditor("", "", null, _colorFilter);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.Tr("Todo.AddDialogTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newText = fields.TextBox.Text.Trim();
        if (newText.Length == 0) return;
        DateTimeOffset? pickedDate = fields.DatePicker.Date;
        await RunWriteOperationAsync(() =>
        {
            _todo.Data.Items.Add(new TodoItem
            {
                Text = newText,
                Notes = fields.NotesBox.Text.Trim(),
                DueDate = pickedDate?.DateTime,
                Color = TodoColors.Normalize(fields.SelectedColor),
                CreatedAt = DateTime.Now
            });
        });
    }

    /// <summary>编辑任务弹窗（内容 + 备注 + 截止日期 + 颜色）</summary>
    private async Task EditItemAsync(TodoItem item)
    {
        if (!EnsureCanWrite()) return;

        var (content, fields) = BuildTodoEditor(item.Text, item.Notes, item.DueDate, item.Color);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.Tr("Todo.EditTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newText = fields.TextBox.Text.Trim();
        DateTimeOffset? pickedDate = fields.DatePicker.Date;
        await RunWriteOperationAsync(() =>
        {
            if (newText.Length > 0)
            {
                item.Text = newText;
            }
            item.Notes = fields.NotesBox.Text.Trim();
            item.DueDate = pickedDate?.DateTime;
            item.Color = TodoColors.Normalize(fields.SelectedColor);
        });
    }

    #endregion

    #region 云同步

    /// <summary>底部"云同步"按钮：从云端重新拉取数据；成功后文字短暂变为"成功"（需求）</summary>
    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TodoService.IsCloudSyncEnabled) return;
        var ok = await ReloadFromCloudAsync(showSyncing: false);
        if (ok)
        {
            // 成功：文字短暂显示"成功"，1 秒后自动变回"云同步"
            SyncButtonText.Text = LocalizationService.Tr("Todo.SyncSuccess");
            await Task.Delay(1000);
            SyncButtonText.Text = LocalizationService.Tr("Todo.SyncRefresh");
        }
    }

    /// <summary>云同步设置变化时由 App 调用（设置页保存/关闭云同步后）：
    /// 立即更新"云同步"按钮可见性，启用时自动刷新一次（需求：不用关开窗口）</summary>
    public void OnSyncSettingsChanged()
    {
        SyncButton.Visibility = TodoService.IsCloudSyncEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (TodoService.IsCloudSyncEnabled)
        {
            _ = ReloadFromCloudAsync(showSyncing: false);
        }
        else
        {
            // 切回本地模式：从磁盘重读本地数据并刷新（云端数据留在内存，随时可切回，互不影响）
            SyncInfoBar.IsOpen = false;
            _todo.LoadLocal();
            RefreshList();
        }
    }

    /// <summary>外部变化后刷新视图（设置里改重要底色、删除/导入 todo 数据后由 App 调用）</summary>
    public void RefreshView() => RefreshList();

    /// <summary>
    /// 从云端拉取数据并刷新界面。返回是否成功。
    /// 成功：替换本地数据 + 重建列表；失败：显示错误条，之后写操作全部禁用（需求）。
    /// </summary>
    private async Task<bool> ReloadFromCloudAsync(bool showSyncing)
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            SyncButton.IsEnabled = false;
            if (showSyncing)
            {
                SyncInfoBar.Severity = InformationalSeverity;
                SyncInfoBar.Message = LocalizationService.Tr("Todo.Syncing");
                SyncInfoBar.IsOpen = true;
            }

            var (ok, errorKey, raw) = await _todo.SyncFromCloudAsync();
            if (ok)
            {
                SyncInfoBar.IsOpen = false;
            }
            else
            {
                ShowSyncError(errorKey ?? "Todo.SyncFailed", raw);
            }
            RefreshList();
            return ok;
        }
        finally
        {
            SyncButton.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>InfoBar 的"提示"级别</summary>
    private const Microsoft.UI.Xaml.Controls.InfoBarSeverity InformationalSeverity
        = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    /// <summary>显示同步错误条</summary>
    private void ShowSyncError(string errorKey, string? raw)
    {
        var message = LocalizationService.Tr(errorKey);
        if (!string.IsNullOrEmpty(raw))
        {
            message += "\n" + raw;
        }
        SyncInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        SyncInfoBar.Message = message;
        SyncInfoBar.IsOpen = true;
    }

    #endregion
}

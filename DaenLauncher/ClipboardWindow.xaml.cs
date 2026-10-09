using DaenLauncher.Models;
using DaenLauncher.Services;
using DaenLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DaenLauncher;

/// <summary>
/// 剪贴板窗口（单例，关闭 = 隐藏）。
/// 从上到下：标题栏 → 分类栏（记录/归档）→ 提示条（未启用提醒）→ 记录列表 → 底部操作栏。
/// - 点击行 = 再次复制到剪贴板，行上盖一层强调色"已复制"蒙版（1.2 秒后自动消失，需求）；
/// - 右键菜单：复制 / 修改（仅文本）/ 归档（或取消归档）/ 删除；
/// - 底部按钮随分类切换：清除全部记录 / 清除全部归档（需求）；
/// - 文本行显示内容预览，图片行显示缩略图预览（需求：图片支持预览），文件行显示路径。
/// </summary>
public sealed partial class ClipboardWindow : Window
{
    private readonly ClipboardService _clipboard = ClipboardService.Instance;
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private IntPtr _hWnd;
    private AppWindow _appWindow = null!;
    private OverlappedPresenter? _presenter;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    /// <summary>窗口位置/尺寸保存防抖计时器（"上次位置"和尺寸记忆用）</summary>
    private readonly DispatcherTimer _savePositionTimer;

    // ===== 界面状态 =====

    /// <summary>当前分类（记录/归档）</summary>
    private ClipboardViewTab _currentTab = ClipboardViewTab.Records;

    /// <summary>分类按钮（页 -> 按钮），用于更新选中态</summary>
    private readonly Dictionary<ClipboardViewTab, Button> _tabButtons = new();

    public ClipboardWindow()
    {
        InitializeComponent();

        // ===== 窗口基础设置（与待办窗口一致：关闭 = 隐藏） =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = _appWindow.Presenter as OverlappedPresenter;

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Clipboard");
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

        // 任务栏/标题栏图标（与待办窗口同款）
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
        BuildTabs();
        RefreshList();

        // 联动：语言切换 / 后台捕获到新记录时刷新列表
        LocalizationService.LanguageChanged += ApplyLocalization;
        _clipboard.DataChanged += OnClipboardDataChanged;
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（由 App.ShowClipboardWindow 调用，每次显示都会执行）</summary>
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

        // 每次显示都重新应用主题（设置窗口里可能刚切换过）+ 刷新数据
        ThemeService.Apply(RootGrid, _settings.Theme);
        RefreshList();
    }

    /// <summary>快捷键显示/隐藏切换（默认 Alt+4）</summary>
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
            _presenter.IsAlwaysOnTop = _settings.ClipboardAlwaysOnTop;
            _presenter.IsResizable = !_settings.ClipboardLockSize;
        }
    }

    /// <summary>外部变化（数据删除等）后刷新视图</summary>
    public void RefreshView() => RefreshList();

    /// <summary>后台捕获到新数据时刷新列表（窗口可见时才刷新，避免无谓重建）</summary>
    private void OnClipboardDataChanged()
    {
        if (_appWindow.IsVisible)
        {
            RefreshList();
        }
    }

    #region 窗口位置/尺寸

    /// <summary>按记忆的尺寸设置窗口大小（逻辑像素 → 物理像素）</summary>
    private void ApplyRememberedSize()
    {
        var scale = GetDpiScale();
        var width = (int)(_settings.ClipboardWindowWidth * scale);
        var height = (int)(_settings.ClipboardWindowHeight * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    /// <summary>按设置的显示位置定位窗口（与待办窗口同款）</summary>
    private void ComputeShowPosition()
    {
        // 位置计算统一走 WindowPositionHelper（多显示器/负数坐标/各屏缩放都在那里处理）
        var point = WindowPositionHelper.Compute(_hWnd, _settings.ClipboardShowPosition,
            _settings.ClipboardWindowWidth, _settings.ClipboardWindowHeight,
            _settings.ClipboardLastWindowX, _settings.ClipboardLastWindowY);
        _appWindow.Move(point);
    }

    /// <summary>窗口位置/尺寸变化：记录到设置（防抖），供"上次位置"和尺寸记忆使用</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        _settings.ClipboardLastWindowX = sender.Position.X;
        _settings.ClipboardLastWindowY = sender.Position.Y;
        // 尺寸记忆：物理像素 → 逻辑像素（AppWindow 没有 Width/Height，用 Size）
        var scale = GetDpiScale();
        if (scale > 0)
        {
            _settings.ClipboardWindowWidth = Math.Round(sender.Size.Width / scale);
            _settings.ClipboardWindowHeight = Math.Round(sender.Size.Height / scale);
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
        var loc = LocalizationService.Instance;
        TitleBarText.Text = App.GetDisplayTitle() + " " + loc.T("Main.Clipboard");
        Title = TitleBarText.Text;
    }

    #region 本地化

    /// <summary>应用界面文字（首次加载和语言切换时调用）</summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        RefreshTitle();
        DisabledInfoBar.Message = loc.T("Clipboard.NotEnabled");

        foreach (var (tab, button) in _tabButtons)
        {
            button.Content = new TextBlock { Text = loc.T(TabTextKey(tab)) };
        }
        UpdateTabVisuals();
        RefreshList();
    }

    #endregion

    #region 分类栏

    /// <summary>构建分类栏（记录 / 归档，需求）</summary>
    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        _tabButtons.Clear();
        foreach (ClipboardViewTab tab in Enum.GetValues<ClipboardViewTab>())
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

    private static string TabTextKey(ClipboardViewTab tab) => tab switch
    {
        ClipboardViewTab.Archives => "Clipboard.Tab.Archives",
        _ => "Clipboard.Tab.Records"
    };

    /// <summary>切换分类并刷新列表</summary>
    private void SelectTab(ClipboardViewTab tab)
    {
        _currentTab = tab;
        UpdateTabVisuals();
        RefreshList();
    }

    /// <summary>更新分类按钮的选中态（选中 = 浅色背景 + 加粗文字）</summary>
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

    #region 列表

    /// <summary>当前分类的数据列表（记录 = 最新的在最上；归档 = 归档时间倒序）</summary>
    private List<ClipboardEntry> GetCurrentEntries() =>
        _currentTab == ClipboardViewTab.Records ? _clipboard.Data.Items : _clipboard.Data.Archives;

    /// <summary>重建列表（数据变化/切换分类后调用）</summary>
    private void RefreshList()
    {
        ListPanel.Children.Clear();
        var entries = GetCurrentEntries();

        if (entries.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr(_currentTab == ClipboardViewTab.Records
                    ? "Clipboard.Empty" : "Clipboard.ArchiveEmpty"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
                Opacity = 0.6
            });
        }
        else
        {
            foreach (var entry in entries)
            {
                ListPanel.Children.Add(BuildEntryRow(entry));
            }
        }

        // 底部统计 + 清除按钮文字/状态（随分类切换，需求）
        CountText.Text = string.Format(
            LocalizationService.Tr(_currentTab == ClipboardViewTab.Records
                ? "Clipboard.ItemsCount" : "Clipboard.ArchiveCount"),
            entries.Count);
        ClearButtonText.Text = LocalizationService.Tr(_currentTab == ClipboardViewTab.Records
            ? "Clipboard.ClearRecords" : "Clipboard.ClearArchives");
        ClearButton.IsEnabled = entries.Count > 0;
        ClearButton.Opacity = entries.Count > 0 ? 1.0 : 0.5;

        // 未启用功能时显示提醒条（引导去设置开启，监听已停止）
        DisabledInfoBar.IsOpen = !_settings.ClipboardEnabled;
    }

    /// <summary>构建一行记录：内容（文本预览/图片缩略图/文件路径）+ 时间 + "已复制"蒙版</summary>
    private Border BuildEntryRow(ClipboardEntry entry)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            // Subtle 是半透明叠加画刷，深浅色下都和窗口底色融合
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };

        var contentStack = new StackPanel { Spacing = 4 };

        // ===== 内容区（按类型渲染） =====
        switch (entry.Kind)
        {
            case ClipboardEntryKind.Text:
                contentStack.Children.Add(new TextBlock
                {
                    Text = entry.Text,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 4,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 13
                });
                break;

            case ClipboardEntryKind.Image:
                contentStack.Children.Add(BuildImagePreview(entry));
                break;

            case ClipboardEntryKind.Files:
                contentStack.Children.Add(BuildFilesPreview(entry));
                break;
        }

        // ===== 时间小字 =====
        contentStack.Children.Add(new TextBlock
        {
            Text = FormatTime(entry.CreatedAt),
            FontSize = 11,
            Opacity = 0.55
        });

        row.Child = contentStack;

        // 点击行 = 再次复制（需求）。用 PointerUpdateKind 区分左键释放（排除右键）
        row.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.PointerUpdateKind
                == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            {
                _ = CopyEntryAsync(entry, row);
            }
        };

        // 右键菜单：复制 / 修改（仅文本）/ 归档（取消归档）/ 删除（需求）
        var menu = new MenuFlyout();

        var copyItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr("Clipboard.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) => _ = CopyEntryAsync(entry, row);
        menu.Items.Add(copyItem);

        if (entry.Kind == ClipboardEntryKind.Text)
        {
            var editItem = new MenuFlyoutItem
            {
                Text = LocalizationService.Tr("Clipboard.Edit"),
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            editItem.Click += (_, _) => _ = EditEntryAsync(entry);
            menu.Items.Add(editItem);
        }

        var archiveItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr(_currentTab == ClipboardViewTab.Records
                ? "Clipboard.Archive" : "Clipboard.Unarchive"),
            Icon = new FontIcon { Glyph = _currentTab == ClipboardViewTab.Records ? "\uE718" : "\uE7B3" }
        };
        archiveItem.Click += (_, _) =>
        {
            if (_currentTab == ClipboardViewTab.Records)
            {
                _clipboard.Archive(entry);
            }
            else
            {
                _clipboard.Unarchive(entry);
            }
        };
        menu.Items.Add(archiveItem);

        menu.Items.Add(new MenuFlyoutSeparator());
        var deleteItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr("Clipboard.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => _clipboard.Delete(entry);
        menu.Items.Add(deleteItem);

        row.ContextFlyout = menu;
        return row;
    }

    /// <summary>图片行：缩略图预览（按比例缩到最高 96px）+ 尺寸信息（需求：图片支持预览）</summary>
    private StackPanel BuildImagePreview(ClipboardEntry entry)
    {
        var panel = new StackPanel { Spacing = 4 };
        var fullPath = Path.Combine(DataPathService.ClipboardDir, entry.ImagePath);
        if (File.Exists(fullPath))
        {
            // 包一层 Border 做圆角（Image 没有 CornerRadius 属性）
            var imageBorder = new Border
            {
                MaxHeight = 96,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(4)
            };
            var image = new Image
            {
                MaxHeight = 96,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform
            };
            // DecodePixelHeight 限制解码尺寸，大图不会占爆内存
            var bitmap = new BitmapImage();
            bitmap.DecodePixelHeight = 192;
            bitmap.UriSource = new Uri(fullPath);
            image.Source = bitmap;
            imageBorder.Child = image;
            panel.Children.Add(imageBorder);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr("Clipboard.ImageMissing"),
                FontSize = 12,
                Opacity = 0.6
            });
        }

        var sizeText = entry.ImageWidth > 0 && entry.ImageHeight > 0
            ? $"{entry.ImageWidth} × {entry.ImageHeight}"
            : "";
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationService.Tr("Clipboard.ImageItem"), sizeText),
            FontSize = 12,
            Opacity = 0.7
        });
        return panel;
    }

    /// <summary>文件行：第一个路径 + "等 N 项"（需求：文件也支持再次复制）</summary>
    private StackPanel BuildFilesPreview(ClipboardEntry entry)
    {
        var panel = new StackPanel { Spacing = 4 };

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7", // Folder
            FontSize = 14,
            FontFamily = new FontFamily(FluentGlyphs.FontFamilyName)
        });
        var firstName = Path.GetFileName(entry.FilePaths[0].TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(firstName))
        {
            firstName = entry.FilePaths[0]; // 盘根目录没有文件名，显示完整路径
        }
        line.Children.Add(new TextBlock
        {
            Text = firstName,
            FontSize = 13,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(line);

        if (entry.FilePaths.Count > 1)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Format(LocalizationService.Tr("Clipboard.FilesCount"), entry.FilePaths.Count),
                FontSize = 12,
                Opacity = 0.7
            });
        }
        return panel;
    }

    /// <summary>"已复制"高亮渐隐的时长（毫秒）</summary>
    private const int CopyHighlightMs = 900;

    /// <summary>复制条目到剪贴板，成功后该行泛一下强调色浅色再渐隐（需求选定的提示样式）</summary>
    private async Task CopyEntryAsync(ClipboardEntry entry, Border row)
    {
        var ok = await _clipboard.CopyToClipboardAsync(entry);
        if (ok)
        {
            HighlightCopiedRow(row);
        }
    }

    /// <summary>
    /// 复制成功反馈：整行背景从"强调色浅色"渐隐回原来的底色（约 0.9 秒）。
    /// 用 Storyboard 的 ColorAnimation 对行背景画刷做颜色渐变，不遮挡任何内容；
    /// 动画结束后把背景还原为主题画刷（继续跟随深浅色主题切换）。
    /// </summary>
    private void HighlightCopiedRow(Border row)
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        var baseBrush = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as SolidColorBrush;

        // 动画用一份独立的画刷实例（不能直接改共享的主题画刷，会影响所有行）
        var flashBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B));
        row.Background = flashBrush;

        var animation = new ColorAnimation
        {
            From = flashBrush.Color,
            To = baseBrush?.Color ?? Colors.Transparent,
            Duration = new Duration(TimeSpan.FromMilliseconds(CopyHighlightMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, flashBrush);
        Storyboard.SetTargetProperty(animation, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            // 还原为主题画刷：主题切换时行的底色仍然跟随变化
            row.Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush;
        };
        storyboard.Begin();
    }

    /// <summary>时间显示：今天只显示时分，更早的带日期</summary>
    private static string FormatTime(DateTime time)
    {
        return time.Date == DateTime.Today
            ? time.ToString("HH:mm")
            : time.ToString("yyyy/M/d HH:mm");
    }

    #endregion

    #region 操作（复制/修改/清除）

    /// <summary>修改内容弹窗（仅文本条目，需求：弹出对话框修改内容）</summary>
    private async Task EditEntryAsync(ClipboardEntry entry)
    {
        // 多行编辑框：固定高度 + 纵向滚动条（需求），多行内容完整显示、超长滚动查看。
        // 注意：AcceptsReturn 等多行属性在构造后单独设置（避免初始化器时序问题）
        var textBox = new TextBox
        {
            Height = 260,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = ClipboardService.MaxStoredTextLength
        };
        textBox.AcceptsReturn = true;
        textBox.IsSpellCheckEnabled = false;
        textBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        textBox.Text = entry.Text;

        var editorHost = new Grid { MinWidth = 440 };
        editorHost.Children.Add(textBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.Tr("Clipboard.EditTitle"),
            Content = editorHost,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newText = textBox.Text;
        if (newText.Length > 0)
        {
            _clipboard.UpdateText(entry, newText);
        }
    }

    /// <summary>底部清除按钮：清除当前分类的全部内容（二次确认）</summary>
    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var isRecords = _currentTab == ClipboardViewTab.Records;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.Tr(isRecords ? "Clipboard.ClearRecords" : "Clipboard.ClearArchives"),
            Content = LocalizationService.Tr(isRecords
                ? "Clipboard.ClearRecordsConfirm" : "Clipboard.ClearArchivesConfirm"),
            PrimaryButtonText = LocalizationService.Tr("Dialog.Delete"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (isRecords)
        {
            _clipboard.ClearRecords();
        }
        else
        {
            _clipboard.ClearArchives();
        }
    }

    #endregion
}

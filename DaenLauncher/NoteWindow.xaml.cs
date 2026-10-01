using DaenLauncher.Controls;
using DaenLauncher.Models;
using DaenLauncher.Services;
using DaenLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher;

/// <summary>
/// 随手记窗口（单例，关闭 = 隐藏）。
/// 左侧：云同步/添加笔记按钮 + 笔记列表（只显示标题和创建时间，置顶恒在最上，按创建时间倒序，
/// 每条可设底色，右键菜单：编辑/置顶/删除）；右侧：纯文本编辑面板。
/// 左右可拖动分隔条调节宽度（受"锁定尺寸"设置限制）。
/// 云同步：沿用 webnote，多标签模式——每条笔记一个条目，每次保存提交全部笔记（覆盖式接口）。
/// 本地/云端两套数据完全独立，可随时切换（与待办同款架构）。
/// </summary>
public sealed partial class NoteWindow : Window
{
    private readonly NoteService _notes = NoteService.Instance;
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private IntPtr _hWnd;
    private OverlappedPresenter? _presenter;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    /// <summary>窗口大小保存防抖计时器</summary>
    private readonly DispatcherTimer _saveSizeTimer;

    /// <summary>窗口位置保存防抖计时器（"上次位置"用）</summary>
    private readonly DispatcherTimer _savePositionTimer;

    /// <summary>编辑器内容保存防抖计时器（停止输入约 800ms 后落盘/推送）</summary>
    private readonly DispatcherTimer _saveTextTimer;

    /// <summary>分隔条拖动中标记</summary>
    private bool _isDraggingSplitter;

    /// <summary>同步/写入操作进行中标记（防抖）</summary>
    private bool _busy;

    /// <summary>当前选中的笔记（右侧编辑器正在编辑的）</summary>
    private NoteItem? _selectedNote;

    /// <summary>加载笔记到编辑器时置 true，避免 TextChanged 触发保存</summary>
    private bool _suppressEditorEvents;

    public NoteWindow()
    {
        InitializeComponent();

        // ===== 防抖计时器（必须先于窗口事件订阅创建） =====
        _saveSizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveSizeTimer.Tick += SaveSizeTimer_Tick;
        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _savePositionTimer.Tick += SavePositionTimer_Tick;
        _saveTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTextTimer.Tick += SaveTextTimer_Tick;

        // ===== 窗口基础设置（与待办窗口一致：关闭 = 隐藏） =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        var appWindow = AppWindow;
        _presenter = appWindow.Presenter as OverlappedPresenter;

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Note");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

        appWindow.Closing += (_, e) =>
        {
            // 关闭（隐藏/退出）前把待保存的内容落盘/推送，窗口位置和大小立即落盘
            SaveOnClose();
            SavePositionNow();
            SaveSizeNow();
            if (!App.IsExiting)
            {
                e.Cancel = true;
                appWindow.Hide();
            }
        };

        // 记录窗口位置（拖动/移动后防抖保存，"上次位置"显示策略用）
        appWindow.Changed += AppWindow_Changed;

        // 分隔条悬停光标（需求：左右箭头提示可拖动，否则用户不知道能拖）
        Splitter.PointerEntered += (_, _) => Splitter.SetSplitterCursor(true);
        Splitter.PointerExited += (_, _) => Splitter.SetSplitterCursor(false);

        // 任务栏/标题栏图标
        try
        {
            var iconPath = IconService.EnsureAppIconExtracted();
            if (iconPath != null) appWindow.SetIcon(iconPath);
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
        ThemeService.ApplyCaptionButtonColors(appWindow, RootGrid);

        // 窗口尺寸（记住上次大小）+ 左栏宽度
        appWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(_settings.NoteWindowWidth * GetDpiScale()),
            (int)(_settings.NoteWindowHeight * GetDpiScale())));
        LeftColumn.Width = new GridLength(_settings.NoteLeftPaneWidth);

        ApplyBehaviorSettings();
        this.SizeChanged += NoteWindow_SizeChanged;

        _ = LoadTitleBarIconAsync();
        ApplyLocalization();
        BuildEditorColorPanel();
        RebuildNoteList();

        LocalizationService.LanguageChanged += ApplyLocalization;
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（由 App.ShowNoteWindow 调用，每次显示都会执行）</summary>
    public void ActivateAndBringToFront()
    {
        ApplyBehaviorSettings();

        if (!AppWindow.IsVisible)
        {
            ComputeShowPosition();
            AppWindow.Show();
        }
        else
        {
            AppWindow.Show(true);
        }
        Activate();

        // 每次显示都重新应用主题（新窗口联动规则：标题/材质/主题）
        ThemeService.Apply(RootGrid, _settings.Theme);

        // 云同步状态可能在上次显示后被修改：重算"云同步"按钮可见性 + 自动刷新
        OnSyncSettingsChanged();
    }

    /// <summary>快捷键显示/隐藏切换（默认 Alt+3）</summary>
    public void ToggleViaHotkey()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else
        {
            ActivateAndBringToFront();
        }
    }

    /// <summary>应用随手记窗口行为设置（永远置顶/锁定尺寸；锁定时分隔条不可拖——
    /// SplitterGrid 不是 Control 没有 IsEnabled，拖动入口里直接按设置拦截）</summary>
    public void ApplyBehaviorSettings()
    {
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _settings.NoteAlwaysOnTop;
            _presenter.IsResizable = !_settings.NoteLockSize;
        }
    }

    /// <summary>按设置的显示位置定位窗口（支持"上次位置"）</summary>
    private void ComputeShowPosition()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var wa = displayArea.WorkArea;

        var scale = GetDpiScale();
        var width = (int)(_settings.NoteWindowWidth * scale);
        var height = (int)(_settings.NoteWindowHeight * scale);

        int x, y;
        switch (_settings.NoteShowPosition)
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
                if (_settings.NoteLastWindowX >= 0 && _settings.NoteLastWindowY >= 0)
                {
                    x = _settings.NoteLastWindowX;
                    y = _settings.NoteLastWindowY;
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

        if (x < wa.X) x = wa.X;
        if (y < wa.Y) y = wa.Y;
        if (x + width > wa.X + wa.Width) x = wa.X + wa.Width - width;
        if (y + height > wa.Y + wa.Height) y = wa.Y + wa.Height - height;

        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

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
        TitleBarText.Text = App.GetDisplayTitle() + " " + loc.T("Main.Note");
        Title = TitleBarText.Text;
    }

    #region 本地化

    /// <summary>应用界面文字（首次加载和语言切换时调用）</summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        RefreshTitle();
        ToolTipService.SetToolTip(SyncButton, loc.T("Note.SyncTooltip"));
        SyncButtonText.Text = loc.T("Note.Sync");
        AddButtonText.Text = loc.T("Note.Add");
        SaveButtonText.Text = loc.T("Dialog.Save");
        EditorTitleBox.PlaceholderText = loc.T("Note.TitleLabel");
        EditorTextBox.PlaceholderText = loc.T("Note.EditorPlaceholder");
        RebuildNoteList();
        UpdateEditorControls();
    }

    #endregion

    #region 笔记列表

    /// <summary>显示排序：置顶恒在最上（需求），其余按创建时间倒序（默认最新在上，不按修改时间）</summary>
    private List<NoteItem> SortedNotes()
    {
        return _notes.Data.Notes
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
    }

    /// <summary>重建笔记列表（数据变化后调用）</summary>
    private void RebuildNoteList()
    {
        ListPanel.Children.Clear();
        var notes = SortedNotes();

        if (notes.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr("Note.Empty"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 0),
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
        }
        else
        {
            foreach (var note in notes)
            {
                ListPanel.Children.Add(BuildNoteCard(note));
            }
        }
    }

    /// <summary>构建一张笔记卡片：标题 + 创建时间（需求：不显示内容摘要）+ 置顶图钉 + 底色</summary>
    private Border BuildNoteCard(NoteItem note)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8),
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1),
            Tag = note
        };

        // 底色（需求：每条笔记支持设置底色；低不透明度叠色，深浅色下文字都清晰）
        if (note.Color != TodoColors.None)
        {
            var hex = TodoColors.GetHex(note.Color);
            row.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                34,
                Convert.ToByte(hex[1..3], 16),
                Convert.ToByte(hex[3..5], 16),
                Convert.ToByte(hex[5..7], 16)));
        }

        // 选中态：描边高亮
        if (note == _selectedNote)
        {
            row.BorderThickness = new Thickness(2);
            row.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 置顶图钉
        if (note.IsPinned)
        {
            var pinIcon = new FontIcon
            {
                Glyph = "\uE841", // Pin
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            };
            grid.Children.Add(pinIcon);
            Grid.SetColumn(pinIcon, 0);
        }

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        textStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title)
                ? LocalizationService.Tr("Note.Untitled")
                : note.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        textStack.Children.Add(new TextBlock
        {
            Text = note.CreatedAt.ToString("yyyy/M/d HH:mm"),
            FontSize = 11,
            Opacity = 0.6
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        row.Child = grid;

        // 左键按下选中笔记（需求）。
        // 注意：必须只响应左键——右键也会触发 PointerPressed，如果右键也重建列表，
        // 被右键的卡片会在 ContextFlyout 弹出前被替换掉，菜单就弹不出来了
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                SelectNote(note);
            }
        };
        // 右键菜单：置顶 / 删除（需求：不需要"编辑"，标题等都在右侧编辑面板里改）
        var menu = new MenuFlyout();
        var pinItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr(note.IsPinned ? "Note.Unpin" : "Note.Pin"),
            Icon = new FontIcon { Glyph = note.IsPinned ? "\uE77A" : "\uE841" } // Unpin : Pin
        };
        pinItem.Click += (_, _) => TogglePin(note);
        var deleteItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Tr("Note.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => DeleteNote(note);
        menu.Items.Add(pinItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(deleteItem);
        row.ContextFlyout = menu;

        return row;
    }

    /// <summary>选中笔记：右侧编辑面板加载全部信息（标题/正文/底色/置顶，需求）</summary>
    private void SelectNote(NoteItem note)
    {
        _selectedNote = note;
        _suppressEditorEvents = true;
        EditorTitleBox.Text = note.Title;
        EditorTextBox.Text = note.Text;
        _suppressEditorEvents = false;
        SetEditorEnabled(true);
        UpdateEditorControls();
        RebuildNoteList();
    }

    /// <summary>清空编辑面板（无选中笔记/切换数据源时）</summary>
    private void ClearEditor()
    {
        _selectedNote = null;
        _suppressEditorEvents = true;
        EditorTitleBox.Text = "";
        EditorTextBox.Text = "";
        _suppressEditorEvents = false;
        SetEditorEnabled(false);
        UpdateEditorControls();
    }

    /// <summary>编辑面板整体启用/禁用（有选中笔记才可编辑）。
    /// 注意 StackPanel 不是 Control 没有 IsEnabled，色块要逐个设置</summary>
    private void SetEditorEnabled(bool enabled)
    {
        EditorTitleBox.IsEnabled = enabled;
        EditorTextBox.IsEnabled = enabled;
        PinButton.IsEnabled = enabled;
        foreach (var child in EditorColorPanel.Children.OfType<Button>())
        {
            child.IsEnabled = enabled;
        }
    }

    /// <summary>刷新编辑面板的显示状态（创建时间、底色选中态、置顶按钮图标）</summary>
    private void UpdateEditorControls()
    {
        if (_selectedNote == null)
        {
            EditorTimeText.Text = "";
            UpdateEditorColorVisual(TodoColors.None);
            UpdatePinVisual(false);
            return;
        }
        EditorTimeText.Text = LocalizationService.Tr("Note.CreatedAt") + " " +
                              _selectedNote.CreatedAt.ToString("yyyy/M/d HH:mm");
        UpdateEditorColorVisual(_selectedNote.Color);
        UpdatePinVisual(_selectedNote.IsPinned);
    }

    /// <summary>更新底色色块的选中描边</summary>
    private void UpdateEditorColorVisual(string selected)
    {
        foreach (var child in EditorColorPanel.Children.OfType<Button>())
        {
            var tag = child.Tag as string ?? TodoColors.None;
            child.BorderBrush = tag == selected
                ? Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"] as Brush
                : Application.Current.Resources["SubtleFillColorTransparentBrush"] as Brush;
        }
    }

    /// <summary>更新置顶按钮（图标 + 悬停提示）</summary>
    private void UpdatePinVisual(bool pinned)
    {
        PinButton.Content = new FontIcon
        {
            Glyph = pinned ? "\uE77A" : "\uE841", // Unpin : Pin
            FontSize = 14
        };
        ToolTipService.SetToolTip(PinButton,
            LocalizationService.Tr(pinned ? "Note.Unpin" : "Note.Pin"));
    }

    /// <summary>构建编辑面板里的底色色块行（无颜色 + 8 色，需求：颜色在面板里直接设置）</summary>
    private void BuildEditorColorPanel()
    {
        EditorColorPanel.Children.Clear();
        void AddColorOption(string marker)
        {
            var button = new Button
            {
                Width = 34,
                Height = 26,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent)
            };
            if (marker == TodoColors.None)
            {
                button.Content = new TextBlock
                {
                    Text = LocalizationService.Tr("Todo.Color.None"),
                    FontSize = 11
                };
            }
            else
            {
                var hex = TodoColors.GetHex(marker);
                button.Content = new Border
                {
                    Width = 16,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                        90,
                        Convert.ToByte(hex[1..3], 16),
                        Convert.ToByte(hex[3..5], 16),
                        Convert.ToByte(hex[5..7], 16)))
                };
            }
            button.Tag = marker;
            button.Click += (_, _) =>
            {
                if (_selectedNote == null) return;
                _ = RunWriteOperationAsync(() => _selectedNote.Color = marker);
            };
            EditorColorPanel.Children.Add(button);
        }
        AddColorOption(TodoColors.None);
        foreach (var marker in TodoColors.All)
        {
            AddColorOption(marker);
        }
    }

    #endregion

    #region 写操作（手动保存，需求）

    /// <summary>是否存在未保存的修改（需求：有修改时"保存"按钮亮起，点击才同步）</summary>
    private bool _isDirty;

    /// <summary>是否可以执行写操作（云同步开启时必须已从云端获取成功；获取失败则写操作禁用）</summary>
    private bool EnsureCanWrite()
    {
        if (_busy) return false;
        if (!NoteService.IsCloudSyncEnabled) return true;
        if (_notes.CloudAvailable) return true;

        ShowSyncError("Note.SyncFailed", null);
        return false;
    }

    /// <summary>
    /// 写操作统一入口：只改内存数据并刷新界面，同时标记"有未保存修改"（保存按钮亮起）。
    /// 不再自动落盘/推送（需求：点"保存"按钮才同步，避免请求过于频繁被 webnote 封禁）。
    /// </summary>
    private Task RunWriteOperationAsync(Action applyLocal)
    {
        if (!EnsureCanWrite()) return Task.CompletedTask;
        applyLocal();
        _isDirty = true;
        UpdateSaveButton();
        RebuildNoteList();
        UpdateEditorControls();
        return Task.CompletedTask;
    }

    /// <summary>更新"保存"按钮的亮起状态（有未保存修改 = 可点击）</summary>
    private void UpdateSaveButton()
    {
        SaveButton.IsEnabled = _isDirty;
    }

    /// <summary>把编辑面板里尚未并入内存的标题/正文应用到内存（防抖结束或点保存时调用）</summary>
    private void ApplyPendingEdits()
    {
        if (_selectedNote == null) return;
        var title = EditorTitleBox.Text.Trim();
        var text = EditorTextBox.Text;
        if (_selectedNote.Title == title && _selectedNote.Text == text) return;
        _selectedNote.Title = title;
        _selectedNote.Text = text;
        _isDirty = true;
    }

    /// <summary>"保存"按钮：本地模式写本地文件；云同步模式推送云端（需求：手动保存）</summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyPendingEdits();
        if (!_isDirty || _busy) return;
        if (!EnsureCanWrite()) return;

        if (!NoteService.IsCloudSyncEnabled)
        {
            _notes.Persist();
            _isDirty = false;
            UpdateSaveButton();
            RebuildNoteList();
            UpdateEditorControls();
            return;
        }

        _busy = true;
        try
        {
            SaveButton.IsEnabled = false;
            var (ok, errorKey, raw) = await _notes.PushToCloudAsync();
            if (ok)
            {
                _isDirty = false;
                SyncInfoBar.IsOpen = false;
            }
            else
            {
                ShowSyncError(errorKey ?? "Note.SyncFailed", raw);
            }
        }
        finally
        {
            _busy = false;
            UpdateSaveButton();
            RebuildNoteList();
            UpdateEditorControls();
        }
    }

    /// <summary>窗口关闭（隐藏/退出）前的保存：本地模式立即落盘；
    /// 云同步模式尽力推送一次（不阻塞关闭，避免丢改动）</summary>
    private void SaveOnClose()
    {
        ApplyPendingEdits();
        if (!_isDirty) return;
        if (!NoteService.IsCloudSyncEnabled)
        {
            _notes.Persist();
            _isDirty = false;
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try { await _notes.PushToCloudAsync(); } catch { /* 尽力而为 */ }
            });
        }
    }

    /// <summary>添加笔记（需求：不弹窗，直接创建并在右侧编辑面板里填
    /// 标题/正文/底色/置顶）：新建空笔记并选中，光标进标题框</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureCanWrite()) return;

        var note = new NoteItem { Title = "", CreatedAt = DateTime.Now };
        _ = RunWriteOperationAsync(() => _notes.Data.Notes.Add(note));
        SelectNote(note);
        EditorTitleBox.Focus(FocusState.Programmatic);
    }

    /// <summary>置顶/取消置顶（置顶的始终排在最上面，需求）</summary>
    private void TogglePin(NoteItem note)
    {
        _ = RunWriteOperationAsync(() => note.IsPinned = !note.IsPinned);
    }

    /// <summary>置顶按钮（编辑面板里）：切换当前笔记的置顶状态</summary>
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote == null) return;
        TogglePin(_selectedNote);
    }

    /// <summary>删除笔记（需求：右键菜单"删除"）</summary>
    private void DeleteNote(NoteItem note)
    {
        _ = RunWriteOperationAsync(() =>
        {
            _notes.Data.Notes.Remove(note);
            if (_selectedNote == note)
            {
                ClearEditor();
            }
        });
    }

    #endregion

    #region 编辑器（标题/正文防抖并入内存）

    /// <summary>标题或正文变化：防抖 800ms 后并入内存（不自动落盘/推送，需求：手动保存）</summary>
    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedNote == null) return;
        _saveTextTimer.Stop();
        _saveTextTimer.Start();
    }

    private void EditorTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedNote == null) return;
        _saveTextTimer.Stop();
        _saveTextTimer.Start();
    }

    private void SaveTextTimer_Tick(object? sender, object e)
    {
        _saveTextTimer.Stop();
        ApplyPendingEdits();
        RebuildNoteList();
        UpdateSaveButton();
    }

    #endregion

    #region 分隔条拖动（受"锁定尺寸"限制，需求）

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.NoteLockSize) return;
        _isDraggingSplitter = true;
        Splitter.CapturePointer(e.Pointer);
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        var rootPosition = e.GetCurrentPoint(RootGrid).Position;
        var newWidth = Math.Clamp(rootPosition.X - 8, 160, RootGrid.ActualWidth - 300);
        LeftColumn.Width = new GridLength(newWidth);
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        _isDraggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
        _settings.NoteLeftPaneWidth = LeftColumn.Width.Value;
        SettingsService.Instance.Save();
    }

    #endregion

    #region 窗口大小/位置记忆

    private void NoteWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (!_settings.NoteLockSize)
        {
            _settings.NoteWindowWidth = args.Size.Width;
            _settings.NoteWindowHeight = args.Size.Height;
            _saveSizeTimer.Stop();
            _saveSizeTimer.Start();
        }
    }

    private void SaveSizeTimer_Tick(object? sender, object e)
    {
        _saveSizeTimer.Stop();
        SettingsService.Instance.Save();
    }

    private void SaveSizeNow()
    {
        if (_saveSizeTimer.IsEnabled)
        {
            _saveSizeTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        _settings.NoteLastWindowX = sender.Position.X;
        _settings.NoteLastWindowY = sender.Position.Y;
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void SavePositionTimer_Tick(object? sender, object e)
    {
        _savePositionTimer.Stop();
        SettingsService.Instance.Save();
    }

    private void SavePositionNow()
    {
        if (_savePositionTimer.IsEnabled)
        {
            _savePositionTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    #endregion

    #region 云同步

    /// <summary>左侧"云同步"按钮：从云端重新拉取；成功后文字短暂变"成功"（与待办同款交互）</summary>
    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NoteService.IsCloudSyncEnabled) return;
        var ok = await ReloadFromCloudAsync();
        if (ok)
        {
            SyncButtonText.Text = LocalizationService.Tr("Todo.SyncSuccess");
            await Task.Delay(1000);
            SyncButtonText.Text = LocalizationService.Tr("Note.Sync");
        }
    }

    /// <summary>云同步设置变化时由 App 调用（设置页保存/关闭云同步后）：
    /// 立即更新"云同步"按钮可见性，启用时自动刷新一次，关闭时切回本地数据</summary>
    public void OnSyncSettingsChanged()
    {
        SyncButton.Visibility = NoteService.IsCloudSyncEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (NoteService.IsCloudSyncEnabled)
        {
            _ = ReloadFromCloudAsync();
        }
        else
        {
            SyncInfoBar.IsOpen = false;
            _notes.LoadLocal();
            _isDirty = false;
            UpdateSaveButton();
            ClearEditor();
            RebuildNoteList();
        }
    }

    /// <summary>外部变化（删除/导入 note 数据）后刷新视图</summary>
    public void RefreshView()
    {
        _notes.LoadLocal();
        _isDirty = false;
        UpdateSaveButton();
        ClearEditor();
        RebuildNoteList();
    }

    /// <summary>从云端拉取数据并刷新界面。返回是否成功。</summary>
    private async Task<bool> ReloadFromCloudAsync()
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            SyncButton.IsEnabled = false;
            var (ok, errorKey, raw) = await _notes.SyncFromCloudAsync();
            if (ok)
            {
                SyncInfoBar.IsOpen = false;
                // 拉取后以云端为准：未保存的修改标记清除
                _isDirty = false;
                UpdateSaveButton();
            }
            else
            {
                ShowSyncError(errorKey ?? "Note.SyncFailed", raw);
            }
            // 拉取后原选中笔记可能已不存在（云端被其他设备删除）：清掉编辑面板
            if (_selectedNote != null && _notes.Data.Notes.All(n => n.Id != _selectedNote.Id))
            {
                ClearEditor();
            }
            RebuildNoteList();
            UpdateEditorControls();
            return ok;
        }
        finally
        {
            SyncButton.IsEnabled = true;
            _busy = false;
        }
    }

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

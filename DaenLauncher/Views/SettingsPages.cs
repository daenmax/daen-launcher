using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher.Views;

/// <summary>语言选择、自启动、启动行为</summary>
public sealed class GeneralPage : SettingsPageBase
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    public GeneralPage()
    {
        BuildLanguageCard();
        BuildAutoStartCard();
        BuildStartBehaviorCard();
    }

    private void BuildLanguageCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 180,
            ItemsSource = LocalizationService.Instance.GetAvailableLanguages(),
            SelectedItem = _settings.Language
        };
        var websiteButton = new Button
        {
            Content = LocalizationService.Tr("Settings.Language.Tutorial"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        websiteButton.Click += (_, _) => SettingsActions.OpenUrl(AppInfoService.Config.WebsiteUrl);

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(comboBox);
        stack.Children.Add(websiteButton);

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is not string language) return;
            _settings.Language = language;
            SettingsService.Instance.Save();
            // 切换后立即生效：SetLanguage 触发 LanguageChanged，
            // 主窗口/设置窗口/托盘菜单都会实时刷新文字（参考 DeskBox）
            LocalizationService.Instance.SetLanguage(language);
        };

        MakeCard(FluentGlyphs.Globe, "Settings.Language", "Settings.Language.Sub", stack);
    }

    private void BuildAutoStartCard()
    {
        var checkBox = new CheckBox
        {
            Content = LocalizationService.Tr("Settings.AutoStart.Enable"),
            IsChecked = _settings.AutoStart
        };
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            Margin = new Thickness(12, 0, 0, 0),
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.AutoStart.Registry"),
                LocalizationService.Tr("Settings.AutoStart.StartupFolder")
            },
            SelectedIndex = _settings.AutoStartMode == AutoStartMode.Registry ? 0 : 1
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(checkBox);
        stack.Children.Add(comboBox);

        checkBox.Checked += (_, _) => { _settings.AutoStart = true; SaveAndApply(); };
        checkBox.Unchecked += (_, _) => { _settings.AutoStart = false; SaveAndApply(); };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.AutoStartMode = comboBox.SelectedIndex == 0 ? AutoStartMode.Registry : AutoStartMode.StartupFolder;
            SaveAndApply();
        };

        MakeCard(FluentGlyphs.PowerButton, "Settings.AutoStart", "Settings.AutoStart.Sub", stack);
    }

    private void SaveAndApply()
    {
        SettingsService.Instance.Save();
        AutoStartService.Apply(_settings);
    }

    private void BuildStartBehaviorCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.StartBehavior.Show"),
                LocalizationService.Tr("Settings.StartBehavior.Hide")
            },
            SelectedIndex = _settings.StartBehavior == StartBehavior.Show ? 0 : 1
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.StartBehavior = comboBox.SelectedIndex == 0 ? StartBehavior.Show : StartBehavior.Hide;
            SettingsService.Instance.Save();
        };
        MakeCard(FluentGlyphs.RedEye, "Settings.StartBehavior", "Settings.StartBehavior.Sub", comboBox);
    }
}

/// <summary>主题、材质、标题</summary>
public sealed class AppearancePage : SettingsPageBase
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    public AppearancePage()
    {
        BuildThemeCard();
        BuildBackdropCard();
        BuildTitleCard();
    }

    private void BuildThemeCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Theme.System"),
                LocalizationService.Tr("Settings.Theme.Light"),
                LocalizationService.Tr("Settings.Theme.Dark")
            },
            SelectedIndex = (int)_settings.Theme
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.Theme = (ThemeMode)comboBox.SelectedIndex;
            SettingsService.Instance.Save();
            // 切换后立即生效（参考 DeskBox：RequestedTheme + 标题栏按钮配色同步刷新）
            App.Instance.ApplyThemeEverywhere();
        };
        MakeCard(FluentGlyphs.Color, "Settings.Theme", "Settings.Theme.Sub", comboBox);
    }

    private void BuildBackdropCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Backdrop.Acrylic"),
                LocalizationService.Tr("Settings.Backdrop.Mica")
            },
            SelectedIndex = _settings.Backdrop == BackdropKind.Acrylic ? 0 : 1
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.Backdrop = comboBox.SelectedIndex == 0 ? BackdropKind.Acrylic : BackdropKind.Mica;
            SettingsService.Instance.Save();
            // 切换后立即生效（需求）
            BackdropService.Apply(App.Instance.GetMainWindow(), _settings.Backdrop);
            if (SettingsWindow.CurrentInstance != null)
            {
                BackdropService.Apply(SettingsWindow.CurrentInstance, _settings.Backdrop);
            }
        };
        MakeCard(FluentGlyphs.BackToWindow, "Settings.Backdrop", "Settings.Backdrop.Sub", comboBox);
    }

    private void BuildTitleCard()
    {
        var textBox = new TextBox
        {
            MinWidth = 240,
            PlaceholderText = AppInfoService.Config.AppName,
            Text = _settings.CustomTitle
        };
        // 确定按钮：点击后立即生效（需求-BUG3），生效范围为主窗口/设置窗口/托盘提示
        void ApplyTitle()
        {
            _settings.CustomTitle = textBox.Text;
            SettingsService.Instance.Save();
            App.Instance.RefreshAllTitles();
        }
        var okButton = new Button
        {
            Content = LocalizationService.Tr("Dialog.Ok"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        okButton.Click += (_, _) => ApplyTitle();
        // 回车等同点确定
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                ApplyTitle();
            }
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(textBox);
        stack.Children.Add(okButton);
        MakeCard(FluentGlyphs.Edit, "Settings.CustomTitle", "Settings.CustomTitle.Sub", stack);
    }
}

/// <summary>数据导出/导入/删除</summary>
public sealed class DataPage : SettingsPageBase
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private readonly CheckBox _exportConfig = new();
    private readonly CheckBox _exportLauncher = new();
    private readonly CheckBox _exportTodo = new();
    private readonly CheckBox _exportNote = new();
    private readonly CheckBox _exportClipboard = new();

    public DataPage()
    {
        BuildOpenFolderCard();
        BuildExportCard();
        BuildImportCard();
        BuildDeleteCard();
        RefreshCheckStates();
    }

    /// <summary>打开配置文件目录卡片：显示实际使用的 data 目录路径，并可一键用资源管理器打开。
    /// 显示真实路径是为了让用户一眼看出数据实际在哪（exe 同级不可写时会回退到 LocalAppData）。</summary>
    private void BuildOpenFolderCard()
    {
        var pathText = new TextBlock
        {
            Text = DataPathService.DataRoot,
            FontSize = 12,
            Opacity = 0.7,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };

        var openButton = new Button { Content = LocalizationService.Tr("Data.OpenFolder.Button"), Margin = new Thickness(0, 8, 0, 0) };
        openButton.Click += (_, _) => OpenDataFolder();

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(pathText);
        content.Children.Add(openButton);
        MakeCard(FluentGlyphs.Folder, "Data.OpenFolder", "Data.OpenFolder.Sub", content);
    }

    /// <summary>用资源管理器打开当前使用的 data 根目录</summary>
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{DataPathService.DataRoot}\""
            });
        }
        catch
        {
            // 打开失败（极罕见）不影响其他功能
        }
    }

    /// <summary>导出勾选框行</summary>
    private StackPanel BuildCheckRow(CheckBox config, CheckBox launcher, CheckBox todo,
        CheckBox note, CheckBox clipboard)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(config);
        stack.Children.Add(launcher);
        stack.Children.Add(todo);
        stack.Children.Add(note);
        stack.Children.Add(clipboard);
        return stack;
    }

    private void RefreshCheckStates()
    {
        var loc = LocalizationService.Instance;
        _exportConfig.Content = loc.T("Data.Item.Config");
        _exportLauncher.Content = loc.T("Data.Item.Launcher");
        _exportTodo.Content = loc.T("Data.Item.Todo");
        _exportNote.Content = loc.T("Data.Item.Note");
        _exportClipboard.Content = loc.T("Data.Item.Clipboard");
        _exportConfig.IsChecked = true;
        _exportLauncher.IsChecked = true;
    }

    private void BuildExportCard()
    {
        var exportButton = new Button { Content = LocalizationService.Tr("Data.Export.Button"), Margin = new Thickness(0, 8, 0, 0) };
        exportButton.Click += async (_, _) => await ExportAsync();

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(BuildCheckRow(_exportConfig, _exportLauncher, _exportTodo, _exportNote, _exportClipboard));
        content.Children.Add(exportButton);
        MakeCard(FluentGlyphs.Save, "Data.Export", "Data.Export.Sub", content);
    }

    private async Task ExportAsync()
    {
        var keys = CollectChecked(
            (_exportConfig, "config"), (_exportLauncher, "launcher"), (_exportTodo, "todo"),
            (_exportNote, "note"), (_exportClipboard, "clipboard"));
        if (keys.Count == 0)
        {
            await ShowInfo(LocalizationService.Tr("Data.NoSelection"));
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance.GetMainWindow().WindowHandle);
        picker.SuggestedFileName = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        picker.FileTypeChoices.Add("zip", new List<string> { ".zip" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            DataService.Export(file.Path, keys);
            await ShowInfo(LocalizationService.Tr("Data.Export.Done"));
        }
        catch (Exception ex)
        {
            // 附上具体异常原因，方便定位（如目标被占用、路径无权限等）
            await ShowInfo(LocalizationService.Tr("Data.Export.Failed") + "\n" + ex.Message);
        }
    }

    private void BuildImportCard()
    {
        var importButton = new Button { Content = LocalizationService.Tr("Data.Import.Button") };
        importButton.Click += async (_, _) => await ImportAsync();
        MakeCard(FluentGlyphs.OpenFile, "Data.Import", "Data.Import.Sub", importButton);
    }

    private async Task ImportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance.GetMainWindow().WindowHandle);
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        // 解析 zip 里有哪些数据项，没有的置灰不可点（需求）
        HashSet<string> available;
        try
        {
            available = DataService.AnalyzeZip(file.Path);
        }
        catch
        {
            await ShowInfo(LocalizationService.Tr("Data.Import.InvalidZip"));
            return;
        }

        var loc = LocalizationService.Instance;
        var boxes = new Dictionary<string, CheckBox>
        {
            ["config"] = new() { Content = loc.T("Data.Item.Config"), IsEnabled = available.Contains("config") },
            ["launcher"] = new() { Content = loc.T("Data.Item.Launcher"), IsEnabled = available.Contains("launcher") },
            ["todo"] = new() { Content = loc.T("Data.Item.Todo"), IsEnabled = available.Contains("todo") },
            ["note"] = new() { Content = loc.T("Data.Item.Note"), IsEnabled = available.Contains("note") },
            ["clipboard"] = new() { Content = loc.T("Data.Item.Clipboard"), IsEnabled = available.Contains("clipboard") }
        };
        foreach (var box in boxes.Values)
        {
            if (box.IsEnabled) box.IsChecked = true;
        }

        var content = new StackPanel { Spacing = 4 };
        foreach (var box in boxes.Values) content.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = LocalizationService.Tr("Data.Import.SelectTitle"),
            Content = content,
            PrimaryButtonText = loc.T("Data.Import.Button"),
            CloseButtonText = loc.T("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selected = boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key).ToList();
        if (selected.Count == 0) return;

        try
        {
            DataService.Import(file.Path, selected);
            await ShowInfo(LocalizationService.Tr("Data.Import.Done"));
            SettingsActions.PromptRestart(Content.XamlRoot);
        }
        catch
        {
            await ShowInfo(LocalizationService.Tr("Data.Import.Failed"));
        }
    }

    private readonly CheckBox _deleteConfig = new();
    private readonly CheckBox _deleteLauncher = new();
    private readonly CheckBox _deleteTodo = new();
    private readonly CheckBox _deleteNote = new();
    private readonly CheckBox _deleteClipboard = new();

    private void BuildDeleteCard()
    {
        var loc = LocalizationService.Instance;
        _deleteConfig.Content = loc.T("Data.Item.Config");
        _deleteLauncher.Content = loc.T("Data.Item.Launcher");
        _deleteTodo.Content = loc.T("Data.Item.Todo");
        _deleteNote.Content = loc.T("Data.Item.Note");
        _deleteClipboard.Content = loc.T("Data.Item.Clipboard");

        var deleteButton = new Button
        {
            Content = loc.T("Data.Delete.Button"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        deleteButton.Click += async (_, _) =>
        {
            var selected = CollectChecked(
                (_deleteConfig, "config"), (_deleteLauncher, "launcher"), (_deleteTodo, "todo"),
                (_deleteNote, "note"), (_deleteClipboard, "clipboard"));
            if (selected.Count == 0)
            {
                await ShowInfo(loc.T("Data.NoSelection"));
                return;
            }

            // 二次确认（需求）
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = loc.T("Data.Delete.ConfirmTitle"),
                Content = loc.T("Data.Delete.ConfirmContent"),
                PrimaryButtonText = loc.T("Dialog.Delete"),
                CloseButtonText = loc.T("Dialog.Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            DataService.DeleteData(selected);

            // 启动器数据：从磁盘重新加载并立即刷新主窗口（无需重启，需求-BUG2）
            if (selected.Contains("launcher"))
            {
                LauncherDataService.Instance.Reload();
                App.Instance.ReloadMainWindowData();
            }

            await ShowInfo(loc.T("Data.Delete.Done"));

            // 软件配置（设置/语言）在内存里有缓存，立即生效需要重启
            if (selected.Contains("config"))
            {
                SettingsActions.PromptRestart(Content.XamlRoot);
            }
        };

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(BuildCheckRow(_deleteConfig, _deleteLauncher, _deleteTodo, _deleteNote, _deleteClipboard));
        content.Children.Add(deleteButton);
        MakeCard(FluentGlyphs.Delete, "Data.Delete", "Data.Delete.Sub", content);
    }

    /// <summary>收集勾选的数据项 key</summary>
    private static List<string> CollectChecked(params (CheckBox Box, string Key)[] items) =>
        items.Where(i => i.Box.IsChecked == true).Select(i => i.Key).ToList();

    private async Task ShowInfo(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Content = message,
            CloseButtonText = LocalizationService.Tr("Dialog.Ok"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}

/// <summary>启动器设置（最复杂的一页）</summary>
public sealed class LauncherPage : SettingsPageBase
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    public LauncherPage()
    {
        BuildCategoryCard();
        BuildSubCategoryStyleCard();
        BuildTriggersCard();
        BuildAlwaysOnTopCard();
        BuildLockSizeCard();
        BuildLockIconsCard();
        BuildShowPositionCard();
        BuildActivateModeCard();
        BuildAfterLaunchCard();
        BuildSizeCard();
        BuildLayoutCard();
    }

    /// <summary>只保存（无需刷新主窗口的设置项：锁定图标/显示位置/项目启动后等）</summary>
    private void SaveOnly() => SettingsService.Instance.Save();

    /// <summary>保存并刷新主窗口（分类列表 + 项目面板都重建，仅分类显示参数变化时用）</summary>
    private void SaveAndRefreshPanel()
    {
        SettingsService.Instance.Save();
        App.Instance.RefreshMainWindowPanel();
    }

    /// <summary>保存并只重建右侧项目面板（布局/尺寸/间距等与分类列表无关的设置，需求-BUG1）</summary>
    private void SaveAndRefreshPanelOnly()
    {
        SettingsService.Instance.Save();
        App.Instance.RefreshMainWindowPanelOnly();
    }

    /// <summary>保存并应用窗口行为（置顶/锁定尺寸），不重建面板</summary>
    private void SaveAndApplyBehavior()
    {
        SettingsService.Instance.Save();
        App.Instance.ApplyMainWindowBehavior();
    }

    /// <summary>项目分类设置卡片：图标位置（左/右）+ 整体位置（居左/居中/居右）（需求-优化2）</summary>
    private void BuildCategoryCard()
    {
        var content = new StackPanel { Spacing = 10 };

        // 图标位置
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        iconRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.CategoryIconPosition"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var iconCombo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.CategoryIconPosition.Left"),
                LocalizationService.Tr("Settings.CategoryIconPosition.Right")
            },
            SelectedIndex = _settings.CategoryIconPosition == CategoryIconPosition.Right ? 1 : 0
        };
        iconCombo.SelectionChanged += (_, _) =>
        {
            _settings.CategoryIconPosition = iconCombo.SelectedIndex == 1 ? CategoryIconPosition.Right : CategoryIconPosition.Left;
            SaveAndRefreshPanel(); // 分类图标位置影响左侧分类列表，需全量刷新
        };
        iconRow.Children.Add(iconCombo);
        content.Children.Add(iconRow);

        // 整体位置
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        alignRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.CategoryAlignment"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var alignCombo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.CategoryAlignment.Left"),
                LocalizationService.Tr("Settings.CategoryAlignment.Center"),
                LocalizationService.Tr("Settings.CategoryAlignment.Right")
            },
            SelectedIndex = (int)_settings.CategoryAlignment
        };
        alignCombo.SelectionChanged += (_, _) =>
        {
            _settings.CategoryAlignment = (ItemHorizontalAlignment)alignCombo.SelectedIndex;
            SaveAndRefreshPanel(); // 分类整体位置影响左侧分类列表，需全量刷新
        };
        alignRow.Children.Add(alignCombo);
        content.Children.Add(alignRow);

        MakeCard(FluentGlyphs.BulletedList, "Settings.CategoryCard", "Settings.CategoryCard.Sub", content);
    }

    private void BuildSubCategoryStyleCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.SubCategoryStyle.Tab"),
                LocalizationService.Tr("Settings.SubCategoryStyle.Card")
            },
            SelectedIndex = _settings.SubCategoryStyle == SubCategoryStyle.Tab ? 0 : 1
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.SubCategoryStyle = comboBox.SelectedIndex == 0 ? SubCategoryStyle.Tab : SubCategoryStyle.Card;
            SaveAndRefreshPanelOnly(); // 切换后保持当前选中的分类/子分类
        };
        MakeCard(FluentGlyphs.Tiles, "Settings.SubCategoryStyle", "Settings.SubCategoryStyle.Sub", comboBox);
    }

    /// <summary>显示和隐藏触发方式（需求-启动器2）</summary>
    private void BuildTriggersCard()
    {
        var content = new StackPanel { Spacing = 6 };

        // 侧键单击 + 后退/前进下拉框
        var sideRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sideCheck = new CheckBox
        {
            Content = LocalizationService.Tr("Settings.Trigger.SideButton"),
            IsChecked = _settings.TriggerSideButton
        };
        var sideCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Trigger.SideBack"),
                LocalizationService.Tr("Settings.Trigger.SideForward")
            },
            SelectedIndex = _settings.SideButton == SideMouseButton.Back ? 0 : 1
        };
        sideCheck.Checked += (_, _) => { _settings.TriggerSideButton = true; ApplyTriggers(); };
        sideCheck.Unchecked += (_, _) => { _settings.TriggerSideButton = false; ApplyTriggers(); };
        sideCombo.SelectionChanged += (_, _) =>
        {
            _settings.SideButton = sideCombo.SelectedIndex == 0 ? SideMouseButton.Back : SideMouseButton.Forward;
            ApplyTriggers();
        };
        sideRow.Children.Add(sideCheck);
        sideRow.Children.Add(sideCombo);
        content.Children.Add(sideRow);

        // 中键单击
        var middleCheck = AddCheck(content, "Settings.Trigger.MiddleButton", _settings.TriggerMiddleButton,
            v => _settings.TriggerMiddleButton = v);

        // 左键双击桌面（预留复选框，需求-启动器6：后续版本再完善）
        var desktopCheck = new CheckBox
        {
            Content = LocalizationService.Tr("Settings.Trigger.DesktopDouble"),
            IsChecked = _settings.TriggerDesktopDoubleClick,
            IsEnabled = false // 预留
        };
        content.Children.Add(desktopCheck);

        // 双击任务栏（预留复选框）
        var taskbarCheck = new CheckBox
        {
            Content = LocalizationService.Tr("Settings.Trigger.TaskbarDouble"),
            IsChecked = _settings.TriggerTaskbarDoubleClick,
            IsEnabled = false // 预留
        };
        content.Children.Add(taskbarCheck);

        // 按 Ctrl 键两次
        AddCheck(content, "Settings.Trigger.CtrlTwice", _settings.TriggerCtrlTwice,
            v => _settings.TriggerCtrlTwice = v);

        // 按 Alt 键两次
        AddCheck(content, "Settings.Trigger.AltTwice", _settings.TriggerAltTwice,
            v => _settings.TriggerAltTwice = v);

        // 使用快捷键 + 快捷键记录框（默认 Alt+1）
        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var hotkeyCheck = new CheckBox
        {
            Content = LocalizationService.Tr("Settings.Trigger.Hotkey"),
            IsChecked = _settings.TriggerHotkey
        };
        var hotkeyBox = new TextBox
        {
            IsReadOnly = true,
            Text = _settings.HotkeyText,
            MinWidth = 120,
            PlaceholderText = LocalizationService.Tr("Settings.Trigger.HotkeyHint")
        };
        hotkeyCheck.Checked += (_, _) => { _settings.TriggerHotkey = true; ApplyTriggers(); };
        hotkeyCheck.Unchecked += (_, _) => { _settings.TriggerHotkey = false; ApplyTriggers(); };

        // 点击框内后按下键盘组合键自动记录
        hotkeyBox.GotFocus += (_, _) => hotkeyBox.Text = LocalizationService.Tr("Settings.Trigger.HotkeyRecording");
        hotkeyBox.KeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = (int)e.Key;
            // 忽略纯修饰键
            if (key is (int)Windows.System.VirtualKey.Control or (int)Windows.System.VirtualKey.LeftControl
                or (int)Windows.System.VirtualKey.RightControl or (int)Windows.System.VirtualKey.Menu
                or (int)Windows.System.VirtualKey.LeftMenu or (int)Windows.System.VirtualKey.RightMenu
                or (int)Windows.System.VirtualKey.Shift or (int)Windows.System.VirtualKey.LeftShift
                or (int)Windows.System.VirtualKey.RightShift or (int)Windows.System.VirtualKey.LeftWindows
                or (int)Windows.System.VirtualKey.RightWindows)
            {
                return;
            }

            var modifiers = 0;
            // 读取当前按下的修饰键（WinUI 3 用 InputKeyboardSource）
            var modifiersState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if ((modifiersState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) modifiers |= Win32Helper.MOD_CONTROL;
            modifiersState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
            if ((modifiersState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) modifiers |= Win32Helper.MOD_ALT;
            modifiersState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            if ((modifiersState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) modifiers |= Win32Helper.MOD_SHIFT;
            modifiersState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows);
            if ((modifiersState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) modifiers |= Win32Helper.MOD_WIN;

            _settings.HotkeyModifiers = modifiers == 0 ? Win32Helper.MOD_ALT : modifiers;
            _settings.HotkeyVirtualKey = key;
            _settings.HotkeyText = BuildHotkeyText(modifiers, key);
            hotkeyBox.Text = _settings.HotkeyText;
            ApplyTriggers();
        };
        hotkeyRow.Children.Add(hotkeyCheck);
        hotkeyRow.Children.Add(hotkeyBox);
        content.Children.Add(hotkeyRow);

        MakeCard(FluentGlyphs.Keyboard, "Settings.Trigger", "Settings.Trigger.Sub", content);
    }

    /// <summary>构造快捷键显示文本（如 Alt+1）</summary>
    private static string BuildHotkeyText(int modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & Win32Helper.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32Helper.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32Helper.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32Helper.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(((Windows.System.VirtualKey)vk).ToString());
        return string.Join("+", parts);
    }

    private CheckBox AddCheck(StackPanel parent, string textKey, bool value, Action<bool> setter)
    {
        var check = new CheckBox { Content = LocalizationService.Tr(textKey), IsChecked = value };
        check.Checked += (_, _) => { setter(true); ApplyTriggers(); };
        check.Unchecked += (_, _) => { setter(false); ApplyTriggers(); };
        parent.Children.Add(check);
        return check;
    }

    private void ApplyTriggers()
    {
        SettingsService.Instance.Save();
        App.Instance.ApplyTriggerSettings();
    }

    private void BuildAlwaysOnTopCard()
    {
        var check = new CheckBox { Content = LocalizationService.Tr("Common.Enable"), IsChecked = _settings.AlwaysOnTop };
        check.Checked += (_, _) => { _settings.AlwaysOnTop = true; SaveAndApplyBehavior(); };
        check.Unchecked += (_, _) => { _settings.AlwaysOnTop = false; SaveAndApplyBehavior(); };
        MakeCard(FluentGlyphs.Pin, "Settings.AlwaysOnTop", "Settings.AlwaysOnTop.Sub", check);
    }

    private void BuildLockSizeCard()
    {
        var check = new CheckBox { Content = LocalizationService.Tr("Common.Enable"), IsChecked = _settings.LockSize };
        check.Checked += (_, _) => { _settings.LockSize = true; SaveAndApplyBehavior(); };
        check.Unchecked += (_, _) => { _settings.LockSize = false; SaveAndApplyBehavior(); };
        MakeCard(FluentGlyphs.Lock, "Settings.LockSize", "Settings.LockSize.Sub", check);
    }

    private void BuildLockIconsCard()
    {
        var check = new CheckBox { Content = LocalizationService.Tr("Common.Enable"), IsChecked = _settings.LockIcons };
        check.Checked += (_, _) => { _settings.LockIcons = true; SaveOnly(); };
        check.Unchecked += (_, _) => { _settings.LockIcons = false; SaveOnly(); };
        MakeCard(FluentGlyphs.Lock, "Settings.LockIcons", "Settings.LockIcons.Sub", check);
    }

    private void BuildShowPositionCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.ShowPosition.FollowMouse"),
                LocalizationService.Tr("Settings.ShowPosition.Center"),
                LocalizationService.Tr("Settings.ShowPosition.TopLeft"),
                LocalizationService.Tr("Settings.ShowPosition.TopRight"),
                LocalizationService.Tr("Settings.ShowPosition.BottomLeft"),
                LocalizationService.Tr("Settings.ShowPosition.BottomRight"),
                LocalizationService.Tr("Settings.ShowPosition.LastPosition")
            },
            SelectedIndex = (int)_settings.ShowPosition
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.ShowPosition = (ShowPosition)comboBox.SelectedIndex;
            SaveOnly(); // 显示时按此定位
        };
        MakeCard(FluentGlyphs.Home, "Settings.ShowPosition", "Settings.ShowPosition.Sub", comboBox);
    }

    private void BuildActivateModeCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Activate.Single"),
                LocalizationService.Tr("Settings.Activate.Double")
            },
            SelectedIndex = _settings.ItemActivateMode == ItemActivateMode.SingleClick ? 0 : 1
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.ItemActivateMode = comboBox.SelectedIndex == 0 ? ItemActivateMode.SingleClick : ItemActivateMode.DoubleClick;
            SaveAndRefreshPanelOnly();
        };
        MakeCard(FluentGlyphs.Play, "Settings.Activate", "Settings.Activate.Sub", comboBox);
    }

    private void BuildAfterLaunchCard()
    {
        var comboBox = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.AfterLaunch.Hide"),
                LocalizationService.Tr("Settings.AfterLaunch.Show")
            },
            SelectedIndex = _settings.AfterLaunch == AfterLaunchBehavior.Hide ? 0 : 1
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            _settings.AfterLaunch = comboBox.SelectedIndex == 0 ? AfterLaunchBehavior.Hide : AfterLaunchBehavior.Show;
            SaveOnly();
        };
        MakeCard(FluentGlyphs.Sync, "Settings.AfterLaunch", "Settings.AfterLaunch.Sub", comboBox);
    }

    /// <summary>构建带数值显示的滑条行（0 = 不显示）</summary>
    private StackPanel BuildSliderRow(string labelKey, double value, double max,
        Action<double> changed)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock { Text = LocalizationService.Tr(labelKey) });
        var slider = new Slider { Minimum = 0, Maximum = max, StepFrequency = 1, Value = value, MinWidth = 260 };
        var valueText = new TextBlock { Text = ((int)value).ToString(), Opacity = 0.7 };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(slider);
        line.Children.Add(valueText);
        row.Children.Add(line);
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = ((int)e.NewValue).ToString();
            changed(e.NewValue);
        };
        return row;
    }

    /// <summary>图标/文字大小卡片（带"不能都为0"约束，需求-启动器开头）</summary>
    private void BuildSizeCard()
    {
        var content = new StackPanel { Spacing = 12 };

        // 项目分类图标大小（需求-启动器9；影响左侧分类列表，需全量刷新）
        content.Children.Add(BuildSliderRow("Settings.Size.CategoryIcon", _settings.CategoryIconSize, 48, v =>
        {
            _settings.CategoryIconSize = ClampPair(v, _settings.CategoryTextSize, w => _settings.CategoryTextSize = w, 12);
            SaveAndRefreshPanel();
        }));

        // 项目分类文字大小（需求-启动器10；影响左侧分类列表，需全量刷新）
        content.Children.Add(BuildSliderRow("Settings.Size.CategoryText", _settings.CategoryTextSize, 28, v =>
        {
            _settings.CategoryTextSize = ClampPair(v, _settings.CategoryIconSize, w => _settings.CategoryIconSize = w, 12);
            SaveAndRefreshPanel();
        }));

        // 子分类图标大小（需求-启动器11）
        content.Children.Add(BuildSliderRow("Settings.Size.SubIcon", _settings.SubCategoryIconSize, 48, v =>
        {
            _settings.SubCategoryIconSize = ClampPair(v, _settings.SubCategoryTextSize, w => _settings.SubCategoryTextSize = w, 12);
            SaveAndRefreshPanelOnly();
        }));

        // 子分类文字大小（需求-启动器12）
        content.Children.Add(BuildSliderRow("Settings.Size.SubText", _settings.SubCategoryTextSize, 28, v =>
        {
            _settings.SubCategoryTextSize = ClampPair(v, _settings.SubCategoryIconSize, w => _settings.SubCategoryIconSize = w, 12);
            SaveAndRefreshPanelOnly();
        }));

        // 项目图标大小（需求-启动器14）
        content.Children.Add(BuildSliderRow("Settings.Size.ItemIcon", _settings.ItemIconSize, 96, v =>
        {
            _settings.ItemIconSize = ClampPair(v, _settings.ItemTextSize, w => _settings.ItemTextSize = w, 12);
            SaveAndRefreshPanelOnly();
        }));

        // 项目文字大小（需求-启动器15）
        content.Children.Add(BuildSliderRow("Settings.Size.ItemText", _settings.ItemTextSize, 28, v =>
        {
            _settings.ItemTextSize = ClampPair(v, _settings.ItemIconSize, w => _settings.ItemIconSize = w, 12);
            SaveAndRefreshPanelOnly();
        }));

        // 横向间距（需求-优化2）
        content.Children.Add(BuildSliderRow("Settings.Size.HSpacing", _settings.ItemHorizontalSpacing, 32, v =>
        {
            _settings.ItemHorizontalSpacing = v;
            SaveAndRefreshPanelOnly();
        }));

        // 纵向间距（需求-优化2）
        content.Children.Add(BuildSliderRow("Settings.Size.VSpacing", _settings.ItemVerticalSpacing, 32, v =>
        {
            _settings.ItemVerticalSpacing = v;
            SaveAndRefreshPanelOnly();
        }));

        // 项目文字最多显示行数（一行/两行/三行）
        var linesCombo = new ComboBox
        {
            MinWidth = 140,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Size.Lines1"),
                LocalizationService.Tr("Settings.Size.Lines2"),
                LocalizationService.Tr("Settings.Size.Lines3")
            },
            SelectedIndex = Math.Clamp(_settings.ItemTextMaxLines, 1, 3) - 1
        };
        linesCombo.SelectionChanged += (_, _) =>
        {
            _settings.ItemTextMaxLines = linesCombo.SelectedIndex + 1;
            SaveAndRefreshPanelOnly();
        };
        var linesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        linesRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.Size.ItemMaxLines"),
            VerticalAlignment = VerticalAlignment.Center
        });
        linesRow.Children.Add(linesCombo);
        content.Children.Add(linesRow);

        // 恢复默认：一键把本卡片里的显示大小恢复为默认值，然后重建当前页让滑条回位
        var restoreButton = new Button
        {
            Content = LocalizationService.Tr("Common.RestoreDefault"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        restoreButton.Click += (_, _) =>
        {
            _settings.CategoryIconSize = 20;
            _settings.CategoryTextSize = 14;
            _settings.SubCategoryIconSize = 18;
            _settings.SubCategoryTextSize = 14;
            _settings.ItemIconSize = 40;
            _settings.ItemTextSize = 12;
            _settings.ItemTextMaxLines = 1;
            _settings.ItemHorizontalSpacing = 8;
            _settings.ItemVerticalSpacing = 8;
            SaveAndRefreshPanelOnly();
            SettingsWindow.CurrentInstance?.RebuildCurrentPage();
        };
        content.Children.Add(restoreButton);

        MakeCard(FluentGlyphs.ZoomIn, "Settings.Size", "Settings.Size.Sub", content);
    }

    /// <summary>约束：图标和文字大小不能都为0（需求-启动器开头）。
    /// 当前值设为 0 时，给配对的另一个值一个最小值。返回修正后的当前值。</summary>
    private static double ClampPair(double current, double other, Action<double> setOther, double fallback)
    {
        if (current <= 0 && other <= 0)
        {
            setOther(fallback);
        }
        return current;
    }

    private void BuildLayoutCard()
    {
        var content = new StackPanel { Spacing = 10 };

        // 项目布局：平铺/列表（需求-启动器13）
        var layoutCombo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.Layout.Grid"),
                LocalizationService.Tr("Settings.Layout.List")
            },
            SelectedIndex = _settings.ItemLayout == ItemLayoutMode.Grid ? 0 : 1
        };
        // 图标和文字位置：项目内容（图标+文字）的对齐，平铺/列表通用（需求-优化3）
        var contentAlignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        contentAlignRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.ContentPosition"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var contentAlignCombo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.ContentPosition.Left"),
                LocalizationService.Tr("Settings.ContentPosition.Center"),
                LocalizationService.Tr("Settings.ContentPosition.Right")
            },
            SelectedIndex = (int)_settings.ItemContentAlignment
        };
        contentAlignCombo.SelectionChanged += (_, _) =>
        {
            _settings.ItemContentAlignment = (ItemHorizontalAlignment)contentAlignCombo.SelectedIndex;
            SaveAndRefreshPanelOnly();
        };
        contentAlignRow.Children.Add(contentAlignCombo);

        layoutCombo.SelectionChanged += (_, _) =>
        {
            _settings.ItemLayout = layoutCombo.SelectedIndex == 0 ? ItemLayoutMode.Grid : ItemLayoutMode.List;
            SaveAndRefreshPanelOnly();
        };
        var layoutRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        layoutRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.Layout"),
            VerticalAlignment = VerticalAlignment.Center
        });
        layoutRow.Children.Add(layoutCombo);
        content.Children.Add(layoutRow);
        content.Children.Add(contentAlignRow);

        // 项目文字位置（需求-启动器16）
        var positionCombo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                LocalizationService.Tr("Settings.TextPosition.Below"),
                LocalizationService.Tr("Settings.TextPosition.Left"),
                LocalizationService.Tr("Settings.TextPosition.Right")
            },
            SelectedIndex = (int)_settings.ItemTextPosition
        };
        positionCombo.SelectionChanged += (_, _) =>
        {
            _settings.ItemTextPosition = (ItemTextPosition)positionCombo.SelectedIndex;
            SaveAndRefreshPanelOnly();
        };
        var positionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        positionRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr("Settings.TextPosition"),
            VerticalAlignment = VerticalAlignment.Center
        });
        positionRow.Children.Add(positionCombo);
        content.Children.Add(positionRow);

        MakeCard(FluentGlyphs.AllApps, "Settings.LayoutCard", "Settings.LayoutCard.Sub", content);
    }
}

/// <summary>开发中占位页（待办/随手记/剪贴板，需求）</summary>
public sealed class PlaceholderPage : SettingsPageBase
{
    public PlaceholderPage(string tag)
    {
        var text = new TextBlock
        {
            Text = LocalizationService.Tr("Settings.UnderDevelopment"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RootPanel.Children.Add(new Border { Padding = new Thickness(0, 120, 0, 0), Child = text });
    }
}

/// <summary>关于页（需求-关于）</summary>
public sealed class AboutPage : SettingsPageBase
{
    public AboutPage()
    {
        BuildHeader();
        BuildWebsiteCard();
        BuildOpenSourceCard();
        BuildUpdateCard();
        BuildContactCard();
    }

    private void BuildHeader()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 16) };
        var logo = new Image { Width = 48, Height = 48 };
        _ = LoadLogoAsync(logo);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = AppInfoService.Config.AppName,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        info.Children.Add(new TextBlock
        {
            Text = "v" + AppInfoService.Version,
            FontSize = 13,
            Opacity = 0.7
        });
        header.Children.Add(logo);
        header.Children.Add(info);
        RootPanel.Children.Add(header);
    }

    private static async Task LoadLogoAsync(Image image)
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_1024.png");
        if (bitmap != null) image.Source = bitmap;
    }

    private void BuildWebsiteCard()
    {
        var button = new Button { Content = LocalizationService.Tr("About.OpenWebsite") };
        button.Click += (_, _) => SettingsActions.OpenUrl(AppInfoService.Config.WebsiteUrl);
        MakeCard(FluentGlyphs.Globe, "About.Website", "About.Website.Sub", button);
    }

    private void BuildOpenSourceCard()
    {
        var button = new Button { Content = LocalizationService.Tr("About.OpenSource") };
        button.Click += (_, _) => SettingsActions.OpenUrl(AppInfoService.Config.OpenSourceUrl);
        MakeCard(FluentGlyphs.Code, "About.OpenSourceCard", "About.OpenSourceCard.Sub", button);
    }

    private void BuildUpdateCard()
    {
        var text = new TextBlock { Text = LocalizationService.Tr("About.Update.Developing"), Opacity = 0.7 };
        MakeCard(FluentGlyphs.Sync, "About.Update", "About.Update.Sub", text);
    }

    private void BuildContactCard()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationService.Tr("About.QQGroup.Content"), AppInfoService.Config.QQGroup),
            TextWrapping = TextWrapping.Wrap
        });
        MakeCard(FluentGlyphs.Contact, "About.QQGroup", "About.QQGroup.Sub", content);
    }
}

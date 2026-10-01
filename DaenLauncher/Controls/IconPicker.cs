using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

using WrapPanel = CommunityToolkit.WinUI.UI.Controls.WrapPanel;

namespace DaenLauncher.Controls;

/// <summary>
/// 图标选择器（需求-分类编辑窗口）：
/// 左侧竖向标签栏（可滚动，group_ 分组多也不会撑爆）：顶部"不选择"（公用，可取消任何已选图标）+
/// 表情图标（内置固定一批，按分类分组，存储 emoji 字符本身）+ 自定义图标（上传 png 保存到
/// data\icon\system，10位秒级时间戳命名）+ 动态 group_ 分组（读取 data\icon 下 group_ 开头的文件夹）。
/// 性能：emoji 面板进程级缓存（只构建一次，之后所有弹窗复用），并按分组增量构建，
/// 避免弹窗打开时卡顿（优化项3）。
/// </summary>
public sealed class IconPicker : Grid
{
    /// <summary>选中的图标值：emoji 字符 或 png 文件名；空表示未选择</summary>
    public string SelectedIcon { get; private set; } = "";

    private readonly System.Collections.Generic.List<(string Value, Border Border)> _allTiles = new();

    // ===== emoji 面板进程级缓存 =====
    private static StackPanel? _cachedEmojiPanel;
    private static Dictionary<string, Border>? _cachedEmojiTiles;

    /// <summary>缓存面板当前挂载的宿主（重挂到新弹窗前必须先解除，否则抛"已有父级"异常）</summary>
    private static ScrollViewer? _cachedEmojiHost;

    /// <summary>当前可见的图标选择器（共享 emoji 磁贴的点击路由到这里）</summary>
    private static IconPicker? _activeInstance;

    // ===== 左侧竖向标签栏状态 =====
    private readonly List<(string Name, Button Button)> _tabs = new();
    private readonly Dictionary<string, UIElement> _tabContent = new();
    private StackPanel _leftPanel = null!;
    private Grid _contentHost = null!;
    private Button? _clearButton;

    /// <summary>表情图标页的自定义输入框（用户可自己输入/粘贴 emoji，需求）</summary>
    private TextBox? _emojiInput;

    /// <summary>图标选择器固定尺寸（需求：切换标签时窗口大小不变）。
    /// 尺寸取自"表情图标"页展开时的合适大小。</summary>
    private const double PickerWidth = 440;
    private const double PickerHeight = 264;

    public IconPicker(string currentIcon)
    {
        SelectedIcon = currentIcon ?? "";
        _activeInstance = this;
        BuildUI();
    }

    private void BuildUI()
    {
        var root = new Grid { ColumnSpacing = 8, Width = PickerWidth, Height = PickerHeight };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ===== 左侧竖向标签栏（需求：group_ 分组可能很多，竖排 + 滚动） =====
        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
            Padding = new Thickness(2)
        };
        _leftPanel = new StackPanel { Spacing = 2, MinWidth = 104 };

        // "不选择"（需求：公用，放在标签栏最顶部）：点击清空已选图标并高亮自己。
        // 注意：图标必须用 FontIcon（符号字体），直接放进 TextBlock 会因字体缺字显示成方框
        var clearPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        clearPanel.Children.Add(new FontIcon { Glyph = "\uE711", FontSize = 14 }); // Cancel：叉号
        clearPanel.Children.Add(new TextBlock { Text = LocalizationService.Tr("IconPicker.None"), FontSize = 13 });
        _clearButton = new Button
        {
            Content = clearPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"]
        };
        ToolTipService.SetToolTip(_clearButton, LocalizationService.Tr("IconPicker.None"));
        _clearButton.Click += (_, _) => ClearIconSelection();
        _leftPanel.Children.Add(_clearButton);

        // 分隔线：把"不选择"和真正的图标来源标签隔开
        _leftPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(4, 4, 4, 4)
        });

        // ===== 各图标来源标签 =====

        // 表情图标页：顶部"自定义 emoji 输入框"（需求：允许用户自己输入 emoji）+
        // 缓存的 emoji 网格。输入框属于本实例（不进共享缓存面板）。
        var emojiPage = new StackPanel { Spacing = 6 };

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _emojiInput = new TextBox
        {
            MinWidth = 220,
            MaxLength = 8, // 预留组合 emoji（一个 emoji 占 2 个 char）
            PlaceholderText = LocalizationService.Tr("IconPicker.EmojiInput")
        };
        var useButton = new Button
        {
            Content = LocalizationService.Tr("IconPicker.EmojiUse"),
            IsEnabled = false
        };
        _emojiInput.TextChanged += (_, _) =>
        {
            var text = _emojiInput.Text.Trim();
            var valid = EmojiCatalog.IsEmoji(text);
            useButton.IsEnabled = valid;
            // 输入合法 emoji 时立即生效（与点选磁贴等效），输入框自身高亮表示"当前选中"
            if (valid)
            {
                ApplyCustomEmoji(text);
            }
        };
        useButton.Click += (_, _) =>
        {
            var text = _emojiInput.Text.Trim();
            if (EmojiCatalog.IsEmoji(text))
            {
                ApplyCustomEmoji(text);
            }
        };
        inputRow.Children.Add(_emojiInput);
        inputRow.Children.Add(useButton);
        emojiPage.Children.Add(inputRow);

        var emojiScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 216,
            Padding = new Thickness(4)
        };
        if (_cachedEmojiPanel != null)
        {
            AttachEmojiPanel(emojiScroll);
        }
        else
        {
            // 占位：按分组增量构建，构建完成后注册选中态
            _ = InitEmojiPanelAsync(emojiScroll);
        }
        emojiPage.Children.Add(emojiScroll);
        AddLeftTab(LocalizationService.Tr("IconPicker.Emoji"), emojiPage);

        // 自定义图标
        AddLeftTab(LocalizationService.Tr("IconPicker.Custom"), BuildCustomTab());

        // 动态 group_ 分组 Tab
        foreach (var (name, path) in IconService.GetGroupFolders())
        {
            AddLeftTab(name, BuildGroupTab(path));
        }

        leftScroll.Content = _leftPanel;
        Grid.SetColumn(leftScroll, 0);
        root.Children.Add(leftScroll);

        // ===== 右侧内容区 =====
        _contentHost = new Grid();
        Grid.SetColumn(_contentHost, 1);
        root.Children.Add(_contentHost);

        Children.Add(root);

        // 默认选中第一个标签（表情图标）
        if (_tabs.Count > 0)
        {
            SelectTab(_tabs[0].Name);
        }

        // 初始高亮：没选图标 → 高亮"不选择"；选了内置 emoji → 高亮对应磁贴；
        // 选了自定义 emoji（不在网格里）→ 回填到输入框（需求）
        if (SelectedIcon.Length == 0)
        {
            if (_clearButton != null) MarkSelected(_clearButton);
        }
        else if (_cachedEmojiTiles != null && EmojiCatalog.IsEmoji(SelectedIcon))
        {
            HighlightEmoji(SelectedIcon);
        }
        else if (EmojiCatalog.IsEmoji(SelectedIcon) && _emojiInput != null)
        {
            _emojiInput.Text = SelectedIcon;
        }
    }

    /// <summary>构建左侧竖排标签项按钮（文字 + 选中态由 SelectTab 更新）</summary>
    private Button CreateLeftItem(string text)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"]
        };
        ToolTipService.SetToolTip(button, text);
        return button;
    }

    /// <summary>向左侧标签栏添加一个标签（名称 + 右侧内容）</summary>
    private void AddLeftTab(string name, UIElement content)
    {
        var button = CreateLeftItem(name);
        button.Click += (_, _) => SelectTab(name);
        _tabs.Add((name, button));
        _tabContent[name] = content;
        _leftPanel.Children.Add(button);
    }

    /// <summary>切换标签：更新左侧按钮选中态，右侧内容区切换到对应面板</summary>
    private void SelectTab(string name)
    {
        foreach (var (tabName, button) in _tabs)
        {
            var selected = tabName == name;
            button.Background = (Brush)Application.Current.Resources[selected
                ? "SubtleFillColorSecondaryBrush"
                : "SubtleFillColorTransparentBrush"];
            if (button.Content is TextBlock text)
            {
                text.FontWeight = selected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        _contentHost.Children.Clear();
        if (_tabContent.TryGetValue(name, out var content))
        {
            _contentHost.Children.Add(content);
        }
    }

    /// <summary>点击"不选择"：清空选中值并高亮"不选择"（需求：公用，且选中态可见）</summary>
    private void ClearIconSelection()
    {
        SelectedIcon = "";
        ClearSelection();
        if (_clearButton != null)
        {
            MarkSelected(_clearButton);
        }
    }

    /// <summary>应用用户输入的自定义 emoji（输入合法时调用，需求）：
    /// 记录选中值、清除磁贴高亮，并让输入框自身保持高亮表示"当前选中的是它"</summary>
    private void ApplyCustomEmoji(string text)
    {
        SelectedIcon = text;
        ClearSelection();
        if (_emojiInput != null)
        {
            MarkSelected(_emojiInput);
        }
    }

    /// <summary>首次构建 emoji 面板并注册选中态</summary>
    private async Task InitEmojiPanelAsync(ScrollViewer target)
    {
        await BuildEmojiPanelAsync(target);
        RegisterEmojiTiles();
        if (EmojiCatalog.IsEmoji(SelectedIcon))
        {
            HighlightEmoji(SelectedIcon);
        }
    }

    /// <summary>把缓存的 emoji 面板挂到本实例的 ScrollViewer 上</summary>
    private void AttachEmojiPanel(ScrollViewer scroll)
    {
        if (_cachedEmojiPanel == null) return;
        // 关键：先解除上一次弹窗的挂载（ScrollViewer.Content 是引用挂载，
        // 不解除的话第二次设置 Content 会抛"元素已有父级"，弹窗就打不开了）
        if (_cachedEmojiHost != null)
        {
            _cachedEmojiHost.Content = null;
        }
        scroll.Content = _cachedEmojiPanel;
        _cachedEmojiHost = scroll;
        RegisterEmojiTiles();
    }

    /// <summary>按分组增量构建 emoji 面板（进程内只构建一次）</summary>
    private static async Task BuildEmojiPanelAsync(ScrollViewer target)
    {
        var panel = new StackPanel { Spacing = 4 };
        var tiles = new Dictionary<string, Border>();

        // 先展示占位，UI 立即可见
        target.Content = panel;
        _cachedEmojiPanel = panel;
        _cachedEmojiTiles = tiles;
        _cachedEmojiHost = target;

        foreach (var (groupName, emojis) in EmojiCatalog.Groups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = groupName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(2, 6, 0, 2)
            });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var emoji in emojis)
            {
                var border = CreateEmojiTileStatic(emoji);
                wrap.Children.Add(border);
                tiles[emoji] = border;
            }
            panel.Children.Add(wrap);

            // 让出 UI 线程，弹窗先显示出来，emoji 分组逐个出现
            await Task.Yield();
        }
    }

    /// <summary>静态 emoji 磁贴（跨弹窗复用）</summary>
    private static Border CreateEmojiTileStatic(string emoji)
    {
        var border = new Border
        {
            Style = (Style)Application.Current.Resources["PickerTileBorderStyle"]
        };
        border.Child = new TextBlock
        {
            Text = emoji,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Tag = emoji;
        return border;
    }

    /// <summary>给缓存面板里的 emoji 磁贴挂点击路由（静态面板只挂一次）</summary>
    private void RegisterEmojiTiles()
    {
        if (_cachedEmojiTiles == null) return;
        foreach (var (_, border) in _cachedEmojiTiles)
        {
            border.PointerPressed -= EmojiTilePressed;
            border.PointerPressed += EmojiTilePressed;
        }
    }

    /// <summary>emoji 磁贴点击（静态面板共享，路由到当前可见的选择器实例）</summary>
    private static void EmojiTilePressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string emoji && _activeInstance != null)
        {
            _activeInstance.SelectTile(emoji, border);
        }
    }

    /// <summary>高亮缓存面板中的某个 emoji（清除其他高亮）</summary>
    private void HighlightEmoji(string emoji)
    {
        if (_cachedEmojiTiles == null) return;
        ClearSelection();
        if (_cachedEmojiTiles.TryGetValue(emoji, out var border))
        {
            MarkSelected(border);
        }
    }

    /// <summary>自定义图标 Tab（已上传图标列表 + 上传按钮）</summary>
    private StackPanel BuildCustomTab()
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };

        var uploadButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE898", FontSize = 14 }, // E898 = 添加
                    new TextBlock { Text = LocalizationService.Tr("IconPicker.Upload") }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Left
        };
        uploadButton.Click += async (_, _) => await UploadPngAsync(panel);
        panel.Children.Add(uploadButton);

        panel.Children.Add(BuildIconGridFromNames(IconService.GetSystemIcons()));
        return panel;
    }

    /// <summary>group_ 分组 Tab</summary>
    private ScrollViewer BuildGroupTab(string folderPath)
    {
        var names = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var file in System.IO.Directory.GetFiles(folderPath, "*.png"))
            {
                names.Add(System.IO.Path.GetFileName(file));
            }
        }
        catch
        {
            // 读取失败
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
            Padding = new Thickness(4)
        };
        scroll.Content = BuildIconGridFromNames(names, folderPath);
        return scroll;
    }

    /// <summary>按文件名列表构建图标网格</summary>
    private WrapPanel BuildIconGridFromNames(System.Collections.Generic.List<string> names, string? folderOverride = null)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var name in names)
        {
            var border = CreateTileBorder();
            var image = new Image
            {
                Width = 32,
                Height = 32,
                Stretch = Stretch.Uniform
            };
            border.Child = image;
            border.Tag = name;
            _ = LoadTileImageAsync(image, name, folderOverride);
            border.PointerPressed += (_, _) => SelectTile(name, border);
            _allTiles.Add((name, border));
            if (SelectedIcon == name) MarkSelected(border);
            wrap.Children.Add(border);
        }
        return wrap;
    }

    private async Task LoadTileImageAsync(Image image, string name, string? folderOverride)
    {
        try
        {
            var path = folderOverride != null
                ? System.IO.Path.Combine(folderOverride, name)
                : System.IO.Path.Combine(DataPathService.IconSystemDir, name);
            if (System.IO.File.Exists(path))
            {
                image.Source = await IconService.LoadBitmapAsync(path);
            }
        }
        catch
        {
            // 加载失败
        }
    }

    /// <summary>上传 png 图标（需求：png 格式，保存到 data\icon\system，10位秒级时间戳命名）</summary>
    private async Task UploadPngAsync(StackPanel panel)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                App.Instance.GetMainWindow().WindowHandle);
            picker.FileTypeFilter.Add(".png");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var bytes = await Windows.Storage.FileIO.ReadBufferAsync(file);
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(bytes);
            var data = new byte[bytes.Length];
            reader.ReadBytes(data);

            var fileName = IconService.SaveUploadedImage(data);

            // 重新构建自定义 Tab 的图标网格
            if (panel.Children.Count > 1)
            {
                panel.Children.RemoveAt(1);
            }
            panel.Children.Add(BuildIconGridFromNames(IconService.GetSystemIcons()));

            SelectedIcon = fileName;
            ClearSelection();
        }
        catch
        {
            // 上传失败
        }
    }

    /// <summary>磁贴通用外观（样式来自 App.xaml，ThemeResource 跟随深浅色）</summary>
    private Border CreateTileBorder() => new()
    {
        Style = (Style)Application.Current.Resources["PickerTileBorderStyle"]
    };

    /// <summary>选中磁贴</summary>
    private void SelectTile(string value, Border border)
    {
        SelectedIcon = value;
        ClearSelection();
        MarkSelected(border);
    }

    private void ClearSelection()
    {
        foreach (var (_, border) in _allTiles)
        {
            border.BorderThickness = new Thickness(0);
            border.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
        // 共享 emoji 磁贴不在 _allTiles（静态路由），单独清高亮
        if (_cachedEmojiTiles != null)
        {
            foreach (var border in _cachedEmojiTiles.Values)
            {
                border.BorderThickness = new Thickness(0);
                border.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            }
        }
        // "不选择"按钮：选中了具体图标时它必须取消高亮
        if (_clearButton != null)
        {
            _clearButton.BorderThickness = new Thickness(0);
            _clearButton.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        }
        // 自定义 emoji 输入框：选中了磁贴/其他图标时取消输入框高亮
        if (_emojiInput != null)
        {
            _emojiInput.BorderThickness = new Thickness(1);
            _emojiInput.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        }
    }

    /// <summary>标记选中态：磁贴（Border）与左侧"不选择"按钮（Button/Control）两个重载</summary>
    private void MarkSelected(Border border)
    {
        border.BorderThickness = new Thickness(2);
        border.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void MarkSelected(Control control)
    {
        control.BorderThickness = new Thickness(2);
        control.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }
}

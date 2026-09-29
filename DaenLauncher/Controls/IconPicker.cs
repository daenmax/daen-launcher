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
/// Tab1 表情图标（内置固定一批，按分类分组，存储 emoji 字符本身）；
/// Tab2 自定义图标（上传 png 保存到 data\icon\system，10位秒级时间戳命名）；
/// 动态 Tab：读取 data\icon 下 group_ 开头的文件夹（直接引用，存储文件名）。
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

    public IconPicker(string currentIcon)
    {
        SelectedIcon = currentIcon ?? "";
        _activeInstance = this;
        BuildUI();
    }

    private void BuildUI()
    {
        var tabView = new TabView
        {
            IsAddTabButtonVisible = false,
            Background = null
        };

        // ===== Tab1: 表情图标（缓存复用；首次按分组增量构建） =====
        var emojiScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
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

        tabView.TabItems.Add(new TabViewItem
        {
            Header = new TextBlock { Text = LocalizationService.Tr("IconPicker.Emoji") },
            IsClosable = false,
            Content = emojiScroll
        });

        // ===== Tab2: 自定义图标 =====
        tabView.TabItems.Add(new TabViewItem
        {
            Header = new TextBlock { Text = LocalizationService.Tr("IconPicker.Custom") },
            IsClosable = false,
            Content = BuildCustomTab()
        });

        // ===== 动态 group_ 分组 Tab =====
        foreach (var (name, path) in IconService.GetGroupFolders())
        {
            tabView.TabItems.Add(new TabViewItem
            {
                Header = new TextBlock { Text = name },
                IsClosable = false,
                Content = BuildGroupTab(path)
            });
        }

        Children.Add(tabView);

        // 当前图标若是 emoji，缓存面板构建完后高亮它
        if (_cachedEmojiTiles != null && EmojiCatalog.IsEmoji(SelectedIcon))
        {
            HighlightEmoji(SelectedIcon);
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

    /// <summary>emoji 磁贴（非缓存场景备用）</summary>
    private FrameworkElement CreateEmojiTile(string emoji)
    {
        var border = CreateTileBorder();
        border.Child = new TextBlock
        {
            Text = emoji,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Tag = emoji;
        border.PointerPressed += (_, _) => SelectTile(emoji, border);
        _allTiles.Add((emoji, border));
        return border;
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
    }

    private void MarkSelected(Border border)
    {
        border.BorderThickness = new Thickness(2);
        border.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }
}

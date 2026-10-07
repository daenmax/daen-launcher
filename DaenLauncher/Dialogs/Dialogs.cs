using DaenLauncher;
using DaenLauncher.Controls;
using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.WindowsRuntime;

namespace DaenLauncher.Dialogs;

/// <summary>
/// 各类编辑弹窗（ContentDialog）：
/// - CategoryEditDialog：新建/编辑项目分类（名称 + 图标选择）
/// - SubCategoryEditDialog：新建/编辑子分类
/// - ManageSubCategoriesDialog：管理子分类（上下移动/编辑/删除/新增）
/// - ItemEditDialog：编辑项目（名称/路径/备注/命令行参数）
/// </summary>
public static class Dialogs
{
    /// <summary>新建/编辑分类弹窗。返回 (名称, 图标)，取消返回 null。</summary>
    public static async Task<(string Name, string Icon)?> CategoryEditDialogAsync(
        XamlRoot xamlRoot, LauncherCategory? category)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = LocalizationService.Tr("Dialog.CategoryName"),
            Text = category?.Name ?? ""
        };
        var picker = new IconPicker(category?.Icon ?? "");

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(picker);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = category == null
                ? LocalizationService.Tr("Main.Category.New")
                : LocalizationService.Tr("Main.Category.Edit"),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }
        return (nameBox.Text.Trim(), picker.SelectedIcon);
    }

    /// <summary>新建/编辑子分类弹窗。返回 (名称, 图标)，取消返回 null。
    /// hideBefore：可选，打开前先隐藏的弹窗（WinUI 不允许两个 ContentDialog 同时打开）。</summary>
    public static async Task<(string Name, string Icon)?> SubCategoryEditDialogAsync(
        XamlRoot xamlRoot, LauncherSubCategory? sub, ContentDialog? hideBefore = null)
    {
        hideBefore?.Hide();
        var nameBox = new TextBox
        {
            PlaceholderText = LocalizationService.Tr("Dialog.SubCategoryName"),
            Text = sub?.Name ?? ""
        };
        var picker = new IconPicker(sub?.Icon ?? "");

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(picker);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = sub == null
                ? LocalizationService.Tr("Main.SubCategory.New")
                : LocalizationService.Tr("Main.SubCategory.Edit"),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }
        return (nameBox.Text.Trim(), picker.SelectedIcon);
    }

    /// <summary>
    /// 管理子分类弹窗：上移/下移/编辑/删除/新增。操作直接修改 category 并保存。
    /// 注意：WinUI 不允许同时打开两个 ContentDialog，所以编辑/新增前先 Hide 本弹窗，操作完重新打开。
    /// </summary>
    public static async Task ManageSubCategoriesDialogAsync(
        XamlRoot xamlRoot, LauncherCategory category, MainWindow mainWindow)
    {
        var list = new StackPanel { Spacing = 4 };
        ContentDialog? currentDialog = null;

        // 重新打开管理弹窗（编辑/新增子分类之后调用）
        async Task ReopenAsync()
        {
            currentDialog?.Hide();
            await ManageSubCategoriesDialogAsync(xamlRoot, category, mainWindow);
        }

        void Rebuild()
        {
            list.Children.Clear();
            var loc = LocalizationService.Instance;
            for (var i = 0; i < category.SubCategories.Count; i++)
            {
                var sub = category.SubCategories[i];
                var index = i;
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameLabel = new TextBlock
                {
                    Text = sub.Name,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameLabel, 0);
                row.Children.Add(nameLabel);

                var upButton = CreateIconButton("\uE74A", loc.T("Main.Category.MoveUp"));
                upButton.IsEnabled = i > 0;
                upButton.Click += (_, _) =>
                {
                    var moved = category.SubCategories[index];
                    category.SubCategories.RemoveAt(index);
                    category.SubCategories.Insert(index - 1, moved);
                    LauncherDataService.Instance.Save();
                    mainWindow.RefreshMainPanel(); // 立即生效，不用重启
                    Rebuild();
                };
                Grid.SetColumn(upButton, 1);
                row.Children.Add(upButton);

                var downButton = CreateIconButton("\uE74B", loc.T("Main.Category.MoveDown"));
                downButton.IsEnabled = i < category.SubCategories.Count - 1;
                downButton.Click += (_, _) =>
                {
                    var moved = category.SubCategories[index];
                    category.SubCategories.RemoveAt(index);
                    category.SubCategories.Insert(index + 1, moved);
                    LauncherDataService.Instance.Save();
                    mainWindow.RefreshMainPanel(); // 立即生效，不用重启
                    Rebuild();
                };
                Grid.SetColumn(downButton, 2);
                row.Children.Add(downButton);

                var editButton = CreateIconButton("\uE70F", loc.T("Main.SubCategory.Edit"));
                editButton.Click += async (_, _) =>
                {
                    // 先关闭管理弹窗再打开编辑弹窗（WinUI 只允许一个 ContentDialog）
                    var result = await SubCategoryEditDialogAsync(xamlRoot, sub, currentDialog);
                    if (result != null)
                    {
                        sub.Name = result.Value.Name;
                        sub.Icon = result.Value.Icon;
                        LauncherDataService.Instance.Save();
                        mainWindow.RefreshMainPanel();
                    }
                    await ReopenAsync();
                };
                Grid.SetColumn(editButton, 3);
                row.Children.Add(editButton);

                var deleteButton = CreateIconButton("\uE74D", loc.T("Main.SubCategory.Delete"));
                deleteButton.Click += (_, _) =>
                {
                    category.SubCategories.Remove(sub);
                    LauncherDataService.Instance.Save();
                    mainWindow.RefreshMainPanel();
                    Rebuild();
                };
                Grid.SetColumn(deleteButton, 4);
                row.Children.Add(deleteButton);

                list.Children.Add(row);
            }
        }

        Rebuild();

        var addButton = new Button
        {
            Content = LocalizationService.Tr("Main.SubCategory.New"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        addButton.Click += async (_, _) =>
        {
            // 先关闭管理弹窗再打开新增弹窗（WinUI 只允许一个 ContentDialog）
            var result = await SubCategoryEditDialogAsync(xamlRoot, null, currentDialog);
            if (result != null)
            {
                category.SubCategories.Add(new LauncherSubCategory { Name = result.Value.Name, Icon = result.Value.Icon });
                LauncherDataService.Instance.Save();
                mainWindow.RefreshMainPanel();
            }
            await ReopenAsync();
        };

        var content = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer
        {
            MaxHeight = 300,
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        content.Children.Add(scroll);
        content.Children.Add(addButton);

        currentDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = LocalizationService.Tr("Main.SubCategory.Manage"),
            Content = content,
            CloseButtonText = LocalizationService.Tr("Dialog.Close"),
            DefaultButton = ContentDialogButton.Close
        };
        await currentDialog.ShowAsync();
    }

    /// <summary>编辑项目弹窗（每个输入框带标签 + 显示项目类型）。返回是否保存了修改。</summary>
    public static async Task<bool> ItemEditDialogAsync(XamlRoot xamlRoot, LauncherItem item)
    {
        var nameBox = new TextBox { PlaceholderText = LocalizationService.Tr("Dialog.ItemName"), Text = item.Name };
        var pathBox = new TextBox
        {
            PlaceholderText = LocalizationService.Tr("Dialog.ItemPath"),
            Text = item.Path
        };
        var remarkBox = new TextBox { PlaceholderText = LocalizationService.Tr("Dialog.ItemRemark"), Text = item.Remark };
        var argsBox = new TextBox { PlaceholderText = LocalizationService.Tr("Dialog.ItemArguments"), Text = item.Arguments };

        // 带标签的行：标签在上，输入框在下（需求-优化3）
        StackPanel LabeledRow(string labelKey, FrameworkElement input)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr(labelKey),
                FontSize = 13,
                Opacity = 0.8
            });
            row.Children.Add(input);
            return row;
        }

        var content = new StackPanel { Spacing = 10 };
        // 项目类型（只读展示）
        var typeText = LocalizationService.Tr("Main.ItemType." + item.Type);
        content.Children.Add(LabeledRow("Dialog.ItemTypeLabel", new TextBlock { Text = typeText, VerticalAlignment = VerticalAlignment.Center }));
        content.Children.Add(LabeledRow("Dialog.ItemNameLabel", nameBox));
        content.Children.Add(LabeledRow("Dialog.ItemPathLabel", pathBox));
        content.Children.Add(LabeledRow("Dialog.ItemRemarkLabel", remarkBox));
        content.Children.Add(LabeledRow("Dialog.ItemArgumentsLabel", argsBox));

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = LocalizationService.Tr("Main.Item.Edit"),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Dialog.Save"),
            CloseButtonText = LocalizationService.Tr("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        item.Name = nameBox.Text.Trim();
        item.Path = pathBox.Text.Trim();
        item.Remark = remarkBox.Text;
        item.Arguments = argsBox.Text;
        return true;
    }

    /// <summary>
    /// 输入地址创建项目的弹窗（网址 / 协议共用，需求：添加项目）。
    /// 带实时校验：网址必须 http/https 开头；协议必须是 scheme: 形式（如 steam://rungameid/993090）。
    /// 返回新项目，取消返回 null。
    /// </summary>
    public static async Task<LauncherItem?> TextItemDialogAsync(XamlRoot xamlRoot, LauncherItemType type)
    {
        var isUrl = type == LauncherItemType.Url;
        var loc = LocalizationService.Instance;

        var pathBox = new TextBox
        {
            PlaceholderText = loc.T(isUrl ? "Dialog.ItemUrl" : "Dialog.ItemProtocol")
        };
        var nameBox = new TextBox { PlaceholderText = loc.T("Dialog.ItemName") };
        var hintText = new TextBlock
        {
            Text = loc.T(isUrl ? "Dialog.UrlInvalid" : "Dialog.ProtocolInvalid"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            Visibility = Visibility.Collapsed
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(pathBox);
        content.Children.Add(nameBox);
        content.Children.Add(hintText);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = loc.T(isUrl ? "Main.Item.Add.Url" : "Main.Item.Add.Protocol"),
            Content = content,
            PrimaryButtonText = loc.T("Dialog.Save"),
            CloseButtonText = loc.T("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            // 地址合法才允许保存
            IsPrimaryButtonEnabled = false
        };

        // 名称是否被用户手动填过：没填过时根据地址自动填充
        var nameEdited = false;
        nameBox.TextChanged += (_, _) => nameEdited = nameBox.Text.Length > 0;

        pathBox.TextChanged += (_, _) =>
        {
            var text = pathBox.Text.Trim();
            var valid = IsTextItemValid(text, isUrl);
            hintText.Visibility = !valid && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = valid;

            // 网址：名称没手动填过时用主机名自动填充（www.example.com → www.example.com）
            if (isUrl && !nameEdited && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                nameBox.Text = uri.Host;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var path = pathBox.Text.Trim();
        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            // 名称留空：网址用主机名，协议用地址本身
            if (isUrl && Uri.TryCreate(path, UriKind.Absolute, out var uri)) name = uri.Host;
            else name = path;
        }
        return new LauncherItem { Name = name, Path = path, Type = type };
    }

    /// <summary>网址/协议地址校验</summary>
    private static bool IsTextItemValid(string text, bool isUrl)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (isUrl)
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                   && !string.IsNullOrEmpty(uri.Host);
        }

        // 协议：scheme 以字母开头，可含 数字/+/-.，后跟冒号（如 steam://、ms-settings:）
        var match = System.Text.RegularExpressions.Regex.Match(text, ProtocolPattern);
        if (!match.Success) return false;
        var scheme = match.Value.TrimEnd(':').ToLowerInvariant();
        // http/https 属于"网址"类型，不算协议
        return scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps;
    }

    /// <summary>协议地址格式（scheme: 形式，如 steam://rungameid/993090、ms-settings:）</summary>
    private const string ProtocolPattern = @"^[a-zA-Z][a-zA-Z0-9+.\-]*:";

    /// <summary>
    /// UWP 应用选择弹窗（需求：添加项目-UWP 应用，列出系统安装的微软商店应用）。
    /// 支持按名称/应用ID 搜索。返回新项目，取消返回 null。
    /// </summary>
    public static async Task<LauncherItem?> UwpAppPickerDialogAsync(XamlRoot xamlRoot)
    {
        var loc = LocalizationService.Instance;

        List<InstalledAppService.InstalledUwpApp> apps;
        try
        {
            apps = await InstalledAppService.GetInstalledAppsAsync();
        }
        catch
        {
            apps = new List<InstalledAppService.InstalledUwpApp>();
        }

        var searchBox = new TextBox { PlaceholderText = loc.T("Main.UwpPicker.Search") };

        // 列表行：应用名 + 应用ID（AUMID）；运行时加载的 DataTemplate 用经典 Binding 反射取值
        var list = new ListView
        {
            MaxHeight = 380,
            SelectionMode = ListViewSelectionMode.Single,
            Margin = new Thickness(0, 8, 0, 0),
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(UwpRowTemplateXaml)
        };
        var emptyText = new TextBlock
        {
            Text = loc.T("Main.UwpPicker.Empty"),
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        void Refill()
        {
            var keyword = searchBox.Text?.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(keyword)
                ? apps
                : apps.Where(a => a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                  || a.Aumid.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            list.ItemsSource = filtered;
            emptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        searchBox.TextChanged += (_, _) => Refill();
        Refill();

        // 后台逐个加载应用图标（按 AUMID 磁盘缓存，首次需要 Shell 提取），加载完一个刷新一行。
        // 此时还在 UI 线程上，取到当前线程的 DispatcherQueue 供后台加载完成后切回来
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = LoadUwpAppIconsAsync(apps, dispatcherQueue);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(searchBox);
        content.Children.Add(list);
        content.Children.Add(emptyText);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = loc.T("Main.Item.Add.Uwp"),
            Content = content,
            PrimaryButtonText = loc.T("Dialog.Save"),
            CloseButtonText = loc.T("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            // 选中一个应用才允许保存
            IsPrimaryButtonEnabled = false
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem != null;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary ||
            list.SelectedItem is not InstalledAppService.InstalledUwpApp selected)
        {
            return null;
        }
        return new LauncherItem
        {
            Name = selected.Name,
            Path = LauncherItemPaths.UwpPrefix + selected.Aumid,
            Type = LauncherItemType.Uwp
        };
    }

    /// <summary>UWP 选择弹窗列表行的模板（图标 + 应用名 + 应用ID 两行）</summary>
    private const string UwpRowTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<StackPanel Orientation=\"Horizontal\" Margin=\"2,4\" Spacing=\"10\">" +
        "<Image Source=\"{Binding IconSource}\" Width=\"32\" Height=\"32\" VerticalAlignment=\"Center\"/>" +
        "<StackPanel Spacing=\"2\" VerticalAlignment=\"Center\">" +
        "<TextBlock Text=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\"/>" +
        "<TextBlock Text=\"{Binding Aumid}\" FontSize=\"11\" Opacity=\"0.6\" TextTrimming=\"CharacterEllipsis\"/>" +
        "</StackPanel></StackPanel></DataTemplate>";

    /// <summary>后台逐个加载 UWP 应用图标（顺序执行，避免几十个并发提取拖慢系统），
    /// 每加载完一个就切回 UI 线程设置 IconSource（BitmapImage 有线程亲和性，必须在 UI 线程创建）</summary>
    private static async Task LoadUwpAppIconsAsync(
        List<InstalledAppService.InstalledUwpApp> apps,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        foreach (var app in apps)
        {
            if (app.IconSource != null) continue; // 搜索过滤共用同一批对象，已加载的不重复加载
            try
            {
                var png = await ItemIconService.GetUwpAppIconPngBytesAsync(app.Aumid);
                if (png == null) continue;
                dispatcherQueue.TryEnqueue(() => _ = SetUwpAppIconAsync(app, png));
            }
            catch
            {
                // 单个图标加载失败不影响其他应用
            }
        }
    }

    /// <summary>在 UI 线程把 PNG 字节解码成 BitmapImage 并设置到应用行</summary>
    private static async Task SetUwpAppIconAsync(InstalledAppService.InstalledUwpApp app, byte[] png)
    {
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new MemoryStream(png);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            app.IconSource = bitmap;
        }
        catch
        {
            // 解码失败该行就没有图标
        }
    }

    /// <summary>小图标按钮</summary>
    private static Button CreateIconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            Padding = new Thickness(4),
            Background = null,
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }
}

/// <summary>弹窗桥接扩展（MainWindow 调用的静态入口）</summary>
public static class CategoryEditDialog
{
    public static Task<(string Name, string Icon)?> ShowAsync(XamlRoot root, LauncherCategory? category)
        => Dialogs.CategoryEditDialogAsync(root, category);
}

public static class SubCategoryEditDialog
{
    public static Task<(string Name, string Icon)?> ShowAsync(XamlRoot root, LauncherSubCategory? sub,
        ContentDialog? hideBefore = null)
        => Dialogs.SubCategoryEditDialogAsync(root, sub, hideBefore);
}

public static class ManageSubCategoriesDialog
{
    public static Task ShowAsync(XamlRoot root, LauncherCategory category, MainWindow mainWindow)
        => Dialogs.ManageSubCategoriesDialogAsync(root, category, mainWindow);
}

public static class ItemEditDialog
{
    public static Task<bool> ShowAsync(XamlRoot root, LauncherItem item)
        => Dialogs.ItemEditDialogAsync(root, item);
}

/// <summary>输入地址创建项目弹窗桥接（网址/协议共用）</summary>
public static class TextItemDialog
{
    public static Task<LauncherItem?> ShowAsync(XamlRoot root, LauncherItemType type)
        => Dialogs.TextItemDialogAsync(root, type);
}

/// <summary>UWP 应用选择弹窗桥接</summary>
public static class UwpAppPickerDialog
{
    public static Task<LauncherItem?> ShowAsync(XamlRoot root)
        => Dialogs.UwpAppPickerDialogAsync(root);
}

using DaenLauncher;
using DaenLauncher.Controls;
using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

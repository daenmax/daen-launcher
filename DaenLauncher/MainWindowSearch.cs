using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text;
using TinyPinyin;

namespace DaenLauncher;

/// <summary>
/// 主窗口的 Ctrl+F 项目搜索弹层（partial class，界面在 MainWindow.xaml 的 SearchOverlay）。
/// 行为：
/// - Ctrl+F 打开/关闭弹层（KeyboardAccelerator 挂在 RootGrid 上，窗口有焦点时才触发）；
/// - 输入即筛选：项目名包含 / 全拼包含 / 拼音首字母包含（大小写不敏感，基于 TinyPinyin）；
/// - 结果带项目图标（复用面板同款图标加载链路），显示"分类 › 子分类"位置；
/// - ↑↓ 选择、回车启动选中项、Esc 关闭、点击结果启动、点遮罩关闭。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>搜索结果最多显示条数（防止项目特别多时列表过长）</summary>
    private const int MaxSearchResults = 50;

    /// <summary>搜索索引条目：项目 + 位置文字（分类 › 子分类）+ 匹配用拼音键</summary>
    private sealed record SearchEntry(LauncherItem Item, string Location, SearchKeys Keys);

    /// <summary>拼音匹配键：原名小写 / 全拼（小写连写）/ 拼音首字母（小写连写）</summary>
    private sealed record SearchKeys(string NameLower, string FullPinyin, string Initials);

    /// <summary>本次弹层打开期间的全量索引（每次打开弹层时重建，项目数据始终最新）</summary>
    private List<SearchEntry> _searchEntries = new();

    /// <summary>Ctrl+F：打开或关闭搜索弹层</summary>
    private void ToggleSearchPopup()
    {
        if (SearchOverlay.Visibility == Visibility.Visible)
        {
            HideSearchPopup();
            return;
        }
        ShowSearchPopup();
    }

    /// <summary>打开搜索弹层：重建索引、清空输入框、显示并聚焦输入框</summary>
    private void ShowSearchPopup()
    {
        // 语言切换后占位符/提示要最新，每次打开都重设
        SearchBox.PlaceholderText = LocalizationService.Tr("Main.Search.Placeholder");
        SearchHintText.Text = LocalizationService.Tr("Main.Search.Hint");

        RebuildSearchIndex();
        SearchBox.Text = "";
        SearchOverlay.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Keyboard);
        ApplySearchFilter();
    }

    /// <summary>关闭搜索弹层并清空状态（窗口隐藏时也会调用，保证下次打开是干净状态）</summary>
    private void HideSearchPopup()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        SearchResultsList.ItemsSource = null;
        _searchEntries = new List<SearchEntry>();
    }

    /// <summary>重建搜索索引：遍历所有分类 → 子分类 → 项目，预生成拼音键</summary>
    private void RebuildSearchIndex()
    {
        _searchEntries = new List<SearchEntry>();
        foreach (var category in LauncherDataService.Instance.Data.Categories)
        {
            foreach (var sub in category.SubCategories)
            {
                foreach (var item in sub.Items)
                {
                    _searchEntries.Add(new SearchEntry(item,
                        $"{category.Name} › {sub.Name}", BuildSearchKeys(item.Name)));
                }
            }
        }
    }

    /// <summary>
    /// 生成一个名称的拼音匹配键：
    /// - 全拼：中文字符 → 完整拼音，字母数字保留，其他符号跳过（"记事本Notepad" → "jishibennotepad"）；
    /// - 首字母：中文取拼音首字母，字母数字保留（"记事本Notepad" → "jsbnotepad"）。
    /// TinyPinyin 输出大写无音调拼音，统一转小写参与匹配。
    /// </summary>
    private static SearchKeys BuildSearchKeys(string name)
    {
        var full = new StringBuilder(name.Length);
        var initials = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (PinyinHelper.IsChinese(ch))
            {
                var pinyin = PinyinHelper.GetPinyin(ch).ToLowerInvariant();
                if (pinyin.Length > 0)
                {
                    full.Append(pinyin);
                    initials.Append(pinyin[0]);
                }
            }
            else if (char.IsLetterOrDigit(ch))
            {
                var lower = char.ToLowerInvariant(ch);
                full.Append(lower);
                initials.Append(lower);
            }
        }
        return new SearchKeys(name.ToLowerInvariant(), full.ToString(), initials.ToString());
    }

    /// <summary>按当前输入筛选并重建结果列表（输入框 TextChanged 时调用）</summary>
    private void ApplySearchFilter()
    {
        // 弹层没开时不处理（清空文本框等操作也会触发 TextChanged）
        if (SearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var query = SearchBox.Text.Trim().ToLowerInvariant();
        var pinyinQuery = query.Replace(" ", "");

        IEnumerable<SearchEntry> matches;
        if (query.Length == 0)
        {
            matches = Enumerable.Empty<SearchEntry>();
        }
        else
        {
            matches = _searchEntries.Where(entry => IsMatch(entry.Keys, query, pinyinQuery));
        }

        var rows = new List<FrameworkElement>();
        foreach (var entry in matches.Take(MaxSearchResults))
        {
            rows.Add(BuildSearchResultRow(entry));
        }

        SearchResultsList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            SearchResultsList.SelectedIndex = 0; // 默认选中第一条，回车直接启动
        }

        // 无结果/无输入时把底部提示行换成状态文字
        SearchHintText.Text = query.Length == 0
            ? LocalizationService.Tr("Main.Search.Hint")
            : rows.Count == 0
                ? LocalizationService.Tr("Main.Search.NoResults")
                : LocalizationService.Tr("Main.Search.Hint");
    }

    /// <summary>匹配规则：项目名包含查询词，或全拼包含查询词，或拼音首字母包含查询词</summary>
    private static bool IsMatch(SearchKeys keys, string query, string pinyinQuery)
    {
        return keys.NameLower.Contains(query, StringComparison.Ordinal) ||
               (pinyinQuery.Length > 0 && keys.FullPinyin.Contains(pinyinQuery, StringComparison.Ordinal)) ||
               (pinyinQuery.Length > 0 && keys.Initials.Contains(pinyinQuery, StringComparison.Ordinal));
    }

    /// <summary>构建一条结果行：图标 + 名称 + 位置（分类 › 子分类）</summary>
    private FrameworkElement BuildSearchResultRow(SearchEntry entry)
    {
        // 图标：与项目面板同款加载链路（提取/缓存/失效图标都一致），加载完成后自动补上
        var iconHost = new Grid
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        _ = LoadItemIconAsync(entry.Item, iconHost, 32);

        var nameText = new TextBlock
        {
            Text = entry.Item.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var locationText = new TextBlock
        {
            Text = entry.Location,
            FontSize = 11,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var textColumn = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textColumn.Children.Add(nameText);
        textColumn.Children.Add(locationText);

        var row = new Grid
        {
            Margin = new Thickness(4, 2, 4, 2),
            Tag = entry.Item // 点击时从 Tag 取回项目
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(textColumn, 1);
        row.Children.Add(iconHost);
        row.Children.Add(textColumn);
        ToolTipService.SetToolTip(row, BuildItemTooltip(entry.Item));
        return row;
    }

    /// <summary>输入框按键：↑↓ 移动选择、回车启动选中项、Esc 关闭</summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = SearchResultsList.Items.Count;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down when count > 0:
                SearchResultsList.SelectedIndex = Math.Min(SearchResultsList.SelectedIndex + 1, count - 1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up when count > 0:
                SearchResultsList.SelectedIndex = Math.Max(SearchResultsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                if (SearchResultsList.SelectedItem is FrameworkElement { Tag: LauncherItem item })
                {
                    HideSearchPopup();
                    LaunchItem(item);
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                HideSearchPopup();
                e.Handled = true;
                break;
        }
        if (SearchResultsList.SelectedItem != null)
        {
            SearchResultsList.ScrollIntoView(SearchResultsList.SelectedItem);
        }
    }

    /// <summary>输入框文字变化：重新筛选</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySearchFilter();
    }

    /// <summary>点击结果行：关闭弹层并启动项目</summary>
    private void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: LauncherItem item })
        {
            HideSearchPopup();
            LaunchItem(item);
        }
    }

    /// <summary>点击半透明遮罩（搜索面板以外区域）：关闭弹层</summary>
    private void SearchBackdrop_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        HideSearchPopup();
    }
}

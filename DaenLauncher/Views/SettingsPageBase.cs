using System.Diagnostics;
using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher.Views;

/// <summary>
/// 设置页公共基类：提供"卡片"构建器（图标 + 选项名大字 + 说明小字 + 内容控件），
/// 需求-设置窗口：每个选项都是一个卡片。
/// </summary>
public class SettingsPageBase : UserControl, ILocalizable
{
    /// <summary>页面根容器（ScrollViewer 包裹的 StackPanel）</summary>
    protected ScrollViewer Scroller = null!;
    protected StackPanel RootPanel = null!;

    public SettingsPageBase()
    {
        RootPanel = new StackPanel { Spacing = 0, Padding = new Thickness(16) };
        Scroller = new ScrollViewer
        {
            Content = RootPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Content = Scroller;
    }

    /// <summary>添加卡片（样式来自 App.xaml，ThemeResource 跟随深浅色）。
    /// glyph：Segoe Fluent Icons 图标字体字形（见 FluentGlyphs 常量类）。</summary>
    protected Border MakeCard(string glyph, string titleKey, string subtitleKey, UIElement? content = null)
    {
        var card = new Border { Style = (Style)Application.Current.Resources["SettingsCardBorderStyle"] };

        var stack = new StackPanel { Spacing = 8 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        if (!string.IsNullOrEmpty(glyph))
        {
            // 用 Fluent 图标字体绘制卡片图标（矢量，随主题变色，替代旧 png 位图）
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily(FluentGlyphs.FontFamilyName),
                FontSize = 18,
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(icon);
        }

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = LocalizationService.Tr(titleKey),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Tag = titleKey
        };
        var subtitleText = new TextBlock
        {
            Text = LocalizationService.Tr(subtitleKey),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Tag = subtitleKey
        };
        titleStack.Children.Add(titleText);
        titleStack.Children.Add(subtitleText);
        header.Children.Add(titleStack);
        stack.Children.Add(header);

        if (content != null)
        {
            stack.Children.Add(content);
        }

        card.Child = stack;
        RootPanel.Children.Add(card);
        return card;
    }

    /// <summary>本地化刷新（语言切换时把所有卡片标题/说明换掉）</summary>
    public virtual void ApplyLocalization()
    {
        RefreshTexts(RootPanel);
    }

    /// <summary>递归刷新带 Tag 的文字控件（Tag 是本地化 key）</summary>
    protected static void RefreshTexts(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock textBlock && textBlock.Tag is string localizationKey &&
                (localizationKey.StartsWith("Settings.") || localizationKey.StartsWith("Dialog.") ||
                 localizationKey.StartsWith("Data.") || localizationKey.StartsWith("About.")))
            {
                textBlock.Text = LocalizationService.Tr(localizationKey);
            }
            RefreshTexts(child);
        }
    }
}

/// <summary>
/// Segoe Fluent Icons 常用字形常量（设置卡片图标用）。
/// 字形对照表：https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font
/// </summary>
public static class FluentGlyphs
{
    /// <summary>Fluent 图标字体名</summary>
    public const string FontFamilyName = "Segoe Fluent Icons";

    public const string Globe = "\uE774";        // 球形：语言/网址
    public const string PowerButton = "\uE7E8";  // 电源：开机自启
    public const string RedEye = "\uE7B3";       // 眼睛：显示/隐藏
    public const string Color = "\uE790";        // 调色板：主题
    public const string BackToWindow = "\uE771"; // 窗口：窗口材质
    public const string Edit = "\uE70F";         // 铅笔：编辑/标题
    public const string Save = "\uE74E";         // 软盘：导出
    public const string OpenFile = "\uE8E5";     // 打开文件夹：导入
    public const string Folder = "\uE8B7";       // 文件夹：打开配置目录
    public const string Delete = "\uE74D";       // 垃圾桶：删除
    public const string Home = "\uE80F";         // 房子：显示位置
    public const string BulletedList = "\uE8FD"; // 项目符号列表：分类
    public const string Keyboard = "\uE765";     // 键盘：触发方式
    public const string Pin = "\uE718";          // 图钉：置顶
    public const string Lock = "\uE72E";         // 锁：锁定
    public const string Play = "\uE768";         // 播放：启动方式
    public const string Sync = "\uE895";         // 循环箭头：启动后行为/更新
    public const string ZoomIn = "\uE8A3";       // 放大镜：显示大小
    public const string AllApps = "\uE71D";      // 磁贴：布局
    public const string Tiles = "\uE80A";        // 网格：子分类风格
    public const string Contact = "\uE77B";      // 人像：联系我们
    public const string Code = "\uE943";         // 代码：开源地址
}

/// <summary>带重启提示的公共逻辑</summary>
public static class SettingsActions
{
    /// <summary>提示需要重启并执行重启（Process.Start 重新拉起自己，需求）</summary>
    public static async void PromptRestart(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = LocalizationService.Tr("Dialog.RestartTitle"),
            Content = LocalizationService.Tr("Dialog.RestartContent"),
            PrimaryButtonText = LocalizationService.Tr("Dialog.RestartNow"),
            CloseButtonText = LocalizationService.Tr("Dialog.Later"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Restart();
        }
    }

    /// <summary>重启软件（Process.Start，AGENTS 规范3-10）</summary>
    public static void Restart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败
        }
        App.Instance.ExitApplication();
    }

    /// <summary>用系统默认方式打开网址</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败
        }
    }
}

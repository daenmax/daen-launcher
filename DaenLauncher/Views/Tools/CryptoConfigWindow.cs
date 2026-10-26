using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DaenLauncher.Views.Tools;

/// <summary>
/// 加解密配置管理窗口（需求：点"管理配置"后展示一个新窗口，列出所有配置并可删除，不支持修改）。
/// 说明：
/// - 只显示配置名和所属算法，删除后立即落盘并通知打开它的工具页刷新下拉框；
/// - 工具窗口默认是置顶的，所以本窗口也置顶并调用 BringToTopMost，避免被工具窗口盖住；
/// - 按住 Esc 关闭窗口（与其他窗口一致的交互）。
/// </summary>
public sealed class CryptoConfigWindow : Window
{
    /// <summary>窗口默认尺寸（逻辑像素，管理列表不需要太大）</summary>
    private const double WindowWidth = 520;
    private const double WindowHeight = 520;

    /// <summary>当前打开的实例（WinUI 窗口被 GC 会直接消失，必须持有引用）</summary>
    private static CryptoConfigWindow? _current;

    private readonly Action? _onConfigsChanged;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _emptyText;

    /// <summary>删除按钮字号（列表行的小图标按钮）</summary>
    private const int DeleteIconSize = 14;

    public CryptoConfigWindow(Action? onConfigsChanged = null)
    {        _onConfigsChanged = onConfigsChanged;
        _current = this;

        var loc = LocalizationService.Instance;
        Title = App.GetDisplayTitle() + " " + loc.T("Tools.Enc.ManageConfig");

        // ===== 内容：标题 + 说明 + 配置列表 =====
        var layout = new StackPanel { Spacing = 10 };
        layout.Children.Add(new TextBlock
        {
            Text = loc.T("Tools.Enc.ManageTitle"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        layout.Children.Add(new TextBlock
        {
            Text = loc.T("Tools.Enc.ManageHint"),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        _emptyText = new TextBlock
        {
            Text = loc.T("Tools.Enc.ManageEmpty"),
            Opacity = 0.6,
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        layout.Children.Add(_emptyText);

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = WindowHeight - 140
        };
        layout.Children.Add(scroll);

        var root = new Grid { Padding = new Thickness(20) };
        root.Children.Add(layout);
        Content = root;

        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)WindowWidth, (int)WindowHeight));

        // 工具窗口是置顶的，本窗口也必须置顶并放到置顶层最上面
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }

        // Esc 关闭（窗口本身就是弹窗语义，关闭 = 关掉自己，不需要隐藏）
        WindowKeyHelper.EnableEscapeToHide(root, () => Close());

        var settings = SettingsService.Instance.Settings;
        BackdropService.Apply(this, settings.Backdrop);
        ThemeService.Apply(root, settings.Theme);
        ThemeService.ApplyCaptionButtonColors(AppWindow, root);

        // 关闭后释放静态引用（下次点"管理配置"会重新创建）
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };

        Rebuild();
    }

    /// <summary>
    /// 显示管理窗口（已打开则激活已有的那个，避免重复开窗）。
    /// </summary>
    public static void ShowOrActivate(Action? onConfigsChanged = null)
    {
        if (_current != null)
        {
            _current.Activate();
            return;
        }
        new CryptoConfigWindow(onConfigsChanged).Activate();
    }

    /// <summary>重建配置列表</summary>
    private void Rebuild()
    {
        _list.Children.Clear();
        var configs = CryptoConfigService.Instance.Data.Configs;
        _emptyText.Visibility = configs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var config in configs)
        {
            _list.Children.Add(BuildRow(config));
        }
    }

    /// <summary>一行配置：[算法标签] 配置名 ................ [删除]</summary>
    private Border BuildRow(CryptoConfig config)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8),
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 算法标签（SM4 / AES）
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush,
            Child = new TextBlock
            {
                Text = LocalizationService.Tr(CryptoAlgoIds.DisplayKey(config.Algo)),
                FontSize = 11,
                Foreground = Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] as Brush
            }
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // 配置名（超长省略）
        var nameText = new TextBlock
        {
            Text = config.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        // 删除按钮
        var deleteButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        deleteButton.Content = new FontIcon
        {
            Glyph = "\uE74D", // 垃圾桶
            FontSize = DeleteIconSize,
            // Win10 没有 Segoe Fluent Icons，必须用动态选择的字体名
            FontFamily = new FontFamily(FluentGlyphs.FontFamilyName)
        };
        ToolTipService.SetToolTip(deleteButton, LocalizationService.Tr("Tools.Enc.DeleteConfig"));
        deleteButton.Click += (_, _) =>
        {
            CryptoConfigService.Instance.Delete(config.Id);
            Rebuild();
            _onConfigsChanged?.Invoke(); // 让工具页的下拉框立即刷新
        };
        Grid.SetColumn(deleteButton, 2);
        grid.Children.Add(deleteButton);

        row.Child = grid;
        return row;
    }
}

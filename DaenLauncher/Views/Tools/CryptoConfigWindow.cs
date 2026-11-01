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
    private readonly Grid _root;

    /// <summary>删除按钮字号（列表行的小图标按钮）</summary>
    private const int DeleteIconSize = 14;

    public CryptoConfigWindow(Action? onConfigsChanged = null)
    {
        _onConfigsChanged = onConfigsChanged;
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

        // ===== 自绘标题栏（与其他窗口一致：48px 标题栏 + 应用图标 + 标题文字）=====
        // 系统的最小化/最大化/关闭按钮仍叠在右上角，但配色由我们按主题设置（见下方）。
        _root = new Grid
        {
            Background = Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] as Brush
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var appTitleBar = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12, 0, 0, 0)
        };
        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleIcon = new Image { Width = 20, Height = 20 };
        _ = LoadTitleBarIconAsync(titleIcon);
        titlePanel.Children.Add(titleIcon);
        titlePanel.Children.Add(new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style
        });
        appTitleBar.Children.Add(titlePanel);
        Grid.SetRow(appTitleBar, 0);
        _root.Children.Add(appTitleBar);

        var contentHost = new Grid { Padding = new Thickness(20) };
        contentHost.Children.Add(layout);
        Grid.SetRow(contentHost, 1);
        _root.Children.Add(contentHost);

        Content = _root;

        // 把内容延伸到标题栏，并指定可拖动区域
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(appTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 工具窗口是置顶的，本窗口也必须置顶并放到置顶层最上面；
        // 同时它是"弹窗"语义：固定尺寸、只有关闭按钮（最小化/最大化没有意义）
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // 显示位置：居中到"常用工具窗口所在的显示器"。
        // 不显式定位时，Windows 会把新窗口默认放到它自己挑的显示器上（多显示器下可能跑到别的屏）。
        var referenceHandle = ToolsWindow.CurrentInstance?.WindowHandle ?? hWnd;
        WindowPositionHelper.PlaceDialogCenteredCenter(
            AppWindow, referenceHandle, hWnd, WindowWidth, WindowHeight);

        // Esc 关闭（窗口本身就是弹窗语义，关闭 = 关掉自己，不需要隐藏）
        WindowKeyHelper.EnableEscapeToHide(_root, () => Close());

        var settings = SettingsService.Instance.Settings;
        BackdropService.Apply(this, settings.Backdrop);
        ThemeService.Apply(_root, settings.Theme);
        // 自绘标题栏的按钮配色（与主窗口/设置窗口同款）。
        // 注意：ActualTheme 要等窗口加载后才可靠，所以激活时再应用一次。
        ThemeService.ApplyCaptionButtonColors(AppWindow, _root);
        Activated += (_, _) => ThemeService.ApplyCaptionButtonColors(AppWindow, _root);

        // 关闭后释放静态引用（下次点"管理配置"会重新创建）
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };

        Rebuild();
    }

    /// <summary>加载标题栏左侧的应用图标（嵌入资源，和其他窗口一致）</summary>
    private static async Task LoadTitleBarIconAsync(Image image)
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) image.Source = bitmap;
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

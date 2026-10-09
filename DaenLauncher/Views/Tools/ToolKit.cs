using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DaenLauncher.Views.Tools;

/// <summary>
/// 常用工具的共用 UI 构建器：所有工具页面的输入框、下拉框、按钮、复制按钮等
/// 都从这里创建，保证风格统一、代码不重复。
/// 全部是静态方法，工具类直接 ToolKit.Xxx() 调用。
/// </summary>
internal static class ToolKit
{
    /// <summary>复制反馈显示的毫秒数（按钮文字短暂变成"已复制"）</summary>
    private const int CopiedFeedbackMs = 1200;

    /// <summary>小标签文字（选项行左侧的说明文字）</summary>
    public static TextBlock Label(string key)
    {
        return new TextBlock
        {
            Text = LocalizationService.Tr(key),
            FontSize = 13,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>一行选项：左侧标签 + 右侧控件（标签固定宽度，多行对齐）</summary>
    public static StackPanel OptionRow(string labelKey, FrameworkElement control, double labelWidth = 110)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var label = Label(labelKey);
        label.Width = labelWidth;
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// 一行选项（控件可拉伸版）：标签固定宽度，右侧控件占满剩余宽度。
    /// 用 Grid 而不是横排 StackPanel，这样长输入框能跟着窗口变宽。
    /// </summary>
    public static Grid OptionRowStretch(string labelKey, FrameworkElement control, double labelWidth = 110)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = Label(labelKey);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(label, 0);
        control.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>
    /// 给多行输入框包一层"可调大小"容器：右下角出现一个小三角拖柄，
    /// 按住拖动可以调整输入框的宽高（最小宽高跟着变，窗口拉大时仍能占满）。
    /// </summary>
    public static Grid ResizableHost(TextBox box, double defaultHeight)
    {
        box.MinHeight = defaultHeight;
        box.Height = defaultHeight;

        var grip = new ResizeGripPanel
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        grip.Children.Add(new TextBlock
        {
            Text = "◢",
            FontSize = 11,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        // 悬停拖柄时换成对角调整光标，离开恢复箭头（Win32 方案）
        grip.PointerEntered += (_, _) => SetCursor(LoadCursor(IntPtr.Zero, IdcSizeNesw));
        grip.PointerExited += (_, _) => SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));

        double? lastX = null, lastY = null;
        grip.PointerPressed += (_, e) =>
        {
            grip.CapturePointer(e.Pointer);
            var p = e.GetCurrentPoint(grip).Position;
            lastX = p.X;
            lastY = p.Y;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (lastX == null || lastY == null)
            {
                return; // 没按下
            }
            var p = e.GetCurrentPoint(grip).Position;
            var dx = p.X - lastX.Value;
            var dy = p.Y - lastY.Value;
            lastX = p.X;
            lastY = p.Y;
            // 调最小宽高：拖大/拖小都生效，且不影响布局的拉伸行为
            box.MinWidth = Math.Clamp(box.MinWidth + dx, 200, 1000);
            box.MinHeight = Math.Clamp(box.MinHeight + dy, 50, 600);
            box.Height = Math.Clamp(box.Height + dy, 50, 600);
        };
        grip.PointerReleased += (_, e) =>
        {
            grip.ReleasePointerCapture(e.Pointer);
            lastX = lastY = null;
        };

        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(grip);
        return host;
    }

    /// <summary>
    /// 调整大小拖柄的容器：一个透明的小面板（Panel 默认不可命中，透明背景让指针事件生效）。
    /// 光标变化用 Win32 SetCursor 实现——ProtectedCursor 是受保护 API 拿不到，
    /// WinUI 又没有公开的"元素级"改光标方法（Panel 也没有光标虚方法可重写）。
    /// </summary>
    private sealed class ResizeGripPanel : Panel
    {
        public ResizeGripPanel()
        {
            // 透明背景保证可命中（Panel 默认 Background=null 不响应指针）
            Background = new SolidColorBrush(Colors.Transparent);
        }

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            foreach (var child in Children)
            {
                child.Measure(availableSize);
            }
            return new Windows.Foundation.Size(0, 0); // 实际大小由外部 Width/Height 决定
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            foreach (var child in Children)
            {
                child.Arrange(new Windows.Foundation.Rect(new Windows.Foundation.Point(0, 0), finalSize));
            }
            return finalSize;
        }
    }

    private const int IdcArrow = 32512;     // Win32 标准箭头光标
    private const int IdcSizeNesw = 32644;  // Win32 对角调整光标

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    /// <summary>多行文本输入框（工具的"输入"区）</summary>
    public static TextBox InputArea(double minHeight = 110, string placeholderKey = "")
    {
        var box = new TextBox
        {
            MinHeight = minHeight,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            PlaceholderText = placeholderKey.Length == 0 ? "" : LocalizationService.Tr(placeholderKey)
        };
        box.AcceptsReturn = true; // 多行属性在构造后单独设置（初始化器里设置有时序问题）
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        return box;
    }

    /// <summary>多行文本输出框（只读，工具的"结果"区）</summary>
    public static TextBox OutputArea(double minHeight = 110)
    {
        var box = new TextBox
        {
            MinHeight = minHeight,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            IsSpellCheckEnabled = false
        };
        box.AcceptsReturn = true;
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        return box;
    }

    /// <summary>单行输入框</summary>
    public static TextBox InputLine(double minWidth = 0, string placeholderKey = "")
    {
        return new TextBox
        {
            MinWidth = minWidth,
            IsSpellCheckEnabled = false,
            PlaceholderText = placeholderKey.Length == 0 ? "" : LocalizationService.Tr(placeholderKey)
        };
    }

    /// <summary>
    /// 下拉框：选项文字来自语言键数组，返回下拉框（默认选中第 0 项）。
    /// 取当前选中项的语言键用 SelectedKey()。
    /// </summary>
    public static ComboBox Combo(params string[] optionKeys)
    {
        var combo = new ComboBox
        {
            ItemsSource = optionKeys.Select(LocalizationService.Tr).ToArray(),
            SelectedIndex = 0,
            MinWidth = 160
        };
        combo.Tag = optionKeys; // 记住语言键，语言切换时重建页面用不到，但调试方便
        return combo;
    }

    /// <summary>取下拉框当前选中项的语言键</summary>
    public static string SelectedKey(ComboBox combo)
    {
        var keys = combo.Tag as string[];
        var index = combo.SelectedIndex;
        if (keys == null || index < 0 || index >= keys.Length)
        {
            return "";
        }
        return keys[index];
    }

    /// <summary>数字输入框（带范围钳制）</summary>
    public static NumberBox NumberBox(double min, double max, double value, double width = 160)
    {
        return new NumberBox
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = width
        };
    }

    /// <summary>普通按钮（点击处理器）</summary>
    public static Button ActionButton(string textKey, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = LocalizationService.Tr(textKey) },
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6)
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>蓝色强调按钮（每个工具的主操作按钮）</summary>
    public static Button PrimaryButton(string textKey, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Padding = new Thickness(18, 6, 18, 6),
            CornerRadius = new CornerRadius(6)
        };
        button.Click += onClick;
        var text = new TextBlock { Text = LocalizationService.Tr(textKey) };
        button.Content = text;
        // 用系统强调色浅背景 + 深色文字，主题切换时跟随（动态画刷只能用 Style 资源绑定）
        button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        return button;
    }

    /// <summary>
    /// "复制结果"按钮：点击时把 provider() 返回的文字复制到剪贴板，
    /// 按钮文字短暂变成"已复制"作为反馈。
    /// </summary>
    public static Button CopyButton(Func<string> provider)
    {
        var text = new TextBlock { Text = LocalizationService.Tr("Tools.CopyResult") };
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6)
        };
        button.Click += (_, _) =>
        {
            var value = provider();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(value);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // 让系统接管内容，应用退出后仍可粘贴

            // 短暂显示"已复制"反馈，然后恢复按钮文字
            text.Text = LocalizationService.Tr("Clipboard.Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CopiedFeedbackMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                text.Text = LocalizationService.Tr("Tools.CopyResult");
            };
            timer.Start();
        };
        return button;
    }

    /// <summary>复选框（选中变化回调 onChanged 参数 = 是否勾选）</summary>
    public static CheckBox Check(string textKey, bool isChecked, Action<bool>? onChanged = null)
    {
        var check = new CheckBox
        {
            Content = LocalizationService.Tr(textKey),
            IsChecked = isChecked
        };
        if (onChanged != null)
        {
            check.Checked += (_, _) => onChanged(true);
            check.Unchecked += (_, _) => onChanged(false);
        }
        return check;
    }

    /// <summary>一行操作按钮（按钮横排，右对齐）</summary>
    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 8)
        };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>把输出文字和错误信息写进输出框（错误以 ✗ 开头；换行统一成 \r\n 保证 TextBox 正确显示）</summary>
    public static void SetOutput(TextBox output, string? text, bool isError = false)
    {
        output.Text = isError ? "✗ " + text : NormalizeNewlines(text ?? "");
    }

    /// <summary>
    /// 把状态提示写进一行小字（不是文本框的提示行，如"正在读取/已获取/错误"）。
    /// 错误同样以 ✗ 开头，成功/进行中的提示保持原样。
    /// </summary>
    public static void SetStatus(TextBlock status, string? text, bool isError = false)
    {
        status.Text = isError ? "✗ " + text : (text ?? "");
    }

    /// <summary>换行统一转成 \r\n（WinUI TextBox 只认 \r\n 才显示成换行）</summary>
    public static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }

    /// <summary>
    /// 按行切分文本。WinUI 的 TextBox 换行符是 \r（不是 \n/\r\n），
    /// 所以三种换行都要归一化，否则多行处理全部失效。
    /// </summary>
    public static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>把多行拼回一段文本（用 Windows 标准 \r\n，TextBox 显示和复制到剪贴板都正确）</summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        return string.Join("\r\n", lines);
    }

    /// <summary>构建一个工具页的根容器（纵向排布各部分，带统一间距）</summary>
    public static StackPanel PageRoot()
    {
        return new StackPanel { Spacing = 4, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>分区小标题（输入 / 结果 等区块标题）</summary>
    public static TextBlock SectionTitle(string key)
    {
        return new TextBlock
        {
            Text = LocalizationService.Tr(key),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 4)
        };
    }
}

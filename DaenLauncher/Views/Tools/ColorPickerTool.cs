using System.Globalization;
using System.Runtime.InteropServices;
using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DaenLauncher.Views.Tools;

// =====================================================================
// 颜色选择器：
// - SV 颜色盘（饱和度/明度二维色块）+ 色相滑条选原色；
// - "屏幕取色"：启动后移动鼠标实时采样屏幕颜色，单击任意位置确认；
// - 输出 HEX / RGB / HSL / HSV / HWB / LAB / LCH / CMYK 八种格式。
// 颜色数学换算在 ColorConverter；屏幕取色的鼠标钩子在 ScreenColorPicker。
// =====================================================================

internal sealed class ColorPickerTool : IToolPage
{
    // ===== 当前颜色状态（HSV 存储，RGB 随用随算） =====
    private double _h;           // 色相 0-360
    private double _s = 1.0;     // 饱和度 0-1
    private double _v = 1.0;     // 明度 0-1

    // ===== 界面控件 =====
    private Rectangle _svPadBase = null!;   // 颜色盘底层（白→纯色横向渐变）
    private Rectangle _svPadShade = null!;  // 颜色盘上层（透明→黑纵向渐变）
    private Ellipse _svThumb = null!;       // 颜色盘上的圆形指示点
    private Grid _padGrid = null!;          // 颜色盘容器（取尺寸用）
    private Slider _hueSlider = null!;      // 色相滑条
    private Rectangle _hueBar = null!;      // 滑条下方的彩虹条（视觉参考）
    private Border _preview = null!;        // 当前颜色预览块
    private TextBlock _previewText = null!;
    private TextBox _output = null!;
    private Button _pickButton = null!;

    /// <summary>取色轮询计时器（取色期间每帧采样光标下的颜色）</summary>
    private DispatcherTimer? _sampleTimer;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        // ===== 预览 + 屏幕取色按钮 =====
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 10) };
        _preview = new Border
        {
            Width = 120,
            Height = 60,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };
        topRow.Children.Add(_preview);
        var previewStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        _previewText = new TextBlock { FontSize = 13 };
        previewStack.Children.Add(_previewText);
        topRow.Children.Add(previewStack);

        _pickButton = ToolKit.PrimaryButton("Tools.Color.Pick", (_, _) => TogglePicking());
        topRow.Children.Add(_pickButton);
        root.Children.Add(topRow);

        // ===== 颜色盘（SV 二维色块） =====
        var pad = new Grid
        {
            Width = 260,
            Height = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _svPadBase = new Rectangle { RadiusX = 8, RadiusY = 8 };
        _padGrid = pad;
        _svPadShade = new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                [
                    new GradientStop { Color = Colors.Transparent, Offset = 0 },
                    new GradientStop { Color = Colors.Black, Offset = 1 }
                ]
            }
        };
        // 指示点放在 Canvas 覆盖层里（Canvas.SetLeft/SetTop 只对 Canvas 子元素生效）
        var thumbLayer = new Canvas { IsHitTestVisible = false };
        _svThumb = new Ellipse
        {
            Width = 16,
            Height = 16,
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(Colors.White),
            Fill = new SolidColorBrush(Colors.Transparent)
        };
        thumbLayer.Children.Add(_svThumb);
        pad.Children.Add(_svPadBase);
        pad.Children.Add(_svPadShade);
        pad.Children.Add(thumbLayer);
        // 指针在色块上按下/拖动 = 调整饱和度和明度（两个事件分开挂，共用同一处理）
        pad.PointerPressed += OnPadPointer;
        pad.PointerMoved += OnPadPointerMoved;
        root.Children.Add(pad);

        // ===== 色相滑条 + 彩虹条 =====
        _hueSlider = new Slider { Minimum = 0, Maximum = 360, Value = 0, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        _hueSlider.ValueChanged += (_, _) => { _h = _hueSlider.Value; RefreshAll(); };
        root.Children.Add(_hueSlider);
        _hueBar = new Rectangle { Height = 10, Width = 260, RadiusX = 5, RadiusY = 5, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(_hueBar);

        // ===== 输出 =====
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(170);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));

        RefreshAll();
        return root;
    }

    #region 颜色盘交互

    /// <summary>指针在颜色盘上按下：按位置换算饱和度（横向）和明度（纵向）</summary>
    private void OnPadPointer(object sender, PointerRoutedEventArgs e) => ApplyPadPoint(sender, e, requirePressed: false);

    /// <summary>指针在颜色盘上拖动：只有按住左键时才调整</summary>
    private void OnPadPointerMoved(object sender, PointerRoutedEventArgs e) => ApplyPadPoint(sender, e, requirePressed: true);

    /// <summary>按指针在颜色盘上的位置换算饱和度（横向）和明度（纵向）</summary>
    private void ApplyPadPoint(object sender, PointerRoutedEventArgs e, bool requirePressed)
    {
        if (sender is not Grid pad)
        {
            return;
        }
        var point = e.GetCurrentPoint(pad);
        // 拖动时要求按住左键；按下事件不要求
        if (requirePressed && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        pad.CapturePointer(e.Pointer);
        var width = Math.Max(1, pad.ActualWidth);
        var height = Math.Max(1, pad.ActualHeight);
        _s = Math.Clamp(point.Position.X / width, 0, 1);
        _v = 1 - Math.Clamp(point.Position.Y / height, 0, 1);
        RefreshAll();
    }

    #endregion

    #region 屏幕取色

    /// <summary>开始/停止屏幕取色（取色中：移动鼠标实时采样，单击任意位置确认）</summary>
    private void TogglePicking()
    {
        if (_sampleTimer != null)
        {
            StopPicking();
            return;
        }

        ScreenColorPicker.Begin(() =>
        {
            // 低级鼠标钩子捕获到"确认单击"后回调（UI 线程）
            StopPicking();
        });

        _sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _sampleTimer.Tick += (_, _) =>
        {
            var color = ScreenColorPicker.SamplePixelUnderCursor();
            if (color.HasValue)
            {
                SetColor(color.Value);
            }
        };
        _sampleTimer.Start();
        _pickButton.Content = new TextBlock { Text = LocalizationService.Tr("Tools.Color.Picking") };
    }

    /// <summary>停止屏幕取色（恢复按钮文字、停采样、卸钩子）</summary>
    private void StopPicking()
    {
        _sampleTimer?.Stop();
        _sampleTimer = null;
        ScreenColorPicker.End();
        _pickButton.Content = new TextBlock { Text = LocalizationService.Tr("Tools.Color.Pick") };
    }

    #endregion

    #region 颜色刷新

    /// <summary>按当前 HSV 设置颜色（RGB 换算一次，界面 + 输出全部刷新）</summary>
    private void SetColor(Color rgb)
    {
        var (h, s, v) = ColorConverter.RgbToHsv(rgb);
        _h = h;
        _s = s;
        _v = v;
        RefreshAll();
    }

    /// <summary>刷新颜色盘渐变、指示点、预览块和输出文字</summary>
    private void RefreshAll()
    {
        var rgb = ColorConverter.HsvToRgb(_h, _s, _v);

        // 颜色盘底层：白 → 当前色相纯色（横向）
        var pure = ColorConverter.HsvToRgb(_h, 1, 1);
        _svPadBase.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            [
                new GradientStop { Color = Colors.White, Offset = 0 },
                new GradientStop { Color = pure, Offset = 1 }
            ]
        };

        // 指示点位置 = 饱和度/明度（Canvas 覆盖层里用 SetLeft/SetTop）
        if (_padGrid.ActualWidth > 0)
        {
            Canvas.SetLeft(_svThumb, _s * _padGrid.ActualWidth - 8);
            Canvas.SetTop(_svThumb, (1 - _v) * _padGrid.ActualHeight - 8);
        }

        // 彩虹条（色相参考）
        _hueBar.Fill = CreateHueGradient();

        // 预览块 + 文字
        _preview.Background = new SolidColorBrush(rgb);
        _previewText.Text = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";

        RefreshOutput(rgb);
    }

    /// <summary>生成 0°→360° 的彩虹渐变画刷（颜色盘用）</summary>
    private static LinearGradientBrush CreateHueGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        for (var i = 0; i <= 6; i++)
        {
            brush.GradientStops.Add(new GradientStop
            {
                Color = ColorConverter.HsvToRgb(i * 60, 1, 1),
                Offset = i / 6.0
            });
        }
        return brush;
    }

    /// <summary>把当前颜色按 8 种格式输出到结果框</summary>
    private void RefreshOutput(Color rgb)
    {
        var (hslH, hslS, hslL) = ColorConverter.RgbToHsl(rgb.R, rgb.G, rgb.B);
        var hwbW = Math.Min(Math.Min(rgb.R, rgb.G), rgb.B) / 255.0;
        var hwbB = 1 - Math.Max(Math.Max(rgb.R, rgb.G), rgb.B) / 255.0;
        var lab = ColorConverter.RgbToLab(rgb);
        var (lchL, lchC, lchH) = ColorConverter.LabToLch(lab);
        var (c, m, y, k) = ColorConverter.RgbToCmyk(rgb);

        var invariant = CultureInfo.InvariantCulture;
        _output.Text = string.Join("\n",
            $"HEX: #{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}",
            $"RGB: rgb({rgb.R}, {rgb.G}, {rgb.B})",
            $"HSL: hsl({hslH.ToString("0", invariant)}, {(hslS * 100).ToString("0", invariant)}%, {(hslL * 100).ToString("0", invariant)}%)",
            $"HSV: hsv({_h.ToString("0", invariant)}, {(_s * 100).ToString("0", invariant)}%, {(_v * 100).ToString("0", invariant)}%)",
            $"HWB: hwb({_h.ToString("0", invariant)}, {(hwbW * 100).ToString("0", invariant)}%, {(hwbB * 100).ToString("0", invariant)}%)",
            $"LAB: lab({lab.L.ToString("0.0", invariant)}, {lab.A.ToString("0.0", invariant)}, {lab.B.ToString("0.0", invariant)})",
            $"LCH: lch({lchL.ToString("0.0", invariant)}, {lchC.ToString("0.0", invariant)}, {lchH.ToString("0", invariant)})",
            $"CMYK: cmyk({(c * 100).ToString("0", invariant)}%, {(m * 100).ToString("0", invariant)}%, {(y * 100).ToString("0", invariant)}%, {(k * 100).ToString("0", invariant)}%)");
    }

    #endregion
}

/// <summary>颜色换算：RGB/HSV/HSL/HWB/LAB/LCH/CMYK（纯数学，无界面依赖）</summary>
internal static class ColorConverter
{
    public record LabColor(double L, double A, double B);

    /// <summary>HSV → RGB（h 0-360，s/v 0-1）</summary>
    public static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>RGB → HSV</summary>
    public static (double h, double s, double v) RgbToHsv(Color rgb)
    {
        return RgbToHsv(rgb.R / 255.0, rgb.G / 255.0, rgb.B / 255.0);
    }

    public static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            h = max switch
            {
                _ when max == r => 60 * (((g - b) / delta) % 6),
                _ when max == g => 60 * ((b - r) / delta + 2),
                _ => 60 * ((r - g) / delta + 4)
            };
        }
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>RGB → HSL（返回 h 0-360，s/l 0-1）</summary>
    public static (double h, double s, double l) RgbToHsl(double r8, double g8, double b8)
    {
        var (h, s, v) = RgbToHsv(r8 / 255.0, g8 / 255.0, b8 / 255.0);
        var l = v * (1 - s / 2);
        var sl = l is 0 or 1 ? 0 : (v - l) / Math.Min(l, 1 - l);
        return (h, sl, l);
    }

    /// <summary>RGB → CMYK（返回 0-1 的四个分量）</summary>
    public static (double c, double m, double y, double k) RgbToCmyk(Color rgb)
    {
        var r = rgb.R / 255.0;
        var g = rgb.G / 255.0;
        var b = rgb.B / 255.0;
        var k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1)
        {
            return (0, 0, 0, 1);
        }
        return ((1 - r - k) / (1 - k), (1 - g - k) / (1 - k), (1 - b - k) / (1 - k), k);
    }

    // ===== LAB / LCH（sRGB D65 → XYZ → CIELAB，标准 sRGB 伽马展开） =====

    /// <summary>8 位通道 → 线性 RGB</summary>
    private static double ToLinear(double channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>线性 RGB → 通道值</summary>
    private static double FromLinear(double c) =>
        c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    public static LabColor RgbToLab(Color rgb)
    {
        var r = ToLinear(rgb.R);
        var g = ToLinear(rgb.G);
        var b = ToLinear(rgb.B);

        // sRGB(D65) → XYZ
        var x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
        var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        var z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;

        // D65 参考白
        const double xn = 0.95047, yn = 1.0, zn = 1.08883;
        var fx = LabF(x / xn);
        var fy = LabF(y / yn);
        var fz = LabF(z / zn);
        return new LabColor(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double LabF(double t) =>
        t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;

    /// <summary>LAB → LCH（C = 色度，H = 色相角 0-360）</summary>
    public static (double l, double c, double h) LabToLch(LabColor lab)
    {
        var c = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        var h = Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
        if (h < 0)
        {
            h += 360;
        }
        return (lab.L, c, h);
    }

    /// <summary>线性 RGB → 通道字节（预留用）</summary>
    public static byte ToByte(double linear) => (byte)Math.Round(Math.Clamp(FromLinear(linear), 0, 1) * 255);
}

/// <summary>
/// 屏幕取色：低级鼠标钩子（WH_MOUSE_LL）捕获"确认单击"，DispatcherTimer 由工具页面驱动，
/// SamplePixelUnderCursor 用 GDI GetPixel 读取光标位置像素。
/// </summary>
internal sealed class ScreenColorPicker
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;

    private IntPtr _hook;
    private LowLevelMouseProc? _proc; // 持有委托引用防止被 GC 回收（钩子回调悬空会崩溃）
    private Action? _onConfirmed;

    public static void Begin(Action onConfirmed)
    {
        var picker = Instance;
        picker._onConfirmed = onConfirmed;
        picker._proc = HookProc;
        picker._hook = SetWindowsHookEx(WhMouseLl, picker._proc, GetModuleHandleW(null), 0);
    }

    public static void End()
    {
        var picker = Instance;
        if (picker._hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(picker._hook);
            picker._hook = IntPtr.Zero;
        }
        picker._onConfirmed = null;
    }

    /// <summary>读取光标位置像素颜色（读不到返回 null）</summary>
    public static Color? SamplePixelUnderCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return null;
        }
        var dc = GetDC(IntPtr.Zero); // 整屏 DC
        try
        {
            var colorRef = GetPixel(dc, point.X, point.Y);
            // COLORREF = 0x00BBGGRR
            return Color.FromArgb(255,
                (byte)(colorRef & 0xFF),
                (byte)((colorRef >> 8) & 0xFF),
                (byte)((colorRef >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static ScreenColorPicker Instance { get; } = new();

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var picker = Instance;
        if (nCode >= 0 && wParam == WmLButtonDown)
        {
            // 确认取色的这一次点击：吞掉（不传给下面的窗口），并通知工具结束取色
            picker._onConfirmed?.Invoke();
            return 1;
        }
        return CallNextHookEx(picker._hook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}

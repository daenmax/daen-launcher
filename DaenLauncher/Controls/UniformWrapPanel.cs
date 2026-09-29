using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DaenLauncher.Controls;

/// <summary>
/// 等宽换行面板（平铺模式专用）。
/// 和普通 WrapPanel 的区别：
/// 1. 每行放几个只取决于面板宽度（格子宽 = 所有子元素的最大需求宽度），不受某个项目文字长短影响；
/// 2. 所有格子宽度相同、每行列数相同 → 所有列严格对齐、行高一致；
/// 3. 最后一行不满时格子仍然平分整行宽度（铺满，不留缺口）。
/// 子元素会被拉伸填满自己的格子，格子内的对齐（居左/居中/居右）由子元素自身的
/// HorizontalContentAlignment / VerticalContentAlignment 控制。
/// 间距沿用项目的 Margin 方案（与主窗口其他面板一致）。
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    // 测量阶段算出的统一格子尺寸（含子元素外边距）
    private double _cellWidth;
    private double _cellHeight;

    /// <summary>固定列数（0 = 按宽度自动算）。列表模式用 1：一行一个、每行铺满面板宽度。</summary>
    public int FixedColumns { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 1. 先测量每个子元素，取最大需求宽度/高度作为统一格子尺寸
        double maxW = 0, maxH = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child is not FrameworkElement element) continue;
            var m = element.Margin;
            maxW = Math.Max(maxW, child.DesiredSize.Width + m.Left + m.Right);
            maxH = Math.Max(maxH, child.DesiredSize.Height + m.Top + m.Bottom);
        }
        _cellWidth = maxW;
        _cellHeight = maxH;

        // 2. 确定列数：固定列数优先；否则一行能放几个由可用宽度决定（宽度无穷时全部排一行）
        int columns;
        if (FixedColumns > 0)
        {
            columns = FixedColumns;
        }
        else
        {
            columns = Math.Max(1, Children.Count);
            if (!double.IsInfinity(availableSize.Width) && maxW > 0)
            {
                columns = Math.Max(1, (int)(availableSize.Width / maxW));
            }
            columns = Math.Min(columns, Math.Max(1, Children.Count));
        }

        var rows = (int)Math.Ceiling(Children.Count / (double)columns);

        // 固定列数（列表模式）时报告"可用宽度"作为期望宽度，
        // 让面板始终占满宿主宽度（这样每行按钮铺满整行，悬停高亮也是整行）
        var desiredWidth = FixedColumns > 0 && !double.IsInfinity(availableSize.Width)
            ? Math.Max(maxW, availableSize.Width)
            : columns * maxW;
        return new Size(desiredWidth, rows * maxH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0 || _cellWidth <= 0 || _cellHeight <= 0)
        {
            return finalSize;
        }

        // 排列时按最终宽度重算列数（测量和排列的宽度可能有细微差异）；固定列数则不重算
        var columns = FixedColumns > 0
            ? FixedColumns
            : Math.Min(Math.Max(1, (int)(finalSize.Width / _cellWidth)), Children.Count);

        // 格子平分整行宽度 → 最后一行不满也铺满，所有列对齐
        var cellW = finalSize.Width / columns;
        var cellH = _cellHeight;

        for (var i = 0; i < Children.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            // 外边距由框架自动从格子矩形里扣除（间距方案与其他面板一致）
            Children[i].Arrange(new Rect(col * cellW, row * cellH, cellW, cellH));
        }
        return finalSize;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DaenLauncher.Controls;

/// <summary>
/// 等宽换行面板（平铺模式专用）。
///
/// 布局规则（需求）：
/// 1. **格子宽度是固定值**，由"项目图标大小 / 项目文字大小 / 横向间距"设置算出来
///    （见 <see cref="DaenLauncher.Models.AppSettings.ComputeItemCellWidth"/>），
///    与项目数量、文字长短都无关；
/// 2. **一行放几列只由"窗口宽度 ÷ 格子宽度"决定**，向下取整；
/// 3. 格子**不拉伸**——末行不满时不会把剩余宽度均分给几个格子（那会让列位置随项目数变化），
///    所有子分类的列数与列位置因此完全一致、天然对齐；
/// 4. 文字在该格子宽度内换行/超出省略（布局给的宽度约束决定了文字能显示多少）。
///
/// 列表模式用 <see cref="FixedColumns"/> = 1：一行一个、每行铺满面板宽度（悬停高亮铺满整行）。
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    /// <summary>测量阶段算出的统一格子高度（含子元素外边距）</summary>
    private double _cellHeight;

    /// <summary>测量阶段用的格子宽度（含子元素外边距）</summary>
    private double _cellWidth;

    /// <summary>
    /// 固定列数（0 = 按宽度自动算）。列表模式用 1：一行一个、每行铺满面板宽度。
    /// 固定列数模式下格子会拉伸铺满整行（与平铺模式的"格子宽度固定"不同）。
    /// </summary>
    public int FixedColumns { get; set; }

    /// <summary>
    /// 平铺模式的格子宽度（逻辑像素，含项目左右外边距）。
    /// 由设置算出后赋给面板；0 时退化为"按内容自适应"（仅用于没有设置的极端情况）。
    /// </summary>
    public double CellWidth { get; set; }

    /// <summary>格子宽度的下限（窗口很窄时不至于把内容压没）</summary>
    private const double MinCellWidth = 40;

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;

        // ===== 1. 确定格子宽度 =====
        // 列表模式（固定列数）：格子 = 整行宽度平分（铺满，悬停高亮满行）
        // 平铺模式：格子宽度固定（来自设置），不随可用宽度变化
        double cellWidth;
        if (FixedColumns > 0)
        {
            cellWidth = double.IsInfinity(availableWidth)
                ? Math.Max(MinCellWidth, CellWidth)
                : Math.Max(MinCellWidth, availableWidth / FixedColumns);
        }
        else
        {
            cellWidth = Math.Max(MinCellWidth, CellWidth);
        }
        _cellWidth = cellWidth;

        // ===== 2. 用"格子宽度 - 该子元素左右外边距"作为宽度约束测量子元素 =====
        // 这一步决定了文字能显示多宽：约束内换行、超出省略（需求：文字长度取决于格子宽度）
        double maxHeight = 0;
        foreach (var child in Children)
        {
            var m = child is FrameworkElement fe ? fe.Margin : new Thickness(0);
            var childWidth = Math.Max(0, cellWidth - m.Left - m.Right);
            child.Measure(new Size(childWidth, double.PositiveInfinity));
            if (child is not FrameworkElement element) continue;
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Height + m.Top + m.Bottom);
        }
        _cellHeight = maxHeight;

        // ===== 3. 列数只由"可用宽度 ÷ 格子宽度"决定 =====
        // 注意：不按子元素数量收敛列数——列位置必须由窗口宽度决定，
        // 这样项目少的子分类和项目多的子分类列位置一致（需求）。
        int columns;
        if (FixedColumns > 0)
        {
            columns = FixedColumns;
        }
        else if (double.IsInfinity(availableWidth))
        {
            // 宽度未知（未布局）：先按内容数量排一行，等真正布局时再算
            columns = Math.Max(1, Children.Count);
        }
        else
        {
            columns = Math.Max(1, (int)(availableWidth / cellWidth));
        }

        var rows = Children.Count == 0 ? 0 : (int)Math.Ceiling(Children.Count / (double)columns);

        // ===== 4. 期望尺寸 =====
        // 固定列数（列表模式）报告可用宽度 → 面板占满宿主，每行按钮铺满整行；
        // 平铺模式报告"列数 × 格子宽度"，不铺满剩余空间（格子不拉伸）
        var desiredWidth = FixedColumns > 0 && !double.IsInfinity(availableWidth)
            ? availableWidth
            : columns * cellWidth;
        return new Size(desiredWidth, rows * maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0 || _cellWidth <= 0 || _cellHeight <= 0)
        {
            return finalSize;
        }

        // 列数：固定列数优先；否则只由最终宽度和格子宽度决定（与测量阶段同一公式）
        int columns;
        if (FixedColumns > 0)
        {
            columns = FixedColumns;
        }
        else if (double.IsInfinity(finalSize.Width))
        {
            columns = Math.Max(1, Children.Count);
        }
        else
        {
            columns = Math.Max(1, (int)(finalSize.Width / _cellWidth));
        }

        // 固定列数（列表模式）：格子平分整行 → 铺满；
        // 平铺模式：格子宽度固定，剩余空间留白（最后一行不满也不均分，保证列位置恒定）
        var cellWidth = FixedColumns > 0 ? finalSize.Width / columns : _cellWidth;
        var cellHeight = _cellHeight;

        for (var i = 0; i < Children.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            // 外边距由框架自动从格子矩形里扣除（项目间距由此生效）
            Children[i].Arrange(new Rect(col * cellWidth, row * cellHeight, cellWidth, cellHeight));
        }
        return finalSize;
    }
}

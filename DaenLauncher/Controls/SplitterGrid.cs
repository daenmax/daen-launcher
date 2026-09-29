using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DaenLauncher.Controls;

/// <summary>
/// 左右分隔条：自定义控件以便在内部设置 ProtectedCursor
/// （ProtectedCursor 是 protected 成员，只有派生类内部能改）。
/// 悬停时显示左右调整光标，提示用户可拖拽（优化项1）。
/// </summary>
public sealed class SplitterGrid : Grid
{
    /// <summary>设置光标：on=true 左右调整箭头，on=false 恢复默认箭头</summary>
    public void SetSplitterCursor(bool on)
    {
        ProtectedCursor = on
            ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast)
            : null;
    }
}

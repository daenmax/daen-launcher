namespace DaenLauncher.Models;

/// <summary>
/// 待办颜色标记（参考 DeskBox 的 8 种颜色）。存储用小写英文标记，显示用对应色值。
/// 空字符串 = 无颜色。
/// </summary>
public static class TodoColors
{
    public const string None = "";
    public const string Red = "red";
    public const string Orange = "orange";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Blue = "blue";
    public const string Purple = "purple";
    public const string Teal = "teal";
    public const string Pink = "pink";

    /// <summary>支持的全部颜色标记（按界面显示顺序）</summary>
    public static readonly string[] All =
    [
        Red, Orange, Yellow, Green, Blue, Purple, Teal, Pink
    ];

    /// <summary>颜色标记对应的显示色值（十六进制，参考 DeskBox）</summary>
    public static string GetHex(string? marker) => marker switch
    {
        Red => "#E34D4D",
        Orange => "#F08A3C",
        Yellow => "#F2C94C",
        Green => "#4CAF6D",
        Blue => "#4D8FE3",
        Purple => "#9B6BE8",
        Teal => "#2DB7A3",
        Pink => "#E66AA2",
        _ => "#8A8F98"
    };

    /// <summary>把非法颜色标记归一化为无颜色（云同步数据可能被其他客户端改过）</summary>
    public static string Normalize(string? marker) =>
        string.IsNullOrWhiteSpace(marker) ? None
        : All.Contains(marker.Trim().ToLowerInvariant()) ? marker.Trim().ToLowerInvariant()
        : None;
}

/// <summary>
/// 一条待办任务。
/// 说明：按需求不做"提醒、重复、步骤、附件、Markdown"，保留基础属性 +
/// 截止日期（"今天"标签页依赖它：显示今天截止和已过期的未完成任务）。
/// </summary>
public sealed class TodoItem
{
    /// <summary>唯一 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>任务内容</summary>
    public string Text { get; set; } = "";

    /// <summary>备注（可选，第二需求新增）</summary>
    public string Notes { get; set; } = "";

    /// <summary>是否已完成</summary>
    public bool IsCompleted { get; set; }

    /// <summary>是否重要</summary>
    public bool IsImportant { get; set; }

    /// <summary>颜色标记（TodoColors 里的常量，空 = 无颜色）</summary>
    public string Color { get; set; } = TodoColors.None;

    /// <summary>截止日期（可选，null = 没有截止日期）</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>完成时间（用于记录，可选）</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>任务文本的显示修饰：已完成加删除线</summary>
    public Windows.UI.Text.TextDecorations TextDecorations =>
        IsCompleted ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
}

/// <summary>
/// 待办数据根对象，序列化保存到 data\todo\todo.json，
/// 云同步时也用这个结构序列化后放进便签的 DaenLauncher-Todo 条目。
/// </summary>
public sealed class TodoData
{
    /// <summary>数据格式版本（将来结构调整用）</summary>
    public int Version { get; set; } = 1;

    /// <summary>全部待办（列表顺序即显示顺序）</summary>
    public List<TodoItem> Items { get; set; } = new();
}

/// <summary>待办窗口的标签页（需求：全部、今天、重要、已完成）</summary>
public enum TodoFilterTab
{
    /// <summary>全部（未完成的，含重要；已完成在"已完成"页）</summary>
    All,

    /// <summary>今天（今天截止 + 已过期的未完成任务）</summary>
    Today,

    /// <summary>重要</summary>
    Important,

    /// <summary>已完成</summary>
    Completed
}

namespace DaenLauncher.Models;

/// <summary>
/// 剪贴板条目的类型（需求：支持文本、图片、文件等）。
/// 存储用小写英文标记与待办颜色一致的风格；C# 内部直接用枚举。
/// </summary>
public enum ClipboardEntryKind
{
    /// <summary>纯文本</summary>
    Text = 0,

    /// <summary>图片（PNG 落盘到 data\clipboard\images\，列表里显示缩略图预览）</summary>
    Image = 1,

    /// <summary>文件/文件夹（存储路径列表）</summary>
    Files = 2
}

/// <summary>
/// 一条剪贴板记录。
/// 记录（Items）受"最大保存数量"限制，超出删最早；归档（Archives）是持久化存储，
/// 不受数量限制（需求）。归档 = 从 Items 移到 Archives。
/// </summary>
public sealed class ClipboardEntry
{
    /// <summary>唯一 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>条目类型</summary>
    public ClipboardEntryKind Kind { get; set; } = ClipboardEntryKind.Text;

    /// <summary>文本内容（Kind = Text 时有效）</summary>
    public string Text { get; set; } = "";

    /// <summary>图片文件相对路径（Kind = Image 时有效，相对于 data\clipboard\，如 images\abc.png）</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>图片像素宽（预览显示用，0 = 未知）</summary>
    public int ImageWidth { get; set; }

    /// <summary>图片像素高（预览显示用，0 = 未知）</summary>
    public int ImageHeight { get; set; }

    /// <summary>图片 PNG 字节的 SHA1（用于去重判断，同一张图连续复制不重复记录）</summary>
    public string ImageHash { get; set; } = "";

    /// <summary>文件/文件夹路径列表（Kind = Files 时有效）</summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>记录时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 剪贴板数据根对象，序列化保存到 data\clipboard\clipboard.json。
/// 图片文件在 data\clipboard\images\ 下，删除条目时同步删除图片文件。
/// </summary>
public sealed class ClipboardData
{
    /// <summary>数据格式版本（将来结构调整用）</summary>
    public int Version { get; set; } = 1;

    /// <summary>复制记录（新记录插在最前，受最大保存数量限制）</summary>
    public List<ClipboardEntry> Items { get; set; } = new();

    /// <summary>归档记录（持久化存储，不受数量限制；列表顺序 = 归档时间倒序）</summary>
    public List<ClipboardEntry> Archives { get; set; } = new();
}

/// <summary>剪贴板窗口的分类栏（需求：记录、归档）</summary>
public enum ClipboardViewTab
{
    /// <summary>记录：所有复制记录（最新的在最上）</summary>
    Records,

    /// <summary>归档：从记录里归档出来的重要内容（持久化）</summary>
    Archives
}

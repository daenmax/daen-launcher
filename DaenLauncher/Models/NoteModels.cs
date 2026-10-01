using System.Text.Json.Serialization;

namespace DaenLauncher.Models;

/// <summary>
/// 一条随手记。本地存 data\note\notes.json；云同步时每条笔记对应 webnote 里的一个"标签"，
/// 条目标题固定为 DaenLauncher-Note-{32位Id}（需求），笔记自身的元数据（用户标题/底色/置顶/创建时间）
/// 序列化成 JSON 放在正文（正文第一行必须是条目标题，换行后才是数据——webnote 的格式约定）。
/// </summary>
public sealed class NoteItem
{
    /// <summary>唯一 ID（32 位：Guid 的 N 格式恰好是 32 个十六进制字符，需求）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>笔记标题（用户自己起的名字，列表里显示它）</summary>
    public string Title { get; set; } = "";

    /// <summary>笔记正文（纯文本，需求：不支持 markdown）</summary>
    public string Text { get; set; } = "";

    /// <summary>底色（TodoColors 标记；None = 无底色，需求：每条笔记可设置底色）</summary>
    public string Color { get; set; } = TodoColors.None;

    /// <summary>是否置顶（置顶的始终排在列表最上面，需求）</summary>
    public bool IsPinned { get; set; }

    /// <summary>创建时间（列表按它倒序排，需求：不按修改时间）</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>云端条目标题（DaenLauncher-Note-{Id}），序列化时不落盘</summary>
    [JsonIgnore]
    public string CloudEntryTitle => NoteData.CloudEntryPrefix + Id;
}

/// <summary>
/// 随手记数据根对象，序列化保存到 data\note\notes.json。
/// 云同步时每条 NoteItem 单独占一个条目（多标签），不走这个结构。
/// </summary>
public sealed class NoteData
{
    /// <summary>数据格式版本</summary>
    public int Version { get; set; } = 1;

    /// <summary>全部笔记（列表顺序不保证显示顺序，显示时按置顶+创建时间排序）</summary>
    public List<NoteItem> Notes { get; set; } = new();

    /// <summary>云端条目标题前缀（需求：DaenLauncher-Note-{32位笔记Id}）</summary>
    public const string CloudEntryPrefix = "DaenLauncher-Note-";
}

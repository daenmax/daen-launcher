namespace DaenLauncher.Models;

/// <summary>
/// 附属功能定义（启动器左下角的入口按钮）。
/// 以后新增附属功能时在这里加一行，并到设置-常规里默认不勾选即可。
/// </summary>
public sealed record AuxiliaryFeature(string Id, string NameKey, string IconResource);

/// <summary>附属功能注册表</summary>
public static class AuxiliaryFeatures
{
    /// <summary>待办功能 id</summary>
    public const string TodoId = "todo";

    /// <summary>随手记功能 id</summary>
    public const string NoteId = "note";

    /// <summary>剪贴板功能 id</summary>
    public const string ClipboardId = "clipboard";

    /// <summary>全部附属功能（数组顺序 = 默认显示顺序）</summary>
    public static readonly AuxiliaryFeature[] All =
    [
        new(TodoId, "Main.Todo", "DaenLauncher.Assets.Icons.待办_64.png"),
        new("note", "Main.Note", "DaenLauncher.Assets.Icons.随记_64.png"),
        new("clipboard", "Main.Clipboard", "DaenLauncher.Assets.Icons.剪贴板_64.png"),
    ];

    /// <summary>底栏最多直接显示的个数（其余通过"更多"菜单访问，需求）</summary>
    public const int MaxVisible = 3;
}

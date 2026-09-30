using System.Text.Json.Serialization;

namespace DaenLauncher.Models;

/// <summary>
/// 应用静态信息配置（来自嵌入的 appconfig.json）。
/// 需求第1条：软件名、描述、官网等文本不能硬编码在代码里，必须放在配置文件中。
/// </summary>
public sealed class AppConfig
{
    /// <summary>软件名称</summary>
    public string AppName { get; set; } = "Daen Launcher";

    /// <summary>软件描述</summary>
    public string AppDescription { get; set; } = "Daen的Windows快捷启动工具";

    /// <summary>官网地址</summary>
    public string WebsiteUrl { get; set; } = "https://github.com/daenmax/daen-launcher";

    /// <summary>开源地址</summary>
    public string OpenSourceUrl { get; set; } = "https://github.com/daenmax/daen-launcher";

    /// <summary>QQ群号</summary>
    public string QQGroup { get; set; } = "";

    /// <summary>QQ群加群链接</summary>
    public string QQGroupLink { get; set; } = "";
}

/// <summary>
/// 启动器项目类型。
/// 需求：Exe=可执行文件，Lnk=快捷方式，Folder=文件夹，File=文件，Url=网址，Uwp=UWP应用，Protocol=特殊协议。
/// </summary>
public enum LauncherItemType
{
    Exe,
    Lnk,
    Folder,
    File,
    Url,
    Uwp,
    Protocol
}

/// <summary>
/// 项目分类（左侧列表的一行）。
/// </summary>
public sealed class LauncherCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>分类名</summary>
    public string Name { get; set; } = "";

    /// <summary>图标：emoji 字符 或 图片文件名（system 目录 或 group_ 目录内）</summary>
    public string Icon { get; set; } = "";

    /// <summary>子分类列表</summary>
    public List<LauncherSubCategory> SubCategories { get; set; } = new();
}

/// <summary>
/// 项目子分类（右侧面板中的卡片或Tab）。
/// </summary>
public sealed class LauncherSubCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>子分类名</summary>
    public string Name { get; set; } = "";

    /// <summary>图标：emoji 字符 或 图片文件名</summary>
    public string Icon { get; set; } = "";

    /// <summary>子分类下的项目列表（顺序即展示顺序）</summary>
    public List<LauncherItem> Items { get; set; } = new();
}

/// <summary>
/// 项目（子分类里的一个可启动项）。
/// </summary>
public sealed class LauncherItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名称</summary>
    public string Name { get; set; } = "";

    /// <summary>完整路径（Url 类型时为 URL）</summary>
    public string Path { get; set; } = "";

    /// <summary>项目类型</summary>
    public LauncherItemType Type { get; set; } = LauncherItemType.Exe;

    /// <summary>备注</summary>
    public string Remark { get; set; } = "";

    /// <summary>命令行参数</summary>
    public string Arguments { get; set; } = "";

    /// <summary>项目图标缓存文件名（存于 data\icon\cache，可选）</summary>
    public string? IconFile { get; set; }

    /// <summary>路径失效标记（软件启动时检测，不序列化保存到 json，需求-优化1）</summary>
    [JsonIgnore]
    public bool IsMissing { get; set; }
}

/// <summary>
/// 启动器数据根对象，序列化保存到 data\launcher\launcher.json。
/// </summary>
public sealed class LauncherData
{
    public List<LauncherCategory> Categories { get; set; } = new();
}

/// <summary>开机自启方式（需求：可写注册表，也可用启动文件夹）</summary>
public enum AutoStartMode
{
    /// <summary>注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run</summary>
    Registry,

    /// <summary>启动文件夹 shell:startup 里放快捷方式</summary>
    StartupFolder
}

/// <summary>窗口材质</summary>
public enum BackdropKind
{
    /// <summary>亚克力</summary>
    Acrylic,

    /// <summary>云母</summary>
    Mica
}

/// <summary>应用主题</summary>
public enum ThemeMode
{
    /// <summary>跟随系统</summary>
    System,

    /// <summary>浅色</summary>
    Light,

    /// <summary>深色</summary>
    Dark
}

/// <summary>软件启动后行为</summary>
public enum StartBehavior
{
    /// <summary>显示窗口</summary>
    Show,

    /// <summary>隐藏到托盘</summary>
    Hide
}

/// <summary>窗口显示位置</summary>
public enum ShowPosition
{
    /// <summary>跟随鼠标位置</summary>
    FollowMouse,

    /// <summary>桌面中央</summary>
    Center,

    /// <summary>桌面左上角</summary>
    TopLeft,

    /// <summary>桌面右上角</summary>
    TopRight,

    /// <summary>桌面左下角</summary>
    BottomLeft,

    /// <summary>桌面右下角</summary>
    BottomRight,

    /// <summary>上次显示的位置（记录用户上次拖动/停留的位置）</summary>
    LastPosition
}

/// <summary>项目启动方式</summary>
public enum ItemActivateMode
{
    /// <summary>单击</summary>
    SingleClick,

    /// <summary>双击</summary>
    DoubleClick
}

/// <summary>项目启动后软件行为</summary>
public enum AfterLaunchBehavior
{
    /// <summary>隐藏软件</summary>
    Hide,

    /// <summary>显示软件（保持显示）</summary>
    Show
}

/// <summary>项目布局：平铺 或 列表</summary>
public enum ItemLayoutMode
{
    /// <summary>平铺（自动换行，跟随窗口大小）</summary>
    Grid,

    /// <summary>列表（一行一个）</summary>
    List
}

/// <summary>项目分类图标相对文字的位置</summary>
public enum CategoryIconPosition
{
    /// <summary>左边</summary>
    Left,

    /// <summary>右边</summary>
    Right
}

/// <summary>列表模式下项目的行内对齐位置</summary>
public enum ItemHorizontalAlignment
{
    /// <summary>居左</summary>
    Left,

    /// <summary>居中</summary>
    Center,

    /// <summary>居右</summary>
    Right
}

/// <summary>项目文字位置</summary>
public enum ItemTextPosition
{
    /// <summary>图标下边</summary>
    Below,

    /// <summary>图标左边</summary>
    Left,

    /// <summary>图标右边</summary>
    Right
}

/// <summary>侧键类型</summary>
public enum SideMouseButton
{
    /// <summary>后退键（XButton1）</summary>
    Back,

    /// <summary>前进键（XButton2）</summary>
    Forward
}

/// <summary>子分类展示风格</summary>
public enum SubCategoryStyle
{
    /// <summary>Tab 风格</summary>
    Tab,

    /// <summary>卡片风格</summary>
    Card
}

/// <summary>
/// 子分类 Tab 的视觉风格（仅 Tab 风格下有效，第二十轮优化6 新增）。
/// </summary>
public enum SubCategoryTabVisualStyle
{
    /// <summary>选择夹风格（系统 TabView 标签页，原来的样子）</summary>
    Classic,

    /// <summary>选中风格（按钮式标签，选中项高亮 + 底部蓝色横条；默认）</summary>
    Highlight
}

using DaenLauncher.Models;

namespace DaenLauncher.Models;

/// <summary>
/// 用户设置，序列化保存到 data\config\settings.json。
/// 所有默认值都在这里定义（需求禁止硬编码散落在代码里）。
/// </summary>
public sealed class AppSettings
{
    // ===== 常规 =====

    /// <summary>界面语言（语言文件名去扩展名，如 "简体中文"、"English"）</summary>
    public string Language { get; set; } = "";

    /// <summary>开机自动启动</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>开机自启方式</summary>
    public AutoStartMode AutoStartMode { get; set; } = AutoStartMode.Registry;

    /// <summary>软件启动后：显示 或 隐藏</summary>
    public StartBehavior StartBehavior { get; set; } = StartBehavior.Show;

    // ===== 外观 =====

    /// <summary>应用主题：跟随系统/浅色/深色</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>窗口材质：亚克力/云母</summary>
    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;

    /// <summary>自定义软件标题（留空使用默认）</summary>
    public string CustomTitle { get; set; } = "";

    // ===== 启动器：显示和隐藏 =====

    /// <summary>鼠标侧键单击 显示/隐藏</summary>
    public bool TriggerSideButton { get; set; } = false;

    /// <summary>使用哪个侧键</summary>
    public SideMouseButton SideButton { get; set; } = SideMouseButton.Back;

    /// <summary>鼠标中键单击 显示/隐藏（仅主窗口隐藏时）</summary>
    public bool TriggerMiddleButton { get; set; } = false;

    /// <summary>左键双击桌面（预留，后续版本完善）</summary>
    public bool TriggerDesktopDoubleClick { get; set; } = false;

    /// <summary>双击任务栏（预留，后续版本完善）</summary>
    public bool TriggerTaskbarDoubleClick { get; set; } = false;

    /// <summary>按 Ctrl 键两次 显示/隐藏</summary>
    public bool TriggerCtrlTwice { get; set; } = false;

    /// <summary>按 Alt 键两次 显示/隐藏</summary>
    public bool TriggerAltTwice { get; set; } = false;

    /// <summary>使用快捷键 显示/隐藏</summary>
    public bool TriggerHotkey { get; set; } = false;

    /// <summary>快捷键的修饰键（Win32 MOD_* 组合值）</summary>
    public int HotkeyModifiers { get; set; } = 0x0001; // MOD_ALT

    /// <summary>快捷键的虚拟键码（Win32 VK_*）</summary>
    public int HotkeyVirtualKey { get; set; } = 0x31;  // '1'

    /// <summary>快捷键显示文本，例如 "Alt+1"（仅用于界面回显）</summary>
    public string HotkeyText { get; set; } = "Alt+1";

    // ===== 启动器：行为 =====

    /// <summary>永远置顶</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>锁定尺寸（窗口不可调整大小，分隔条不可拖动）</summary>
    public bool LockSize { get; set; } = false;

    /// <summary>锁定图标（项目不能拖动调整顺序）</summary>
    public bool LockIcons { get; set; } = false;

    /// <summary>显示位置</summary>
    public ShowPosition ShowPosition { get; set; } = ShowPosition.FollowMouse;

    /// <summary>项目启动方式：单击/双击</summary>
    public ItemActivateMode ItemActivateMode { get; set; } = ItemActivateMode.SingleClick;

    /// <summary>项目启动后软件行为：隐藏/显示</summary>
    public AfterLaunchBehavior AfterLaunch { get; set; } = AfterLaunchBehavior.Hide;

    // ===== 启动器：显示尺寸 =====

    /// <summary>子分类风格：Tab/卡片</summary>
    public SubCategoryStyle SubCategoryStyle { get; set; } = SubCategoryStyle.Tab;

    /// <summary>项目分类图标大小（0 = 不显示图标）</summary>
    public double CategoryIconSize { get; set; } = 20;

    /// <summary>项目分类文字大小（0 = 不显示文字）</summary>
    public double CategoryTextSize { get; set; } = 14;

    /// <summary>子分类图标大小（0 = 不显示图标）</summary>
    public double SubCategoryIconSize { get; set; } = 18;

    /// <summary>子分类文字大小（0 = 不显示文字）</summary>
    public double SubCategoryTextSize { get; set; } = 14;

    /// <summary>项目布局：平铺/列表</summary>
    public ItemLayoutMode ItemLayout { get; set; } = ItemLayoutMode.Grid;

    /// <summary>项目图标大小（0 = 不显示图标）</summary>
    public double ItemIconSize { get; set; } = 40;

    /// <summary>项目文字大小（0 = 不显示文字）</summary>
    public double ItemTextSize { get; set; } = 12;

    /// <summary>项目文字最多显示行数（1-3 行，超出省略号）</summary>
    public int ItemTextMaxLines { get; set; } = 1;

    /// <summary>项目内容（图标+文字）的对齐位置（平铺格子和列表整行通用）。
    /// 注：原"列表视图位置"（ListItemAlignment）已并入本设置。</summary>
    public ItemHorizontalAlignment ItemContentAlignment { get; set; } = ItemHorizontalAlignment.Center;

    /// <summary>项目之间的横向间距（总像素，平铺和列表通用）</summary>
    public double ItemHorizontalSpacing { get; set; } = 8;

    /// <summary>项目之间的纵向间距（总像素，平铺和列表通用）</summary>
    public double ItemVerticalSpacing { get; set; } = 8;

    /// <summary>项目文字位置</summary>
    public ItemTextPosition ItemTextPosition { get; set; } = ItemTextPosition.Below;

    // ===== 窗口状态（自动保存） =====

    /// <summary>主窗口宽度</summary>
    public double WindowWidth { get; set; } = 920;

    /// <summary>主窗口高度</summary>
    public double WindowHeight { get; set; } = 640;

    /// <summary>左侧分类栏宽度</summary>
    public double LeftPaneWidth { get; set; } = 200;

    /// <summary>设置窗口宽度</summary>
    public double SettingsWindowWidth { get; set; } = 860;

    /// <summary>设置窗口高度</summary>
    public double SettingsWindowHeight { get; set; } = 640;

    /// <summary>项目分类：图标在文字的左边还是右边</summary>
    public CategoryIconPosition CategoryIconPosition { get; set; } = CategoryIconPosition.Left;

    /// <summary>项目分类：整体在左侧栏的对齐位置</summary>
    public ItemHorizontalAlignment CategoryAlignment { get; set; } = ItemHorizontalAlignment.Left;

    /// <summary>上次窗口位置 X（物理像素，-1 = 还没记录过）</summary>
    public int LastWindowX { get; set; } = -1;

    /// <summary>上次窗口位置 Y（物理像素，-1 = 还没记录过）</summary>
    public int LastWindowY { get; set; } = -1;
}

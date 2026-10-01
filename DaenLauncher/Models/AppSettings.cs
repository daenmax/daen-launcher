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

    /// <summary>
    /// 启动器左下角显示的附属功能（有序 id 列表，最多 3 个，需求）。
    /// 不在列表里的附属功能通过底栏"更多"菜单访问。
    /// </summary>
    public List<string> AuxiliaryVisible { get; set; } = new() { "todo", "note", "clipboard" };

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

    // ===== 待办 =====

    /// <summary>待办是否启用云同步（webnote 便签；false = 数据仅存本地 data\todo）。
    /// 注意：只有在设置页验证过便签名称和密码后才会置为 true。</summary>
    public bool TodoCloudSyncEnabled { get; set; } = false;

    /// <summary>云同步便签名称（note_name）</summary>
    public string TodoNoteName { get; set; } = "";

    /// <summary>云同步便签密码（note_pwd，明文存于本机 settings.json）</summary>
    public string TodoNotePwd { get; set; } = "";

    /// <summary>待办：使用快捷键 显示/隐藏待办窗口</summary>
    public bool TodoTriggerHotkey { get; set; } = false;

    /// <summary>待办快捷键的修饰键（Win32 MOD_* 组合值）</summary>
    public int TodoHotkeyModifiers { get; set; } = 0x0001; // MOD_ALT

    /// <summary>待办快捷键的虚拟键码（Win32 VK_*，默认 '2'）</summary>
    public int TodoHotkeyVirtualKey { get; set; } = 0x32;

    /// <summary>待办快捷键显示文本（仅界面回显，默认 Alt+2）</summary>
    public string TodoHotkeyText { get; set; } = "Alt+2";

    /// <summary>待办：永远置顶</summary>
    public bool TodoAlwaysOnTop { get; set; } = true;

    /// <summary>待办：锁定尺寸（窗口不可调整大小）</summary>
    public bool TodoLockSize { get; set; } = false;

    /// <summary>待办：重要任务在列表中的底色（TodoColors 标记；None = 不加底色）</summary>
    public string TodoImportantColor { get; set; } = TodoColors.Yellow;

    /// <summary>待办：窗口显示位置（上次位置视为桌面中央）</summary>
    public ShowPosition TodoShowPosition { get; set; } = ShowPosition.Center;

    /// <summary>子分类 Tab 的视觉风格（仅 Tab 风格下有效，默认选中风格）</summary>
    public SubCategoryTabVisualStyle SubCategoryTabVisual { get; set; } = SubCategoryTabVisualStyle.Highlight;

    // ===== 随手记 =====

    /// <summary>随手记是否启用云同步（webnote 便签；false = 数据仅存本地 data\note）。
    /// 注意：便签名称不能和待办的相同（设置页有查重）。</summary>
    public bool NoteCloudSyncEnabled { get; set; } = false;

    /// <summary>随手记云同步便签名称（不能和待办的相同，需求）</summary>
    public string NoteSyncName { get; set; } = "";

    /// <summary>随手记云同步便签密码（明文存于本机 settings.json）</summary>
    public string NoteSyncPwd { get; set; } = "";

    /// <summary>随手记：使用快捷键 显示/隐藏随手记窗口（默认 Alt+3）</summary>
    public bool NoteTriggerHotkey { get; set; } = false;

    /// <summary>随手记快捷键的修饰键（Win32 MOD_* 组合值）</summary>
    public int NoteHotkeyModifiers { get; set; } = 0x0001; // MOD_ALT

    /// <summary>随手记快捷键的虚拟键码（Win32 VK_*，默认 '3'）</summary>
    public int NoteHotkeyVirtualKey { get; set; } = 0x33;

    /// <summary>随手记快捷键显示文本（仅界面回显，默认 Alt+3）</summary>
    public string NoteHotkeyText { get; set; } = "Alt+3";

    /// <summary>随手记：永远置顶</summary>
    public bool NoteAlwaysOnTop { get; set; } = true;

    /// <summary>随手记：锁定尺寸（窗口不可调整大小，左右分隔条不可拖动）</summary>
    public bool NoteLockSize { get; set; } = false;

    /// <summary>随手记：窗口显示位置（上次位置用 NoteLastWindowX/Y）</summary>
    public ShowPosition NoteShowPosition { get; set; } = ShowPosition.Center;

    /// <summary>随手记窗口宽度（逻辑像素）</summary>
    public double NoteWindowWidth { get; set; } = 720;

    /// <summary>随手记窗口高度（逻辑像素）</summary>
    public double NoteWindowHeight { get; set; } = 560;

    /// <summary>随手记左侧笔记列表宽度（逻辑像素，分隔条拖动后记忆）</summary>
    public double NoteLeftPaneWidth { get; set; } = 230;

    /// <summary>随手记窗口上次位置 X（物理像素，-1 = 还没记录过）</summary>
    public int NoteLastWindowX { get; set; } = -1;

    /// <summary>随手记窗口上次位置 Y（物理像素，-1 = 还没记录过）</summary>
    public int NoteLastWindowY { get; set; } = -1;

    /// <summary>上次窗口位置 X（物理像素，-1 = 还没记录过）</summary>
    public int LastWindowX { get; set; } = -1;

    /// <summary>上次窗口位置 Y（物理像素，-1 = 还没记录过）</summary>
    public int LastWindowY { get; set; } = -1;

    /// <summary>待办窗口上次位置 X（物理像素，-1 = 还没记录过）</summary>
    public int TodoLastWindowX { get; set; } = -1;

    /// <summary>待办窗口上次位置 Y（物理像素，-1 = 还没记录过）</summary>
    public int TodoLastWindowY { get; set; } = -1;

    // ===== 剪贴板 =====

    /// <summary>是否启用剪贴板功能（启用后开始监听系统剪贴板并记录，需求）</summary>
    public bool ClipboardEnabled { get; set; } = false;

    /// <summary>最大保存记录数量（超出后删除最早记录，仅针对记录，不针对归档，需求）</summary>
    public int ClipboardMaxRecords { get; set; } = 60;

    /// <summary>剪贴板：使用快捷键 显示/隐藏窗口（默认 Alt+4）</summary>
    public bool ClipboardTriggerHotkey { get; set; } = false;

    /// <summary>剪贴板快捷键的修饰键（Win32 MOD_* 组合值）</summary>
    public int ClipboardHotkeyModifiers { get; set; } = 0x0001; // MOD_ALT

    /// <summary>剪贴板快捷键的虚拟键码（Win32 VK_*，默认 '4'）</summary>
    public int ClipboardHotkeyVirtualKey { get; set; } = 0x34;

    /// <summary>剪贴板快捷键显示文本（仅界面回显，默认 Alt+4）</summary>
    public string ClipboardHotkeyText { get; set; } = "Alt+4";

    /// <summary>剪贴板：锁定尺寸（窗口不可调整大小）</summary>
    public bool ClipboardLockSize { get; set; } = false;

    /// <summary>剪贴板：窗口显示位置（上次位置用 ClipboardLastWindowX/Y）</summary>
    public ShowPosition ClipboardShowPosition { get; set; } = ShowPosition.Center;

    /// <summary>剪贴板窗口宽度（逻辑像素）</summary>
    public double ClipboardWindowWidth { get; set; } = 480;

    /// <summary>剪贴板窗口高度（逻辑像素）</summary>
    public double ClipboardWindowHeight { get; set; } = 640;

    /// <summary>剪贴板窗口上次位置 X（物理像素，-1 = 还没记录过）</summary>
    public int ClipboardLastWindowX { get; set; } = -1;

    /// <summary>剪贴板窗口上次位置 Y（物理像素，-1 = 还没记录过）</summary>
    public int ClipboardLastWindowY { get; set; } = -1;
}

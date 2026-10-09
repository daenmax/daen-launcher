using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 启动器数据服务：管理分类/子分类/项目数据（data\launcher\launcher.json）。
/// 需求：没有数据时创建"默认分类"+"默认子分类"并选中。
/// </summary>
public sealed class LauncherDataService
{
    private static readonly Lazy<LauncherDataService> Lazy = new(() => new LauncherDataService());
    public static LauncherDataService Instance => Lazy.Value;

    private readonly JsonStore<LauncherData> _store;

    private LauncherDataService()
    {
        _store = new JsonStore<LauncherData>(Path.Combine(DataPathService.LauncherDir, "launcher.json"));
        Data = _store.Load();

        // 兼容旧数据修正（第五十四轮/第五十六轮）：有修改就立即落盘
        var changed = NormalizeLegacyUrlTypes();
        changed |= NormalizeLegacyShortcutPaths();
        if (changed)
        {
            Save();
        }

        // 首次启动：创建默认分类和默认子分类
        if (Data.Categories.Count == 0)
        {
            var sub = new LauncherSubCategory { Name = LocalizationService.Tr("Main.DefaultSubCategory") };
            var category = new LauncherCategory
            {
                Name = LocalizationService.Tr("Main.DefaultCategory"),
                SubCategories = { sub }
            };
            Data.Categories.Add(category);
            Save();
        }
    }

    public LauncherData Data { get; private set; }

    /// <summary>立即保存</summary>
    public void Save() => _store.Save(Data);

    /// <summary>重新加载（导入数据后调用）</summary>
    public void Reload()
    {
        _store.Reload();
        Data = _store.Load();

        var changed = NormalizeLegacyUrlTypes();
        changed |= NormalizeLegacyShortcutPaths();
        if (changed)
        {
            Save();
        }

        if (Data.Categories.Count == 0)
        {
            var sub = new LauncherSubCategory { Name = LocalizationService.Tr("Main.DefaultSubCategory") };
            var category = new LauncherCategory
            {
                Name = LocalizationService.Tr("Main.DefaultCategory"),
                SubCategories = { sub }
            };
            Data.Categories.Add(category);
            Save();
        }
    }

    /// <summary>
    /// 旧数据修正：第五十四轮之前从 .url 文件（Steam 游戏桌面快捷方式）拖入的项目
    /// 一律被标为"网址"类型，但 steam:// 这类地址抓不到 favicon、图标永远是占位——
    /// 地址不是 http/https 的统一修正为"协议"类型（启动方式相同，图标走协议逻辑）。
    /// 返回是否有修改。
    /// </summary>
    private bool NormalizeLegacyUrlTypes()
    {
        var changed = false;
        foreach (var category in Data.Categories)
        {
            foreach (var sub in category.SubCategories)
            {
                foreach (var item in sub.Items)
                {
                    if (item.Type == LauncherItemType.Url &&
                        !item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Type = LauncherItemType.Protocol;
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// 旧数据修正（第五十六轮）：以前拖入 .bat/.cmd/.ps1 等非 exe 目标的快捷方式时，
    /// 项目存的是快捷方式自己的路径（如 "xx.bat - 快捷方式.lnk"）而不是真实目标，
    /// 导致复制路径/创建桌面快捷方式/打开所在位置都指向 .lnk。
    /// 这里把这类项目重新解析成真实目标；解析不出来就保持原样（仍可点击启动）。
    /// 返回是否有修改。
    /// </summary>
    private bool NormalizeLegacyShortcutPaths()
    {
        var changed = false;
        foreach (var category in Data.Categories)
        {
            foreach (var sub in category.SubCategories)
            {
                foreach (var item in sub.Items)
                {
                    if (item.Type != LauncherItemType.Lnk) continue;
                    if (string.IsNullOrWhiteSpace(item.Path)) continue;

                    // 仅处理"指向真实存在的 .lnk/.url 文件"的项目
                    var extension = Path.GetExtension(item.Path).ToLowerInvariant();
                    if (extension is not (".lnk" or ".url")) continue;
                    if (!File.Exists(item.Path)) continue;

                    var resolved = DropResolver.ResolveShortcutFile(item.Path);
                    if (resolved == null) continue;

                    // 解析结果仍指向快捷方式本身则不动（避免无意义的替换）
                    if (string.Equals(resolved.Path, item.Path, StringComparison.OrdinalIgnoreCase)) continue;

                    item.Path = resolved.Path;
                    item.Type = resolved.Type;
                    item.Arguments = resolved.Arguments;
                    item.IconFile = resolved.IconFile;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>导出数据的目录列表（数据页勾选项）</summary>
    public static string[] DataFolderNames => new[] { "config", "language", "icon", "launcher", "todo", "note", "clipboard" };

    /// <summary>
    /// 判断单个项目的路径是否已失效（Url/协议/UWP 没有本地路径可查，视为有效）。
    /// </summary>
    public static bool IsItemMissing(LauncherItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path)) return item.Type is LauncherItemType.Exe or LauncherItemType.Lnk or LauncherItemType.File or LauncherItemType.Folder;
        return item.Type switch
        {
            LauncherItemType.Exe or LauncherItemType.Lnk or LauncherItemType.File => !File.Exists(item.Path),
            LauncherItemType.Folder => !Directory.Exists(item.Path),
            _ => false
        };
    }

    /// <summary>
    /// 检查所有项目的路径是否还有效，失效的打上 IsMissing 标记
    /// （仅在软件启动时执行一次，需求-优化1；界面据此显示"项目无法找到"图标）。
    /// </summary>
    public void CheckMissingItems()
    {
        foreach (var category in Data.Categories)
        {
            foreach (var sub in category.SubCategories)
            {
                foreach (var item in sub.Items)
                {
                    item.IsMissing = IsItemMissing(item);
                }
            }
        }
    }

    /// <summary>
    /// 重新检查某个子分类下所有项目的有效性（需求：右键子分类空白处"刷新项目"）。
    /// 返回 (检查总数, 失效数量)，供界面提示用。
    /// </summary>
    public static (int Total, int Missing) RecheckMissing(LauncherSubCategory sub)
    {
        var missing = 0;
        foreach (var item in sub.Items)
        {
            item.IsMissing = IsItemMissing(item);
            if (item.IsMissing) missing++;
        }
        return (sub.Items.Count, missing);
    }
}

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
}

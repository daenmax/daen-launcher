using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 设置服务：管理 AppSettings 的加载与保存（data\config\settings.json）。
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> Lazy = new(() => new SettingsService());
    public static SettingsService Instance => Lazy.Value;

    private readonly JsonStore<AppSettings> _store;

    private SettingsService()
    {
        _store = new JsonStore<AppSettings>(Path.Combine(DataPathService.ConfigDir, "settings.json"));
        Settings = _store.Load();
    }

    /// <summary>当前设置（内存单例，修改后调用 Save 立即写盘）</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>立即保存设置到磁盘</summary>
    public void Save()
    {
        _store.Save(Settings);
    }

    /// <summary>重新从磁盘加载（导入数据后调用）</summary>
    public void Reload()
    {
        _store.Reload();
        Settings = _store.Load();
    }
}

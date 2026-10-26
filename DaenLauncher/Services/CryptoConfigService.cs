using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 加解密配置服务（需求：SM4/AES 增加"配置"功能）。
/// - 存储：data\tools\crypto-configs.json（复用 JsonStore，写入即落盘）；
/// - 配置分 SM4 / AES 两组，界面下拉框只显示当前算法的配置；
/// - 管理窗口删除配置后触发 <see cref="Changed"/>，已打开的工具页会重建下拉框。
/// </summary>
public sealed class CryptoConfigService
{
    /// <summary>全局单例</summary>
    public static CryptoConfigService Instance { get; } = new();

    private readonly JsonStore<CryptoConfigData> _store =
        new(Path.Combine(DataPathService.ToolsDir, "crypto-configs.json"));

    private CryptoConfigData _data = new();

    /// <summary>全部配置（管理窗口展示用）</summary>
    public CryptoConfigData Data => _data;

    /// <summary>配置列表发生变化（新增/删除/导入）后触发</summary>
    public static event Action? Changed;

    private CryptoConfigService()
    {
    }

    /// <summary>应用启动时加载（数据页导入/删除后调用 LoadLocal）</summary>
    public void Initialize()
    {
        _store.Reload();
        _data = _store.Load();
    }

    /// <summary>从磁盘重新加载（数据页删除/导入 tools 数据后调用）</summary>
    public void LoadLocal()
    {
        _store.Reload();
        _data = _store.Load();
        Changed?.Invoke();
    }

    /// <summary>取某个算法下的配置（按保存顺序）</summary>
    public List<CryptoConfig> GetConfigs(string algo) =>
        _data.Configs.Where(c => string.Equals(c.Algo, algo, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>同一算法下是否已有同名配置（名称不区分大小写）</summary>
    public bool NameExists(string algo, string name, string? exceptId = null) =>
        _data.Configs.Any(c => c.Algo == algo &&
                               c.Id != exceptId &&
                               string.Equals(c.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>新增一套配置（调用方负责校验名称非空/不重名）</summary>
    public void Add(CryptoConfig config)
    {
        _data.Configs.Add(config);
        Persist();
    }

    /// <summary>按 Id 删除配置</summary>
    public void Delete(string id)
    {
        if (_data.Configs.RemoveAll(c => c.Id == id) > 0)
        {
            Persist();
        }
    }

    private void Persist()
    {
        _store.Save(_data);
        Changed?.Invoke();
    }
}

using System.Text;
using System.Text.Json;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 待办服务：本地存储（data\todo\todo.json）与 webnote 云同步是**两套完全独立的数据**：
/// - 不启用云同步：读/写本地文件，改动立即落盘；
/// - 启用云同步：数据只在内存（来自云端），改动只推送云端，**不写本地文件**；
/// - 随时切换互不影响（第二十四轮：本地/云端彻底分离）。
/// 数据结构用 TodoData；云同步时序列化整个 TodoData 作为便签里
/// title="DaenLauncher-Todo" 那一条的正文（第一行必须是标题，换行后才是数据）。
///
/// note_token 规则（需求）：
/// - 每次打开窗口/点刷新，先调【获取】接口，note_id 和 note_token 缓存到内存；
/// - 每个 note_token 只能用（保存）一次，保存成功后响应返回新 token，更新缓存；
/// - 每次新增/删除/修改前也先调【获取】对比 token：不一致说明其他客户端改过，
///   用最新的 token 去保存，并把返回的新 token 更新进缓存；
/// - 【获取】失败时所有写操作（新增/修改/删除/清除）都不可用。
/// </summary>
public sealed class TodoService
{
    /// <summary>全局单例</summary>
    public static TodoService Instance { get; } = new();

    /// <summary>便签里本应用数据条目的标题标记（正文的第一个换行前必须是它）</summary>
    public const string CloudEntryTitle = "DaenLauncher-Todo";

    private readonly JsonStore<TodoData> _store =
        new(Path.Combine(DataPathService.TodoDir, "todo.json"));

    /// <summary>本地数据（不启用云同步时使用，对应 todo.json）</summary>
    private TodoData _localData = new();

    /// <summary>云端数据（启用云同步时使用，仅内存，不落盘）</summary>
    private TodoData _cloudData = new();

    /// <summary>当前生效的数据：启用云同步 = 云端数据，否则 = 本地数据。
    /// 注意两套数据完全独立，切模式时由 OnSyncSettingsChanged 触发重新加载。</summary>
    public TodoData Data => IsCloudSyncEnabled ? _cloudData : _localData;

    /// <summary>云同步是否已启用且本次会话验证通过（获取成功过至少一次）</summary>
    public bool CloudAvailable { get; private set; }

    /// <summary>是否启用了云同步（设置项）</summary>
    public static bool IsCloudSyncEnabled => SettingsService.Instance.Settings.TodoCloudSyncEnabled;

    // ===== 云端凭据缓存（仅内存，不落盘；落盘的在 settings.json）=====
    private string? _noteId;
    private string? _noteToken;

    /// <summary>最近一次云端返回的 note_token（调试/状态展示用）</summary>
    public string? CachedNoteToken => _noteToken;

    private TodoService()
    {
    }

    /// <summary>
    /// 从磁盘重新加载本地数据（应用启动时、切回本地模式、删除/导入 todo 数据后调用）。
    /// 先清 JsonStore 内存缓存再读，保证拿到的是磁盘上的最新内容。
    /// </summary>
    public void LoadLocal()
    {
        _store.Reload();
        _localData = _store.Load();
    }

    #region 云同步

    /// <summary>
    /// 从云端拉取数据（打开窗口/点刷新按钮时调用）：
    /// 调【获取】接口 -> 缓存 note_id/note_token -> 便签里找 DaenLauncher-Todo 条目
    /// -> 有则用云端数据替换本地；没有则用本地数据调【保存】创建条目。
    /// 失败时 CloudAvailable=false，写操作全部禁用。
    /// </summary>
    public async Task<(bool Success, string? ErrorKey, string? RawError)> SyncFromCloudAsync()
    {
        var settings = SettingsService.Instance.Settings;
        CloudAvailable = false;

        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.TodoNoteName, settings.TodoNotePwd);
        if (!fetch.Success || fetch.NoteId == null || fetch.NoteToken == null)
        {
            return (false, fetch.ErrorKey, fetch.RawError);
        }

        // 缓存最新的 note_id / note_token（需求）
        _noteId = fetch.NoteId;
        _noteToken = fetch.NoteToken;

        var remoteData = ParseCloudContent(fetch.NoteContent);
        if (remoteData != null)
        {
            // 云端已有本应用的数据条目：以云端为准
            _cloudData = remoteData;
        }
        else
        {
            // 云端还没有 DaenLauncher-Todo 条目：先用空数据。
            // 本地（云端内存）无数据时不调用保存接口——提交内容会被服务端拒绝
            // （随手记实测：空数组/无意义内容会报"数据格式错误"，用户定位），等有数据再创建
            _cloudData = new TodoData();
        }

        CloudAvailable = true;
        return (true, null, null);
    }

    /// <summary>
    /// 把当前本地数据推送到云端（写操作后调用）。
    /// 按需求先调【获取】对比 token（不一致 = 其他客户端改过，用最新 token），
    /// 再调【保存】，保存返回的新 token 更新进缓存。
    /// </summary>
    public async Task<(bool Success, string? ErrorKey, string? RawError)> PushToCloudAsync()
    {
        var settings = SettingsService.Instance.Settings;

        // 写操作前先获取一次：拿最新 note_id/note_token 并和缓存对比
        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.TodoNoteName, settings.TodoNotePwd);
        if (!fetch.Success || fetch.NoteId == null || fetch.NoteToken == null)
        {
            CloudAvailable = false;
            return (false, fetch.ErrorKey, fetch.RawError);
        }

        if (fetch.NoteToken != _noteToken)
        {
            // token 不一致：其他客户端修改过便签，改用最新 token（需求）
            // （保存会整体覆盖 DaenLauncher-Todo 条目，此处以本地数据为准）
            _noteToken = fetch.NoteToken;
        }
        _noteId = fetch.NoteId;

        var save = await SaveWithRetryAsync();
        if (!save.Success)
        {
            return (false, save.ErrorKey, save.RawError);
        }
        CloudAvailable = true;
        return (true, null, null);
    }

    /// <summary>
    /// 保存（带临时错误重试）：保存失败且属临时性错误（服务端波动，如"服务器负载过高"、
    /// "数据格式错误"）时，等待 1.5 秒后重新获取 token 再试一次；名称/密码错误重试无意义。
    /// </summary>
    private async Task<(bool Success, string? ErrorKey, string? RawError)> SaveWithRetryAsync()
    {
        var save = await SaveToCloudCoreAsync();
        if (save.Success || save.ErrorKey != "WebNote.NetworkError")
        {
            return save;
        }

        await Task.Delay(1500);
        var settings = SettingsService.Instance.Settings;
        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.TodoNoteName, settings.TodoNotePwd);
        if (fetch.Success && fetch.NoteId != null && fetch.NoteToken != null)
        {
            _noteId = fetch.NoteId;
            _noteToken = fetch.NoteToken;
            save = await SaveToCloudCoreAsync();
        }
        return save;
    }

    /// <summary>用缓存的 note_id/note_token 调【保存】接口，成功后更新 token 缓存</summary>
    private async Task<(bool Success, string? ErrorKey, string? RawError)> SaveToCloudCoreAsync()
    {
        if (_noteId == null || _noteToken == null)
        {
            return (false, "WebNote.NotVerified", null);
        }

        var settings = SettingsService.Instance.Settings;
        var save = await WebNoteClient.SaveNoteAsync(
            settings.TodoNoteName, settings.TodoNotePwd,
            _noteId, _noteToken, BuildCloudContent());

        if (save.Success)
        {
            // 每个 token 只能用一次：保存成功后立即换用返回的新 token
            if (!string.IsNullOrEmpty(save.NewNoteToken))
            {
                _noteToken = save.NewNoteToken;
            }
            return (true, null, null);
        }
        return (false, save.ErrorKey, save.RawError);
    }

    /// <summary>
    /// 构建云端 note_content：JSON 数组，只放一个成员（我们的数据），
    /// content 第一行是标题 DaenLauncher-Todo，换行后是 TodoData 的 JSON（需求格式）。
    /// </summary>
    private static string BuildCloudContent()
    {
        var content = CloudEntryTitle + "\n" +
                      JsonSerializer.Serialize(Instance.Data);
        var entry = new[] { new { title = CloudEntryTitle, content } };
        return JsonSerializer.Serialize(entry);
    }

    /// <summary>解析云端 note_content，找出 DaenLauncher-Todo 条目并反序列化；没有则返回 null</summary>
    private static TodoData? ParseCloudContent(string? noteContent)
    {
        if (string.IsNullOrWhiteSpace(noteContent))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(noteContent);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var title = element.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (!string.Equals(title, CloudEntryTitle, StringComparison.Ordinal))
                {
                    continue;
                }
                var content = element.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(content))
                {
                    return null;
                }
                // content 第一行是标题，换行后的部分才是数据 JSON（需求）
                var newlineIndex = content.IndexOf('\n');
                if (newlineIndex < 0)
                {
                    return null;
                }
                var payload = content[(newlineIndex + 1)..].Trim();
                var data = JsonSerializer.Deserialize<TodoData>(payload);
                if (data != null)
                {
                    // 归一化颜色标记（云端数据可能来自旧版本或被手改）
                    foreach (var item in data.Items)
                    {
                        item.Color = TodoColors.Normalize(item.Color);
                    }
                    return data;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 本地读写

    /// <summary>
    /// 写操作后的落盘：本地模式立即写 todo.json；
    /// 云同步模式不写任何本地文件（数据只推云端，需求：本地/云端两套数据互不影响）。
    /// </summary>
    public void Persist()
    {
        if (!IsCloudSyncEnabled)
        {
            _store.Save(_localData);
        }
    }

    #endregion
}

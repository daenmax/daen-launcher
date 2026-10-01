using System.Text;
using System.Text.Json;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 随手记服务：本地存储（data\note\notes.json）与 webnote 云同步是**两套完全独立的数据**
/// （与待办同款架构，可随时切换互不影响）。
///
/// 云同步特点（需求）：多标签——每条笔记对应 webnote 里的一个条目（标签），
/// 条目标题 = DaenLauncher-Note-{32位笔记Id}，正文 = 第一行标题 + 换行 + 笔记元数据 JSON。
/// 保存接口是**整体覆盖**，所以每次保存必须提交**全部笔记**；
/// 非 DaenLauncher-Note 开头的条目（便签里其他应用/手动创建的内容）在保存时原样保留。
///
/// note_token 规则与待办一致：打开窗口/点刷新先【获取】缓存 note_id/note_token（仅内存）；
/// 每次写操作前再【获取】对比 token（不一致 = 其他客户端改过，用最新 token）；
/// 保存成功后用响应返回的新 token 更新缓存；【获取】失败则所有写操作禁用。
/// </summary>
public sealed class NoteService
{
    /// <summary>全局单例</summary>
    public static NoteService Instance { get; } = new();

    private readonly JsonStore<NoteData> _store =
        new(Path.Combine(DataPathService.NoteDir, "notes.json"));

    private static readonly JsonSerializerOptions EntryJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>本地数据（不启用云同步时使用，对应 notes.json）</summary>
    private NoteData _localData = new();

    /// <summary>云端数据（启用云同步时使用，仅内存，不落盘）</summary>
    private NoteData _cloudData = new();

    /// <summary>当前生效的数据：启用云同步 = 云端数据，否则 = 本地数据</summary>
    public NoteData Data => IsCloudSyncEnabled ? _cloudData : _localData;

    /// <summary>云同步是否已启用且本次会话验证通过（获取成功过至少一次）</summary>
    public bool CloudAvailable { get; private set; }

    /// <summary>是否启用了云同步（设置项）</summary>
    public static bool IsCloudSyncEnabled => SettingsService.Instance.Settings.NoteCloudSyncEnabled;

    // ===== 云端凭据缓存（仅内存）=====
    private string? _noteId;
    private string? _noteToken;

    /// <summary>便签里非本应用的条目（保存时原样保留，避免覆盖删除其他内容）</summary>
    private readonly List<(string Title, string Content)> _foreignEntries = new();

    private NoteService()
    {
    }

    /// <summary>从磁盘重新加载本地数据（应用启动时、切回本地模式、删除/导入 note 数据后调用）</summary>
    public void LoadLocal()
    {
        _store.Reload();
        _localData = _store.Load();
    }

    /// <summary>
    /// 写操作后的落盘：本地模式立即写 notes.json；云同步模式不写任何本地文件。
    /// </summary>
    public void Persist()
    {
        if (!IsCloudSyncEnabled)
        {
            _store.Save(_localData);
        }
    }

    #region 云同步

    /// <summary>
    /// 从云端拉取（打开窗口/点"云同步"按钮时调用）：
    /// 【获取】→ 缓存 note_id/note_token → 解析所有 DaenLauncher-Note-* 条目为本应用笔记，
    /// 其余条目记入 _foreignEntries（保存时原样带回）。云端一条我们的笔记都没有时直接创建。
    /// </summary>
    public async Task<(bool Success, string? ErrorKey, string? RawError)> SyncFromCloudAsync()
    {
        var settings = SettingsService.Instance.Settings;
        CloudAvailable = false;

        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.NoteSyncName, settings.NoteSyncPwd);
        if (!fetch.Success || fetch.NoteId == null || fetch.NoteToken == null)
        {
            return (false, fetch.ErrorKey, fetch.RawError);
        }

        _noteId = fetch.NoteId;
        _noteToken = fetch.NoteToken;

        var (notes, foreign) = ParseCloudContent(fetch.NoteContent);
        _cloudData = new NoteData { Notes = notes };
        _foreignEntries.Clear();
        _foreignEntries.AddRange(foreign);

        // 注意：云端一条我们的笔记都没有时不调用保存接口——
        // 本地无数据时提交空数组 "[]" 会被服务端拒绝（"数据格式错误"，用户实测定位）
        CloudAvailable = true;
        return (true, null, null);
    }

    /// <summary>
    /// 把当前全部笔记推送到云端（点"保存"按钮/窗口关闭时调用）。
    /// 先【获取】对比 token（不一致 = 其他客户端改过，用最新 token），再【保存】全部笔记。
    /// </summary>
    public async Task<(bool Success, string? ErrorKey, string? RawError)> PushToCloudAsync()
    {
        var settings = SettingsService.Instance.Settings;

        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.NoteSyncName, settings.NoteSyncPwd);
        if (!fetch.Success || fetch.NoteId == null || fetch.NoteToken == null)
        {
            CloudAvailable = false;
            return (false, fetch.ErrorKey, fetch.RawError);
        }

        if (fetch.NoteToken != _noteToken)
        {
            // token 不一致：其他客户端修改过便签，改用最新 token（需求，与待办同款）
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
        var fetch = await WebNoteClient.FetchOrCreateAsync(settings.NoteSyncName, settings.NoteSyncPwd);
        if (fetch.Success && fetch.NoteId != null && fetch.NoteToken != null)
        {
            _noteId = fetch.NoteId;
            _noteToken = fetch.NoteToken;
            save = await SaveToCloudCoreAsync();
        }
        return save;
    }

    /// <summary>用缓存的 note_id/note_token 调【保存】接口：提交全部笔记 + 保留的他人条目</summary>
    private async Task<(bool Success, string? ErrorKey, string? RawError)> SaveToCloudCoreAsync()
    {
        if (_noteId == null || _noteToken == null)
        {
            return (false, "WebNote.NotVerified", null);
        }

        var settings = SettingsService.Instance.Settings;
        var save = await WebNoteClient.SaveNoteAsync(
            settings.NoteSyncName, settings.NoteSyncPwd,
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
    /// 构建云端 note_content：我们全部笔记（每条一个条目）+ 保留的他人条目。
    /// 保存是整体覆盖，漏提交一条我们的笔记就等于删掉它（需求强调）。
    /// </summary>
    private string BuildCloudContent()
    {
        var entries = new List<object>();
        foreach (var note in _cloudData.Notes)
        {
            var title = note.CloudEntryTitle;
            // 正文第一行必须是条目标题（webnote 格式约定），换行后是笔记数据
            var payload = JsonSerializer.Serialize(note, EntryJsonOptions);
            entries.Add(new { title, content = title + "\n" + payload });
        }
        foreach (var (title, content) in _foreignEntries)
        {
            entries.Add(new { title, content });
        }

        // 全部笔记都被删除且便签里也没有其他内容时，提交空数组 "[]" 会被服务端拒绝
        // （"数据格式错误"），用创建时的占位条目代替——云端效果等价于"没有我们的笔记"
        if (entries.Count == 0)
        {
            return WebNoteClient.DefaultNoteContent;
        }
        return JsonSerializer.Serialize(entries, EntryJsonOptions);
    }

    /// <summary>
    /// 解析云端 note_content：DaenLauncher-Note-* 条目 → 笔记列表；
    /// 其余 → 他人条目（保存时原样保留）。解析失败的条目按他人条目保留（不丢数据）。
    /// </summary>
    private static (List<NoteItem> Notes, List<(string Title, string Content)> Foreign) ParseCloudContent(
        string? noteContent)
    {
        var notes = new List<NoteItem>();
        var foreign = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(noteContent))
        {
            return (notes, foreign);
        }
        try
        {
            using var doc = JsonDocument.Parse(noteContent);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var title = element.TryGetProperty("title", out var t) ? t.GetString() : null;
                var content = element.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
                {
                    continue;
                }

                if (!title.StartsWith(NoteData.CloudEntryPrefix, StringComparison.Ordinal))
                {
                    // 他人条目：原样保留
                    foreign.Add((title, content));
                    continue;
                }

                var id = title[NoteData.CloudEntryPrefix.Length..];
                var newlineIndex = content.IndexOf('\n');
                if (id.Length != 32 || newlineIndex < 0)
                {
                    // 结构异常：当作他人条目保留，避免覆盖时丢掉
                    foreign.Add((title, content));
                    continue;
                }

                try
                {
                    var payload = content[(newlineIndex + 1)..].Trim();
                    var note = JsonSerializer.Deserialize<NoteItem>(payload, EntryJsonOptions);
                    if (note != null)
                    {
                        note.Id = id; // 以条目标题里的 Id 为准
                        note.Color = TodoColors.Normalize(note.Color);
                        notes.Add(note);
                        continue;
                    }
                }
                catch
                {
                    // 反序列化失败按他人条目保留
                }
                foreign.Add((title, content));
            }
        }
        catch
        {
            // 整体解析失败：视为空便签
        }
        return (notes, foreign);
    }

    #endregion
}

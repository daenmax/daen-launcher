using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DaenLauncher.Services;

/// <summary>
/// webnote（webnote.cc）云便签 API 客户端，负责待办云同步的底层网络请求。
/// 接口说明（抓包自 webnote.cc）：
/// - 获取便签信息和内容：POST multipart/form-data（note_name + note_pwd）
/// - 保存便签内容：POST application/x-www-form-urlencoded（note_name/note_id/note_content/note_token/expire_time/note_pwd）
/// 鉴权方式：Referer 头带上 "https://webnote.cc/{名称}@{密码}" 模拟网页端。
/// </summary>
public static class WebNoteClient
{
    /// <summary>便签站点首页（教程按钮打开用）</summary>
    public const string SiteUrl = "https://webnote.cc/";

    /// <summary>API 基地址</summary>
    private const string ApiBase = "https://api-webnote.txttool.cn/netcut/note/";

    /// <summary>便签有效期（秒，3 年，与网页端一致）</summary>
    public const int ExpireTimeSeconds = 94608000;

    /// <summary>
    /// 自动创建便签时提交的占位内容（需求规定：便签不存在时调保存接口创建，
    /// 内容为一个标题是"新便签"的空条目）。
    /// </summary>
    public const string DefaultNoteContent = "[{\"title\":\"新便签\",\"content\":\"\"}]";

    /// <summary>接口状态码：成功</summary>
    public const int StatusOk = 1;
    /// <summary>接口状态码：便签不存在</summary>
    public const int StatusNoteNotExist = 2;
    /// <summary>接口状态码：密码为空</summary>
    public const int StatusPwdEmpty = 3;
    /// <summary>接口状态码：密码错误</summary>
    public const int StatusPwdWrong = 4;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36";

    /// <summary>HttpClient（统一走代理配置，需求；ProxyService 共享连接池，配置变化立即生效）</summary>
    private static HttpClient Http => ProxyService.CreateHttpClient(TimeSpan.FromSeconds(15));

    /// <summary>获取便签信息的结果</summary>
    public sealed class FetchResult
    {
        public bool Success { get; init; }
        /// <summary>失败原因对应的本地化 key（WebNote.*）或 null</summary>
        public string? ErrorKey { get; init; }
        /// <summary>服务端原始错误信息（拼在提示后面方便排查）</summary>
        public string? RawError { get; init; }
        public string? NoteId { get; init; }
        public string? NoteToken { get; init; }
        /// <summary>便签原始内容（note_content 字段，未解析）</summary>
        public string? NoteContent { get; init; }
    }

    /// <summary>保存便签的结果</summary>
    public sealed class SaveResult
    {
        public bool Success { get; init; }
        public string? ErrorKey { get; init; }
        public string? RawError { get; init; }
        /// <summary>保存成功后返回的新 note_token（每个 token 只能用一次，必须更新缓存）</summary>
        public string? NewNoteToken { get; init; }
    }

    /// <summary>构建公共请求头（Origin/Referer/UA/Accept）</summary>
    private static void SetCommonHeaders(HttpRequestMessage request, string noteName, string notePwd)
    {
        request.Headers.Add("Origin", "https://webnote.cc");
        // 网页端用 Referer 携带账号信息：https://webnote.cc/{名称}@{密码}
        request.Headers.TryAddWithoutValidation("Referer",
            $"https://webnote.cc/{Uri.EscapeDataString(noteName)}@{Uri.EscapeDataString(notePwd)}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Add("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    /// <summary>
    /// 获取便签信息，便签不存在时自动创建（需求-优化1）：
    /// 【获取】报"剪贴板不存在"（status=2）时，调一次【保存】接口
    /// （note_id/note_token 传空，内容为占位条目）创建便签，然后重新获取。
    /// 待办和随手记的云同步都走这个方法，用户不再需要提前去网页创建便签。
    /// 服务端不稳定（"服务器负载过高"/"数据格式错误"等临时错误）时自动重试一次。
    /// </summary>
    public static async Task<FetchResult> FetchOrCreateAsync(string noteName, string notePwd)
    {
        var fetch = await FetchInfoAsync(noteName, notePwd);
        if (!fetch.Success && fetch.ErrorKey == "WebNote.NameNotExist")
        {
            var create = await SaveNoteAsync(noteName, notePwd, "", "", DefaultNoteContent);
            if (create.Success)
            {
                // 创建成功：重新获取一次拿 note_id/note_token 和内容
                fetch = await FetchInfoAsync(noteName, notePwd);
            }
            else
            {
                return new FetchResult
                {
                    Success = false,
                    ErrorKey = create.ErrorKey,
                    RawError = create.RawError
                };
            }
        }

        // 临时性失败（服务端波动，如负载过高/数据格式错误）：短暂等待后重试一次
        if (!fetch.Success && fetch.ErrorKey == "WebNote.NetworkError")
        {
            await Task.Delay(1500);
            fetch = await FetchInfoAsync(noteName, notePwd);
        }
        return fetch;
    }

    /// <summary>
    /// 获取便签信息和内容（【获取标签列表和内容】接口）。
    /// 成功返回 note_id / note_token / note_content；名称不存在或密码错误时返回对应错误。
    /// </summary>
    public static async Task<FetchResult> FetchInfoAsync(string noteName, string notePwd)
    {
        try
        {
            // 按网页端抓包用 multipart/form-data（curl --form）
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(noteName), "note_name");
            form.Add(new StringContent(notePwd), "note_pwd");

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "info/")
            {
                Content = form
            };
            SetCommonHeaders(request, noteName, notePwd);

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return ParseFetchResponse(body);
        }
        catch (Exception)
        {
            // 网络异常（无网/超时/DNS 失败等）
            return new FetchResult { Success = false, ErrorKey = "WebNote.NetworkError" };
        }
    }

    /// <summary>解析获取接口的响应 JSON</summary>
    private static FetchResult ParseFetchResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
            if (status == StatusOk)
            {
                var data = root.GetProperty("data");
                return new FetchResult
                {
                    Success = true,
                    NoteId = data.TryGetProperty("note_id", out var id) ? id.GetString() : null,
                    NoteToken = data.TryGetProperty("note_token", out var token) ? token.GetString() : null,
                    NoteContent = data.TryGetProperty("note_content", out var content) ? content.GetString() : null
                };
            }

            return new FetchResult
            {
                Success = false,
                ErrorKey = ErrorKeyForStatus(status),
                RawError = root.TryGetProperty("error", out var err) ? err.GetString() : null
            };
        }
        catch
        {
            return new FetchResult { Success = false, ErrorKey = "WebNote.NetworkError", RawError = body };
        }
    }

    /// <summary>状态码 -> 本地化错误 key</summary>
    private static string ErrorKeyForStatus(int status) => status switch
    {
        StatusNoteNotExist => "WebNote.NameNotExist",
        StatusPwdEmpty or StatusPwdWrong => "WebNote.PwdError",
        _ => "WebNote.NetworkError"
    };

    /// <summary>
    /// 保存便签内容（【保存】接口）。urlencoded 表单，字段与网页端抓包一致。
    /// 成功后响应里会带一个新的 note_token（旧的立即作废）。
    /// </summary>
    public static async Task<SaveResult> SaveNoteAsync(
        string noteName, string notePwd, string noteId, string noteToken, string noteContent)
    {
        try
        {
            // x-www-form-urlencoded，逐字段 URL 编码（与网页端 F12 载荷一致）
            var payload = new Dictionary<string, string>
            {
                ["note_name"] = noteName,
                ["note_id"] = noteId,
                ["note_content"] = noteContent,
                ["note_token"] = noteToken,
                ["expire_time"] = ExpireTimeSeconds.ToString(),
                ["note_pwd"] = notePwd
            };
            var encoded = string.Join("&", payload.Select(kv =>
                kv.Key + "=" + Uri.EscapeDataString(kv.Value)));

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "save/")
            {
                Content = new StringContent(encoded, Encoding.UTF8,
                    "application/x-www-form-urlencoded")
            };
            SetCommonHeaders(request, noteName, notePwd);

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return ParseSaveResponse(body);
        }
        catch
        {
            return new SaveResult { Success = false, ErrorKey = "WebNote.NetworkError" };
        }
    }

    /// <summary>解析保存接口的响应 JSON</summary>
    private static SaveResult ParseSaveResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
            if (status == StatusOk)
            {
                var data = root.GetProperty("data");
                return new SaveResult
                {
                    Success = true,
                    NewNoteToken = data.TryGetProperty("note_token", out var token) ? token.GetString() : null
                };
            }

            return new SaveResult
            {
                Success = false,
                ErrorKey = ErrorKeyForStatus(status),
                RawError = root.TryGetProperty("error", out var err) ? err.GetString() : null
            };
        }
        catch
        {
            return new SaveResult { Success = false, ErrorKey = "WebNote.NetworkError", RawError = body };
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DaenLauncher.Services;

/// <summary>
/// 北京时间获取服务（需求：其他常用-北京时间同步）。
/// 两种方式：
/// - NTP/SNTP：向时间服务器发 UDP 授时包（RFC 4330 简化版），取服务器返回的标准时间；
/// - HTTP 时间 API：请求各家的 HTTP 接口，从响应里读出时间（淘宝/苏宁接口返回毫秒时间戳）。
///
/// 返回的都是"北京时间的墙上时间"，与本地时区无关——
/// 同步时按同一个 UTC 瞬间设置本地时间，所以本地时区设置不会被改变。
/// </summary>
public static class TimeSyncService
{
    /// <summary>NTP 时间戳起点（1900-01-01）到 Unix 纪元（1970-01-01）的秒数</summary>
    private const long NtpEpochOffsetSeconds = 2208988800L;

    /// <summary>NTP 包长度（字节）</summary>
    private const int NtpPacketSize = 48;

    /// <summary>NTP 请求超时（毫秒）</summary>
    private const int NtpTimeoutMs = 4000;

    /// <summary>NTP 版本（4）左移 3 位后的标志字节（LI=0, VN=4, Mode=3 客户端）</summary>
    private const byte NtpRequestHeader = 0x1B;

    /// <summary>HTTP 时间接口超时（秒，走 ProxyService 统一代理）</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 从 HTTP 响应里找时间戳（毫秒或秒，取 10~13 位数字）。
    /// 字段名各接口不同：淘宝 "t"、苏宁 "currentTime"，所以用一个宽松的字段名集合；
    /// 带超时防正则回溯。
    /// </summary>
    private static readonly Regex TimestampPattern = new(
        "\"(?:t|currentTime|timestamp|now|sysTime)\"\\s*:\\s*\"?(\\d{10,13})",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>获取北京时间的方式</summary>
    public enum SyncMethod
    {
        /// <summary>NTP / SNTP 网络授时</summary>
        Ntp,

        /// <summary>HTTP 时间 API</summary>
        Http
    }

    /// <summary>
    /// 获取结果：UTC 时间 + 用于界面显示的中文来源说明。
    /// </summary>
    public sealed record SyncResult(DateTime UtcTime, string Source);

    #region 服务器清单（禁止硬编码散落，全部集中在这里）

    /// <summary>NTP 服务器（key = 语言键，value = 主机名）</summary>
    public static readonly (string NameKey, string Host)[] NtpServers =
    [
        ("Tools.Time.Server.Ntsc", "ntp.ntsc.ac.cn"),
        ("Tools.Time.Server.Aliyun", "ntp.aliyun.com"),
        ("Tools.Time.Server.Tencent", "ntp.tencent.com"),
        ("Tools.Time.Server.Huawei", "ntp.cn-north-1.myhuaweicloud.com"),
        ("Tools.Time.Server.Pool", "cn.pool.ntp.org"),
        ("Tools.Time.Server.Edu", "time.edu.cn")
    ];

    /// <summary>HTTP 时间 API（key = 语言键，value = 接口地址；均为实测可用）</summary>
    public static readonly (string NameKey, string Url)[] HttpApis =
    [
        ("Tools.Time.Api.Taobao", "https://acs.m.taobao.com/gw/mtop.common.getTimestamp/"),
        ("Tools.Time.Api.Suning", "https://f.m.suning.com/api/ct.do"),
        ("Tools.Time.Api.Baidu", "https://www.baidu.com")
    ];

    #endregion

    /// <summary>
    /// 获取北京时间（返回 UTC 瞬间，界面显示时按本地时区格式化）。
    /// 失败时抛异常，消息可直接展示给用户。
    /// </summary>
    public static async Task<SyncResult> GetBeijingTimeAsync(SyncMethod method,
        string serverOrUrl, CancellationToken cancellationToken = default)
    {
        if (method == SyncMethod.Ntp)
        {
            var utc = await QueryNtpAsync(serverOrUrl, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(LocalizationService.Tr("Tools.Time.ErrNtpFailed"));
            return new SyncResult(utc, serverOrUrl);
        }

        var (httpUtc, source) = await QueryHttpAsync(serverOrUrl, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException(LocalizationService.Tr("Tools.Time.ErrHttpFailed"));
        return new SyncResult(httpUtc, source);
    }

    /// <summary>
    /// 本地时间与北京时间的差值（本地时间 - 北京时间）。
    /// 正值表示本地时间比北京时间快，负值表示慢。
    /// </summary>
    public static TimeSpan ComputeOffset(DateTime localNow, DateTime beijingUtc)
    {
        // 本地时间统一按 UTC 瞬间比较（避免时区/夏令时干扰）
        var localUtc = localNow.ToUniversalTime();
        return localUtc - beijingUtc;
    }

    /// <summary>
    /// 用管理员权限把本地时钟校准到给定时间（需求：同步本地时间）。
    /// 实现：以 runas 提权启动 PowerShell 的 Set-Date（会弹 UAC）。
    /// 提权进程无法重定向输出，所以"是否成功"靠事后重新读时钟校验。
    /// 同步的是同一 UTC 瞬间，本地时区设置不变。
    /// </summary>
    public static bool ApplySystemTime(DateTime utcTime, out string? error)
    {
        error = null;
        try
        {
            // Set-Date 接受本地时间，所以把 UTC 换算成本地时间再传
            var localTime = utcTime.ToLocalTime();
            var formatted = localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Set-Date -Date '{formatted}'\"",
                // 用 runas 提权必须走 ShellExecute（并因此不能重定向输出）
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    error = LocalizationService.Tr("Tools.Time.ErrStartFailed");
                    return false;
                }
                process.WaitForExit(ProcessTimeoutMs);
            }

            // 提权进程无法回读输出，用"事后校验时钟"判断是否真的设置成功
            var drift = Math.Abs((DateTime.UtcNow - utcTime).TotalSeconds);
            if (drift <= VerifyToleranceSeconds) return true;

            error = LocalizationService.Tr("Tools.Time.ErrSetFailed");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗点了"否"
            error = ex.NativeErrorCode == UserCancelledErrorCode
                ? LocalizationService.Tr("Tools.Time.ErrUserCancelled")
                : ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>校验同步结果时允许的误差（秒）——网络延迟 + 进程启动开销</summary>
    private const int VerifyToleranceSeconds = 10;

    /// <summary>UAC 被用户取消的错误码（ERROR_CANCELLED）</summary>
    private const int UserCancelledErrorCode = 1223;

    /// <summary>提权进程等待超时（毫秒）</summary>
    private const int ProcessTimeoutMs = 60000;

    /// <summary>
    /// NTP 查询（SNTP 客户端模式，RFC 4330 简化版）：失败返回 null。
    /// 返回服务器给的 UTC 时间（已按网络往返的一半做补偿）。
    ///
    /// 健壮性：UDP 丢包很常见，所以每个 IP 重试几次；域名解析到多个地址（含 IPv6）时逐个尝试——
    /// 只有 IPv6 地址的服务器在纯 IPv4 网络下必然失败，必须换下一个地址。
    /// </summary>
    private static async Task<DateTime?> QueryNtpAsync(string host, CancellationToken cancellationToken)
    {
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        if (addresses.Length == 0) return null;

        // IPv4 优先（国内纯 IPv4 网络更常见，IPv6 地址排后面再试）
        var ordered = addresses
            .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .Take(NtpMaxAddresses);

        foreach (var address in ordered)
        {
            for (var attempt = 0; attempt < NtpRetriesPerAddress; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await QueryNtpOnceAsync(address, cancellationToken).ConfigureAwait(false);
                if (result != null) return result;
            }
        }
        return null;
    }

    /// <summary>单个地址的一次 NTP 查询</summary>
    private static async Task<DateTime?> QueryNtpOnceAsync(System.Net.IPAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(address.AddressFamily);
            client.Client.SendTimeout = NtpTimeoutMs;
            client.Client.ReceiveTimeout = NtpTimeoutMs;

            var request = new byte[NtpPacketSize];
            request[0] = NtpRequestHeader; // LI=0, VN=4, Mode=3（客户端）
            var sendTime = DateTime.UtcNow;

            await client.SendAsync(request, request.Length,
                new System.Net.IPEndPoint(address, NtpPort)).ConfigureAwait(false);

            var receiveTask = client.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask,
                Task.Delay(NtpTimeoutMs, cancellationToken)).ConfigureAwait(false);
            if (completed != receiveTask) return null;

            var response = receiveTask.Result.Buffer;
            var receiveTime = DateTime.UtcNow;
            if (response.Length < NtpPacketSize) return null;

            // 字节 40..43 = 服务器发送时间（Transmit Timestamp，大端 32 位秒数 + 32 位小数）
            var seconds = ReadBigEndianUInt32(response, 40);
            if (seconds == 0) return null;

            var ntpTime = DateTime.UnixEpoch.AddSeconds(seconds - NtpEpochOffsetSeconds);
            // 加上服务器→本地的一半往返时间，得到更准的当前时刻
            var roundTrip = receiveTime - sendTime;
            return ntpTime + TimeSpan.FromTicks(roundTrip.Ticks / 2);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>每个地址的 NTP 重试次数（UDP 丢包常见）</summary>
    private const int NtpRetriesPerAddress = 3;

    /// <summary>最多尝试几个解析出的地址</summary>
    private const int NtpMaxAddresses = 3;

    /// <summary>NTP 标准端口</summary>
    private const int NtpPort = 123;

    /// <summary>从大端字节序读 4 字节无符号整数</summary>
    private static long ReadBigEndianUInt32(byte[] buffer, int offset)
    {
        return ((long)buffer[offset] << 24) | ((long)buffer[offset + 1] << 16) |
               ((long)buffer[offset + 2] << 8) | buffer[offset + 3];
    }

    /// <summary>
    /// HTTP 时间 API 查询（成功返回 (UTC 时间, 来源说明)；失败返回 null）。
    /// 走 ProxyService 统一代理——软件内所有联网功能都遵循代理设置（需求）。
    /// </summary>
    private static async Task<(DateTime UtcTime, string Source)?> QueryHttpAsync(string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = ProxyService.CreateHttpClient(HttpTimeout);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // 淘宝/苏宁接口都返回 JSON 里的毫秒时间戳（"t": 1234567890123）
            var match = TimestampPattern.Match(body);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var milliseconds))
            {
                // 13 位 = 毫秒，10 位 = 秒
                if (match.Groups[1].Value.Length <= 10) milliseconds *= 1000;
                return (DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime, url);
            }

            // 百度等网页：读响应头的 Date（HTTP 标准头，精度到秒）
            if (response.Headers.Date.HasValue)
            {
                return (response.Headers.Date.Value.UtcDateTime, url);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

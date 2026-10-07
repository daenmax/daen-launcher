using System.Net;
using System.Net.Http;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 统一网络代理服务（需求：设置-常规-代理，软件内所有联网功能统一遵循此配置）。
/// 使用方式：任何需要联网的代码都用 <see cref="CreateHttpClient"/> 获取 HttpClient，
/// 不要自己 new HttpClient()——这样代理模式（不使用/系统/HTTP/SOCKS5）才能全局生效。
/// 已接入：webnote 云同步（WebNoteClient）、必应壁纸（WallpaperService）、
/// 网址图标（FaviconService）；检查版本更新（占位，后续完善时同样走这里）。
/// 原理：SocketsHttpHandler 按代理模式构造（.NET 内置支持 socks5:// 代理），
/// 共享同一个 handler 复用连接池；代理配置变化后自动换新 handler，立即生效无需重启。
/// </summary>
public static class ProxyService
{
    /// <summary>代理连通性测试地址（选百度：国内外都可达、响应快、无需鉴权）</summary>
    public const string ProxyTestUrl = "https://www.baidu.com/";

    /// <summary>连通性测试的超时时间</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>未显式指定超时时 CreateHttpClient 使用的默认超时</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly object Lock = new();

    /// <summary>共享的 handler（按代理配置构造，配置变化时整体换新，连接池全局复用）</summary>
    private static SocketsHttpHandler? _sharedHandler;

    /// <summary>共享 handler 对应的配置指纹（模式|地址|端口|账号|密码）</summary>
    private static string _handlerFingerprint = "";

    /// <summary>
    /// 创建 HttpClient：底层共享同一个连接池（handler），
    /// 代理配置变化后自动换新 handler，新配置立即生效（无需重启软件）。
    /// 返回的 client 用完可以不 Dispose（handler 不会被连带释放）。
    /// </summary>
    /// <param name="timeout">超时时间（各功能不同：favicon 8s / webnote 15s / 壁纸 20s，默认 30s）</param>
    public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        lock (Lock)
        {
            var fingerprint = CurrentFingerprint();
            if (_sharedHandler == null || fingerprint != _handlerFingerprint)
            {
                // 旧 handler 不主动 Dispose（可能还有在途请求引用它），交给 GC 回收
                var settings = SettingsService.Instance.Settings;
                _sharedHandler = BuildSocketsHandler(
                    settings.ProxyMode, settings.ProxyHost, settings.ProxyPort,
                    settings.ProxyUsername, settings.ProxyPassword);
                _handlerFingerprint = fingerprint;
            }
            return new HttpClient(_sharedHandler, disposeHandler: false)
            {
                Timeout = timeout ?? DefaultTimeout
            };
        }
    }

    /// <summary>
    /// 测试代理连通性（设置页"测试代理"按钮）：用界面上填写的配置（不含已保存值）
    /// 构造 handler，向测试地址发一个真实 GET 请求。返回是否成功和失败原因。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestProxyAsync(
        ProxyMode mode, string host, int port, string username, string password)
    {
        try
        {
            using var handler = BuildSocketsHandler(mode, host, port, username, password);
            using var client = new HttpClient(handler) { Timeout = TestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, ProxyTestUrl);
            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return (true, "");
            }
            return (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // 常见失败：连接被拒绝/超时/认证失败——消息直接展示方便定位
            var message = ex.InnerException?.Message ?? ex.Message;
            return (false, message);
        }
    }

    /// <summary>当前代理配置的指纹（用于判断配置是否变化）</summary>
    private static string CurrentFingerprint()
    {
        var settings = SettingsService.Instance.Settings;
        return $"{(int)settings.ProxyMode}|{settings.ProxyHost}|{settings.ProxyPort}" +
               $"|{settings.ProxyUsername}|{settings.ProxyPassword}";
    }

    /// <summary>
    /// 按代理模式构造 SocketsHttpHandler。
    /// 防呆：HTTP/SOCKS5 模式下地址为空或端口非法时回退为系统代理（默认行为），
    /// 避免配置不完整导致整个软件断网。
    /// </summary>
    private static SocketsHttpHandler BuildSocketsHandler(
        ProxyMode mode, string host, int port, string username, string password)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // 连接定期重建：DNS 变化、系统代理变化能被感知
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        switch (mode)
        {
            case ProxyMode.None:
                // 直连，显式关闭代理（不读系统设置）
                handler.UseProxy = false;
                break;

            case ProxyMode.Http:
            case ProxyMode.Socks5:
            {
                // 配置不完整：回退系统代理（UseProxy=true + Proxy=null 的默认行为）
                if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
                {
                    break;
                }
                // .NET 内置支持 http:// 和 socks5:// 两种代理协议
                var scheme = mode == ProxyMode.Http ? "http" : "socks5";
                var proxy = new WebProxy($"{scheme}://{host}:{port}");
                if (!string.IsNullOrEmpty(username))
                {
                    proxy.Credentials = new NetworkCredential(username, password);
                }
                handler.Proxy = proxy;
                break;
            }

            case ProxyMode.System:
            default:
                // UseProxy=true（默认）+ Proxy=null = 使用系统代理设置（HttpClient.DefaultProxy）
                break;
        }

        return handler;
    }
}

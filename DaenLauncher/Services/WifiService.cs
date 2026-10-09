using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DaenLauncher.Services;

/// <summary>
/// 已连接过的 WiFi 信息读取服务（需求：其他常用-已连接wifi密码查看）。
///
/// 实现方式：调用系统自带的 netsh 命令解析文本输出
/// （参考 https://www.cnblogs.com/daen/p/16587081.html）。
/// - 列出所有连接过的 WiFi：netsh wlan show profiles
/// - 查看某个 WiFi 的密码：netsh wlan show profile name="名称" key=clear → "关键内容"
///
/// 注意：netsh 输出会跟随系统语言变化，所以匹配用的是"关键字 + 冒号"的宽松策略，
/// 同时兼容中文（名称/关键内容/身份验证）和英文（Name/Key Content/Authentication）输出。
/// </summary>
public static class WifiService
{
    /// <summary>单个已保存 WiFi 的信息</summary>
    public sealed class WifiProfile
    {
        /// <summary>WiFi 名称（SSID）</summary>
        public string Name { get; set; } = "";

        /// <summary>密码（明文；没有密码或读取失败时为空）</summary>
        public string Password { get; set; } = "";

        /// <summary>身份验证方式（如 WPA2-Personal；构建二维码载荷用）</summary>
        public string Authentication { get; set; } = "";

        /// <summary>是否隐藏的网络</summary>
        public bool IsHidden { get; set; }

        /// <summary>是否有密码（开放网络 = false）</summary>
        public bool HasPassword => !string.IsNullOrEmpty(Password);
    }

    /// <summary>netsh 输出里标识行的关键字（中英文都列上，系统语言不同会命中不同项）</summary>
    private static readonly string[] NameLabels = ["名称", "Name", "SSID 名称", "SSID name"];

    private static readonly string[] KeyContentLabels = ["关键内容", "Key Content"];

    private static readonly string[] AuthenticationLabels = ["身份验证", "Authentication"];

    /// <summary>无线网络服务未运行 / 没有无线网卡时 netsh 会提示的关键字</summary>
    private static readonly string[] NoWirelessKeywords =
        ["无线自动配置服务", "Wireless AutoConfig Service", "无线服务", "没有无线接口", "There is no wireless interface"];

    /// <summary>
    /// 读取本机所有"连接过的"WiFi 列表（含密码）。
    /// 失败时通过 error 返回原因，成功时 error 为 null。
    /// </summary>
    public static List<WifiProfile> GetAllProfiles(out string? error)
    {
        error = null;
        var profiles = new List<WifiProfile>();

        var listOutput = RunNetsh("wlan show profiles");
        if (listOutput == null)
        {
            error = LocalizationService.Tr("Tools.Wifi.ErrRunFailed");
            return profiles;
        }

        if (LooksLikeNoWireless(listOutput))
        {
            error = LocalizationService.Tr("Tools.Wifi.ErrNoWireless");
            return profiles;
        }

        var names = ParseProfileNames(listOutput);
        if (names.Count == 0)
        {
            error = LocalizationService.Tr("Tools.Wifi.ErrEmpty");
            return profiles;
        }

        foreach (var name in names)
        {
            profiles.Add(GetProfileDetail(name));
        }
        return profiles;
    }

    /// <summary>读取单个 WiFi 的详细信息（密码/加密方式）；读取失败时只有名称</summary>
    public static WifiProfile GetProfileDetail(string name)
    {
        var profile = new WifiProfile { Name = name };

        // 名称可能含双引号等字符，用引号包裹传给 netsh
        var output = RunNetsh($"wlan show profile name=\"{name}\" key=clear");
        if (output == null) return profile;

        profile.Password = ExtractValue(output, KeyContentLabels);
        profile.Authentication = ExtractValue(output, AuthenticationLabels);
        profile.IsHidden = IsHiddenNetwork(output);
        return profile;
    }

    /// <summary>
    /// 判断是否是隐藏网络（SSID 未广播）。
    /// 依据 netsh 的"网络广播"行：
    /// 隐藏网络 = "即使网络未广播也连接"/"Connect even if the network is not broadcasting"，
    /// 普通网络 = "只在网络广播时连接"/"Connect only if the network is broadcasting"。
    /// 所以必须匹配"未广播"/"not broadcasting"，不能只匹配"广播"（会误判普通网络）。
    /// </summary>
    private static bool IsHiddenNetwork(string output)
    {
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.Contains("未广播", StringComparison.Ordinal) ||
                line.Contains("not broadcasting", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从 netsh 输出里解析出 WiFi 名称列表。
    /// 行形如 "    所有用户配置文件 : Daen"（中文）或 "    All User Profile     : Daen"（英文）。
    /// </summary>
    private static List<string> ParseProfileNames(string output)
    {
        var names = new List<string>();
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!line.Contains(':')) continue;
            // 只认"配置文件"相关的行，避免误把其他统计信息当名称
            if (!line.Contains("配置文件", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("User Profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ValueAfterColon(line);
            if (value.Length > 0 && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(value);
            }
        }
        return names;
    }

    /// <summary>按关键字（如"关键内容"/"Key Content"）从输出里取冒号后的值</summary>
    private static string ExtractValue(string output, string[] labels)
    {
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            foreach (var label in labels)
            {
                if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var value = ValueAfterColon(line);
                if (value.Length > 0) return value;
            }
        }
        return "";
    }

    /// <summary>取第一个冒号之后的内容并去空白（值本身可能含冒号，只取第一个冒号后全部）</summary>
    private static string ValueAfterColon(string line)
    {
        var index = line.IndexOf(':');
        if (index < 0 || index == line.Length - 1) return "";
        return line[(index + 1)..].Trim();
    }

    private static bool LooksLikeNoWireless(string output)
    {
        foreach (var keyword in NoWirelessKeywords)
        {
            if (output.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>按三种换行符切分（命令行输出是 CRLF，但保险起见归一化）</summary>
    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// 执行 netsh 命令并返回标准输出；失败返回 null。
    /// netsh 的输出编码跟随系统区域设置（中文 Windows 是 GBK，不是 UTF-8），
    /// 所以这里读原始字节后手动解码：先按 UTF-8 严格解码，失败再按系统 ANSI 代码页解码。
    /// </summary>
    private static string? RunNetsh(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            // 用二进制读取，避免 .NET 默认按 UTF-8 解码导致中文乱码
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit(TimeoutMs);

            return DecodeOutput(buffer.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解码命令行输出：优先 UTF-8（严格模式，遇到非法字节就抛异常），
    /// 失败则回退到系统 ANSI 代码页（中文系统即 GBK）。
    /// </summary>
    private static string DecodeOutput(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                // .NET Core 默认不带 GBK 等代码页，需要注册提供程序
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return Encoding.GetEncoding(codePage).GetString(bytes);
            }
            catch
            {
                // 兜底：宽容模式的 UTF-8（乱码也好过没有输出）
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>netsh 命令超时（毫秒）——网络异常时避免界面卡死</summary>
    private const int TimeoutMs = 8000;
}

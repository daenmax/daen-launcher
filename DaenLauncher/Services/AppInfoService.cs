using System.Reflection;
using System.Text.Json;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 应用静态信息（AppConfig）加载器。
/// 需求第1条：软件名、描述、官网等文本放在 appconfig.json 中，作为 EmbeddedResource 嵌入 exe。
/// </summary>
public static class AppInfoService
{
    private static readonly Lazy<AppConfig> Lazy = new(LoadFromEmbeddedResource);

    /// <summary>应用配置（只读）</summary>
    public static AppConfig Config => Lazy.Value;

    private const string ResourceName = "DaenLauncher.Assets.appconfig.json";

    private static AppConfig LoadFromEmbeddedResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream != null)
            {
                var config = JsonSerializer.Deserialize<AppConfig>(stream);
                if (config != null) return config;
            }
        }
        catch
        {
            // 读取失败时使用默认值
        }
        return new AppConfig();
    }

    /// <summary>程序版本号（来自程序集）</summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}

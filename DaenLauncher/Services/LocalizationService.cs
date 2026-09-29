using System.Reflection;
using System.Text.Json;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 多语言服务（需求-常规第1条）：
/// - 简体中文、English 两个语言包作为 EmbeddedResource 嵌入 exe；
/// - 首次启动或文件缺失时释放到 data\language 目录；
/// - 支持用户往 data\language 放其他语言文件，重启后出现在下拉列表中；
/// - 当前系统语言是简体中文则默认简体中文，否则一律 English。
/// </summary>
public sealed class LocalizationService
{
    /// <summary>内嵌语言包：显示名 -> 嵌入资源名（避免非 ASCII 资源名问题）</summary>
    private static readonly Dictionary<string, string> BuiltinLanguages = new()
    {
        ["简体中文"] = "DaenLauncher.Languages.zh-CN.json",
        ["English"] = "DaenLauncher.Languages.en-US.json"
    };

    /// <summary>释放到 data\language 时的文件名</summary>
    private static readonly Dictionary<string, string> BuiltinFileNames = new()
    {
        ["简体中文"] = "简体中文.json",
        ["English"] = "English.json"
    };

    /// <summary>语言变化事件（所有窗口订阅后重新刷新文字）</summary>
    public static event Action? LanguageChanged;

    private static readonly Lazy<LocalizationService> Lazy = new(() => new LocalizationService());
    public static LocalizationService Instance => Lazy.Value;

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallbackStrings = new();

    /// <summary>当前语言显示名</summary>
    public string CurrentLanguage { get; private set; } = "English";

    private LocalizationService()
    {
    }

    /// <summary>
    /// 初始化：释放内嵌语言包、加载当前语言字典。在 App 启动时调用。
    /// </summary>
    public void Initialize(AppSettings settings)
    {
        EnsureBuiltinReleased();

        // 首次启动：检测系统语言
        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            var isChinese = IsSystemChinese();
            settings.Language = isChinese ? "简体中文" : "English";
            SettingsService.Instance.Save();
        }

        SetLanguage(settings.Language);
    }

    /// <summary>判断当前系统语言是否是简体中文</summary>
    private static bool IsSystemChinese()
    {
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            return culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把两个内嵌语言文件写入 data\language。
    /// 注意：每次启动都覆盖写入——软件升级后新增的翻译键才能生效；
    /// 自定义语言请另存新文件，不要直接改这两个内置文件。
    /// </summary>
    private void EnsureBuiltinReleased()
    {
        foreach (var (name, fileName) in BuiltinFileNames)
        {
            var target = Path.Combine(DataPathService.LanguageDir, fileName);
            try
            {
                var json = ReadEmbedded(BuiltinLanguages[name]);
                if (json != null)
                {
                    // 内容没变就跳过写盘，减少磁盘损耗
                    if (!File.Exists(target) || File.ReadAllText(target) != json)
                    {
                        File.WriteAllText(target, json);
                    }
                }
            }
            catch
            {
                // 释放失败不阻塞启动
            }
        }
    }

    /// <summary>读取嵌入资源内容</summary>
    private static string? ReadEmbedded(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 可用语言列表：data\language 目录下所有 .json（文件名即显示名），加上内置兜底。
    /// </summary>
    public List<string> GetAvailableLanguages()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.GetFiles(DataPathService.LanguageDir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // 目录读取失败就用内置列表
        }

        // 确保内置语言始终在列表里
        foreach (var name in BuiltinLanguages.Keys)
        {
            if (seen.Add(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// 切换语言（设置里选择后调用；调用方负责提示重启）。
    /// </summary>
    public void SetLanguage(string name)
    {
        CurrentLanguage = name;

        // 回退字典：English 内置包
        _fallbackStrings = LoadDictionary(BuiltinLanguages["English"]) ?? new();

        // 当前语言字典：优先 data\language 下的同名文件，缺失则用内置包
        Dictionary<string, string>? current = null;
        var file = Path.Combine(DataPathService.LanguageDir, name + ".json");
        if (File.Exists(file))
        {
            try
            {
                current = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(file));
            }
            catch
            {
                current = null;
            }
        }

        current ??= LoadDictionary(BuiltinLanguages.GetValueOrDefault(name, ""));

        _strings = current ?? new Dictionary<string, string>();

        LanguageChanged?.Invoke();
    }

    /// <summary>加载内嵌语言字典</summary>
    private static Dictionary<string, string>? LoadDictionary(string resourceName)
    {
        if (string.IsNullOrEmpty(resourceName)) return null;
        try
        {
            var json = ReadEmbedded(resourceName);
            if (json == null) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 翻译：取当前语言 -> 回退 English -> 返回 key 本身。
    /// </summary>
    public string T(string key)
    {
        if (_strings.TryGetValue(key, out var value)) return value;
        if (_fallbackStrings.TryGetValue(key, out var fb)) return fb;
        return key;
    }

    /// <summary>静态快捷翻译</summary>
    public static string Tr(string key) => Instance.T(key);
}

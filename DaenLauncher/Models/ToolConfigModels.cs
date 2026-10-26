namespace DaenLauncher.Models;

/// <summary>
/// 加解密算法的存储标识（配置文件里的值，禁止用界面枚举名硬编码）。
/// 与 Views.Tools.CryptoAlgo 一一对应。
/// </summary>
public static class CryptoAlgoIds
{
    /// <summary>国密 SM4</summary>
    public const string Sm4 = "sm4";

    /// <summary>AES</summary>
    public const string Aes = "aes";

    /// <summary>全部算法标识</summary>
    public static readonly string[] All = [Sm4, Aes];

    /// <summary>算法标识 → 界面显示用的语言键（配置管理窗口展示用）</summary>
    public static string DisplayKey(string? algo) =>
        algo == Aes ? "Tools.Aes" : "Tools.Sm4";
}

/// <summary>
/// 一套加解密配置（保存 SM4/AES 页面上的全部参数）。
/// 存储可选值统一用语言键（Tools.Enc.*），与界面下拉框的取值方式一致。
/// </summary>
public sealed class CryptoConfig
{
    /// <summary>唯一 ID（删除时定位用）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>配置名称（用户填写，同一算法下不能重名）</summary>
    public string Name { get; set; } = "";

    /// <summary>算法标识（CryptoAlgoIds）</summary>
    public string Algo { get; set; } = CryptoAlgoIds.Sm4;

    /// <summary>原文格式（Tools.Enc.Utf8 / Hex / Base64）</summary>
    public string TextFormat { get; set; } = "Tools.Enc.Utf8";

    /// <summary>加密模式（Tools.Enc.CBC 等）</summary>
    public string Mode { get; set; } = "Tools.Enc.CBC";

    /// <summary>密钥（按 KeyFormat 解释的文本）</summary>
    public string Key { get; set; } = "";

    /// <summary>密钥格式（Tools.Enc.Utf8 / Hex / Base64）</summary>
    public string KeyFormat { get; set; } = "Tools.Enc.Utf8";

    /// <summary>IV（按 IvFormat 解释的文本）</summary>
    public string Iv { get; set; } = "";

    /// <summary>IV 格式（Tools.Enc.Utf8 / Hex / Base64）</summary>
    public string IvFormat { get; set; } = "Tools.Enc.Utf8";

    /// <summary>填充方式（Tools.Enc.Pkcs7 等）</summary>
    public string Padding { get; set; } = "Tools.Enc.Pkcs7";

    /// <summary>输出格式（Tools.Enc.Hex / Base64）</summary>
    public string OutputFormat { get; set; } = "Tools.Enc.Hex";

    /// <summary>输出结果转大写</summary>
    public bool Upper { get; set; }
}

/// <summary>加解密配置文件根对象，保存到 data\tools\crypto-configs.json</summary>
public sealed class CryptoConfigData
{
    /// <summary>数据格式版本（将来结构调整用）</summary>
    public int Version { get; set; } = 1;

    /// <summary>全部配置（SM4 与 AES 混在一起，按 Algo 区分）</summary>
    public List<CryptoConfig> Configs { get; set; } = new();
}

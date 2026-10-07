using Microsoft.UI.Xaml;

namespace DaenLauncher.Views.Tools;

/// <summary>
/// 每个工具页面实现该接口：Build() 构建自己的界面元素。
/// 窗口会对每个工具只调用一次 Build 并缓存（切换工具时保留输入内容等状态）。
/// </summary>
public interface IToolPage
{
    /// <summary>构建工具界面（窗口在首次选中该工具时调用一次）</summary>
    UIElement Build();
}

/// <summary>工具定义：id（缓存键）、名称的语言键、构建工厂</summary>
public sealed record ToolDefinition(string Id, string NameKey, Func<UIElement> Create);

/// <summary>分类 id 常量（禁止硬编码字符串散落各处，统一放这里）</summary>
public static class ToolCategoryIds
{
    /// <summary>加解密类</summary>
    public const string Crypto = "crypto";

    /// <summary>文本处理</summary>
    public const string Text = "text";

    /// <summary>格式化</summary>
    public const string Format = "format";

    /// <summary>其他常用</summary>
    public const string Misc = "misc";
}

/// <summary>
/// 常用工具注册表：分类 + 分类下的全部子功能。
/// 新增子功能时：写一个实现 IToolPage 的类，然后在对应分类的数组里加一行。
/// </summary>
public static class ToolRegistry
{
    /// <summary>分类定义：id、名称语言键、该分类下的子功能列表</summary>
    public sealed record Category(string Id, string NameKey, ToolDefinition[] Tools);

    /// <summary>全部分类（数组顺序 = 左侧导航顺序）</summary>
    public static readonly Category[] Categories =
    [
        // ===== 加解密类 =====
        new(ToolCategoryIds.Crypto, "Tools.Cat.Crypto",
        [
            new ToolDefinition("sm4", "Tools.Sm4", () => new CryptoToolPage(CryptoAlgo.Sm4).Build()),
            new ToolDefinition("aes", "Tools.Aes", () => new CryptoToolPage(CryptoAlgo.Aes).Build()),
        ]),

        // ===== 文本处理 =====
        new(ToolCategoryIds.Text, "Tools.Cat.Text",
        [
            new ToolDefinition("md5", "Tools.Md5", () => new Md5Tool().Build()),
            new ToolDefinition("url", "Tools.Url", () => new UrlTool().Build()),
            new ToolDefinition("base64", "Tools.Base64", () => new Base64Tool().Build()),
            new ToolDefinition("newlineQuote", "Tools.NewlineQuote", () => new NewlineQuoteTool().Build()),
            new ToolDefinition("lineDedup", "Tools.LineDedup", () => new LineDedupTool().Build()),
            new ToolDefinition("replace", "Tools.Replace", () => new ReplaceTool().Build()),
            new ToolDefinition("case", "Tools.Case", () => new CaseTool().Build()),
            new ToolDefinition("reverse", "Tools.Reverse", () => new ReverseTool().Build()),
            new ToolDefinition("camelSnake", "Tools.CamelSnake", () => new CamelSnakeTool().Build()),
            new ToolDefinition("sort", "Tools.Sort", () => new SortTool().Build()),
            new ToolDefinition("trim", "Tools.Trim", () => new TrimTool().Build()),
            new ToolDefinition("removeBlankLines", "Tools.RemoveBlankLines", () => new RemoveBlankLinesTool().Build()),
        ]),

        // ===== 格式化 =====
        new(ToolCategoryIds.Format, "Tools.Cat.Format",
        [
            new ToolDefinition("json", "Tools.Json", () => new JsonTool().Build()),
            new ToolDefinition("xml", "Tools.Xml", () => new XmlTool().Build()),
            new ToolDefinition("sql", "Tools.Sql", () => new SqlTool().Build()),
        ]),

        // ===== 其他常用 =====
        new(ToolCategoryIds.Misc, "Tools.Cat.Misc",
        [
            new ToolDefinition("lineSum", "Tools.LineSum", () => new LineSumTool().Build()),
            new ToolDefinition("curl", "Tools.Curl", () => new CurlTool().Build()),
            new ToolDefinition("fileNames", "Tools.FileNames", () => new FileNameTool().Build()),
            new ToolDefinition("password", "Tools.Password", () => new PasswordTool().Build()),
            new ToolDefinition("uuid", "Tools.Uuid", () => new UuidTool().Build()),
            new ToolDefinition("timestamp", "Tools.Timestamp", () => new TimestampTool().Build()),
            new ToolDefinition("colorPicker", "Tools.ColorPicker", () => new ColorPickerTool().Build()),
        ]),
    ];

    /// <summary>按 id 查分类（找不到返回 null）</summary>
    public static Category? FindCategory(string id) =>
        Categories.FirstOrDefault(c => c.Id == id);
}

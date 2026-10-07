using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Xml.Linq;

namespace DaenLauncher.Views.Tools;

// =====================================================================
// 格式化分类的 3 个工具：JSON / XML / SQL（都提供格式化 + 压缩）。
// JSON 额外提供转义 / 去转义；JSON 带注释时处理前自动删除注释。
// =====================================================================

/// <summary>JSON 格式化/压缩/转义/去转义（支持带注释的 JSON，处理时自动删除注释）</summary>
internal sealed class JsonTool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(150);
        root.Children.Add(_input);

        var format = ToolKit.PrimaryButton("Tools.Btn.Format", (_, _) => Run(JsonOp.Format));
        var compress = ToolKit.ActionButton("Tools.Btn.Compress", (_, _) => Run(JsonOp.Compress));
        var escape = ToolKit.ActionButton("Tools.Btn.Escape", (_, _) => Run(JsonOp.Escape));
        var unescape = ToolKit.ActionButton("Tools.Btn.Unescape", (_, _) => Run(JsonOp.Unescape));
        root.Children.Add(ToolKit.ButtonRow(format, compress, escape, unescape));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(150);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private enum JsonOp { Format, Compress, Escape, Unescape }

    private void Run(JsonOp op)
    {
        try
        {
            switch (op)
            {
                case JsonOp.Format:
                {
                    var node = JsonNode.Parse(StripJsonComments(_input.Text));
                    ToolKit.SetOutput(_output, node == null
                        ? LocalizationService.Tr("Tools.Json.ErrEmpty")
                        : JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                }
                case JsonOp.Compress:
                {
                    var node = JsonNode.Parse(StripJsonComments(_input.Text));
                    ToolKit.SetOutput(_output, node == null
                        ? LocalizationService.Tr("Tools.Json.ErrEmpty")
                        : JsonSerializer.Serialize(node));
                    break;
                }
                case JsonOp.Escape:
                    // 转义：给 " 和 \ 前加反斜杠（用于把 JSON 塞进 SQL/字符串里），不改变换行结构
                    ToolKit.SetOutput(_output, _input.Text.Replace("\\", "\\\\").Replace("\"", "\\\""));
                    break;
                case JsonOp.Unescape:
                    // 去转义：逐字符扫描，去掉 \ 后面的转义作用（\" → "，\\ → \，\n → 换行）
                    ToolKit.SetOutput(_output, UnescapeBackslashes(_input.Text));
                    break;
            }
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }

    /// <summary>
    /// 去转义：逐字符扫描，遇到 \ 就取它后面的那个字符（\" → "，\\ → \，\n → 换行）。
    /// 比整串 Replace 更稳：连续的 \\\\ 不会被错误地反转两次。
    /// </summary>
    internal static string UnescapeBackslashes(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next // \" → "，\\ → \，其他原样保留
                });
            }
            else
            {
                builder.Append(text[i]);
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 删除 JSON 里的 // 行注释 和 /* 块注释 */。
    /// 逐字符扫描并跟踪"当前是否在字符串里"，字符串内的 // 和 /* 不会误删。
    /// </summary>
    internal static string StripJsonComments(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < json.Length)
                {
                    builder.Append(json[++i]); // 转义字符原样带过（\" 不会终止字符串）
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n') i++; // 跳到行尾（换行符保留在外面）
                builder.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                i++; // 跳过 */
                continue;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }
}

/// <summary>XML 格式化/压缩（可选保留 &lt;?xml 头）/转义/反转义</summary>
internal sealed class XmlTool : IToolPage
{
    private TextBox _input = null!;
    private CheckBox _keepDeclaration = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(130);
        root.Children.Add(_input);

        _keepDeclaration = ToolKit.Check("Tools.Xml.KeepDecl", true);
        root.Children.Add(_keepDeclaration);

        var format = ToolKit.PrimaryButton("Tools.Btn.Format", (_, _) => Run(XmlOp.Format));
        var compress = ToolKit.ActionButton("Tools.Btn.Compress", (_, _) => Run(XmlOp.Compress));
        var escape = ToolKit.ActionButton("Tools.Btn.Escape", (_, _) => Run(XmlOp.Escape));
        var unescape = ToolKit.ActionButton("Tools.Xml.Unescape", (_, _) => Run(XmlOp.Unescape));
        root.Children.Add(ToolKit.ButtonRow(format, compress, escape, unescape));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(130);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private enum XmlOp { Format, Compress, Escape, Unescape }

    private void Run(XmlOp op)
    {
        try
        {
            switch (op)
            {
                case XmlOp.Format:
                {
                    var doc = XDocument.Parse(_input.Text.Trim());
                    ToolKit.SetOutput(_output, doc.Declaration == null
                        ? doc.ToString(SaveOptions.None)
                        : doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.None));
                    break;
                }
                case XmlOp.Compress:
                {
                    var doc = XDocument.Parse(_input.Text.Trim());
                    var text = doc.ToString(SaveOptions.DisableFormatting);
                    // 注意：XDocument.ToString() 本来就不输出 XML 声明，
                    // 勾选"压缩时保留<?xml头"要手动把声明拼回去
                    if (_keepDeclaration.IsChecked == true && doc.Declaration != null)
                    {
                        text = doc.Declaration + Environment.NewLine + text;
                    }
                    ToolKit.SetOutput(_output, text);
                    break;
                }
                case XmlOp.Escape:
                    // 转义：& < > " ' 换成 XML 实体（& 必须最先替换）
                    ToolKit.SetOutput(_output, _input.Text
                        .Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("\"", "&quot;")
                        .Replace("'", "&apos;"));
                    break;
                case XmlOp.Unescape:
                    // 反转义：实体换回字符（&amp; 必须最后替换，避免二次解错）
                    ToolKit.SetOutput(_output, _input.Text
                        .Replace("&lt;", "<")
                        .Replace("&gt;", ">")
                        .Replace("&quot;", "\"")
                        .Replace("&apos;", "'")
                        .Replace("&amp;", "&"));
                    break;
            }
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

/// <summary>SQL 格式化/压缩（关键字大写 + 主要子句换行，满足日常阅读需要）</summary>
internal sealed class SqlTool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(150);
        root.Children.Add(_input);

        var format = ToolKit.PrimaryButton("Tools.Btn.Format", (_, _) => Run(true));
        var compress = ToolKit.ActionButton("Tools.Btn.Compress", (_, _) => Run(false));
        root.Children.Add(ToolKit.ButtonRow(format, compress));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(150);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run(bool format)
    {
        try
        {
            ToolKit.SetOutput(_output, format
                ? SqlFormatter.Format(_input.Text)
                : SqlFormatter.Compress(_input.Text));
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

/// <summary>
/// 轻量 SQL 格式化器：
/// - 关键字统一大写；
/// - SELECT/FROM/WHERE/GROUP BY/ORDER BY/HAVING/LIMIT/JOIN/INSERT INTO/VALUES/UPDATE/SET/DELETE FROM/UNION 等主要子句前换行；
/// - WHERE 里的 AND/OR/ON 换行并缩进；
/// - 字符串字面量和注释原样保留，不会被改动。
/// </summary>
internal static class SqlFormatter
{
    /// <summary>前面要换行的主要子句关键字（支持两字关键字，如 GROUP BY）</summary>
    private static readonly string[] MajorKeywords =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "UNION ALL", "UNION", "INTERSECT", "EXCEPT",
        "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN", "LEFT JOIN", "RIGHT JOIN",
        "FULL JOIN", "INNER JOIN", "OUTER JOIN", "CROSS JOIN", "JOIN",
        "INSERT INTO", "REPLACE INTO", "VALUES", "UPDATE", "SET", "DELETE FROM"
    ];

    /// <summary>换行 + 缩进的子条件关键字（AND/OR/ON，属于子句内部的条件）</summary>
    private static readonly string[] IndentKeywords = ["AND", "OR", "ON"];

    public static string Format(string sql)
    {
        var tokens = Tokenize(sql);
        var builder = new StringBuilder(sql.Length + 128);
        for (var i = 0; i < tokens.Count;)
        {
            // 主要子句关键字（含两字的整组匹配）：换行 + 全大写输出
            var majorWords = MatchKeyword(tokens, i, MajorKeywords);
            if (majorWords > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                for (var w = 0; w < majorWords; w++)
                {
                    if (w > 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(tokens[i++].ToUpperInvariant());
                }
                builder.Append(' ');
                continue;
            }

            // 子条件关键字：换行 + 两格缩进
            if (MatchKeyword(tokens, i, IndentKeywords) > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine().Append("  ");
                }
                builder.Append(tokens[i++].ToUpperInvariant()).Append(' ');
                continue;
            }

            var token = tokens[i++];

            // 注释独立成行（原样保留）
            if (token.StartsWith("--") || token.StartsWith("/*"))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.AppendLine(token.TrimEnd());
                continue;
            }

            AppendToken(builder, token);
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>按上下文拼接 token，让括号/逗号紧贴（输出更接近正常 SQL 排版）</summary>
    private static void AppendToken(StringBuilder builder, string token)
    {
        if (builder.Length == 0)
        {
            builder.Append(token);
            return;
        }
        var last = builder[^1];
        // 右括号/逗号/分号：贴着前一个内容
        if (token is ")" or "," or ";" or ".")
        {
            if (last == ' ')
            {
                builder.Length--;
            }
            builder.Append(token).Append(' ');
            return;
        }
        // 左括号/点号：贴着后面的内容
        if (last == '(' || last == '.' || last == '`' || last == '\'')
        {
            builder.Append(token);
            return;
        }
        builder.Append(token).Append(' ');
    }

    /// <summary>判断 tokens 的第 i 个位置是否匹配 keywords 中的关键字，返回消耗的 token 数（0 = 不匹配）</summary>
    private static int MatchKeyword(List<string> tokens, int i, string[] keywords)
    {
        var upper = tokens[i].ToUpperInvariant();
        foreach (var keyword in keywords)
        {
            var words = keyword.Split(' ');
            if (words[0] != upper)
            {
                continue;
            }
            var matched = true;
            for (var w = 1; w < words.Length; w++)
            {
                if (i + w >= tokens.Count || tokens[i + w].ToUpperInvariant() != words[w])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return words.Length;
            }
        }
        return 0;
    }

    /// <summary>压缩：整个语句合并成一行（字符串里的空白不动）</summary>
    public static string Compress(string sql)
    {
        var tokens = Tokenize(sql);
        var builder = new StringBuilder(sql.Length);
        foreach (var token in tokens)
        {
            builder.Append(token).Append(' ');
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 词法切分：产出 单词 / 符号 / 字符串字面量（含引号）/ 注释 的序列，
    /// 字符串和注释原样保留，不会被格式化改动。
    /// </summary>
    private static List<string> Tokenize(string sql)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            // 行注释 -- 到行尾
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                if (end < 0) end = sql.Length;
                tokens.Add(sql[i..end].TrimEnd());
                i = end;
                continue;
            }
            // 块注释 /* */
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                tokens.Add(sql[i..end]);
                i = end;
                continue;
            }
            // 字符串字面量 '...'（'' 为转义）；标识符引用 `...` 和 "..."
            if (c is '\'' or '"' or '`')
            {
                var quote = c;
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote && quote != '`') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(sql[start..Math.Min(i, sql.Length)]);
                continue;
            }
            // 空白跳过
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            // 连续的单词（字母/数字/下划线/@/#/$，点号并入：t.col、t.*）
            if (char.IsLetterOrDigit(c) || "_@#$".Contains(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || "_@#$".Contains(sql[i]))) i++;
                while (i < sql.Length && sql[i] == '.' && i + 1 < sql.Length &&
                       (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_' || sql[i + 1] == '*'))
                {
                    i++;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '*')) i++;
                }
                tokens.Add(sql[start..i]);
                continue;
            }
            // 其余符号：连续的符号字符合并成一个 token
            var symbolStart = i;
            while (i < sql.Length && !char.IsWhiteSpace(sql[i]) && !char.IsLetterOrDigit(sql[i]) &&
                   !"'\"`/".Contains(sql[i]) && !"_@#$".Contains(sql[i]) &&
                   !(sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-'))
            {
                i++;
            }
            if (i == symbolStart) i++; // 单字符兜底，保证不死循环
            tokens.Add(sql[symbolStart..i]);
        }
        return tokens;
    }
}

using System.Security.Cryptography;
using System.Text;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DaenLauncher.Views.Tools;

// =====================================================================
// 文本处理分类的 12 个小工具：MD5 / URL / Base64 / 换行互转 / 行行去重 /
// 字符串替换 / 大小写转换 / 文本颠倒 / 驼峰下划线 / 文本排序 / 去首尾空格 / 删空白行。
// 每个工具 = 一个实现 IToolPage 的小类，Build() 里搭界面 + 绑定处理逻辑。
// =====================================================================

/// <summary>字符串 MD5：输入任意文本，实时显示 32/16 位、大小写四种格式中的选中一种</summary>
internal sealed class Md5Tool : IToolPage
{
    private static readonly string[] FormatKeys =
        ["Tools.Md5.Fmt32Upper", "Tools.Md5.Fmt32Lower", "Tools.Md5.Fmt16Upper", "Tools.Md5.Fmt16Lower"];

    private TextBox _input = null!;
    private ComboBox _format = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(100);
        _input.TextChanged += (_, _) => Compute();
        root.Children.Add(_input);

        _format = ToolKit.Combo(FormatKeys);
        _format.SelectionChanged += (_, _) => Compute();
        root.Children.Add(ToolKit.OptionRow("Tools.Md5.OutputFormat", _format));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(60);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Compute()
    {
        var text = _input.Text;
        if (text.Length == 0)
        {
            _output.Text = "";
            return;
        }
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        var hex32 = Convert.ToHexString(bytes); // 32 位大写
        var hex16 = hex32.Substring(8, 16);     // 16 位 = 32 位的第 9~24 位（常见做法）
        _output.Text = ToolKit.SelectedKey(_format) switch
        {
            "Tools.Md5.Fmt32Lower" => hex32.ToLowerInvariant(),
            "Tools.Md5.Fmt16Upper" => hex16,
            "Tools.Md5.Fmt16Lower" => hex16.ToLowerInvariant(),
            _ => hex32
        };
    }
}

/// <summary>URL 编码/解码按钮</summary>
internal sealed class UrlTool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(100);
        root.Children.Add(_input);

        var encode = ToolKit.PrimaryButton("Tools.Url.Encode", (_, _) => Run(true));
        var decode = ToolKit.ActionButton("Tools.Url.Decode", (_, _) => Run(false));
        root.Children.Add(ToolKit.ButtonRow(encode, decode));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(100);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run(bool encode)
    {
        try
        {
            // 编码用 EscapeDataString（整段文本视为一个成分）；解码用 UnescapeDataString（%XX 还原，'+' 原样保留）
            ToolKit.SetOutput(_output, encode
                ? Uri.EscapeDataString(_input.Text)
                : Uri.UnescapeDataString(_input.Text));
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

/// <summary>Base64 编码/解码（UTF-8 文本）</summary>
internal sealed class Base64Tool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(100);
        root.Children.Add(_input);

        var encode = ToolKit.PrimaryButton("Tools.Base64.Encode", (_, _) => Run(true));
        var decode = ToolKit.ActionButton("Tools.Base64.Decode", (_, _) => Run(false));
        root.Children.Add(ToolKit.ButtonRow(encode, decode));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(100);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run(bool encode)
    {
        try
        {
            ToolKit.SetOutput(_output, encode
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(_input.Text))
                : Encoding.UTF8.GetString(Convert.FromBase64String(_input.Text.Trim())));
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

/// <summary>
/// 换行互转 ''：多行文本 ↔ 'AA','BB','CC'（写 SQL 的 in 条件用）。
/// 两个方向都支持，下拉框选择。
/// </summary>
internal sealed class NewlineQuoteTool : IToolPage
{
    private static readonly string[] ModeKeys = ["Tools.Quote.ToQuoted", "Tools.Quote.ToLines"];

    private TextBox _input = null!;
    private ComboBox _mode = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(110);
        root.Children.Add(_input);

        _mode = ToolKit.Combo(ModeKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.ConvertMode", _mode));

        var run = ToolKit.PrimaryButton("Tools.Btn.Convert", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        if (ToolKit.SelectedKey(_mode) == "Tools.Quote.ToQuoted")
        {
            // 多行 → 'AA','BB','CC'：跳过空行
            var items = lines.Where(l => l.Trim().Length > 0).Select(l => $"'{l}'");
            ToolKit.SetOutput(_output, string.Join(",", items));
        }
        else
        {
            // 'AA','BB','CC' → 多行：去掉首尾引号后按 ','  切开
            var text = _input.Text.Trim();
            if (text.StartsWith("'") && text.EndsWith("'") && text.Length >= 2)
            {
                text = text[1..^1];
            }
            var items = text.Split("','");
            ToolKit.SetOutput(_output, ToolKit.JoinLines(items));
        }
    }
}

/// <summary>行行去重：同样的行只保留一个（保持首次出现的顺序）</summary>
internal sealed class LineDedupTool : IToolPage
{
    private TextBox _input = null!;
    private CheckBox _ignoreSpace = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(130);
        root.Children.Add(_input);

        _ignoreSpace = ToolKit.Check("Tools.Dedup.IgnoreSpaces", false);
        root.Children.Add(_ignoreSpace);

        var run = ToolKit.PrimaryButton("Tools.Dedup.Run", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(130);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var line in lines)
        {
            // 勾选"忽略首尾空格"：先剔除首尾空格再去重（输出也用剔除后的行）
            var key = _ignoreSpace.IsChecked == true ? line.Trim() : line;
            if (seen.Add(key))
            {
                result.Add(key);
            }
        }
        ToolKit.SetOutput(_output, ToolKit.JoinLines(result));
    }
}

/// <summary>
/// 字符串替换：查找内容下拉框（换行/空格/自定义 + 自定义输入框），
/// 替换为下拉框（换行/空格/删除/自定义 + 自定义输入框）。
/// </summary>
internal sealed class ReplaceTool : IToolPage
{
    private static readonly string[] FindKeys = ["Tools.Replace.Newline", "Tools.Replace.Space", "Tools.Replace.Custom"];
    private static readonly string[] ReplaceKeys =
        ["Tools.Replace.Newline", "Tools.Replace.Space", "Tools.Replace.Delete", "Tools.Replace.Custom"];

    private TextBox _input = null!;
    private ComboBox _findMode = null!;
    private TextBox _findCustom = null!;
    private ComboBox _replaceMode = null!;
    private TextBox _replaceCustom = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(110);
        root.Children.Add(_input);

        // 查找内容：模式下拉 + 自定义输入框（选"自定义"时才可用）
        _findMode = ToolKit.Combo(FindKeys);
        _findCustom = ToolKit.InputLine(160);
        _findCustom.IsEnabled = false;
        _findMode.SelectionChanged += (_, _) => _findCustom.IsEnabled = IsCustom(_findMode);
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        findRow.Children.Add(_findMode);
        findRow.Children.Add(_findCustom);
        root.Children.Add(ToolKit.OptionRow("Tools.Replace.Find", findRow));

        // 替换为
        _replaceMode = ToolKit.Combo(ReplaceKeys);
        _replaceCustom = ToolKit.InputLine(160);
        _replaceCustom.IsEnabled = false;
        _replaceMode.SelectionChanged += (_, _) => _replaceCustom.IsEnabled = IsCustom(_replaceMode);
        var replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        replaceRow.Children.Add(_replaceMode);
        replaceRow.Children.Add(_replaceCustom);
        root.Children.Add(ToolKit.OptionRow("Tools.Replace.With", replaceRow));

        var run = ToolKit.PrimaryButton("Tools.Btn.Replace", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private static bool IsCustom(ComboBox combo) => ToolKit.SelectedKey(combo) == "Tools.Replace.Custom";

    /// <summary>按模式下拉框解析出实际要查找/替换的字符串（"删除" = 替换为空）</summary>
    private static string Resolve(ComboBox combo, TextBox custom)
    {
        var text = ToolKit.SelectedKey(combo) switch
        {
            "Tools.Replace.Newline" => "\n",
            "Tools.Replace.Space" => " ",
            _ => custom.Text
        };
        // 输入框里写 \n 时按真实换行处理（写换行符不方便）
        return text.Replace("\\n", "\n");
    }

    private void Run()
    {
        var find = Resolve(_findMode, _findCustom);
        if (find.Length == 0)
        {
            ToolKit.SetOutput(_output, LocalizationService.Tr("Tools.Replace.ErrEmpty"), isError: true);
            return;
        }
        // 输入先把换行归一化成 LF 再替换（TextBox 里是 CR，CRLF 会让"查找:换行"只匹配到一半）
        var text = string.Join("\n", ToolKit.SplitLines(_input.Text));
        var replaceWith = ToolKit.SelectedKey(_replaceMode) == "Tools.Replace.Delete" ? "" : Resolve(_replaceMode, _replaceCustom);
        ToolKit.SetOutput(_output, text.Replace(find, replaceWith));
    }
}

/// <summary>大小写转换：全部大写 / 全部小写（只影响英文字母）</summary>
internal sealed class CaseTool : IToolPage
{
    private static readonly string[] ModeKeys = ["Tools.Case.Upper", "Tools.Case.Lower"];

    private TextBox _input = null!;
    private ComboBox _mode = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(110);
        root.Children.Add(_input);

        _mode = ToolKit.Combo(ModeKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.ConvertMode", _mode));

        var run = ToolKit.PrimaryButton("Tools.Btn.Convert", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var text = _input.Text;
        ToolKit.SetOutput(_output, ToolKit.SelectedKey(_mode) == "Tools.Case.Upper"
            ? text.ToUpperInvariant()
            : text.ToLowerInvariant());
    }
}

/// <summary>
/// 文本颠倒：前后颠倒（整段文字反过来）或行行颠倒（每行内部字符前后反过来，行顺序不变）。
/// 例：前后颠倒 "你好ABC123" → "321CBA好你"。
/// </summary>
internal sealed class ReverseTool : IToolPage
{
    private static readonly string[] ModeKeys = ["Tools.Reverse.All", "Tools.Reverse.PerLine"];

    private TextBox _input = null!;
    private ComboBox _mode = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(110);
        root.Children.Add(_input);

        _mode = ToolKit.Combo(ModeKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.ConvertMode", _mode));

        var run = ToolKit.PrimaryButton("Tools.Btn.Convert", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private static string ReverseString(string text)
    {
        // 用字符数组反转（char 层面反转，对中文安全）
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private void Run()
    {
        if (ToolKit.SelectedKey(_mode) == "Tools.Reverse.All")
        {
            ToolKit.SetOutput(_output, ReverseString(_input.Text));
            return;
        }
        // 行行颠倒：只颠倒行与行的顺序，行内的字符不动（需求明确）
        var lines = ToolKit.SplitLines(_input.Text);
        Array.Reverse(lines);
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines));
    }
}

/// <summary>驼峰 / 下划线命名互转：userName ↔ user_name（连续大写也按常见规则拆分）</summary>
internal sealed class CamelSnakeTool : IToolPage
{
    private static readonly string[] ModeKeys = ["Tools.Naming.ToSnake", "Tools.Naming.ToCamel"];

    private TextBox _input = null!;
    private ComboBox _mode = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(110);
        root.Children.Add(_input);

        _mode = ToolKit.Combo(ModeKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.ConvertMode", _mode));

        var run = ToolKit.PrimaryButton("Tools.Btn.Convert", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        var toSnake = ToolKit.SelectedKey(_mode) == "Tools.Naming.ToSnake";
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines.Select(l => toSnake ? ToSnake(l) : ToCamel(l))));
    }

    /// <summary>驼峰 → 下划线：在每个"小写/数字 → 大写"的边界前插入下划线，再整体转小写</summary>
    private static string ToSnake(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])
                || (i + 1 < text.Length && char.IsLower(text[i + 1]))))
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>下划线 → 驼峰：去掉下划线，下划线后的首字母大写（首段保持小写 = 小驼峰）</summary>
    private static string ToCamel(string text)
    {
        var parts = text.Split('_');
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }
            builder.Append(i == 0
                ? char.ToLowerInvariant(parts[i][0]) + parts[i][1..]
                : char.ToUpperInvariant(parts[i][0]) + parts[i][1..]);
        }
        return builder.ToString();
    }
}

/// <summary>文本排序：首字母/首数值/长度 升降序 + 随机</summary>
internal sealed class SortTool : IToolPage
{
    private static readonly string[] ModeKeys =
        ["Tools.Sort.AlphaAsc", "Tools.Sort.AlphaDesc", "Tools.Sort.NumAsc", "Tools.Sort.NumDesc",
         "Tools.Sort.LenAsc", "Tools.Sort.LenDesc", "Tools.Sort.Random"];

    private TextBox _input = null!;
    private ComboBox _mode = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(130);
        root.Children.Add(_input);

        _mode = ToolKit.Combo(ModeKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.ConvertMode", _mode));

        var run = ToolKit.PrimaryButton("Tools.Sort.Run", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(130);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    /// <summary>提取行首的数值（如 "3 apples" → 3），取不到按 0 处理</summary>
    private static double LeadingNumber(string line)
    {
        var text = line.TrimStart();
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.' || text[end] == '-'))
        {
            end++;
        }
        return end > 0 && double.TryParse(text[..end], out var value) ? value : 0;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text).ToList();
        switch (ToolKit.SelectedKey(_mode))
        {
            case "Tools.Sort.AlphaAsc":
                lines.Sort(string.Compare);
                break;
            case "Tools.Sort.AlphaDesc":
                lines.Sort((a, b) => string.Compare(b, a));
                break;
            case "Tools.Sort.NumAsc":
                lines.Sort((a, b) => LeadingNumber(a).CompareTo(LeadingNumber(b)));
                break;
            case "Tools.Sort.NumDesc":
                lines.Sort((a, b) => LeadingNumber(b).CompareTo(LeadingNumber(a)));
                break;
            case "Tools.Sort.LenAsc":
                lines.Sort((a, b) => a.Length.CompareTo(b.Length));
                break;
            case "Tools.Sort.LenDesc":
                lines.Sort((a, b) => b.Length.CompareTo(a.Length));
                break;
            case "Tools.Sort.Random":
            {
                // Shuffle 作用在数组/Span 上，List 不能直接传
                var array = lines.ToArray();
                Random.Shared.Shuffle(array);
                lines.Clear();
                lines.AddRange(array);
                break;
            }
        }
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines));
    }
}

/// <summary>去除首尾空格：每行的首尾空白都剔除</summary>
internal sealed class TrimTool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(130);
        root.Children.Add(_input);

        var run = ToolKit.PrimaryButton("Tools.Trim.Run", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(130);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines.Select(l => l.Trim())));
    }
}

/// <summary>删除空白行：去掉整行都是空白（或空）的行</summary>
internal sealed class RemoveBlankLinesTool : IToolPage
{
    private TextBox _input = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(130);
        root.Children.Add(_input);

        var run = ToolKit.PrimaryButton("Tools.RemoveBlank.Run", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(130);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines.Where(l => l.Trim().Length > 0)));
    }
}

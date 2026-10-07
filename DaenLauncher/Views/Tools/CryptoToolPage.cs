using System.Text;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace DaenLauncher.Views.Tools;

/// <summary>算法枚举：SM4 / AES（同一个加密页面复用，按算法切换引擎）</summary>
internal enum CryptoAlgo
{
    /// <summary>国密 SM4（密钥固定 16 字节）</summary>
    Sm4,

    /// <summary>AES（密钥 16/24/32 字节 = 128/192/256 位）</summary>
    Aes
}

/// <summary>
/// 加解密工具页面（SM4 / AES 共用一套界面，只差引擎和标题）。
/// 选项：原文格式、加密模式、密钥格式、IV 格式、填充方式、输出格式、输出转大写、
/// 多行模式（一行一个）、复杂文本处理（支持 Tab 分隔的两字段数据）。
/// </summary>
internal sealed class CryptoToolPage : IToolPage
{
    // ===== 选项数组的语言键（顺序即下拉框顺序） =====
    private static readonly string[] TextFormatKeys = ["Tools.Enc.Utf8", "Tools.Enc.Hex", "Tools.Enc.Base64"];
    private static readonly string[] ModeKeys =
        ["Tools.Enc.CBC", "Tools.Enc.ECB", "Tools.Enc.CFB", "Tools.Enc.OFB", "Tools.Enc.CTR", "Tools.Enc.GCM"];
    private static readonly string[] PaddingKeys =
        ["Tools.Enc.Pkcs5", "Tools.Enc.Pkcs7", "Tools.Enc.Zeros", "Tools.Enc.Iso10126",
         "Tools.Enc.AnsiX923", "Tools.Enc.Iso7816", "Tools.Enc.NoPadding"];
    private static readonly string[] OutputFormatKeys = ["Tools.Enc.Hex", "Tools.Enc.Base64"];

    private readonly CryptoAlgo _algo;

    // ===== 界面控件（Build 时创建） =====
    private ComboBox _textFormat = null!;      // 原文格式
    private ComboBox _mode = null!;            // 加密模式
    private TextBox _keyBox = null!;           // 密钥
    private ComboBox _keyFormat = null!;       // 密钥格式
    private TextBox _ivBox = null!;            // IV
    private ComboBox _ivFormat = null!;        // IV 格式
    private StackPanel _ivRow = null!;         // IV 整行（ECB 模式下隐藏）
    private Microsoft.UI.Xaml.Controls.TextBlock _ivLabel = null!; // IV 标签（随整行一起隐藏）
    private ComboBox _padding = null!;         // 填充方式
    private ComboBox _outputFormat = null!;    // 输出格式
    private CheckBox _upperCheck = null!;      // 输出转大写
    private CheckBox _multiLineCheck = null!;  // 多行模式
    private CheckBox _tabFieldCheck = null!;   // 复杂文本处理
    private TextBox _input = null!;            // 输入
    private TextBox _output = null!;           // 输出

    public CryptoToolPage(CryptoAlgo algo)
    {
        _algo = algo;
    }

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        // ===== 输入区 =====
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(120);
        root.Children.Add(_input);

        // ===== 参数区（紧凑网格：同一行左右两组"标签+控件"，填充/输出紧跟在原文/模式右边） =====
        _textFormat = ToolKit.Combo(TextFormatKeys);
        _mode = ToolKit.Combo(ModeKeys);
        _mode.SelectionChanged += (_, _) => UpdateIvVisibility();
        _keyBox = ToolKit.InputLine(150, "Tools.Enc.KeyPlaceholder");
        _keyFormat = ToolKit.Combo(TextFormatKeys);
        _ivBox = ToolKit.InputLine(150);
        _ivFormat = ToolKit.Combo(TextFormatKeys);
        _padding = ToolKit.Combo(PaddingKeys);
        _outputFormat = ToolKit.Combo(OutputFormatKeys);

        // 密钥格式紧跟密钥输入框，IV 格式紧跟 IV 输入框（需求：放同一行）
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyRow.Children.Add(_keyBox);
        keyRow.Children.Add(_keyFormat);
        var ivRowInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ivRowInner.Children.Add(_ivBox);
        ivRowInner.Children.Add(_ivFormat);

        var paramGrid = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnSpacing = 12, RowSpacing = 8 };
        for (var i = 0; i < 4; i++)
        {
            // 列：标签(110) | 控件 | 标签(110) | 控件
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i % 2 == 0 ? new GridLength(100) : new GridLength(1, GridUnitType.Auto)
            });
        }
        paramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddParam(int row, int col, string labelKey, FrameworkElement control, int span = 1)
        {
            var label = ToolKit.Label(labelKey);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            paramGrid.Children.Add(label);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(control, row);
            Grid.SetColumn(control, col + 1);
            if (span > 1)
            {
                Grid.SetColumnSpan(control, span);
            }
            paramGrid.Children.Add(control);
        }

        AddParam(0, 0, "Tools.Enc.TextFormat", _textFormat);
        AddParam(0, 2, "Tools.Enc.Padding", _padding);
        AddParam(1, 0, "Tools.Enc.Mode", _mode);
        AddParam(1, 2, "Tools.Enc.OutputFormat", _outputFormat);
        AddParam(2, 0, "Tools.Enc.Key", keyRow, span: 3);
        _ivRow = new StackPanel();
        _ivRow.Children.Add(ivRowInner);
        Grid.SetRow(_ivRow, 3);
        Grid.SetColumn(_ivRow, 1);
        Grid.SetColumnSpan(_ivRow, 3);
        paramGrid.Children.Add(_ivRow);
        _ivLabel = ToolKit.Label("Tools.Enc.Iv");
        var ivLabel = _ivLabel;
        Grid.SetRow(ivLabel, 3);
        Grid.SetColumn(ivLabel, 0);
        paramGrid.Children.Add(ivLabel);
        root.Children.Add(paramGrid);

        // ===== 操作区：复选框（输出转大写在最左，需求）+ 加密 / 解密按钮 =====
        _upperCheck = ToolKit.Check("Tools.Enc.Uppercase", false);
        _multiLineCheck = ToolKit.Check("Tools.Enc.MultiLine", false);
        _tabFieldCheck = ToolKit.Check("Tools.Enc.TabField", false);
        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 0, 0, 4) };
        optionsRow.Children.Add(_upperCheck);
        optionsRow.Children.Add(_multiLineCheck);
        optionsRow.Children.Add(_tabFieldCheck);
        root.Children.Add(optionsRow);

        var encryptButton = ToolKit.PrimaryButton("Tools.Enc.Encrypt", (_, _) => RunCore(true));
        var decryptButton = ToolKit.ActionButton("Tools.Enc.Decrypt", (_, _) => RunCore(false));
        root.Children.Add(ToolKit.ButtonRow(encryptButton, decryptButton));

        // ===== 结果区 =====
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(120);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));

        UpdateIvVisibility();
        return root;
    }

    /// <summary>ECB 模式没有 IV，隐藏 IV 整行和标签</summary>
    private void UpdateIvVisibility()
    {
        var visible = ToolKit.SelectedKey(_mode) != "Tools.Enc.ECB";
        _ivRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _ivLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>执行加密/解密（含多行模式、复杂文本处理的分发）</summary>
    private void RunCore(bool encrypt)
    {
        try
        {
            var key = DecodeBytes(_keyBox.Text, SelectedTextFormat(_keyFormat));
            if (key.Length == 0)
            {
                ToolKit.SetOutput(_output, LocalizationService.Tr("Tools.Enc.ErrKeyEmpty"), isError: true);
                return;
            }
            var validKeyLengths = _algo == CryptoAlgo.Aes ? [16, 24, 32] : new[] { 16 };
            if (!validKeyLengths.Contains(key.Length))
            {
                ToolKit.SetOutput(_output, string.Format(
                    LocalizationService.Tr(_algo == CryptoAlgo.Aes
                        ? "Tools.Enc.ErrKeyLengthAes" : "Tools.Enc.ErrKeyLengthSm4"),
                    key.Length), isError: true);
                return;
            }

            var modeKey = ToolKit.SelectedKey(_mode);
            var paddingKey = ToolKit.SelectedKey(_padding);
            var iv = modeKey == "Tools.Enc.ECB"
                ? Array.Empty<byte>()
                : DecodeBytes(_ivBox.Text, SelectedTextFormat(_ivFormat));
            var upper = _upperCheck.IsChecked == true;
            var outputHex = ToolKit.SelectedKey(_outputFormat) == "Tools.Enc.Hex";

            string Transform(string text) => encrypt
                ? CryptoEngine.Encrypt(_algo, modeKey, paddingKey, key, iv,
                    DecodeBytes(text, SelectedTextFormat(_textFormat)), upper, outputHex)
                : CryptoEngine.Decrypt(_algo, modeKey, paddingKey, key, iv, text,
                    SelectedTextFormat(_outputFormat), SelectedTextFormat(_textFormat), upper);

            var result = _multiLineCheck.IsChecked == true
                ? RunMultiLine(_input.Text, _tabFieldCheck.IsChecked == true, Transform)
                : Transform(_input.Text);

            ToolKit.SetOutput(_output, result);
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }

    /// <summary>
    /// 多行模式：一行一个数据，各自处理后再拼回多行；
    /// 复杂文本处理：每行可以是 "字段1<TAB>字段2"，两个字段分别处理、保持 Tab 分隔。
    /// </summary>
    private static string RunMultiLine(string input, bool tabField, Func<string, string> transform)
    {
        var lines = ToolKit.SplitLines(input);
        var results = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            // 行首尾空白先剔除（复制粘贴常见的尾随空格会导致 Hex/Base64 解码失败）
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                results[i] = ""; // 空行原样保留
                continue;
            }
            if (tabField && line.Contains('\t'))
            {
                var fields = line.Split('\t');
                for (var f = 0; f < fields.Length; f++)
                {
                    fields[f] = SafeTransform(fields[f], transform, i + 1);
                }
                results[i] = string.Join("\t", fields);
            }
            else
            {
                results[i] = SafeTransform(line, transform, i + 1);
            }
        }
        return ToolKit.JoinLines(results);
    }

    /// <summary>单行处理失败时不中断整体，输出带行号的错误标记</summary>
    private static string SafeTransform(string text, Func<string, string> transform, int lineNumber)
    {
        try
        {
            return transform(text);
        }
        catch (Exception ex)
        {
            return $"[第{lineNumber}行失败: {ex.Message}]";
        }
    }

    /// <summary>取"原文格式"下拉框当前值对应的编码枚举</summary>
    private static TextEncoding SelectedTextFormat(ComboBox combo) => ToolKit.SelectedKey(combo) switch
    {
        "Tools.Enc.Hex" => TextEncoding.Hex,
        "Tools.Enc.Base64" => TextEncoding.Base64,
        _ => TextEncoding.Utf8
    };

    /// <summary>按指定格式把文字解码成字节数组</summary>
    private static byte[] DecodeBytes(string text, TextEncoding format) => format switch
    {
        TextEncoding.Hex => Convert.FromHexString(FilterHex(text)),
        TextEncoding.Base64 => Convert.FromBase64String(RegexReplaceWhitespace(text)),
        _ => Encoding.UTF8.GetBytes(text)
    };

    /// <summary>Hex 容错：去掉空格和冒号（方便粘贴 "AA BB" / "AA:BB" 格式）</summary>
    private static string FilterHex(string text) =>
        text.Trim().Replace(" ", "").Replace(":", "");

    private static string RegexReplaceWhitespace(string text) => text.Trim();
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DaenLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DaenLauncher.Views.Tools;

// =====================================================================
// 其他常用分类：行行求和 / curl 命令生成 / 批量提取文件名 / 密码生成 /
// UUID 生成 / 时间戳。（颜色选择器逻辑较长，单独放在 ColorPickerTool.cs）
// =====================================================================

/// <summary>
/// 行行求和：一行一个数字求和。支持 ¥/$ 等货币符号、千分位逗号（中英文）、
/// 元/角/分等后缀单位、正负号；无法解析的行自动跳过。
/// </summary>
internal sealed class LineSumTool : IToolPage
{
    /// <summary>解析数字前剔除的字符：货币符号 + 千分位逗号（中英文）+ 中文单位</summary>
    private const string StripChars = "¥￥$,， 元分角 \t";

    private TextBox _input = null!;
    private TextBox _output = null!;
    private TextBlock _info = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();
        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Input"));
        _input = ToolKit.InputArea(140);
        root.Children.Add(_input);

        var run = ToolKit.PrimaryButton("Tools.LineSum.Run", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(run));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(50);
        root.Children.Add(_output);
        _info = new TextBlock { FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 4) };
        root.Children.Add(_info);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var lines = ToolKit.SplitLines(_input.Text);
        decimal sum = 0;
        var parsed = 0;
        var skipped = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue; // 空行不算"跳过"
            }
            // 先剔除首尾的货币符号/中文单位/空格，再去掉中间的千分位逗号（中英文）后解析
            var cleaned = line.Trim(StripChars.ToCharArray())
                .Replace(",", "").Replace("，", "");
            if (decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.CurrentCulture, out var value))
            {
                sum += value;
                parsed++;
            }
            else
            {
                skipped++;
            }
        }
        _output.Text = sum.ToString(CultureInfo.CurrentCulture);
        _info.Text = string.Format(LocalizationService.Tr("Tools.LineSum.Info"), parsed, skipped);
    }
}

/// <summary>
/// curl 命令生成：按请求地址/方法/请求头/请求体/代理设置拼出可直接执行的 curl 命令。
/// 输出格式与需求示例一致：curl -v [-x 代理] [-U "账号:密码"] -X 方法 "地址" -H "头" -d '请求体'
/// </summary>
internal sealed class CurlTool : IToolPage
{
    private static readonly string[] MethodKeys =
        ["Tools.Curl.GET", "Tools.Curl.POST", "Tools.Curl.PUT", "Tools.Curl.DELETE",
         "Tools.Curl.PATCH", "Tools.Curl.HEAD", "Tools.Curl.OPTIONS"];
    private static readonly string[] ProxyTypeKeys = ["Tools.Curl.ProxyHTTP", "Tools.Curl.ProxySocks5"];

    private TextBox _url = null!;
    private ComboBox _method = null!;
    private TextBox _headers = null!;
    private TextBox _body = null!;
    private CheckBox _useProxy = null!;
    private ComboBox _proxyType = null!;
    private TextBox _proxyHost = null!;
    private TextBox _proxyPort = null!;
    private TextBox _proxyUser = null!;
    private TextBox _proxyPassword = null!;
    private StackPanel _proxyPanel = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        // 请求地址/请求头/请求体用可拉伸行（跟着窗口变宽），且请求头/请求体可拖右下角调大小
        _url = ToolKit.InputLine(0, "Tools.Curl.UrlPlaceholder");
        root.Children.Add(ToolKit.OptionRowStretch("Tools.Curl.Url", _url));

        _method = ToolKit.Combo(MethodKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.Curl.Method", _method));

        _headers = ToolKit.InputArea(90, "Tools.Curl.HeadersPlaceholder");
        root.Children.Add(ToolKit.OptionRowStretch("Tools.Curl.Headers", ToolKit.ResizableHost(_headers, 90)));

        _body = ToolKit.InputArea(90, "Tools.Curl.BodyPlaceholder");
        root.Children.Add(ToolKit.OptionRowStretch("Tools.Curl.Body", ToolKit.ResizableHost(_body, 90)));

        // 代理开关 + 代理参数（勾选"使用代理"才显示参数行）
        _useProxy = ToolKit.Check("Tools.Curl.UseProxy", false, _ =>
        {
            _proxyPanel.Visibility = _useProxy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        });
        root.Children.Add(_useProxy);

        _proxyType = ToolKit.Combo(ProxyTypeKeys);
        _proxyHost = ToolKit.InputLine(150);
        _proxyPort = ToolKit.InputLine(60);
        var hostRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hostRow.Children.Add(_proxyHost);
        hostRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center });
        hostRow.Children.Add(_proxyPort);
        _proxyUser = ToolKit.InputLine(120);
        _proxyPassword = ToolKit.InputLine(120);
        _proxyPanel = new StackPanel { Spacing = 0, Margin = new Thickness(24, 0, 0, 0), Visibility = Visibility.Collapsed };
        _proxyPanel.Children.Add(ToolKit.OptionRow("Tools.Curl.ProxyType", _proxyType));
        _proxyPanel.Children.Add(ToolKit.OptionRow("Tools.Curl.ProxyAddr", hostRow));
        _proxyPanel.Children.Add(ToolKit.OptionRow("Tools.Curl.ProxyUser", _proxyUser));
        _proxyPanel.Children.Add(ToolKit.OptionRow("Tools.Curl.ProxyPassword", _proxyPassword));
        root.Children.Add(_proxyPanel);

        var generate = ToolKit.PrimaryButton("Tools.Curl.Generate", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(generate));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(110);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var command = new StringBuilder("curl -v");

        // 代理：-x http://host:port 或 socks5://host:port；有账号密码再加 -U "user:pass"
        if (_useProxy.IsChecked == true)
        {
            var scheme = ToolKit.SelectedKey(_proxyType) == "Tools.Curl.ProxySocks5" ? "socks5" : "http";
            var host = _proxyHost.Text.Trim();
            var port = _proxyPort.Text.Trim();
            if (host.Length > 0)
            {
                command.Append(" -x ").Append(scheme).Append("://").Append(host);
                if (port.Length > 0)
                {
                    command.Append(':').Append(port);
                }
            }
            var user = _proxyUser.Text.Trim();
            if (user.Length > 0)
            {
                command.Append(" -U \"").Append(user).Append(':').Append(_proxyPassword.Text).Append('"');
            }
        }

        command.Append(" -X ").Append(ToolKit.SelectedKey(_method).Split('.')[^1]);
        command.Append(" \"").Append(_url.Text.Trim()).Append('"');

        // 请求头：一行一个 "name: value"，逐个输出 -H
        var headerLines = ToolKit.SplitLines(_headers.Text);
        foreach (var headerLine in headerLines)
        {
            var trimmed = headerLine.Trim();
            if (trimmed.Length > 0)
            {
                command.Append(" -H \"").Append(trimmed).Append('"');
            }
        }

        // 请求体：-d '...'（有内容才输出）
        var body = _body.Text.Trim();
        if (body.Length > 0)
        {
            command.Append(" -d '").Append(body.Replace("'", "'\\''")).Append('\'');
        }

        ToolKit.SetOutput(_output, command.ToString());
    }
}

/// <summary>
/// 批量提取文件名：勾选"保留完整路径/保留文件拓展名"，拖拽文件或文件夹到输入框，
/// 自动列出文件（文件夹会递归其中的文件），一行一个。
/// </summary>
internal sealed class FileNameTool : IToolPage
{
    private CheckBox _keepPath = null!;
    private CheckBox _keepExtension = null!;
    private CheckBox _recurse = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 0, 0, 6) };
        _keepPath = ToolKit.Check("Tools.FileNames.KeepPath", true);
        _keepExtension = ToolKit.Check("Tools.FileNames.KeepExtension", true);
        _recurse = ToolKit.Check("Tools.FileNames.Recurse", true);
        checks.Children.Add(_keepPath);
        checks.Children.Add(_keepExtension);
        checks.Children.Add(_recurse);
        root.Children.Add(checks);

        // 拖放区：TextBox 外面包一层 Grid 接收拖放（TextBox 自身不接收文件拖放）
        _output = ToolKit.OutputArea(190);
        _output.PlaceholderText = LocalizationService.Tr("Tools.FileNames.Placeholder");
        var dropHost = new Grid
        {
            AllowDrop = true,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        dropHost.Children.Add(_output);
        dropHost.DragOver += OnDragOver;
        dropHost.Drop += OnDrop;
        root.Children.Add(dropHost);

        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            var deferral = e.GetDeferral();
            // 在任何 await 之前先读取勾选状态（避免异步过程中状态变化的干扰）
            var recurse = _recurse.IsChecked == true;
            var paths = new List<string>();
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    var path = item.Path;
                    if (File.Exists(path))
                    {
                        paths.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        // 文件夹：勾选"遍历子目录"才递归，否则只取第一层（System.IO 语义完全确定）
                        var options = new EnumerationOptions
                        {
                            RecurseSubdirectories = recurse,
                            IgnoreInaccessible = true
                        };
                        paths.AddRange(Directory.EnumerateFiles(path, "*", options));
                    }
                }
            }
            deferral.Complete();

            // 【临时调试日志，定位后删除】
            try
            {
                var allRepr = string.Join(" | ", paths);
                File.AppendAllText(Path.Combine(DataPathService.DataRoot, "uia_debug.log"),
                    "OnDrop recurse=" + recurse + " count=" + paths.Count + " paths=[" + allRepr + "]" + Environment.NewLine);
            }
            catch { }

            var keepPath = _keepPath.IsChecked == true;
            var keepExtension = _keepExtension.IsChecked == true;
            var lines = paths.Select(path =>
            {
                var text = keepPath ? path : Path.GetFileName(path);
                if (!keepExtension)
                {
                    text = Path.ChangeExtension(text, null);
                }
                return text;
            });
            ToolKit.SetOutput(_output, ToolKit.JoinLines(lines));
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

/// <summary>密码生成：勾选字符组合、设置长度和数量，批量生成随机密码</summary>
internal sealed class PasswordTool : IToolPage
{
    /// <summary>可选的四类字符（需求指定的符号集）</summary>
    private const string Digits = "0123456789";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Symbols = "~!@#$%^&*()-+_=,.";

    private CheckBox _digits = null!;
    private CheckBox _lower = null!;
    private CheckBox _upper = null!;
    private CheckBox _symbols = null!;
    private NumberBox _length = null!;
    private NumberBox _count = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(0, 0, 0, 6) };
        _digits = ToolKit.Check("Tools.Pw.Digits", true);
        _lower = ToolKit.Check("Tools.Pw.Lower", true);
        _upper = ToolKit.Check("Tools.Pw.Upper", true);
        _symbols = ToolKit.Check("Tools.Pw.Symbols", true);
        checks.Children.Add(_digits);
        checks.Children.Add(_lower);
        checks.Children.Add(_upper);
        checks.Children.Add(_symbols);
        root.Children.Add(checks);

        _length = ToolKit.NumberBox(4, 128, 16);
        _count = ToolKit.NumberBox(1, 200, 5);
        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        settingsRow.Children.Add(ToolKit.OptionRow("Tools.Pw.Length", _length, 90));
        settingsRow.Children.Add(ToolKit.OptionRow("Tools.Pw.Count", _count, 90));
        root.Children.Add(settingsRow);

        var generate = ToolKit.PrimaryButton("Tools.Btn.Generate", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(generate));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(140);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var pool = new StringBuilder();
        if (_digits.IsChecked == true) pool.Append(Digits);
        if (_lower.IsChecked == true) pool.Append(Lowercase);
        if (_upper.IsChecked == true) pool.Append(Uppercase);
        if (_symbols.IsChecked == true) pool.Append(Symbols);
        if (pool.Length == 0)
        {
            ToolKit.SetOutput(_output, LocalizationService.Tr("Tools.Pw.ErrNoCharset"), isError: true);
            return;
        }

        var length = Math.Max(4, (int)Math.Round(_length.Value));
        var count = Math.Max(1, (int)Math.Round(_count.Value));
        var chars = pool.ToString();
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var builder = new StringBuilder(length);
            for (var j = 0; j < length; j++)
            {
                // RandomNumberGenerator 是加密级随机数，比 Random 更适合生成密码
                builder.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
            }
            lines.Add(builder.ToString());
        }
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines));
    }
}

/// <summary>UUID 生成：可选横杠/大小写/数量，批量生成（Guid v4）</summary>
internal sealed class UuidTool : IToolPage
{
    private static readonly string[] CaseKeys = ["Tools.Uuid.Lower", "Tools.Uuid.Upper"];

    private CheckBox _keepHyphen = null!;
    private ComboBox _caseMode = null!;
    private NumberBox _count = null!;
    private TextBox _output = null!;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        _keepHyphen = ToolKit.Check("Tools.Uuid.KeepHyphen", true);
        _caseMode = ToolKit.Combo(CaseKeys);
        _count = ToolKit.NumberBox(1, 500, 5);

        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        settingsRow.Children.Add(_keepHyphen);
        settingsRow.Children.Add(ToolKit.OptionRow("Tools.Uuid.Case", _caseMode, 90));
        settingsRow.Children.Add(ToolKit.OptionRow("Tools.Pw.Count", _count, 90));
        root.Children.Add(settingsRow);

        var generate = ToolKit.PrimaryButton("Tools.Btn.Generate", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(generate));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(140);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    private void Run()
    {
        var count = Math.Max(1, (int)Math.Round(_count.Value));
        var upper = ToolKit.SelectedKey(_caseMode) == "Tools.Uuid.Upper";
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var text = Guid.NewGuid().ToString();
            if (_keepHyphen.IsChecked != true)
            {
                text = text.Replace("-", "");
            }
            lines.Add(upper ? text.ToUpperInvariant() : text);
        }
        ToolKit.SetOutput(_output, ToolKit.JoinLines(lines));
    }
}

/// <summary>
/// 时间戳工具：
/// - 顶部实时显示当前秒级/毫秒级时间戳（每秒刷新一次）；
/// - 下方互转：时间戳 → 时间 或 时间 → 时间戳，可选"到秒/到毫秒"。
/// </summary>
internal sealed class TimestampTool : IToolPage
{
    private static readonly string[] DirectionKeys = ["Tools.Ts.ToTime", "Tools.Ts.ToTimestamp"];
    private static readonly string[] PrecisionKeys = ["Tools.Ts.Seconds", "Tools.Ts.Milliseconds"];

    /// <summary>当前时间的可选格式（下拉框直接展示格式字符串，不做翻译）</summary>
    private static readonly string[] TimeFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy年MM月dd日 HH:mm:ss",
         "yyyyMMddHHmmss", "yyyy-MM-dd", "HH:mm:ss"];

    private TextBlock _nowSeconds = null!;
    private TextBlock _nowMilliseconds = null!;
    private ComboBox _timeFormat = null!;
    private TextBox _input = null!;
    private ComboBox _direction = null!;
    private ComboBox _precision = null!;
    private TextBox _output = null!;
    private DispatcherTimer? _timer;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        // 当前时间戳（每秒刷新）+ 三个"复制当前值"按钮（需求：秒/毫秒/当前时间）
        _nowSeconds = new TextBlock { FontSize = 14 };
        _nowMilliseconds = new TextBlock { FontSize = 14, Margin = new Thickness(0, 2, 0, 8) };
        root.Children.Add(_nowSeconds);
        root.Children.Add(_nowMilliseconds);

        _timeFormat = ToolKit.Combo(TimeFormats);
        var copySeconds = ToolKit.ActionButton("Tools.Ts.CopySeconds",
            (_, _) => CopyText(DateTimeOffset.Now.ToUnixTimeSeconds().ToString()));
        var copyMilliseconds = ToolKit.ActionButton("Tools.Ts.CopyMilliseconds",
            (_, _) => CopyText(DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString()));
        var copyTime = ToolKit.ActionButton("Tools.Ts.CopyTime",
            (_, _) => CopyText(DateTime.Now.ToString(ToolKit.SelectedKey(_timeFormat))));
        root.Children.Add(ToolKit.ButtonRow(copySeconds, copyMilliseconds, copyTime));
        root.Children.Add(ToolKit.OptionRow("Tools.Ts.TimeFormat", _timeFormat));

        UpdateNow();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateNow();
        _timer.Start();

        _input = ToolKit.InputLine(0, "Tools.Ts.InputPlaceholder");
        root.Children.Add(ToolKit.OptionRow("Tools.Ts.Input", _input));

        _direction = ToolKit.Combo(DirectionKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.Ts.Direction", _direction));

        _precision = ToolKit.Combo(PrecisionKeys);
        root.Children.Add(ToolKit.OptionRow("Tools.Ts.Precision", _precision));

        var convert = ToolKit.PrimaryButton("Tools.Btn.Convert", (_, _) => Run());
        root.Children.Add(ToolKit.ButtonRow(convert));

        root.Children.Add(ToolKit.SectionTitle("Tools.Enc.Output"));
        _output = ToolKit.OutputArea(50);
        root.Children.Add(_output);
        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _output.Text)));
        return root;
    }

    /// <summary>刷新顶部的当前时间戳显示</summary>
    private void UpdateNow()
    {
        var now = DateTimeOffset.Now;
        _nowSeconds.Text = string.Format(LocalizationService.Tr("Tools.Ts.NowSeconds"), now.ToUnixTimeSeconds());
        _nowMilliseconds.Text = string.Format(LocalizationService.Tr("Tools.Ts.NowMilliseconds"), now.ToUnixTimeMilliseconds());
    }

    /// <summary>把文字复制到剪贴板（复制按钮的 Provider 在点击时才求值，取到的总是"当下"的值）</summary>
    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private void Run()
    {
        try
        {
            var toSeconds = ToolKit.SelectedKey(_precision) == "Tools.Ts.Seconds";
            if (ToolKit.SelectedKey(_direction) == "Tools.Ts.ToTime")
            {
                // 时间戳 → 时间（支持秒级 10 位和毫秒级 13 位，自动识别位数）
                var raw = _input.Text.Trim();
                if (!long.TryParse(raw, out var timestamp))
                {
                    throw new InvalidOperationException(LocalizationService.Tr("Tools.Ts.ErrNumber"));
                }
                var milliseconds = timestamp.ToString().Length <= 10 ? timestamp * 1000 : timestamp;
                var time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
                // "时间戳 → 时间"也用时间格式下拉框里选的格式
                ToolKit.SetOutput(_output, time.ToString(ToolKit.SelectedKey(_timeFormat)));
            }
            else
            {
                // 时间 → 时间戳
                var time = DateTime.Parse(_input.Text.Trim(), CultureInfo.CurrentCulture);
                var offset = new DateTimeOffset(time);
                ToolKit.SetOutput(_output, toSeconds
                    ? offset.ToUnixTimeSeconds().ToString()
                    : offset.ToUnixTimeMilliseconds().ToString());
            }
        }
        catch (Exception ex)
        {
            ToolKit.SetOutput(_output, ex.Message, isError: true);
        }
    }
}

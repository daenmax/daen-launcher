using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DaenLauncher.Views.Tools;

// =====================================================================
// 其他常用（网络/编码类）：
// - 已连接 WiFi 密码查看：列出连接过的 WiFi，右键查看密码和扫码连接二维码；
// - 北京时间同步：NTP/HTTP 两种方式取北京时间，对比本地时间并可一键同步；
// - 二维码生成：任意内容转二维码图片，可复制/保存。
// 二维码绘制统一走 QrCodeService；WiFi 信息读取走 WifiService；时间获取走 TimeSyncService。
// =====================================================================

/// <summary>
/// 已连接 WiFi 密码查看（需求）：
/// 列表展示所有连接过的 WiFi（netsh wlan show profiles），
/// 右键菜单"查看密码和二维码"弹出对话框，同时展示明文密码与手机扫码连接用的二维码。
/// </summary>
internal sealed class WifiPasswordTool : IToolPage
{
    /// <summary>二维码在弹窗里的显示边长（像素）</summary>
    private const int QrDisplaySize = 260;

    private TextBlock _status = null!;
    private StackPanel _listPanel = null!;
    private Button _refreshButton = null!;

    /// <summary>是否已自动读取过（页面缓存导致 Loaded 会多次触发，只自动读一次）</summary>
    private bool _loadedOnce;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        _refreshButton = ToolKit.PrimaryButton("Tools.Wifi.Refresh", async (_, _) =>
        {
            _refreshButton.IsEnabled = false;
            try
            {
                await LoadAsync();
            }
            finally
            {
                _refreshButton.IsEnabled = true;
            }
        });
        root.Children.Add(ToolKit.ButtonRow(_refreshButton));

        _status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(_status);

        _listPanel = new StackPanel { Spacing = 4 };
        root.Children.Add(_listPanel);

        // 首次进入自动读取一次（界面已在可视树上，弹窗用的 XamlRoot 才可用）。
        // 页面是缓存的，切走再切回也会触发 Loaded——用标记保证只自动读一次，
        // 之后的刷新由"刷新列表"按钮负责（netsh 读取所有配置文件有明显耗时）。
        root.Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await LoadAsync();
        };
        return root;
    }

    /// <summary>读取 WiFi 列表（netsh 调用在后台线程，避免界面卡顿）</summary>
    private async Task LoadAsync()
    {
        ToolKit.SetStatus(_status, LocalizationService.Tr("Tools.Wifi.Loading"));
        _listPanel.Children.Clear();

        // 元组返回：out 参数不能直接跨 Task.Run 的 lambda 传出来
        var (profiles, error) = await Task.Run(() =>
        {
            var list = WifiService.GetAllProfiles(out var err);
            return (list, err);
        });

        if (profiles.Count == 0)
        {
            ToolKit.SetStatus(_status, error ?? LocalizationService.Tr("Tools.Wifi.ErrEmpty"), isError: true);
            return;
        }

        ToolKit.SetStatus(_status,
            string.Format(LocalizationService.Tr("Tools.Wifi.Count"), profiles.Count));

        foreach (var profile in profiles)
        {
            _listPanel.Children.Add(BuildProfileRow(profile));
        }
    }

    /// <summary>构建一行 WiFi（名称 + 身份验证方式；右键弹菜单）</summary>
    private Border BuildProfileRow(WifiService.WifiProfile profile)
    {
        var name = new TextBlock
        {
            Text = profile.Name,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var detail = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(profile.Authentication)
                ? LocalizationService.Tr("Tools.Wifi.Open")
                : profile.Authentication,
            FontSize = 12,
            Opacity = 0.6
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(name);
        stack.Children.Add(detail);

        var row = new Border
        {
            Child = stack,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };

        // 右键菜单：查看密码和二维码（需求）。
        // ContextFlyout 会在右键时自动在指针位置弹出，不需要自己写 RightTapped
        // （两处都写会导致菜单弹两次）。
        var menu = new MenuFlyout();
        var viewItem = new MenuFlyoutItem { Text = LocalizationService.Tr("Tools.Wifi.ViewPassword") };
        viewItem.Click += (_, _) => _ = ShowPasswordDialogAsync(profile);
        menu.Items.Add(viewItem);

        var copyItem = new MenuFlyoutItem { Text = LocalizationService.Tr("Tools.Wifi.CopyPassword") };
        copyItem.Click += (_, _) => CopyPassword(profile);
        menu.Items.Add(copyItem);

        row.ContextFlyout = menu;
        return row;
    }

    /// <summary>弹出"WiFi 二维码 + 密码"对话框（需求：布局好看即可）</summary>
    private async Task ShowPasswordDialogAsync(WifiService.WifiProfile profile)
    {
        var content = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 300
        };

        // 二维码：内容为 WIFI:... 载荷，手机相机扫一下即可直接连网
        var payload = QrCodeService.BuildWifiPayload(profile.Name, profile.Password,
            NormalizeAuthType(profile.Authentication), profile.IsHidden);
        var qrPng = QrCodeService.CreatePngBytes(payload, QrDisplaySize);
        var qrBitmap = qrPng == null ? null : await QrCodeService.CreateBitmapAsync(qrPng);
        if (qrBitmap != null)
        {
            var qrImage = new Image
            {
                Source = qrBitmap,
                Width = QrDisplaySize,
                Height = QrDisplaySize,
                Stretch = Stretch.Uniform
            };
            // 二维码白底黑块，深色主题下也要保证对比度 → 包一层白底圆角
            content.Children.Add(new Border
            {
                Child = qrImage,
                Background = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = LocalizationService.Tr("Tools.Wifi.ErrQrFailed"),
                Foreground = new SolidColorBrush(Colors.OrangeRed),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        // 名称
        content.Children.Add(BuildInfoRow(LocalizationService.Tr("Tools.Wifi.Name"), profile.Name));

        // 密码（选中即可复制；密码为空说明是开放网络）
        var passwordText = profile.HasPassword
            ? profile.Password
            : LocalizationService.Tr("Tools.Wifi.NoPassword");
        content.Children.Add(BuildInfoRow(LocalizationService.Tr("Tools.Wifi.Password"), passwordText));

        // 身份验证方式
        if (!string.IsNullOrWhiteSpace(profile.Authentication))
        {
            content.Children.Add(BuildInfoRow(LocalizationService.Tr("Tools.Wifi.Authentication"),
                profile.Authentication));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _listPanel.XamlRoot,
            Title = string.Format(LocalizationService.Tr("Tools.Wifi.DialogTitle"), profile.Name),
            Content = content,
            PrimaryButtonText = LocalizationService.Tr("Tools.Wifi.CopyPayload"),
            CloseButtonText = LocalizationService.Tr("Dialog.Close"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            // 复制二维码内容（可粘贴到别处生成二维码）
            CopyText(payload);
        }
    }

    /// <summary>一行"标签 + 值"（值可选中复制）</summary>
    private static Grid BuildInfoRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelBlock, 0);

        // 用只读 TextBox 而不是 TextBlock：用户可以选中复制密码
        var valueBox = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            FontSize = 14,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBox, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBox);
        return grid;
    }

    /// <summary>把 netsh 的"身份验证"描述转成二维码规范要求的类型（WPA/WEP/nopass）</summary>
    private static string NormalizeAuthType(string authentication)
    {
        if (string.IsNullOrWhiteSpace(authentication)) return "WPA";
        var text = authentication.ToUpperInvariant();
        if (text.Contains("WEP")) return "WEP";
        if (text.Contains("WPA")) return "WPA";
        if (text.Contains("开放") || text.Contains("OPEN") || text.Contains("NONE")) return "nopass";
        return "WPA"; // 兜底：绝大多数是 WPA/WPA2/WPA3
    }

    private void CopyPassword(WifiService.WifiProfile profile)
    {
        if (profile.HasPassword)
        {
            CopyText(profile.Password);
        }
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}

/// <summary>
/// 北京时间同步（需求）：
/// 展示本地时间和北京时间（每秒刷新），可选 NTP / HTTP 两种取时方式和各自的服务器，
/// 点"同步本地时间"把本地时钟校准到北京时间（需要管理员权限）。
/// </summary>
internal sealed class TimeSyncTool : IToolPage
{
    private static readonly string[] MethodKeys = ["Tools.Time.Method.Ntp", "Tools.Time.Method.Http"];

    private TextBlock _localTime = null!;
    private TextBlock _beijingTime = null!;
    private TextBlock _diffText = null!;
    private TextBlock _status = null!;
    private ComboBox _method = null!;
    private ComboBox _server = null!;
    private Button _syncButton = null!;

    /// <summary>最近一次从网络取到的北京时间（UTC）；null = 还没获取过</summary>
    private DateTime? _fetchedUtc;

    /// <summary>取到该时间时的本地时刻，用于实时推算"现在的北京时间"</summary>
    private DateTime _fetchedLocalAt;

    /// <summary>是否已自动获取过（页面缓存导致 Loaded 多次触发，只自动取一次）</summary>
    private bool _loadedOnce;

    private DispatcherTimer? _timer;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        // ===== 时间显示区（大字号，居中） =====
        var display = new Grid
        {
            ColumnSpacing = 24,
            Margin = new Thickness(0, 8, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        display.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        display.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var localStack = BuildTimeBlock("Tools.Time.LocalTime", out _localTime);
        Grid.SetColumn(localStack, 0);
        var beijingStack = BuildTimeBlock("Tools.Time.BeijingTime", out _beijingTime);
        Grid.SetColumn(beijingStack, 1);
        display.Children.Add(localStack);
        display.Children.Add(beijingStack);
        root.Children.Add(display);

        _diffText = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(_diffText);

        // ===== 获取方式 =====
        _method = ToolKit.Combo(MethodKeys);
        _method.SelectionChanged += (_, _) => RebuildServerList();
        root.Children.Add(ToolKit.OptionRow("Tools.Time.Method", _method));

        _server = ToolKit.Combo(TimeSyncService.NtpServers.Select(s => s.NameKey).ToArray());
        root.Children.Add(ToolKit.OptionRow("Tools.Time.Server", _server));

        _syncButton = ToolKit.PrimaryButton("Tools.Time.Sync", async (_, _) => await SyncAsync());
        root.Children.Add(ToolKit.ButtonRow(_syncButton));

        _status = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_status);

        // ===== 每秒刷新 =====
        UpdateClocks();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateClocks();
        _timer.Start();

        // 切走时页面会脱离可视树（工具页面缓存复用），必须停掉计时器，
        // 否则它会一直持有旧页面并空转；切回来时再启动（详见下方 Loaded）。
        root.Unloaded += (_, _) =>
        {
            _timer?.Stop();
        };

        // 进入界面：重启计时器 + 首次自动取一次北京时间。
        // 注意页面是缓存的，切走再切回会再次触发 Loaded（所以计时器要在这里重启），
        // 但"自动取时间"只做一次（之后由按钮或用户刷新）。
        root.Loaded += async (_, _) =>
        {
            _timer?.Start();
            if (_loadedOnce) return;
            _loadedOnce = true;
            await FetchAsync(silent: true);
        };
        return root;
    }

    /// <summary>一块时间显示（小标题 + 大字号时间）</summary>
    private static StackPanel BuildTimeBlock(string labelKey, out TextBlock valueBlock)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = LocalizationService.Tr(labelKey),
            FontSize = 12,
            Opacity = 0.7
        });
        valueBlock = new TextBlock
        {
            Text = "--",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        stack.Children.Add(valueBlock);
        return stack;
    }

    /// <summary>切换方式后重建服务器下拉框内容</summary>
    private void RebuildServerList()
    {
        var isNtp = ToolKit.SelectedKey(_method) == "Tools.Time.Method.Ntp";
        var keys = isNtp
            ? TimeSyncService.NtpServers.Select(s => s.NameKey).ToArray()
            : TimeSyncService.HttpApis.Select(a => a.NameKey).ToArray();
        _server.ItemsSource = keys.Select(LocalizationService.Tr).ToArray();
        _server.Tag = keys; // Combo 的 SelectedKey() 从这里取
        _server.SelectedIndex = 0;
    }

    /// <summary>每秒刷新两个时钟和差值</summary>
    private void UpdateClocks()
    {
        var now = DateTime.Now;
        _localTime.Text = now.ToString("yyyy-MM-dd HH:mm:ss");

        if (_fetchedUtc == null)
        {
            _beijingTime.Text = LocalizationService.Tr("Tools.Time.NotFetched");
            _diffText.Text = "";
            return;
        }

        // 取时后没有系统时钟跳变的话，按本地时钟走时推算当前北京时间
        var beijingNow = _fetchedUtc.Value + (DateTime.UtcNow - _fetchedLocalAt);
        _beijingTime.Text = beijingNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        // 差值：本地时间 - 北京时间（正 = 本地快）
        var offset = beijingNow - DateTime.UtcNow;
        var seconds = offset.TotalSeconds;
        var direction = seconds > 0
            ? LocalizationService.Tr("Tools.Time.Fast")
            : LocalizationService.Tr("Tools.Time.Slow");
        _diffText.Text = Math.Abs(seconds) < 1
            ? LocalizationService.Tr("Tools.Time.DiffAccurate")
            : string.Format(LocalizationService.Tr("Tools.Time.Diff"), direction,
                Math.Abs(seconds).ToString("F1"));
    }

    /// <summary>获取北京时间（silent = true 时不显示错误状态，只静默失败）</summary>
    private async Task<bool> FetchAsync(bool silent)
    {
        _syncButton.IsEnabled = false;
        if (!silent)
        {
            ToolKit.SetStatus(_status, LocalizationService.Tr("Tools.Time.Fetching"));
        }

        try
        {
            var isNtp = ToolKit.SelectedKey(_method) == "Tools.Time.Method.Ntp";
            var serverKey = ToolKit.SelectedKey(_server);
            string target;
            if (isNtp)
            {
                target = TimeSyncService.NtpServers
                    .FirstOrDefault(s => s.NameKey == serverKey).Host ?? TimeSyncService.NtpServers[0].Host;
            }
            else
            {
                target = TimeSyncService.HttpApis
                    .FirstOrDefault(a => a.NameKey == serverKey).Url ?? TimeSyncService.HttpApis[0].Url;
            }

            var result = await TimeSyncService.GetBeijingTimeAsync(
                isNtp ? TimeSyncService.SyncMethod.Ntp : TimeSyncService.SyncMethod.Http, target);

            _fetchedUtc = result.UtcTime;
            _fetchedLocalAt = DateTime.UtcNow;
            UpdateClocks();

            if (!silent)
            {
                ToolKit.SetStatus(_status,
                    string.Format(LocalizationService.Tr("Tools.Time.Fetched"), result.Source));
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                ToolKit.SetStatus(_status, ex.Message, isError: true);
            }
            return false;
        }
        finally
        {
            _syncButton.IsEnabled = true;
        }
    }

    /// <summary>同步本地时间：先取最新北京时间，再以管理员权限改系统时间（需求）</summary>
    private async Task SyncAsync()
    {
        // 先重新取一次，保证同步用的是刚刚的时间
        var ok = await FetchAsync(silent: false);
        if (!ok || _fetchedUtc == null) return;

        var success = TimeSyncService.ApplySystemTime(_fetchedUtc.Value, out var error);
        if (success)
        {
            ToolKit.SetStatus(_status, LocalizationService.Tr("Tools.Time.SyncSuccess"));
            UpdateClocks();
        }
        else
        {
            ToolKit.SetStatus(_status,
                string.Format(LocalizationService.Tr("Tools.Time.SyncFailed"), error ?? ""), isError: true);
        }
    }
}

/// <summary>
/// 二维码生成（需求）：输入任意内容，点击生成二维码图片；可复制内容与保存图片文件。
/// </summary>
internal sealed class QrCodeTool : IToolPage
{
    /// <summary>界面上二维码的显示边长（像素）</summary>
    private const int DisplaySize = 320;

    private static readonly string[] ErrorLevelKeys =
        ["Tools.Qr.Ecc.L", "Tools.Qr.Ecc.M", "Tools.Qr.Ecc.Q", "Tools.Qr.Ecc.H"];

    /// <summary>纠错级别下拉项 → 服务层的纠错级别（顺序与 ErrorLevelKeys 一致）</summary>
    private static readonly QrCodeService.ErrorLevel[] ErrorLevels =
    [
        QrCodeService.ErrorLevel.Low,
        QrCodeService.ErrorLevel.Medium,
        QrCodeService.ErrorLevel.Quartile,
        QrCodeService.ErrorLevel.High
    ];

    private TextBox _input = null!;
    private ComboBox _errorLevel = null!;
    private StackPanel _imagePanel = null!;
    private TextBlock _status = null!;
    private Button _saveButton = null!;

    /// <summary>当前生成的二维码 PNG 字节（保存按钮直接写这个，不再二次编码）</summary>
    private byte[]? _currentPng;

    public UIElement Build()
    {
        var root = ToolKit.PageRoot();

        root.Children.Add(ToolKit.SectionTitle("Tools.Qr.Input"));
        _input = ToolKit.InputArea(100, "Tools.Qr.Placeholder");
        root.Children.Add(_input);

        _errorLevel = ToolKit.Combo(ErrorLevelKeys);
        _errorLevel.SelectedIndex = 1; // 默认 M（15%）
        root.Children.Add(ToolKit.OptionRow("Tools.Qr.ErrorLevel", _errorLevel));

        var generate = ToolKit.PrimaryButton("Tools.Btn.Generate", async (_, _) => await RunAsync());
        _saveButton = ToolKit.ActionButton("Tools.Qr.Save", async (_, _) => await SaveAsync());
        _saveButton.IsEnabled = false;
        root.Children.Add(ToolKit.ButtonRow(generate, _saveButton));

        _status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(_status);

        // 图片区（白底见下方 Border）：深色主题下二维码也能保持黑白对比正常扫描
        _imagePanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        root.Children.Add(_imagePanel);

        root.Children.Add(ToolKit.ButtonRow(ToolKit.CopyButton(() => _input.Text)));
        return root;
    }

    private async Task RunAsync()
    {
        var content = _input.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            ToolKit.SetStatus(_status, LocalizationService.Tr("Tools.Qr.ErrEmpty"), isError: true);
            return;
        }

        var png = QrCodeService.CreatePngBytes(content, DisplaySize, SelectedErrorLevel());
        var bitmap = png == null ? null : await QrCodeService.CreateBitmapAsync(png);
        if (png == null || bitmap == null)
        {
            // 内容超出二维码最大容量（约 2900 字节）时生成失败
            ToolKit.SetStatus(_status, LocalizationService.Tr("Tools.Qr.ErrTooLong"), isError: true);
            _currentPng = null;
            _saveButton.IsEnabled = false;
            _imagePanel.Children.Clear();
            return;
        }

        _currentPng = png;
        _saveButton.IsEnabled = true;
        _imagePanel.Children.Clear();
        _imagePanel.Children.Add(new Border
        {
            Child = new Image
            {
                Source = bitmap,
                Width = DisplaySize,
                Height = DisplaySize,
                Stretch = Stretch.Uniform
            },
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10)
        });
        ToolKit.SetStatus(_status,
            string.Format(LocalizationService.Tr("Tools.Qr.Generated"), content.Length));
    }

    /// <summary>当前下拉框选中的纠错级别</summary>
    private QrCodeService.ErrorLevel SelectedErrorLevel()
    {
        var index = _errorLevel.SelectedIndex;
        return index >= 0 && index < ErrorLevels.Length
            ? ErrorLevels[index]
            : QrCodeService.ErrorLevel.Medium;
    }

    /// <summary>把二维码保存成 png 文件（用系统的"另存为"对话框）</summary>
    private async Task SaveAsync()
    {
        if (_currentPng == null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = "qrcode"
            };
            picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
            // 文件选择器在解包应用里必须绑定窗口句柄（与设置页/图标选择器同款）
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                App.Instance.GetMainWindow().WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            // 另存为对话框会预先创建空文件，必须用 FileMode.Create 覆盖（不是 CreateNew）
            using (var stream = new FileStream(file.Path, FileMode.Create, FileAccess.Write))
            {
                await stream.WriteAsync(_currentPng);
            }
            ToolKit.SetStatus(_status, string.Format(LocalizationService.Tr("Tools.Qr.Saved"), file.Path));
        }
        catch (Exception ex)
        {
            ToolKit.SetStatus(_status,
                string.Format(LocalizationService.Tr("Tools.Qr.ErrSaveFailedWith"), ex.Message), isError: true);
        }
    }
}

using DaenLauncher.Models;
using DaenLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DaenLauncher;

/// <summary>
/// 必应每日壁纸窗口（单例，关闭 = 隐藏）。
/// 布局：整幅圆角壁纸卡片铺满窗口 —— 大图 + 底部渐变遮罩上叠"日期 + 图片描述(copyright)"
/// + 右下角"更换壁纸"按钮；打开窗口自动展示今日壁纸（只展示，不更换），
/// 点按钮才会重新获取并更换桌面壁纸。
/// </summary>
public sealed partial class WallpaperWindow : Window
{
    private readonly AppSettings _settings = SettingsService.Instance.Settings;

    private IntPtr _hWnd;
    private AppWindow _appWindow = null!;
    private OverlappedPresenter? _presenter;
    private System.Drawing.Icon? _titleBarIconSmall;
    private System.Drawing.Icon? _titleBarIconBig;

    /// <summary>窗口位置/尺寸保存防抖计时器（"上次位置"和尺寸记忆用）</summary>
    private readonly DispatcherTimer _savePositionTimer;

    // ===== 界面状态 =====

    /// <summary>当前展示的壁纸数据（语言切换时按它重刷文字）</summary>
    private WallpaperInfo? _currentInfo;

    /// <summary>是否正在网络请求（防止重复点击 / 重复显示触发并发加载）</summary>
    private bool _isBusy;

    public WallpaperWindow()
    {
        InitializeComponent();

        // ===== 窗口基础设置（与其他附属功能窗口一致：关闭 = 隐藏） =====
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = _appWindow.Presenter as OverlappedPresenter;

        Title = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Wallpaper");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        _appWindow.Closing += (_, e) =>
        {
            // 关闭（隐藏/退出）前把窗口位置/尺寸立即落盘
            SavePositionNow();
            if (!App.IsExiting)
            {
                e.Cancel = true;
                _appWindow.Hide();
            }
        };

        // 记录窗口位置/尺寸（防抖保存；计时器必须先于 Changed 订阅创建）
        _savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _savePositionTimer.Tick += SavePositionTimer_Tick;
        _appWindow.Changed += AppWindow_Changed;

        // 任务栏/标题栏图标（与其他附属功能窗口同款）
        try
        {
            var iconPath = IconService.EnsureAppIconExtracted();
            if (iconPath != null) _appWindow.SetIcon(iconPath);
            if (iconPath != null)
            {
                using var fs = File.OpenRead(iconPath);
                _titleBarIconSmall = new System.Drawing.Icon(fs, 16, 16);
                fs.Position = 0;
                _titleBarIconBig = new System.Drawing.Icon(fs, 32, 32);
                Win32Helper.SendMessage(_hWnd, Win32Helper.WM_SETICON,
                    Win32Helper.ICON_SMALL, _titleBarIconSmall.Handle);
                Win32Helper.SendMessage(_hWnd, Win32Helper.WM_SETICON,
                    Win32Helper.ICON_BIG, _titleBarIconBig.Handle);
            }
        }
        catch { /* 图标设置失败不影响运行 */ }

        BackdropService.Apply(this, _settings.Backdrop);
        ThemeService.Apply(RootGrid, _settings.Theme);
        ThemeService.ApplyCaptionButtonColors(_appWindow, RootGrid);

        // 记忆的窗口尺寸（逻辑像素 → 物理像素）
        ApplyRememberedSize();

        // 应用锁定尺寸设置
        ApplyBehaviorSettings();

        _ = LoadTitleBarIconAsync();
        ApplyLocalization();

        // 联动：语言切换后刷新标题和界面文字
        LocalizationService.LanguageChanged += ApplyLocalization;
    }

    private async Task LoadTitleBarIconAsync()
    {
        var bitmap = await IconService.LoadEmbeddedAsync("DaenLauncher.Assets.logo.logo_64.png");
        if (bitmap != null) TitleBarIcon.Source = bitmap;
    }

    /// <summary>显示并置前（由 App.ShowWallpaperWindow 调用，每次显示都会执行）</summary>
    public void ActivateAndBringToFront()
    {
        ApplyBehaviorSettings();

        if (!_appWindow.IsVisible)
        {
            ComputeShowPosition();
            _appWindow.Show();
        }
        else
        {
            _appWindow.Show(true);
        }
        Activate();

        // 每次显示都重新应用主题（设置窗口里可能刚切换过）
        ThemeService.Apply(RootGrid, _settings.Theme);

        // 打开窗口即展示今日壁纸（需求：页面显示获取到的今日壁纸数据；只展示不更换）
        _ = LoadTodayAsync();
    }

    /// <summary>快捷键显示/隐藏切换（默认 Alt+6）</summary>
    public void ToggleViaHotkey()
    {
        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }
        else
        {
            ActivateAndBringToFront();
        }
    }

    /// <summary>外部变化（数据页删除/导入 wallpaper 数据）后刷新展示：记录没了就清空画面</summary>
    public void RefreshView()
    {
        _currentInfo = null;
        var record = WallpaperService.Instance.Record;
        if (string.IsNullOrEmpty(record.EndDate))
        {
            DateText.Text = "";
            CopyrightText.Text = "";
            WallpaperImage.Source = null;
            return;
        }
        ShowRecordInstantly();
    }

    /// <summary>应用窗口行为设置（永远置顶 / 锁定尺寸，设置页修改后即时生效）</summary>
    public void ApplyBehaviorSettings()
    {
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _settings.WallpaperAlwaysOnTop;
            _presenter.IsResizable = !_settings.WallpaperLockSize;
        }
    }

    #region 窗口位置/尺寸

    /// <summary>按记忆的尺寸设置窗口大小（逻辑像素 → 物理像素）</summary>
    private void ApplyRememberedSize()
    {
        var scale = GetDpiScale();
        var width = (int)(_settings.WallpaperWindowWidth * scale);
        var height = (int)(_settings.WallpaperWindowHeight * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    /// <summary>按设置的显示位置定位窗口（与其他附属功能窗口同款）</summary>
    private void ComputeShowPosition()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
            DisplayAreaFallback.Nearest);
        var wa = displayArea.WorkArea;

        var scale = GetDpiScale();
        var width = (int)(_settings.WallpaperWindowWidth * scale);
        var height = (int)(_settings.WallpaperWindowHeight * scale);

        int x, y;
        switch (_settings.WallpaperShowPosition)
        {
            case ShowPosition.TopLeft:
                x = wa.X; y = wa.Y;
                break;
            case ShowPosition.TopRight:
                x = wa.X + wa.Width - width; y = wa.Y;
                break;
            case ShowPosition.BottomLeft:
                x = wa.X; y = wa.Y + wa.Height - height;
                break;
            case ShowPosition.BottomRight:
                x = wa.X + wa.Width - width; y = wa.Y + wa.Height - height;
                break;
            case ShowPosition.LastPosition:
                // 上次位置：用记录的坐标（物理像素）；没记录过则退回屏幕中央
                if (_settings.WallpaperLastWindowX >= 0 && _settings.WallpaperLastWindowY >= 0)
                {
                    x = _settings.WallpaperLastWindowX;
                    y = _settings.WallpaperLastWindowY;
                }
                else
                {
                    x = wa.X + (wa.Width - width) / 2;
                    y = wa.Y + (wa.Height - height) / 2;
                }
                break;
            case ShowPosition.FollowMouse:
                Win32Helper.GetCursorPos(out var cursor);
                x = cursor.X - width / 2;
                y = cursor.Y - height / 2;
                break;
            case ShowPosition.Center:
            default:
                x = wa.X + (wa.Width - width) / 2;
                y = wa.Y + (wa.Height - height) / 2;
                break;
        }

        // 防止超出屏幕边缘
        if (x < wa.X) x = wa.X;
        if (y < wa.Y) y = wa.Y;
        if (x + width > wa.X + wa.Width) x = wa.X + wa.Width - width;
        if (y + height > wa.Y + wa.Height) y = wa.Y + wa.Height - height;

        _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>窗口位置/尺寸变化：记录到设置（防抖），供"上次位置"和尺寸记忆使用</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        _settings.WallpaperLastWindowX = sender.Position.X;
        _settings.WallpaperLastWindowY = sender.Position.Y;
        // 尺寸记忆：物理像素 → 逻辑像素（AppWindow 没有 Width/Height，用 Size）
        var scale = GetDpiScale();
        if (scale > 0)
        {
            _settings.WallpaperWindowWidth = Math.Round(sender.Size.Width / scale);
            _settings.WallpaperWindowHeight = Math.Round(sender.Size.Height / scale);
        }
        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void SavePositionTimer_Tick(object? sender, object e)
    {
        _savePositionTimer.Stop();
        SettingsService.Instance.Save();
    }

    /// <summary>立即把待保存的窗口位置/尺寸写盘（隐藏/退出前调用）</summary>
    private void SavePositionNow()
    {
        if (_savePositionTimer.IsEnabled)
        {
            _savePositionTimer.Stop();
            SettingsService.Instance.Save();
        }
    }

    /// <summary>窗口 DPI 缩放</summary>
    private double GetDpiScale()
    {
        try
        {
            return Win32Helper.GetDpiForWindow(_hWnd) / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    #endregion

    #region 本地化

    /// <summary>刷新窗口标题（自定义软件标题变化后由 App.RefreshAllTitles 调用，联动需求）</summary>
    public void RefreshTitle()
    {
        TitleBarText.Text = App.GetDisplayTitle() + " " + LocalizationService.Tr("Main.Wallpaper");
        Title = TitleBarText.Text;
    }

    /// <summary>应用界面文字（首次加载和语言切换时调用）</summary>
    private void ApplyLocalization()
    {
        RefreshTitle();
        if (!_isBusy)
        {
            // 空闲时按钮文字跟随语言；正在请求时保持"正在获取…"反馈，结束后由 finally 恢复
            ApplyButtonText.Text = LocalizationService.Tr("Wallpaper.Apply");
        }
        RefreshInfoTexts();
    }

    /// <summary>按当前展示的壁纸数据刷新日期/描述文字（日期格式随语言变化）</summary>
    private void RefreshInfoTexts()
    {
        if (_currentInfo == null) return;
        DateText.Text = FormatDate(_currentInfo.EndDate);
        CopyrightText.Text = _currentInfo.Copyright;
    }

    #endregion

    #region 展示今日壁纸

    /// <summary>
    /// 加载并展示今日壁纸（打开窗口时自动执行；只获取展示，不更换桌面壁纸）。
    /// 先把记录里的上次结果立即显示出来（本地缓存，无网络等待），再后台获取今日数据。
    /// </summary>
    private async Task LoadTodayAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            ErrorBar.IsOpen = false;
            ApplyButton.IsEnabled = false;
            LoadingRing.IsActive = true;

            ShowRecordInstantly();

            // 获取今日元数据 + 下载显示用图片（缓存过就直接复用本地文件）
            var info = await WallpaperService.Instance.FetchInfoAsync(_settings);
            var file = await WallpaperService.Instance.DownloadImageAsync(info);
            _currentInfo = info;
            SetImage(file);
            RefreshInfoTexts();
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Tr("Wallpaper.LoadFailed"), ex.Message);
        }
        finally
        {
            LoadingRing.IsActive = false;
            ApplyButton.IsEnabled = true;
            _isBusy = false;
        }
    }

    /// <summary>把更换记录里的上次结果立即显示出来（本地缓存文件，无网络等待，体验更顺滑）</summary>
    private void ShowRecordInstantly()
    {
        var record = WallpaperService.Instance.Record;
        if (string.IsNullOrEmpty(record.EndDate)) return;
        DateText.Text = FormatDate(record.EndDate);
        CopyrightText.Text = record.Copyright;
        var cacheFile = Path.Combine(
            DataPathService.WallpaperCacheDir, $"{record.EndDate}_{record.SizeText}.jpg");
        if (File.Exists(cacheFile))
        {
            SetImage(cacheFile);
        }
    }

    /// <summary>"更换壁纸"按钮：重新获取今日壁纸并更换桌面壁纸（需求）</summary>
    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            ErrorBar.IsOpen = false;
            ApplyButton.IsEnabled = false;
            ApplyButtonText.Text = LocalizationService.Tr("Wallpaper.Applying");
            LoadingRing.IsActive = true;

            var result = await WallpaperService.Instance.ApplyWallpaperAsync();
            if (result.Success && result.Info != null)
            {
                _currentInfo = result.Info;
                // 刚下载/复用的缓存文件直接显示（与刚设置的桌面壁纸是同一张）
                SetImage(Path.Combine(
                    DataPathService.WallpaperCacheDir, result.Info.FileName));
                RefreshInfoTexts();

                // 按钮文字短暂变成"已更换"作为成功反馈（与常用工具"已复制"同款交互）
                ApplyButtonText.Text = LocalizationService.Tr("Wallpaper.ApplySuccess");
                await Task.Delay(1500);
            }
            else
            {
                ShowError(LocalizationService.Tr("Wallpaper.ApplyFailed"), result.Error);
            }
        }
        finally
        {
            LoadingRing.IsActive = false;
            _isBusy = false;
            ApplyButtonText.Text = LocalizationService.Tr("Wallpaper.Apply");
            ApplyButton.IsEnabled = true;
        }
    }

    #endregion

    #region 显示辅助

    /// <summary>显示图片（本地缓存文件；解码失败不崩溃）</summary>
    private void SetImage(string filePath)
    {
        try
        {
            WallpaperImage.Source = new BitmapImage(new Uri(filePath));
        }
        catch { /* 图片文件异常时保持原图 */ }
    }

    /// <summary>yyyyMMdd → 按语言文件的格式串显示（zh：2026年10月06日，需求；逻辑在 WallpaperService）</summary>
    private static string FormatDate(string yyyyMMdd)
    {
        return WallpaperService.FormatDateForDisplay(yyyyMMdd);
    }

    /// <summary>顶部错误提示条</summary>
    private void ShowError(string title, string detail)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = detail;
        ErrorBar.IsOpen = true;
    }

    #endregion
}

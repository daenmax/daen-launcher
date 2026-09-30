using DaenLauncher.Models;
using DaenLauncher.Services;
using DaenLauncher.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DaenLauncher;

/// <summary>
/// 应用入口：单实例、初始化服务、创建托盘、管理主窗口与设置窗口。
/// </summary>
public partial class App : Application
{
    #region 单实例

    private const string SingleInstanceMutexName = "DaenLauncher_SingleInstance_Mutex";
    private const string ActivationEventName = "DaenLauncher_Activate_Event";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    #endregion

    /// <summary>UI 线程调度器（服务里用于切回主线程）</summary>
    public static DispatcherQueue? UiDispatcherQueue { get; private set; }

    /// <summary>全局单例</summary>
    public static App Instance { get; private set; } = null!;

    /// <summary>是否正在退出（区分"隐藏窗口"和"退出进程"）</summary>
    public static bool IsExiting { get; private set; }

    private TrayService? _trayService;
    private InputHookService? _inputHookService;

    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TodoWindow? _todoWindow;

    public App()
    {
        InitializeComponent();
        Instance = this;
        UiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // 全局异常日志（写入 data\crash.log，方便排查启动问题）
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(DataPathService.DataRoot, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Unhandled: {e.Message}\n{e.Exception}\n\n");
            }
            catch { /* 忽略 */ }
            // 标记已处理：非致命的后台异常（如偶发的文件占用）不再直接崩溃整个应用
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunchedCore(args);
        }
        catch (Exception ex)
        {
            try
            {
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                File.AppendAllText(
                    Path.Combine(exeDir, "data", "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} OnLaunched: {ex}\n\n");
            }
            catch { /* 忽略 */ }
            throw;
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        // ===== 单实例检测（需求第6条） =====
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            // 已有实例在运行：等它退出（重启场景最多等 3 秒），等不到就通知它显示窗口并退出自己
            var acquired = false;
            try { acquired = _singleInstanceMutex.WaitOne(3000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                _activationEvent.Set();
                Environment.Exit(0);
                return;
            }
        }

        // 主实例：监听激活事件，收到信号就显示主窗口
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => UiDispatcherQueue?.TryEnqueue(() =>
            {
                try { ShowMainWindow(); } catch { /* 激活失败忽略 */ }
            }),
            null,
            Timeout.Infinite,
            false);

        // ===== 初始化服务 =====
        DataPathService.Initialize();
        var settings = SettingsService.Instance.Settings;
        LocalizationService.Instance.Initialize(settings);

        // ===== 托盘 =====
        _trayService = new TrayService();
        _trayService.Initialize();

        // ===== 开机自启设置 =====
        AutoStartService.Apply(settings);

        // ===== 显示/隐藏触发方式（钩子 + 全局热键） =====
        _inputHookService = new InputHookService();
        _inputHookService.ToggleRequested += ToggleMainWindow;
        _inputHookService.ApplySettings(settings);
        SetupHotkeys();

        // ===== 语言切换：托盘菜单文字实时刷新 =====
        LocalizationService.LanguageChanged += RefreshTrayLocalization;

        // ===== 待办：从磁盘加载本地数据（云同步开启时窗口打开后以云端为准） =====
        TodoService.Instance.LoadLocal();

        // ===== 项目完整性检测（仅在软件启动时执行一次，失效项目显示"项目无法找到"图标，需求-优化1）=====
        LauncherDataService.Instance.CheckMissingItems();

        // ===== 首屏 =====
        if (settings.StartBehavior == StartBehavior.Show)
        {
            ShowMainWindow();
        }
    }

    #region 窗口管理

    /// <summary>获取（懒创建）主窗口</summary>
    public MainWindow GetMainWindow()
    {
        _mainWindow ??= new MainWindow();
        return _mainWindow;
    }

    /// <summary>显示主窗口（按设置的位置显示）</summary>
    public void ShowMainWindow()
    {
        var window = GetMainWindow();
        window.ShowAtConfiguredPosition();
    }

    /// <summary>显示/隐藏主窗口切换（托盘双击、快捷键、侧键等）</summary>
    public void ToggleMainWindow()
    {
        var window = GetMainWindow();
        window.ToggleVisibility();
    }

    /// <summary>打开设置窗口（单例）</summary>
    public void ShowSettingsWindow()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ActivateAndBringToFront();
    }

    /// <summary>打开设置窗口并选中"关于"页（托盘"关于我们"）</summary>
    public void ShowAboutPage()
    {
        ShowSettingsWindow();
        _settingsWindow?.NavigateToAbout();
    }

    /// <summary>打开待办窗口（单例）</summary>
    public void ShowTodoWindow()
    {
        if (_todoWindow == null)
        {
            _todoWindow = new TodoWindow();
            _todoWindow.Closed += (_, _) => _todoWindow = null;
        }
        _todoWindow.ActivateAndBringToFront();
    }

    /// <summary>显示/隐藏待办窗口切换（待办快捷键，默认 Alt+2）</summary>
    public void ToggleTodoWindow()
    {
        if (_todoWindow == null)
        {
            ShowTodoWindow();
            return;
        }
        _todoWindow.ToggleViaHotkey();
    }

    #endregion

    #region 热键

    private MessageWindow? _hotkeyWindow;

    /// <summary>
    /// 创建热键消息窗口并注册热键（UI 线程调用）。
    /// 注意：热键注册在自建的经典 Win32 消息窗口上——WinUI 3 主窗口的过程
    /// 无法可靠拦截 WM_HOTKEY（实测收不到），消息窗口是稳妥方案。
    /// </summary>
    public void SetupHotkeys()
    {
        _hotkeyWindow = new MessageWindow();
        _hotkeyWindow.Create();
        // WM_HOTKEY 的 wParam 是注册时的热键 id，按 id 分发到主窗口/待办窗口
        _hotkeyWindow.MessageReceived += (msg, wParam, _) => Win32Helper.HandleHotkeyMessage(msg, wParam);
        Win32Helper.HotkeyPressed += ToggleMainWindow;
        Win32Helper.TodoHotkeyPressed += ToggleTodoWindow;
        RegisterHotkey();
    }

    /// <summary>按当前设置注册/注销全局热键（主窗口 + 待办窗口两个热键）</summary>
    public void RegisterHotkey()
    {
        if (_hotkeyWindow == null || _hotkeyWindow.Handle == IntPtr.Zero) return;
        var settings = SettingsService.Instance.Settings;

        // 主窗口热键（id=1）
        Win32Helper.UnregisterHotKeyId(_hotkeyWindow.Handle, Win32Helper.HotkeyIdMain);
        if (settings.TriggerHotkey)
        {
            Win32Helper.RegisterHotKeyId(_hotkeyWindow.Handle, Win32Helper.HotkeyIdMain,
                (uint)(settings.HotkeyModifiers | Win32Helper.MOD_NOREPEAT),
                (uint)settings.HotkeyVirtualKey);
        }

        // 待办窗口热键（id=2）
        Win32Helper.UnregisterHotKeyId(_hotkeyWindow.Handle, Win32Helper.HotkeyIdTodo);
        if (settings.TodoTriggerHotkey)
        {
            Win32Helper.RegisterHotKeyId(_hotkeyWindow.Handle, Win32Helper.HotkeyIdTodo,
                (uint)(settings.TodoHotkeyModifiers | Win32Helper.MOD_NOREPEAT),
                (uint)settings.TodoHotkeyVirtualKey);
        }
    }

    /// <summary>重新应用显示/隐藏触发设置（设置页修改后调用）</summary>
    public void ApplyTriggerSettings()
    {
        var settings = SettingsService.Instance.Settings;
        _inputHookService?.ApplySettings(settings);
        RegisterHotkey();
    }

    #endregion

    #region 退出

    /// <summary>完全退出软件（托盘"退出"）</summary>
    public void ExitApplication()
    {
        IsExiting = true;

        _inputHookService?.Dispose();
        _activationRegistration?.Unregister(null);
        _trayService?.Dispose();

        try { _mainWindow?.Close(); } catch { /* 忽略 */ }
        try { _settingsWindow?.Close(); } catch { /* 忽略 */ }
        try { _todoWindow?.Close(); } catch { /* 忽略 */ }

        _singleInstanceMutex?.ReleaseMutex();
        Current.Exit();
    }

    #endregion

    /// <summary>托盘菜单文字刷新（语言切换后）</summary>
    public void RefreshTrayLocalization() => _trayService?.RefreshLocalization();

    /// <summary>
    /// 把主题应用到所有已创建的窗口（设置页切换主题后调用，立即生效）。
    /// </summary>
    public void ApplyThemeEverywhere()
    {
        var mode = SettingsService.Instance.Settings.Theme;
        if (_mainWindow != null)
        {
            ThemeService.Apply((FrameworkElement)_mainWindow.Content, mode);
            ThemeService.ApplyCaptionButtonColors(_mainWindow.AppWindow, (FrameworkElement)_mainWindow.Content);
        }
        if (_settingsWindow != null)
        {
            ThemeService.Apply((FrameworkElement)_settingsWindow.Content, mode);
            ThemeService.ApplyCaptionButtonColors(_settingsWindow.AppWindow, (FrameworkElement)_settingsWindow.Content);
        }
        if (_todoWindow != null)
        {
            ThemeService.Apply((FrameworkElement)_todoWindow.Content, mode);
            ThemeService.ApplyCaptionButtonColors(_todoWindow.AppWindow, (FrameworkElement)_todoWindow.Content);
        }
    }

    /// <summary>只刷新主窗口面板显示（子分类风格/布局/尺寸等显示参数变化时调用）</summary>
    public void RefreshMainWindowPanel()
    {
        _mainWindow?.RefreshMainPanel();
    }

    /// <summary>只重建右侧项目面板（布局/尺寸等与左侧分类列表无关的设置变化时调用，避免分类列表无谓刷新）</summary>
    public void RefreshMainWindowPanelOnly()
    {
        _mainWindow?.RefreshPanelOnly();
    }

    /// <summary>只应用主窗口行为设置（置顶/锁定尺寸变化时调用，不重建面板）</summary>
    public void ApplyMainWindowBehavior()
    {
        _mainWindow?.ApplyBehaviorSettings();
    }

    /// <summary>只应用待办窗口行为设置（置顶/锁定尺寸变化时调用）</summary>
    public void ApplyTodoWindowBehavior()
    {
        _todoWindow?.ApplyBehaviorSettings();
    }

    /// <summary>云同步设置变化（设置页保存/关闭云同步）时通知待办窗口立即响应：
    /// 显示/隐藏"云同步"按钮并自动刷新一次（需求：不用关开窗口）</summary>
    public void OnTodoSyncSettingsChanged()
    {
        _todoWindow?.OnSyncSettingsChanged();
    }

    /// <summary>外部变化（重要底色设置、删除/导入 todo 数据）后刷新待办窗口视图</summary>
    public void RefreshTodoWindowView()
    {
        _todoWindow?.RefreshView();
    }

    /// <summary>重建主窗口左下角附属功能栏（设置-常规里勾选/排序后调用）</summary>
    public void RefreshAuxiliaryBar()
    {
        _mainWindow?.RebuildAuxiliaryBar();
    }

    /// <summary>把窗口材质应用到所有已创建的窗口（设置-外观切换材质后立即生效）</summary>
    public void ApplyBackdropEverywhere()
    {
        var backdrop = SettingsService.Instance.Settings.Backdrop;
        BackdropService.Apply(GetMainWindow(), backdrop);
        if (_settingsWindow != null)
        {
            BackdropService.Apply(_settingsWindow, backdrop);
        }
        if (_todoWindow != null)
        {
            BackdropService.Apply(_todoWindow, backdrop);
        }
    }

    /// <summary>只刷新主窗口标题（自定义标题变化时调用）</summary>
    public void RefreshMainWindowTitle()
    {
        _mainWindow?.RefreshTitle();
    }

    /// <summary>刷新所有窗口的标题和托盘提示（自定义标题确认后调用，需求-BUG3；新窗口都要接入联动）</summary>
    public void RefreshAllTitles()
    {
        _mainWindow?.RefreshTitle();
        _settingsWindow?.RefreshTitle();
        _todoWindow?.RefreshTitle();
        _trayService?.RefreshTooltip();
    }

    /// <summary>数据显示标题：自定义标题优先，留空用默认软件名</summary>
    public static string GetDisplayTitle()
    {
        var custom = SettingsService.Instance.Settings.CustomTitle;
        return string.IsNullOrWhiteSpace(custom) ? AppInfoService.Config.AppName : custom;
    }

    /// <summary>数据被删除/导入后，让主窗口从磁盘重新加载数据（需求-BUG2）</summary>
    public void ReloadMainWindowData()
    {
        _mainWindow?.ReloadData();
    }
}

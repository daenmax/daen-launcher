> 该文件是项目的"长期记忆"，每次完成一轮需求后都要更新。

# Daen Launcher 项目记忆

## 项目理解
- **是什么**：Daen Launcher，"Daen的Windows快捷启动工具"，WinUI 3 桌面应用。
- **面向谁**：Windows 用户，快速启动软件/文件夹/网址，附带待办、随手记、剪贴板（后三个后续版本开发）。
- **官网**：https://github.com/daenmax/daen-launcher（配置在 `DaenLauncher/Assets/appconfig.json`，嵌入 exe，禁止硬编码在代码里）。

## 技术架构
- 语言：C#（.NET 10，SDK 10.0.401），TFM `net10.0-windows10.0.22621.0`
- UI：WinUI 3，NuGet `Microsoft.WindowsAppSDK 2.5.1`（拆分依赖 InteractiveExperiences 2.1.9 + WinUI 2.3.9）
- 托盘：NuGet `H.NotifyIcon.WinUI 2.5.0-beta.3`
- 打包：Unpackaged + SelfContained + PublishSingleFile（单文件 exe，无需用户装运行时），产物约 130MB
- 数据：全部 JSON 存 `exe同级\data\`（不可写回退 `%LocalAppData%\DaenLauncher\data`）

## 目录结构
```
DaenLauncher/
  App.xaml(.cs)          入口：单实例(Mutex+Event)、服务初始化、托盘、钩子、窗口管理
  MainWindow.xaml(.cs)   主窗口（标题栏/分类列表/项目面板/拖拽/右键菜单）
  SettingsWindow.xaml(.cs) 设置窗口外壳（NavigationView 汉堡导航）
  Views/SettingsPageBase.cs、SettingsPages.cs  设置页（常规/外观/数据/启动器/占位/关于）
  Views/Dialogs/Dialogs.cs   分类/子分类/项目编辑弹窗 + 管理子分类弹窗
  Controls/IconPicker.cs 图标选择器（emoji/自定义上传/group_分组 三个来源）
  Controls/WrapPanel.cs  自定义换行面板（此版 WinUI 没有内置 WrapPanel！）
  Models/Models.cs、AppSettings.cs  数据模型（分类/子分类/项目/设置）
  Services/  DataPathService、JsonStore、SettingsService、LauncherDataService、
             LocalizationService、IconService、ItemIconService、LnkResolver、
             LauncherRunner、AutoStartService、TrayService、InputHookService、
             Win32Helper、DropResolver、DataService、WindowPositionHelper、
             QrCodeService、WifiService、TimeSyncService、
             ThemeService、AppInfoService、RelayCommand
  Assets/    appconfig.json、logo、设置图标（全部 EmbeddedResource）
  Languages/ zh-CN.json、en-US.json（EmbeddedResource，启动时释放到 data\language）
build.bat / clean.bat   双击可用的编译/清理脚本
```

## 核心模块职责
- **JsonStore<T>**：每文件一把 SemaphoreSlim 串行访问，临时文件+Replace 原子写，.bak 备份，内存缓存。
- **LocalizationService**：内嵌 zh-CN/en-US → 释放到 data\language → 扫描目录支持自定义语言；系统语言 zh-CN 则默认中文否则英文；`T(key)` 翻译，回退 English。
- **TrayService**：1x1 隐藏 Window 承载 TaskbarIcon；菜单为代码构建的 MenuFlyout（SecondWindow 模式，有动画）；双击=DoubleClickCommand，右键=RightClickCommand（此 beta 版没有 Tray*DoubleClick 等 CLR 事件！只有命令属性）。
- **单实例**：Mutex `DaenLauncher_SingleInstance_Mutex` + EventWaitHandle `DaenLauncher_Activate_Event`；二实例等旧实例 3 秒（重启场景）后 Set 事件退出；主实例 RegisterWaitForSingleObject 显示主窗口。
- **InputHookService**：WH_MOUSE_LL（侧键/中键）+ WH_KEYBOARD_LL（Ctrl/Alt 双击检测）全局钩子。
- **热键**：RegisterHotKey 注册在主窗口 HWND，comctl32 SetWindowSubclass 拦 WM_HOTKEY。
- ~~**ShellContextMenuHelper**~~：已于第十九轮彻底移除（需求放弃，见第十九轮与关键决策补充）。
- **ItemIconService**：SHGetFileInfo 提取图标 → png 缓存到 data\icon\cache\<ItemId>.png + .meta 记录源路径。
- **DataService**：导出（勾选项打包 zip，zip 内前缀 data/）、导入（先备份 data→日期目录，删旧再解压）、删除。
- **窗口显隐**：主/设置窗口 Closing 事件 `e.Cancel=true` + Hide（`App.IsExiting` 区分退出）；主窗口去掉 WS_SYSMENU 实现无系统标题栏按钮。

## 最新变更（2026-09-28 第四轮：5 BUG + 4 优化）
- **BUG1 缩放窗口崩溃（100% 复现）**：crash.log 实锤——`JsonStore.Save` 的 `File.Replace` 在高频缩放保存 settings.json 时抛 IOException（"无法删除要替换的文件"）→ UnhandledException → 崩溃。三重修复：① Save 加 IOException 重试（4 次×40ms）；② 防抖计时器 Tick 只订阅一次（原 ScheduleSaveSettings 每次都 `+=` 叠加订阅）；③ `App.UnhandledException` 里 `e.Handled = true` 兜底（记录日志但不崩进程）。SetWindowPos 压测 60 次 + 真实边框拖拽 15 次无崩溃。
- **BUG2 分类弹窗第二次打不开**：emoji 缓存面板挂在旧弹窗的 ScrollViewer 上（`ScrollViewer.Content` 是引用挂载），二次挂载抛 "Element is already the child of another element"（crash.log 实锤）。修复：静态记录 `_cachedEmojiHost`，重挂前先 `oldHost.Content = null`；emoji 磁贴点击改静态路由到 `_activeInstance`（避免处理器随弹窗叠加）。
- **BUG3 Shell 菜单崩溃**：两个根因。① URL/UWP/协议路径（非文件系统路径）传给 `ILCreateFromPath` 引发原生崩溃 → 入口守卫直接跳过；② `GetUIObjectOf` 返回的对象 **QI IContextMenu3 失败**（crash.log 实锤 Specified cast is not valid）→ 改为 `as` 平滑降级（IContextMenu3 → IContextMenu2 → IContextMenu），owner-draw 消息按实际支持的接口转发。另外 WinUI 菜单点击回调里直接开 Win32 菜单会冲突 → DispatcherQueue.TryEnqueue 延迟一拍。实测：原生右键菜单完整弹出（含图标和第三方扩展），应用存活。
- **BUG4 lnk 参数乱码（"你헊翵"等）**：手写 IShellLinkW 互操作编组不可靠。重写 `LnkResolver`：首选 **WScript.Shell COM**（反射 InvokeMember，无 dynamic 依赖），STA 线程守卫，保留手写 IShellLinkW 和 .url 文本解析做两级回退。编辑弹窗实测无乱码。
- **BUG5 子分类排序要重启**：管理子分类的上移/下移漏了刷新 → 补 `mainWindow.RefreshMainPanel()`，立即生效。
- **优化1** 列表模式显示"列表视图位置"下拉框（居左/居中/居右），仅列表模式可见；`ItemHorizontalAlignment` 枚举。
- **优化2** 显示大小卡片新增横向/纵向间距滑条（0-32，默认 4，用项目 Margin 平分实现，Toolkit WrapPanel 无间距属性）；恢复默认按钮一并重置间距。
- **优化3** 编辑项目弹窗每个输入框上方带标签（名称/路径/备注/命令行参数），并显示只读的"类型"（Main.ItemType.* 键）。
- **优化4** 新建项目分类自动带一个"默认子分类"。
- **测试**：UIA/SendInput 实测全部通过：40 次缩放无崩溃、分类弹窗连开 3 次、Shell 原生菜单完整弹出且存活、编辑弹窗标签+类型+无乱码、列表模式+位置下拉+间距滑条渲染正确。
- **数据丢失防护**：发现多次强杀进程可能撞上保存瞬间丢 launcher.json → JsonStore.Load 时主文件缺失但 .bak 存在则自动恢复。

## 最新变更（2026-09-28 第五轮：5 BUG + 4 优化）
- **BUG1 Shell 菜单点击无反应**（上轮的菜单能显示但命令不执行）：
  - 用独立控制台程序隔离测试发现：**该 shell 对象的菜单项 id 即 InvokeCommand 的动词序号**，减 1（教科书式的 id-idCmdFirst）会 E_INVALIDARG（0x80070057）。实测 `lpVerb = (IntPtr)cmd` 后命令真实执行（Notepad++/记事本都被成功启动）。
  - 顺带修复：菜单弹出前 `SetForegroundWindow(hwnd)`（否则菜单"看得见点不动"）；**整个流程移到专用 STA 线程**执行（UI 线程上"菜单交互后 Invoke 必失败、无菜单则成功"的诡异现象彻底规避）；GetUIObjectOf 的 hwnd 传 Zero。
  - 留有 invoke 失败日志（crash.log），个别第三方扩展命令失败属正常。
- **BUG2 新键没汉化**：data\language 里的语言文件是旧版释放的，升级后新增键不在其中 → 回退英文包。修复：**内置语言包（简体中文/English）每次启动覆盖写入**（内容不变则跳过写盘），用户自定义语言放新文件不受影响。
- **BUG3 卡片拖放仍进第一个子分类**：卡片/Tab 内容没开 AllowDrop → DragEnter 根本不在卡片上触发。修复：card/grid `AllowDrop=true` + Drop 时用指针位置对 _panelTargets 实时命中（比悬停记录更可靠）。
- **BUG4 lnk 参数乱码**：重写 LnkResolver 为 WScript.Shell COM（反射 InvokeMember，STA 线程守卫，IShellLinkW 手写互操作保留为回退）。
- **BUG5 子分类排序立即生效**：管理弹窗的上移/下移补 RefreshMainPanel。
- **优化1** 列表模式下显示"列表视图位置"（居左/居中/居右）；**优化2** 横向/纵向间距滑条（含恢复默认）；**优化3** 编辑项目弹窗每个输入框带标签 + 显示只读类型；**优化4** 新建分类自动带默认子分类。
- **测试**：Shell 菜单端到端（点菜单项 → 命令执行 → Notepad++ 启动）、语言键完整性校验（0 缺失）、编译 0 错误 0 警告。

## 最新变更（2026-09-28 第六轮：任务栏图标 + 窗口记忆 + 分类设置 + Shell菜单下线）
- **BUG 任务栏无 logo**：三层根因连环。① 用户提供的 app.ico（logo_图标组.ico，179KB 多尺寸）**无法被 csc /win32icon 处理**（构建静默失败，exe 不含图标资源）→ 用 PowerShell 按尺寸生成标准 DIB 条目后由 Python 合并成标准多尺寸 `app_multi.ico`（16/32/48/256，44KB）作为 `<ApplicationIcon>`（已实测嵌入 exe ✓）。② unpackaged WinUI 3 的**任务栏按钮图标不读取 exe 资源、也不响应 AppWindow.SetIcon** → 运行时用 `WM_SETICON`（ICON_SMALL=16x16 句柄 + ICON_BIG=32x32 句柄，SetClassLongPtr 双保险），且**必须按尺寸创建句柄**（传 256px 大图句柄会被任务栏拒绝）。③ 任务栏/Explorer 有图标缓存——修好后需重启 explorer（或重启系统）才能看到。
- **调整 Shell 菜单下线**：按需求删除 ShellContextMenuHelper.cs（备份在 .trash）及相关调用，"打开资源管理器菜单"菜单项保留，点击弹"开发中，敬请期待"。
- **优化 窗口尺寸记忆**：设置窗口新增 SettingsWindowWidth/Height 持久化（主窗口此前已支持）。
- **优化 项目分类设置卡片**：图标位置（左边/右边，CategoryIconPosition）+ 整体位置（居左/居中/居右，CategoryAlignment），渲染时动态调整模板内图标/文字顺序与对齐。
- **测试**：Release 单文件 exe ExtractAssociatedIcon 验证 avg=67（logo）✓；任务栏按钮截图确认 logo 显示 ✓；语言键完整性 0 缺失。

## 最新变更（2026-09-29 第七轮：3 BUG + 4 优化）
- **BUG1 列表模式不铺满整行**：原实现把对齐设在按钮的 HorizontalAlignment 上，按钮只占内容宽度，悬停高亮不满行。修复：列表模式按钮 `HorizontalAlignment=Stretch`（铺满整行），对齐改走 `HorizontalContentAlignment`；平铺模式新增 `ItemContentAlignment`（图标和文字位置，居左/中/右，仅平铺模式显示该下拉框）。
- **BUG2 列表模式无法滚动**：Tab 风格的 `BuildTabContent` 里项目区直接放 Grid/Border，没有 ScrollViewer → 内容被裁剪且无滚动条。修复：包一层 `ScrollViewer`（Auto 滚动条 + `HorizontalContentAlignment=Stretch` 保证铺满整行）。卡片风格本来有外层 ScrollViewer 不受影响。顺带：列表容器去掉硬编码 `Spacing=4`（间距已由项目 Margin 控制，两处叠加导致间距偏大）。
- **BUG3 导出必失败**：系统的"另存为"对话框（PickSaveFileAsync）会**预先创建空 zip 文件**，而 `ZipFile.Open(path, ZipArchiveMode.Create)` 内部用 `FileMode.CreateNew`，文件已存在直接抛 IOException → "导出失败"。修复：`DataService.Export` 改为 `new FileStream(finalPath, FileMode.Create...)` + `new ZipArchive(stream, Create)`；失败提示附上 ex.Message 方便定位。
- **优化1 设置卡片图标**：`SettingsPageBase.MakeCard` 由嵌入 png 改为 FontIcon 绘制（Segoe Fluent Icons 字体），新增 `FluentGlyphs` 常量类（20+ 字形，字体名也是常量）；全部 24 张卡片换字形，png 仍在 Assets 里但设置卡片不再引用。
- **优化2 项目图标文字对齐**：见 BUG1，平铺模式新设置 `Settings.ContentPosition.*`。
- **优化3 间距默认值**：横向/纵向间距默认 4 → 8（AppSettings 默认值 + 恢复默认按钮两处）。
- **优化4 显示位置"上次位置"**：`ShowPosition.LastPosition`（枚举追加在末尾，SelectedIndex 兼容）；AppSettings 新增 `LastWindowX/Y`（物理像素，-1=未记录）；MainWindow 监听 `AppWindow.Changed`（DidPositionChange）防抖 500ms 保存，HideWindow/真正退出前强制落盘；显示时超出屏幕会钳制回工作区。
- **语言**：zh-CN/en-US 新增 Settings.ShowPosition.LastPosition、Settings.ContentPosition(.Left/.Center/.Right)。
- **测试**：Debug 编译 0 错误 0 警告；其余待用户实测。

## 最新变更（2026-09-29 第八轮：3 BUG 修复）
- **BUG1 平铺模式对齐/列不对齐**：两个根因。① 上一轮"图标和文字位置"设在按钮 `HorizontalContentAlignment`，但按钮按内容大小排布（宽度=内容宽度），对齐自然看不出效果；② Toolkit WrapPanel 各格子随内容宽窄不一，列不齐。修复：新增 `Controls/UniformWrapPanel.cs`（等宽换行面板）——格子宽=所有子元素最大需求宽度，每行列数只由面板宽度决定，最后一行平分铺满整行；按钮 Stretch 填满格子，内容对齐（居左/中/右）由 HorizontalContentAlignment 真正生效。主窗口 3 处（BuildItemsHost/PanelHost_Drop/重排方向判断）从 WrapPanel 切换；移除 CommunityToolkit WrapPanel 的 using 别名（包仍引用，未卸载）。
- **BUG2 删除数据后界面不刷新**：JsonStore 有内存缓存，删文件不影响已加载数据。修复：勾选 launcher 删除后 `LauncherDataService.Reload()`（清缓存重读，空则自动建默认分类）+ `MainWindow.ReloadData()`（LoadCategories 全量重建，新增 public 方法）；勾选 config（设置/语言）因内存缓存深，删除后弹重启提示（复用 SettingsActions.PromptRestart）。
- **BUG3 软件标题不生效**：实锤 `App.RaiseSettingsChanged()` **没有任何订阅者**（死代码），标题改动从来没即时生效过。重构：标题卡片加"确定"按钮（回车等效）→ `App.RefreshAllTitles()` 统一刷新主窗口 + 设置窗口 + 托盘 tooltip；新增 `App.GetDisplayTitle()`（自定义标题优先）替换 MainWindow/SettingsWindow/TrayService 四处重复逻辑；删除死事件 SettingsChanged。
- **测试**：Debug 编译 0 错误 0 警告；UI 行为待用户实测。

## 最新变更（2026-09-29 第九轮：2 BUG + 3 优化）
- **BUG1 改布局/尺寸会刷新左侧分类列表**：SaveAndRefreshPanel 走 RefreshMainPanel（分类列表+面板都重建）。拆分：新增 `RefreshPanelOnly`（只 RebuildPanel）+ `App.RefreshMainWindowPanelOnly`；布局/间距/尺寸/子分类风格/启动方式等全部改走"只重建面板"，仅分类相关设置（分类图标/文字大小、图标位置、整体位置）保留全量刷新。
- **BUG2 列表模式悬停高亮不满行**：根因——Tab 风格里垂直 StackPanel 作为 ScrollViewer 内容，ScrollViewer 的 ContentPresenter 未把内容拉伸到视口宽，StackPanel 只滚到最宽子项的宽度，按钮 Stretch 也只到那个宽度。修复：给 `UniformWrapPanel` 加 `FixedColumns` 属性，列表模式 `FixedColumns=1`——测量时直接报告"可用宽度"作为期望宽度（不依赖宿主拉伸），排列时每行铺满 finalSize.Width，悬停高亮必然整行。
- **优化1 拖动动画**：拖动项目跨过阈值后，原按钮 Opacity=0.4（示意被拖走），同时用 `RenderTargetBitmap.RenderAsync` 拍按钮快照，作为幽灵 Image 放在根 Grid 顶部的 `_dragLayer`（Canvas，IsHitTestVisible=false，RowSpan=2）里，按"按下点相对按钮的偏移"跟随鼠标（像抓在手里）；松手/取消时移除幽灵恢复透明度（EndDragGhost 在 ResetItemDrag 兜底）。
- **优化2 logo 更换**：新 logo 在 `素材\logo`（logo_1024.png / logo_64.png / logo_图标组.ico，ico 为标准多尺寸 16~256px 含 32bpp）。旧 4 个文件（app.ico/app_multi.ico/logo_1060.png/logo_512.png）按规范备份到 `.trash\DaenLauncher\Assets\logo\` 后从项目移除。接线：csproj ApplicationIcon=logo_图标组.ico（exe 图标）；`EnsureAppIconExtracted`/托盘 `LoadEmbeddedIcon` 改为嵌入资源 `DaenLauncher.Assets.logo.logo_图标组.ico`（任务栏 WM_SETICON 16/32、AppWindow.SetIcon、托盘全部用同一个 ico）；窗口内标题栏小图和设置窗口标题栏用 logo_64.png（WinUI 的 Image 不能直接渲染 .ico，用同 logo 的 64px png）；关于页用 logo_1024.png。
- **优化3 列表模式对齐**："列表视图位置"与"图标和文字位置"合并为一个设置 `ItemContentAlignment`（两个下拉框控制的其实是同一件事），平铺/列表通用，`ListItemAlignment` 属性从 AppSettings 移除（旧 settings.json 多余字段反序列化自动忽略）。列表模式 FixedColumns=1 + 内容居左时，每行图标都从行首开始 → 图标列严格对齐，不受文字长度影响。
- **测试**：Debug 编译 0 错误 0 警告；UI 行为待用户实测。

## 最新变更（2026-09-29 第十轮：2 BUG + 3 优化，其中拖动动画已下线）
- **BUG2 列表悬停高亮仍不满行（上轮修复无效的真根因）**：ScrollViewer 的 `HorizontalScrollBarVisibility` 默认值允许横向滚动 → 内容以"无限宽"测量，UniformWrapPanel 拿不到视口宽度，只能按内容收缩。修复：Tab 和卡片两个 ScrollViewer 显式 `HorizontalScrollBarVisibility=Disabled`（顺带修掉平铺模式项目多时可能变成单长行的隐患）。**教训：WinUI ScrollViewer 默认横/纵滚动都是 Auto，测量约束是 Infinity，包内容器必须显式禁用不需要的方向。**
- **BUG2 列表居左+文字在下图标仍居中**：WinUI 中固定宽度的子元素（图标 Grid）在默认 Stretch 对齐下会被**居中**放置。修复：内容块改为收缩对齐（由按钮 HorizontalContentAlignment 定位），图标/文字块的对齐显式跟随 ItemContentAlignment 设置 → 居左时图标贴行首，每行图标列严格对齐。
- **优化1 启动时项目路径丢失检测**：`LauncherItem.IsMissing`（`[JsonIgnore]` 运行时标记，不落盘）；`LauncherDataService.CheckMissingItems()` 在 `App.OnLaunchedCore` 启动流程里执行一次（不是显示/隐藏时）；Exe/Lnk/File 查 File.Exists、Folder 查 Directory.Exists，Url/Protocol/UWP 视为有效；`LoadItemIconAsync` 对 IsMissing 项目加载嵌入的"项目无法找到"图标（Assets\Icons\项目无法找到.png，来自素材\其他素材）；编辑项目保存后立即重新评估该项目的失效状态。
- **优化2 拖动动画改桌面风格**（已随下线调整移除，此条仅留档）：去掉"保持按下点偏移"，幽灵图标改为**中心跟随鼠标箭头**（PositionDragGhost 用 Width/Height 居中计算），和拖动桌面图标手感一致。
- **优化3 桌面拖入可放任意位置**：`PanelHost_Drop` 在 await 之前先取目标容器和指针位置（DragEventArgs await 后不可用），落地时找"中心离鼠标最近的项目"，鼠标在其右/下插到其后、否则插到其前（列表看垂直、平铺看水平），数据和 UI 同步按序插入；面板不在可视树上（如拖到非当前 Tab）时退回追加末尾。
- **测试**：Debug 编译 0 错误 0 警告；UI 行为待用户实测。
- **调整 拖动动画下线（2026-09-29 用户决定）**：不做"拖动拽着图标"动画，已完整移除（_dragLayer 覆盖层 / BeginDragGhost 快照 / PositionDragGhost / EndDragGhost 及所有调用点），拖动排序回到纯指针重排行为。**保留不受影响**：拖动排序本身、桌面拖入按位置插入（优化3）、丢失检测（优化1）；用户确认其他需求测试没问题。

## 最新变更（2026-09-29 第十一轮：拖动动画回归——按 DeskBox 文件格子方案重做原生拖拽）
- **背景**：第十轮曾下线"拽着图标"动画（当时是自绘幽灵快照方案），本轮用户要求参考 DeskBox 的"文件格子"重新实现。研究了 `参考项目\DeskBox-main` 的 `FileSurfaceContent`：**GridView/ListView 开 `CanDragItems` 用系统原生拖拽**——系统自动生成被拖项的快照跟随鼠标（"鼠标拽着图标"的动画），DataPackage.Properties 打内部标记区分内部/外部拖放，DragOver 里算插入位置，Drop 落库，`RepositionThemeTransition` 做落位动画。
- **本项目的落地方式**（不迁移 GridView，保留自定义按钮 + UniformWrapPanel 架构）：
  - `BuildItemButton`：`button.CanDrag = true` + `DragStarting`（LockIcons 时 Cancel；记录会话 `_dragItem/_dragItemSub/_dragButton`；源按钮 Opacity=0.45 示意被拖走；Data.Properties 打标记 `DaenLauncher.InternalItemDrag` + SetText + RequestedOperation=Move）+ `DropCompleted`（恢复透明度、清会话）。
  - `BuildItemsHost`：面板 `AllowDrop=true` + `ItemPanel_DragOver`（内部拖动 → `MoveDraggedButtonByPosition` 实时重排 + 接受 Move + DragUIOverride 隐藏系统角标；外部拖放 → 不处理冒泡给 PanelHost）+ `ItemPanel_Drop`（同面板 `CommitPanelOrder` 落库；跨面板 `FindSubByPanel` 移动 + RebuildPanel）。
  - `PanelHost_DragOver/Drop`：加内部拖动分支——拖到 Tab 头/卡片/空白区松手也能移动项目（指针命中 `_panelTargets`，退回 `_dropHoverSub/_currentTabSub`）。
  - **删除整套指针级拖动**：ItemButton_PointerPressed / ItemPanel_PointerMoved/Released / ReorderItemsByPointer / _suppressClickUntil（原生拖拽中 Click 不会触发，无需防误点击）及 5 个状态字段。拖动中实时重排逻辑（最近中心算法）保留，只是从指针事件搬进 DragOver。
- **与旧方案的区别**：不再自绘快照/覆盖层，跟随鼠标的图标由系统生成（含跨窗口拖动也跟随）；源按钮变淡留在原地让位；松手落库，中途取消（Esc/拖出）由 DropCompleted 兜底恢复。
- **修复：彻底无法拖动（用户实测）**：`Button.CanDrag=true` 在 WinUI 3 上**不会自动发起拖拽**——ButtonBase 吞掉指针输入，系统的"按住拖动"手势检测永远不触发（微软 Q&A 确认是设计限制，TextBox 同样）。而 DeskBox 对 Button 类元素（Todo 颜色筛选按钮）的真正做法是：**保留 CanDrag + DragStarting 填数据包，但用指针事件手动检测拖动阈值（5px），超过后 `await button.StartDragAsync(e.GetCurrentPoint(button))` 主动发起原生拖拽**，之后 DragStarting/DragOver/Drop/DropCompleted 全套原生流程照常走。落地：ItemButton_PointerPressed/Moved（AddHandler handledEventsToo:true，阈值 25）/Released/CaptureLost + StartDragAsync；恢复 `_suppressClickUntil`（拖拽发起后短时间屏蔽 Click 防误启动，LaunchItem 守卫同步恢复）。
- **测试**：Release 编译 0 错误（产物 build\DaenLauncher.exe）；拖动手感/外部拖入/跨子分类移动待用户实测。

## 最新变更（2026-09-30 第十二轮：重排改"覆盖 70% 触发 + 让位滑动动画"）
- **回滚点**：第十一轮修好的拖拽版本已提交 git（commit 991b8b1"检查点：拖动图标原生拖拽动画可用版本"），随时可回滚。参考项目\（45MB 第三方参考代码）刻意未纳入 git。
- **需求**：用户反馈"最近中心"算法过于抖动（指针一碰邻格就换位），改为——拖动图标**覆盖某项目 ≥70% 面积**才判定"要放到这个位置"，被覆盖项目及其后项目整体往后让一位，并加滑动动画。
- **实现**（MainWindow.xaml.cs）：
  - `DragCoverThreshold = 0.7` 常量；`MoveDraggedButtonByPosition` 重写：跟随鼠标的快照近似为"以指针为中心、与被拖按钮同尺寸"的矩形，遍历项目算相交面积/目标面积，取覆盖比例最高且 ≥70% 的为目标；**拼接移动**：移除自己 → 插到 targetIndex（见下），覆盖不足则完全不动（迟滞防抖）。平铺/列表通吃，不再需要区分水平流/垂直流（旧的方向判断逻辑删除）。
  - **修复：往前拖（前面项目拖到后面）不好使**：最初对"目标在自己后面"的情况用了 `targetIndex-1`（插到目标之前）→ 被拖按钮永远落后指针一格、盖住紧邻下一格时插入位==当前位被 no-op 守卫拦掉完全没反应。正确做法：**移除自己后一律插到 targetIndex**——往前拖 = 插到目标之前（目标及其后往后让位）；往后拖 = 移除后目标已前滑一位，插到 targetIndex 正好在它之后（落在目标原格子上）。两方向都精确落在"被覆盖项目原来的格子"。
  - **滑动动画**：BuildItemButton 给按钮挂 `RepositionThemeTransition { IsStaggeringEnabled=false }`（DeskBox 文件格子同款），格子位置变化时平滑滑动（被拖按钮自己滑进空位，被让位的项目滑到新格子）。TransitionCollection/RepositionThemeTransition 都在 `Microsoft.UI.Xaml.Media.Animation` 命名空间（该文件只 using 到 Media，需全限定）。
- **稳定性推演**：触发后被拖按钮精确落在被覆盖项目的原格子上，指针正好位于自己身上（自身不参与判定）→ 不回弹；触发后指针继续盖着同一目标时 no-op 守卫兜底。
- **测试**：Release 编译 0 错误；手感（触发灵敏度、动画效果）待用户实测，阈值 DragCoverThreshold 可调。

## 最新变更（2026-09-30 第十三轮：更换 logo + 任务栏旧图标缓存修复）
- **更换 logo**（上一会话完成）：Assets/logo 三件套（logo_1024.png / logo_64.png / logo_图标组.ico）换新，exe 文件图标（csproj 的 ApplicationIcon）、标题栏、托盘、设置窗口全部引用新资源。
- **任务栏残留旧 logo——两层缓存**：
  - ① **应用自身缓存**：`IconService.EnsureAppIconExtracted` 把内嵌 ico 释放到 `data\icon\cache\app.ico` 供 WM_SETICON/ SetIcon 使用，原逻辑"文件存在就不再释放"→ 从旧版升级的用户永远用残留的旧 ico。已改为**内嵌资源与缓存文件字节比对，不一致即重写**（升级后首次启动自动刷新）。
  - ② **Windows 资源管理器图标缓存**：Explorer 按 exe 路径缓存任务栏图标，换 exe 后旧图标可能残留，需重启 explorer / 清 iconcache_*.db / 重新固定任务栏（应用侧无法干预）。
- **app.rc 引用修正**：原引用已删除的 `Assets/logo/app.ico`（现仅在 .trash），改为 `logo_图标组.ico`，防止将来重新接入 rc 编译时构建失败（当前构建链不编译 .rc，exe 图标实际来自 `<ApplicationIcon>`）。
- **测试**：按用户要求未测试，由用户自测（跑新 exe 自动刷新缓存 + 必要时重启 explorer）。

## 最新变更（2026-09-30 第十四轮：任务栏旧图标真相 + 打开配置目录按钮）
- **任务栏旧 logo 真相（重要）**：用户改名 exe 测试仍显示旧图标 + data 目录"不自动创建"——根因是**旧版本实例一直没退出**（桌面 `新建文1件夹\DaenLauncher.exe`，早于新编译启动）。单实例互斥体使新 exe（含改名副本）启动时只激活旧实例就退出，用户看到的任务栏图标、data 目录行为全部来自旧进程。已 force kill 旧实例；新 exe 正常运行即可看到新图标（Explorer 图标缓存如残留再重启 explorer）。**教训：单实例应用测试新版本前，必须先从托盘退出旧实例。**
- **新功能 打开配置文件目录**：设置-数据页新增第一张卡片——显示实际使用的 DataRoot 真实路径（可选中文本）+ "打开目录"按钮（explorer.exe 打开，LocalAppData 回退时用户也能看出数据实际位置）。新增 FluentGlyphs.Folder（\uE8B7）和语言键 Data.OpenFolder / .Sub / .Button（zh/en 各 3 键，218 键两边对齐 0 缺失）。
- **测试**：按用户要求未整体测试，由用户自测（语言键完整性已脚本校验通过）。

## 最新变更（2026-09-30 第十五轮：Shell 菜单正式回归——右键项目打开资源管理器菜单）
- **背景**：第六轮曾按需求把 ShellContextMenuHelper 下线（备份在 .trash），菜单项占位显示"开发中"。本轮正式做回来，直接复用第四/五轮已调通的实现（当时的坑全部有解：非文件系统路径守卫、IContextMenu3→2→接口降级、SetForegroundWindow、lpVerb 直接用菜单 id、专用 STA 线程）。
- **恢复 + 完善 ShellContextMenuHelper.cs**（从 .trash 复制回来）：
  - 签名简化为 `ShowContextMenu(path, screenX, screenY)`——删掉从未使用的 ownerHwnd 参数/字段（Helper 自己在调用线程上建隐藏顶层窗口做 owner，message-only 窗口无法 SetForegroundWindow）。
  - 新增防重入守卫（Interlocked 标记）：同一时刻只允许一个 Shell 菜单，第二个请求忽略（静态接口引用会在窗口过程里转发消息，并发会串）。
  - finally 里清空静态接口引用 + 释放标记：异常中断也不残留失效对象。
- **MainWindow 接线**：右键菜单"打开资源管理器菜单"→ `ShowShellContextMenuForItem(item)`：
  - Url/Protocol/Uwp 无本地文件 → 弹提示（新键 Main.ShellMenu.NotSupported）；
  - 路径为空/不存在 → 弹提示（新键 Main.ShellMenu.PathMissing）；
  - 有效路径 → DispatcherQueue.TryEnqueue 延迟一拍（等 WinUI 菜单完全关闭）→ GetCursorPos 取鼠标处为弹出位置 → **专用 STA 线程**执行整个 Shell 菜单流程。
- **语言**：zh-CN/en-US 各 +2 键（Main.ShellMenu.NotSupported / .PathMissing，220 键对齐）。
- **测试**：Debug 编译 0 错误 0 警告；菜单弹出/命令执行待用户实测（此前第五轮已端到端实测过同款实现：菜单完整弹出含图标和第三方扩展、点菜单项命令真实执行）。

## 最新变更（2026-09-30 第十六轮：修复 Shell 菜单"能弹出但点击无效"）
- **用户实测**：第十五轮后菜单能正常弹出（含图标和第三方扩展），但点任何菜单项都没反应。
- **根因：lpVerb 整体偏移一位**。`CMINVOKECOMMANDINFO.lpVerb` 官方约定是**相对 idCmdFirst 的偏移量**，第五轮代码 `lpVerb = (IntPtr)cmd` 直接传了绝对菜单 id（idCmdFirst=1），点第 1 项实际执行偏移 1 的第 2 条命令，点最后一项超出范围 → 无效/E_INVALIDARG。
- **第五轮错误结论的来源（纠正）**：当时的控制台隔离测试大概率用了 `idCmdFirst = 0`（菜单 id 即偏移，"直接用 cmd"恰好正确、"减 1"恰好越界 E_INVALIDARG），但把结论照搬到了正式代码的 `idCmdFirst = 1` 上 → 整体偏移一位。**教训：lpVerb 必须始终用 `cmd - idCmdFirst`，idCmdFirst 是多少由自己的 QueryContextMenu 调用决定。**
- **修复**（ShellContextMenuHelper.cs，参考 ChatGPT 分析验证）：① `lpVerb = cmd - 1`；② `CMINVOKECOMMANDINFO.hwnd` 传隐藏宿主窗口（此前传 Zero，部分会弹 UI 的命令需要宿主）；③ `GetUIObjectOf` 也传宿主窗口；④ invoke 失败日志带上 cmd/verbOffset/hr 便于继续定位。
- **保留不动**：专用 STA 线程、IContextMenu3→2→1 降级、隐藏 owner 窗口、SetForegroundWindow、owner-draw 消息转发。
- **测试**：Debug 编译 0 错误 0 警告；命令执行待用户实测（若仍有个别第三方扩展命令无效，查 crash.log 的 verbOffset/hr）。

## 最新变更（2026-09-30 第十七轮：彻底修复 Shell 菜单点击无效——Windows 11 命令表重排适配）
- **用户实测**：第十六轮 `cmd-1` 后依然全无反应。crash.log 抓到实锤：`invoke failed cmd=151 verbOffset=150 hr=0x80070057`、`cmd=20 → 19 → 同样失败`。
- **独立控制台复现程序**（%TEMP%\ShellCtxTest，完整复刻 Helper 流程 + 全量日志 + 自动键盘/鼠标点击）拿到决定性数据：
  - QueryContextMenu 返回码 R=199（不是"最大ID"而是命令总数）；
  - 菜单共 40 项但 ID 稀疏：打开=164、编辑=167、属性=20、复制路径=196——**ID ≠ idCmdFirst+序号**；
  - `GetCommandString` 全表扫描（0..204 逐个探动词，无副作用）显示 'open' 在槽 136=164-28、'edit' 在 139=167-28、'CopyAsPath' 在 168=196-28……全部差 **基数 28**；
  - `InvokeCommand(cmd-1)`、`InvokeCommand(cmd)`、EX 结构体 + UNICODE + ptInvoke 全部 E_FAIL/E_INVALIDARG；
  - **`InvokeCommand(cmd-28)` → S_OK 且 test.txt 真实打开**（记事本++ 窗口标题验证）。
- **根因（Windows 11 行为）**：菜单弹出过程中 Shell 会重排内部命令表——弹出前"槽位=ID-idCmdFirst"（教科书态，'properties' 在槽 19）；弹出后现代命令部分整体 +经典块大小（本机 27），即 ID=槽位+28，而经典命令（剪切/复制/删除/属性/创建快捷方式）ID 不变。教科书公式在弹出后的状态上整体偏移 → 全部失败。第五轮"直接用 cmd 可用"的错误结论也是同一机制的误观测。
- **base 的运行时求法（无副作用）**：`base = R - V`，R=QueryContextMenu 返回码，V=用 GCS_VALIDATEW（只验证存在、不执行）从高到低探到的最大有效槽位。本机 199-171=28。该公式同时兼容 Win10（V=R-1 → base=1 → 退化为教科书公式）。
- **修复后的完整算法**（ShellContextMenuHelper.ShowContextMenuCore）：
  1. QueryContextMenu(idCmdFirst=1) 记录 R；
  2. 菜单弹出（原有逻辑不变：STA 线程/隐藏窗口/SetForegroundWindow/消息转发）；
  3. 用户选中 cmd 后：`base = R - FindMaxValidSlot()`；`verbOffset = cmd >= base ? cmd - base : cmd - IdCmdFirst`；
  4. 安全兜底：GCS_VALIDATE 验证槽位存在才执行，否则记日志放弃（宁可不动也不乱发命令）；
  5. `CMINVOKECOMMANDINFOEX` + `CMIC_MASK_UNICODE|CMIC_MASK_PTINVOKE` + ptInvoke=弹出点 执行，失败记 verb/hr 到 crash.log。
- **测试**：复现程序端到端验证（打开→记事本++ 打开文件 ✓）；Debug 编译 0 错误 0 警告；build.bat 已出 Release exe 待用户实测。

## 最新变更（2026-09-30 第十八轮：修复点击菜单项百分百闪退——改用动词字符串调用）
- **用户实测**：第十七轮后点任意菜单项百分百闪退。事件查看器实锤：0xc0000005 访问违例、故障模块 = DaenLauncher.exe 自身（进程内 COM 状态被破坏的典型特征），点 3 次崩 3 次。
- **直接根因（我的低级错误）**：探测命令槽位时把 `GCS_VALIDATEW` 写成了 **6——那是 GCS_INVALIDATEW（"使命令失效/释放资源"）**！等于朝 Shell 处理器连发 ~28 次"作废你的状态"然后紧接着 InvokeCommand → 进程内访问违例。正确值是 **7**。（教训：Win32 常量必须核对官方文档，GCS_VERBA=0/HELPTEXTA=1/INVALIDATEA=2/VALIDATEA=3/VERBW=4/HELPTEXTW=5/INVALIDATEW=6/**VALIDATEW=7**。）
- **更深层发现**：用正确常量重新实测后发现 **默认 Shell 处理器根本不支持 GCS_VALIDATE**（第三方处理器支持，到槽 134 就断了，'open' 在 136）→ 第十七轮的 base = R - V 公式不可靠，废弃。且命令表布局**无法稳定预测**，一切"菜单ID算数"都不可靠。
- **最终方案（v3，端到端验证通过）——字符串调用绕开命令表**：
  1. 弹出前（QueryContextMenu 之后、TrackPopupMenuEx 之前）：枚举顶层菜单项，`GetCommandString(id - idCmdFirst, GCS_VERBW)` 记录每个 ID 的规范动词名（此时是教科书布局，可信；实测 test.txt 记录到 17 个动词，164='open'、167='edit'…）。GCS_VERBW 是纯读取、无副作用。
  2. WndProc 捕获 WM_MENUSELECT（0x011F，wParam 低16位=项id、lParam=所在菜单 HMENU），记住最后一次真实选中；**忽略 0xFFFF（菜单关闭通知）避免覆盖**。
  3. 点击后：若选中项是顶层项且记录到了动词名 → **InvokeCommand 传动词名字符串**（CMINVOKECOMMANDINFOEX 的 lpVerb=ANSI、lpVerbW=宽字符，UNICODE|PTINVOKE 标志）。字符串按名字解析命令，**完全绕开被重排的命令表**（实测 S_OK 且文件真实打开）。
  4. 子菜单项/无动词名的项：用"动词名匹配投票"测平移量 s（弹出前每个已记录项的槽位是 id-1；在弹出后的表里找同名词槽位，s = (id-1)-槽位，取票数最多的 s，实测 27）→ 槽位 = cmd-idCmdFirst-s → 该槽位有动词名就仍走字符串，没有才用数字偏移（全部记日志）。
  5. 防御：verb 为空且槽位为负时不调用（lpVerb 高位非 0 会被当字符串指针解引用）；不用 Marshal.DestroyStructure（会把装整数偏移的 IntPtr 字段当指针释放）；释放 COM 对象前先清空静态引用（防 WndProc 竞态）。
- **验证记录**：复现程序（%TEMP%\ShellCtxTest）端到端：pre-recorded 17 verbs → shift=27（投票）→ slot 136 verb='open' → InvokeCommand S_OK → test.txt 真实打开（Notepad++ 标题确认）。字符串调用在弹出后状态下同样有效（run8）。
- **测试**：Debug 0 错误 0 警告；build.bat 已出 Release exe。待用户实测（重点：顶层项"打开/属性"、子菜单项如压缩软件、第三方扩展项）。

## 最新变更（2026-09-30 第十九轮：彻底放弃"资源管理器菜单"需求，相关代码全部移除）
- **用户决定**：第十八轮修复后实测只有第一个"打开"菜单项能执行，其余仍无效，**用户决定彻底放弃该需求**。
- **已删除**（ShellContextMenuHelper.cs 已按规范备份到 .trash/DaenLauncher/Services/，同名旧备份加时间戳后缀）：
  - `Services/ShellContextMenuHelper.cs` 整个文件（含全部 COM 互操作）；
  - MainWindow 项目右键菜单里的"打开资源管理器菜单"菜单项及 `ShowShellContextMenuForItem` 方法；
  - 语言键 Main.Item.ShellMenu / Main.ShellMenu.NotSupported / Main.ShellMenu.PathMissing（zh/en 各删 3 键，现 217 键两边对齐 0 缺失）；
  - 临时诊断程序 %TEMP%\ShellCtxTest。
- **测试**：Debug 编译 0 错误 0 警告；build.bat 已出 Release exe。项目右键菜单回归为：以管理员身份运行 / 打开所在位置 / 复制完整路径 / 删除项目 / 编辑项目。

20. **单文件 exe 验证产物内容要用未压缩的中间 DLL**：EnableCompressionInSingleFile 会压缩程序集，对 exe 做字节搜索找不到里面的字符串；查 `bin\x64\Release\...\DaenLauncher.dll` 才准。
21. **批量补丁脚本中途失败必须立即补跑完**：脚本 NOT FOUND 退出后如果先去做别的，极易忘记剩余补丁，导致"改了但没生效"的事故（第四十四轮）；交付前对每个补丁点 grep 复查。

22. **加解密实现必须全等断言回环**：DoFinal（解密）返回的是去填充后的真实长度，缓冲区尾部有残留；用"前缀比较"的回环测试放过了这个 bug（第四十六轮）。
23. **用户的测试副本 ≠ build\ 产物**：桌面快捷方式指向 D:\Program Files\DaenLauncher\；每轮交付要提醒"退出应用→覆盖部署→核对设置-关于的版本号"。版本号在 csproj 的 Version。
24. **PowerShell UIA 测试要点**：PS1 要 UTF-8 BOM；单实例应用按窗口名找窗口（Start-Process 的新 PID 会退出）；PS 5.1 静态方法绑定偶发失败（Move 调不动）改名绕过；构建前 taskkill 否则 DLL 被锁、复制失败但 grep error CS 看不出来。

## 关键决策（补充）——为什么放弃"资源管理器菜单"需求
- Windows 11 的 IContextMenu 经典菜单实现破坏了教科书契约：命令表在菜单弹出过程中被重排（本机实测菜单 ID 稀疏且弹出前后布局还会变），`lpVerb = cmd - idCmdFirst` 不可用；动词字符串调用只对部分命令有效（实测仅"打开"），GCS_VALIDATE 又不被默认处理器支持。经 4 轮修复（第十五~十八轮）仍无法让全部菜单项可靠执行，且中间过程引入过进程内访问违例（0xc0000005）。**结论：在 WinUI 3 宿主里可靠弹出并执行 Windows 11 的完整 Shell 右键菜单目前没有稳定可行的纯 Win32 路径，需求放弃。** 若将来重启此需求，考虑的方向是只提供固定 canonical 动词（open/runas/properties 等自绘菜单），不再托管完整 Shell 菜单。


## 最新变更（2026-09-30 第二十轮：待办功能 + webnote 云同步）
- **待办窗口**（`TodoWindow.xaml/.cs`，单例、关闭=隐藏、420x640）：
  - 标题栏右侧加"同步刷新"按钮（关闭按钮旁边，仅云同步启用时显示）；
  - 标签栏：全部 / 今天 / 重要 / 已完成（"今天"= 未完成且今天截止或已过期，依赖截止日期字段）；
  - 颜色选择：8 色（红橙黄绿蓝紫青粉，参考 DeskBox）过滤行，点击过滤、再点取消；过滤中的颜色同时作为新任务默认颜色；
  - 添加任务：全宽按钮 → 点击变行内输入框，回车添加（Esc 取消）；列表行：勾选框（完成加删除线）+ 颜色条 + 文字 + 截止日期小字（过期红色"已过期"）+ 悬停操作（重要星标/删除）+ 双击/右键编辑；
  - 编辑弹窗：内容 + 截止日期（CalendarDatePicker）+ 颜色（含"无颜色"）；
  - 右下角"清除已完成"按钮（无已完成任务时置灰）+ 左侧总任务数统计。
  - 按需求**不做**提醒、重复、步骤、附件、Markdown。
- **数据模型**（`Models/TodoModels.cs`）：TodoItem（Id/Text/IsCompleted/IsImportant/Color/DueDate/CompletedAt/CreatedAt）+ TodoData（Version+Items）+ TodoColors 常量（存小写英文标记）+ TodoFilterTab 枚举。本地存 `data\todo\todo.json`（复用 JsonStore）。
- **云同步**（`Services/WebNoteClient.cs` + `Services/TodoService.cs`）：
  - webnote（webnote.cc）接口：**获取** info/ 用 multipart/form-data（note_name/note_pwd），**保存** save/ 用 urlencoded（note_name/note_id/note_content/note_token/expire_time=94608000/note_pwd）；鉴权靠 Referer 头 `https://webnote.cc/{名称}@{密码}` + Chrome UA；status：1 成功、2 名称不存在、3/4 密码错。
  - 数据载体：note_content = JSON 数组**只放一个成员** `{title:"DaenLauncher-Todo", content:"DaenLauncher-Todo\n<TodoData JSON>"}`（content 第一行必须是标题，换行后才是数据）。
  - note_token 规则（按需求）：打开窗口/点刷新 → 调【获取】缓存 note_id/note_token（仅内存）；每次写操作前再调【获取】对比 token，不一致=其他客户端改过 → 用最新 token 保存；保存成功后响应返回新 token（每个 token 只能用一次）→ 更新缓存。【获取】失败则所有写操作禁用（窗口顶部 InfoBar 报错）。
  - 云端没有 DaenLauncher-Todo 条目时用本地数据自动创建；开启同步后云端为准，本地始终留一份（离线可见）。
- **设置-待办页**（`Views/SettingsPages.cs` 新增 TodoPage，替换占位页）：
  - 云同步下拉框（不启用=默认 / 启用）；启用后显示便签名称、便签密码（PasswordBox）输入框 + 保存按钮，两者都填才能点；保存前弹警示框（需求原文"清除已有内容并仅保存同步内容"），确认后调【获取】验证，成功才写入配置（TodoCloudSyncEnabled/TodoNoteName/TodoNotePwd），失败提示名称不存在/密码错误；
  - 使用教程卡片：两步说明 + "查看教程"按钮打开 https://webnote.cc/ 。
- **接线**：App 新增 `ShowTodoWindow()`（单例）并纳入主题切换/退出清理；主窗口底部"待办"按钮从占位改为打开待办窗口；启动时 `TodoService.LoadLocal()`；SettingsWindow 导航 "Todo" => TodoPage。
- **语言**：zh-CN/en-US 各 +40 键（Settings.TodoSync.* / WebNote.* / Todo.*，257 键两边对齐 0 缺失）。
- **测试**：Debug 编译 0 错误 0 警告；build.bat Release 单文件产物正常。云同步接口/窗口交互待用户实测。

## 最新变更（2026-09-30 第二十一轮：待办 BUG 修复 + 6 项优化 + 启动器"选中风格"标签）
- **BUG1 云同步保存成功但仍提示写操作禁用、标题栏看不到同步刷新按钮**：待办窗口是单例，"同步按钮可见性 + 首次云端拉取"原来只在构造函数里做一次——在设置页启用云同步之前窗口已创建的话，按钮永远隐藏、也永远不同步。修复：全部挪到 `ActivateAndBringToFront()`（每次显示都执行）：重算 SyncButton 可见性 + 云同步开启时重新调用【获取】接口。**教训：单例窗口的"按设置变化"的 UI 状态和同步动作必须放在每次显示路径上，不能只放在构造函数。**
- **优化1 待办备注**：TodoItem 新增 `Notes`；编辑/新增弹窗都有备注输入框（默认 5 行高，Height=118，超长出滚动条）；列表行在截止日期下方显示一行备注预览（超长省略）。
- **优化2 新增待办弹窗**：底部"清除已完成"右边加"添加任务"按钮，点击弹出新增弹窗（内容/备注/截止日期/颜色一次填全，默认颜色=当前颜色过滤色）；原行内快捷添加保留，按钮文字改为"快捷添加任务"。新增/编辑弹窗共用 `BuildTodoEditor`（返回元组，避免 lambda 捕获 out 参数的 CS1628）。
- **优化3 快捷截止日期**：截止日期下方一排快捷按钮——今天 / 明天 / 本周六（周一为起点算本周六，当天是周六即今天）/ 下周一 / 清除（CalendarDatePicker 自身无法取消已选日期）。
- **优化4** 设置页"查看教程"按钮文字改为"打开webnote"。
- **优化5 待办窗口设置**：设置-待办页新增 4 张卡片——显示和隐藏（快捷键，默认 Alt+2，记录交互与启动器一致）、永远置顶（默认开）、锁定尺寸（默认关）、显示位置（默认桌面中央，"上次位置"按中央处理）。热键走 Win32 热键 id=2（`Win32Helper.HotkeyIdTodo` + `TodoHotkeyPressed` 事件，`HandleHotkeyMessage` 改为带 wParam 按 id 分发）；App 新增 `ToggleTodoWindow` / `ApplyTodoWindowBehavior`；待办窗口显示时按 TodoShowPosition 定位（ComputeShowPosition 复刻主窗口逻辑，逻辑像素 420x640 × DPI）。
- **优化6 启动器子分类 Tab"选中风格"**：`SubCategoryTabVisualStyle` 枚举（Classic 选择夹=原 TabView / Highlight 选中风格=新增，**默认选中风格**）；`BuildHighlightTabPanel` 自绘：一行 N 列按钮式标签（选中浅背景+加粗）+ 选中列底部 3px 高亮条（AccentFillColorDefaultBrush，默认即蓝色）+ 内容区只放当前子分类（点击切换）；标签按钮注册进 `_panelTargets` 保持跨子分类拖动；设置-启动器-子分类风格卡片新增"标签样式"下拉框（仅 Tab 风格时显示）。注意 `SetHeaderFontWeight` 用 VisualTreeHelper 递归改头部文字粗细。
- **语言**：zh-CN/en-US 各 +22 键（Settings.TodoTrigger/.TodoAlwaysOnTop/.TodoLockSize/.TodoShowPosition/.TabVisual.*、Todo.QuickAdd/.AddTask/.AddDialogTitle/.Content/.Notes/.Due.Quick.*/.DueDate.Clear，并改 Tutorial 文案，279 键对齐 0 缺失）。
- **编译注意**：C# 对象初始化器里不能写附加属性（`ScrollViewer.VerticalScrollBarVisibility`），要构造后 `SetValue`；lambda 里不能捕获 out 参数（CS1628），用返回元组解决。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测。

## 最新变更（2026-09-30 第二十二轮：待办 UI 5 项优化）
- **同步刷新按钮移到底部**：从标题栏移到左下角"清除已完成"左边（云同步未启用仍隐藏）；同步失败提示文案同步去掉"标题栏"字样。
- **"添加任务"按钮改蓝色**：AccentButtonStyle（系统强调色）。
- **重要任务淡色底**：TodoItem 重要时列表行加底色——8 色清淡色板（alpha=34 的浅红/橙/黄/绿/蓝/紫/青/粉），按 Id 手写稳定哈希取色（string.GetHashCode 每次进程运行会变，手写保证同一条任务颜色固定），深浅色主题下都不影响文字。
- **教程卡片条件显示**：设置-待办"使用教程"卡片只在云同步下拉选"启用"时显示（卡片引用存 `_tutorialCard`，下拉切换 Visible/Collapsed），且位于云同步卡片下方。
- **便签密码小眼睛**：密码框旁加眼睛按钮，点击切换 PasswordRevealMode（Visible ↔ Hidden）；图标 E7B3（RedEye）↔ ED1A（Hide）。
- **编译坑**：WinUI 投影里没有 `Windows.UI.Colors`（要用 `Microsoft.UI.Colors`）；`PasswordRevealMode` 的成员是 Peek/Hidden/Visible（没有 Password）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测。

## 最新变更（2026-09-30 第二十三轮：待办联动与细节修复 5 项）
- **教程卡片位置**：设置-待办页构建顺序调整为 云同步 → 教程 → 热键/置顶/锁定/位置，教程卡片显示在"云同步"卡片正下方（仍只在选"启用"时显示）。
- **云同步保存立即生效**：TodoPage 保存成功或下拉关闭云同步后调 `App.OnTodoSyncSettingsChanged()` → 待办窗口 `OnSyncSettingsChanged()`：立即显示/隐藏"云同步"按钮 + 启用时自动刷新一次，不再需要关开窗口。窗口显示路径（ActivateAndBringToFront）也统一走该方法。
- **"云同步"按钮反馈**：按钮文字"同步刷新"→"云同步"；点击同步成功后文字短暂变"成功"，1 秒后自动变回（ReloadFromCloudAsync 改为返回 bool，文字反馈只在按钮点击路径）。
- **新窗口联动规则（重要，长期约束）**：之后所有新窗口都必须接入——① 软件标题：`RefreshTitle()` + App.RefreshAllTitles 里调用；② 窗口材质：App.ApplyBackdropEverywhere() 统一应用（外观页改用它，不再逐窗口点名）；③ 主题：App.ApplyThemeEverywhere()。本轮已把待办窗口接入 RefreshAllTitles 和 ApplyBackdropEverywhere。
- **深色模式列表底色**：任务行背景从 CardBackgroundFillColorDefaultBrush（深色下灰重）改为 SubtleFillColorSecondaryBrush（半透明叠加，和窗口底色融合），边框不变。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测。

## 最新变更（2026-09-30 第二十四轮：云/本地数据彻底分离 + 重要底色自定义）
- **云/本地两套完全独立的数据（重要架构调整）**：TodoService 拆成 `_localData`（todo.json）和 `_cloudData`（仅内存），`Data` 属性按模式返回对应那套——
  - 本地模式：读写 todo.json，改动立即落盘；
  - 云同步模式：数据只来自云端、只推云端，**不写任何本地文件**；
  - 切换模式：窗口 `OnSyncSettingsChanged` 里切回本地时 `LoadLocal()`（先清 JsonStore 缓存再读盘）+ 刷新列表，云端数据留在内存随时可切回；LoadLocal 改为先 `_store.Reload()` 保证拿到磁盘最新内容；
  - 云端没有 DaenLauncher-Todo 条目时改为推**空的 TodoData**（原来推本地数据——两套数据分离后不再混合）；
  - 数据-删除勾选 todo 后也调 `LoadLocal()` + 刷新窗口，防止内存缓存把删除的文件写回去。
- **重要任务底色改为用户设置**：AppSettings 新增 `TodoImportantColor`（TodoColors 标记，默认 yellow）；设置-待办新增"重要任务底色"卡片（8 色 + "无"，单选带描边，色块按 alpha=90 预览实际效果）；待办窗口列表按设置取色（alpha=34 叠加），不再随机取色（删除 ImportantTints 色板和 StableHash）。
- **联动**：改重要底色后 `App.RefreshTodoWindowView()` → 待办窗口 `RefreshView()` 立即生效。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测。

## 最新变更（2026-09-30 第二十五轮：待办"上次位置"修复）
- **待办显示位置"上次位置"无效**：当初实现偷懒只做了"LastPosition 落到 default 分支=桌面中央"，且从未记录过待办窗口位置。修复（与主窗口同款方案）：AppSettings 新增 `TodoLastWindowX/Y`（物理像素，-1=未记录）；TodoWindow 订阅 `AppWindow.Changed`（DidPositionChange）防抖 500ms 保存；Closing（隐藏/退出）前 `SavePositionNow()` 立即落盘；ComputeShowPosition 增加 LastPosition 分支（用记录坐标，越界钳制回工作区，没记录过退回桌面中央）。**坑：Changed 订阅前必须先创建防抖计时器，否则事件先到会 NullReference。**
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测（拖动待办窗口→关闭→重开应在上次位置显示）。

## 最新变更（2026-09-30 第二十六轮：附属功能栏可配置——勾选 + 排序 + "更多"菜单）
- **需求**：附属功能（待办/随手记/剪贴板，以后还会加）不能都平铺在启动器左下角。设计经用户确认：底栏最多显示 3 个勾选的功能 + 一个"⋯"按钮，点击弹出系统风格菜单（图标+名称，仅列出**未勾选**的功能）。
- **注册表**（`Models/AuxiliaryFeatures.cs`）：`AuxiliaryFeature(Id, NameKey, IconResource)` + `All[]`（以后新增附属功能在此加一行）+ `MaxVisible=3` + `TodoId` 常量。
- **设置**（AppSettings）：`AuxiliaryVisible`（有序 id 列表，默认 todo/note/clipboard 全显）。
- **MainWindow**：XAML 三个硬编码按钮换成空 `AuxBarPanel`，代码 `RebuildAuxiliaryBar()` 按设置构建——可见按钮 52x44（图标）+ 未勾选时追加"⋯"按钮（\uE712）；"⋯"每次点击重建 MenuFlyout（语言切换后文字最新），`ShowAt(anchor, Placement=Top)` 从按钮上方弹出；`OpenAuxiliaryFeature(id)` 分发（todo→待办窗口，其余→开发中弹窗）。语言切换时也重建底栏。删除原 LoadPlaceholderIconsAsync 和 PlaceholderButton_Click。
- **设置-常规新增"附属功能"卡片**：每行 = 显示勾选框 + 图标名称 + 上移/下移（\uE70E/\uE70D）；已勾选的行才可移动（在显示列表内交换）；勾选超 3 个拒绝并弹提示；卡片底部有"最多显示 3 个"说明；变化后 `App.RefreshAuxiliaryBar()` 立即生效。GeneralPage 里 `BuildAuxiliaryRow` 返回 Grid。
- **语言**：zh/en 各 +6 键（Settings.Auxiliary/.Show/.MaxHint/.MaxReached、Main.MoreFeatures，290 键对齐）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测。

## 最新变更（2026-09-30 第二十七轮：待办"全部"页不再显示已完成）
- "全部"标签页过滤条件改为只显示未完成任务（原来会混入已完成的删除线项），已完成的内容只出现在"已完成"页；底部统计的"共 N 项"仍统计全部数据（不变）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。小改动，由用户自测。

## 最新变更（2026-09-30 第二十八轮：图标选择器支持"不选择"）
- emoji 面板顶部新增"不选择"磁贴（叉号图标 + 文字说明）：点击后 `SelectedIcon=""` 并清除所有高亮，解决"分类/子分类一旦选了表情图标就取不掉"的问题。属于共享缓存面板的一部分，点击走静态路由（ClearTilePressed → 当前可见实例）。空图标在分类/子分类渲染端本来就按"无图标"处理（CreateIconElement 返回 null），无需改动。
- 语言：IconPicker.None（zh/en 各 +1，291 键对齐）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。小改动，由用户自测。

## 最新变更（2026-09-30 第二十九轮：图标选择器改竖排标签 + "不选择"公用高亮）
- **用户反馈**：上一轮的"不选择"能用但选中后没有高亮（不知道选没选上）；标签横向排，group_ 多了放不下；"不选择"只在 emoji 页有。
- **重写 IconPicker 布局**：改为左右两栏——左侧竖向标签栏（可滚动）：**"不选择"固定在最顶部**（叉号图标，公用，与具体标签无关，下面有分隔线）→ 表情图标 → 自定义图标 → 各 group_ 分组；右侧为内容区，点击左侧项切换（选中项浅背景 + 加粗，同待办窗口标签样式）。
- **"不选择"高亮**：点击后清空 SelectedIcon 并给自己描边高亮；选中任何具体图标时自动取消高亮（ClearSelection 统一处理）；打开弹窗时若当前图标本来就为空也高亮。MarkSelected 拆成 Border/Control 两个重载（FrameworkElement 没有 BorderThickness 属性）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。由用户自测。

## 最新变更（2026-09-30 第三十轮：图标选择器 3 项修复/优化）
- **"不选择"图标显示方框**：上一轮把叉号字符（\uE711）直接写进了 TextBlock——TextBlock 用默认 UI 字体（Segoe UI），没有符号字形，显示成方框。修复：改用 FontIcon + TextBlock 的横向组合。**教训：符号字体字形必须走 FontIcon（或显式指定符号字体族），不能塞进普通 TextBlock。**
- **切换标签窗口大小变化**：选择器根 Grid 设固定尺寸（Width=440, Height=264，取自表情图标页的合适大小），ContentDialog 不再随内容自适应跳动；表情页网格 MaxHeight 相应调整为 216 给输入框让位。
- **自定义 emoji 输入**：表情图标页顶部新增输入框（MaxLength=8，兼容组合 emoji）+"使用"按钮；输入合法 emoji（EmojiCatalog.IsEmoji：非空且不含路径字符）立即生效并高亮输入框；打开弹窗时若当前图标是不在网格里的自定义 emoji，自动回填到输入框。输入框为每实例元素（不进共享缓存面板）。
- **语言**：IconPicker.EmojiInput/.EmojiUse（zh/en 各 +2，293 键对齐）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。由用户自测。

## 最新变更（2026-09-30 第三十一轮：随手记功能上线）
- **随手记窗口**（`NoteWindow.xaml/.cs`，单例、关闭=隐藏、默认 720x560 记忆大小）：
  - 左侧：顶部一行 [云同步][添加笔记（蓝色强调）] + 笔记列表。列表项只显示标题+创建时间（需求：不显示内容摘要），置顶恒在最上、其余按创建时间倒序；卡片可设底色（TodoColors 8 色，alpha=34 叠加）；选中项描边高亮；右键菜单：编辑（改标题+底色的弹窗）/置顶/取消置顶/删除；
  - 右侧：纯文本编辑器（需求：不支持 markdown），头部显示当前笔记标题+创建时间；内容输入防抖 800ms 保存（本地落盘/云端推送）；
  - 左右分隔条复用 `SplitterGrid`，拖动记忆 `NoteLeftPaneWidth`；**锁定尺寸时分隔条不可拖**（PointerPressed 里按设置拦截——SplitterGrid 是 Grid 不是 Control，没有 IsEnabled）；
  - 新窗口联动规则全接入：RefreshTitle / ApplyBackdropEverywhere / ApplyThemeEverywhere / ApplyCaptionButtonColors。
- **数据**（`Models/NoteModels.cs`）：NoteItem（Id=Guid "N" 格式 32 位十六进制，Title/Text/Color/IsPinned/CreatedAt）+ NoteData；本地存 data\note\notes.json（复用 JsonStore）。
- **云同步**（`Services/NoteService.cs`，多标签，需求）：
  - 每条笔记 = webnote 一个条目：标题 `DaenLauncher-Note-{32位Id}`，正文 = 第一行标题 + 换行 + NoteItem JSON；
  - **保存是整体覆盖 → 每次保存提交全部笔记**（漏提交=删除）；非 DaenLauncher-Note-* 的他人条目在内存保留、保存时原样带回（防止覆盖删除便签里其他内容；结构异常的本应用条目也按他人条目保留不丢数据）；
  - token 规则与待办一致（写操作前【获取】对比、保存后换新 token、获取失败禁写）；
  - 本地/云端两套数据完全独立（与待办同款架构，随时切换）。
- **设置-随手记页**（NotePage，与 TodoPage 同款）：云同步开关 + 便签名称/密码（小眼睛）+ 保存（警示框→接口验证→落盘）；**便签名称查重（需求）：与待办的便签名称相同则拒绝并提示——TodoPage 保存时也加了反向查重**；教程卡片（启用时显示）；显示和隐藏快捷键（默认 Alt+3，热键 id=3）；永远置顶/锁定尺寸/显示位置（支持上次位置，NoteLastWindowX/Y）。
- **接线**：App 新增 ShowNoteWindow/ToggleNoteWindow/ApplyNoteWindowBehavior/OnNoteSyncSettingsChanged/RefreshNoteWindowView；注册/分发热键 id=3（NoteHotkeyPressed）；附属功能注册表 NoteId，主窗口左下角"随手记"按钮打开随手记窗口；数据删除勾选 note 后 LoadLocal+刷新窗口；主题/材质/标题联动全部接入。
- **编译坑**：Grid.Children[i] 返回 UIElement，Grid.SetColumn 需要 FrameworkElement（先存 var）；Grid/Panel 没有 IsEnabled（那是 Control 的）。
- **语言**：zh/en 各 +37 键（Settings.NoteSync.*/NoteTrigger/NoteAlwaysOnTop/NoteLockSize/NoteShowPosition、Note.* 窗口键，330 键对齐）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。云同步/窗口交互待用户实测。

## 最新变更（2026-09-30 第三十二轮：webnote 便签自动创建 + 随手记交互修复）
- **webnote 便签自动创建（需求-优化1）**：WebNoteClient 新增 `FetchOrCreateAsync`——【获取】返回"剪贴板不存在"（status=2）时，自动调一次【保存】（note_id/note_token 传空，内容为 `[{"title":"新便签","content":""}]`）创建便签，然后重新【获取】。TodoService/NoteService 的所有获取点（窗口打开/刷新/写操作前）和两个设置页的保存验证都改走该方法——**用户不再需要提前去网页创建便签**。据此删除了待办/随手记设置页的"使用教程"卡片（相关方法、字段、显隐逻辑全部移除，语言键保留未删）。
- **随手记"功能没做"的真相（需求-优化2 的排查结论）**：排序/底色/置顶/右键菜单其实都已实现，用户看不到是因为三个叠加问题——
  1. **云同步便签不存在时所有写操作被禁用**（优化1已修复根因）；
  2. **右键菜单弹不出来（实锤 bug）**：右键也会触发 PointerPressed → SelectNote → RebuildNoteList 重建列表，被右键的卡片在 ContextFlyout 弹出前就被替换掉了。修复：PointerPressed 只响应左键（`IsLeftButtonPressed` 判断）；
  3. **分隔条没有左右箭头光标**（实锤 bug）：NoteWindow 构造时漏了 PointerEntered/Exited → SetSplitterCursor（主窗口有，随手记漏抄）。已补。
- **教训**：① ContextFlyout 与列表重建的时序——凡是"按下即重建列表"的交互，必须区分左右键；② 复用主窗口模式（分隔条光标）时要核对每一个配套事件是否都带过来了。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。待用户实测（重点：不提前建便签直接启用云同步、随手记右键菜单、分隔条光标与拖动）。

## 最新变更（2026-09-30 第三十三轮：随手记新建弹窗 + 快捷键修复）
- **新建笔记支持先填标题和底色（需求）**：点"添加笔记"先弹出与编辑同款的弹窗（标题+底色，抽成 `BuildNoteEditContent` 供新增/编辑共用），确认后创建并选中、光标进编辑器。
- **随手记快捷键 Alt+3 无效（实锤）**：第三十一轮接线时**漏了 `Win32Helper.NoteHotkeyPressed += ToggleNoteWindow` 订阅**——热键注册了（id=3 也注册上了）但事件没人响应。已补。**教训：加新热键要"注册+分发+订阅"三件套核对，之前 TodoHotkey 是在第二轮逐步补齐的所以没暴露这个模式。**
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。由用户实测。

## 最新变更（2026-09-30 第三十四轮：随手记编辑面板化——不弹窗编辑）
- **需求变更**：新建笔记不弹编辑弹窗，标题/正文/底色/置顶全部在右侧编辑面板里直接填；右键菜单去掉"编辑"，只留置顶（取消置顶）和删除。
- **右侧编辑面板重构**：头部 = 标题输入框 + 置顶按钮（图钉图标随状态切换 E841/E77A）+ 底色色块行（无颜色+8色，点击即改并同步列表卡片底色）+ 创建时间；下方正文编辑框。所有字段变化走同一个 800ms 防抖保存（SaveTextNow 同时保存标题和正文）；底色/置顶点击立即生效。
- 新增 `ClearEditor()`/`SetEditorEnabled()`/`UpdateEditorControls()` 统一管理编辑面板状态（之前散落在 4 处的内联清空代码全部收敛）；删除 BuildNoteEditContent/EditNoteAsync/双击编辑。
- **再次踩坑记录**：StackPanel/Grid 没有 IsEnabled（Control 才有）——上一轮刚记过 Grid.SetColumn 的坑，同族问题，以后凡"容器整体禁用"都要逐个设置子控件或包一个 ContentControl。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。由用户实测。

## 最新变更（2026-10-01 第三十五轮：随手记手动保存 + webnote 临时错误重试）
- **"数据格式错误"排查结论（实测 API 确认）**：用 daena1 实测——① 我们的 note_content 格式（含自动创建的 `[{"title":"新便签","content":""}]` 和随手记多标签条目）服务端**全部接受**，甚至双重编码的内容服务端也照存不误；② **Accept-Language 头缺失时服务端会返回"服务器负载过高，业务处理失败"**（用户提示后确认，之前 curl 诊断全被这个误导）；③ "数据格式错误"与"负载过高"一样是**服务端临时性错误**，不是我们的编码问题。修复：WebNoteClient.FetchOrCreateAsync 与两个 Service 的保存都加了**临时错误自动重试一次**（等待 1.5 秒后重新获取 token 再试；只重试 NetworkError 类，名称/密码错误不重试）。
- **随手记改为手动保存（需求-优化2）**：右侧编辑面板头部新增"保存"按钮（蓝色强调样式，无修改时置灰）——新增/修改/删除/置顶/底色只改内存并亮起按钮，**点保存才落盘/推送**（本地 Persist / 云端 PushToCloud），彻底避免高频请求被 webnote 封禁。标题/正文输入仍是 800ms 防抖并入内存（RebuildNoteList 让列表实时反映）。窗口关闭时：本地模式立即落盘；云模式尽力后台推送一次（防丢改动）。云端拉取成功后清除 dirty 标记。
- **遗留坑记录**：webnote 服务端极不稳定（负载过高/数据格式错误等临时错误频发），未来若再遇"偶发同步失败"先怀疑服务端，重试是正确姿势；诊断时请求头必须带全（尤其 Accept-Language）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。已把测试便签 daena1 恢复为干净内容。待用户实测。

## 最新变更（2026-10-01 第三十六轮：webnote HTTP 全量调试日志（临时））
- **需求**：换新便签名仍报"数据格式错误"，用户要求加 HTTP 日志自行分析，修复后删除。
- **临时日志功能**（WebNoteClient.DebugLog，**修复后整体删除**）：每次【获取】/【保存】都把 请求 URL、请求头摘要、完整请求体（保存接口是 URL 编码后的原始报文，获取接口是 multipart 字段）、响应状态码、完整响应体 追加写入 `data\webnote_debug.log`；网络异常也记录 ex.ToString()。日志含账号密码，仅本机排查用。
- **日志位置**：exe 同级 `data\webnote_debug.log`（exe 目录不可写时在设置-数据-打开配置文件目录里看真实位置）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。等用户抓到失败请求的日志再分析。

## 最新变更（2026-10-01 第三十七轮：修复"数据格式错误"——本地无数据时不调用保存接口）
- **用户通过日志定位根因**：自动创建便签成功后，本地（云端内存）没有任何数据，SyncFromCloudAsync 仍调了一次保存接口，提交的 note_content 是 `[]`（URL 编码 %5B%5D），服务端直接拒绝并报"数据格式错误"。
- **修复**：① NoteService.SyncFromCloudAsync 云端无本应用笔记时**跳过保存**（CloudAvailable 直接置 true）；② TodoService.SyncFromCloudAsync 同样按"无数据不保存"处理；③ 连带防御：随手记把笔记全部删除后点保存，为避免再提交空数组，用创建时的占位条目（新便签）代替空数组——云端效果等价于"没有我们的笔记"。
- **日志功能保留**：等用户确认修复后再删除（第三十六轮加的 webnote_debug.log）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。由用户实测验证。

## 最新变更（2026-10-01 第三十八轮：删除 webnote 调试日志功能）
- 用户确认"数据格式错误"修复没问题，按约定删除第三十六轮加的临时 HTTP 日志：WebNoteClient 里的 DebugLog/DebugLogException 方法及全部调用点移除，代码恢复原样；磁盘上残留的 webnote_debug.log（在 build - 副本\data 下发现一个，含账号密码）已删除。
- **测试**：Debug 0 错误 0 警告；build.bat Release 产物正常。

## 关键决策（待办云同步）与已知限制
- **便签密码明文存 settings.json**（本机文件，与桌面便签场景风险可接受；如需加密另做）。
- **推送失败的处理**：写操作先改内存再推云端（云端模式不落本地）；若推送失败，云端数据未更新，**下次窗口打开会重新从云端拉取**，未推送的修改会丢（云为唯一事实源的模型，需求如此设计）。
- **云/本地是两套独立数据**（第二十四轮）：云同步模式不写本地文件，本地模式不碰云端，随时切换互不影响。
- **token 冲突不合并内容**：需求只要求"发现不一致就用最新 token 保存"（以本机数据覆盖），未做内容级合并。
- CalendarDatePicker.FirstDayOfWeek 的类型是 `Windows.Globalization.DayOfWeek`（不是 Microsoft.UI.Xaml 的）。

## 最新变更（2026-10-01 第三十九轮：剪贴板功能上线 + 2 项调整）

- **剪贴板窗口**（`ClipboardWindow.xaml/.cs`，单例、关闭=隐藏、默认 480x640 记忆大小）：
  - 分类栏：记录 / 归档（同待办窗口标签样式，选中浅背景+加粗）；归档 = 持久化存储，不受数量限制（需求）；
  - 列表一行一条、最新在最上；点击行 = **再次复制到剪贴板**，成功后**行高亮渐隐**反馈（见下）；
  - 右键菜单：复制 / 修改（仅文本，弹 ContentDialog 多行编辑框）/ 归档（或取消归档）/ 删除；
  - 底部按钮随分类切换：记录 → "清除全部记录"，归档 → "清除全部归档"（均有二次确认弹窗）；左侧统计"共 N 条记录/归档"；
  - **非文本支持**（需求评估后实现）：图片条目显示缩略图预览（DecodePixelHeight=192 限内存）+ 像素尺寸，再次复制用 SetBitmap；文件/文件夹条目显示首项名称 + "共 N 项"，再次复制用 SetStorageItems（已不存在的路径自动跳过）；
  - 未启用功能时窗口顶部显示 Warning InfoBar 引导去设置开启（监听已停，查看/再次复制仍可用）。
- **监听实现**（`Services/ClipboardService.cs`）：**DispatcherTimer 每 800ms 轮询 `GetClipboardSequenceNumber`**（新增 P/Invoke）——比 `Clipboard.ContentChanged` 事件可靠（事件在窗口焦点切换时才可能触发）；捕获优先级 文本 > 图片 > 文件（同一内容带多格式取一种）。
  - 去重：与最新一条相同（文本全等 / 图片像素 SHA1 指纹 / 文件路径列表相同）不重复记录；
  - 图片落盘 `data\clipboard\images\{id}.png`：BitmapDecoder 解码像素 → BitmapEncoder 重编码 PNG（不依赖剪贴板源格式）；
  - **"再次复制"防自记录**：写剪贴板前设 `_suppressNextCapture`，下一次轮询跳过；`Clipboard.Flush()` 让系统接管内容（应用退出后仍可粘贴）；
  - 数量裁剪：超出"最大保存数量"从最旧端删，图片文件一并删除（ImagePath 限定在 images 目录内防路径注入）；文本超 100 万字符不记录；
  - 存储 `data\clipboard\clipboard.json`（复用 JsonStore）；数据删除页勾选 clipboard 后 `LoadLocal()` + 刷新窗口。
- **设置-剪贴板页**（ClipboardPage，替换占位页）：启用本功能（默认**关**，隐私考虑，开启即开始监听）、最大保存记录数量（NumberBox 10-1000，默认 60，调小立即裁剪）、显示和隐藏快捷键（默认 Alt+4，热键 id=4）、锁定尺寸、显示位置（支持上次位置 ClipboardLastWindowX/Y + 尺寸记忆 ClipboardWindowWidth/Height）。
- **接线**：App 新增 ShowClipboardWindow/ToggleClipboardWindow/ApplyClipboardWindowBehavior/RefreshClipboardWindowView，主题/材质/标题三项联动全部接入；热键 id=4（`Win32Helper.HotkeyIdClipboard` + `ClipboardHotkeyPressed`，**注册+分发+订阅三件套齐全**）；MainWindow 附属功能栏 clipboard 分支（AuxiliaryFeatures.ClipboardId 常量）；SettingsWindow 导航 Clipboard => ClipboardPage。
- **语言**：zh/en 各 +31 键（Settings.Clipboard* 5 卡片 + Clipboard.* 窗口键，362 键两边对齐 0 缺失）。
- **新编译坑（第三十九轮）**：① `ClipboardContent` 类型不存在——`Clipboard.GetContent()` 返回的是 **`DataPackageView`**；② NumberBox 上下限属性是 **Minimum/Maximum**（不是 Min/Max）；③ `AppWindow` 没有 Width/Height，尺寸在 **`AppWindow.Size`**；④ `Image` 没有 CornerRadius 属性，圆角要包 Border（但 Border 圆角不裁剪子内容，仅背景圆角）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 单文件产物正常。待用户实测（重点：复制文本/截图/文件的记录与再次复制、行高亮渐隐、归档往返、数量裁剪、Alt+4 热键）。

## 最新变更（2026-10-01 第四十轮：剪贴板 2 项调整）
- **复制成功提示改版（需求：用户嫌蒙版丑，给了 4 方案由用户选定"行高亮渐隐"）**：从"整行强调色蒙版 + 已复制文字"改为**行高亮渐隐**——点击复制成功后整行背景泛一下系统强调色浅色（alpha=90），约 0.9 秒 ColorAnimation 渐隐回原底色，不遮挡内容。实现要点：Storyboard 的 ColorAnimation 作用在**独立画刷实例**上（共享 ThemeResource 画刷不能直接改，会影响所有行）；动画 Completed 后把行背景还原为主题画刷（深浅色主题切换仍跟随）。
- **修改弹窗多行显示修复**：原 TextBox 只设 MinHeight，ContentDialog 把内容压矮且无滚动条 → 多行内容只露第一行。改为固定 Height=260 + VerticalScrollBarVisibility=Auto + 显式设置 AcceptsReturn（构造后单独赋值），纵向滚动条为需求要求。
- **测试**：Debug 0 错误 0 警告。由用户实测（多行文本修改、行高亮渐隐效果）。

## 最新变更（2026-10-01 第四十一轮：剪贴板"永远置顶"设置）
- 与待办/随手记同款：AppSettings 新增 `ClipboardAlwaysOnTop`（默认开）；ClipboardPage 在"显示和隐藏"卡片后新增"永远置顶"卡片（勾选即 `SaveAndApplyBehavior` → `App.ApplyClipboardWindowBehavior` → 窗口 `ApplyBehaviorSettings` 里 `_presenter.IsAlwaysOnTop`，即时生效）；窗口构造和每次显示路径（ActivateAndBringToFront）都会应用。
- 语言：zh/en 各 +2 键（Settings.ClipboardAlwaysOnTop/.Sub，364 键对齐 0 缺失）。
- **测试**：Debug 编译 0 错误 0 警告。小改动，由用户自测（勾选后剪贴板窗口应立即置顶/取消）。

## 最新变更（2026-10-02 第四十二轮：常用工具附属功能上线）

- **常用工具窗口**（`ToolsWindow.xaml/.cs`，单例、关闭=隐藏、默认 1020x680 记忆大小），三栏布局：
  - 左侧分类导航：加解密类 / 文本处理 / 格式化 / 其他常用（4 个按钮，选中=浅背景+加粗，同剪贴板标签样式）；
  - 中间工具面板：当前工具标题 + 面板（ScrollViewer 包 ContentControl）；
  - **右侧子功能竖向导航**（需求）：显示当前分类下的全部工具，点击切换；
  - **工具界面缓存**：每个工具 Build 一次存 `_toolPages`，切换回来输入内容不丢；语言切换时清缓存全部重建；
  - 窗口行为同剪贴板窗口：永远置顶（默认开）/ 锁定尺寸 / 显示位置（含上次位置 ToolsLastWindowX/Y）/ 尺寸记忆 / DPI 换算。
- **工具框架**（`Views/Tools/`）：
  - `ToolRegistry.cs`：IToolPage 接口 + ToolDefinition(id、语言键、工厂) + 4 分类注册表；新增工具=写类+在分类数组加一行；
  - `ToolKit.cs`：共用 UI 构建器（标签/选项行/输入输出区/下拉框/NumberBox/主按钮/复制按钮/复选框）；
    复制按钮 = DataPackage+SetText+`Clipboard.Flush()`，文字短暂变"已复制"反馈。
- **加解密**（`CryptoToolPage.cs` + `CryptoEngine.cs`，NuGet 新增 **BouncyCastle.Cryptography 2.5.1**）：
  - SM4/AES 共用一个页面；原文格式（UTF-8/Hex/Base64）× 模式（CBC/ECB/CFB/OFB/CTR/GCM）× 密钥/IV 格式 × 填充（PKCS5/PKCS7/Zeros/ISO10126/ANSIX923/ISO7816-4/NoPadding）× 输出（Hex/Base64/大写）；
  - 多行模式（一行一个，失败行输出 [第N行失败]）+ 复杂文本处理（Tab 分隔多字段逐个处理）；
  - **BouncyCastle 2.5.1 API 坑（实测）**：① 接口是 `IBlockCipherPadding` 不是 IPadding；填充类名 `ISO10126d2Padding`/`X923Padding`/`ISO7816d4Padding`（大小写与 Java 版不同）；② `GcmBlockCipher.DoFinal` 只有 `int DoFinal(byte[] buf, int offset)` 就地收尾版——先 ProcessBytes 写入输出缓冲，再 DoFinal(buf, written)（已用控制台程序 68 组合回环验证全过，NoPadding+非整块报错属预期）；③ CfbBlockCipher/OfbBlockCipher 的第二个参数是**字节**（传 16=CFB-128）；④ PKCS5 与 PKCS7 等价都映射 Pkcs7Padding；⑤ CFB/OFB/CTR/GCM 是流模式忽略填充选项；
  - Hex/Base64 解码容错（去空格/冒号）；AES 密钥 16/24/32、SM4 固定 16 校验；ECB 自动隐藏 IV 行。
- **文本处理 12 个**（`TextTools.cs`）：MD5（32/16 位大小写实时算）、URL 编解码（Escape/UnescapeDataString）、Base64 编解码、换行互转''（SQL in 用）、行行去重（可忽略首尾空格）、字符串替换（查找:换行/空格/自定义；替换为:换行/空格/删除/自定义，
 自动转真换行）、大小写、文本颠倒（前后/每行内）、驼峰↔下划线（逐行处理，处理 ABC 连续大写边界）、排序（首字母/首数值/长度 升降序+随机，数值=取行首数字）、去首尾空格（每行）、删空白行。
- **格式化 3 个**（`FormatTools.cs`）：JSON 格式化/压缩/转义/去转义（自写 StripJsonComments 删 // 和 /* */ 且不伤字符串；JsonNode 解析）；XML 格式化（带声明）/压缩（XDocument）；SQL 格式化/压缩（自写 SqlFormatter：词法切分保护字符串和注释、两字关键字整组匹配换行、AND/OR/ON 缩进、括号逗号紧贴）。
- **其他常用 7 个**（`MiscTools.cs` + `ColorPickerTool.cs`）：
  - 行行求和：剔除 ¥￥$,，元分角 后 decimal 求和（含中间千分位逗号），显示"解析 N 行跳过 M 行"；
  - curl 生成：按需求示例格式拼 `curl -v [-x http/socks5://host:port] [-U "user:pass"] -X 方法 "url" [-H ...] [-d '...']`，单引号体做 ''' 转义；
  - 批量提取文件名：**TextBox 外包 Grid 接收拖放**（TextBox 不收文件拖放），StorageFolder 递归收文件，可去路径/去拓展名；
  - 密码生成（RandomNumberGenerator 加密级随机）、UUID 生成（横杠/大小写/数量）、时间戳（顶部秒/毫秒每秒刷新，双向互转自动识别 10/13 位）；
  - **颜色选择器**：SV 二维颜色盘（白→纯色横向渐变+透明→黑纵向渐变叠加，指示点在 Canvas 覆盖层用 SetLeft/SetTop）+ 色相滑条 + 彩虹参考条；**屏幕取色** = WH_MOUSE_LL 低级钩子（单击确认并吞掉该次点击）+ 30ms DispatcherTimer 轮询 GetCursorPos+GetPixel(整屏 DC)；输出 HEX/RGB/HSL/HSV/HWB/LAB/LCH/CMYK 八种（LAB 用 sRGB D65 标准换算）；**钩子委托必须存字段防 GC**。
- **接线**：AuxiliaryFeatures 加 tools（图标 素材→Assets\Icons\常用工具_64.png）；MainWindow.OpenAuxiliaryFeature 分支；App 新增 ShowToolsWindow/ApplyToolsWindowBehavior，主题/材质/标题三联动全接入，退出关闭；SettingsWindow 导航 Tools => ToolsPage（置顶/锁定/显示位置三卡片）；AppSettings 新增 Tools* 7 项（默认 1020x680 居中）。
- **语言**：zh/en 各 +170 键（Main.Tools、Settings.Tools* 6、Tools.* 全部界面文字，534 键两边对齐 0 缺失）；无单独热键（需求未提，后续可加 id=5）。
- **测试**：Debug 0 错误 0 警告；build.bat Release 单文件产物正常；BouncyCastle 全模式/填充组合回环通过（独立控制台验证）。窗口交互和各工具实际操作由用户实测。

## 最新变更（2026-10-02 第四十三轮：常用工具 9 项修补）

- **根因级修复：多行处理全部失效**（用户实测 SM4 多行/换行互转只出单行）——**WinUI TextBox 的换行符是 CR（不是 LF/CRLF）**，之前按 LF 切分自然全挂。修复：ToolKit 新增 `SplitLines`（归一化 CRLF/CR/LF 后切分）、`JoinLines`（用 CRLF 拼回，显示和复制都正确）、`SetOutput` 里统一 `NormalizeNewlines`（JSON 序列化器输出的 LF 也能正常显示）。**凡是按行处理的地方（加解密多行、换行互转、去重、替换、颠倒、驼峰、排序、Trim、删空行、请求头拆行）全部切到这两个方法**；字符串替换还会先把输入归一化再 Replace（否则"查找:换行"匹配不到 CR 文本）。
- **加解密布局调整（需求）**：输出转大写移到"多行模式"左侧（复选框行：转大写/多行/复杂文本）；密钥格式下拉框放到密钥输入框右边同一行，IV 格式同理（原独立两行删除）。
- **左右导航选中态加蓝色竖条（需求）**：BuildNavButton 内容 = [3px 圆角竖条 Border] + 文字，竖条常驻（未选中透明）避免文字跳动，选中时用 SystemAccentColor；左右两栏导航同一套样式（模仿设置窗口 NavigationView 指示条）。
- **JSON 转义/去转义改语义（需求）**：转义 = 只给双引号和反斜杠前加反斜杠（用户示例：塞 SQL 用，不包引号、不动换行）；去转义 = `UnescapeBackslashes` 逐字符扫描（\n/\r/\t 转真字符、\" 还原、\\ 还原，比整串 Replace 稳）。
- **XML 增强（需求）**：新增"压缩时保留<?xml头"复选框（默认勾选；不勾则压缩时正则剥掉 XML 声明）+ 转义/反转义按钮（& < > " ' 实体互转，&amp; 最后替换防二次解错）。
- **curl（需求）**：新增 `ToolKit.OptionRowStretch`（Grid 两列：标签固定宽 + 控件占满剩余，请求地址/请求头/请求体跟随窗口拉伸）；新增 `ToolKit.ResizableHost`（输入框右下角"◢"拖柄，拖动调宽高）——**三个坑**：Border 是密封类不能派生；ProtectedCursor 是受保护 API；Panel 没有指针虚方法——最终方案：自定义 `ResizeGripPanel : Panel`（透明背景保证可命中）+ PointerEntered/Exited 事件里 Win32 `SetCursor(LoadCursor(IDC_SIZENESW))` 换光标。
- **批量提取文件名（需求）**：新增"遍历子目录"复选框（默认勾选=原行为），不勾则只列文件夹第一层文件。
- **时间戳（需求）**：新增复制当前秒级/毫秒级时间戳按钮 + 复制当前时间按钮（时间格式下拉：yyyy-MM-dd HH:mm:ss 等 6 种，格式串直接当下拉文字不翻译）；CopyButton 的 Provider 是点击时求值所以取到的总是当下值。
- **行行颠倒语义修正（需求澄清）**：只颠倒行与行的顺序（Array.Reverse 整个行数组），行内字符不动。
- **语言**：zh/en 各 +6 键（Tools.Xml.KeepDecl/.Unescape、Tools.Ts.Copy*/TimeFormat）+1 更新（Tools.Reverse.PerLine 文案），540 键对齐。
- **测试**：Debug 0 错误 0 警告；build.bat Release 正常。多行修复影响所有按行工具，请用户重点复测。

## 最新变更（2026-10-02 第四十四轮：常用工具热键 + 7 项修补）

- **常用工具快捷键 Alt+5（需求）**：完整走"注册+分发+订阅"三件套——AppSettings 新增 ToolsTriggerHotkey/Modifiers/VirtualKey/Text（默认 Alt+5、默认关）；Win32Helper 新增 HotkeyIdTools=5 + ToolsHotkeyPressed 事件 + HandleHotkeyMessage case（**必须放在 default 之前**，否则 Alt+5 会落进 default 触发主窗口显隐）；App.SetupHotkeys 订阅 + RegisterHotkey 注册；ToolsWindow.ToggleViaHotkey（与剪贴板同款）；设置-常用工具页新增"显示和隐藏"卡片（BuildHotkeyText 复制了一份到 ToolsPage——它是 ClipboardPage 的 private 方法，下轮可考虑上移到 SettingsPageBase）。
- **加解密参数区改紧凑网格（需求）**：废弃左右两个 StackPanel（间距 30 太远），改单 Grid 四列（标签100|控件|标签100|控件，ColumnSpacing/RowSpacing=12/8）：原文格式+填充方式同行、加密模式+输出格式同行、密钥+密钥格式一行、IV+IV 格式一行。ECB 隐藏 IV 时连标签一起藏（_ivLabel 字段）。
- **字符串替换"查找:换行"失效（用户实测）**：第四十三轮把输入归一化成 CRLF 后，查找串 "\n" 只能匹配到 CRLF 的后半截（剩个孤 CR，显示上还是换行）。修复：替换前归一化成 **LF**（string.Join("\n", SplitLines(...))），查找/替换都基于 LF 做，输出由 SetOutput 统一回 CRLF。
- **文本排序"完全无效"（用户实测）**：第四十三轮批量替换漏了 `.Split('\n').ToList()` 变体（排序那行的调用形态不同没匹配上），单行文本没切开排序等于没排。已切到 ToolKit.SplitLines。**教训：批量替换后要 grep 复查所有变体**。
- **XML 压缩丢声明（用户实测）**：XDocument.ToString() 本来就不输出 XML 声明（经典坑），之前只在格式化路径手动拼了声明。修复：压缩时勾选"保留<?xml头"就手动拼回 doc.Declaration。
- **批量提取文件名（用户反馈）**："遍历子目录"键漏了汉化（补 Tools.FileNames.Recurse）；**递归逻辑复核无误**（CollectFilesAsync(folder, paths, recurse) 内部 sub && recurse 才递归）——用户"勾不勾都递归"的现象怀疑是旧实例/旧 exe（工具窗口单例缓存页面，重开进程才生效），已请用户用新构建复测。
- **时间戳（需求）**："时间戳 → 时间"改用时间格式下拉框里选的格式输出（此前固定 yyyy-MM-dd HH:mm:ss）。
- **README.md 更新（需求）**：附属功能下新增"🧰 常用工具"小节（四类工具 + 快捷键 Alt+5），窗口联动清单加上常用工具。
- **语言**：zh/en 各 +3 键（Settings.ToolsTrigger/.Sub、Tools.FileNames.Recurse），543 键对齐。
- **测试**：Debug 0 错误 0 警告；build.bat Release 正常。由用户实测（重点：Alt+5 热键、加解密紧凑布局、替换/排序、XML 压缩带声明、文件名遍历勾选）。

## 最新变更（2026-10-02 第四十五轮：第四十四轮补丁事故修复 + 3 个真 bug）

- **重大事故复盘：第四十四轮有 4 个补丁从未生效**（用户复测替换/排序/XML/时间戳全部"没生效"，实锤）。根因：patch 脚本中途 NOT FOUND 退出后，我只做了去重和后续编辑，**没有回头把剩余补丁执行完**就编译交付了。已补执行（ReplaceTool 归一化 LF、SortTool 的 .ToList() 漏改变体、XML 压缩拼声明、时间戳转时间用格式下拉）。**教训：批量补丁脚本必须一次跑完并 grep 验证每一处；脚本中途失败后禁止"先做别的稍后再说"**。
- **导航蓝色竖条压住第一个字（用户实测）**：BuildNavButton 的 Grid 没建 ColumnDefinition，竖条和文字叠在同一个单元格里。修复：两列布局（Auto 竖条列 + * 文字列）。
- **批量提取文件名递归（用户两次实测勾不勾都递归）**：StorageFolder.GetItemsAsync 版本逻辑复核无误但现象仍在，改为 **System.IO 确定性实现**——拖放项 File.Exists → 直接加；Directory.Exists → `Directory.EnumerateFiles(path, "*", EnumerationOptions { RecurseSubdirectories = 勾选 })`；勾选状态在 await 前先读进局部变量。另注：如果拖的是搜索结果/Everything 里选中的多个文件，那些文件本身就是拖放内容（可能来自不同子目录），与"遍历子目录"开关无关。
- **多行解密只出一行（用户实测）**：通读 CryptoToolPage，加解密共用 RunMultiLine，代码对称无明显问题；用控制台程序完整模拟"TextBox 的 \r 换行 → 逐行加密 → \r\n 拼接 → 粘回 → 逐行解密"回环，3 行全部正确解出——**引擎层无此 bug**。健壮性加固：RunMultiLine 逐行 Trim（尾随空格会导致 Hex/Base64 解码失败）。若用户仍复现，输出里的"[第N行失败: 原因]"标记会给出真实错误，让用户把标记内容反馈回来。
- **产物验证方法**：单文件 exe 开了 EnableCompressionInSingleFile，**字节搜索验证不到内容**（压缩后字符串不可见）；要验证产物新旧，查 `bin\x64\Release\net10.0-...\win-x64\DaenLauncher.dll`（未压缩中间产物）的特征串。本轮已验证 DLL 含全部新代码。
- **测试**：Debug 0 错误 0 警告；build.bat Release 正常；多行加解密回环控制台实测通过。由用户复测（替换、排序、XML 压缩声明、时间戳格式、蓝条、文件名递归、多行解密）。

## 最新变更（2026-10-02 第四十六轮：多行解密根因修复 + 部署真相）

- **多行解密只出一行——根因找到并用真实 UI 复实**（用户两次反馈，前两轮的"代码审查没问题"是误判）。根因：`CryptoEngine.RunBuffered` **忽略了 `DoFinal` 的返回值**——解密时它返回"去填充后的真实长度"（如 3），但代码返回了整个输出缓冲区（16 字节），尾部 13 个填充区残留 NUL 字节跟着进了明文。多行时每行明文都带一串 NUL，TextBox 渲染把第二行吞掉（视觉上"只有一行"）。修复：按 `ProcessBytes + DoFinal` 的实际长度截断返回。**教训：我的引擎回环测试用"前缀比较"（`back[..pt.Length] == pt`）而非全等，恰好放过了这个 bug——加解密测试必须全等断言。**
- **复现手段（重要资产）**：PowerShell UIA 自动化驱动真实 UI——Start-Process 启动 → 找"Daen Launcher"主窗口 → 点附属功能按钮 → 常用工具窗口 → ValuePattern 填输入框/密钥/IV → TogglePattern 勾选 → Invoke 加密/解密 → ValuePattern 读输出。配合 RunCore 临时日志（input/result 的 repr）拿到第一手证据：result=[123+13×NUL <CR><LF> 123+13×NUL]。**坑：① PS1 脚本必须 UTF-8 BOM 否则中文乱码解析失败；② 应用单实例——脚本 Start-Process 后要按窗口名（不是新 PID）找窗口；③ PowerShell 5.1 对某些静态方法名（如 Move）绑定失败（GetMethods 能看到却调不动），改名（MoveTo）绕过；④ 运行中的应用会锁住 DLL，构建前必须先 taskkill（我曾因此构建了旧代码还不自知——grep 只匹配 error CS 漏掉了文件占用错误）。**
- **批量提取文件名递归：彻底重写为 System.IO**——`Directory.EnumerateFiles(path, "*", EnumerationOptions { RecurseSubdirectories = 勾选 })`，勾选状态在任何 await 之前先读进局部变量；删除 StorageFolder 递归版。枚举语义已用控制台程序在用户同款测试文件夹（桌面\应用）上验证：不勾选=6 个文件（仅第一层），勾选=23 个。老的 StorageFolder 版逻辑反复审查无错、GetItemsAsync 实测也只返回第一层，用户的"勾不勾都递归"最可能与部署滞后有关（见下条）。
- **部署真相（本轮最大发现）**：用户桌面快捷方式指向 **`D:\Program Files\DaenLauncher\DaenLauncher.exe`——不是 build\ 下我构建的产物**！用户测的是手动部署的旧副本，这解释了此前多轮"修复没生效"（叠加真实的 DoFinal bug）。**措施：csproj 版本号 1.0.0 → 1.1.0（设置-关于可见），以后每轮交付让用户核对版本确认部署到位；交付说明里明确"先完全退出托盘里的应用，再把 build\DaenLauncher.exe 复制到 D:\Program Files\DaenLauncher\ 覆盖"。**
- **保留一轮 OnDrop 临时日志**（MiscTools，写 data\uia_debug.log，记录 recurse 勾选状态/文件数/路径清单）——若用户在新版本上仍见递归异常，让其提供该日志即可精确定位；确认正常后删除。
- **测试**：Debug/Release 0 错误 0 警告；UIA 真实 UI 回归：多行加密输出 2 行、解密输出干净 2 行（123\r123）；枚举语义 6/23 验证通过。

## 最新变更（2026-10-06 第四十七轮：必应每日壁纸附属功能上线）

> 用户消息开头写的是"常用工具"，但内容/图标/数据目录全是壁纸——"常用工具"是第四十二轮已上线的功能，本轮实为**新的附属功能：必应每日壁纸**（热键顺位 Alt+2/3/4/5/6 的 6 也印证）。

- **窗口**（`WallpaperWindow.xaml/.cs`，单例、关闭=隐藏、默认 960x640 记忆大小）：
  - 整幅圆角壁纸卡片铺满窗口（**Grid 自带 CornerRadius 能裁剪内部 Image，Border 不能**）；`UniformToFill` 等比铺满 + 底部黑色渐变遮罩上叠"日期大字（按语言格式化，zh=2026年10月06日）+ copyright 小字（最多两行）"；
  - 右下角强调色"更换壁纸"按钮（Sync 图标），点击 = 获取→下载→换桌面壁纸→记录→按需保存本地，成功后按钮文字短暂变"已更换"（与工具"已复制"同款反馈），失败显示顶部 Error InfoBar；
  - **打开窗口只展示不更换**：先秒显 record.json 里的上次结果（本地缓存无网络等待），再后台抓今日元数据+图片刷新；ProgressRing 加载态；
  - 窗口行为与常用工具同款：置顶（默认开）/锁定尺寸/显示位置（含上次位置 WallpaperLastWindowX/Y）/尺寸记忆/热键 Alt+6 显隐；三项联动（标题/材质/主题）全接入。
- **服务**（`Services/WallpaperService.cs` + `Models/WallpaperModels.cs`）：
  - 常量类 `WallpaperConstants`：接口地址、尺寸后缀（_1920x1080.jpg/_UHD.jpg）、来源标记（official/biturl）、域名（www/cn/global.bing.com）、日期格式 yyyyMMdd 全部集中（禁硬编码）；
  - **官方接口**：HPImageArchive.aspx 取 images[0] 的 urlbase/copyright/enddate，直链 = 域名前缀 + urlbase + 尺寸后缀；**biturl 接口**：取 url/copyright/end_date，从 "/th?id=" 截取路径、`LastIndexOf('_')` 去掉旧尺寸段（OHR 图片 id 自带下划线，尺寸一定是最后一段）、拼新后缀——两个来源拼链方式统一；
  - 换壁纸 = 写注册表 WallpaperStyle=10（填充）+ `SystemParametersInfo(SPI_SETDESKWALLPAPER)`（SetLastError，失败抛错码）；图片缓存 `data\wallpaper\cache\{enddate}_{尺寸}.jpg`（同日同尺寸复用不重复下载）；
  - **每日更换记录** `data\wallpaper\record.json`（JsonStore）：LastChangeDate（yyyyMMdd，"每日自动更换"据此判断今天是否已换过）+ 当前壁纸的 enddate/copyright/url/尺寸/完整时间；启动时 `Initialize()` 若勾选自动更换且今天没换过 → 后台自动更换（失败等 20 秒重试，最多 3 次——开机时网络常未就绪）；
  - 保存本地：更换成功（手动/自动都算）后按 `{enddate}_{尺寸}.jpg` 复制到设置目录；设置留空 = 默认 `data\wallpaper\image`（留空而非存路径，数据目录回退 LocalAppData 时默认位置仍正确）；
  - `_applyLock` 信号量防自动+手动并发更换。
- **设置页**（WallpaperPage，设置导航插在常用工具和关于之间）：壁纸尺寸（1080P 默认/4K）、数据来源（官方默认/biturl）、数据下载（通用默认/中国/全球）、每日自动更换（默认关）、保存壁纸文件到本地（默认关）、壁纸文件保存目录（输入框+浏览按钮，**仅勾选保存后可改**，FolderPicker 挂主窗口句柄 `InitializeWithWindow`，失焦提交、留空回显解析后的默认路径）、显示和隐藏（Alt+6，热键三件套注册+分发+订阅齐全，id=6）、永远置顶/锁定尺寸/显示位置。FluentGlyphs 新增 Download=\uE896。
- **接线**：AuxiliaryFeatures 加 wallpaper（图标 素材→Assets\Icons\必应每日壁纸_64.png）；MainWindow.OpenAuxiliaryFeature 分支；App 新增 ShowWallpaperWindow/ToggleWallpaperWindow/ApplyWallpaperWindowBehavior + 启动时 WallpaperService.Initialize()，主题/材质/标题/退出联动全接入；AppSettings 新增 Wallpaper* 16 项；语言 zh/en 各 +36 键（579 键两边对齐 0 缺失，日期格式串 Wallpaper.DateFormat 也进语言文件，en 用 "MMMM d, yyyy"）。
- **编译坑（新）**：`PickerLocationId` 在 `Windows.Storage.Pickers` 命名空间下，不能裸写（其余页面用 FileSavePicker 时也没写 SuggestedStartLocation 所以没暴露）。
- **产物验证**：Debug/Release 0 错误 0 警告；单文件 exe 已复制 build\；中间 DLL 探测（字符串字面量在 US 堆是 **UTF-16**，ASCII 探测会假阴性，要按 utf-16-le 探）确认 WallpaperService/WallpaperWindow/WallpaperPage、接口地址、图标资源全部在内。
- **测试**：接口/换壁纸/记录/保存目录待用户实测（重点：4K 尺寸直链是否有效、biturl 来源、每日自动更换的开机时机、保存目录浏览选择）。

## 最新变更（2026-10-07 第四十八轮：壁纸 toast 通知 + 显示模式 + 缓存清理策略）

- **更换成功弹系统 Toast（需求）**：新 `Services/ToastService.cs`——ToastGeneric 模板（**hero 大图**=壁纸文件 + 标题"壁纸已更换" + 日期 + copyright 描述），在 `ApplyWallpaperAsync` 成功路径调用（手动按钮/开机自动更换都会弹）；失败只写 crash.log 绝不影响换壁纸。
  - **解包应用发通知的 AUMID 双保障**：① 注册表 `HKCU\Software\Classes\AppUserModelId\<AUMID>`（DisplayName=appconfig 的 AppName、IconUri=exe）；② 用户开始菜单 `Daen Launcher.lnk`（缺失自动重建，进程内只查一次）。**快捷方式带 AUMID 必须走 IShellLinkW + IPropertyStore 写 PKEY_AppUserModel_ID（{9F4C2855-...}，pid=5）——WScript.Shell 做不了这件事**；PROPVARIANT 用 VT_LPWSTR（vt=31，指针在偏移 8），结构体尾部补到 24 字节（x64 真实大小）。
  - **坑（CS0030）**：ComImport 的 ShellLink coclass **不能标 sealed**，否则"类实例 cast 到 COM 接口"直接编译失败；另 `PickerLocationId` 上轮已记。
  - **独立控制台端到端实测通过**：官方接口→拼链→下载 336KB→注册表→快捷方式→`CreateToastNotifier(AUMID).Show()` 无异常（用户屏幕已弹过测试通知，enddate=20261007 直链有效）。测试现场已清理（开始菜单的 Daen Launcher.lnk 和注册表 AUMID 是应用自身要创建的，保留无害）。
- **"模式"设置（需求）**：`AppSettings.WallpaperStyle`（stretch 默认 / fit / fill / tile / center / span，设置页新卡片插在"壁纸尺寸"下面）→ 注册表映射：拉伸=2、适应=6、填充=10、平铺=0+TileWallpaper=1、居中=0、跨区=22（TileWallpaper=0）；常量全在 WallpaperConstants（RegistryStyle* / TileOn/Off）。旧版固定"填充"的行为被本设置取代。
- **cache 目录清理策略（用户问题：cache 什么时候删？此前实现永不删除会无限堆积）**：
  - 每次展示下载后：清理"既不是记录里当前壁纸、也不是本次下载"的文件；
  - 每次更换成功后：只保留刚设置的那张；
  - 被占用的文件跳过下次再删；效果 = cache 最多 1~2 张（当前桌面壁纸 + 今日展示图），旧的自动消失，image 目录（保存到本地）不受影响。
- **语言**：zh/en 各 +9 键（Settings.WallpaperStyle 8 + Notification.WallpaperChanged，588 键对齐 0 缺失）；版本号 1.2.0 → 1.3.0。
- **测试**：Debug/Release 0 错误 0 警告；Release 产物已复制 build\ 并探针确认。由用户实测（重点：toast 是否带大图正常弹出、六种模式切换后的桌面效果、cache 目录只剩当前壁纸）。

## 最新变更（2026-10-07 第四十九轮：数据导出/导入/删除支持壁纸数据 + README 同步）

- **数据页补上必应壁纸**（用户反馈第四十七轮遗漏）：`DataService.DataItems` 加 ("wallpaper", ["wallpaper"])（导出/导入/删除三处共用）；数据页导出勾选框、导入弹窗勾选框、删除勾选框各加"必应每日壁纸"一项（BuildCheckRow 加第 6 个参数，三处调用点同步）。
- **删除/导入后的缓存一致性**：WallpaperService 新增 `ReloadRecord()`（只重载 record.json，**故意不触发每日自动更换**——导入数据不该引起换壁纸）；删除勾选壁纸后调它 + `App.RefreshWallpaperWindowView()`（新方法）→ 壁纸窗口 `RefreshView()`：记录为空就清空画面，避免内存旧记录之后写回磁盘。导入流程本身走 PromptRestart 重启，无需额外处理。
- **语言**：zh/en 各 +1 键（Data.Item.Wallpaper，589 键对齐）。**踩坑记录：给语言 JSON 插键时用 `content.replace('"Data.Item.Clipboard"', ...)` 会把锚点行的键和值拆开导致 JSON 损坏——插键必须按整行/行级操作，插入后必须立即 json.load 校验**（本轮已当场修复，两边文件无损恢复）。
- **README**：设置-数据条目更新为明确列出六类数据（软件配置/启动器/待办/随手记/剪贴板/必应壁纸）。
- 版本号 1.3.0 → 1.3.1。Debug/Release 0 错误 0 警告，产物已复制 build\ 并探针确认（ReloadRecord/RefreshWallpaperWindowView 均在）。
- **测试**：由用户实测（导出勾壁纸 → 删除本地壁纸数据 → 导入回 → 壁纸窗口/记录恢复正常）。

## 最新变更（2026-10-07 第五十轮：Tooltip 增强 + 桌面快捷方式 + 默认尺寸 + 右键"添加项目"）

- **需求1 悬停提示增强**：项目 Tooltip 由"名称/备注"改为 **名称 + 路径 + 备注 + 命令行参数**（备注/参数为空时不显示对应行），MainWindow.BuildItemTooltip 统一构建，语言键 Main.Item.Tooltip.Path/Remark/Arguments。
- **需求2 创建桌面快捷方式**：项目右键菜单新增"创建桌面快捷方式"。新服务 `DesktopShortcutService` 分类型处理：Exe/Lnk/File/Folder 走 WScript.Shell 反射建 .lnk（带 Arguments/WorkingDirectory）；Url/Protocol 写 .url 文件（InternetShortcut INI，URL 先转绝对 URI 转义非 ASCII）；**Uwp 用 SHParseDisplayName(项目路径) → IShellLinkW.SetIDList → IPersistFile.Save**（系统同款方式，快捷方式自动带应用图标），失败回退 explorer.exe 中转 .lnk。桌面文件不覆盖：重名追加" (2)"。成功静默，失败弹错误弹窗。复用 LnkResolver 里 internal 的 ShellLink/IShellLinkW/IPersistFile 声明。
- **需求3 默认显示尺寸**：项目图标 40→**32**、文字 12→**14**、横向间距 8→**12**。默认值提为 AppSettings 常量（DefaultItemIconSize/DefaultItemTextSize/DefaultItemHorizontalSpacing），设置页"恢复默认"按钮引用同一常量（不再两处漂移）。
- **需求4 右键"添加项目"**：项目面板空白处右键 → MenuFlyoutSubItem"添加项目" → **可执行程序(.exe 选择器)/快捷方式(.lnk 选择器，DropResolver.ResolveShortcutFile 解析出真实目标，与拖拽行为一致)/文件夹(FolderPicker)/文件/网址(输入弹窗+校验)/UWP 应用（微软商店）/协议(输入弹窗+校验)**。目标子分类 = 指针所在卡片/Tab头（FindSubAtPanelPointer，从 PanelHost_Drop 的两处重复命中逻辑提取）> 当前 Tab > 第一个。MainWindow.xaml 给 PanelHost 挂 RightTapped；右键到项目按钮上时跳过（项目自己的菜单已弹出）。
- **UWP 应用枚举**：新服务 `InstalledAppService`，枚举 shell:AppsFolder（AppsFolder shell 命名空间）：IShellFolder.EnumObjects 逐项取解析名，**只保留解析名含 "!" 的项（UWP AUMID 固定"包家族名!应用ID"，桌面程序没有）**。实测 264 子项 → 38 个商店应用。结果进程内缓存 + 显示名排序。新弹窗 UwpAppPickerDialog：搜索框（按名称/ID 过滤，运行时 DataTemplate + {Binding}）+ ListView 双行（名称+ID）。添加后 Path = "shell:AppsFolder\AUMID"（**前缀提为 Models.LauncherItemPaths.UwpPrefix 常量**，LauncherRunner/DropResolver 同步改用）。
- **新增服务**：DesktopShortcutService、InstalledAppService、ShellConstants（FOLDERID_AppsFolder GUID/SHGDN/SHCONTF 常量）、Dialogs.TextItemDialog（网址/协议共用，实时校验：网址须 http/https；协议须 scheme: 形式且排除 http/https；名称可自动填充主机名）。
- **语言**：zh/en 各 +19 键（608 对齐 0 缺失）：Main.Item.CreateShortcut(+Failed)/Add.*（7 类型）/Tooltip.*(3)、Main.UwpPicker.*（2）、Dialog.ItemUrl/ItemProtocol/UrlInvalid/ProtocolInvalid。
- **测试**：Debug 0 错误 0 警告；**核心 COM 互操作用独立控制台程序验证**——AppsFolder 枚举（264 子项/38 UWP）、UWP .lnk 创建（SetIDList）、.url 写入、WScript .lnk 全通过；应用启动冒烟 6s 存活无 crash.log。UI 交互（菜单/弹窗/Tooltip 显示效果）待用户实测。

## 最新变更（2026-10-07 第五十一轮：UWP/协议项目显示真实图标）

- **问题**：UWP 应用和协议类型的项目添加后只显示通用占位图标——`ItemIconService.GetIconAsync` 原来对 Uwp/Url/Protocol 三种类型直接早退返回 null。
- **UWP 图标**：新方法 `ExtractUwpIconPng`——`SHCreateItemFromParsingName(shell:AppsFolder\AUMID)` → `IShellItemImageFactory.GetImage(256×256, SIIGBF_ICONONLY|SIIGBF_BIGGERSIZEOK)` → `HBitmapToPngBytes`。这是系统给商店应用建快捷方式用的同款 API，拿到的是应用正式图标（256px 高清，UI 自动缩放）。路径规范化：没带 `shell:AppsFolder\` 前缀但含 `!` 的老数据自动补全。**实测 Armoury Crate（用户给的 AUMID）成功提取 256×256 官方图标**。
- **协议图标**：新方法 `ExtractProtocolIconPng`——`AssocQueryString(ASSOCSTR_EXECUTABLE, 协议名, "open")` 查协议默认处理程序的 exe → 复用 SHGetFileInfo 提取。**实测 steam:// → steam.exe 图标**。注意语义：一个协议只有一个处理程序，同一协议的不同地址（不同 Steam 游戏）图标相同；无 exe 处理程序的协议（ms-settings:）返回 0x80070483 → 回退占位图标。
- **HBITMAP→PNG 透明度保留**：GetImage 返回的 HBITMAP 不能直接 `Image.FromHbitmap`（丢 alpha，图标变黑底方块）——用 `GetDIBits` 拷 32bpp 位数据（负高度=自上而下）→ 扫描 alpha 字节全为 0 则置 255（修正无透明通道图标整体消失）→ LockBits 写回 → 存 PNG。
- **Url 类型维持占位图标**：各网站应显示各自图标需要联网取 favicon，暂不支持（协议/UWP 是本地可解的，所以本轮只做这两个）。
- **缓存**：走原有机制（item.Id.png + .meta 记录源路径，路径改了自动重新提取）。
- **测试**：独立控制台程序验证（IShellItemImageFactory 三应用 256px + steam 协议 + ms-settings 优雅失败 + alpha 采样确认透明区域正常）；Debug 0 错 0 警；应用冒烟 6s 存活无 crash.log。UI 效果待用户实测（老项目首次显示时会有一次图标提取）。
- 版本号 1.4.0 → 1.4.1。

## 最新变更（2026-10-07 第五十二轮：网址自动取 favicon + UWP 选择器列表带图标）

- **需求1 网址类型联网取 favicon**：新服务 `FaviconService`（纯 System.Drawing+Http，无 WinUI 依赖，可独立测试）。抓取策略：① `https://主机/favicon.ico` → ② `http://主机/favicon.ico` → ③ 下载首页前 256KB 解析 `<link rel="icon">`（优先 apple-touch-icon，相对地址转绝对）再下载。下载后统一重编码：ICO 用 `new Icon(ms,32,32)` 选最接近 32px 的帧（GDI+ 默认取第一帧常常 16px 太糊），大图等比缩到 ≤128px，统一转 PNG 存缓存。
  - **站点连不上直接短路**：TryDownloadAsync 返回 (png, reachable)，连接失败就放弃后续尝试——失败站点 24s → 8s（实测 github 直连）。HttpClient 共享实例，**默认走系统代理**（用户配了代理自动生效，不硬编码代理）。
  - **会话内失败记录**：ItemIconService.FailedFaviconUrls（HashSet+锁）——面板重建会反复触发 GetIconAsync，失败网址本次会话不再联网（避免反复超时），下次启动自动重试。
- **需求2 UWP 选择器列表加图标**：`InstalledUwpApp` record → **INotifyPropertyChanged 类**（+IconSource 属性，加载完自动刷新行）；`ItemIconService.GetUwpAppIconPngBytesAsync`（按 AUMID 磁盘缓存 `uwp_应用ID.png`，主面板项目图标仍按项目 Id 缓存，两套互不影响）；选择器 DataTemplate 加 32×32 Image 列 + 后台**顺序**加载循环（LoadUwpAppIconsAsync，避免几十个并发 Shell 提取；每个完成切回 UI 线程创建 BitmapImage——WinUI 位图有线程亲和性）。列表立即显示、图标逐行浮现，搜索过滤共用同一批对象不重复加载。
- **踩坑**：这版 SDK 的 **XamlRoot 没有 DispatcherQueue 属性**（后续版本才有）→ 在弹窗打开时（UI 线程上）`DispatcherQueue.GetForCurrentThread()` 取。
- **测试**：FaviconService 用独立工程直接 Compile Include 真实源文件实测——baidu 64px/qq 96px/bilibili 32px(CDN link 解析)/zhihu 32px 成功、github 直连失败优雅 null（8s 短路）、不存在域名 51ms null；图标透明通道完好（QQ 企鹅/B 站电视目视确认）。Debug 0 错 0 警；应用冒烟 6s 存活无 crash.log。UI 效果待用户实测。
- 版本号 1.4.1 → 1.4.2。

## 最新变更（2026-10-07 第五十三轮：右键菜单"刷新图标"）

- **需求**：项目右键菜单新增"刷新图标"（放在"复制完整路径"之后、删除之前），点击后强制重新提取图标并更新本地缓存，**支持全部类型**（可执行程序/快捷方式/文件夹/文件/网址/UWP/协议）。
- **实现**：`ItemIconService.RefreshIconAsync(item)`——DeleteCache（清 {itemId}.png + .meta）→ **ClearFailedFavicon**（新增 public 方法：网址类型清会话内失败记录，允许立即重新联网抓 favicon，否则会被 FailedFaviconUrls 拦住直接返回 null）→ GetIconAsync 按类型走原提取链路（Shell 提取/IShellItemImageFactory/AssocQueryString/FaviconService）并重写缓存。成功后 RebuildPanel 让新图标立即显示；失败弹"图标刷新失败：{0}"。
- **语言**：zh/en 各 +2 键（Main.Item.RefreshIcon / RefreshIconFailed，610 键对齐 0 缺失）。
- **测试**：Debug 0 错 0 警；应用冒烟 6s 存活无 crash.log。UI 交互待用户实测。

## 最新变更（2026-10-07 第五十四轮：修复 Steam 游戏 .url 拖入误判"网址"+ 游戏专属图标）

- **问题**：桌面拖入 Steam 游戏快捷方式（`.url` 文件，内容 `URL=steam://rungameid/993090`）被一律标为"网址"类型——网址类型联网抓 favicon，steam:// 永远抓不到 → 图标不显示、"刷新图标"也失败。
- **修复1 类型判断**：`DropResolver.ResolveShortcutFile` 的 .url 分支新增 `ClassifyUrl`——http/https → 网址，其他（steam:// 等）→ **协议**（.lnk 分支原本就有此判断，.url 分支漏了）。
- **修复2 游戏专属图标**：Steam 的 .url 里带 `IconFile` 声明（指向该游戏的 ico，如 `steam\games\xxx.ico`）。`LnkResolver` 新增 `ReadInternetShortcut`（URL + IconFile + IconIndex 三项解析）；`LauncherItem.IconFile` 字段（原本声明未使用）改为"外部图标源路径"语义，.url 拖入时记录；`ItemIconService` 提取图标时**优先用 IconFile**（新增 `ExtractExternalIconPng`：png/jpg/bmp/gif 直接 GDI+ 解码，.ico 等走 SHGetFileInfo），文件消失回退类型默认提取。**实测 Lossless Scaling 提取出黄色小鸭游戏图标（比协议处理程序的通用 Steam 图标更好）**。右键"刷新图标"对此类项目同样生效。
- **修复3 存量数据迁移**：`LauncherDataService.NormalizeLegacyUrlTypes`——启动/导入重载时把"网址类型但地址非 http/https"的项目自动修正为"协议"并落盘（用户现有的 Lossless Scaling 条目下次启动自动变协议、显示 Steam 图标；想换游戏小鸭图标删掉重新拖入即可）。
- **测试**：控制台工程编译真实源文件实测——真实桌面 Lossless Scaling.url 解析为 Protocol + IconFile 指向游戏 ico ✓；.ico 经 SHGetFileInfo 提取出 32px 小鸭图标 ✓。Debug 0 错 0 警；应用冒烟 6s 存活无 crash.log。
- 版本号 1.4.3 → 1.4.4。

## 最新变更（2026-10-07 第五十五轮：设置-常规新增"代理"配置，全局联网统一走代理）

- **需求**：设置-常规新增代理卡片——下拉框（不使用代理/使用系统代理设置/使用 HTTP 代理/使用 SOCKS5 代理，**默认系统代理**）；选 HTTP/SOCKS5 时显示地址/端口/用户名/密码输入框 + "测试代理"和"保存代理"按钮，**测试通过才能点保存**。
- **新服务 `ProxyService`**（所有联网代码统一入口，禁止自己 new HttpClient）：
  - `CreateHttpClient(timeout?)`——按设置构造 `SocketsHttpHandler`：None=UseProxy=false；System=默认（读系统代理）；Http/Socks5=`WebProxy("http://…" / "socks5://…")`（.NET 内置支持 socks5），账号非空时挂 NetworkCredential。
  - **共享 handler + 配置指纹**：模式|地址|端口|账号|密码 拼指纹，变化即换新 handler（旧的不 Dispose 防在途请求崩），**代理修改立即生效无需重启**；各功能共用连接池，超时各自传参（favicon 8s/webnote 15s/壁纸 20s/默认 30s）。
  - **防呆**：HTTP/SOCKS5 模式下地址为空或端口非法 → 回退系统代理（避免配置不完整导致全软件断网）。
  - `TestProxyAsync(mode,host,port,user,pass)`——用界面值（非已保存值）构造 handler 请求 `https://www.baidu.com/`（国内外都可达），失败消息原样展示（连接拒绝/超时/认证失败可直接定位）。
  - **占位注释**：检查版本更新（后续完善）实现时用 CreateHttpClient 即可自动走代理。
- **消费者接入**（字段改属性，调用点零改动）：FaviconService / WebNoteClient（webnote 云同步）/ WallpaperService（必应壁纸）。
- **设置 UI**：GeneralPage.BuildProxyCard——模式切换立即保存（与其他设置一致）+ 明细区按模式显隐；**任一字段修改后测试结果作废、保存按钮重新禁用**；端口校验 1-65535；结果文字 绿=通过/红=失败/灰=测试中。卡片图标复用 Globe 字形。
- **测试**：控制台工程编译真实源文件实测——HTTP 真实代理(127.0.0.1:7890) OK、直连 OK、未开放端口快速失败带原因、空地址回退系统代理 OK、SOCKS5 协议真实生效（7890 是 mixed 端口同时支持）、指纹重建 OK。Debug 0 错 0 警；应用冒烟 6s 存活无 crash.log。设置页 UI 待用户实测。
- **语言**：zh/en 各 +20 键（Settings.Proxy.* 20 项，630 键对齐 0 缺失）。版本号 1.4.4 → 1.5.0。
- **README 同步**（第五十~五十五轮）：多种项目类型（右键添加项目/UWP 商店应用选择器）、智能图标完整能力（UWP 高清图标/协议处理程序图标/favicon/Steam 游戏专属图标/刷新图标）、右键菜单新项（创建桌面快捷方式/刷新图标）、悬停提示、设置-常规网络代理。

## 最新变更（2026-10-08 第五十六轮：3 个 BUG 修复 + 3 个新常用工具）

### BUG1 多显示器"上次位置"失效（副屏永远居中）

- **用户现象**：多显示器电脑上显示位置选"上次位置"，主屏正常；副屏每次显示都在副屏正中央。
- **两个叠加根因**（都在 `ComputeShowPosition` 里）：
  1. **判断"是否记录过位置"用了 `>= 0`**——虚拟桌面坐标系里副屏在主屏左侧/上方时 X/Y 是 **负数**，保存的正确值（如 -1700）被这个判断当成"没记录过"，直接落到居中分支 → 每次都回到副屏中央（正是用户看到的现象）；
  2. **钳制用的是"窗口当前所在显示器"的工作区**（`DisplayArea.GetFromWindowId`，恢复时窗口还在主屏）——即使坐标是正数，`x + width > 主屏右边界` 也会把副屏坐标硬拉回主屏。只有副屏在主屏右下方时碰巧不被钳制，所以表现为"部分情况正常"。
  - 附带：尺寸换算用的 `GetDpiForWindow`（窗口当前所在屏的 DPI）在混合缩放的副屏上会算错宽高。
- **修复：新增 `Services/WindowPositionHelper.cs`（六处重复逻辑收敛成一份）**：
  - "上次位置"分支先 `DisplayArea.GetFromPoint(记录坐标)` **按记录的坐标找到那块显示器**，再用它的工作区做 `Math.Clamp`；
  - "是否记录过"统一用哨兵值 `AppSettings.WindowPositionNotSet`（-1，新增常量），**负数坐标是合法值**；
  - 尺寸换算新增 `Win32Helper.GetDpiScaleForPoint(x, y)`（`MonitorFromPoint` + `Shcore.GetDpiForMonitor`，取目标屏的 DPI）；
  - "跟随鼠标"也顺带修正为按**鼠标所在显示器**的工作区钳制（原来用窗口所在屏）。
- **六个窗口全部改为调用该 Helper**：主窗口、待办、随手记、剪贴板、常用工具、必应壁纸（此前六份代码各自复制粘贴同一段错误逻辑）。
- **实测**：把 `LastWindowX` 设为 -1700（模拟主屏左侧的副屏）启动，窗口按 LastPosition 分支处理（本机只有 0~2560 单屏，X 被钳制为 0）；修复前同样的输入会走进居中分支（X≈820）。

### BUG2 项目文字被截断（"启动oracle"只显示"启"）

- **用户现象**：一行内容少、格子看起来很宽，但"启动oracle"只显示"启"字，同行的"启动redis"却完整。
- **根因**：`BuildItemText` 给文字块写死了 `MaxWidth = Math.Max(50, ItemIconSize * 1.8)`（默认图标 32 → **57.6px**），与格子实际宽度无关；`TextTrimming = CharacterEllipsis` 就在这 57.6px 上截断。"启动redis"比它窄所以完整显示。所谓"格子很宽"是 `UniformWrapPanel` 给的格子宽，文字块自己仍被卡在 57.6px。
- **修复**：去掉这个 MaxWidth（文字宽度改由格子约束决定）。
- **连带问题**：去掉上限后，一个超长名字会让"等宽格子"撑到整行宽、面板退化成单列。
  本轮（第五十六轮）先按"格子宽度自适应内容 + 至少 2 列"实现，**但方向是错的**——
  格子宽度随内容变化会导致"项目数量/文字长短影响列数与列位置"，正是用户第五十七轮反馈的
  "两个子分类一个 2 列一个 3 列、项目少的被拉伸"问题。**第五十七轮已改为"格子宽度是固定常量、
  列数只由窗口宽度决定"的正确模型**（详见第五十七轮）。

### BUG3 拖入 .bat 快捷方式后路径是 .lnk（不是真实目标）

- **用户现象**：拖桌面"START-HERE.bat - 快捷方式"进来，项目路径存的是 `...\START-HERE.bat - 快捷方式.lnk`，而真实目标是 `D:\Program Files\Strata-main\START-HERE.bat`。
- **根因**：`DropResolver.ResolveShortcutFile` 的类型判定只有 `.exe` 一支（`target.EndsWith(".exe")`），**非 exe 的本地文件目标全部落到最后的兜底分支**——那里 return 的是 `path`（快捷方式自己）+ `Type = Lnk`，已经解析出来的 `target`/`args` 被丢弃。.bat/.cmd/.ps1/.msc 以及普通文件（.txt 等）全中招。
- **修复**（`DropResolver.cs`）：
  - `.exe` 判断之后新增"**目标是存在的本地文件**"分支 → `Path = target, Type = File`（启动走 ShellExecute，系统按扩展名关联执行，与 .txt 等一致）；
  - 目标是文件夹时也直接识别为 `Folder`（原来会退回 .lnk）；
  - 目标不存在（快捷方式指向的文件已被删除）才保留快捷方式本身为 `Lnk`，保证仍可点击启动；
  - 浏览器噪音参数过滤提取为 `StripBrowserNoiseArguments`。
- **连带排查修复（同类问题一处）**：右键"添加项目 → 文件"选中 .lnk/.url 时**不会解析目标**（只有"快捷方式"菜单项会）→ 现在两个菜单项都统一走 `ResolveShortcutFile`，且从"文件"菜单选中 .exe 也会正确标为 Exe 类型。
- **存量数据自动迁移**：`LauncherDataService.NormalizeLegacyShortcutPaths()`——启动/导入重载时把"Path 指向存在 .lnk/.url 的 Lnk 类型项目"重新解析成真实目标（只在解析结果确实不同时才改，解析不出来保持原样）；与旧有的 `NormalizeLegacyUrlTypes` 共用一个改动标记。
- **实测**：独立控制台工程**直接 Compile Include 真实源文件**，对 8 种快捷方式断言全过——.exe ✓、.exe 带参数 ✓、.bat ✓（本次核心）、.txt ✓、文件夹 ✓、目标不存在回退 .lnk ✓、.url 网址 ✓、.url 协议(steam) ✓。

### 新工具1 已连接 WiFi 密码查看（其他常用）

- **服务 `Services/WifiService.cs`**：调系统 `netsh` 解析文本（参数化 `ProcessStartInfo`，不经 shell）。
  - 列表 `netsh wlan show profiles`；详情 `netsh wlan show profile name="X" key=clear` 取"关键内容"=密码、"身份验证"=加密方式。
  - **netsh 输出编码跟随系统区域设置**（中文 Windows 常见 GBK）→ 读原始字节后先按 UTF-8 严格解码，失败回退 `CodePagesEncodingProvider` + `CurrentCulture.TextInfo.ANSICodePage`（需 `Encoding.RegisterProvider`，.NET Core 默认不带 GBK）。
  - 关键字中英文双匹配（名称/Name、关键内容/Key Content、身份验证/Authentication）；超时 8s 防界面卡死。
  - **隐藏网络判定**：必须匹配"**未广播**"/"not broadcasting"——普通网络那行是"只在网络广播时连接"，只匹配"广播"会全部误判为隐藏（实测踩到）。
- **工具页 `WifiPasswordTool`**（`Views/Tools/NetworkTools.cs`）：顶部"刷新列表"按钮 + 状态行 + 列表（名称 + 加密方式，卡片样式）；**右键行**弹菜单"查看密码和二维码/复制密码"。
  - 弹窗：二维码（260px，手机扫码直接连网）+ 名称/密码/身份验证三行（值用只读 TextBox 以便选中复制）；底部"复制二维码内容"+"关闭"。
  - netsh 调用放 `Task.Run` 后台线程；页面缓存导致 `Loaded` 会多次触发 → `_loadedOnce` 标记只自动读一次。
  - **本机实测（UIA + 截图）**：读到 **97 个**已连接 WiFi、emoji SSID（🐷🍊🌹）完整无乱码、弹窗二维码 260×260 渲染正常、密码 `q12345678` 与 netsh 一致。

### 新工具2 北京时间同步（其他常用）

- **服务 `Services/TimeSyncService.cs`**：
  - **NTP/SNTP 授时**（RFC 4330 简化客户端）：UDP 48 字节包（首字节 0x1B = LI0/VN4/Mode3），从响应 **字节 40..43**（Transmit Timestamp 大端秒数）取时间，减去 1900→1970 的 2208988800 秒偏移，再加**往返时间的一半**做补偿。
  - **健壮性（实测教训）**：UDP 丢包常见 → **每个 IP 重试 3 次**；域名解析出多地址时逐个尝试且 **IPv4 优先**（只有 IPv6 的服务器在纯 IPv4 网络必然失败）。国家授时中心实测要重试才通。
  - **HTTP 时间 API**：走 `ProxyService.CreateHttpClient`（遵循软件代理设置）；响应里正则找时间戳，字段名各接口不同所以用宽松集合 `t|currentTime|timestamp|now|sysTime`（淘宝 `t`、苏宁 **`currentTime`**——只匹配 `t` 会漏掉苏宁），兜底读 HTTP 响应的 `Date` 头。
  - **实测可用清单**（2026-10-08 本机同时验证直连与代理）：NTP = 国家授时中心 ntp.ntsc.ac.cn、阿里云 ntp.aliyun.com、腾讯云 ntp.tencent.com、华为云 **ntp.cn-north-1.myhuaweicloud.com**、中国 NTP 公共池 cn.pool.ntp.org、教育网 time.edu.cn；HTTP = 淘宝 **https://acs.m.taobao.com/gw/mtop.common.getTimestamp/**、苏宁 https://f.m.suning.com/api/ct.do、百度 https://www.baidu.com（响应头）。
  - **踩坑**：`ntp.huaweicloud.com` **域名不存在**（多个公共 DNS 均 NXDOMAIN），华为云公网 NTP 要用区域域名且部分区域是 VPC 内网不可达 → 选 cn-north-1；`api.m.taobao.com` 那个旧接口本机直连 TLS 失败 → 换 acs 域名。
  - **同步本地时间**：`ProcessStartInfo{ UseShellExecute=true, Verb="runas" }` 提权跑 PowerShell `Set-Date`（会弹 UAC）。**`Verb="runas"` 必须配 `UseShellExecute=true`**，且此组合**不能重定向输出** → 改为"事后重读时钟校验"判断成功（容差 10s）；UAC 被拒的错误码 1223 = ERROR_CANCELLED 单独提示。同步的是同一 UTC 瞬间，本地时区不变。
- **工具页 `TimeSyncTool`**：本地时间/北京时间两个大字号时钟（每秒刷新）+ 差值文字（快/慢 N 秒，<1s 显示"一致"）+ 获取方式下拉（NTP/HTTP，切换时重建服务器下拉）+ 服务器下拉 + "同步本地时间"按钮；进入界面自动取一次（静默失败）。
- **实测**：6 个 NTP 源 + 3 个 HTTP 源全部真实取到时间（本机 UTC 15:0x ↔ 本地 23:0x，即北京时间）；"同步本地时间"未实际提权执行（避免改动本机时钟），仅代码路径校验。

### 新工具3 二维码生成（其他常用）

- **服务 `Services/QrCodeService.cs`**：NuGet 新增 **QRCoder 1.6.0**（`PngByteQRCode` 直接产出 PNG 字节，**以 PNG 字节为唯一产物**——显示和保存共用，不落临时文件、不依赖 System.Drawing 渲染器）。
  - 尺寸：`pixelsPerModule = max(4, 目标尺寸 / (模块数 + 8))`（+8 = 上下各 4 模块静区，`drawQuietZones: true` 必开，否则很多扫码器认不出）；实际边长取模块整数倍保证边缘锐利。
  - 纠错级别对外用自建枚举 `QrCodeService.ErrorLevel`（Low/Medium/Quartile/High），**不把 QRCoder 类型泄漏到界面层**。
  - `BuildWifiPayload`：`WIFI:T:加密方式;S:名称;P:密码;H:是否隐藏;;`，名称/密码里 `\ ; , : "` 必须转义（按规范加反斜杠）。
- **工具页 `QrCodeTool`**：内容输入区 + 纠错级别下拉（默认 M）+ "生成"（蓝色）/ "保存图片" + 状态行 + 白底圆角二维码图片区 + "复制结果"。
  - 保存走 `FileSavePicker`（绑主窗口句柄），**必须 `FileMode.Create`**（另存为对话框会预创建空文件，`CreateNew` 会抛 IOException——第七轮踩过的同类坑）。
- **实测（独立解码器验证，最强证据）**：用 **ZXing.Net**（完全独立的第三方解码器）把 QRCoder 生成的图**解回原文**——7 个用例（网址/中文/单字/500 字符/ECC L·M·Q·H/WiFi 载荷）**全部 PASS 逐字相等**；另验证 PNG 签名 `89 50 4E 47…`、各等级容量边界（4000 字符在 M/Q/H 报 `DataTooLongException` 被正确捕获为可读提示）、WiFi 转义断言通过。

### 编译坑 / 教训（第五十六轮）

- **`ProcessStartInfo` 的 `Verb = "runas"` 与输出重定向互斥**：必须 `UseShellExecute = true`，此时不能再设 `RedirectStandardOutput/Error`（运行时会抛），失败判定要另想办法（重读状态校验）。
- **`Windows.Storage.Streams` 的 buffer 转换在这版 SDK 上不顺手**：`IBuffer.AsStream()` / `WriteableBitmap.PixelBuffer` 直接操作都编译不过 → 结论是**不要自己拼像素**，用库的 PNG 编码器产字节、再 `DataWriter` + `BitmapImage.SetSourceAsync` 显示（`SetSource` 同步版也可）。
- **QRCoder 命名空间**：`QRCode`/`PngByteQRCode` 渲染类不在 `QRCoder` 根命名空间能直接用的位置，`QRCode` 类会 CS0246 → 用 `PngByteQRCode`（其 `GetGraphic(int, bool drawQuietZones)` 重载实测可用）。
- **netsh 输出编码**是中文 Windows 的实际坑（GBK），必须注册 CodePagesEncodingProvider；`UTF8Encoding(false, true)` 的严格模式用来做"能 UTF-8 就 UTF-8"的探测。
- **UIA 测试脚本**：PowerShell 变量 **`$TRUE`/`$true` 是内建常量不能自建**（会 `MethodArgumentConversionInvalidCastArgument`）；Heredoc 写 C# 正则会被 shell 吃掉反斜杠（改用 Write 工具或 python 写文件）；中文/emoji 经控制台输出会丢 → **结果写 UTF-8 文件再由 python 读**才是可靠验证方式。
- **语言键删改必须校验**：给 JSON 插/删键后用 `json.load` 复验 + 键集合对比 + "代码引用的键是否都有定义"的反向扫描（本轮据此发现我多加了一个没人用的 `Tools.Wifi.Empty` 并删除）。

### 语言 / 版本

- zh/en 各 **+64 键**（Tools.Wifi.* 16 / Tools.Time.* 30 / Tools.Qr.* 13 + 3 个工具名），删掉 1 个未使用的 `Tools.Wifi.Empty` → **693 键两边对齐 0 缺失**，无空值。
- 版本号 1.5.0 → **1.6.0**；Debug/Release 均 0 错误 0 警告；Release 单文件 exe（136MB）已复制 `build\DaenLauncher.exe`，探针确认全部新服务/新工具/新语言键在中（US 堆字符串要按 **utf-16-le** 探，ASCII 会假阴性——第四十七轮已记）。

## 最新变更（2026-10-08 第五十七轮：修正平铺布局模型——格子宽度固定、列数只由窗口宽度决定）

> **用户反馈（第五十六轮 BUG2 的修复方向错了）**：我上一轮把"格子宽度"改成去适应文字内容，结果**两个项目数量相近的子分类一个 2 列一个 3 列**，明明窗口宽度本可以容纳更多列。用户明确要求的模型是：
> **文字长度取决于格子宽度 → 格子宽度取决于（项目图标大小、项目文字大小、横向间距、纵向间距）→ 每行几列取决于（格子宽度 + 窗口宽度）**；
> 列数**不得**取决于项目数量或文字长短；每列必须对齐；**绝不能因为某子分类项目少就把格子拉伸平分整行**。

- **布局模型重写**（`Controls/UniformWrapPanel.cs`）：
  - 新增 `CellWidth` 属性 = **固定格子宽度**（平铺模式格子宽度不再随内容/可用宽度变化）；
  - `MeasureOverride` 用"格子宽度 − 该子元素左右外边距"作为**宽度约束**去测量子元素 → 文字在格子内换行/超出省略（文字宽度由格子决定，符合用户模型）；
  - 列数 = `可用宽度 ÷ 格子宽度` 向下取整，**只由窗口宽度决定**（不再 `Math.Min(列数, 子元素数量)` 收敛，这正是"项目少的子分类列数变少/被拉伸"的根源）；
  - 平铺模式 `ArrangeOverride` 用固定 `_cellWidth` 排列，**末行不满不再平分铺满**（去掉"最后一行为铺满而拉伸"的旧行为）→ 所有子分类列位置恒定、天然对齐；
  - 列表模式（`FixedColumns = 1`）保持原语义：格子 = 整行平分、铺满，悬停高亮满行。
- **格子宽度的来源**（`Models/AppSettings.cs`）：新增 `ComputeItemCellWidth()` ——
  `max(项目图标大小, 项目文字大小 × ItemCellTextChars) + 按钮内边距边框 + 横向间距`。
  - 新常量 `ItemCellTextChars = 9`（"格子能容纳几个文字字符"，默认字号 14 下每格约 9 个中文 / 18 个西文；**这是唯一的"格子要多大"调节点**，不受项目数量/文字长短影响）；`ItemCellMinContentWidth = 48`（图标文字都不显示时的保底）、`ItemButtonPadding = 6`、`ItemButtonBorderThickness = 1`、`ItemButtonChromeWidth`（算格子宽必须加上按钮自身的内边距+边框，否则文字可用宽度少一点点会被提前省略）。
  - **纵向间距不参与格子宽度**（它只影响行距）——用户提到的四项设置里，前三项决定格子宽、纵向间距决定行距。
- **面板创建统一走一个工厂**（`MainWindow.CreateItemsPanel(settings)`）：`BuildItemsHost` 和拖放路径（`PanelHost_Drop`）此前各自 new 面板、易漂移，现在共用同一个工厂（平铺=固定格子宽、列表=固定 1 列）。
- **实测（真实数据 + 截图 + UIA 坐标）**：用用户提供的 `build/data`（18/9/20/7/7/10/3/2… 项的子分类）验证——
  - 同一分类下多个子分类**列数完全一致且左对齐**（如 1300px 宽时两子分类都是 6 列）；
  - **项目少不拉伸**：9 项的子分类第 3 行只有 3 个，仍从第 1 列起排、不铺满整行；
  - **列数只随窗口宽度变化**：900px → 4 列、1041px → 5 列、1300px → 6 列、1500px → 9 列，两个子分类始终同步；
  - 列表模式仍为一行一个、文字完整不截断；Tab 风格与卡片风格表现一致。
- **教训（重要）**："为了多显示文字而把格子宽度改成自适应内容"是错的方向——**等宽网格的格子宽度必须是外生常量**，让内容去适应格子；一旦让格子去适应内容，列数/列位置就会随数据变化，"对齐"这个需求目标必然失败。

## 最新变更（2026-10-09 第五十八轮：平铺格子宽度加设置页滑条）

- **需求**：第五十七轮已验证布局模型正确（用户确认"效果非常棒"），按当时提议给"每格显示多少字"加一个设置页滑条。
- **实现**：
  - `AppSettings.ItemCellTextChars` 由 **const 常量改为可序列化的属性**（默认值常量 `DefaultItemCellTextChars = 9`；新增 `MinItemCellTextChars = 1`、`MaxItemCellTextChars = 30`）。**旧 settings.json 没有这个键时会用属性默认值 9**，不影响老用户配置；`ComputeItemCellWidth()` 里再 `Math.Clamp` 兜一层，越界值自动纠正。
  - 设置页"显示大小"卡片新增滑条 **"项目格子宽度（每格字符数）"**（范围 1~30，默认 9），排在"纵向间距"之后、"项目文字最多显示行数"之前——按"先定格子尺寸、再定行数"的逻辑顺序。拖动即时 `SaveAndRefreshPanelOnly()` 生效。
  - `BuildSliderRow` 新增可选参数 `min`（默认 0，保持其它滑条原语义），构造时对值做 `Math.Clamp` 防越界。
  - "恢复默认"按钮一并重置该项为 `AppSettings.DefaultItemCellTextChars`。
- **实测（UIA 真实拖动 + 截图）**：滑条 RangeValue 范围实测 `min=1 max=30`、初值 9；设 18 后格子变宽、列数由 5 降到 2，长名字（"大恩每日必应壁纸自动更换""ContextMenuManager.NET.4"）完整显示；设 5 后格子收窄、列数增多；值正确落盘 settings.json；恢复 9 后回到 5 列。设置页确认新滑条标题文字存在（共 9 个滑条）。
- **语言**：zh/en 各 **+1 键**（`Settings.Size.ItemCellChars`，694 键两边对齐 0 缺失）。

## 最新变更（2026-10-09 第五十九轮：默认格子宽度改 6 + 面板右键「刷新项目」）

- **需求1 默认格子宽度 9 → 6**：`AppSettings.DefaultItemCellTextChars = 6`（默认字号 14 下每格约 6 个中文，常见 1000px 窗口约 7 列，比原来的 5 列更紧凑）。**注意：这是"默认值"——已经用过滑条的老用户配置里已有 `ItemCellTextChars`，不会被改动；只有没这个键的（老版本升级上来的）才会拿到新默认 6。**
- **需求2 新增「刷新项目」菜单项**（用户原话：以前只有重启软件才会检测，需要手动刷新）：
  - 位置：**右键面板空白处**的弹出菜单里，紧跟在「添加项目」子菜单之后（加了一条分隔线）。
  - 作用范围：**指针所在的那个子分类**（与「添加项目」用同一套 `FindSubAtPanelPointer` 命中逻辑，即卡片/Tab 头 > 当前 Tab > 第一个子分类）。
  - 做两件事（`MainWindow.RefreshSubCategoryItemsAsync`）：
    1. **重新检测存在性**——`LauncherDataService.RecheckMissing(sub)`（新方法，逐项 `IsItemMissing` 并写回 `IsMissing`），纯 IO 放后台线程执行，返回 (总数, 失效数)；
    2. **清图标缓存并重建面板**——逐项 `ItemIconService.DeleteCache` + `ClearFailedFavicon`（网址类型必须清会话内失败记录，否则本次会话永远不会重新联网抓 favicon），然后 `RefreshPanelOnly()` 重建面板，图标按最新状态重新提取：正常的取真实图标，失效的显示「项目无法找到」。
  - 反馈：**只有存在失效项目时才弹提示**（「已刷新 N 个项目，其中 M 个无法找到（图标已标记）」），全部正常则静默（避免每次刷新都弹窗）；子分类为空时提示「这个子分类里还没有项目」。
- **实测（真实 UI，截图 + 对话框确证）**：
  - 造两个真实 exe 项目（`WillVanish.exe` / `Stays.exe`）启动 → 两个都是正常图标；
  - **启动后删掉 `WillVanish.exe`**（模拟程序被卸载）→ 右键空白处点「刷新项目」→ 该项目图标变成 **「项目无法找到」（⚠）**、另一个保持正常图标，并弹出「已刷新 2 个项目，其中 1 个无法找到（图标已标记）」；
  - **把文件放回去**再点「刷新项目」→ 图标**恢复成正常的记事本图标**（反向也成立）。
  - 空白处右键菜单确认同时含「添加项目」与「刷新项目」。
- **语言**：zh/en 各 **+3 键**（`Main.Item.RefreshItems` / `.Empty` / `.FoundMissing`，697 键两边对齐 0 缺失）。

### 语言 / 版本（第五十九轮）

- 版本号 1.6.2 → **1.6.3**；Debug/Release 均 0 错误 0 警告；Release 单文件 exe 已复制 `build\DaenLauncher.exe`。

### 语言 / 版本（第五十八轮）

- 版本号 1.6.1 → **1.6.2**；Debug/Release 均 0 错误 0 警告；Release 单文件 exe 已复制 `build\DaenLauncher.exe`（FileVersion 1.6.2.0）。
- README 更新"灵活布局"条目：补充等宽网格规则（列数只由窗口宽度决定、各子分类对齐、项目少不拉伸）+ 新滑条说明。

### 语言 / 版本（第五十七轮）

- 无新增语言键（仍 **693 键两边对齐**）。
- 版本号 1.6.0 → **1.6.1**；Debug/Release 均 0 错误 0 警告；Release 单文件 exe 已复制 `build\DaenLauncher.exe`（FileVersion 1.6.1.0）。

## 当前进度
- ✅ 需求1.md 主体 + 十八轮改进/修复全部完成；**"资源管理器菜单"需求已在第十九轮彻底移除（用户决定放弃）**。
- ✅ 第二十轮：待办功能完成（窗口 + 本地存储 + webnote 云同步 + 设置页）。
- ✅ 第二十一轮：云同步 BUG 修复 + 待办备注/新增弹窗/快捷日期/窗口设置 + 启动器"选中风格"标签。
- ✅ 第二十二轮：待办 UI 5 项优化（同步按钮移底部、蓝色添加按钮、重要淡色底、教程卡片条件显示、密码小眼睛）。
- ✅ 第二十三轮：教程卡片位置、云同步保存即时生效、"云同步"按钮成功反馈、标题/材质联动、深色模式底色修复。
- ✅ 第二十四轮：云/本地数据彻底分离（随时切换互不影响）+ 重要底色改为设置里自选。
- ✅ 第二十五轮：待办"上次位置"修复（位置记录 + 恢复）。
- ✅ 第二十六轮：附属功能栏可配置（勾选最多3个 + 排序 + "⋯"更多菜单）。
- ✅ 第二十八~三十轮：图标选择器（"不选择"公用+高亮、竖排标签、固定尺寸、自定义 emoji 输入）。
- ✅ 第三十一轮：随手记功能上线（窗口 + 多标签云同步 + 设置页 + 便签名查重 + 热键 Alt+3）。
- ✅ 第三十八轮：剪贴板功能上线（监听记录 + 窗口 + 再次复制 + 图片/文件支持 + 归档 + 设置页 + 热键 Alt+4）。
- ✅ 第四十轮：复制提示改"行高亮渐隐"（用户选定）+ 修改弹窗多行显示修复。
- ✅ 第四十一轮：剪贴板设置新增"永远置顶"（默认开，即时生效）。
- ✅ 第四十二轮：常用工具功能上线（三栏窗口 + 24 个子工具：SM4/AES 加解密、文本处理 12 个、JSON/XML/SQL 格式化、行行求和/curl/文件名提取/密码/UUID/时间戳/颜色选择器 + 设置页）。
- ✅ 第四十三轮：常用工具 9 项修补（**换行符根因修复**、加解密布局、导航蓝条、JSON/XML 转义语义、curl 布局与拖拽调大小、子目录遍历勾选、时间戳复制按钮、行行颠倒=行序反转）。
- ✅ 第四十四轮：常用工具快捷键 Alt+5 + 7 项修补（紧凑参数网格、替换查找换行、排序漏改、XML 压缩声明、遍历子目录汉化、时间戳格式联动、README 更新）。
- ✅ 第四十五轮：第四十四轮 4 个漏执行补丁补齐（替换/排序/XML/时间戳）+ 蓝条重叠修复 + 文件递归改 System.IO + 多行解密加固。
- ✅ 第四十六轮：多行解密根因修复（DoFinal 长度截断，UIA 真实 UI 复现实证）+ 文件递归 System.IO 化（6/23 实测）+ 发现部署副本真相 + 版本号 1.1.0。
- ✅ 第四十七轮：必应每日壁纸功能上线（窗口 + 官方/biturl 双接口 + 换壁纸 + 每日自动更换记录 + 保存到本地 + 设置页 + 热键 Alt+6）。
- ✅ 第四十八轮：壁纸更换成功弹系统 Toast（大图+日期+描述）+ "模式"设置（六种显示方式）+ cache 目录自动清理策略。
- ✅ 第四十九轮：数据导出/导入/删除支持必应壁纸数据（含删除/导入后的记录重载与窗口刷新）+ README 数据条目同步。
- ✅ 第五十轮：项目 Tooltip 显示路径/备注/参数 + 右键"创建桌面快捷方式"（三类快捷方式全支持含 UWP）+ 默认尺寸 32/14/12 + 面板右键"添加项目"（7 种类型，UWP 可列出商店应用）。
- ✅ 第五十一轮：UWP/协议项目显示真实图标（IShellItemImageFactory 拿商店应用 256px 图标；AssocQueryString 查协议处理程序图标；GetDIBits 保留透明度）。
- ✅ 第五十二轮：网址类型联网取 favicon（favicon.ico → HTML link 解析，系统代理自动生效，失败短路 + 会话内不重试）；UWP 选择器列表逐行显示应用图标（AUMID 磁盘缓存 + INPC 刷新）。
- ✅ 第五十三轮：项目右键"刷新图标"（清缓存重新提取全类型支持；网址类型同时清会话内失败记录允许立即重联网）。
- ✅ 第五十四轮：修复 .url 拖入误判"网址"（steam:// 现在正确判为协议）+ 记录 .url 的 IconFile 用游戏专属图标 + 旧数据自动迁移（非 http/https 的网址条目 → 协议）。
- ✅ 第五十五轮：设置-常规新增"代理"配置（四种模式默认系统代理，HTTP/SOCKS5 测试通过才能保存）；新 ProxyService 统一全局代理（FaviconService/WebNoteClient/WallpaperService 已接入，配置变化立即生效）。
- ✅ 第五十六轮：**3 个 BUG 修复**（多显示器"上次位置"负数坐标/钳制错屏 → 新 WindowPositionHelper 六窗口共用；项目文字被 57.6px 死上限截断 → 去上限 + UniformWrapPanel 加测量约束与最少 2 列；拖入 .bat/.txt 快捷方式存成 .lnk 路径 → 非 exe 文件目标识别为 File/文件夹识别为 Folder + "添加项目-文件"也解析快捷方式 + 存量数据自动迁移）；**3 个新工具**（已连接 WiFi 密码查看 / 北京时间同步 / 二维码生成）。版本 1.6.0。
- ✅ 第五十七轮：**修正平铺布局模型**（格子宽度改为固定常量 `AppSettings.ComputeItemCellWidth()`，列数只由"窗口宽度 ÷ 格子宽度"决定；末行不再拉伸铺满；所有子分类列数一致且对齐；项目少不再被平分）。版本 1.6.1。
- ✅ 第五十八轮：平铺格子宽度加设置页滑条（`ItemCellTextChars` 改为可配置属性，范围 1~30 默认 9，随时拖动即时生效 + 恢复默认）。版本 1.6.2。
- ✅ 第五十九轮：默认格子宽度 9→6（已有配置不受影响）+ 面板右键「刷新项目」（重新检测存在性 + 重提图标，失效换「项目无法找到」、恢复则换回正常图标，仅失效时提示）。版本 1.6.3。
- ⚠️ 待用户实测：第十二轮（覆盖 70% 触发重排 + 滑动动画）、**第二十~三十一轮（待办/随手记全部交互、云同步、附属功能栏配置、图标选择器）**、**第三十八轮（剪贴板全部交互）**。
- 📌 回滚点：commit 991b8b1（第十一轮拖拽可用版本）。第十二轮起改动尚未提交，确认手感后再提交新检查点。

## 待办事项
- 检查版本更新/自动更新（关于页占位）：实现时用 `ProxyService.CreateHttpClient()` 发请求即可自动遵循代理配置（第五十五轮占位记录）。
- 用户实测后修 bug（重点：第四十二轮常用工具的全部子工具交互、SM4/AES 各模式参数组合、屏幕取色、颜色盘拖动、文件拖拽；第四十七轮壁纸的接口连通性/换壁纸/每日自动更换）。
- 剪贴板云同步（如需要，下轮需求；当前剪贴板仅本地存储）。
- "左键双击桌面"、"双击任务栏"触发（预留复选框，需窗口层级判断）。
- 关于页"应用更新"检查/自动更新。

## 关键决策
- **窗口显示位置计算统一走 `WindowPositionHelper`（第五十六轮起长期约束）**：任何窗口的"显示位置"都必须调用它，**禁止再各窗口复制粘贴一段 `DisplayArea.GetFromWindowId` 的定位代码**（该模式曾复制到 6 个窗口并携带两个多显示器 BUG）。要点：判断"上次位置是否记录过"用 `AppSettings.WindowPositionNotSet`（-1）而非 `>= 0`（副屏坐标可以是负数）；钳制用**目标坐标所在显示器**的工作区（`DisplayArea.GetFromPoint`）；尺寸换算用 `Win32Helper.GetDpiScaleForPoint` 取目标屏 DPI。
- **等宽网格的格子宽度必须是"外生常量"，不许由内容决定（第五十七轮，长期约束）**：平铺模式的格子宽度只能来自设置（`AppSettings.ComputeItemCellWidth()`，由项目图标大小/文字大小/横向间距算出），**一行几列 = 窗口宽度 ÷ 格子宽度**，与项目数量、文字长短完全无关；末行不满**不许拉伸铺满**。任何"让格子去适应文字内容"的做法都会让列数/列位置随数据漂移，破坏对齐（第五十六轮的错误方向、第五十七轮修正，务必不要再犯）。测量子元素时必须给宽度约束（= 格子宽 − 外边距），否则文字不换行、长名字会把格子撑爆。
- **快捷方式解析的唯一入口是 `DropResolver.ResolveShortcutFile`（第五十六轮）**：拖拽、右键"添加项目-快捷方式"、"添加项目-文件"（选中 .lnk/.url 时）都必须走它，不允许直接把 `.lnk` 路径存进项目。**任何"目标类型判定"的新分支都要放在 `.exe` 判定之后、兜底分支之前**——兜底分支返回的是快捷方式自身（Type=Lnk），漏判就会退化成"指向 .lnk"。
- **新窗口必须接入三项联动（第二十三轮起长期约束）**：① `RefreshTitle()` 并加入 `App.RefreshAllTitles()`（自定义软件标题）；② 窗口材质通过 `App.ApplyBackdropEverywhere()` 统一应用；③ 主题通过 `App.ApplyThemeEverywhere()`。已接入：主窗口、设置窗口、待办窗口。
- **不内置 WrapPanel**：2.3.9 的 WinUI 没有 → 自己写了 `Controls/WrapPanel.cs`。
- **托盘菜单手动弹出**：`MenuActivation=None` + `RightClickCommand` 里 `ShowContextMenu(光标位置)`，避免位置不准。
- **热键注册在主窗口 HWND**：主窗口懒创建前热键不可用，可接受（静默启动时托盘/钩子可用）。
- **拖拽 UWP 是 best-effort**：无本地路径的虚拟项按 Uwp 类型存名字。
- **语言文件内嵌名**：嵌入资源用 `zh-CN.json/en-US.json`（避免中文资源名问题），释放到磁盘时改名为"简体中文.json/English.json"。
- **窗口关闭=隐藏**：AppWindow.Closing 里 Cancel+Hide，`App.IsExiting` 才真正退出。

## 踩过的坑
1. **单文件 + BaseDirectory**：PublishSingleFile 下 `AppContext.BaseDirectory` = `%Temp%\.net\...` 解压目录 → data 会建到临时目录。必须用 `Environment.ProcessPath`。
2. **SelectionChanged 中改集合**：WinUI 对 ObservableCollection 的事件重入校验会抛 COMException 0x8000FFFF（"collection modification in progress"），且异常可能被 XAML 吞成灾难性故障。排查手段：App ctor 里挂 `UnhandledException` 写 `data\crash.log`。
3. **H.NotifyIcon 2.5.0-beta.3 API**：没有 `TrayLeftDoubleClick` 等 CLR 事件（那是 RoutedEvent），要用 `DoubleClickCommand/RightClickCommand/LeftClickCommand`（ICommand，配 RelayCommand）。
4. **枚举名差异**：这版 SDK 里是 `TitleBarHeightOption`（不是 TitleBarHeightMode）、`DisplayAreaFallback.Nearest`（不是 GetAncestorOption）；`AppWindow` 等类型在 InteractiveExperiences.Projection.dll。
5. **Win32 GetWindowLongPtr**：必须指定 `EntryPoint="GetWindowLongPtrW"`，否则 P/Invoke 找不到入口。
6. **XAML 编译器 WMC9999**（"未将对象引用设置到对象的实例"）通常是 C# 代码先编译失败导致的连锁错误，先修 C# 错误。
7. **System.Drawing**：WinUI 项目默认不带，需要 `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />`（用于图标提取）。
8. **调试技巧**：打包后启动即闪退时查事件查看器 Application 日志（Application Error 1000 / WER 1001），能拿到故障模块和异常代码（0xc000027b = XAML stowed exception）。
9. **`Application.Current.Resources[key]` 取画笔不随主题切换**（返回默认主题变体）→ 动态创建的元素要跟随深浅色，必须用带 `{ThemeResource}` Setter 的 Style。
10. **ContentDialog 全局只允许一个**：弹窗上再开弹窗直接 COMException 崩溃，嵌套弹窗要先 Hide 再开。
11. **ContainerContentChanging 里改 `ItemContainer.Content` 无效**（显示的还是 item.ToString()）→ 用 ItemTemplate + 遍历可视化树找模板元素设置属性。
12. **WinUI 拖放命中**：Grid/Panel 默认 Background=null 不可命中，拖放目标区要 `Background=Transparent`；且容器实际命中区域是布局后尺寸（WrapPanel 只有内容大小），整块区域接收拖放要挂在外层容器上。
13. **CommunityToolkit.WinUI.UI.Controls 是空 meta 包**，要装 `CommunityToolkit.WinUI.UI.Controls.Primitives`（7.1.2，WrapPanel 在里面）；其 WrapPanel 无间距属性。
14. **UIA 测试注意**：ListViewItem 的 AutomationName 是 item.ToString()（如类型名），可见文本是其 Text 子元素——按名字找控件要找 Text 层；只读 TextBox（快捷键框）会干扰 ValuePattern 查找，用 `IsReadOnly` 过滤。
15. **WinUI 3 的 `DragStartingEventArgs` 没有 `AllowedOperation` 属性**（UWP 才有），设置允许操作要写 `args.Data.RequestedOperation`；对应地 DragOver 里读 `e.AcceptedOperation`、收尾事件是 `UIElement.DropCompleted`（DropResult 判断是否真的落下了）。
16. **Button 上 CanDrag 不自动发起拖拽**：ButtonBase（Button/TextBox 等）吞掉指针输入，系统"按住拖动"手势检测不触发（微软 Q&A 确认的设计限制）→ 参考 DeskBox：CanDrag=true + 指针事件手动检测阈值（AddHandler handledEventsToo:true 收 PointerPressed/Moved）+ `await button.StartDragAsync(e.GetCurrentPoint(button))` 主动发起；StartDragAsync 的 await 到拖拽会话结束才返回，之后走 DragStarting/DragOver/Drop/DropCompleted 原生流程；发起后要短时间屏蔽 Click 防误启动。
17. **原生拖放识别"自己拖的"**：DragStarting 往 `args.Data.Properties[key]` 写标记，DragOver/Drop 里从 `e.DataView.Properties.TryGetValue(key, ...)` 读回（同应用拖放标记一直在，DeskBox 同款做法）；内部拖动的 DragOver 若不处理要保持"未处理"状态，事件会冒泡到外层 AllowDrop 容器（PanelHost），外部文件拖放因此不受影响。
18. **CS0136 同名局部变量**：方法体块里先在嵌套 if 块中声明 `x`、后面又在方法级声明 `x` 会报 CS0136（C# 的块作用域是整个块，不管声明先后顺序）——嵌套块里的换名即可。
19. **图标缓存有两层**：换 logo 后"任务栏还是旧图标"要先分清——① 应用自己的 `data\icon\cache\app.ico`（EnsureAppIconExtracted 现按字节比对自动刷新）；② Windows 资源管理器的图标缓存（按 exe 路径缓存，重启 explorer / 删 `%LocalAppData%\IconCache.db` 和 `%LocalAppData%\Microsoft\Windows\Explorer\iconcache_*.db` / 取消重新固定任务栏才能刷新）。验证技巧：把 exe 复制改名再跑，若任务栏显示新图标即实锤是系统缓存。

20. **WinUI TextBox 换行符是 CR**：按行处理文本必须归一化 CRLF/CR/LF（ToolKit.SplitLines），输出统一拼 CRLF（ToolKit.JoinLines；SetOutput 自动归一化）。
21. **ComImport 接口必须声明完整 vtable**（第五十轮实锤）：COM 接口互操作只声明"用得到的方法"会把调用路由到错误的函数指针上——IShellFolder 漏了中间的 BindToStorage/CompareIDs/CreateViewObject 后，GetDisplayNameOf 直接 0xC0000005 原生崩溃（文件系统项也一样崩，可据此与"环境问题"区分）。排查手段：把可疑互操作拷进独立控制台程序 + 对照实验（桌面文件夹解析 notepad.exe 后调 GetDisplayNameOf）。
22. **FOLDERID_AppsFolder 可能 E_FAIL**：SHGetKnownFolderPath({1e87508d-…}) 在受限上下文返回 0x80004005，而 **"shell:AppsFolder" 可以被 SHParseDisplayName 直接解析**——取 AppsFolder 入口要多级回退（known folder → shell:AppsFolder → ::{GUID}）。识别 UWP 应用的规则：AppsFolder 子项解析名含 "!"（AUMID = 包家族名!应用ID），桌面程序没有。
23. **x64 ABI：≤8 字节的结构体参数按"单个寄存器"整体传值**（第五十一轮实锤）：`IShellItemImageFactory.GetImage(SIZE size, ...)` 的 SIZE 拆成两个 int 声明会占两个寄存器、参数错位，直接 0xC0000005——必须原样声明结构体参数。同族坑：第 21 条的 vtable 漏方法。
24. **GetImage 的 HBITMAP 直接 FromHbitmap 丢 alpha**：图标会变黑底方块；必须 GetDIBits 拷 32bpp 数据自建 Bitmap，且要处理"全图 alpha=0"的退化情况（统一置 255，否则图标整体透明看不见）。
25. **PowerShell 的 `$true`/`$TRUE` 是内建常量，不能自建同名变量**（第五十六轮实锤）：写 UIA 测试脚本时 `$TRUE = [Condition]::TrueCondition` 会让 `FindAll` 抛 `MethodArgumentConversionInvalidCastArgument`（变量名大小写不敏感），改用 `$TRUE_COND` 之类的名字。
26. **控件宽度上限写死会截断文字**（第五十六轮实锤）：项目文字曾固定 `MaxWidth = 图标大小*1.8`（默认 57.6px），同一行里格子再宽文字也在这 57.6px 上省略——"启动redis"完整、"启动oracle"只剩"启"。文字的宽度约束应由**布局格子**决定，不要按图标尺寸推算。
27. **虚拟桌面坐标是带负数的**（第五十六轮实锤）：副屏在主屏左侧/上方时窗口 X/Y 为负数，"是否记录过位置"必须用哨兵值（-1）判断，写 `>= 0` 会把合法坐标当成未记录；"防超出屏幕"的钳制必须用**目标坐标所在显示器**的工作区，用窗口当前所在屏会把副屏坐标拉回主屏。
28. **`ProcessStartInfo` 的 `Verb = "runas"` 与输出重定向互斥**（第五十六轮）：提权启动必须 `UseShellExecute = true`，此时不能再设 `RedirectStandardOutput/Error`（运行时报错），成功与否只能靠"事后重读状态校验"。
29. **等宽网格：格子宽度不能自适应内容**（第五十六轮踩、第五十七轮修正）：① `child.Measure(new Size(inf, inf))` 会让文字不换行，一个超长名字把所有格子撑成整行宽、面板退化成单列——测量宽度必须给约束（格子宽 − 外边距）；② 更根本的错：把格子宽度做成"随内容变化"后，列数/列位置会随"项目数量、文字长短"漂移（用户实测同一分类下两个子分类一个 2 列一个 3 列、项目少的被拉伸平分）。正确模型是**格子宽度 = 由设置算出的固定值，列数 = 窗口宽度 ÷ 格子宽度，末行不拉伸**；测量与排列两阶段必须用同一个列宽/列数。
30. **netsh 输出编码跟随系统区域设置**（第五十六轮实锤）：中文 Windows 是 GBK 而非 UTF-8，读原始字节后先按 `UTF8Encoding(false, true)` 严格解码、失败回退 `CodePagesEncodingProvider` + `CurrentCulture.TextInfo.ANSICodePage`；`H:` 隐藏网络判定必须匹配"未广播"/"not broadcasting"（普通网络那行也含"广播"二字）。
31. **给语言 JSON 插/删键必须复验**（第五十六轮）：插键后 `json.load` + 键集合对比 + **反向扫描"代码引用的键是否都有定义"**；本轮据此发现多加了一个没人引用的键。删键时注意别把锚点行的键值拆开（第四十九轮踩过）。
32. **UIA 测试的中文/emoji 结果要写 UTF-8 文件再读**（第五十六轮）：控制台编码会丢字符（emoji 显示成 `??`），直接看输出会误判"乱码了"——实际数据没问题。另外用 heredoc 写含反斜杠的 C# 代码会被 shell 吞掉转义，改用 Write 工具或 python 写文件。

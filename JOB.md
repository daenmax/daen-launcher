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
             Win32Helper、ShellContextMenuHelper、DropResolver、DataService、
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
- **ShellContextMenuHelper**：IShellFolder+IContextMenu3 COM 弹出系统右键菜单（含 owner-draw 消息转发）。
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

## 当前进度
- ✅ 需求1.md 主体 + 十一轮改进/修复全部完成。
- ⚠️ 待用户实测：第十一轮改动（原生拖拽动画、跨子分类拖动、外部拖入回归）。

## 待办事项
- 用户实测后修 bug。
- 待办/随手记/剪贴板功能（下轮需求）。
- "左键双击桌面"、"双击任务栏"触发（预留复选框，需窗口层级判断）。
- 关于页"应用更新"检查/自动更新。

## 关键决策
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

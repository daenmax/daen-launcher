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
             Win32Helper、DropResolver、DataService、
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

## 关键决策（待办云同步）与已知限制
- **便签密码明文存 settings.json**（本机文件，与桌面便签场景风险可接受；如需加密另做）。
- **推送失败的处理**：写操作先改内存再推云端（云端模式不落本地）；若推送失败，云端数据未更新，**下次窗口打开会重新从云端拉取**，未推送的修改会丢（云为唯一事实源的模型，需求如此设计）。
- **云/本地是两套独立数据**（第二十四轮）：云同步模式不写本地文件，本地模式不碰云端，随时切换互不影响。
- **token 冲突不合并内容**：需求只要求"发现不一致就用最新 token 保存"（以本机数据覆盖），未做内容级合并。
- CalendarDatePicker.FirstDayOfWeek 的类型是 `Windows.Globalization.DayOfWeek`（不是 Microsoft.UI.Xaml 的）。

## 当前进度
- ✅ 需求1.md 主体 + 十八轮改进/修复全部完成；**"资源管理器菜单"需求已在第十九轮彻底移除（用户决定放弃）**。
- ✅ 第二十轮：待办功能完成（窗口 + 本地存储 + webnote 云同步 + 设置页）。
- ✅ 第二十一轮：云同步 BUG 修复 + 待办备注/新增弹窗/快捷日期/窗口设置 + 启动器"选中风格"标签。
- ✅ 第二十二轮：待办 UI 5 项优化（同步按钮移底部、蓝色添加按钮、重要淡色底、教程卡片条件显示、密码小眼睛）。
- ✅ 第二十三轮：教程卡片位置、云同步保存即时生效、"云同步"按钮成功反馈、标题/材质联动、深色模式底色修复。
- ✅ 第二十四轮：云/本地数据彻底分离（随时切换互不影响）+ 重要底色改为设置里自选。
- ✅ 第二十五轮：待办"上次位置"修复（位置记录 + 恢复）。
- ✅ 第二十六轮：附属功能栏可配置（勾选最多3个 + 排序 + "⋯"更多菜单）。
- ⚠️ 待用户实测：第十二轮（覆盖 70% 触发重排 + 滑动动画）、**第二十~二十六轮（待办窗口全部交互、云同步、待办快捷键 Alt+2、启动器选中风格标签、附属功能栏配置）**。
- 📌 回滚点：commit 991b8b1（第十一轮拖拽可用版本）。第十二轮起改动尚未提交，确认手感后再提交新检查点。

## 待办事项
- 用户实测后修 bug。
- 随手记/剪贴板功能（下轮需求）。
- "左键双击桌面"、"双击任务栏"触发（预留复选框，需窗口层级判断）。
- 关于页"应用更新"检查/自动更新。

## 关键决策
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

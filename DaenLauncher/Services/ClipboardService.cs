using System.Security.Cryptography;
using System.Text.Json;
using DaenLauncher.Models;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DaenLauncher.Services;

/// <summary>
/// 剪贴板服务：监听系统剪贴板、保存复制记录（文本/图片/文件）、支持"再次复制"。
/// - 监听方式：DispatcherTimer 轮询 GetClipboardSequenceNumber（比 ContentChanged 事件
///   更可靠——事件在窗口焦点切换时才可能触发，轮询无此问题）；
/// - 存储：data\clipboard\clipboard.json（复用 JsonStore），图片 PNG 落盘 data\clipboard\images\；
/// - 去重：与最新一条内容相同（文本/图片像素/文件路径一致）时不重复记录；
/// - 再次复制由本应用主动 SetContent，通过抑制标记避免把"自己复制的"再记录一遍。
/// </summary>
public sealed class ClipboardService
{
    /// <summary>全局单例</summary>
    public static ClipboardService Instance { get; } = new();

    /// <summary>存储的文本长度上限（超过则不记录，避免 JSON 无限膨胀）</summary>
    public const int MaxStoredTextLength = 1_000_000;

    /// <summary>轮询间隔（毫秒）</summary>
    private const int PollIntervalMs = 800;

    private readonly JsonStore<ClipboardData> _store =
        new(Path.Combine(DataPathService.ClipboardDir, "clipboard.json"));

    private ClipboardData _data = new();

    /// <summary>当前数据（记录 + 归档）</summary>
    public ClipboardData Data => _data;

    /// <summary>数据变化（新增记录/删除/归档/清除）后触发，剪贴板窗口订阅刷新列表</summary>
    public event Action? DataChanged;

    /// <summary>轮询计时器（UI 线程创建）</summary>
    private DispatcherTimer? _pollTimer;

    /// <summary>上次看到的剪贴板序列号</summary>
    private uint _lastSequence;

    /// <summary>防止捕获重入</summary>
    private bool _capturing;

    /// <summary>抑制标记：本应用主动写剪贴板（"再次复制"）后跳过下一次捕获</summary>
    private bool _suppressNextCapture;

    private ClipboardService()
    {
    }

    /// <summary>图片保存目录：data\clipboard\images\</summary>
    public static string ImagesDir => Path.Combine(DataPathService.ClipboardDir, "images");

    /// <summary>
    /// 应用启动时调用：从磁盘加载本地数据，并按设置启动/停止监听。
    /// </summary>
    public void Initialize()
    {
        _store.Reload();
        _data = _store.Load();
        ApplyEnabledSetting();
    }

    /// <summary>按设置启动/停止剪贴板监听（设置页开关变化后也调用这个）</summary>
    public void ApplyEnabledSetting()
    {
        var enabled = SettingsService.Instance.Settings.ClipboardEnabled;
        if (enabled && _pollTimer == null)
        {
            // DispatcherTimer 必须在 UI 线程创建（App 启动流程和设置页都在 UI 线程）
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
            _pollTimer.Tick += (_, _) => _ = PollTickAsync();
            _lastSequence = Win32Helper.GetClipboardSequenceNumber();
            _pollTimer.Start();
        }
        else if (!enabled && _pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer = null;
        }
    }

    /// <summary>轮询：序列号变了说明剪贴板内容变化，触发捕获</summary>
    private async Task PollTickAsync()
    {
        var seq = Win32Helper.GetClipboardSequenceNumber();
        if (seq == _lastSequence || _capturing)
        {
            return;
        }
        _lastSequence = seq;

        if (_suppressNextCapture)
        {
            // 是我们自己"再次复制"写进去的，不记录
            _suppressNextCapture = false;
            return;
        }

        _capturing = true;
        try
        {
            await CaptureAsync();
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>捕获当前剪贴板内容：文本 > 图片 > 文件（同一内容通常带多种格式，按优先级取一种）</summary>
    private async Task CaptureAsync()
    {
        DataPackageView? content = null;
        try
        {
            content = Clipboard.GetContent();
        }
        catch
        {
            return; // 剪贴板被其他进程占用等，跳过本次
        }

        try
        {
            // ===== 1. 文本（最常见的场景） =====
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                AddTextEntry(text);
                return;
            }

            // ===== 2. 图片 =====
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                await CaptureImageAsync(content);
                return;
            }

            // ===== 3. 文件/文件夹 =====
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                await CaptureFilesAsync(content);
                return;
            }
        }
        catch (Exception ex)
        {
            // 捕获失败不影响运行（格式半途失效、被占用等），记日志方便排查
            try
            {
                File.AppendAllText(Path.Combine(DataPathService.DataRoot, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Clipboard capture: {ex.Message}\n");
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>记录一条文本（去重：与最新一条相同则跳过）</summary>
    private void AddTextEntry(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxStoredTextLength)
        {
            return; // 空文本 / 超大文本不记录
        }
        var top = _data.Items.FirstOrDefault();
        if (top != null && top.Kind == ClipboardEntryKind.Text && top.Text == text)
        {
            return; // 与最新记录相同，不重复
        }
        InsertAndTrim(new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = text });
    }

    /// <summary>捕获图片：解码 → 存 PNG → 记录（同一张图不重复记录）</summary>
    private async Task CaptureImageAsync(DataPackageView content)
    {
        var reference = await content.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

        var pixelProvider = await decoder.GetPixelDataAsync();
        var pixels = pixelProvider.DetachPixelData();

        // 像素数据的 SHA1 作为图片指纹：同一张图连续复制不重复记录
        var hash = Convert.ToHexString(SHA1.HashData(pixels));
        var top = _data.Items.FirstOrDefault();
        if (top != null && top.Kind == ClipboardEntryKind.Image && top.ImageHash == hash)
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(ImagesDir);
        var fullPath = Path.Combine(ImagesDir, id + ".png");

        // 用像素数据重新编码为 PNG（不依赖剪贴板里源格式的编码方式）
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ImagesDir);
        var file = await folder.CreateFileAsync(id + ".png",
            Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using (var ras = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }

        InsertAndTrim(new ClipboardEntry
        {
            Kind = ClipboardEntryKind.Image,
            ImagePath = "images\\" + id + ".png",
            ImageWidth = (int)decoder.OrientedPixelWidth,
            ImageHeight = (int)decoder.OrientedPixelHeight,
            ImageHash = hash
        });
    }

    /// <summary>捕获文件/文件夹列表（虚拟项没有本地路径，跳过）</summary>
    private async Task CaptureFilesAsync(DataPackageView content)
    {
        var items = await content.GetStorageItemsAsync();
        var paths = items
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var joined = string.Join("\n", paths);
        var top = _data.Items.FirstOrDefault();
        if (top != null && top.Kind == ClipboardEntryKind.Files &&
            string.Join("\n", top.FilePaths) == joined)
        {
            return; // 与最新记录相同的文件列表，不重复
        }

        InsertAndTrim(new ClipboardEntry
        {
            Kind = ClipboardEntryKind.Files,
            FilePaths = paths
        });
    }

    /// <summary>新记录插到最前（最新的在最上），并按"最大保存数量"裁剪最早的（仅记录，不动归档）</summary>
    private void InsertAndTrim(ClipboardEntry entry)
    {
        _data.Items.Insert(0, entry);
        TrimToMax();
        Persist();
        DataChanged?.Invoke();
    }

    /// <summary>按设置的最大记录数裁剪（从最旧的一端删，删掉的图片文件一并删除）</summary>
    public void TrimToMax()
    {
        var max = Math.Max(10, SettingsService.Instance.Settings.ClipboardMaxRecords);
        var removedAny = false;
        while (_data.Items.Count > max)
        {
            var removed = _data.Items[^1];
            _data.Items.RemoveAt(_data.Items.Count - 1);
            DeleteEntryFiles(removed);
            removedAny = true;
        }
        if (removedAny)
        {
            Persist();
            DataChanged?.Invoke();
        }
    }

    /// <summary>落盘（本地模式，剪贴板不做云同步）</summary>
    public void Persist()
    {
        _store.Save(_data);
    }

    /// <summary>从磁盘重新加载（数据删除功能删掉 clipboard 目录后调用）</summary>
    public void LoadLocal()
    {
        _store.Reload();
        _data = _store.Load();
        DataChanged?.Invoke();
    }

    #region 写操作（窗口调用）

    /// <summary>归档：从记录移到归档（持久化存储，需求）</summary>
    public void Archive(ClipboardEntry entry)
    {
        if (_data.Items.Remove(entry))
        {
            _data.Archives.Insert(0, entry);
            Persist();
            DataChanged?.Invoke();
        }
    }

    /// <summary>取消归档：从归档移回记录（并触发数量裁剪）</summary>
    public void Unarchive(ClipboardEntry entry)
    {
        if (_data.Archives.Remove(entry))
        {
            _data.Items.Insert(0, entry);
            TrimToMax();
            Persist();
            DataChanged?.Invoke();
        }
    }

    /// <summary>删除一条（记录或归档），图片条目同步删除 PNG 文件</summary>
    public void Delete(ClipboardEntry entry)
    {
        if (!_data.Items.Remove(entry))
        {
            _data.Archives.Remove(entry);
        }
        DeleteEntryFiles(entry);
        Persist();
        DataChanged?.Invoke();
    }

    /// <summary>清除全部记录（不动归档，需求）</summary>
    public void ClearRecords()
    {
        foreach (var entry in _data.Items)
        {
            DeleteEntryFiles(entry);
        }
        _data.Items.Clear();
        Persist();
        DataChanged?.Invoke();
    }

    /// <summary>清除全部归档</summary>
    public void ClearArchives()
    {
        foreach (var entry in _data.Archives)
        {
            DeleteEntryFiles(entry);
        }
        _data.Archives.Clear();
        Persist();
        DataChanged?.Invoke();
    }

    /// <summary>删除条目对应的图片文件（文本/文件条目无文件）</summary>
    private static void DeleteEntryFiles(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Image || string.IsNullOrEmpty(entry.ImagePath))
        {
            return;
        }
        try
        {
            // ImagePath 是相对 data\clipboard\ 的相对路径（防路径注入，只允许在 images 目录内）
            var full = Path.GetFullPath(Path.Combine(DataPathService.ClipboardDir, entry.ImagePath));
            if (full.StartsWith(Path.GetFullPath(ImagesDir), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch { /* 删除失败不影响主流程 */ }
    }

    /// <summary>修改文本条目内容（编辑弹窗保存后调用）</summary>
    public void UpdateText(ClipboardEntry entry, string newText)
    {
        entry.Text = newText;
        Persist();
        DataChanged?.Invoke();
    }

    #endregion

    #region 再次复制（需求：复制到剪贴板方便在其他地方粘贴）

    /// <summary>
    /// 把条目重新复制到系统剪贴板（窗口点击行 / 右键"复制"时调用）。
    /// 写剪贴板前设置抑制标记，避免把自己的复制再记录一遍。
    /// </summary>
    public async Task<bool> CopyToClipboardAsync(ClipboardEntry entry)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text:
                    package.SetText(entry.Text);
                    break;

                case ClipboardEntryKind.Image:
                {
                    var full = Path.Combine(DataPathService.ClipboardDir, entry.ImagePath);
                    if (!File.Exists(full))
                    {
                        return false;
                    }
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(full);
                    package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                    break;
                }

                case ClipboardEntryKind.Files:
                {
                    var items = new List<Windows.Storage.IStorageItem>();
                    foreach (var path in entry.FilePaths)
                    {
                        if (Directory.Exists(path))
                        {
                            items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path));
                        }
                        else if (File.Exists(path))
                        {
                            items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                        }
                        // 已不存在的路径跳过（文件可能被移动/删除）
                    }
                    if (items.Count == 0)
                    {
                        return false;
                    }
                    package.SetStorageItems(items);
                    break;
                }
            }

            _suppressNextCapture = true;
            Clipboard.SetContent(package);
            try
            {
                // Flush 让系统接管内容：即使本应用退出，剪贴板里仍然可以粘贴
                Clipboard.Flush();
            }
            catch { /* Flush 失败不影响当前会话粘贴 */ }
            return true;
        }
        catch
        {
            _suppressNextCapture = false;
            return false;
        }
    }

    #endregion
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaenLauncher.Services;

/// <summary>
/// 通用 JSON 文件存储（需求-数据存储第2条）：
/// 同一个 json 文件同一时间只允许一个线程访问，其他线程排队（SemaphoreSlim 每文件一个）。
/// 每次修改立即保存（需求-数据存储第3条）；保存使用"写临时文件 + 替换"防止写坏。
/// </summary>
public sealed class JsonStore<T> where T : new()
{
    /// <summary>每个文件路径一把锁，保证串行访问</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new();

    private static SemaphoreSlim LockFor(string path) =>
        FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _filePath;
    private readonly object _cacheLock = new();
    private T? _cache;

    public JsonStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>文件完整路径</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 加载文件（带内存缓存，首次从磁盘读）。文件不存在或损坏时返回默认值。
    /// </summary>
    public T Load()
    {
        lock (_cacheLock)
        {
            if (_cache != null) return _cache;
        }

        var semaphore = LockFor(_filePath);
        semaphore.Wait();
        try
        {
            T result;
            if (!File.Exists(_filePath) && File.Exists(_filePath + ".bak"))
            {
                // 主文件丢失（如进程被强杀时撞上保存瞬间）但备份还在：从备份恢复
                try
                {
                    File.Copy(_filePath + ".bak", _filePath);
                }
                catch { /* 恢复失败按缺失处理 */ }
            }
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    result = JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? new T();
                }
                catch
                {
                    // 文件损坏：备份后使用默认值，避免反复崩溃
                    TryBackupCorrupted();
                    result = new T();
                }
            }
            else
            {
                result = new T();
            }

            lock (_cacheLock)
            {
                _cache = result;
            }
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// 立即保存到磁盘（需求-数据存储第3条：每次修改立即保存）。
    /// 连续保存（如拖动窗口大小时）可能遇到文件被占用，带重试。
    /// </summary>
    public void Save(T data)
    {
        lock (_cacheLock)
        {
            _cache = data;
        }

        var semaphore = LockFor(_filePath);
        semaphore.Wait();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(data, SerializerOptions);

            // 写临时文件再替换，避免写一半崩溃导致文件损坏。
            // 快速连续保存时 File.Replace 可能抛 IOException（被占用/被杀毒扫描），重试几次
            for (var attempt = 1; ; attempt++)
            {
                var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(_filePath))
                    {
                        // 保留一份 .bak 备份
                        var bakPath = _filePath + ".bak";
                        File.Replace(tempPath, _filePath, bakPath);
                    }
                    else
                    {
                        File.Move(tempPath, _filePath);
                    }
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    try { File.Delete(tempPath); } catch { /* 忽略 */ }
                    Thread.Sleep(40 * attempt);
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>损坏文件备份成 .corrupt</summary>
    private void TryBackupCorrupted()
    {
        try
        {
            var corruptPath = _filePath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_filePath, corruptPath, true);
        }
        catch
        {
            // 备份失败不影响主流程
        }
    }

    /// <summary>重新从磁盘加载（导入数据后调用）</summary>
    public void Reload()
    {
        lock (_cacheLock)
        {
            _cache = default;
        }
    }
}

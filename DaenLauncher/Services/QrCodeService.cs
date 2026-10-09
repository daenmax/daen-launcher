using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace DaenLauncher.Services;

/// <summary>
/// 二维码生成服务（WiFi 密码查看 / 二维码生成工具共用）。
///
/// 统一以"PNG 字节"为唯一产物（QRCoder 的 PngByteQRCode 直接产出，自带静区、
/// 由库保证编码正确性）：界面显示用它构造 BitmapImage，保存文件直接写这些字节——
/// 不依赖 System.Drawing 渲染器，也不需要落临时图片文件。
/// </summary>
public static class QrCodeService
{
    /// <summary>二维码目标边长（像素，正方形）。实际边长取模块数的整数倍，保证边缘锐利。</summary>
    public const int DefaultSize = 320;

    /// <summary>每个模块最少占的像素数（小尺寸下也不糊）</summary>
    private const int MinModulePixels = 4;

    /// <summary>纠错级别（对外枚举，避免把 QRCoder 的类型泄漏到界面层）</summary>
    public enum ErrorLevel
    {
        /// <summary>L：约 7% 容错（容量最大）</summary>
        Low,

        /// <summary>M：约 15% 容错（默认，日常够用）</summary>
        Medium,

        /// <summary>Q：约 25% 容错</summary>
        Quartile,

        /// <summary>H：约 30% 容错（脏污/遮挡下最稳，容量最小）</summary>
        High
    }

    /// <summary>
    /// 生成二维码 PNG 字节。内容为空或超出二维码容量（约 2900 字节）时返回 null。
    /// </summary>
    public static byte[]? CreatePngBytes(string content, int sizePixels = DefaultSize,
        ErrorLevel level = ErrorLevel.Medium)
    {
        if (string.IsNullOrEmpty(content)) return null;
        if (sizePixels <= 0) sizePixels = DefaultSize;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, ToEccLevel(level));

            // 模块数 + 静区（QRCoder 画 4 模块静区，上下合计 8）
            var totalModules = data.ModuleMatrix.Count + 8;
            var pixelsPerModule = Math.Max(MinModulePixels, sizePixels / totalModules);

            // drawQuietZones: true —— 四周留白，否则很多扫码器识别困难
            return new PngByteQRCode(data).GetGraphic(pixelsPerModule, drawQuietZones: true);
        }
        catch
        {
            // 内容超长（超出二维码最大容量）或编码失败
            return null;
        }
    }

    /// <summary>
    /// 把 PNG 字节构造成 WinUI 位图（用于 Image 显示）；失败返回 null。
    /// 必须在 UI 线程调用（WinUI 位图有线程亲和性）。
    /// </summary>
    public static async Task<BitmapImage?> CreateBitmapAsync(byte[] pngBytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream(); // 解绑，避免 Dispose 关闭流
            }
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拼 WiFi 二维码内容（扫码即连，需求参考文章格式）：
    /// WIFI:T:加密方式;S:名称;P:密码;H:是否隐藏;;
    /// 名称/密码里的特殊字符（\ ; , : "）必须转义，否则扫码端解析错乱。
    /// </summary>
    public static string BuildWifiPayload(string ssid, string password, string authType,
        bool isHidden = false)
    {
        return $"WIFI:T:{Escape(authType)};S:{Escape(ssid)};P:{Escape(password)};" +
               $"H:{(isHidden ? "true" : "false")};;";
    }

    /// <summary>WiFi 载荷里的特殊字符转义（按规范在字符前加反斜杠）</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>对外枚举 → QRCoder 纠错级别</summary>
    private static QRCodeGenerator.ECCLevel ToEccLevel(ErrorLevel level) => level switch
    {
        ErrorLevel.Low => QRCodeGenerator.ECCLevel.L,
        ErrorLevel.Quartile => QRCodeGenerator.ECCLevel.Q,
        ErrorLevel.High => QRCodeGenerator.ECCLevel.H,
        _ => QRCodeGenerator.ECCLevel.M
    };
}

using System.Text;
using DaenLauncher.Services;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace DaenLauncher.Views.Tools;

/// <summary>文字 ↔ 字节的编码格式（原文/密钥/IV 通用）</summary>
internal enum TextEncoding
{
    Utf8,
    Hex,
    Base64
}

/// <summary>
/// 加解密引擎：封装 BouncyCastle，对界面层提供"一行调用"的加密/解密。
/// 支持 SM4 / AES × CBC/ECB/CFB/OFB/CTR/GCM × PKCS5/PKCS7/Zeros/ISO10126/ANSIX923/ISO7816-4/NoPadding。
/// 说明：
/// - PKCS#5 在 16 字节分组算法上与 PKCS#7 完全等价（都按块长填充），映射到同一个实现；
/// - CFB/OFB/CTR/GCM 是流模式，不需要填充（界面上的填充选项对这些模式不生效，与常见在线工具一致）；
/// - CFB 使用 128 位反馈（CFB-128，CryptoJS 等常见库的默认行为）。
/// </summary>
internal static class CryptoEngine
{
    /// <summary>GCM 认证标签长度（位）</summary>
    private const int GcmTagBits = 128;

    /// <summary>加密：明文（字节）→ 密文文字（按输出格式编码，可选转大写）</summary>
    public static string Encrypt(CryptoAlgo algo, string modeKey, string paddingKey,
        byte[] key, byte[] iv, byte[] plain, bool upper, bool outputHex)
    {
        var cipherText = algo == CryptoAlgo.Sm4
            ? ProcessSm4(modeKey, paddingKey, key, iv, plain, encrypt: true)
            : ProcessAes(modeKey, paddingKey, key, iv, plain, encrypt: true);
        var text = outputHex ? ToHex(cipherText) : Convert.ToBase64String(cipherText);
        return upper ? text.ToUpperInvariant() : text;
    }

    /// <summary>解密：密文文字（按输出格式解码）→ 明文文字（按原文格式编码）</summary>
    public static string Decrypt(CryptoAlgo algo, string modeKey, string paddingKey,
        byte[] key, byte[] iv, string cipherTextText, TextEncoding cipherFormat, TextEncoding plainFormat, bool upper)
    {
        var cipherBytes = cipherFormat switch
        {
            TextEncoding.Hex => Convert.FromHexString(FilterHexChars(cipherTextText)),
            TextEncoding.Base64 => Convert.FromBase64String(cipherTextText.Trim()),
            _ => throw new InvalidOperationException(LocalizationService.Tr("Tools.Enc.ErrCipherFormat"))
        };

        var plain = algo == CryptoAlgo.Sm4
            ? ProcessSm4(modeKey, paddingKey, key, iv, cipherBytes, encrypt: false)
            : ProcessAes(modeKey, paddingKey, key, iv, cipherBytes, encrypt: false);

        return plainFormat switch
        {
            TextEncoding.Hex => upper ? ToHex(plain).ToUpperInvariant() : ToHex(plain),
            TextEncoding.Base64 => upper ? Convert.ToBase64String(plain).ToUpperInvariant() : Convert.ToBase64String(plain),
            _ => Encoding.UTF8.GetString(plain)
        };
    }

    /// <summary>字节转 Hex 小写字符串</summary>
    private static string ToHex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>Hex 容错：去掉空格/冒号/换行</summary>
    private static string FilterHexChars(string text) =>
        text.Trim().Replace(" ", "").Replace(":", "").Replace("\n", "").Replace("\r", "");

    // ===== SM4 / AES 的公共处理流程（只差引擎创建） =====

    private static byte[] ProcessSm4(string modeKey, string paddingKey, byte[] key, byte[] iv, byte[] data, bool encrypt) =>
        ProcessBlockCipher(new SM4Engine(), modeKey, paddingKey, key, iv, data, encrypt);

    private static byte[] ProcessAes(string modeKey, string paddingKey, byte[] key, byte[] iv, byte[] data, bool encrypt) =>
        ProcessBlockCipher(new AesEngine(), modeKey, paddingKey, key, iv, data, encrypt);

    /// <summary>按模式/填充组合执行一个分组密码的加密或解密</summary>
    private static byte[] ProcessBlockCipher(IBlockCipher engine, string modeKey, string paddingKey,
        byte[] key, byte[] iv, byte[] data, bool encrypt)
    {
        switch (modeKey)
        {
            case "Tools.Enc.GCM":
            {
                // GCM：认证加密，输出 = 密文 + 16 字节认证标签；解密时标签不对会抛异常
                var gcm = new GcmBlockCipher(engine);
                gcm.Init(encrypt,
                    new AeadParameters(new KeyParameter(key), GcmTagBits, iv.Length > 0 ? iv : GcmZeroIv));
                // 这个版本的 API：ProcessBytes 先写入输出缓冲，DoFinal(缓冲, 已写字节数) 就地收尾（GCM 追加认证标签）
                var output = new byte[gcm.GetOutputSize(data.Length)];
                var written = gcm.ProcessBytes(data, 0, data.Length, output, 0);
                gcm.DoFinal(output, written);
                return output;
            }

            case "Tools.Enc.CBC":
                return RunBuffered(new CbcBlockCipher(engine), paddingKey, key, iv, data, encrypt, withIv: true);

            case "Tools.Enc.ECB":
                return RunBuffered(engine, paddingKey, key, iv, data, encrypt, withIv: false);

            case "Tools.Enc.CFB":
                // CFB/OFB 的"分组长度"参数单位是字节（这里取满块 = CFB-128 / OFB-128）
                return RunBuffered(new CfbBlockCipher(engine, engine.GetBlockSize()),
                    paddingKey, key, iv, data, encrypt, withIv: true, streamMode: true);

            case "Tools.Enc.OFB":
                return RunBuffered(new OfbBlockCipher(engine, engine.GetBlockSize()),
                    paddingKey, key, iv, data, encrypt, withIv: true, streamMode: true);

            case "Tools.Enc.CTR":
                return RunBuffered(new SicBlockCipher(engine),
                    paddingKey, key, iv, data, encrypt, withIv: true, streamMode: true);

            default:
                throw new InvalidOperationException(LocalizationService.Tr("Tools.Enc.ErrMode"));
        }
    }

    /// <summary>GCM 未填 IV 时使用的全零 IV（与常见在线工具行为一致，长度 12 字节）</summary>
    private static readonly byte[] GcmZeroIv = new byte[12];

    /// <summary>用 PaddedBufferedBlockCipher（或无填充的 BufferedBlockCipher）跑完一次加/解密</summary>
    private static byte[] RunBuffered(IBlockCipher cipher, string paddingKey, byte[] key, byte[] iv,
        byte[] data, bool encrypt, bool withIv, bool streamMode = false)
    {
        // 流模式（CFB/OFB/CTR）不需要填充；分组模式按用户选择取填充实现
        IBufferedCipher buffered;
        if (streamMode || GetPadding(paddingKey) == null)
        {
            buffered = new BufferedBlockCipher(cipher);
        }
        else
        {
            buffered = new PaddedBufferedBlockCipher(cipher, GetPadding(paddingKey)!);
        }

        ICipherParameters parameters = withIv
            ? new ParametersWithIV(new KeyParameter(key), iv)
            : new KeyParameter(key);
        buffered.Init(encrypt, parameters);

        // 注意：解密时 DoFinal 返回"去填充后的真实长度"，比缓冲区小
        // （缓冲区尾部是填充区的残留字节，不截断会把垃圾带进明文，
        //  表现就是"解密结果后面跟着一串看不见的字符"，多行时第二行会被渲染吞掉）
        var output = new byte[buffered.GetOutputSize(data.Length)];
        var len = buffered.ProcessBytes(data, 0, data.Length, output, 0);
        len += buffered.DoFinal(output, len);
        if (len == output.Length)
        {
            return output;
        }
        var final = new byte[len];
        Array.Copy(output, final, len);
        return final;
    }

    /// <summary>填充方式语言键 → BouncyCastle 填充实现（NoPadding 返回 null）</summary>
    private static IBlockCipherPadding? GetPadding(string paddingKey) => paddingKey switch
    {
        "Tools.Enc.Pkcs5" or "Tools.Enc.Pkcs7" => new Pkcs7Padding(),
        "Tools.Enc.Zeros" => new ZeroBytePadding(),
        "Tools.Enc.Iso10126" => new ISO10126d2Padding(),
        "Tools.Enc.AnsiX923" => new X923Padding(),
        "Tools.Enc.Iso7816" => new ISO7816d4Padding(),
        _ => null // Tools.Enc.NoPadding
    };
}

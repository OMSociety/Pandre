using System.Buffers.Binary;

namespace Pandre.Core.Quark;

/// <summary>
/// SHA1 中间态计算：对前 N 个完整 64 字节块求压缩结果（不填充），
/// 供夸克 X-Oss-Hash-Ctx 头使用（分片定死 4MB，必然是 64 的倍数，无残块）。
/// 算法与 QuarkPan 的 calculate_sha1_incremental_state 等价（标准 FIPS 180-1 主循环）。
/// </summary>
internal static class Sha1Incremental
{
    public static (uint H0, uint H1, uint H2, uint H3, uint H4) MidState(ReadOnlySpan<byte> data)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
        Span<uint> w = stackalloc uint[80];
        for (var i = 0; i + 64 <= data.Length; i += 64)
        {
            var block = data.Slice(i, 64);
            for (var j = 0; j < 16; j++)
                w[j] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(j * 4, 4));
            for (var t = 16; t < 80; t++)
            {
                var x = w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16];
                w[t] = (x << 1) | (x >> 31);
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4;
            for (var t = 0; t < 80; t++)
            {
                uint f, k;
                if (t < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
                else if (t < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
                else if (t < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = b ^ c ^ d; k = 0xCA62C1D6; }
                var tmp = ((a << 5) | (a >> 27)) + f + e + k + w[t];
                e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
            }
            h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
        }
        return (h0, h1, h2, h3, h4);
    }

    /// <summary>拼 X-Oss-Hash-Ctx：五元组十进制字符串 + Nl=已处理位数的 JSON，base64。</summary>
    public static string BuildHashCtx(byte[] processedBytes)
    {
        var (h0, h1, h2, h3, h4) = MidState(processedBytes);
        var json = $"{{\"hash_type\":\"sha1\",\"h0\":\"{h0}\",\"h1\":\"{h1}\",\"h2\":\"{h2}\",\"h3\":\"{h3}\",\"h4\":\"{h4}\",\"Nl\":\"{(long)processedBytes.Length * 8}\",\"Nh\":\"0\",\"data\":\"\",\"num\":\"0\"}}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
    }
}

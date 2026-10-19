using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Pandre.Core.Quark;

/// <summary>
/// SHA1 流式计算：一次扫盘同时产出分片中间态快照（hash-ctx）与完整哈希。
/// 中间态 = 已处理完整 64 字节块的压缩结果（不填充），供夸克 X-Oss-Hash-Ctx 头使用
/// （分片大小是 65536 的倍数，边界必然落在完整块上）。
/// 算法与夸克桌面端 getHashCtx 等价（标准 FIPS 180-1 主循环；Nl/Nh 按总位数高低 32 位拆分，
/// 前缀超过 512MB 时 Nh 非零）。
/// </summary>
internal sealed class Sha1Incremental
{
    private uint _h0 = 0x67452301, _h1 = 0xEFCDAB89, _h2 = 0x98BADCFE, _h3 = 0x10325476, _h4 = 0xC3D2E1F0;
    private long _processed; // 已压缩进状态的完整块字节数
    private readonly byte[] _tail = new byte[64]; // 残块暂存（仅完整哈希收尾用）
    private int _tailLen;
    private readonly uint[] _w = new uint[80];

    /// <summary>喂入数据；处理全部完整 64B 块，残块暂存到下次 Feed 或收尾。</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_tailLen > 0)
        {
            var take = Math.Min(64 - _tailLen, data.Length);
            data[..take].CopyTo(_tail.AsSpan(_tailLen));
            _tailLen += take;
            data = data[take..];
            if (_tailLen == 64)
            {
                CompressBlock(_tail);
                _processed += 64;
                _tailLen = 0;
            }
        }
        while (data.Length >= 64)
        {
            CompressBlock(data[..64]);
            _processed += 64;
            data = data[64..];
        }
        if (!data.IsEmpty)
        {
            data.CopyTo(_tail.AsSpan(_tailLen));
            _tailLen += data.Length;
        }
    }

    /// <summary>
    /// 当前中间态 → X-Oss-Hash-Ctx。Nl = 总位数低 32 位、Nh = 高 32 位
    /// （与夸克桌面端 getHashCtx 的 BigInt 拆分一致）。
    /// </summary>
    public string SnapshotCtx()
    {
        // 快照只覆盖已压缩进状态的完整 64 字节块，残块（_tail）尚未进状态：
        // 若在带残块的时刻拍快照，Nl/Nh 会静默少计尾部字节，hash-ctx 与服务端预期错位。
        // 调用约定：只在分片边界（65536 的倍数，必为 64 的倍数）拍快照，此刻残块必为空。
        Debug.Assert(_tailLen == 0, "SnapshotCtx 必须在无残块（分片边界）时调用，否则中间态少计尾部字节");
        var bits = (ulong)((long)_processed * 8);
        var json = $"{{\"hash_type\":\"sha1\",\"h0\":\"{_h0}\",\"h1\":\"{_h1}\",\"h2\":\"{_h2}\",\"h3\":\"{_h3}\",\"h4\":\"{_h4}\"," +
                   $"\"Nl\":\"{(uint)(bits & 0xFFFFFFFFu)}\",\"Nh\":\"{(uint)(bits >> 32)}\",\"data\":\"\",\"num\":\"0\"}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>标准填充收尾 → 完整 SHA1 hex（与 SHA1.HashData 结果一致）。</summary>
    public string FinalizeHex()
    {
        var totalBits = (ulong)((long)(_processed + _tailLen) * 8);
        var padded = (_tailLen + 9 + 63) / 64 * 64; // 0x80 + 长度 8B，凑满 64B 倍数
        Span<byte> last = stackalloc byte[128]; // tailLen<=63 时 padded<=128
        last.Clear();
        _tail.AsSpan(0, _tailLen).CopyTo(last);
        last[_tailLen] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(last[(padded - 8)..padded], totalBits);
        for (var i = 0; i < padded; i += 64)
            CompressBlock(last.Slice(i, 64));
        return $"{_h0:x8}{_h1:x8}{_h2:x8}{_h3:x8}{_h4:x8}";
    }

    private void CompressBlock(ReadOnlySpan<byte> block)
    {
        for (var j = 0; j < 16; j++)
            _w[j] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(j * 4, 4));
        for (var t = 16; t < 80; t++)
        {
            var x = _w[t - 3] ^ _w[t - 8] ^ _w[t - 14] ^ _w[t - 16];
            _w[t] = (x << 1) | (x >> 31);
        }

        uint a = _h0, b = _h1, c = _h2, d = _h3, e = _h4;
        for (var t = 0; t < 80; t++)
        {
            uint f, k;
            if (t < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
            else if (t < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
            else if (t < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6; }
            var tmp = ((a << 5) | (a >> 27)) + f + e + k + _w[t];
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
        }
        _h0 += a; _h1 += b; _h2 += c; _h3 += d; _h4 += e;
    }
}

using System.Text;

namespace LivePhoto.Core.Mp4;

/// <summary>
/// 内嵌视频的<b>容器补全</b>：把缺失的 <c>ftyp</c> 盒补回视频切片开头。
/// <para>
/// 背景（2026-10-17 查明）：部分第三方转换链路给文件写上了标准 Google XMP，
/// 却在拼接视频时剥掉了 20 字节的 ftyp 盒。此时视频数据（<c>mdat</c>）与索引
/// （<c>moov</c>）本身完好，但播放器按 XMP 声明切出这段视频后<b>认不出容器类型</b> ——
/// 绿联上的表现正是「能识别成实况照片、但视频播放失败」。
/// </para>
/// <para>
/// <b>关键是偏移必须同步平移</b>：<c>stco</c>/<c>co64</c> 里的 chunk 偏移是以
/// 「视频切片第一个字节」为原点的。实测铁证 —— 无 ftyp 样本的视频轨首个偏移是 16
/// （= wide 8 + mdat 头 8），有 ftyp 的同设备样本则是 36（= ftyp 20 + wide 8 + mdat 头 8），
/// 差值恰好等于 ftyp 的长度。只补头不平移，索引就会整体偏移 20 字节
/// （典型的"能识别、不能播"）。
/// </para>
/// <para>
/// 本类只碰容器头与索引表，<b>mdat 内的音视频数据一个字节都不动</b>。
/// </para>
/// </summary>
public static class Mp4ContainerRepair
{
    /// <summary>
    /// 标准 20 字节 ftyp（QuickTime 品牌）：
    /// major_brand = <c>qt  </c>，minor_version = 0，compatible_brands = <c>qt  </c>。
    /// <para>
    /// 用 QuickTime 品牌而非 <c>isom</c>/<c>mp42</c>，因为这类视频确实是 QuickTime 容器
    /// （带 <c>wide</c> 占位盒、<c>lpcm</c> 音轨、<c>meta</c> 轨），
    /// 且与同设备原生实况视频的 ftyp 逐字节一致（该品牌已实测可在绿联正常播放）。
    /// </para>
    /// </summary>
    public static readonly byte[] QuickTimeFtyp =
    {
        0x00, 0x00, 0x00, 0x14,                                     // size = 20
        (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'q', (byte)'t', (byte)' ', (byte)' ',                 // major_brand
        0x00, 0x00, 0x00, 0x00,                                     // minor_version
        (byte)'q', (byte)'t', (byte)' ', (byte)' ',                 // compatible_brands[0]
    };

    /// <summary>补进去的 ftyp 盒长度，同时也是 chunk 偏移的平移量。</summary>
    public static int FtypSize => QuickTimeFtyp.Length;

    private static readonly string[] Containers =
    {
        "moov", "trak", "mdia", "minf", "stbl", "dinf", "udta", "meta", "edts",
    };

    /// <summary>
    /// 给缺 ftyp 的视频切片补回标准头。
    /// </summary>
    /// <param name="mov">原视频切片（以 <c>wide</c> 盒开头）。</param>
    /// <param name="patchedOffsets">被平移的 chunk 偏移条目数（写入日志用）。</param>
    /// <returns>补全后的字节：<c>[ftyp][原切片，其中 moov 内的偏移已 +20]</c>。</returns>
    public static byte[] AddFtyp(byte[] mov, out int patchedOffsets)
    {
        int shift = FtypSize;
        byte[] dst = new byte[mov.Length + shift];
        Array.Copy(QuickTimeFtyp, 0, dst, 0, shift);
        Array.Copy(mov, 0, dst, shift, mov.Length);
        patchedOffsets = ShiftChunkOffsets(dst, shift, dst.Length, +shift, depth: 0);

        // 一处 chunk 偏移都没找到，说明这不是我们认得的结构 ——
        // 此时宁可不补：补个"看似标准、实际索引错位"的头出去，比不修更糟。
        // 调用方据 patchedOffsets 决定是采用结果还是拒绝写出。
        return patchedOffsets > 0 ? dst : mov;
    }

    /// <summary>
    /// <see cref="AddFtyp"/> 的逆运算：去掉 ftyp 并把偏移还原。
    /// <para>
    /// 供校验器做往返比对 —— 用来证明"补全除了 ftyp 与那些偏移之外，没有动过任何字节"。
    /// 这比逐项检查偏移范围强得多：偏移平移少一处、多一处都会被这一项抓住。
    /// </para>
    /// </summary>
    public static byte[] RemoveFtyp(byte[] mov)
    {
        if (!IsValidQuickTimeFtyp(mov))
        {
            return mov;
        }

        int shift = FtypSize;
        byte[] dst = new byte[mov.Length - shift];
        Array.Copy(mov, shift, dst, 0, dst.Length);
        ShiftChunkOffsets(dst, 0, dst.Length, -shift, depth: 0);
        return dst;
    }

    /// <summary>前 20 字节是否恰为本类写入的标准 QuickTime ftyp。</summary>
    public static bool IsValidQuickTimeFtyp(byte[] mov) =>
        mov.Length >= FtypSize && mov.AsSpan(0, FtypSize).SequenceEqual(QuickTimeFtyp);

    /// <summary>
    /// 把整个视频切片内所有 <c>stco</c>/<c>co64</c> 偏移就地平移 <paramref name="delta"/>，
    /// 返回平移的条目数。
    /// <para>
    /// 供测试构造"真实形态"的缺 ftyp 文件用 —— 真实的转换链路在剥掉 ftyp 时会按新的
    /// 切片起点重算偏移，只删头不平移造出来的是另一种（本身就不自洽的）文件。
    /// </para>
    /// </summary>
    public static int ShiftChunkOffsets(byte[] mov, int delta) =>
        ShiftChunkOffsets(mov, 0, mov.Length, delta, depth: 0);

    /// <summary>
    /// 只读收集全部 <c>stco</c>/<c>co64</c> 条目（供校验器检查偏移是否落在 mdat 数据区内）。
    /// </summary>
    public static IReadOnlyList<long> CollectChunkOffsets(byte[] mov)
    {
        List<long> list = new();
        CollectChunkOffsets(mov, 0, mov.Length, list, depth: 0);
        return list;
    }

    private static void CollectChunkOffsets(byte[] buf, int start, int end, List<long> outList, int depth)
    {
        int p = start;
        while (p + 8 <= end)
        {
            uint size = ReadU32(buf, p);
            int hdr = 8;
            if (size == 1)
            {
                if (p + 16 > end)
                {
                    break;
                }

                ulong big = ReadU64(buf, p + 8);
                if (big > int.MaxValue)
                {
                    break;
                }

                size = (uint)big;
                hdr = 16;
            }
            else if (size == 0)
            {
                size = (uint)(end - p);
            }

            if (size < hdr || p + (long)size > end)
            {
                break;
            }

            string type = Encoding.Latin1.GetString(buf, p + 4, 4);
            if (type is "stco" or "co64")
            {
                int countAt = p + hdr + 4;
                int boxEnd = p + (int)size;
                int entrySize = type == "co64" ? 8 : 4;
                if (countAt + 4 <= boxEnd)
                {
                    long count = ReadU32(buf, countAt);
                    int entriesAt = countAt + 4;
                    if (entriesAt + count * entrySize <= boxEnd)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            outList.Add(type == "co64"
                                ? (long)ReadU64(buf, entriesAt + i * 8)
                                : ReadU32(buf, entriesAt + i * 4));
                        }
                    }
                }
            }
            else if (depth < 8 && Array.IndexOf(Containers, type) >= 0)
            {
                CollectChunkOffsets(buf, p + hdr + (type == "meta" ? 4 : 0), p + (int)size, outList, depth + 1);
            }

            p += (int)size;
        }
    }

    /// <summary>
    /// 递归平移 <c>stco</c>/<c>co64</c> 表内的所有 chunk 偏移。
    /// <b>只认结构自洽的表</b>（entry_count 与盒子尺寸必须对得上），否则原样不动 ——
    /// 宁可少改，也不能把损坏的索引改成另一种损坏。
    /// </summary>
    private static int ShiftChunkOffsets(byte[] buf, int start, int end, int delta, int depth)
    {
        int patched = 0;
        int p = start;
        while (p + 8 <= end)
        {
            uint size = ReadU32(buf, p);
            int hdr = 8;
            if (size == 1)
            {
                if (p + 16 > end)
                {
                    break;
                }

                ulong big = ReadU64(buf, p + 8);
                if (big > int.MaxValue)
                {
                    break;
                }

                size = (uint)big;
                hdr = 16;
            }
            else if (size == 0)
            {
                size = (uint)(end - p);
            }

            if (size < hdr || p + (long)size > end)
            {
                break;
            }

            string type = Encoding.Latin1.GetString(buf, p + 4, 4);

            if (type is "stco" or "co64")
            {
                patched += ShiftOneTable(buf, p, hdr, (int)size, delta, wide: type == "co64");
            }
            else if (depth < 8 && Array.IndexOf(Containers, type) >= 0)
            {
                int body = p + hdr + (type == "meta" ? 4 : 0);
                patched += ShiftChunkOffsets(buf, body, p + (int)size, delta, depth + 1);
            }

            p += (int)size;
        }

        return patched;
    }

    /// <summary>
    /// 平移单个 stco/co64 表。<c>full box</c> 布局：version(1) + flags(3) + entry_count(4) + entries。
    /// </summary>
    private static int ShiftOneTable(byte[] buf, int boxStart, int hdr, int boxSize, int delta, bool wide)
    {
        int entrySize = wide ? 8 : 4;
        int countAt = boxStart + hdr + 4;
        int boxEnd = boxStart + boxSize;
        if (countAt + 4 > boxEnd)
        {
            return 0;
        }

        long count = ReadU32(buf, countAt);
        int entriesAt = countAt + 4;
        if (entriesAt + count * entrySize > boxEnd)
        {
            return 0;   // 表本身不自洽，不动
        }

        int patched = 0;
        for (int i = 0; i < count; i++)
        {
            int at = entriesAt + (int)(i * entrySize);
            if (wide)
            {
                long v = (long)ReadU64(buf, at) + delta;
                if (v < 0)
                {
                    continue;
                }

                WriteU64(buf, at, (ulong)v);
            }
            else
            {
                long v = (long)ReadU32(buf, at) + delta;
                if (v < 0 || v > uint.MaxValue)
                {
                    continue;
                }

                WriteU32(buf, at, (uint)v);
            }

            patched++;
        }

        return patched;
    }

    private static uint ReadU32(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    private static ulong ReadU64(byte[] b, int off)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v = (v << 8) | b[off + i];
        }

        return v;
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    private static void WriteU64(byte[] b, int off, ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            b[off + i] = (byte)(v >> (56 - i * 8));
        }
    }
}

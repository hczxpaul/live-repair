namespace LivePhoto.Core.Exif;

/// <summary>
/// EXIF/TIFF 的解析与重序列化。
/// <para>
/// <b>这是全项目最关键的一块。</b>序列化输出必须与 Python 参考实现
/// （<c>fix_livephoto.serialize</c>）<b>逐字节相同</b>，否则产出的文件会与已验证版本不一致。
/// </para>
/// <para>
/// 关键行为（已逐条对齐参考实现）：
/// <list type="bullet">
/// <item>布局固定为：TIFF头(8) → IFD0 → Exif → GPS → Interop → IFD1 → data blob</item>
/// <item>data blob 跨所有 IFD 连续追加，按 emit 顺序累积，非 4 字节值时做偶对齐补零</item>
/// <item>子 IFD 指针值强制重写为新的 IFD 偏移（不沿用原文件的值）</item>
/// <item>条目顺序 = 列表顺序（只有追加新标签时才重排为 tag 升序）</item>
/// </list>
/// </para>
/// </summary>
public static class TiffCodec
{
    private static int TypeSize(ushort t) => t switch
    {
        1 => 1, 2 => 1, 3 => 2, 4 => 4, 5 => 8, 6 => 1, 7 => 1,
        8 => 2, 9 => 4, 10 => 8, 11 => 4, 12 => 8,
        _ => 1,
    };

    private static int IfdLen(List<Entry> l) => 2 + (12 * l.Count) + 4;

    private static byte[] W(uint v, int size, bool le)
    {
        byte[] r = new byte[size];
        for (int i = 0; i < size; i++)
        {
            r[le ? i : size - 1 - i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        return r;
    }

    private static ushort R16(byte[] b, int off, bool le) =>
        le ? (ushort)(b[off] | (b[off + 1] << 8))
           : (ushort)((b[off] << 8) | b[off + 1]);

    private static uint R32(byte[] b, int off, bool le) =>
        le ? (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24))
           : ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    /// <summary>
    /// 从条目 <c>Data</c> 的前 4 字节读一个 uint。参考实现用
    /// <c>int.from_bytes(e.data[0:4])</c>，data 不足 4 字节时等价于高位补零。
    /// </summary>
    private static uint R32Entry(byte[] data, bool le)
    {
        byte[] buf = new byte[4];
        int n = Math.Min(4, data.Length);
        for (int i = 0; i < n; i++)
        {
            buf[i] = data[i];
        }

        return R32(buf, 0, le);
    }

    /// <summary>解析 TIFF。参考实现的 <c>parse_tiff</c>。</summary>
    public static Tiff Parse(byte[] tf)
    {
        if (tf.Length < 8)
        {
            throw new ExifFormatException("TIFF 数据过短");
        }

        bool le = tf[0] == 0x49 && tf[1] == 0x49;
        if (!le && !(tf[0] == 0x4D && tf[1] == 0x4D))
        {
            throw new ExifFormatException("不是 TIFF 结构（端序标记非法）");
        }

        Tiff t = new() { LittleEndian = le };

        (List<Entry> ifd0, uint nxt) = ParseIfd(tf, R32(tf, 4, le), le);
        t.Ifd0.AddRange(ifd0);

        if (nxt != 0)
        {
            (List<Entry> ifd1, _) = ParseIfd(tf, nxt, le);
            t.Ifd1 = ifd1;

            Entry? o = ExifAccess.Get(ifd1, 0x0201);
            Entry? l = ExifAccess.Get(ifd1, 0x0202);
            if (o is not null && l is not null)
            {
                uint to = R32Entry(o.Data, le);
                uint tl = R32Entry(l.Data, le);
                if (tl > 0 && (long)to + tl <= tf.Length)
                {
                    t.Thumb = new byte[tl];
                    Array.Copy(tf, to, t.Thumb, 0, (int)tl);
                    ExifAccess.Del(ifd1, 0x0201);
                    ExifAccess.Del(ifd1, 0x0202);
                }
            }
        }

        foreach (Entry e in t.Ifd0)
        {
            uint so = R32Entry(e.Data, le);
            switch (e.Tag)
            {
                case Tiff.TagExifIfd:
                    t.Exif = ParseIfd(tf, so, le).Entries;
                    break;
                case Tiff.TagGpsIfd:
                    t.Gps = ParseIfd(tf, so, le).Entries;
                    break;
                case Tiff.TagInteropIfd:
                    t.Interop = ParseIfd(tf, so, le).Entries;
                    break;
            }
        }

        return t;
    }

    private static (List<Entry> Entries, uint Next) ParseIfd(byte[] tf, uint off, bool le)
    {
        if (off + 2 > tf.Length)
        {
            throw new ExifFormatException($"IFD 偏移越界: {off}");
        }

        int o = (int)off;
        ushort n = R16(tf, o, le);
        List<Entry> entries = new(n);

        for (int i = 0; i < n; i++)
        {
            int p = o + 2 + (12 * i);
            if (p + 12 > tf.Length)
            {
                throw new ExifFormatException($"IFD 条目越界: {p}");
            }

            ushort tag = R16(tf, p, le);
            ushort typ = R16(tf, p + 2, le);
            uint cnt = R32(tf, p + 4, le);
            long sz = (long)TypeSize(typ) * cnt;

            if (sz <= 4)
            {
                byte[] raw = new byte[sz];
                Array.Copy(tf, p + 8, raw, 0, (int)sz);
                entries.Add(new Entry(tag, typ, cnt, raw));
            }
            else
            {
                uint vo = R32(tf, p + 8, le);
                if (vo + sz > tf.Length)
                {
                    throw new ExifFormatException($"IFD 数据越界: tag=0x{tag:X4} off={vo} len={sz}");
                }

                byte[] raw = new byte[sz];
                Array.Copy(tf, vo, raw, 0, (int)sz);
                entries.Add(new Entry(tag, typ, cnt, raw));
            }
        }

        uint nxt = R32(tf, o + 2 + (12 * n), le);
        return (entries, nxt);
    }

    /// <summary>重序列化 TIFF。参考实现的 <c>serialize</c>。</summary>
    public static byte[] Serialize(Tiff t)
    {
        bool le = t.LittleEndian;

        Dictionary<string, int> pos = new();
        int cur = 8;
        pos["ifd0"] = cur;
        cur += IfdLen(t.Ifd0);

        if (t.Exif is not null)
        {
            pos["exif"] = cur;
            cur += IfdLen(t.Exif);
        }

        if (t.Gps is not null)
        {
            pos["gps"] = cur;
            cur += IfdLen(t.Gps);
        }

        if (t.Interop is not null)
        {
            pos["interop"] = cur;
            cur += IfdLen(t.Interop);
        }

        if (t.Ifd1 is not null)
        {
            pos["ifd1"] = cur;
            cur += IfdLen(t.Ifd1);
        }

        int dataStart = cur;
        List<byte> blob = new();
        if (t.Thumb is not null)
        {
            blob.AddRange(t.Thumb);
            if (blob.Count % 2 != 0)
            {
                blob.Add(0);
            }
        }

        List<byte> Emit(List<Entry> entries, Dictionary<ushort, string>? ptr)
        {
            List<byte> b = new(2 + (12 * entries.Count) + 4);
            b.AddRange(W((uint)entries.Count, 2, le));

            foreach (Entry e in entries)
            {
                b.AddRange(W(e.Tag, 2, le));
                b.AddRange(W(e.Type, 2, le));
                b.AddRange(W(e.Count, 4, le));

                if (ptr is not null && ptr.Count > 0 && ptr.TryGetValue(e.Tag, out string? kn))
                {
                    b.AddRange(W((uint)pos[kn], 4, le));
                }
                else if (e.Tag == 0x0201 && t.Thumb is not null)
                {
                    b.AddRange(W((uint)dataStart, 4, le));
                }
                else if (e.Tag == 0x0202 && t.Thumb is not null)
                {
                    b.AddRange(W((uint)t.Thumb.Length, 4, le));
                }
                else if (e.Data.Length <= 4)
                {
                    b.AddRange(e.Data);
                    for (int i = e.Data.Length; i < 4; i++)
                    {
                        b.Add(0);
                    }
                }
                else
                {
                    b.AddRange(W((uint)(dataStart + blob.Count), 4, le));
                    blob.AddRange(e.Data);
                    if (blob.Count % 2 != 0)
                    {
                        blob.Add(0);
                    }
                }
            }

            b.AddRange(new byte[] { 0, 0, 0, 0 });
            return b;
        }

        Dictionary<ushort, string> sub = new();
        if (t.Exif is not null)
        {
            sub[Tiff.TagExifIfd] = "exif";
        }

        if (t.Gps is not null)
        {
            sub[Tiff.TagGpsIfd] = "gps";
        }

        if (t.Interop is not null)
        {
            sub[Tiff.TagInteropIfd] = "interop";
        }

        List<byte> outB = new();
        outB.AddRange(le ? new byte[] { 0x49, 0x49 } : new byte[] { 0x4D, 0x4D });
        outB.AddRange(W(42, 2, le));
        outB.AddRange(W((uint)pos["ifd0"], 4, le));

        outB.AddRange(Emit(t.Ifd0, sub));
        if (t.Exif is not null)
        {
            outB.AddRange(Emit(t.Exif, null));
        }

        if (t.Gps is not null)
        {
            outB.AddRange(Emit(t.Gps, null));
        }

        if (t.Interop is not null)
        {
            outB.AddRange(Emit(t.Interop, null));
        }

        if (t.Ifd1 is not null)
        {
            outB.AddRange(Emit(t.Ifd1, null));
        }

        outB.AddRange(blob);
        return outB.ToArray();
    }
}

/// <summary>EXIF 结构损坏时抛出。调用方应据此把文件归类为 <see cref="PhotoCondition.BrokenExif"/>。</summary>
public sealed class ExifFormatException : Exception
{
    public ExifFormatException(string message)
        : base(message)
    {
    }
}

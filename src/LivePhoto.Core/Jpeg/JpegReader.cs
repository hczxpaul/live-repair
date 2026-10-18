namespace LivePhoto.Core.Jpeg;

/// <summary>
/// 一个 JPEG 段。<c>Start</c> 是 marker 起点（含 FFFF 前缀中的后一个 FF，即 i）。
/// <c>PayloadStart</c> = Start + 4（越过长度字段），<c>End</c> = Start + 2 + length。
/// </summary>
public readonly record struct JpegSegment(int Marker, int Start, int PayloadStart, int End)
{
    /// <summary>段总长（含 marker 两字节）。</summary>
    public int Length => End - Start;
}

/// <summary>
/// JPEG 段扫描与 EOI 定位。
/// <para>
/// <b>全项目唯一的 JPEG 结构解析实现。</b>分析、修复、校验三处都必须走这里 ——
/// 若各自实现一遍"找 EOI"，容错策略稍有差异就会出现
/// "列表里显示可修、一点修复立刻抛格式异常"这类极难定位的 bug。
/// </para>
/// </summary>
public static class JpegReader
{
    public const int MarkerSoi = 0xD8;

    public const int MarkerSos = 0xDA;

    public const int MarkerApp1 = 0xE1;

    public const int MarkerDri = 0xDD;

    private static bool IsStandalone(int m) =>
        m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7);

    /// <summary>扫描 JPEG 段，遇到 SOS(FFDA) 停止。参考实现的 <c>jpeg_segments</c>。</summary>
    public static List<JpegSegment> ReadSegments(byte[] d)
    {
        if (d.Length < 2 || d[0] != 0xFF || d[1] != 0xD8)
        {
            throw new JpegFormatException("不是 JPEG（缺少 SOI）");
        }

        List<JpegSegment> segs = new();
        int i = 2;
        while (i < d.Length - 1)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }

            int m = d[i + 1];
            if (IsStandalone(m))
            {
                i += 2;
                continue;
            }

            if (i + 4 > d.Length)
            {
                throw new JpegFormatException($"段长度字段越界: offset={i}");
            }

            int ln = (d[i + 2] << 8) | d[i + 3];
            if (ln < 2)
            {
                throw new JpegFormatException($"段长度非法: offset={i} len={ln}");
            }

            int end = i + 2 + ln;
            if (end > d.Length)
            {
                throw new JpegFormatException($"段越界: offset={i} len={ln}，文件仅 {d.Length} 字节");
            }

            segs.Add(new JpegSegment(m, i, i + 4, end));
            if (m == MarkerSos)
            {
                break;
            }

            i += 2 + ln;
        }

        return segs;
    }

    /// <summary>
    /// 定位 EOI 的偏移（指向 FFD9 的 FF 字节）。
    /// 找不到返回 -1。
    /// <para>
    /// 扫描时会跳过熵编码数据里的 <c>FF00</c>（字节填充）与 <c>FFD0~FFD7</c>（RSTn 重启标记），
    /// 这两个不是段 marker，必须跳过，否则会提前误判 EOI。
    /// </para>
    /// </summary>
    public static int FindEoi(byte[] d) => FindEoi(d, ReadSegments(d));

    /// <summary>同上，但复用已扫描好的段表（头部区段只需扫一次）。</summary>
    public static int FindEoi(byte[] d, List<JpegSegment> segs)
    {
        foreach (JpegSegment s in segs)
        {
            if (s.Marker != MarkerSos)
            {
                continue;
            }

            int j = s.Start + 2;
            while (j < d.Length - 1)
            {
                if (d[j] == 0xFF)
                {
                    int nx = d[j + 1];
                    if (nx == 0x00 || (nx >= 0xD0 && nx <= 0xD7))
                    {
                        j += 2;
                        continue;
                    }

                    return j;
                }

                j++;
            }
        }

        return -1;
    }

    /// <summary>取第一个 Exif APP1 段（payload 以 <c>Exif\0\0</c> 开头）。</summary>
    public static JpegSegment? FindExifApp1(byte[] d) => FindExifApp1(d, ReadSegments(d));

    /// <summary>同上，但复用已扫描好的段表。</summary>
    public static JpegSegment? FindExifApp1(byte[] d, List<JpegSegment> segs)
    {
        foreach (JpegSegment s in segs)
        {
            if (s.Marker != MarkerApp1)
            {
                continue;
            }

            if (s.PayloadStart + 6 <= d.Length &&
                d[s.PayloadStart] == 0x45 && d[s.PayloadStart + 1] == 0x78 &&
                d[s.PayloadStart + 2] == 0x69 && d[s.PayloadStart + 3] == 0x66 &&
                d[s.PayloadStart + 4] == 0x00 && d[s.PayloadStart + 5] == 0x00)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>取第一个 XMP APP1 段（payload 以 <c>http</c> 开头）。</summary>
    public static JpegSegment? FindXmpApp1(byte[] d) => FindXmpApp1(d, ReadSegments(d));

    /// <summary>同上，但复用已扫描好的段表。</summary>
    public static JpegSegment? FindXmpApp1(byte[] d, List<JpegSegment> segs)
    {
        foreach (JpegSegment s in segs)
        {
            if (s.Marker != MarkerApp1)
            {
                continue;
            }

            if (s.PayloadStart + 4 <= d.Length &&
                d[s.PayloadStart] == 0x68 && d[s.PayloadStart + 1] == 0x74 &&
                d[s.PayloadStart + 2] == 0x74 && d[s.PayloadStart + 3] == 0x70)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>读取 XMP 段的文本内容（UTF-8）。没有则返回 null。</summary>
    public static string? ReadXmpText(byte[] d) => ReadXmpText(d, ReadSegments(d));

    /// <summary>同上，但复用已扫描好的段表。</summary>
    public static string? ReadXmpText(byte[] d, List<JpegSegment> segs) => ReadXmpText(d, FindXmpApp1(d, segs));

    private static string? ReadXmpText(byte[] d, JpegSegment? s)
    {
        if (s is null)
        {
            return null;
        }

        JpegSegment seg = s.Value;
        int from = seg.PayloadStart + 29;   // 跳过 "http://ns.adobe.com/xap/1.0/\0" 共 29 字节
        if (from > seg.End)
        {
            from = seg.End;
        }

        return System.Text.Encoding.UTF8.GetString(d, from, seg.End - from);
    }
}

/// <summary>JPEG 结构损坏时抛出。对应 <see cref="PhotoCondition.BrokenJpeg"/>。</summary>
public sealed class JpegFormatException : Exception
{
    public JpegFormatException(string message)
        : base(message)
    {
    }
}

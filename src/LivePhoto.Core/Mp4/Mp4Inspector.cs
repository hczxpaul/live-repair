namespace LivePhoto.Core.Mp4;

/// <summary>
/// ISO-BMFF（MP4/MOV）的只读解析。不写入任何字节 ——
/// 修复全程视频部分只做搬运，一个字节都不改。
/// </summary>
public static class Mp4Inspector
{
    private static readonly string[] Containers =
    {
        "moov", "trak", "mdia", "minf", "stbl", "udta", "meta", "edts", "dinf",
    };

    /// <summary>
    /// 在给定缓冲区里定位 ftyp box。
    /// <para>
    /// 必须<b>双重校验</b>：不仅要有 <c>ftyp</c> 四字节，size 还要落在 16..64。
    /// 否则会命中厂商私有块内部恰好出现的同名字节，导致视频起点判断错误。
    /// </para>
    /// </summary>
    public static int FindFtyp(byte[] buf)
    {
        for (int i = 0; i + 8 <= buf.Length; i++)
        {
            if (buf[i + 4] == 0x66 && buf[i + 5] == 0x74 && buf[i + 6] == 0x79 && buf[i + 7] == 0x70)
            {
                uint sz = ReadU32(buf, i);
                if (sz >= 16 && sz <= 64)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// 定位内嵌视频（MP4）的起点。
    /// <para>
    /// 优先找 <c>ftyp</c> 盒；找不到时回退识别以 <c>wide</c> 盒开头的老式 QuickTime 布局 ——
    /// 实测部分互传/下载链路会剥掉 20 字节的 ftyp 盒，视频数据（mdat/moov）本身完好，
    /// 只按 ftyp 判断会把这类文件误判成"没有内嵌视频"。
    /// </para>
    /// </summary>
    public static int FindVideoStart(byte[] buf) => FindVideoStart(buf, 0);

    /// <summary>同上，但从 <paramref name="offset"/> 之后开始搜索（避免整段拷贝）。</summary>
    public static int FindVideoStart(byte[] buf, int offset)
    {
        int f = FindFtyp(buf, offset);
        return f >= 0 ? f : FindWideStart(buf, offset);
    }

    /// <summary>
    /// wide 盒回退搜索。三重校验防误判：
    /// ① size 恒为 8；② 类型恰为 <c>wide</c>；③ 自此开始的盒链全部合法且能走到 mdat/moov。
    /// 熵数据里偶然出现同名字节的概率在这三道关面前可以忽略。
    /// </summary>
    private static int FindWideStart(byte[] buf, int offset)
    {
        for (int i = offset; i + 16 <= buf.Length; i++)
        {
            if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0 || buf[i + 3] != 8)
            {
                continue;
            }

            if (buf[i + 4] != 0x77 || buf[i + 5] != 0x69 || buf[i + 6] != 0x64 || buf[i + 7] != 0x65)
            {
                continue;
            }

            if (LeadsToMedia(buf, i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>从 <paramref name="start"/> 开始的顶层盒链是否合法、且含有 mdat 或 moov。</summary>
    private static bool LeadsToMedia(byte[] buf, int start)
    {
        int p = start;
        int boxes = 0;
        while (p + 8 <= buf.Length && boxes < 64)
        {
            uint sz = ReadU32(buf, p);
            string t = BoxType(buf, p);
            if (sz == 1)
            {
                if (p + 16 > buf.Length)
                {
                    return false;
                }

                sz = (uint)ReadU64(buf, p + 8);
            }
            else if (sz == 0)
            {
                sz = (uint)(buf.Length - p);
            }

            if (sz < 8 || p + sz > buf.Length)
            {
                return false;
            }

            boxes++;
            if (t is "mdat" or "moov")
            {
                return true;
            }

            p += (int)sz;
        }

        return false;
    }

    /// <summary>同上，但在 <paramref name="buf"/> 的 <paramref name="offset"/> 之后开始搜索（避免整段拷贝）。</summary>
    public static int FindFtyp(byte[] buf, int offset)
    {
        for (int i = offset; i + 8 <= buf.Length; i++)
        {
            if (buf[i + 4] == 0x66 && buf[i + 5] == 0x74 && buf[i + 6] == 0x79 && buf[i + 7] == 0x70)
            {
                uint sz = ReadU32(buf, i);
                if (sz >= 16 && sz <= 64)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>顶层 box 链及覆盖到的字节数。参考实现的 <c>mp4_boxes_of</c>。</summary>
    public static (List<Box> Boxes, int Coverage) BoxesOf(byte[] m)
    {
        List<Box> outList = new();
        int p = 0;
        while (p + 8 <= m.Length)
        {
            uint sz = ReadU32(m, p);
            string tp = BoxType(m, p);
            if (sz == 1)
            {
                if (p + 16 > m.Length)
                {
                    break;
                }

                sz = (uint)ReadU64(m, p + 8);
            }
            else if (sz == 0)
            {
                sz = (uint)(m.Length - p);
            }

            if (sz < 8 || p + sz > m.Length)
            {
                break;
            }

            outList.Add(new Box(tp, p, (int)sz));
            p += (int)sz;
        }

        return (outList, p);
    }

    /// <summary>
    /// 视频轨时长（毫秒）。参考实现的 <c>video_duration_ms</c>。
    /// <para>
    /// Apple 的实况 MP4 里 <c>hdlr</c> 在 <c>mdia</c> 直级与 <c>minf</c> 各出现一次，
    /// 后者为空值 —— <b>必须取第一个非空值</b>，否则会读到空 handler 而判不出视频轨。
    /// </para>
    /// </summary>
    public static double? DurationMs(byte[] mov)
    {
        List<Node> all = new();
        IterBoxes(mov, 0, mov.Length, 0, string.Empty, all);

        foreach (Node n in all)
        {
            if (!n.Path.EndsWith("/trak", StringComparison.Ordinal))
            {
                continue;
            }

            string? handler = null;
            uint? timescale = null;
            ulong? duration = null;

            List<Node> inner = new();
            IterBoxes(mov, n.Offset + n.Header, n.Offset + n.Size, 0, string.Empty, inner);

            foreach (Node b in inner)
            {
                string leaf = b.Path[(b.Path.LastIndexOf('/') + 1)..];
                if (leaf == "hdlr" && handler is null)
                {
                    int at = b.Offset + b.Header + 8;
                    if (at + 4 <= mov.Length)
                    {
                        string v = System.Text.Encoding.Latin1.GetString(mov, at, 4);
                        if (v.Trim('\0').Trim().Length > 0)
                        {
                            handler = v;
                        }
                    }
                }
                else if (leaf == "mdhd" && timescale is null)
                {
                    int at = b.Offset + b.Header;
                    if (at + 20 > mov.Length)
                    {
                        continue;
                    }

                    byte ver = mov[at];
                    if (ver == 0)
                    {
                        timescale = ReadU32(mov, at + 12);
                        duration = ReadU32(mov, at + 16);
                    }
                    else if (at + 32 <= mov.Length)
                    {
                        timescale = ReadU32(mov, at + 20);
                        duration = ReadU64(mov, at + 24);
                    }
                }
            }

            if (handler is not null && handler.Trim() == "vide" && timescale is > 0 && duration is not null)
            {
                return (double)duration.Value / timescale.Value * 1000.0;
            }
        }

        return null;
    }

    /// <summary>MOV 是否以 ftyp 盒开头（决定第 8..12 字节是不是品牌字段）。</summary>
    public static bool StartsWithFtyp(byte[] mov) =>
        mov.Length >= 8 && mov[4] == 0x66 && mov[5] == 0x74 && mov[6] == 0x79 && mov[7] == 0x70;

    /// <summary>MIME 类型：<c>qt  </c> 品牌 → quicktime，其余 → mp4。</summary>
    public static string MimeOf(byte[] mov)    {
        if (mov.Length < 12)
        {
            return "video/mp4";
        }

        string brand = System.Text.Encoding.Latin1.GetString(mov, 8, 4);
        return brand == "qt  " ? "video/quicktime" : "video/mp4";
    }

    /// <summary>
    /// 取出 mdat box 的完整字节，用于修复前后逐字节比对。参考实现的 <c>mdat_of</c>。
    /// </summary>
    public static byte[] MdatOf(byte[] d)
    {
        int e = Jpeg.JpegReader.FindEoi(d);
        if (e < 0)
        {
            return Array.Empty<byte>();
        }

        int start = e + 2;

        // 必须和 FindVideoStart 一样全范围搜索：
        // EOI 与视频起点之间可能有厂商私有块（含第二张 MPF 小图），某些文件超过 140 KB ——
        // 若只在开头 4 KB 里找，会取不到 mdat，视频校验就此变成"空比空"的假通过。
        int f = FindVideoStart(d, start);
        if (f < 0)
        {
            return Array.Empty<byte>();
        }

        (int off, int len) = MdatRange(d, f, d.Length);
        return off < 0 ? Array.Empty<byte>() : Materialize(d, off, len);
    }

    private static byte[] Materialize(byte[] d, int off, int len)
    {
        byte[] r = new byte[len];
        Array.Copy(d, off, r, 0, len);
        return r;
    }

    /// <summary>
    /// 直接从 mov 缓冲区定位 mdat 盒，返回其字节的<b>零拷贝视图</b>（含盒头）。
    /// <para>
    /// 校验器用：src/dst 两侧都走这里，口径一致，且不必各拷一份 4 MB 级的载荷。
    /// 注意与 <see cref="MdatOf"/> 的区别 —— 那个要先找 EOI/视频起点，
    /// 传 JPEG 封面段给它是什么都取不到的（EOI 之后没有字节），
    /// 校验会退化成恒真，所以校验路径必须用这个重载。
    /// </para>
    /// </summary>
    public static ReadOnlySpan<byte> MdatSpanOf(byte[] mov)
    {
        (int off, int len) = MdatRange(mov, 0, mov.Length);
        return off < 0 ? default : mov.AsSpan(off, len);
    }

    /// <summary>在 [start, end) 范围的顶层盒链里定位 mdat，返回 (盒起始, 盒长)；找不到返回 (-1, 0)。</summary>
    private static (int Offset, int Length) MdatRange(byte[] d, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            uint sz = ReadU32(d, p);
            string t = BoxType(d, p);
            if (sz == 1)
            {
                if (p + 16 > end)
                {
                    break;
                }

                sz = (uint)ReadU64(d, p + 8);
            }
            else if (sz == 0)
            {
                sz = (uint)(end - p);
            }

            if (sz < 8 || p + sz > end)
            {
                break;
            }

            if (t == "mdat")
            {
                return (p, (int)sz);
            }

            p += (int)sz;
        }

        return (-1, 0);
    }

    private static void IterBoxes(byte[] buf, int start, int end, int depth, string path, List<Node> outList)
    {
        int p = start;
        while (p < end - 7)
        {
            uint sz = ReadU32(buf, p);
            string tp = BoxType(buf, p);
            int hdr = 8;
            if (sz == 1)
            {
                if (p + 16 > end)
                {
                    return;
                }

                sz = (uint)ReadU64(buf, p + 8);
                hdr = 16;
            }
            else if (sz == 0)
            {
                sz = (uint)(end - p);
            }

            if (sz < hdr || p + sz > end)
            {
                return;
            }

            string cur = path + "/" + tp;
            outList.Add(new Node(depth, cur, p, (int)sz, hdr));

            if (Array.IndexOf(Containers, tp) >= 0)
            {
                IterBoxes(buf, p + hdr + (tp == "meta" ? 4 : 0), p + (int)sz, depth + 1, cur, outList);
            }

            p += (int)sz;
        }
    }

    private static string BoxType(byte[] b, int off) =>
        System.Text.Encoding.Latin1.GetString(b, off + 4, 4);

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
}

/// <summary>顶层 box。</summary>
public readonly record struct Box(string Type, int Offset, int Size);

/// <summary>递归遍历时的 box 节点（带层级路径）。</summary>
public readonly record struct Node(int Depth, string Path, int Offset, int Size, int Header);

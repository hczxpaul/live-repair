namespace LivePhoto.Core.Jpeg;

/// <summary>
/// JPEG 字节流的结构化改写。
/// <para>
/// 这里独占所有 JPEG 段级别的字节操作。<see cref="LivePhoto.Core.Repair.RepairEngine"/>
/// 只允许调用本类，**不允许**自己做偏移运算 —— 这条约束由架构测试守卫。
/// </para>
/// <para>铁律：除 XMP 段与 Exif 段外，不增加、不删除、不改动任何其他段（含 DRI/APP0/APP13/ICC）。</para>
/// </summary>
public static class JpegEditor
{
    private const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";

    /// <summary>构造一个 XMP APP1 段。参考实现的 <c>make_xmp_seg</c>。</summary>
    public static byte[] MakeXmpSegment(byte[] payload)
    {
        byte[] body = System.Text.Encoding.ASCII.GetBytes(XmpNamespace + "\0").Concat(payload).ToArray();
        byte[] seg = new byte[body.Length + 4];
        seg[0] = 0xFF;
        seg[1] = 0xE1;
        int segLen = body.Length + 2;
        seg[2] = (byte)((segLen >> 8) & 0xFF);
        seg[3] = (byte)(segLen & 0xFF);
        Array.Copy(body, 0, seg, 4, body.Length);
        return seg;
    }

    /// <summary>
    /// 删除全部旧 XMP 段，并在 Exif APP1 段之后插入新 XMP 段。
    /// 参考实现的 <c>replace_xmp</c>。
    /// </summary>
    public static byte[] ReplaceXmp(byte[] jpg, byte[] payload)
    {
        byte[] seg = MakeXmpSegment(payload);

        // 第一步：删掉所有旧 XMP 段
        List<byte> stripped = new(jpg.Length);
        int prev = 0;
        foreach (JpegSegment s in JpegReader.ReadSegments(jpg))
        {
            if (s.Marker == JpegReader.MarkerApp1 && IsXmpPayload(jpg, s))
            {
                for (int k = prev; k < s.Start; k++)
                {
                    stripped.Add(jpg[k]);
                }

                prev = s.End;
            }
        }

        for (int k = prev; k < jpg.Length; k++)
        {
            stripped.Add(jpg[k]);
        }

        byte[] work = stripped.ToArray();

        // 第二步：在 Exif APP1 之后插入
        foreach (JpegSegment s in JpegReader.ReadSegments(work))
        {
            if (s.Marker == JpegReader.MarkerApp1 && IsExifPayload(work, s))
            {
                return Splice(work, s.End, seg);
            }
        }

        // 没有 Exif APP1 时降级：插到 SOI 之后
        return Splice(work, 2, seg);
    }

    /// <summary>
    /// 用新的 TIFF 替换第一个 Exif APP1 段。参考实现的 <c>replace_exif</c>。
    /// </summary>
    public static byte[] ReplaceExif(byte[] jpg, byte[] newTiff)
    {
        foreach (JpegSegment s in JpegReader.ReadSegments(jpg))
        {
            if (s.Marker != JpegReader.MarkerApp1 || !IsExifPayload(jpg, s))
            {
                continue;
            }

            byte[] body = new byte[6 + newTiff.Length];
            body[0] = 0x45;
            body[1] = 0x78;
            body[2] = 0x69;
            body[3] = 0x66;
            body[4] = 0x00;
            body[5] = 0x00;
            Array.Copy(newTiff, 0, body, 6, newTiff.Length);

            byte[] seg = new byte[body.Length + 4];
            seg[0] = 0xFF;
            seg[1] = 0xE1;
            int segLen = body.Length + 2;
            seg[2] = (byte)((segLen >> 8) & 0xFF);
            seg[3] = (byte)(segLen & 0xFF);
            Array.Copy(body, 0, seg, 4, body.Length);

            byte[] outBuf = new byte[jpg.Length - (s.End - s.Start) + seg.Length];
            Array.Copy(jpg, 0, outBuf, 0, s.Start);
            Array.Copy(seg, 0, outBuf, s.Start, seg.Length);
            Array.Copy(jpg, s.End, outBuf, s.Start + seg.Length, jpg.Length - s.End);
            return outBuf;
        }

        throw new JpegFormatException("找不到 Exif APP1 段");
    }

    private static byte[] Splice(byte[] src, int at, byte[] insert)
    {
        byte[] outBuf = new byte[src.Length + insert.Length];
        Array.Copy(src, 0, outBuf, 0, at);
        Array.Copy(insert, 0, outBuf, at, insert.Length);
        Array.Copy(src, at, outBuf, at + insert.Length, src.Length - at);
        return outBuf;
    }

    private static bool IsExifPayload(byte[] d, JpegSegment s) =>
        s.PayloadStart + 6 <= d.Length &&
        d[s.PayloadStart] == 0x45 && d[s.PayloadStart + 1] == 0x78 &&
        d[s.PayloadStart + 2] == 0x69 && d[s.PayloadStart + 3] == 0x66 &&
        d[s.PayloadStart + 4] == 0x00 && d[s.PayloadStart + 5] == 0x00;

    private static bool IsXmpPayload(byte[] d, JpegSegment s) =>
        s.PayloadStart + 4 <= d.Length &&
        d[s.PayloadStart] == 0x68 && d[s.PayloadStart + 1] == 0x74 &&
        d[s.PayloadStart + 2] == 0x74 && d[s.PayloadStart + 3] == 0x70;
}

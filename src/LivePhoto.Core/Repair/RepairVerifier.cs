using LivePhoto.Core.Exif;
using LivePhoto.Core.Jpeg;
using LivePhoto.Core.Mp4;
using System.Text.RegularExpressions;

namespace LivePhoto.Core.Repair;

/// <summary>单项校验结果。</summary>
public sealed record CheckItem(string Name, bool Passed, string Detail);

/// <summary>校验报告。<b>校验失败不抛异常</b>，由调用方决定是否落盘。</summary>
public sealed record VerifyReport(bool Passed, IReadOnlyList<CheckItem> Items)
{
    public IEnumerable<CheckItem> Failures => Items.Where(i => !i.Passed);
}

/// <summary>
/// 写出前的强制校验（T1 差分不变式）。
/// <para>
/// 每一项都是"相对本文件自己的源文件"做差分，<b>不需要预先知道任何答案</b>，
/// 因此天然适用于任意一张照片 —— 这正是不能靠固定哈希做验收的原因。
/// </para>
/// </summary>
public static class RepairVerifier
{
    /// <summary>封面像素比对的解码宽度。0 = 全分辨率（旧行为）。</summary>
    private const int PixelCompareWidth = 640;

    /// <summary>
    /// 校验。<paramref name="decoder"/> 为 null 时像素比对跳过，由"熵数据逐字节"项兜底
    /// （后者不需要解码器，且是等价强度的保证）。
    /// </summary>
    public static VerifyReport Verify(
        byte[] srcJpg, byte[] srcBlk, byte[] srcMov,
        byte[] dstJpg, byte[] dstBlk, byte[] dstMov,
        IPixelDecoder? decoder)
    {
        List<CheckItem> items = new();
        bool all = true;

        void Add(string name, bool ok, string detail)
        {
            all &= ok;
            items.Add(new CheckItem(name, ok, detail));
        }

        // 1 封面像素
        if (decoder is null)
        {
            Add("封面像素", true, "未提供解码器，已跳过（由「熵数据逐字节」项兜底）");
        }
        else
        {
            // 640px 降采样比对（2026-10-17，见《优化计划》A 项）：
            // WIC 在任何缩放等级都完整解析段头与全部熵数据，"解不开"照样失败；
            // 而"解码结果相同"已由第 13 项（除 Exif/XMP 外逐字节一致）数学保证。
            // 全分辨率解码每张多花 ~200 ms 与 ~90 MB，买不到额外保证。
            bool ok = decoder.ArePixelsIdentical(srcJpg, dstJpg, out int w, out int h, PixelCompareWidth);
            Add("封面像素", ok, $"{w}x{h} 降采样逐点比对");
        }

        // 2 视频 mdat 逐字节。
        //    必须从 srcMov/dstMov 取 —— 传 JPEG 封面段的话 EOI 之后没有字节可扫，
        //    两侧都会是空，这项就恒真了（2026-10-17 修：原来正是如此）。
        //    用 span 视图比对，不拷贝 4 MB 级的载荷。
        ReadOnlySpan<byte> srcMdat = Mp4Inspector.MdatSpanOf(srcMov);
        ReadOnlySpan<byte> dstMdat = Mp4Inspector.MdatSpanOf(dstMov);
        if (srcMdat.Length == 0 && dstMdat.Length == 0)
        {
            Add("视频 mdat", true, "两侧均无 mdat");
        }
        else
        {
            Add("视频 mdat", srcMdat.SequenceEqual(dstMdat), $"{dstMdat.Length} B 逐字节比对");
        }

        // 3 视频容器头：源有 ftyp 时必须原样；源缺 ftyp 时允许（且要求）补回标准头。
        //    （改了 stco 偏移会全错 → 能识别不能播，所以这里是硬校验而不是提示。）
        bool srcHasFtyp = Mp4Inspector.StartsWithFtyp(srcMov);
        bool dstHasFtyp = Mp4Inspector.StartsWithFtyp(dstMov);
        bool headOk;
        string headDetail;
        if (srcHasFtyp)
        {
            int headLen = Math.Min(16, Math.Min(srcMov.Length, dstMov.Length));
            headOk = headLen >= 16 && srcMov.AsSpan(0, 16).SequenceEqual(dstMov.AsSpan(0, 16));
            headDetail = dstMov.Length >= 12 ? System.Text.Encoding.Latin1.GetString(dstMov, 8, 4) : "无";
        }
        else
        {
            int shift = Mp4ContainerRepair.FtypSize;
            headOk = dstHasFtyp && dstMov.Length == srcMov.Length + shift;
            headDetail = headOk
                ? "源缺 ftyp → 已补回标准 QuickTime ftyp"
                : $"源缺 ftyp，但成品头不合法（{dstMov.Length} B vs 源 {srcMov.Length} B + {shift}）";
        }

        Add("视频容器头", headOk, headDetail);

        // 3b 补全可逆（仅补全时）：去掉 ftyp 并把偏移还原后，必须与源逐字节相同 ——
        //    证明补全除了 ftyp 与那些偏移之外<b>没有动过任何字节</b>。
        if (!srcHasFtyp && dstHasFtyp)
        {
            byte[] roundTrip = Mp4ContainerRepair.RemoveFtyp(dstMov);
            bool revOk = roundTrip.Length == srcMov.Length && roundTrip.AsSpan().SequenceEqual(srcMov);
            Add("补全可逆", revOk,
                revOk ? "去掉 ftyp 并还原偏移后与源逐字节相同"
                      : $"逆运算 {roundTrip.Length} B vs 源 {srcMov.Length} B");
        }

        // 3c chunk 偏移必须落在 mdat 数据区内 —— 这是"漏平移"的唯一可靠抓手：
        //    偏移少加 20 就会指到容器头，播放器能识别却读不出数据。
        (bool offOk, string offDetail) = ChunkOffsetsInsideMdat(dstMov);
        Add("chunk 偏移", offOk, offDetail);

        // 段表只扫一次：④/⑤/⑬/⑭ 四项共用（原先每项各自从 SOI 重扫到 SOS，
        // 一次 Verify 要扫 9 遍）。src/dst 分别容错 —— 与原先"各自 catch"的行为一致；
        // src 解析失败在实践中不可达（分析与修复前都已扫过同一缓冲区），
        // 真出现时按"校验失败"处理而不是放过。
        List<JpegSegment>? srcSegs = SafeSegments(srcJpg, out string? srcSegsErr);
        List<JpegSegment>? dstSegs = SafeSegments(dstJpg, out string? dstSegsErr);

        // 4 JPEG 段长度自洽
        bool segOk = true;
        int segCount = 0;
        if (dstSegs is null)
        {
            segOk = false;
            items.Add(new CheckItem("JPEG 段自洽", false, dstSegsErr ?? "段解析失败"));
            all = false;
        }
        else
        {
            segCount = dstSegs.Count;
            foreach (JpegSegment s in dstSegs)
            {
                if (s.Start < 0 || s.End > dstJpg.Length || s.End <= s.Start)
                {
                    segOk = false;
                    break;
                }
            }
        }

        if (segOk)
        {
            Add("JPEG 段自洽", true, $"{segCount} 个段");
        }

        // 5 DRI 保留（丢失则含 RSTn 的封面会出条纹）
        bool srcDri = HasDri(srcSegs);
        bool dstDri = HasDri(dstSegs);
        Add("DRI 保留", !srcDri || dstDri, srcDri ? "源有 DRI → 成品保留" : "源本来无 DRI");

        // 6 MP4 box 链
        (_, int cov) = Mp4Inspector.BoxesOf(dstMov);
        long stray = dstMov.Length - cov;
        Add("box 链", true,
            $"覆盖 {cov}/{dstMov.Length}" + (stray > 0 ? $"（尾部 {stray}B 为原文件保留的杂散字节）" : "（完整）"));

        // 7 Item:Length 精确
        string? xmpText = JpegReader.ReadXmpText(dstJpg);
        long declared = -1;
        if (xmpText is not null)
        {
            Match m = Regex.Match(xmpText,
                "Item:Semantic=\"MotionPhoto\".*?Item:Length=\"(\\d+)\"", RegexOptions.Singleline);
            if (m.Success)
            {
                declared = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        Add("Item:Length", declared == dstMov.Length, $"{declared} vs 实际 {dstMov.Length}");

        // 8 XMP 纯净
        bool pure = xmpText is not null &&
                    xmpText.Contains("GCamera:MotionPhoto", StringComparison.Ordinal) &&
                    !xmpText.Contains("VCamera", StringComparison.Ordinal);
        Add("XMP 纯净", pure, "仅 Google 标准命名空间");

        // 9 XMP 细节
        Match tk = xmpText is not null
            ? Regex.Match(xmpText, "x:xmptk=\"([^\"]*)\"")
            : Match.Empty;
        string tkv = tk.Success ? tk.Groups[1].Value : "缺失";
        bool tkOk = tk.Success && !tkv.Contains("WorkBuddy", StringComparison.Ordinal);
        bool padOk = xmpText is not null && Regex.IsMatch(xmpText,
            "Item:Semantic=\"MotionPhoto\".*?Item:Padding=\"0\"", RegexOptions.Singleline);
        Add("XMP 细节", tkOk && padOk, $"xmptk={tkv} MotionPhoto Padding={(padOk ? "有" : "缺")}");

        // EXIF 只解析 src/dst 各一次 —— MakerNote / 设备时间 / GPS 三项共用。
        // （原先每项各自 Parse 一遍，合计 5 次同样的 ~23 KB TIFF，纯浪费。）
        Tiff? srcTiff = ParseOf(srcJpg);
        Tiff? dstTiff = ParseOf(dstJpg);

        // 10 MakerNote 不再含 Apple
        byte[]? dstMn = dstTiff is null ? null : ExifAccess.GetMakerNote(dstTiff);
        bool mnOk = dstMn is null || dstMn.Length < 5 ||
                    System.Text.Encoding.ASCII.GetString(dstMn, 0, 5) != "Apple";
        Add("MakerNote", mnOk, dstMn is null ? "已删除" : $"{dstMn.Length}B {Convert.ToHexString(dstMn[..Math.Min(8, dstMn.Length)])}");

        // 11 设备/时间/曝光一致
        List<string> diffs = ExifKeyDiffs(srcTiff, dstTiff);
        Add("设备/时间/曝光", diffs.Count == 0,
            diffs.Count == 0 ? "全部一致" : "变动项: " + string.Join(", ", diffs));

        // 12 GPS 一致
        Add("GPS", GpsEqual(srcTiff, dstTiff), "GPS 子 IFD 全标签比对");

        // 13（加强项）除 Exif/XMP 外所有 JPEG 字节逐字节一致
        if (srcSegs is null || dstSegs is null)
        {
            // 段表扫不出来时按失败处理（不放任"空比空"的假通过）
            Add("其余段与熵数据", false, $"段解析失败（{srcSegsErr ?? dstSegsErr}）");
        }
        else
        {
            byte[] srcRest = StripExifAndXmp(srcJpg, srcSegs);
            byte[] dstRest = StripExifAndXmp(dstJpg, dstSegs);
            Add("其余段与熵数据", srcRest.AsSpan().SequenceEqual(dstRest),
                $"{dstRest.Length} B 逐字节比对（不含 Exif/XMP）");
        }

        // 14（加强项）段类型不得减少（删除预算）
        string? lost = LostSegmentTypes(srcSegs, dstSegs);
        Add("段类型无丢失", lost is null, lost ?? "无丢失");

        // 15 blk 区域必须原样
        Add("EOI 后私有块", srcBlk.AsSpan().SequenceEqual(dstBlk), $"{dstBlk.Length} B 原样");

        return new VerifyReport(all, items);
    }

    /// <summary>
    /// 所有 chunk 偏移是否落在 mdat 的数据区内。
    /// <para>
    /// 补 ftyp 是「插头 + 平移索引」的成对动作，只做一半（插了头、漏了平移）正是
    /// 「能识别成实况照片、但视频播放失败」的成因；偏移少加 20 就会指到容器头，
    /// 所以这一项按硬校验处理。没有 mdat 或没有索引表时放行（超出本项职责）。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ChunkOffsetsInsideMdat(byte[] mov)
    {
        long payloadStart = -1;
        long payloadEnd = -1;
        foreach (Box b in Mp4Inspector.BoxesOf(mov).Boxes)
        {
            if (b.Type == "mdat")
            {
                uint first = ((uint)mov[b.Offset] << 24) | ((uint)mov[b.Offset + 1] << 16) |
                             ((uint)mov[b.Offset + 2] << 8) | mov[b.Offset + 3];
                int hdr = first == 1 ? 16 : 8;
                payloadStart = b.Offset + hdr;
                payloadEnd = b.Offset + (long)b.Size;
                break;
            }
        }

        if (payloadStart < 0)
        {
            return (true, "无 mdat，跳过");
        }

        IReadOnlyList<long> offsets = Mp4ContainerRepair.CollectChunkOffsets(mov);
        if (offsets.Count == 0)
        {
            return (true, "无 stco/co64 表，跳过");
        }

        long lo = long.MaxValue;
        long hi = long.MinValue;
        int bad = 0;
        foreach (long o in offsets)
        {
            lo = Math.Min(lo, o);
            hi = Math.Max(hi, o);
            if (o < payloadStart || o >= payloadEnd)
            {
                bad++;
            }
        }

        return bad == 0
            ? (true, $"{offsets.Count} 项全部落在 mdat 数据区 [{payloadStart}, {payloadEnd})，区间 [{lo}, {hi}]")
            : (false, $"{bad}/{offsets.Count} 项越界（mdat 数据区 [{payloadStart}, {payloadEnd})，实际 [{lo}, {hi}]）");
    }

    /// <summary>扫段表；失败返回 null 并把原因写进 <paramref name="error"/>（不抛出）。</summary>
    private static List<JpegSegment>? SafeSegments(byte[] jpg, out string? error)
    {
        try
        {
            error = null;
            return JpegReader.ReadSegments(jpg);
        }
        catch (JpegFormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool HasDri(List<JpegSegment>? segs) =>
        segs is not null && segs.Any(s => s.Marker == JpegReader.MarkerDri);

    private static Tiff? ParseOf(byte[] jpg)
    {
        JpegSegment? seg = JpegReader.FindExifApp1(jpg);
        if (seg is null)
        {
            return null;
        }

        try
        {
            JpegSegment s = seg.Value;
            byte[] raw = new byte[s.End - (s.PayloadStart + 6)];
            Array.Copy(jpg, s.PayloadStart + 6, raw, 0, raw.Length);
            return TiffCodec.Parse(raw);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly ushort[] ExifKeyTags =
    {
        0x829A, 0x829D, 0x8827, 0x920A, 0x9209, 0x9204, 0x9003, 0x9004,
    };

    // IFD0 关键标签。0x0112（Orientation）是 2026-10-17 补的：实测 WPF 解码不套用
    // EXIF 旋转，像素比对对朝向篡改返回"相同"，朝向原先没有任何校验守护；
    // 而修复的 TIFF 往返实测保持该标签原值（6→6），补进来只会拦住"真被改错"的情况。
    private static readonly ushort[] Ifd0KeyTags = { 0x010F, 0x0110, 0x0112, 0x0131, 0x0132 };

    private static List<string> ExifKeyDiffs(Tiff? a, Tiff? b)
    {
        List<string> diffs = new();
        if (a is null || b is null)
        {
            diffs.Add("EXIF 无法解析");
            return diffs;
        }

        foreach (ushort tag in Ifd0KeyTags)
        {
            if (!EntryEqual(ExifAccess.Get(a.Ifd0, tag), ExifAccess.Get(b.Ifd0, tag)))
            {
                diffs.Add($"0x{tag:X4}");
            }
        }

        foreach (ushort tag in ExifKeyTags)
        {
            if (!EntryEqual(ExifAccess.Get(a.Exif, tag), ExifAccess.Get(b.Exif, tag)))
            {
                diffs.Add($"0x{tag:X4}");
            }
        }

        return diffs;
    }

    private static bool EntryEqual(Entry? a, Entry? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.Type == b.Type && a.Count == b.Count && a.Data.AsSpan().SequenceEqual(b.Data);
    }

    private static bool GpsEqual(Tiff? a, Tiff? b)
    {
        if (a?.Gps is null && b?.Gps is null)
        {
            return true;
        }

        if (a?.Gps is null || b?.Gps is null)
        {
            return false;
        }

        if (a.Gps.Count != b.Gps.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Gps.Count; i++)
        {
            if (!EntryEqual(a.Gps[i], b.Gps[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 去掉 Exif 与 XMP 段后的全部字节（含 SOS 之后的熵数据）。
    /// 源与成品在这部分必须<b>逐字节相同</b> —— 这比"像素比对"更强，
    /// 且不需要任何解码器。
    /// </summary>
    private static byte[] StripExifAndXmp(byte[] jpg, List<JpegSegment> segs)
    {
        int eoi = JpegReader.FindEoi(jpg, segs);

        // 第一遍：定尺寸 + 记录 SOS 位置
        int total = 0;
        JpegSegment? sos = null;
        foreach (JpegSegment s in segs)
        {
            if (IsExifOrXmpApp1(jpg, s))
            {
                continue;
            }

            if (s.Marker == JpegReader.MarkerSos)
            {
                sos = s;
            }

            total += s.End - s.Start;
        }

        int entropyFrom = -1;
        int entropyTo = -1;
        if (sos is not null && eoi > sos.Value.End)
        {
            entropyFrom = sos.Value.End;
            entropyTo = Math.Min(eoi + 2, jpg.Length);
            total += entropyTo - entropyFrom;
        }

        // 第二遍：整段拷贝。段与段首尾相接，剥掉 Exif/XMP 后剩下的是几块连续区间，
        // 逐字节 Add 又慢又反复扩容（封面 4 MB 级时是纯浪费）——这里一次分配 + Array.Copy，
        // 产出字节与原实现完全一致（同样的段、同样的跳过判据、同样的熵数据区间）。
        byte[] buf = new byte[total];
        int w = 0;
        foreach (JpegSegment s in segs)
        {
            if (IsExifOrXmpApp1(jpg, s))
            {
                continue;
            }

            Array.Copy(jpg, s.Start, buf, w, s.End - s.Start);
            w += s.End - s.Start;
        }

        if (entropyFrom >= 0)
        {
            Array.Copy(jpg, entropyFrom, buf, w, entropyTo - entropyFrom);
        }

        return buf;
    }

    /// <summary>是否是要剥掉的 Exif / XMP APP1 段（判据与历史实现逐字节一致）。</summary>
    private static bool IsExifOrXmpApp1(byte[] jpg, JpegSegment s)
    {
        if (s.Marker != JpegReader.MarkerApp1)
        {
            return false;
        }

        bool isExif = s.PayloadStart + 6 <= jpg.Length &&
                      jpg[s.PayloadStart] == 0x45 && jpg[s.PayloadStart + 1] == 0x78 &&
                      jpg[s.PayloadStart + 2] == 0x69 && jpg[s.PayloadStart + 3] == 0x66;
        bool isXmp = s.PayloadStart + 4 <= jpg.Length &&
                     jpg[s.PayloadStart] == 0x68 && jpg[s.PayloadStart + 1] == 0x74 &&
                     jpg[s.PayloadStart + 2] == 0x74 && jpg[s.PayloadStart + 3] == 0x70;
        return isExif || isXmp;
    }

    private static string? LostSegmentTypes(List<JpegSegment>? a, List<JpegSegment>? b)
    {
        if (a is null || b is null)
        {
            return "段解析失败";
        }

        HashSet<int> sa = a.Select(s => s.Marker).ToHashSet();
        HashSet<int> sb = b.Select(s => s.Marker).ToHashSet();

        sa.ExceptWith(sb);
        return sa.Count == 0 ? null : string.Join(", ", sa.Select(m => $"FF{m:X2}"));
    }
}

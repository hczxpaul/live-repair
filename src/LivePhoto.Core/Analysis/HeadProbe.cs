using System.Text;
using System.Text.RegularExpressions;
using LivePhoto.Core.Exif;
using LivePhoto.Core.Jpeg;

namespace LivePhoto.Core.Analysis;

/// <summary>
/// 头部探测结果 —— 只读前 <see cref="HeadProbe.HeadBytes"/> 就能拿到的东西。
/// <para>
/// 刻意<b>不含</b>任何需要通读文件的字段（有没有内嵌视频、视频多长），
/// 那些属于 T2 全量分析。两者分开是三级漏斗能省 I/O 的前提。
/// </para>
/// </summary>
public sealed record HeadInfo(
    string Path,
    string FileName,
    long Size,
    bool Ok,
    string? Error,
    string? Make,
    string? Model,
    string? Software,
    string? DateTimeOriginal,
    bool HasXmp,
    bool HasGoogleXmp,
    int MakerNoteLength,
    bool MakerNoteIsApple,
    /// <summary>
    /// 内嵌视频切片是否以 <c>ftyp</c> 盒开头（＝容器完整）。<b>null 表示判断不出来</b>。
    /// <para>
    /// 用途只有一个：把「已合规」和「需补容器」分开。
    /// 这两类文件在头部区域<b>完全一样</b>（都带 Google 声明、都看不出视频），
    /// 唯一区别在于视频切片有没有容器头。靠 <c>Item:Length</c> 算出切片起点后，
    /// 在那个偏移读 32 字节即可判定 —— 代价约占整读的 0.8%（2026-10-18 实测）。
    /// </para>
    /// <para>
    /// <b>判不出来时必须保守放行</b>：把它当成"可能要修"，照常进 T2。
    /// 这一路只是省一次全量读的手段，绝不能成为漏掉待修复文件的理由。
    /// </para>
    /// </summary>
    bool? VideoStartsWithFtyp = null);

/// <summary>
/// T1 头部探测：<b>只读文件开头一小段</b>，代价与文件大小无关。
/// <para>
/// 实测：19 张真实样本，读前 4 KB 就能 19/19 拿到设备名；
/// 这里取 64 KB 是为了兼容不同厂商把 APP1 放得更远的情况（EXIF 段最大到 23 KB）。
/// 远程盘上成本主要是网络往返而非字节数，所以这个余量几乎不花钱。
/// </para>
/// <para>
/// 探测失败只表示"这批信息没拿到"，<b>不代表文件有问题</b> ——
/// 调用方必须让探测失败的文件照常进入 T2，绝不能因此把它筛掉。
/// </para>
/// </summary>
public static class HeadProbe
{
    public const int HeadBytes = 65536;

    private const string GoogleMotionPhoto = "GCamera:MotionPhoto";

    private static readonly string[] ApplePrefix = { "Apple" };

    /// <summary>探测单个文件。任何异常都转成 <c>Ok=false</c>，不抛出。</summary>
    public static HeadInfo Probe(string path)
    {
        string name = Path.GetFileName(path);
        long size = 0;
        try
        {
            FileInfo fi = new(path);
            size = fi.Exists ? fi.Length : 0;
        }
        catch
        {
            // 大小拿不到不影响探测本身
        }

        try
        {
            // 头部和 32 字节的容器头共用同一个句柄 —— 远程盘上每开一次文件就是一次往返，
            // 能省一次就省一次。
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                      HeadBytes, FileOptions.SequentialScan);
            int n = (int)Math.Min(HeadBytes, fs.Length);
            byte[] head = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = fs.Read(head, got, n - got);
                if (r == 0)
                {
                    break;
                }

                got += r;
            }

            if (got < n)
            {
                Array.Resize(ref head, got);
            }

            HeadInfo info = Parse(path, name, size, head);

            // 「有 Google 声明」不等于「不需要修」——视频可能缺 ftyp 盒（ContainerFix）。
            // 二者在头部毫无区别，但 XMP 里写了视频长度 Item:Length，
            // 于是切片起点 = 文件大小 - 长度，在那儿读 32 字节看是不是 ftyp 即可。
            // 代价约为整读的 0.8%（2026-10-18 实测），换来的是「排除已合规」不再误杀待修文件。
            if (info.HasGoogleXmp)
            {
                long? declared = DeclaredVideoLength(head);
                if (declared is > 0)
                {
                    long start = size - declared.Value;
                    if (start >= 16 && start + 8 <= size)
                    {
                        info = info with { VideoStartsWithFtyp = ReadBoxType(fs, start, "ftyp") };
                    }
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            return Fail(path, name, size, $"读取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从头部 XMP 里读出实况视频的长度声明（<c>Item:Mime="video/mp4"</c> 那一项的 <c>Item:Length</c>）。
    /// 读不到返回 null（没有 XMP、声明格式不同……）。
    /// </summary>
    public static long? DeclaredVideoLength(byte[] head)
    {
        string? xmp;
        try
        {
            List<JpegSegment> segs = JpegReader.ReadSegmentsLenient(head);
            xmp = JpegReader.ReadXmpText(head, segs);
        }
        catch (Exception)
        {
            return null;
        }

        if (xmp is null)
        {
            return null;
        }

        Match m = Regex.Match(xmp,
            @"Item:Mime=""video/mp4""(?:(?!Item:Mime=)[\s\S])*?Item:Length=""(\d+)""",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!m.Success)
        {
            // 退化：容器目录里视频项通常排在第二位
            MatchCollection all = Regex.Matches(xmp, @"Item:Length=""(\d+)""");
            if (all.Count >= 2 && long.TryParse(all[^1].Groups[1].Value, out long v2))
            {
                return v2;
            }

            return null;
        }

        return long.TryParse(m.Groups[1].Value, out long v) ? v : null;
    }

    /// <summary>
    /// 在指定偏移读一个盒头，判断类型是否为 <paramref name="want"/>。
    /// 读不到 / 字节不足 / 盒头不自洽都返回 null（＝判断不出来，调用方必须保守处理）。
    /// </summary>
    public static bool? ReadBoxTypeIs(string path, long offset, string want)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32, FileOptions.None);
            return ReadBoxType(fs, offset, want);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? ReadBoxType(FileStream fs, long offset, string want)
    {
        try
        {
            if (offset + 8 > fs.Length)
            {
                return null;
            }

            fs.Seek(offset, SeekOrigin.Begin);
            byte[] box = new byte[32];
            int got = 0;
            while (got < box.Length)
            {
                int r = fs.Read(box, got, box.Length - got);
                if (r == 0)
                {
                    break;
                }

                got += r;
            }

            if (got < 8)
            {
                return null;
            }

            long sz = ((long)box[0] << 24) | ((long)box[1] << 16) | ((long)box[2] << 8) | box[3];
            string type = Encoding.Latin1.GetString(box, 4, 4);
            if (sz < 8 || sz > fs.Length - offset)
            {
                // 盒头不自洽 —— 偏移多半算错了，别下结论
                return null;
            }

            return type == want;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>对已读入内存的头部字节做解析（供测试复用）。</summary>
    public static HeadInfo Parse(string path, string name, long size, byte[] head)
    {
        try
        {
            // 容错扫描：T1 只有前 64 KB，遇到"下一段超出缓冲区"就此打住即可。
            // 荣耀等厂商会写 64 KB 级的 APP6~APP9，用严格版会让整张图探测失败。
            List<JpegSegment> segs = JpegReader.ReadSegmentsLenient(head);
            JpegSegment? exifSeg = JpegReader.FindExifApp1(head, segs);
            if (exifSeg is null)
            {
                return Fail(path, name, size, "头部内没有 Exif APP1 段");
            }

            JpegSegment s = exifSeg.Value;
            if (s.End > head.Length)
            {
                // EXIF 段被截断 —— 不猜，交给 T2 兜底
                return Fail(path, name, size, $"EXIF 段超出探测范围（{s.End} > {head.Length}）");
            }

            byte[] raw = new byte[s.End - (s.PayloadStart + 6)];
            Array.Copy(head, s.PayloadStart + 6, raw, 0, raw.Length);

            Tiff tiff = TiffCodec.Parse(raw);

            string? xmp = JpegReader.ReadXmpText(head, segs);
            bool hasXmp = xmp is not null;
            bool hasGoogleXmp = xmp is not null && xmp.Contains(GoogleMotionPhoto, StringComparison.Ordinal);

            byte[]? mn = ExifAccess.GetMakerNote(tiff);
            bool mnIsApple = mn is not null && mn.Length >= 5 &&
                             Encoding.ASCII.GetString(mn, 0, 5) == ApplePrefix[0];

            return new HeadInfo(
                path, name, size, true, null,
                ExifAccess.GetStr(tiff, Tiff.TagMake),
                ExifAccess.GetStr(tiff, Tiff.TagModel),
                ExifAccess.GetStr(tiff, Tiff.TagSoftware),
                ReadDateTime(tiff),
                hasXmp, hasGoogleXmp,
                mn?.Length ?? 0,
                mnIsApple);
        }
        catch (Exception ex)
        {
            return Fail(path, name, size, $"解析失败：{ex.Message}");
        }
    }

    private static string? ReadDateTime(Tiff tiff)
    {
        Entry? e = ExifAccess.GetInExif(tiff, Tiff.TagDateTimeOriginal)
                   ?? ExifAccess.Get(tiff.Ifd0, Tiff.TagDateTime);
        if (e is null || e.Data.Length == 0)
        {
            return null;
        }

        return Encoding.Latin1.GetString(e.Data).TrimEnd('\0');
    }

    private static HeadInfo Fail(string path, string name, long size, string why) =>
        new(path, name, size, false, why, null, null, null, null, false, false, 0, false);
}

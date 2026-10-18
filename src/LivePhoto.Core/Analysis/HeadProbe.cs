using System.Text;
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
    bool MakerNoteIsApple);

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

        byte[] head;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                      HeadBytes, FileOptions.SequentialScan);
            int n = (int)Math.Min(HeadBytes, fs.Length);
            head = new byte[n];
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
        }
        catch (Exception ex)
        {
            return Fail(path, name, size, $"读取失败：{ex.Message}");
        }

        return Parse(path, name, size, head);
    }

    /// <summary>对已读入内存的头部字节做解析（供测试复用）。</summary>
    public static HeadInfo Parse(string path, string name, long size, byte[] head)
    {
        try
        {
            List<JpegSegment> segs = JpegReader.ReadSegments(head);
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

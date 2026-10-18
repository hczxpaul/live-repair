using LivePhoto.Core.Exif;
using LivePhoto.Core.Jpeg;
using LivePhoto.Core.Mp4;
using System.Text;

namespace LivePhoto.Core.Analysis;

/// <summary>
/// 三级判定流水线：<b>无共享状态</b>，可任意并发调用。
/// <list type="number">
/// <item>零级：扩展名 + 文件名前缀（由调用方做，这里聚焦结构）</item>
/// <item>一级：JPEG 结构 / EXIF / 内嵌视频是否成立</item>
/// <item>二级：MakerNote 是否被 Apple 改写（核心判据）</item>
/// </list>
/// </summary>
public static class Analyzer
{
    private static readonly string[] ApplePrefix = { "Apple" };

    private const string GoogleMotionPhoto = "GCamera:MotionPhoto";

    /// <summary>分析一个文件。异常一律转为 <see cref="PhotoCondition"/>，不向上抛出。</summary>
    public static AnalysisResult Analyze(string path)
    {
        string name = Path.GetFileName(path);
        FileInfo fi = new(path);
        long size = fi.Exists ? fi.Length : 0;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif")
        {
            return Make(path, name, size, PhotoCondition.UnsupportedFormat, PairingStatus.None,
                "HEIC/HEIF 不在本方案的覆盖范围（当前仅支持 JPEG）");
        }

        byte[] data;
        try
        {
            // 1 MB 缓冲 + SequentialScan：远程盘（SMB）上小块读请求会放大往返成本，
            // 顺序大块读对 NAS 预读与机械盘顺序读都最友好。行为与 ReadAllBytes 一致。
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                      1 << 20, FileOptions.SequentialScan);
            data = new byte[fs.Length];
            fs.ReadExactly(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            return Make(path, name, size, PhotoCondition.BrokenJpeg, PairingStatus.None,
                $"读取失败：{ex.Message}");
        }

        return AnalyzeBytes(path, name, size, data);
    }

    /// <summary>对已读入内存的字节做分析（供测试与批量复用）。</summary>
    public static AnalysisResult AnalyzeBytes(string path, string name, long size, byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return Make(path, name, size, PhotoCondition.BrokenJpeg, PairingStatus.None, "不是 JPEG（缺少 SOI）");
        }

        List<JpegSegment> segs;
        int eoi;
        try
        {
            segs = JpegReader.ReadSegments(data);
            // 段表只扫一次，下面三处复用（原先各自内部都会重扫一遍 SOI→SOS）
            eoi = JpegReader.FindEoi(data, segs);
        }
        catch (JpegFormatException ex)
        {
            return Make(path, name, size, PhotoCondition.BrokenJpeg, PairingStatus.None, $"JPEG 结构损坏：{ex.Message}");
        }

        if (eoi < 0)
        {
            return Make(path, name, size, PhotoCondition.BrokenJpeg, PairingStatus.None, "找不到 EOI");
        }

        JpegSegment? exifSeg = JpegReader.FindExifApp1(data, segs);
        if (exifSeg is null)
        {
            return Make(path, name, size, PhotoCondition.BrokenExif, PairingStatus.None, "没有 Exif APP1 段");
        }

        Tiff? tiff;
        try
        {
            JpegSegment s = exifSeg.Value;
            byte[] raw = new byte[s.End - (s.PayloadStart + 6)];
            Array.Copy(data, s.PayloadStart + 6, raw, 0, raw.Length);
            tiff = TiffCodec.Parse(raw);
        }
        catch (Exception ex)
        {
            return Make(path, name, size, PhotoCondition.BrokenExif, PairingStatus.None, $"EXIF 解析失败：{ex.Message}");
        }

        string? xmp = JpegReader.ReadXmpText(data, segs);
        bool hasGoogleXmp = xmp is not null && xmp.Contains(GoogleMotionPhoto, StringComparison.Ordinal);

        // ---- 内嵌视频 ----
        // 直接在原数组上定位视频起点（FindVideoStart 的 offset 重载），
        // 不再整段拷一份 tail 再拷一份 mov —— 少一次 ~4~8 MB 拷贝，定位结果完全一致。
        int tailStart = eoi + 2;
        int ftyp = tailStart <= data.Length ? Mp4Inspector.FindVideoStart(data, tailStart) : -1;
        bool hasEmbedded = ftyp >= 0;
        long blkLen = hasEmbedded ? ftyp - tailStart : 0;
        byte[] mov = hasEmbedded ? data[ftyp..] : Array.Empty<byte>();

        // ---- 外部同名 MOV（Apple 原生配对形式）----
        string? dir = Path.GetDirectoryName(path);
        string baseName = Path.GetFileNameWithoutExtension(path);
        long? movSize = null;
        if (dir is not null)
        {
            foreach (string cand in new[] { baseName + ".mov", baseName + ".MOV" })
            {
                string p = Path.Combine(dir, cand);
                if (File.Exists(p))
                {
                    movSize = new FileInfo(p).Length;
                    break;
                }
            }
        }

        PairingStatus pairing = (hasEmbedded, movSize is not null) switch
        {
            (true, true) => PairingStatus.Both,
            (true, false) => PairingStatus.EmbeddedVideo,
            (false, true) => PairingStatus.ExternalMov,
            _ => PairingStatus.None,
        };

        byte[]? mn = ExifAccess.GetMakerNote(tiff);
        bool mnIsApple = mn is not null && mn.Length >= 5 &&
                         Encoding.ASCII.GetString(mn, 0, 5) == ApplePrefix[0];
        string? make = ExifAccess.GetStr(tiff, Tiff.TagMake);
        string? model = ExifAccess.GetStr(tiff, Tiff.TagModel);
        string? software = ExifAccess.GetStr(tiff, Tiff.TagSoftware);

        string? mime = hasEmbedded ? Mp4Inspector.MimeOf(mov) : null;
        // 只有 ftyp 开头的 MOV 第 8..12 字节才是品牌；wide 开头的老式布局那里是 mdat 尺寸，读了也是垃圾
        string? brand = hasEmbedded && mov.Length >= 12 && Mp4Inspector.StartsWithFtyp(mov)
            ? Encoding.Latin1.GetString(mov, 8, 4)
            : null;
        double? dur = hasEmbedded ? Mp4Inspector.DurationMs(mov) : null;
        long tailStray = 0;
        if (hasEmbedded)
        {
            tailStray = mov.Length - Mp4Inspector.BoxesOf(mov).Coverage;
        }

        // ---- 二级判定（顺序对齐已验证的 Python 判据）----
        PhotoCondition cond;
        string reason;
        if (mnIsApple && hasEmbedded)
        {
            cond = PhotoCondition.NeedFix;
            reason = "MakerNote 被 Apple 互传改写（绿联会去找同名外挂 MOV 而找不到）";
        }
        else if (hasGoogleXmp)
        {
            // 「自带标准声明」不等于「一定能播」：部分转换链路写上了 Google XMP，
            // 却在拼接视频时剥掉了 ftyp 盒 —— 播放器按声明切出视频后认不出容器类型。
            // 绿联上的表现正是「能识别成实况照片、但视频播放失败」，必须补容器而不是跳过。
            if (hasEmbedded && !Mp4Inspector.StartsWithFtyp(mov))
            {
                cond = PhotoCondition.ContainerFix;
                reason = "已有 Google 实况声明，但内嵌视频缺 ftyp 盒（播放器会拒绝解码）";
            }
            else
            {
                cond = PhotoCondition.AlreadyStandard;
                reason = "已有 Google 标准实况声明，无需处理";
            }
        }
        else if (!hasEmbedded && movSize is null)
        {
            cond = PhotoCondition.NotMotionPhoto;
            reason = "既无内嵌视频也无同名外挂 MOV，不是实况照片";
        }
        else if (!hasEmbedded && movSize is not null)
        {
            bool apple = make is not null && make.Equals("Apple", StringComparison.OrdinalIgnoreCase);
            cond = apple ? PhotoCondition.AppleNative : PhotoCondition.ExternalMovOnly;
            reason = apple
                ? "iPhone 原生 Live Photo，不是被破坏的文件"
                : "只有外部 MOV 配对，没有内嵌视频";
        }
        else
        {
            // 走到这里必然是：有内嵌视频、MakerNote 不是 Apple 改写值、也没有 Google 标准声明。
            // 这不是"存疑"而是明确的缺陷：相册/播放器缺少识别依据，会把它当成静态图片处理
            // —— 用户真机实测"没修之前被误认为静态图片、视频播不了"。
            // 修复动作也很确定：补一段标准实况声明（+1071 B），MakerNote 与视频都不动。
            //
            // 归为 MissingDeclaration 而不是 NeedFix：它同样是待修复（默认勾选、进入修复流），
            // 但成因与"被 Apple 改写"是两回事，必须分开标识 —— 用户要能一眼看出毛病在哪。
            cond = PhotoCondition.MissingDeclaration;
            reason = "缺少 Google 标准实况声明，相册会当成静态图片（视频播不出）→ 补声明即可，MakerNote 原样保留";
        }

        // 待修复的三种成因都要给出 MakerNote 的处置说明（分类不合并）
        bool willFix = cond is PhotoCondition.NeedFix or PhotoCondition.MissingDeclaration;
        string action = "无需处理";
        if (willFix)
        {
            // 只有 MakerNote 真是 Apple 改写值时，修复引擎才会动它；
            // 缺声明这种形态下 MakerNote 是空的或非 Apple 值，引擎会直接放行。
            // 文案必须如实反映，否则用户会以为要被删掉。
            action = !mnIsApple
                ? "保持原样（非 Apple 值，修复不动它）"
                : make is not null && make.StartsWith("vivo", StringComparison.OrdinalIgnoreCase)
                    ? "替换为 vivo 原生值（同源设备，非伪造）"
                    : "删除 MakerNote 标签";
        }

        Entry? dto = ExifAccess.GetInExif(tiff, Tiff.TagDateTimeOriginal);
        string? dt = null;
        if (dto is not null && dto.Data.Length > 0)
        {
            dt = Encoding.Latin1.GetString(dto.Data).TrimEnd('\0');
        }

        (int? w, int? h) = ReadSofSize(data, segs);

        return new AnalysisResult
        {
            Path = path,
            FileName = name,
            Size = size,
            Condition = cond,
            Pairing = pairing,
            Reason = reason,
            Make = make,
            Model = model,
            Software = software,
            MakerNoteLength = mn?.Length ?? 0,
            MakerNoteIsApple = mnIsApple,
            HasGoogleXmp = hasGoogleXmp,
            HasEmbeddedVideo = hasEmbedded,
            VideoLength = mov.Length,
            FtypBrand = brand,
            DurationMs = dur,
            Mime = mime,
            BlkLength = blkLen,
            TailStray = tailStray,
            ExternalMovSize = movSize,
            MakerNoteAction = action,
            DateTimeOriginal = dt,
            Width = w,
            Height = h,
        };
    }

    private static (int? W, int? H) ReadSofSize(byte[] d, List<JpegSegment> segs)
    {
        foreach (JpegSegment s in segs)
        {
            if (s.Marker is < 0xC0 or > 0xCF)
            {
                continue;
            }

            if (s.Marker is 0xC4 or 0xC8 or 0xCC)
            {
                continue;
            }

            int p = s.PayloadStart + 1;   // 跳过精度
            if (p + 4 > d.Length)
            {
                return (null, null);
            }

            int h = (d[p] << 8) | d[p + 1];
            int w = (d[p + 2] << 8) | d[p + 3];
            return (w, h);
        }

        return (null, null);
    }

    private static AnalysisResult Make(string path, string name, long size, PhotoCondition c, PairingStatus p, string why) =>
        new()
        {
            Path = path,
            FileName = name,
            Size = size,
            Condition = c,
            Pairing = p,
            Reason = why,
            MakerNoteAction = "无需处理",
        };
}

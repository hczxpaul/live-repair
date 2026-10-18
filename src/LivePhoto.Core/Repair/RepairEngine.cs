using LivePhoto.Core.Exif;
using LivePhoto.Core.Jpeg;
using LivePhoto.Core.Xmp;
using System.Text;

namespace LivePhoto.Core.Repair;

/// <summary>
/// 修复编排。
/// <para>
/// <b>本类只做"决定改什么、按什么顺序"，不碰任何字节偏移</b> ——
/// 段编辑归 <see cref="JpegEditor"/>，TIFF 归 <see cref="TiffCodec"/>，XMP 归 <see cref="GoogleXmpBuilder"/>。
/// 这条边界由架构测试守卫：本类不得出现位移运算或 XML 字符串拼接。
/// </para>
/// </summary>
public static class RepairEngine
{
    /// <summary>vivo 原生 MakerNote 值（2 字节）。</summary>
    public static readonly byte[] VivoMakerNote = { 0x30, 0x00 };

    /// <summary>修复 JPEG 部分（不含视频）。返回新的 JPEG 字节（含 EOI）。</summary>
    public static byte[] RepairJpegPart(
        byte[] jpgPart,
        long videoLength,
        string mime,
        double? durationMs,
        MakerNotePolicy policy,
        out List<string> notes)
    {
        notes = new List<string>();

        long tsUs = durationMs.HasValue ? (long)(durationMs.Value * 1000.0 / 2.0) : -1;
        byte[] xmp = GoogleXmpBuilder.Build(videoLength, tsUs, mime);

        // 顺序对齐已验证实现：先换 XMP，再换 Exif
        byte[] withXmp = JpegEditor.ReplaceXmp(jpgPart, xmp);
        notes.Add($"写入 Google Motion Photo 1.0 XMP（Item:Length={videoLength} 精确）");

        JpegSegment? seg = JpegReader.FindExifApp1(withXmp);
        if (seg is null)
        {
            return withXmp;
        }

        JpegSegment s = seg.Value;
        byte[] tiffRaw = new byte[s.End - (s.PayloadStart + 6)];
        Array.Copy(withXmp, s.PayloadStart + 6, tiffRaw, 0, tiffRaw.Length);

        Tiff t = TiffCodec.Parse(tiffRaw);
        byte[]? mn = ExifAccess.GetMakerNote(t);

        bool isApple = mn is not null && mn.Length >= 5 &&
                       Encoding.ASCII.GetString(mn, 0, 5) == "Apple";

        if (!isApple)
        {
            return withXmp;
        }

        string make = ExifAccess.GetStr(t, Tiff.TagMake) ?? string.Empty;
        bool sameVendor = make.StartsWith("vivo", StringComparison.OrdinalIgnoreCase);

        switch (policy)
        {
            case MakerNotePolicy.Keep:
                notes.Add("保留 MakerNote（按策略不动）");
                return withXmp;

            case MakerNotePolicy.AlwaysDelete:
                ExifAccess.Del(t.Exif!, Tiff.TagMakerNote);
                notes.Add($"删除 Apple MakerNote 标签（{mn!.Length}B）");
                break;

            default:
                if (sameVendor)
                {
                    // 同源设备：写回该厂商自己的原生值，不构成伪造
                    ExifAccess.SetUndef(t.Exif!, Tiff.TagMakerNote, VivoMakerNote, 2);
                    notes.Add($"Apple MakerNote（{mn!.Length}B）→ vivo 原生 30 00（ASCII/2B）");
                }
                else
                {
                    // 不同源：绝不把 vivo 的值写到别家设备上，直接删标签
                    ExifAccess.Del(t.Exif!, Tiff.TagMakerNote);
                    notes.Add($"删除 Apple MakerNote 标签（{mn!.Length}B，非 vivo 设备，不做厂商替换）");
                }

                break;
        }

        return JpegEditor.ReplaceExif(withXmp, TiffCodec.Serialize(t));
    }

    /// <summary>计算 PresentationTimestampUs（规范建议值 = 时长的一半）。</summary>
    public static long TimestampUs(double? durationMs) =>
        durationMs.HasValue ? (long)(durationMs.Value * 1000.0 / 2.0) : -1;
}

/// <summary>修复选项。</summary>
/// <param name="UserOverride">
/// 用户显式勾选即视为授权：允许修复分析结论为「可疑」的文件
/// （有内嵌视频、无 Apple 改写痕迹，默认不勾选、不进自动批处理）。
/// 其余非 <see cref="PhotoCondition.NeedFix"/> 结论（非实况 / 仅外挂 MOV /
/// iPhone 原生 / 已合规 / 损坏）即使勾选也仍然跳过 —— 无从修起或无需修。
/// </param>
public sealed record RepairOptions(
    RepairMode Mode = RepairMode.Retain,
    MakerNotePolicy MakerNotePolicy = MakerNotePolicy.Auto,
    bool UserOverride = false);

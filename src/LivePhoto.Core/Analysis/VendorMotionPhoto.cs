using System.Text;

namespace LivePhoto.Core.Analysis;

/// <summary>
/// 厂商私有动态照片声明的识别。
/// <para>
/// Google 的 Motion Photo 用 XMP（<c>GCamera:MotionPhoto</c> + <c>Container:Directory</c>）
/// 声明"尾部的 MP4 是实况视频"。但国内厂商各有各的写法：荣耀/华为不用 XMP，
/// 而是在<b>视频切片之前的私有块里</b>写一串厂商标识，系统相册认这个标识。
/// </para>
/// <para>
/// 实测结构（荣耀 MagicOS，<c>IMG_20250609_181150.jpg</c>，17,202,660 B）：
/// <code>
/// [JPEG 封面 5,829,087 B（含 APP6~APP9 各 64 KB 的厂商段）]
/// [EOI]
/// [私有块 1,067,967 B：缩略图/深度图 + 一堆厂商数据]
///   ... 00 80 00 00 00 02 95 0f 00 "HiHonor_EhdrEn" 00000000 00000001
///       00 00 00 48 "HiHonor_ExtInfo" 00000000
/// [视频 10,305,606 B：ftyp(mp42) + moov + free + mdat]
/// </code>
/// 标识就在视频起点前 43 字节处，因此<b>不需要额外读盘</b> —— 判定时这些字节已经在内存里。
/// </para>
/// </summary>
public static class VendorMotionPhoto
{
    /// <summary>
    /// 已知厂商标识。只认<b>完整、长度足够</b>的 ASCII 标识，宁可漏判也不能误判：
    /// 漏判的结果是"照旧当作缺声明"（老行为），误判会把一个该修的文件放过去。
    /// </summary>
    private static readonly (string Vendor, byte[] Marker)[] Markers =
    {
        // 荣耀 MagicOS：视频前紧跟 HiHonor_ 记录，实测 EhdrEn / ExtInfo 两条
        ("荣耀", Encoding.ASCII.GetBytes("HiHonor_EhdrEn")),
        ("荣耀", Encoding.ASCII.GetBytes("HiHonor_ExtInfo")),
    };

    /// <summary>
    /// 在视频起点前的私有块里找厂商标识。找到返回厂商标（如"荣耀"），否则 null。
    /// <para>
    /// 只扫最后 64 KB：标识必然紧贴视频起点（实测 43 字节），
    /// 而私有块里还可能嵌着缩略图 JPEG，扫全块既慢又容易撞上偶然字节。
    /// </para>
    /// </summary>
    public static string? Detect(byte[] data, int videoStart)
    {
        if (videoStart <= 0 || videoStart > data.Length)
        {
            return null;
        }

        int from = Math.Max(0, videoStart - 65536);
        ReadOnlySpan<byte> tail = data.AsSpan(from, videoStart - from);

        foreach ((string vendor, byte[] marker) in Markers)
        {
            if (tail.IndexOf(marker) >= 0)
            {
                return vendor;
            }
        }

        return null;
    }
}

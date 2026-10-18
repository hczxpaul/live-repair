namespace LivePhoto.Core.Exif;

/// <summary>
/// 一个 IFD 条目。对应 Python 参考实现的 <c>Entry(tag, typ, count, raw)</c>。
/// <para>
/// <c>Data</c> 保存的是**原始字节**（未经类型解码），长度 = TypeSize(typ) × count。
/// 序列化时按原样写回，这样即使遇到未知类型也能字节级往返。
/// </para>
/// </summary>
public sealed class Entry
{
    public ushort Tag { get; set; }

    /// <summary>TIFF 类型码（1=BYTE, 2=ASCII, 3=SHORT, 4=LONG, 5=RATIONAL, 7=UNDEFINED …）。</summary>
    public ushort Type { get; set; }

    public uint Count { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();

    public Entry()
    {
    }

    public Entry(ushort tag, ushort type, uint count, byte[] data)
    {
        Tag = tag;
        Type = type;
        Count = count;
        Data = data;
    }
}

/// <summary>
/// EXIF/TIFF 结构树。对应 Python 的 <c>Tiff</c>。
/// <para>
/// 字段可为 null —— null 表示"源文件里没有这个子 IFD"，序列化时**不会**生成对应 IFD，
/// 这与参考实现一致（参考实现用 None 判断是否存在）。
/// </para>
/// </summary>
public sealed class Tiff
{
    /// <summary>true = II(小端)，false = MM(大端)。</summary>
    public bool LittleEndian { get; set; }

    public List<Entry> Ifd0 { get; } = new();

    public List<Entry>? Exif { get; set; }

    public List<Entry>? Gps { get; set; }

    public List<Entry>? Interop { get; set; }

    public List<Entry>? Ifd1 { get; set; }

    /// <summary>IFD1 里的缩略图原始字节（已从 IFD1 的 0x0201/0x0202 摘出）。</summary>
    public byte[]? Thumb { get; set; }

    /// <summary>标准子 IFD 指针标签。</summary>
    public const ushort TagExifIfd = 0x8769;

    public const ushort TagGpsIfd = 0x8825;

    public const ushort TagInteropIfd = 0xA005;

    public const ushort TagMakerNote = 0x927C;

    public const ushort TagMake = 0x010F;

    public const ushort TagModel = 0x0110;

    public const ushort TagSoftware = 0x0131;

    public const ushort TagDateTimeOriginal = 0x9003;

    public const ushort TagDateTimeDigitized = 0x9004;

    /// <summary>IFD0 里的文件修改时间（0x0132）。</summary>
    public const ushort TagDateTime = 0x0132;
}

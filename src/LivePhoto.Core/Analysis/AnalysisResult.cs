namespace LivePhoto.Core.Analysis;

/// <summary>
/// 单个文件的分析结论。<b>不可变</b>，可安全跨线程传递。
/// <para>
/// 注意这里<b>不含任何 UI 概念</b>（没有"✔"、没有"8.21 MB"这类格式化文本）——
/// 那是 ViewModel 层 mapper 的职责，领域层不认识它们。
/// </para>
/// </summary>
public sealed class AnalysisResult
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    public long Size { get; init; }

    public required PhotoCondition Condition { get; init; }

    public required PairingStatus Pairing { get; init; }

    /// <summary>人话结论。例如"MakerNote 被 Apple 互传改写"。</summary>
    public required string Reason { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Software { get; init; }

    public int MakerNoteLength { get; init; }

    public bool MakerNoteIsApple { get; init; }

    public bool HasGoogleXmp { get; init; }

    public bool HasEmbeddedVideo { get; init; }

    public long VideoLength { get; init; }

    public string? FtypBrand { get; init; }

    public double? DurationMs { get; init; }

    public string? Mime { get; init; }

    /// <summary>EOI 与 ftyp 之间的厂商私有块长度（vivo 原生通常 269 B，被 Apple 互传后为 0）。</summary>
    public long BlkLength { get; init; }

    /// <summary>
    /// 识别出的厂商私有声明来源（如"荣耀"）。只有
    /// <see cref="PhotoCondition.VendorDeclared"/> 才会填它，其余为 null。
    /// </summary>
    public string? Vendor { get; init; }

    /// <summary>MP4 尾部不属于任何 box 的杂散字节数。</summary>
    public long TailStray { get; init; }

    /// <summary>同目录同名外挂 MOV 的大小（Apple 原生配对形式）。</summary>
    public long? ExternalMovSize { get; init; }

    /// <summary>MakerNote 策略将如何处置这个文件（用于 UI 明示，不允许静默改写）。</summary>
    public string MakerNoteAction { get; init; } = "无需处理";

    public string? DateTimeOriginal { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>
    /// 是否属于待修复。成因各不相同，但每一种都有确定的修复动作：
    /// <list type="bullet">
    /// <item><see cref="PhotoCondition.NeedFix"/> —— MakerNote 被 Apple 改写；</item>
    /// <item><see cref="PhotoCondition.ContainerFix"/> —— 自带声明但视频缺 ftyp 盒；</item>
    /// <item><see cref="PhotoCondition.MissingDeclaration"/> —— 缺 Google 标准实况声明。</item>
    /// </list>
    /// 三者都默认勾选、进入修复流；<b>分类不合并</b>，界面上要看得出各自毛病。
    /// </summary>
    public bool NeedsFix => Condition is PhotoCondition.NeedFix
                                       or PhotoCondition.ContainerFix
                                       or PhotoCondition.MissingDeclaration;
}

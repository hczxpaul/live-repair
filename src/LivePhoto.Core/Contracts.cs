namespace LivePhoto.Core;

/// <summary>
/// 文件是否符合修复条件。全局唯一，UI 与日志只消费这个枚举，
/// 不允许任何模块自己发明状态字符串。
/// </summary>
public enum PhotoCondition
{
    /// <summary>MakerNote 是 Apple 私有值且有内嵌视频 —— 需要修复。</summary>
    NeedFix,

    /// <summary>
    /// 有内嵌视频、MakerNote 不是 Apple 改写值，且<b>缺少 Google 标准实况声明</b>。
    /// <para>
    /// 相册缺少识别依据，会把它当成静态图片处理（真机实测：视频播不了）——
    /// 后果确定、修复动作也确定（补一段声明），所以<b>属于待修复</b>，
    /// 但成因与「被 Apple 改写」不同，必须分开标识，否则用户看不出毛病在哪。
    /// </para>
    /// </summary>
    MissingDeclaration,

    /// <summary>已有 Google 标准实况声明且 MakerNote 非 Apple —— 无需处理。</summary>
    AlreadyStandard,

    /// <summary>
    /// 已有 Google 标准实况声明，但内嵌视频<b>缺 ftyp 盒</b> —— 需要补容器。
    /// <para>
    /// 某些第三方转换链路会给文件写上标准 XMP、却在拼接视频时剥掉了 20 字节的 ftyp，
    /// 结果播放器按声明切出视频后无法识别容器类型。绿联上的表现是
    /// 「能识别成实况照片、但视频播放失败」。修复 = 补回 ftyp 并同步平移 chunk 偏移。
    /// </para>
    /// </summary>
    ContainerFix,

    /// <summary>
    /// 未见 Apple 痕迹但有内嵌视频 —— 默认不勾选；用户显式勾选后按需修复
    /// （修复只补 Google 实况声明，厂商 MakerNote 原样保留）。
    /// </summary>
    Suspicious,

    /// <summary>不是实况照片：既无内嵌视频，也无同名外挂 MOV。</summary>
    NotMotionPhoto,

    /// <summary>只有Apple原生外挂 MOV，无内嵌视频。</summary>
    ExternalMovOnly,

    /// <summary>JPEG 结构破损：无 SOI / 段长度不自洽 / 找不到 EOI。</summary>
    BrokenJpeg,

    /// <summary>EXIF 无法解析，拿不到 MakerNote，无法做判定。</summary>
    BrokenExif,

    /// <summary>不支持的格式（HEIC / HEIF / PNG 等）。</summary>
    UnsupportedFormat,

    /// <summary>iPhone 原生 Live Photo，不是被破坏的文件。</summary>
    AppleNative,

    /// <summary>
    /// 厂商私有声明：视频切片之前有厂商标识（荣耀为 <c>HiHonor_</c> 尾巴），
    /// 但<b>没有</b> Google 标准 XMP。
    /// <para>
    /// 这类文件在绿联上能被正常识别并播放（2026-10-19 用户实测），所以它<b>不是缺陷</b>：
    /// 既不该默认勾选，也不该被改写。加 Google XMP 反而是给一个已经能用的文件动手术。
    /// 单独分类而不是并进「已合规」，是因为判定依据不同（厂商私有 vs Google 标准），
    /// 将来要是真需要转标准格式，也能一把捞出来。
    /// </para>
    /// </summary>
    VendorDeclared,
}

/// <summary>
/// 静态图与其动态部分的配对情况。
/// 这是绿联出错的根因：Apple 改写了 MakerNote 后，绿联会去找同名外挂 MOV。
/// </summary>
public enum PairingStatus
{
    /// <summary>视频内嵌在自己体内（EOI 之后有合法 ftyp）。最理想。</summary>
    EmbeddedVideo,

    /// <summary>同目录存在同名 .mov —— Apple 原生配对形式。</summary>
    ExternalMov,

    /// <summary>两者皆有。罕见，需提示用户注意重复。</summary>
    Both,

    /// <summary>两者都无 —— 动态部分已丢失，修复也救不回来。</summary>
    None,
}

/// <summary>
/// 单条修复的最终结果。
/// </summary>
public enum RepairOutcome
{
    Success,

    /// <summary>经判定无需修复，未产出文件。</summary>
    Skipped,

    /// <summary>12 项校验未通过 —— 不产出任何文件，也不残留 tmp。</summary>
    VerifyFailed,

    /// <summary>源文件读取失败（占用 / 无权限 / 已被外部修改）。</summary>
    ReadFailed,

    /// <summary>目标写入失败（磁盘满 / 路径非法 / 权限）。</summary>
    WriteFailed,

    /// <summary>被安全闸门拦下（典型：输出目录落在源目录内）。</summary>
    GuardRejected,

    /// <summary>用户取消。</summary>
    Cancelled,
}

/// <summary>
/// MakerNote 的处理策略。
/// </summary>
public enum MakerNotePolicy
{
    /// <summary>
    /// 推荐：Make 明确是 vivo 才写 vivo 原生值（同源，不构成伪造）；
    /// 否则直接删除标签。绝不把 vivo 的值写到非 vivo 文件上。
    /// </summary>
    Auto,

    /// <summary>无条件删除 MakerNote 标签。最干净，零伪造可能。</summary>
    AlwaysDelete,

    /// <summary>不动 MakerNote。会导致绿联不识别，仅调试用。</summary>
    Keep,
}

/// <summary>
/// 修复模式。
/// </summary>
public enum RepairMode
{
    /// <summary>字节零删除：保留 EOI 与 ftyp 之间的私有块、以及 MP4 尾部杂散字节。</summary>
    Retain,

    /// <summary>清理模式：删除 MP4 尾部不属于任何 box 的杂散字节，box 链变为完整。</summary>
    Clean,
}

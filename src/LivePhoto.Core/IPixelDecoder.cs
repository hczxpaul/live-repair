namespace LivePhoto.Core;

/// <summary>
/// 像素解码能力抽象。
/// <para>
/// <b>依赖倒置的关键接口。</b>校验时需要比对封面像素，但 JPEG 解码器（WIC）属于 WPF，
/// 若 Core 直接引用就会变成 Windows/WPF 专用库，测试与未来的 CLI 都没法复用。
/// 因此 Core 只认这个接口，实现由 <c>LivePhoto.App.Imaging.WpfDecoder</c> 提供并注入。
/// </para>
/// </summary>
public interface IPixelDecoder
{
    /// <summary>
    /// 两张 JPEG 解码后是否<b>逐点完全一致</b>。
    /// 输入是完整 JPEG 字节（可以带内嵌视频，解码器只取封面）。
    /// <para>
    /// <paramref name="maxWidth"/> &gt; 0 时按该宽度降采样解码后再比对（WIC 缩放 IDCT）。
    /// 降采样不损失校验强度：解码仍会完整解析段头与全部熵数据（缩放只发生在 IDCT），
    /// "解不开"在两种分辨率下同样会失败；而"解码结果相同"已由校验器的
    /// 「除 Exif/XMP 外所有字节逐字节一致」项在字节级保证。默认 0 = 全分辨率（原行为）。
    /// </para>
    /// </summary>
    bool ArePixelsIdentical(byte[] left, byte[] right, out int width, out int height, int maxWidth = 0);
}

/// <summary>
/// 空实现：不提供解码能力时使用。像素校验项会被标记为"未启用"而非"不通过"，
/// 避免误判为修复失败。
/// </summary>
public sealed class NullPixelDecoder : IPixelDecoder
{
    public bool ArePixelsIdentical(byte[] left, byte[] right, out int width, out int height, int maxWidth = 0)
    {
        width = 0;
        height = 0;
        return false;
    }
}

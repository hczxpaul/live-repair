using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LivePhoto.Core;

namespace LivePhoto.App.Imaging;

/// <summary>
/// <see cref="IPixelDecoder"/> 的 WIC 实现（WPF 自带解码器，零 NuGet 依赖）。
/// <para>
/// <b>关键约束：一律 <see cref="BitmapCacheOption.OnLoad"/> 并立即释放流。</b>
/// 否则缩略图会锁住源文件句柄，导致"修复完成后无法删除源文件"——
/// 那会直接破坏本工具"非侵入"的根本承诺。
/// </para>
/// </summary>
public sealed class WpfDecoder : IPixelDecoder
{
    public bool ArePixelsIdentical(byte[] left, byte[] right, out int width, out int height, int maxWidth = 0)
    {
        width = 0;
        height = 0;
        try
        {
            (int w1, int h1, byte[] p1) = DecodePixels(left, maxWidth);
            (int w2, int h2, byte[] p2) = DecodePixels(right, maxWidth);
            width = w1;
            height = h1;
            return w1 == w2 && h1 == h2 && p1.AsSpan().SequenceEqual(p2);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>加载缩略图。解码后立即释放流，不锁文件。</summary>
    public BitmapSource? LoadThumbnail(string path, int maxWidth)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return LoadThumbnail(bytes, maxWidth);
        }
        catch
        {
            return null;
        }
    }

    public BitmapSource? LoadThumbnail(byte[] data, int maxWidth)
    {
        try
        {
            using MemoryStream ms = new(data, writable: false);
            BitmapImage bmp = new();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 必须：解码完立刻释放流
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = Math.Min(maxWidth, 4096);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 整幅解码为 Bgr32。<paramref name="maxWidth"/> &gt; 0 时按宽度缩放
    /// （WIC 仍完整解析全部熵数据，缩放只发生在 IDCT 环节）。
    /// </summary>
    private static (int W, int H, byte[] Pixels) DecodePixels(byte[] data, int maxWidth = 0)
    {
        using MemoryStream ms = new(data, writable: false);
        BitmapImage bmp = new();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        if (maxWidth > 0)
        {
            bmp.DecodePixelWidth = maxWidth;
        }

        bmp.EndInit();
        bmp.Freeze();

        FormatConvertedBitmap conv = new(bmp, PixelFormats.Bgr32, null, 0);
        int w = conv.PixelWidth;
        int h = conv.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        conv.CopyPixels(pixels, stride, 0);
        return (w, h, pixels);
    }
}

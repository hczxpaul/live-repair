using System.Text;

namespace LivePhoto.Core.Xmp;

/// <summary>
/// 构造 Google Motion Photo Format 1.0 的 XMP 文本。
/// <para><b>纯函数</b>：零 IO、零状态、无随机数。给定同样入参必须产出同样字节。</para>
/// <para>
/// 四个强约束（违反任一都会让绿联认不出或定位错视频）：
/// <list type="bullet">
/// <item><c>x:xmptk</c> 恒为 <c>Adobe XMP Core 5.1.0-jc003</c>（vivo 官方模板实测值，不写自制工具指纹）</item>
/// <item>两个 Container Item 都要带 <c>Item:Padding="0"</c></item>
/// <item><c>Item:Length</c> 必须精确等于 EOI 之后的实际视频字节数</item>
/// <item>只用 Google 三家命名空间，绝不出现厂商私有命名空间</item>
/// </list>
/// </para>
/// </summary>
public static class GoogleXmpBuilder
{
    public const string Toolkit = "Adobe XMP Core 5.1.0-jc003";

    public static byte[] Build(long videoLength, long presentationTimestampUs, string mime)
    {
        StringBuilder sb = new(1100);
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"").Append(Toolkit).Append("\">\n");
        sb.Append("  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("    <rdf:Description rdf:about=\"\"\n");
        sb.Append("        xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"\n");
        sb.Append("        xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"\n");
        sb.Append("        xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"\n");
        sb.Append("      GCamera:MotionPhoto=\"1\"\n");
        sb.Append("      GCamera:MotionPhotoVersion=\"1\"\n");
        sb.Append("      GCamera:MotionPhotoPresentationTimestampUs=\"")
          .Append(presentationTimestampUs.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append("\">\n");
        sb.Append("      <Container:Directory>\n");
        sb.Append("        <rdf:Seq>\n");
        sb.Append("          <rdf:li rdf:parseType=\"Resource\">\n");
        sb.Append("            <Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\"\n");
        sb.Append("              Item:Length=\"0\" Item:Padding=\"0\"/>\n");
        sb.Append("          </rdf:li>\n");
        sb.Append("          <rdf:li rdf:parseType=\"Resource\">\n");
        sb.Append("            <Container:Item Item:Mime=\"").Append(mime)
          .Append("\" Item:Semantic=\"MotionPhoto\"\n");
        sb.Append("              Item:Length=\"")
          .Append(videoLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append("\" Item:Padding=\"0\"/>\n");
        sb.Append("          </rdf:li>\n");
        sb.Append("        </rdf:Seq>\n");
        sb.Append("      </Container:Directory>\n");
        sb.Append("    </rdf:Description>\n");
        sb.Append("  </rdf:RDF>\n");
        sb.Append("</x:xmpmeta>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

namespace LivePhoto.Core.Exif;

/// <summary>
/// IFD 条目的增删改查。语义与参考实现 <c>fix_livephoto.py</c> 完全一致 ——
/// 包括"追加新标签后按 tag 升序排序"这一行为。
/// </summary>
public static class ExifAccess
{
    public static Entry? Get(List<Entry>? list, ushort tag)
    {
        if (list is null)
        {
            return null;
        }

        foreach (Entry e in list)
        {
            if (e.Tag == tag)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>删除标签（原地）。参考实现的 <c>_del</c>。</summary>
    public static void Del(List<Entry> list, ushort tag) => list.RemoveAll(e => e.Tag == tag);

    /// <summary>
    /// 写入原始字节值。参考实现的 <c>set_undef</c>。
    /// <para>已存在 → 原地改 typ/count/data（**不改变顺序**）；不存在 → 追加后按 tag 升序排序。</para>
    /// </summary>
    public static void SetUndef(List<Entry> list, ushort tag, byte[] raw, ushort typ = 7)
    {
        Entry? e = Get(list, tag);
        if (e is not null)
        {
            e.Type = typ;
            e.Count = (uint)raw.Length;
            e.Data = raw;
            return;
        }

        list.Add(new Entry(tag, typ, (uint)raw.Length, raw));
        list.Sort((a, b) => a.Tag.CompareTo(b.Tag));
    }

    /// <summary>写入 ASCII 值（自动补 NUL 终止符）。参考实现的 <c>set_ascii</c>。</summary>
    public static void SetAscii(List<Entry> list, ushort tag, string s)
    {
        byte[] raw = System.Text.Encoding.ASCII.GetBytes(s + "\0");
        Entry? e = Get(list, tag);
        if (e is not null)
        {
            e.Type = 2;
            e.Count = (uint)raw.Length;
            e.Data = raw;
            return;
        }

        list.Add(new Entry(tag, 2, (uint)raw.Length, raw));
        list.Sort((a, b) => a.Tag.CompareTo(b.Tag));
    }

    /// <summary>读取 ASCII 字符串（去掉尾部 NUL）。参考实现的 <c>get_str</c>。</summary>
    public static string? GetStr(Tiff? t, ushort tag)
    {
        Entry? e = Get(t?.Ifd0, tag);
        if (e is null)
        {
            return null;
        }

        int len = e.Data.Length;
        while (len > 0 && e.Data[len - 1] == 0)
        {
            len--;
        }

        return System.Text.Encoding.Latin1.GetString(e.Data, 0, len);
    }

    /// <summary>取 MakerNote 原始字节。它在 Exif 子 IFD 里（tag 0x927C）。</summary>
    public static byte[]? GetMakerNote(Tiff? t) => Get(t?.Exif, Tiff.TagMakerNote)?.Data;

    /// <summary>取 Exif 子 IFD 中的标签（0x9003 等拍摄参数在这里）。</summary>
    public static Entry? GetInExif(Tiff? t, ushort tag) => Get(t?.Exif, tag);
}

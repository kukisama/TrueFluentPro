namespace IconTool.Core;

/// <summary>
/// ICO 文件格式解析与操作工具。
/// </summary>
public static class IcoHelper
{
    /// <summary>
    /// 解析 ICO 文件字节，返回所有图像条目的元信息。
    /// </summary>
    public static List<IconEntryInfo> ParseEntries(byte[] icoData)
    {
        var entries = new List<IconEntryInfo>();
        if (icoData.Length < 6) return entries;

        var count = BitConverter.ToUInt16(icoData, 4);
        for (int i = 0; i < count; i++)
        {
            var entryOffset = 6 + i * 16;
            if (entryOffset + 16 > icoData.Length) break;

            int w = icoData[entryOffset] == 0 ? 256 : icoData[entryOffset];
            int h = icoData[entryOffset + 1] == 0 ? 256 : icoData[entryOffset + 1];
            int bitCount = BitConverter.ToUInt16(icoData, entryOffset + 6);
            int dataSize = BitConverter.ToInt32(icoData, entryOffset + 8);

            entries.Add(new IconEntryInfo(w, h, bitCount, dataSize));
        }

        return entries;
    }

    /// <summary>
    /// 获取 ICO 中最大尺寸条目的索引。
    /// </summary>
    public static int GetLargestEntryIndex(byte[] icoData)
    {
        var entries = ParseEntries(icoData);
        if (entries.Count == 0) return -1;

        int maxIndex = 0;
        int maxPixels = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            int pixels = entries[i].Width * entries[i].Height;
            if (pixels > maxPixels)
            {
                maxPixels = pixels;
                maxIndex = i;
            }
        }
        return maxIndex;
    }

    /// <summary>
    /// 描述 ICO 文件中包含的尺寸列表。
    /// </summary>
    public static string DescribeSizes(byte[] icoData)
    {
        var entries = ParseEntries(icoData);
        if (entries.Count == 0) return "?";

        var sizes = entries.Select(e => e.Width == e.Height ? $"{e.Width}" : $"{e.Width}x{e.Height}");
        return $"{entries.Count} 张 ({string.Join("/", sizes)})";
    }

    /// <summary>
    /// 获取 ICO 中最大尺寸。
    /// </summary>
    public static int GetMaxSize(byte[] icoData)
    {
        var entries = ParseEntries(icoData);
        return entries.Count > 0 ? entries.Max(e => Math.Max(e.Width, e.Height)) : 0;
    }

    /// <summary>
    /// 从 ICO 文件字节中提取指定条目的原始图像数据。
    /// 返回的数据可能是 PNG（以 0x89504E47 开头）或 BMP DIB 数据。
    /// </summary>
    public static byte[]? ExtractEntryImageData(byte[] icoData, int entryIndex)
    {
        if (icoData.Length < 6) return null;

        var count = BitConverter.ToUInt16(icoData, 4);
        if (entryIndex < 0 || entryIndex >= count) return null;

        int dirEntryOffset = 6 + entryIndex * 16;
        if (dirEntryOffset + 16 > icoData.Length) return null;

        int dataSize = BitConverter.ToInt32(icoData, dirEntryOffset + 8);
        int dataOffset = BitConverter.ToInt32(icoData, dirEntryOffset + 12);

        if (dataOffset + dataSize > icoData.Length || dataSize <= 0) return null;

        var data = new byte[dataSize];
        Array.Copy(icoData, dataOffset, data, 0, dataSize);
        return data;
    }

    /// <summary>
    /// 从 ICO 数据中提取最大尺寸的图像数据（PNG 或 BMP DIB）。
    /// </summary>
    public static byte[]? ExtractLargestImageData(byte[] icoData)
    {
        int idx = GetLargestEntryIndex(icoData);
        return idx >= 0 ? ExtractEntryImageData(icoData, idx) : null;
    }

    /// <summary>
    /// 判断字节数据是否为 PNG 格式。
    /// </summary>
    public static bool IsPng(byte[] data)
    {
        return data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50
            && data[2] == 0x4E && data[3] == 0x47;
    }
}

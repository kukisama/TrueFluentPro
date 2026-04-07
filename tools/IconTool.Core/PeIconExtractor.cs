namespace IconTool.Core;

/// <summary>
/// 从 PE (EXE/DLL) 文件中提取图标资源。
/// 纯字节操作，不依赖 Win32 API 或第三方库。
/// </summary>
public static class PeIconExtractor
{
    /// <summary>
    /// 从 PE 文件路径提取所有图标组。
    /// </summary>
    public static List<IconGroupInfo> ExtractFromFile(string peFilePath)
    {
        if (!File.Exists(peFilePath))
            throw new FileNotFoundException($"文件不存在: {peFilePath}", peFilePath);

        var ext = Path.GetExtension(peFilePath).ToLowerInvariant();
        if (ext is not ".exe" and not ".dll")
            throw new ArgumentException($"不支持的文件格式: {ext}。仅支持 .exe 和 .dll。");

        var peBytes = File.ReadAllBytes(peFilePath);
        return ExtractFromBytes(peBytes);
    }

    /// <summary>
    /// 从 PE 文件字节提取所有图标组。每个图标组为一个完整的 ICO 文件。
    /// </summary>
    public static List<IconGroupInfo> ExtractFromBytes(byte[] pe)
    {
        var rawIcons = ExtractIconsFromPE(pe);
        var result = new List<IconGroupInfo>();

        foreach (var (index, icoData) in rawIcons)
        {
            var entries = IcoHelper.ParseEntries(icoData);
            result.Add(new IconGroupInfo(index, icoData, entries));
        }

        return result;
    }

    /// <summary>
    /// 扫描目录中的所有 EXE 文件并提取图标。
    /// </summary>
    public static List<ExeIconInfo> ScanDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"目录不存在: {directoryPath}");

        var result = new List<ExeIconInfo>();
        var exeFiles = Directory.GetFiles(directoryPath, "*.exe", SearchOption.TopDirectoryOnly);

        foreach (var exePath in exeFiles)
        {
            try
            {
                var groups = ExtractFromFile(exePath);
                if (groups.Count > 0)
                {
                    result.Add(new ExeIconInfo(
                        exePath,
                        Path.GetFileName(exePath),
                        groups));
                }
            }
            catch
            {
                // 跳过无法解析的 EXE 文件
            }
        }

        return result;
    }

    // ─── PE 解析核心（从 IconTool/Program.cs 提取） ───

    private static List<(int Index, byte[] IcoData)> ExtractIconsFromPE(byte[] pe)
    {
        var result = new List<(int, byte[])>();

        // PE 头解析
        if (pe.Length < 64 || pe[0] != 'M' || pe[1] != 'Z')
            throw new InvalidDataException("不是有效的 PE 文件（缺少 MZ 签名）。");

        int peOffset = BitConverter.ToInt32(pe, 0x3C);
        if (peOffset + 4 > pe.Length || pe[peOffset] != 'P' || pe[peOffset + 1] != 'E')
            throw new InvalidDataException("不是有效的 PE 文件（缺少 PE 签名）。");

        int coffOffset = peOffset + 4;
        ushort numberOfSections = BitConverter.ToUInt16(pe, coffOffset + 2);
        ushort sizeOfOptionalHeader = BitConverter.ToUInt16(pe, coffOffset + 16);

        int optionalHeaderOffset = coffOffset + 20;
        ushort magic = BitConverter.ToUInt16(pe, optionalHeaderOffset);
        bool isPE32Plus = magic == 0x20B;

        // 获取资源目录 RVA 和 Size (Data Directory index 2 = Resource)
        int dataDirectoryOffset = optionalHeaderOffset + (isPE32Plus ? 112 : 96);
        int resourceDirRVA = BitConverter.ToInt32(pe, dataDirectoryOffset + 2 * 8);
        int resourceDirSize = BitConverter.ToInt32(pe, dataDirectoryOffset + 2 * 8 + 4);

        if (resourceDirRVA == 0 || resourceDirSize == 0)
            return result;

        // 查找 .rsrc Section
        int sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
        int rsrcFileOffset = -1;
        int rsrcVirtualAddress = 0;

        for (int i = 0; i < numberOfSections; i++)
        {
            int secOffset = sectionTableOffset + i * 40;
            int secVA = BitConverter.ToInt32(pe, secOffset + 12);
            int secRawSize = BitConverter.ToInt32(pe, secOffset + 16);
            int secRawPtr = BitConverter.ToInt32(pe, secOffset + 20);

            if (resourceDirRVA >= secVA && resourceDirRVA < secVA + secRawSize)
            {
                rsrcFileOffset = secRawPtr + (resourceDirRVA - secVA);
                rsrcVirtualAddress = secVA;
                break;
            }
        }

        if (rsrcFileOffset < 0)
            return result;

        int RvaToOffset(int rva) => rsrcFileOffset + (rva - rsrcVirtualAddress);

        // 解析资源目录树
        var iconEntries = new Dictionary<int, byte[]>();
        var groupEntries = new List<(int Id, byte[] Data)>();

        var (topNamed, topId) = ReadResDir(pe, rsrcFileOffset);
        int topEntryCount = topNamed + topId;

        for (int t = 0; t < topEntryCount; t++)
        {
            int entryOff = rsrcFileOffset + 16 + t * 8;
            int typeId = BitConverter.ToInt32(pe, entryOff);
            int offsetOrDir = BitConverter.ToInt32(pe, entryOff + 4);

            bool isDir = (offsetOrDir & 0x80000000) != 0;
            if (!isDir) continue;

            int subDirOffset = rsrcFileOffset + (int)(offsetOrDir & 0x7FFFFFFF);
            bool isIcon = (typeId & 0x7FFFFFFF) == 3;
            bool isGroupIcon = (typeId & 0x7FFFFFFF) == 14;

            if (!isIcon && !isGroupIcon) continue;

            var (l1Named, l1Id) = ReadResDir(pe, subDirOffset);
            int l1Count = l1Named + l1Id;

            for (int n = 0; n < l1Count; n++)
            {
                int l1EntryOff = subDirOffset + 16 + n * 8;
                int nameId = BitConverter.ToInt32(pe, l1EntryOff) & 0x7FFFFFFF;
                int l1OffsetOrDir = BitConverter.ToInt32(pe, l1EntryOff + 4);

                bool l1IsDir = (l1OffsetOrDir & 0x80000000) != 0;
                if (!l1IsDir)
                {
                    var dataEntry = ReadResourceDataEntry(pe, rsrcFileOffset + (int)(l1OffsetOrDir & 0x7FFFFFFF), pe, RvaToOffset);
                    if (dataEntry is not null)
                    {
                        if (isIcon) iconEntries[nameId] = dataEntry;
                        else if (isGroupIcon) groupEntries.Add((nameId, dataEntry));
                    }
                    continue;
                }

                int l2DirOffset = rsrcFileOffset + (int)(l1OffsetOrDir & 0x7FFFFFFF);
                var (l2Named, l2Id) = ReadResDir(pe, l2DirOffset);
                int l2Count = l2Named + l2Id;
                if (l2Count == 0) continue;

                int l2EntryOff = l2DirOffset + 16;
                int l2OffsetOrDir = BitConverter.ToInt32(pe, l2EntryOff + 4);

                bool l2IsDir = (l2OffsetOrDir & 0x80000000) != 0;
                if (l2IsDir) continue;

                var data = ReadResourceDataEntry(pe, rsrcFileOffset + l2OffsetOrDir, pe, RvaToOffset);
                if (data is not null)
                {
                    if (isIcon) iconEntries[nameId] = data;
                    else if (isGroupIcon) groupEntries.Add((nameId, data));
                }
            }
        }

        // 将 RT_GROUP_ICON + RT_ICON 组合为标准 ICO
        foreach (var (groupId, groupData) in groupEntries)
        {
            if (groupData.Length < 6) continue;

            ushort type = BitConverter.ToUInt16(groupData, 2);
            ushort count = BitConverter.ToUInt16(groupData, 4);

            if (type != 1 || count == 0) continue;

            var entries = new List<(byte[] Header14, byte[] Data)>();

            for (int i = 0; i < count; i++)
            {
                int grpEntryOff = 6 + i * 14;
                if (grpEntryOff + 14 > groupData.Length) break;

                ushort iconId = BitConverter.ToUInt16(groupData, grpEntryOff + 12);

                if (iconEntries.TryGetValue(iconId, out var iconData))
                {
                    var header = new byte[12];
                    Array.Copy(groupData, grpEntryOff, header, 0, 12);
                    var actualSize = BitConverter.GetBytes((uint)iconData.Length);
                    Array.Copy(actualSize, 0, header, 8, 4);
                    entries.Add((header, iconData));
                }
            }

            if (entries.Count == 0) continue;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)entries.Count);

            int dataOffset = 6 + entries.Count * 16;

            foreach (var (header, data) in entries)
            {
                bw.Write(header, 0, 12);
                bw.Write((uint)dataOffset);
                dataOffset += data.Length;
            }

            foreach (var (_, data) in entries)
            {
                bw.Write(data);
            }

            bw.Flush();
            result.Add((groupId, ms.ToArray()));
        }

        return result;
    }

    private static (ushort NamedEntries, ushort IdEntries) ReadResDir(byte[] data, int offset)
    {
        ushort named = BitConverter.ToUInt16(data, offset + 12);
        ushort id = BitConverter.ToUInt16(data, offset + 14);
        return (named, id);
    }

    private static byte[]? ReadResourceDataEntry(byte[] pe, int entryFileOffset, byte[] fullPe, Func<int, int> rvaToOffset)
    {
        if (entryFileOffset + 16 > pe.Length) return null;

        int dataRva = BitConverter.ToInt32(pe, entryFileOffset);
        int dataSize = BitConverter.ToInt32(pe, entryFileOffset + 4);

        if (dataRva == 0 || dataSize <= 0) return null;

        int dataFileOffset = RvaToFileOffset(fullPe, dataRva);
        if (dataFileOffset < 0 || dataFileOffset + dataSize > fullPe.Length) return null;

        var data = new byte[dataSize];
        Array.Copy(fullPe, dataFileOffset, data, 0, dataSize);
        return data;
    }

    private static int RvaToFileOffset(byte[] pe, int rva)
    {
        int peOffset = BitConverter.ToInt32(pe, 0x3C);
        int coffOffset = peOffset + 4;
        ushort numberOfSections = BitConverter.ToUInt16(pe, coffOffset + 2);
        ushort sizeOfOptionalHeader = BitConverter.ToUInt16(pe, coffOffset + 16);
        int sectionTableOffset = coffOffset + 20 + sizeOfOptionalHeader;

        for (int i = 0; i < numberOfSections; i++)
        {
            int secOffset = sectionTableOffset + i * 40;
            int secVA = BitConverter.ToInt32(pe, secOffset + 12);
            int secRawSize = BitConverter.ToInt32(pe, secOffset + 16);
            int secRawPtr = BitConverter.ToInt32(pe, secOffset + 20);
            int secVirtSize = BitConverter.ToInt32(pe, secOffset + 8);
            int effectiveSize = Math.Max(secRawSize, secVirtSize);

            if (rva >= secVA && rva < secVA + effectiveSize)
            {
                return secRawPtr + (rva - secVA);
            }
        }

        return -1;
    }
}

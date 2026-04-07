namespace IconTool.Core;

/// <summary>
/// 表示从 PE 文件中提取的一个图标组（对应一个 RT_GROUP_ICON 资源）。
/// IcoData 是完整的 ICO 文件字节，可直接写入文件或加载为图像。
/// </summary>
public record IconGroupInfo(int GroupId, byte[] IcoData, List<IconEntryInfo> Entries);

/// <summary>
/// ICO 文件中单个图像条目的元信息。
/// </summary>
public record IconEntryInfo(int Width, int Height, int BitCount, int DataSize);

/// <summary>
/// 扫描目录时，描述一个 EXE 文件及其包含的所有图标组。
/// </summary>
public record ExeIconInfo(string ExeFilePath, string ExeFileName, List<IconGroupInfo> IconGroups);

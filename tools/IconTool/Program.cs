using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// ─── 编码初始化 ───
try
{
    if (OperatingSystem.IsWindows())
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        var ansiEncoding = Encoding.GetEncoding(ansiCodePage);
        Console.OutputEncoding = ansiEncoding;
        Console.InputEncoding = ansiEncoding;
    }
    else
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
    }
}
catch { /* Best-effort */ }

// ─── 参数解析 ───
if (args.Length == 0)
{
    return RunAutoProcess();
}

var command = args[0].ToLowerInvariant();

return command switch
{
    "check" => RunCheck(args),
    "transparent" => RunTransparent(args),
    "crop" => RunCrop(args),
    "extract" => RunExtract(args),
    "seticon" => RunSetIcon(args),
    "convert" => RunConvert(args),
    "resize" => RunResize(args),
    "info" => RunInfo(args),
    "pad" => RunPad(args),
    "round" => RunRound(args),
    "shadow" => RunShadow(args),
    "overlay" => RunOverlay(args),
    "compose" => RunCompose(args),
    "favicon" => RunFavicon(args),
    "sheet" => RunSheet(args),
    "browseicons" => RunBrowseIcons(args),
    "help" or "--help" or "-h" or "/?" => PrintUsageAndReturn(),
    _ => Error($"未知命令: {command}。使用 'IconTool help' 查看帮助。")
};

// ═══════════════════════════════════════════════════════════════════
// 帮助信息
// ═══════════════════════════════════════════════════════════════════

static int PrintUsageAndReturn() { PrintUsage(); return 0; }

static void PrintUsage()
{
    Console.WriteLine("""
    IconTool — 图标与图像多功能命令行工具

    用法:
      IconTool                                 自动处理当前目录所有 PNG/JPG
      IconTool check <文件路径>                检测 PNG/ICO 是否包含透明通道
      IconTool transparent <文件路径> [选项]    将图片背景透明化并输出为 PNG
      IconTool crop <文件路径> [选项]           居中裁剪缩放到指定尺寸
      IconTool extract <exe路径> [选项]         从 EXE/DLL 中提取图标资源
      IconTool seticon <ico路径> <目录路径>     设置目录的自定义图标
      IconTool convert <文件路径> [选项]        格式互转 (PNG/ICO/BMP)
      IconTool resize <文件路径> [选项]         批量生成多尺寸图标
      IconTool info <文件路径>                 查看图像元信息
      IconTool pad <文件路径> [选项]            加边距/画布扩展
      IconTool round <文件路径> [选项]          圆角/圆形裁剪
      IconTool shadow <文件路径> [选项]         添加外阴影
      IconTool overlay <底图> <叠加图> [选项]   角标/水印叠加
      IconTool compose [选项] <文件1> <文件2>...  多图拼合成 ICO
      IconTool favicon <文件路径> [选项]        一键生成 Web 全套 favicon
      IconTool sheet [选项] <文件1> <文件2>...    图标合并为 sprite sheet
      IconTool browseicons <目录路径>             浏览目录中 EXE 的图标并设置为目录图标

    使用 'IconTool help' 查看详细用法。
    """);
}

// ═══════════════════════════════════════════════════════════════════
// check 命令：检测透明度
// ═══════════════════════════════════════════════════════════════════

static int RunCheck(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool check <文件路径>");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    var ext = Path.GetExtension(filePath).ToLowerInvariant();

    return ext switch
    {
        ".ico" => CheckIco(filePath),
        ".png" => CheckPng(filePath),
        _ => Error($"不支持的文件格式: {ext}。仅支持 .png 和 .ico。")
    };
}

static int CheckPng(string filePath)
{
    Console.WriteLine($"── 检测 PNG: {Path.GetFileName(filePath)} ──");
    Console.WriteLine();

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        var report = AnalyzeTransparency(image);
        PrintTransparencyReport(report, image.Width, image.Height);
        return 0;
    }
    catch (Exception ex)
    {
        return Error($"读取 PNG 失败: {ex.Message}");
    }
}

static int CheckIco(string filePath)
{
    Console.WriteLine($"── 检测 ICO: {Path.GetFileName(filePath)} ──");
    Console.WriteLine();

    try
    {
        var icoBytes = File.ReadAllBytes(filePath);

        // ICO 格式解析: ICONDIR (6 bytes) + N * ICONDIRENTRY (16 bytes each)
        if (icoBytes.Length < 6)
            return Error("ICO 文件太小，不是有效的 ICO 格式。");

        var reserved = BitConverter.ToUInt16(icoBytes, 0);
        var type = BitConverter.ToUInt16(icoBytes, 2);
        var count = BitConverter.ToUInt16(icoBytes, 4);

        if (reserved != 0 || type != 1)
            return Error("不是有效的 ICO 文件（头部标志不正确）。");

        Console.WriteLine($"ICO 包含 {count} 张嵌入图像");
        Console.WriteLine();

        for (int i = 0; i < count; i++)
        {
            var entryOffset = 6 + i * 16;
            if (entryOffset + 16 > icoBytes.Length)
            {
                Console.WriteLine($"  [图像 {i + 1}] 目录项超出文件范围，跳过。");
                continue;
            }

            var w = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            var h = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            var dataSize = BitConverter.ToInt32(icoBytes, entryOffset + 8);
            var dataOffset = BitConverter.ToInt32(icoBytes, entryOffset + 12);

            Console.WriteLine($"  [图像 {i + 1}] {w}x{h}, 数据大小={dataSize} 字节");

            if (dataOffset + dataSize > icoBytes.Length)
            {
                Console.WriteLine("    ⚠ 数据偏移超出文件范围，跳过。");
                Console.WriteLine();
                continue;
            }

            var imageData = new byte[dataSize];
            Array.Copy(icoBytes, dataOffset, imageData, 0, dataSize);

            try
            {
                using var ms = new MemoryStream(imageData);
                using var image = Image.Load<Rgba32>(ms);
                var report = AnalyzeTransparency(image);
                PrintTransparencyReport(report, image.Width, image.Height, indent: "    ");
            }
            catch
            {
                // 可能是 BMP 格式的嵌入图像，尝试直接分析位图数据
                Console.WriteLine("    ⚠ 无法解码嵌入图像（可能是旧式 BMP 格式），跳过透明度分析。");
            }

            Console.WriteLine();
        }

        return 0;
    }
    catch (Exception ex)
    {
        return Error($"读取 ICO 失败: {ex.Message}");
    }
}

// ═══════════════════════════════════════════════════════════════════
// transparent 命令：透明化处理
// ═══════════════════════════════════════════════════════════════════

static int RunTransparent(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool transparent <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    // 解析选项
    string? outputPath = null;
    string colorName = "white";
    int threshold = 30;
    bool useFloodFill = false;

    for (int i = 2; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        switch (arg)
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o/--output 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-c" or "--color":
                if (i + 1 >= args.Length) return Error("-c/--color 需要一个参数。");
                colorName = args[++i];
                break;
            case "-t" or "--threshold":
                if (i + 1 >= args.Length) return Error("-t/--threshold 需要一个参数。");
                if (!int.TryParse(args[++i], out threshold) || threshold < 0 || threshold > 255)
                    return Error("阈值必须为 0-255 之间的整数。");
                break;
            case "--flood":
                useFloodFill = true;
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    // 确定输出路径
    if (string.IsNullOrEmpty(outputPath))
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(filePath);
        outputPath = Path.Combine(dir, $"{name}_transparent.png");
    }

    // 解析目标颜色
    if (!TryParseColor(colorName, out var targetColor))
        return Error($"无法解析颜色: {colorName}。支持 white, black, #RRGGBB, #RRGGBBAA。");

    Console.WriteLine($"── 透明化处理 ──");
    Console.WriteLine($"  输入: {filePath}");
    Console.WriteLine($"  去除背景色: {colorName} (R={targetColor.R}, G={targetColor.G}, B={targetColor.B})");
    Console.WriteLine($"  颜色容差: {threshold}");
    Console.WriteLine($"  模式: {(useFloodFill ? "连通填充（仅从边缘可达区域）" : "全局匹配")}");
    Console.WriteLine($"  输出: {outputPath}");
    Console.WriteLine();

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        Console.WriteLine($"  图像尺寸: {image.Width}x{image.Height}");

        int removedCount;
        int totalPixels = image.Width * image.Height;

        if (useFloodFill)
        {
            removedCount = FloodFillTransparent(image, targetColor, threshold);
        }
        else
        {
            removedCount = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref var pixel = ref row[x];
                        if (IsColorMatch(pixel, targetColor, threshold))
                        {
                            pixel = new Rgba32(0, 0, 0, 0);
                            removedCount++;
                        }
                        else if (pixel.A == 255)
                        {
                            var distance = ColorDistance(pixel, targetColor);
                            if (distance < threshold * 2)
                            {
                                var alpha = (byte)Math.Clamp((int)(255.0 * distance / (threshold * 2)), 0, 255);
                                pixel = new Rgba32(pixel.R, pixel.G, pixel.B, alpha);
                                if (alpha == 0) removedCount++;
                            }
                        }
                    }
                }
            });
        }

        double ratio = Math.Round((double)removedCount / totalPixels * 100, 2);
        Console.WriteLine($"  已透明化像素: {removedCount}/{totalPixels} ({ratio}%)");

        // 确保输出目录存在
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
        image.SaveAsPng(outputPath, rgbaEncoder);
        var fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"  输出文件大小: {FormatFileSize(fileSize)}");
        Console.WriteLine();

        // 输出后自动做一次透明度检测
        Console.WriteLine("── 输出文件透明度检测 ──");
        Console.WriteLine();
        var report = AnalyzeTransparency(image);
        PrintTransparencyReport(report, image.Width, image.Height);

        Console.WriteLine();
        Console.WriteLine("✓ 透明化处理完成。");

        return 0;
    }
    catch (Exception ex)
    {
        return Error($"处理失败: {ex.Message}");
    }
}

// ═══════════════════════════════════════════════════════════════════
// extract 命令：从 EXE/DLL 提取图标资源
// ═══════════════════════════════════════════════════════════════════

static int RunExtract(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool extract <exe/dll路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext is not ".exe" and not ".dll")
        return Error($"不支持的文件格式: {ext}。仅支持 .exe 和 .dll。");

    // 解析选项
    string? outputDir = null;
    for (int i = 2; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        switch (arg)
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o/--output 需要一个参数。");
                outputDir = args[++i];
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputDir ??= Directory.GetCurrentDirectory();
    Directory.CreateDirectory(outputDir);

    Console.WriteLine($"── 从 PE 文件提取图标 ──");
    Console.WriteLine($"  输入: {filePath}");
    Console.WriteLine($"  输出目录: {Path.GetFullPath(outputDir)}");
    Console.WriteLine();

    try
    {
        var peBytes = File.ReadAllBytes(filePath);
        var icons = ExtractIconsFromPE(peBytes);

        if (icons.Count == 0)
        {
            Console.WriteLine("  未找到图标资源。");
            return 0;
        }

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        int savedCount = 0;

        foreach (var (index, icoData) in icons)
        {
            // 解析 ICO 头部获取尺寸信息
            var sizeInfo = DescribeIcoSizes(icoData);
            var outPath = Path.Combine(outputDir, $"{baseName}_icon{index}.ico");
            File.WriteAllBytes(outPath, icoData);
            var fileSize = new FileInfo(outPath).Length;
            Console.WriteLine($"  [图标 {index}] {sizeInfo} → {Path.GetFileName(outPath)} ({FormatFileSize(fileSize)})");
            savedCount++;
        }

        Console.WriteLine();
        Console.WriteLine($"✓ 共提取 {savedCount} 个图标文件。");
        return 0;
    }
    catch (Exception ex)
    {
        return Error($"提取失败: {ex.Message}");
    }
}

/// <summary>
/// 描述 ICO 文件中包含的尺寸列表。
/// </summary>
static string DescribeIcoSizes(byte[] icoData)
{
    if (icoData.Length < 6) return "?";
    var count = BitConverter.ToUInt16(icoData, 4);
    var sizes = new List<string>();
    for (int i = 0; i < count; i++)
    {
        var entryOffset = 6 + i * 16;
        if (entryOffset + 16 > icoData.Length) break;
        var w = icoData[entryOffset] == 0 ? 256 : icoData[entryOffset];
        var h = icoData[entryOffset + 1] == 0 ? 256 : icoData[entryOffset + 1];
        sizes.Add(w == h ? $"{w}" : $"{w}x{h}");
    }
    return sizes.Count > 0 ? $"{count} 张 ({string.Join("/", sizes)})" : $"{count} 张";
}

/// <summary>
/// 解析 PE 文件的资源段，提取 RT_GROUP_ICON + RT_ICON 并组合为标准 ICO 文件。
/// 纯字节操作，不依赖 Win32 API。
/// </summary>
static List<(int Index, byte[] IcoData)> ExtractIconsFromPE(byte[] pe)
{
    var result = new List<(int, byte[])>();

    // ── PE 头解析 ──
    if (pe.Length < 64 || pe[0] != 'M' || pe[1] != 'Z')
        throw new InvalidDataException("不是有效的 PE 文件（缺少 MZ 签名）。");

    int peOffset = BitConverter.ToInt32(pe, 0x3C);
    if (peOffset + 4 > pe.Length || pe[peOffset] != 'P' || pe[peOffset + 1] != 'E')
        throw new InvalidDataException("不是有效的 PE 文件（缺少 PE 签名）。");

    int coffOffset = peOffset + 4;
    ushort machine = BitConverter.ToUInt16(pe, coffOffset);
    ushort numberOfSections = BitConverter.ToUInt16(pe, coffOffset + 2);
    ushort sizeOfOptionalHeader = BitConverter.ToUInt16(pe, coffOffset + 16);

    int optionalHeaderOffset = coffOffset + 20;
    ushort magic = BitConverter.ToUInt16(pe, optionalHeaderOffset);
    bool isPE32Plus = magic == 0x20B; // PE32+ (64-bit)

    // 获取资源目录 RVA 和 Size (Data Directory index 2 = Resource)
    int dataDirectoryOffset = optionalHeaderOffset + (isPE32Plus ? 112 : 96);
    // Resource 是第 3 个 Data Directory (index=2)
    int resourceDirRVA = BitConverter.ToInt32(pe, dataDirectoryOffset + 2 * 8);
    int resourceDirSize = BitConverter.ToInt32(pe, dataDirectoryOffset + 2 * 8 + 4);

    if (resourceDirRVA == 0 || resourceDirSize == 0)
        return result; // 无资源段

    // ── 查找 .rsrc Section ──
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

    // 将 RVA 转为文件偏移的辅助函数
    int RvaToOffset(int rva) => rsrcFileOffset + (rva - rsrcVirtualAddress);

    // ── 解析资源目录树 ──
    // Level 0: Type → Level 1: Name/ID → Level 2: Language
    // RT_ICON = 3, RT_GROUP_ICON = 14

    var iconEntries = new Dictionary<int, byte[]>();  // icon ID → raw data
    var groupEntries = new List<(int Id, byte[] Data)>(); // group icon entries

    // 读取 IMAGE_RESOURCE_DIRECTORY
    static (ushort NamedEntries, ushort IdEntries) ReadResDir(byte[] data, int offset)
    {
        ushort named = BitConverter.ToUInt16(data, offset + 12);
        ushort id = BitConverter.ToUInt16(data, offset + 14);
        return (named, id);
    }

    // 遍历顶层类型目录
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
        // typeId 高位为1表示名称引用，取低位作为ID；否则直接作为类型ID
        bool isIcon = (typeId & 0x7FFFFFFF) == 3;        // RT_ICON
        bool isGroupIcon = (typeId & 0x7FFFFFFF) == 14;  // RT_GROUP_ICON

        if (!isIcon && !isGroupIcon) continue;

        // Level 1: Name/ID entries
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
                // 直接数据条目（无语言层级，极少见）
                var dataEntry = ReadResourceDataEntry(pe, rsrcFileOffset + (int)(l1OffsetOrDir & 0x7FFFFFFF), pe, RvaToOffset);
                if (dataEntry is not null)
                {
                    if (isIcon) iconEntries[nameId] = dataEntry;
                    else if (isGroupIcon) groupEntries.Add((nameId, dataEntry));
                }
                continue;
            }

            int l2DirOffset = rsrcFileOffset + (int)(l1OffsetOrDir & 0x7FFFFFFF);

            // Level 2: Language entries — 取第一个语言
            var (l2Named, l2Id) = ReadResDir(pe, l2DirOffset);
            int l2Count = l2Named + l2Id;
            if (l2Count == 0) continue;

            int l2EntryOff = l2DirOffset + 16; // 第一个条目
            int l2OffsetOrDir = BitConverter.ToInt32(pe, l2EntryOff + 4);

            bool l2IsDir = (l2OffsetOrDir & 0x80000000) != 0;
            int dataEntryOffset = l2IsDir
                ? rsrcFileOffset + (int)(l2OffsetOrDir & 0x7FFFFFFF)
                : rsrcFileOffset + l2OffsetOrDir;

            if (l2IsDir) continue; // 不应该再有子目录

            var data = ReadResourceDataEntry(pe, rsrcFileOffset + l2OffsetOrDir, pe, RvaToOffset);
            if (data is not null)
            {
                if (isIcon) iconEntries[nameId] = data;
                else if (isGroupIcon) groupEntries.Add((nameId, data));
            }
        }
    }

    // ── 将 RT_GROUP_ICON + RT_ICON 组合为标准 ICO ──
    foreach (var (groupId, groupData) in groupEntries)
    {
        if (groupData.Length < 6) continue;

        ushort reserved = BitConverter.ToUInt16(groupData, 0);
        ushort type = BitConverter.ToUInt16(groupData, 2);
        ushort count = BitConverter.ToUInt16(groupData, 4);

        if (type != 1 || count == 0) continue;

        // 收集此 group 引用的所有 icon 数据
        var entries = new List<(byte[] Header14, byte[] Data)>();

        for (int i = 0; i < count; i++)
        {
            int grpEntryOff = 6 + i * 14;
            if (grpEntryOff + 14 > groupData.Length) break;

            // GRPICONDIRENTRY 的最后 2 字节是 nId（icon 资源 ID），而不是标准 ICO 的 dwImageOffset
            ushort iconId = BitConverter.ToUInt16(groupData, grpEntryOff + 12);

            if (iconEntries.TryGetValue(iconId, out var iconData))
            {
                // 保留前 12 字节（w, h, colorCount, reserved, planes, bitCount, bytesInRes）
                var header = new byte[12];
                Array.Copy(groupData, grpEntryOff, header, 0, 12);
                // 修正 bytesInRes 为实际数据长度（有些 PE 工具写入的 group 条目长度可能不准）
                var actualSize = BitConverter.GetBytes((uint)iconData.Length);
                Array.Copy(actualSize, 0, header, 8, 4);
                entries.Add((header, iconData));
            }
        }

        if (entries.Count == 0) continue;

        // 构造标准 ICO 文件
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ICONDIR
        bw.Write((ushort)0);                    // reserved
        bw.Write((ushort)1);                    // type = ICO
        bw.Write((ushort)entries.Count);         // count

        // 计算数据偏移（ICONDIR 6 + ICONDIRENTRY * N * 16）
        int dataOffset = 6 + entries.Count * 16;

        foreach (var (header, data) in entries)
        {
            // ICONDIRENTRY: 前 12 字节来自 group entry，后 4 字节为文件内偏移
            bw.Write(header, 0, 12);
            bw.Write((uint)dataOffset);
            dataOffset += data.Length;
        }

        // 写入图像数据
        foreach (var (_, data) in entries)
        {
            bw.Write(data);
        }

        bw.Flush();
        result.Add((groupId, ms.ToArray()));
    }

    return result;
}

/// <summary>
/// 读取 IMAGE_RESOURCE_DATA_ENTRY 并返回实际数据。
/// </summary>
static byte[]? ReadResourceDataEntry(byte[] pe, int entryFileOffset, byte[] fullPe, Func<int, int> rvaToOffset)
{
    if (entryFileOffset + 16 > pe.Length) return null;

    int dataRva = BitConverter.ToInt32(pe, entryFileOffset);
    int dataSize = BitConverter.ToInt32(pe, entryFileOffset + 4);

    if (dataRva == 0 || dataSize <= 0) return null;

    // 将数据 RVA 转为文件偏移 — 需要扫描所有 Section
    int dataFileOffset = RvaToFileOffset(fullPe, dataRva);
    if (dataFileOffset < 0 || dataFileOffset + dataSize > fullPe.Length) return null;

    var data = new byte[dataSize];
    Array.Copy(fullPe, dataFileOffset, data, 0, dataSize);
    return data;
}

/// <summary>
/// 通用 RVA→文件偏移转换，遍历所有 Section。
/// </summary>
static int RvaToFileOffset(byte[] pe, int rva)
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

// ═══════════════════════════════════════════════════════════════════
// seticon 命令：设置目录图标
// ═══════════════════════════════════════════════════════════════════

static int RunSetIcon(string[] args)
{
    if (args.Length < 3)
        return Error("用法: IconTool seticon <ico路径> <目录路径>");

    var icoPath = args[1];
    var targetDir = args[2];

    if (!File.Exists(icoPath))
        return Error($"ICO 文件不存在: {icoPath}");

    if (!Path.GetExtension(icoPath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        return Error("图标文件必须是 .ico 格式。");

    // "." 表示当前目录
    targetDir = Path.GetFullPath(targetDir);
    if (!Directory.Exists(targetDir))
        return Error($"目录不存在: {targetDir}");

    Console.WriteLine($"── 设置目录图标 ──");
    Console.WriteLine($"  图标: {Path.GetFullPath(icoPath)}");
    Console.WriteLine($"  目录: {targetDir}");
    Console.WriteLine();

    try
    {
        var icoFileName = Path.GetFileName(icoPath);
        var destIcoPath = Path.Combine(targetDir, icoFileName);

        // 复制 ICO 到目标目录（如果不在同一位置）
        var sourceFullPath = Path.GetFullPath(icoPath);
        if (!string.Equals(sourceFullPath, destIcoPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceFullPath, destIcoPath, overwrite: true);
            Console.WriteLine($"  复制图标: {icoFileName} → {targetDir}");
        }

        // 设置 ICO 文件为隐藏+系统
        File.SetAttributes(destIcoPath, FileAttributes.Hidden | FileAttributes.System);

        // 创建/更新 desktop.ini
        var desktopIniPath = Path.Combine(targetDir, "desktop.ini");

        // 先移除只读属性以便写入（如果已存在）
        if (File.Exists(desktopIniPath))
        {
            var existingAttr = File.GetAttributes(desktopIniPath);
            File.SetAttributes(desktopIniPath, existingAttr & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
        }

        // 写入 desktop.ini
        // 使用 ANSI 编码（默认系统代码页），这是 Windows Shell 读取 desktop.ini 的默认编码
        var iniContent = $"""
            [.ShellClassInfo]
            IconResource={icoFileName},0
            """.Replace("            ", "");

        // desktop.ini 需要 UTF-8 BOM 或 ANSI 编码才能被 Shell 正确读取
        File.WriteAllText(desktopIniPath, iniContent, Encoding.UTF8);
        Console.WriteLine($"  写入: desktop.ini");

        // 设置 desktop.ini 为隐藏+系统
        File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);

        // 设置目标目录为 ReadOnly（Shell 要求此属性才会读取 desktop.ini）
        var dirInfo = new DirectoryInfo(targetDir);
        dirInfo.Attributes |= FileAttributes.ReadOnly;
        Console.WriteLine($"  设置目录属性: ReadOnly");

        // 通知 Shell 刷新
        NotifyShellIconChanged(targetDir);
        Console.WriteLine($"  已通知资源管理器刷新图标缓存");

        Console.WriteLine();
        Console.WriteLine("✓ 目录图标设置完成。");
        Console.WriteLine();
        Console.WriteLine("提示: 如果图标未立即显示，可尝试：");
        Console.WriteLine("  1. 按 F5 刷新资源管理器");
        Console.WriteLine("  2. 关闭并重新打开资源管理器窗口");

        return 0;
    }
    catch (Exception ex)
    {
        return Error($"设置失败: {ex.Message}");
    }
}

/// <summary>
/// 调用 SHChangeNotify 通知 Shell 目录图标已更新，使其立即生效。
/// </summary>
static void NotifyShellIconChanged(string directoryPath)
{
    if (!OperatingSystem.IsWindows()) return;

    try
    {
        ShellNotify.UpdateDirectoryIcon(directoryPath);
    }
    catch
    {
        // 非致命，忽略
    }
}

// ═══════════════════════════════════════════════════════════════════
// convert 命令：格式互转
// ═══════════════════════════════════════════════════════════════════

static int RunConvert(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool convert <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputPath = null;
    string? format = null;
    string sizeList = "16,32,48,256";

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-f" or "--format":
                if (i + 1 >= args.Length) return Error("-f 需要一个参数。");
                format = args[++i].ToLowerInvariant();
                break;
            case "-s" or "--sizes":
                if (i + 1 >= args.Length) return Error("-s 需要一个参数。");
                sizeList = args[++i];
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    var srcExt = Path.GetExtension(filePath).ToLowerInvariant();

    // 自动推断格式
    if (format is null)
    {
        format = srcExt switch
        {
            ".png" => "ico",
            ".ico" => "png",
            ".bmp" => "png",
            _ => "png"
        };
    }

    if (format is not "png" and not "ico" and not "bmp")
        return Error($"不支持的目标格式: {format}。支持 png, ico, bmp。");

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".",
        Path.GetFileNameWithoutExtension(filePath) + "." + format);

    Console.WriteLine($"── 格式转换 ──");
    Console.WriteLine($"  输入: {filePath} ({srcExt})");
    Console.WriteLine($"  输出: {outputPath} (.{format})");

    try
    {
        if (srcExt == ".ico" && format == "png")
        {
            // ICO → PNG：提取最大的那张图
            var icoBytes = File.ReadAllBytes(filePath);
            var largest = ExtractLargestImageFromIco(icoBytes);
            if (largest is null) return Error("无法从 ICO 中提取图像。");
            File.WriteAllBytes(outputPath, largest);
        }
        else if (format == "ico")
        {
            // PNG/BMP → ICO
            var sizes = ParseSizeList(sizeList);
            Console.WriteLine($"  ICO 尺寸: {string.Join("/", sizes)}");
            using var image = Image.Load<Rgba32>(filePath);
            using var square = PadToSquare(image);
            BuildAndSaveIco(square, sizes, outputPath);
        }
        else if (format == "bmp")
        {
            using var image = Image.Load<Rgba32>(filePath);
            image.Save(outputPath, new BmpEncoder());
        }
        else // png
        {
            using var image = Image.Load<Rgba32>(filePath);
            image.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"  输出大小: {FormatFileSize(fileSize)}");
        Console.WriteLine("✓ 转换完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"转换失败: {ex.Message}"); }
}

static int[] ParseSizeList(string s)
{
    var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var sizes = new List<int>();
    foreach (var p in parts)
        if (int.TryParse(p, out var v) && v > 0 && v <= 512)
            sizes.Add(v);
    return sizes.Count > 0 ? sizes.ToArray() : [16, 32, 48, 256];
}

static byte[]? ExtractLargestImageFromIco(byte[] ico)
{
    if (ico.Length < 6) return null;
    var count = BitConverter.ToUInt16(ico, 4);
    int bestSize = 0;
    byte[]? bestData = null;
    for (int i = 0; i < count; i++)
    {
        var off = 6 + i * 16;
        if (off + 16 > ico.Length) break;
        var w = ico[off] == 0 ? 256 : ico[off];
        var h = ico[off + 1] == 0 ? 256 : ico[off + 1];
        var dataSize = BitConverter.ToInt32(ico, off + 8);
        var dataOffset = BitConverter.ToInt32(ico, off + 12);
        if (w * h > bestSize && dataOffset + dataSize <= ico.Length)
        {
            bestSize = w * h;
            bestData = new byte[dataSize];
            Array.Copy(ico, dataOffset, bestData, 0, dataSize);
        }
    }
    if (bestData is null) return null;
    // 尝试将嵌入数据保存为 PNG
    try
    {
        using var ms = new MemoryStream(bestData);
        using var img = Image.Load<Rgba32>(ms);
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        return outMs.ToArray();
    }
    catch { return bestData; }
}

static void BuildAndSaveIco(Image<Rgba32> square, int[] sizes, string outPath)
{
    var icoImages = new List<(int Size, byte[] PngBytes)>();
    foreach (var size in sizes)
    {
        using var resized = square.Clone(ctx => ctx.Resize(size, size, KnownResamplers.Lanczos3));
        icoImages.Add((size, EncodePng(resized)));
    }

    var tempIco = outPath + ".tmp";
    using (var fs = new FileStream(tempIco, FileMode.Create, FileAccess.Write, FileShare.None))
    using (var bw = new BinaryWriter(fs))
    {
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)icoImages.Count);
        var offset = 6 + 16 * icoImages.Count;
        foreach (var (size, png) in icoImages)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)png.Length);
            bw.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in icoImages)
            bw.Write(png);
        bw.Flush();
        fs.Flush(true);
    }
    File.Move(tempIco, outPath, overwrite: true);
}

// ═══════════════════════════════════════════════════════════════════
// resize 命令：批量生成多尺寸
// ═══════════════════════════════════════════════════════════════════

static int RunResize(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool resize <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputDir = null;
    string sizeList = "16,20,24,32,40,48,64,128,256";
    string format = "png";

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputDir = args[++i];
                break;
            case "-s" or "--sizes":
                if (i + 1 >= args.Length) return Error("-s 需要一个参数。");
                sizeList = args[++i];
                break;
            case "-f" or "--format":
                if (i + 1 >= args.Length) return Error("-f 需要一个参数。");
                format = args[++i].ToLowerInvariant();
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputDir ??= Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
    Directory.CreateDirectory(outputDir);
    var sizes = ParseSizeList(sizeList);
    var baseName = Path.GetFileNameWithoutExtension(filePath);

    Console.WriteLine($"── 批量生成多尺寸 ──");
    Console.WriteLine($"  输入: {filePath}");
    Console.WriteLine($"  尺寸: {string.Join(", ", sizes)}");
    Console.WriteLine($"  格式: {format}");
    Console.WriteLine();

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        using var square = PadToSquare(image);

        foreach (var size in sizes)
        {
            using var resized = square.Clone(ctx => ctx.Resize(size, size, KnownResamplers.Lanczos3));
            var outPath = Path.Combine(outputDir, $"{baseName}_{size}x{size}.{format}");

            if (format == "bmp")
                resized.Save(outPath, new BmpEncoder());
            else
                resized.SaveAsPng(outPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });

            var fileSize = new FileInfo(outPath).Length;
            Console.WriteLine($"  {size}x{size} → {Path.GetFileName(outPath)} ({FormatFileSize(fileSize)})");
        }

        Console.WriteLine();
        Console.WriteLine($"✓ 共生成 {sizes.Length} 个文件。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// info 命令：图像元信息
// ═══════════════════════════════════════════════════════════════════

static int RunInfo(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool info <文件路径>");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    var ext = Path.GetExtension(filePath).ToLowerInvariant();

    Console.WriteLine($"── 图像信息: {Path.GetFileName(filePath)} ──");
    Console.WriteLine($"  路径: {Path.GetFullPath(filePath)}");
    Console.WriteLine($"  文件大小: {FormatFileSize(new FileInfo(filePath).Length)}");
    Console.WriteLine($"  格式: {ext.TrimStart('.')}");

    try
    {
        if (ext == ".ico")
        {
            var icoBytes = File.ReadAllBytes(filePath);
            if (icoBytes.Length < 6) return Error("ICO 文件太小。");
            var count = BitConverter.ToUInt16(icoBytes, 4);
            Console.WriteLine($"  包含图像: {count} 张");
            for (int i = 0; i < count; i++)
            {
                var off = 6 + i * 16;
                if (off + 16 > icoBytes.Length) break;
                var w = icoBytes[off] == 0 ? 256 : icoBytes[off];
                var h = icoBytes[off + 1] == 0 ? 256 : icoBytes[off + 1];
                var bpp = BitConverter.ToUInt16(icoBytes, off + 6);
                var dataSize = BitConverter.ToInt32(icoBytes, off + 8);
                Console.WriteLine($"    [{i + 1}] {w}x{h}, {bpp}bpp, {FormatFileSize(dataSize)}");
            }
        }
        else
        {
            using var image = Image.Load<Rgba32>(filePath);
            Console.WriteLine($"  尺寸: {image.Width}x{image.Height}");
            Console.WriteLine($"  像素数: {image.Width * image.Height:N0}");
            Console.WriteLine($"  宽高比: {(double)image.Width / image.Height:F3}");

            // DPI
            Console.WriteLine($"  DPI: {image.Metadata.HorizontalResolution:F0}x{image.Metadata.VerticalResolution:F0}");

            // 透明度快速统计
            int transparent = 0, semiTransparent = 0, total = image.Width * image.Height;
            image.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (row[x].A == 0) transparent++;
                        else if (row[x].A < 255) semiTransparent++;
                    }
                }
            });
            bool hasAlpha = transparent + semiTransparent > 0;
            Console.WriteLine($"  包含Alpha: {(hasAlpha ? "是" : "否")}");
            if (hasAlpha)
            {
                Console.WriteLine($"    完全透明: {transparent:N0} ({(double)transparent / total * 100:F1}%)");
                Console.WriteLine($"    半透明: {semiTransparent:N0} ({(double)semiTransparent / total * 100:F1}%)");
            }
        }

        return 0;
    }
    catch (Exception ex) { return Error($"读取失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// pad 命令：加边距/画布扩展
// ═══════════════════════════════════════════════════════════════════

static int RunPad(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool pad <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputPath = null;
    int padding = -1;       // 绝对像素边距
    int percent = 10;       // 百分比边距（默认 10%）
    string bgColor = "transparent";

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-p" or "--padding":
                if (i + 1 >= args.Length) return Error("-p 需要一个参数。");
                if (!int.TryParse(args[++i], out padding) || padding < 0)
                    return Error("边距必须 >= 0。");
                break;
            case "--percent":
                if (i + 1 >= args.Length) return Error("--percent 需要一个参数。");
                if (!int.TryParse(args[++i], out percent) || percent < 0 || percent > 50)
                    return Error("百分比必须 0-50。");
                break;
            case "-c" or "--color":
                if (i + 1 >= args.Length) return Error("-c 需要一个参数。");
                bgColor = args[++i];
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".",
        Path.GetFileNameWithoutExtension(filePath) + "_padded.png");

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        int pad = padding >= 0 ? padding : (int)(Math.Max(image.Width, image.Height) * percent / 100.0);
        int newW = image.Width + pad * 2;
        int newH = image.Height + pad * 2;

        Rgba32 bg;
        if (bgColor.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            bg = new Rgba32(0, 0, 0, 0);
        else if (!TryParseColor(bgColor, out bg))
            return Error($"无法解析颜色: {bgColor}");

        Console.WriteLine($"── 加边距 ──");
        Console.WriteLine($"  输入: {filePath} ({image.Width}x{image.Height})");
        Console.WriteLine($"  边距: {pad}px");
        Console.WriteLine($"  输出尺寸: {newW}x{newH}");

        using var result = new Image<Rgba32>(newW, newH, bg);
        result.Mutate(ctx => ctx.DrawImage(image, new Point(pad, pad), 1f));

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        result.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Console.WriteLine($"  输出: {outputPath} ({FormatFileSize(new FileInfo(outputPath).Length)})");
        Console.WriteLine("✓ 完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// round 命令：圆角/圆形裁剪
// ═══════════════════════════════════════════════════════════════════

static int RunRound(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool round <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputPath = null;
    int radius = -1;  // -1 = auto
    bool circle = false;

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-r" or "--radius":
                if (i + 1 >= args.Length) return Error("-r 需要一个参数。");
                if (!int.TryParse(args[++i], out radius) || radius < 0)
                    return Error("圆角半径必须 >= 0。");
                break;
            case "--circle":
                circle = true;
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".",
        Path.GetFileNameWithoutExtension(filePath) + "_round.png");

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        int w = image.Width, h = image.Height;
        int side = Math.Min(w, h);

        if (circle)
            radius = side / 2;
        else if (radius < 0)
            radius = (int)(side * 0.15); // 默认 15% 圆角

        Console.WriteLine($"── 圆角裁剪 ──");
        Console.WriteLine($"  输入: {filePath} ({w}x{h})");
        Console.WriteLine($"  模式: {(circle ? "圆形" : $"圆角 r={radius}")}");

        // 通过 Alpha mask 实现圆角：创建一个圆角矩形路径作为 mask
        ApplyRoundedCornersMask(image, radius);

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        image.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Console.WriteLine($"  输出: {outputPath} ({FormatFileSize(new FileInfo(outputPath).Length)})");
        Console.WriteLine("✓ 完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

/// <summary>
/// 通过像素级 Alpha mask 实现圆角矩形裁剪。
/// 不依赖 DrawingOptions/ClipPath API —— 纯数学计算每个像素到圆角的距离。
/// </summary>
static void ApplyRoundedCornersMask(Image<Rgba32> image, int radius)
{
    int w = image.Width, h = image.Height;
    if (radius <= 0) return;
    radius = Math.Min(radius, Math.Min(w, h) / 2);

    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                float alpha = GetRoundedRectAlpha(x, y, w, h, radius);
                if (alpha < 1f)
                {
                    ref var pixel = ref row[x];
                    byte newA = (byte)(pixel.A * alpha);
                    pixel = new Rgba32(pixel.R, pixel.G, pixel.B, newA);
                }
            }
        }
    });
}

/// <summary>
/// 计算像素 (x,y) 在圆角矩形中的 alpha 值 (0~1)。
/// 内部为 1，外部为 0，边缘抗锯齿渐变。
/// </summary>
static float GetRoundedRectAlpha(int x, int y, int w, int h, int r)
{
    // 四个角的圆心坐标
    float cx, cy;

    if (x < r && y < r) { cx = r; cy = r; }                     // 左上
    else if (x >= w - r && y < r) { cx = w - r; cy = r; }       // 右上
    else if (x < r && y >= h - r) { cx = r; cy = h - r; }       // 左下
    else if (x >= w - r && y >= h - r) { cx = w - r; cy = h - r; } // 右下
    else return 1f; // 不在圆角区域

    float dx = x - cx + 0.5f;
    float dy = y - cy + 0.5f;
    float dist = MathF.Sqrt(dx * dx + dy * dy);

    if (dist <= r - 0.5f) return 1f;
    if (dist >= r + 0.5f) return 0f;
    return r + 0.5f - dist; // 抗锯齿渐变
}

// ═══════════════════════════════════════════════════════════════════
// shadow 命令：添加外阴影
// ═══════════════════════════════════════════════════════════════════

static int RunShadow(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool shadow <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputPath = null;
    int blur = 20;
    int offsetX = 4, offsetY = 4;
    string shadowColor = "black";

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-b" or "--blur":
                if (i + 1 >= args.Length) return Error("-b 需要一个参数。");
                if (!int.TryParse(args[++i], out blur) || blur < 0 || blur > 100)
                    return Error("模糊半径必须 0-100。");
                break;
            case "--offset":
                if (i + 1 >= args.Length) return Error("--offset 需要 x,y 参数。");
                var offParts = args[++i].Split(',');
                if (offParts.Length != 2
                    || !int.TryParse(offParts[0], out offsetX)
                    || !int.TryParse(offParts[1], out offsetY))
                    return Error("偏移格式: x,y (如 4,4)");
                break;
            case "-c" or "--color":
                if (i + 1 >= args.Length) return Error("-c 需要一个参数。");
                shadowColor = args[++i];
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".",
        Path.GetFileNameWithoutExtension(filePath) + "_shadow.png");

    if (!TryParseColor(shadowColor, out var sColor))
        return Error($"无法解析颜色: {shadowColor}");

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        int w = image.Width, h = image.Height;

        // 画布需要额外空间容纳阴影+偏移+模糊
        int expand = blur * 2 + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY));
        int canvasW = w + expand * 2;
        int canvasH = h + expand * 2;

        Console.WriteLine($"── 添加阴影 ──");
        Console.WriteLine($"  输入: {filePath} ({w}x{h})");
        Console.WriteLine($"  模糊半径: {blur}, 偏移: ({offsetX},{offsetY})");
        Console.WriteLine($"  画布: {canvasW}x{canvasH}");

        // 1. 创建阴影层：用源图 alpha 生成纯色阴影
        using var shadowLayer = new Image<Rgba32>(canvasW, canvasH, new Rgba32(0, 0, 0, 0));
        int shadowDrawX = expand + offsetX;
        int shadowDrawY = expand + offsetY;

        // 将源图的 alpha 映射为阴影色
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var srcPixel = image[x, y];
                if (srcPixel.A > 0)
                {
                    int sx = shadowDrawX + x, sy = shadowDrawY + y;
                    if (sx >= 0 && sx < canvasW && sy >= 0 && sy < canvasH)
                    {
                        byte shadowA = (byte)(srcPixel.A * 0.5); // 50% 不透明度
                        shadowLayer[sx, sy] = new Rgba32(sColor.R, sColor.G, sColor.B, shadowA);
                    }
                }
            }
        }

        // 2. 对阴影层做高斯模糊
        if (blur > 0)
            shadowLayer.Mutate(ctx => ctx.GaussianBlur(blur));

        // 3. 在阴影层上方绘制原图
        shadowLayer.Mutate(ctx => ctx.DrawImage(image, new Point(expand, expand), 1f));

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        shadowLayer.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Console.WriteLine($"  输出: {outputPath} ({FormatFileSize(new FileInfo(outputPath).Length)})");
        Console.WriteLine("✓ 完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// overlay 命令：角标/水印叠加
// ═══════════════════════════════════════════════════════════════════

static int RunOverlay(string[] args)
{
    if (args.Length < 3)
        return Error("用法: IconTool overlay <底图> <叠加图> [选项]");

    var basePath = args[1];
    var overlayPath = args[2];
    if (!File.Exists(basePath)) return Error($"底图不存在: {basePath}");
    if (!File.Exists(overlayPath)) return Error($"叠加图不存在: {overlayPath}");

    string? outputPath = null;
    string position = "br"; // bottom-right
    int scale = 30;         // 叠加图占底图宽度的百分比
    int margin = 2;         // 边距百分比

    for (int i = 3; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-p" or "--position":
                if (i + 1 >= args.Length) return Error("-p 需要一个参数。");
                position = args[++i].ToLowerInvariant();
                break;
            case "-s" or "--scale":
                if (i + 1 >= args.Length) return Error("-s 需要一个参数。");
                if (!int.TryParse(args[++i], out scale) || scale < 5 || scale > 100)
                    return Error("缩放比例必须 5-100。");
                break;
            case "-m" or "--margin":
                if (i + 1 >= args.Length) return Error("-m 需要一个参数。");
                if (!int.TryParse(args[++i], out margin) || margin < 0 || margin > 50)
                    return Error("边距必须 0-50。");
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".",
        Path.GetFileNameWithoutExtension(basePath) + "_overlay.png");

    try
    {
        using var baseImg = Image.Load<Rgba32>(basePath);
        using var overlayImg = Image.Load<Rgba32>(overlayPath);

        int bw = baseImg.Width, bh = baseImg.Height;
        int targetW = (int)(bw * scale / 100.0);
        int targetH = (int)(overlayImg.Height * ((double)targetW / overlayImg.Width));
        int marginPx = (int)(bw * margin / 100.0);

        using var resizedOverlay = overlayImg.Clone(ctx => ctx.Resize(targetW, targetH, KnownResamplers.Lanczos3));

        var (x, y) = position switch
        {
            "tl" => (marginPx, marginPx),
            "tr" => (bw - targetW - marginPx, marginPx),
            "bl" => (marginPx, bh - targetH - marginPx),
            "br" => (bw - targetW - marginPx, bh - targetH - marginPx),
            "c" or "center" => ((bw - targetW) / 2, (bh - targetH) / 2),
            _ => (bw - targetW - marginPx, bh - targetH - marginPx)
        };

        Console.WriteLine($"── 叠加角标 ──");
        Console.WriteLine($"  底图: {basePath} ({bw}x{bh})");
        Console.WriteLine($"  叠加: {overlayPath} → {targetW}x{targetH}");
        Console.WriteLine($"  位置: {position}, 偏移: ({x},{y})");

        baseImg.Mutate(ctx => ctx.DrawImage(resizedOverlay, new Point(x, y), 1f));

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        baseImg.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Console.WriteLine($"  输出: {outputPath} ({FormatFileSize(new FileInfo(outputPath).Length)})");
        Console.WriteLine("✓ 完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// compose 命令：多图拼合成 ICO
// ═══════════════════════════════════════════════════════════════════

static int RunCompose(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool compose [选项] <文件1> <文件2> ...");

    string? outputPath = null;
    var files = new List<string>();

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            default:
                if (args[i].StartsWith('-'))
                    return Error($"未知选项: {args[i]}");
                files.Add(args[i]);
                break;
        }
    }

    if (files.Count == 0)
        return Error("至少需要一个图片文件。");

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(files[0])) ?? ".",
        "composed.ico");

    Console.WriteLine($"── 多图拼合成 ICO ──");
    Console.WriteLine($"  输入: {files.Count} 个文件");

    try
    {
        var icoImages = new List<(int Size, byte[] PngBytes)>();

        foreach (var f in files)
        {
            if (!File.Exists(f))
            {
                Console.WriteLine($"  ⚠ 跳过不存在的文件: {f}");
                continue;
            }

            using var img = Image.Load<Rgba32>(f);
            // 用实际宽度作为 size（假设正方形或以宽为准）
            int size = img.Width;
            var pngBytes = EncodePng(img);
            icoImages.Add((size, pngBytes));
            Console.WriteLine($"  [{icoImages.Count}] {Path.GetFileName(f)} → {size}x{img.Height}");
        }

        if (icoImages.Count == 0)
            return Error("没有有效的图片文件。");

        // Sort by size
        icoImages.Sort((a, b) => a.Size.CompareTo(b.Size));

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)icoImages.Count);
        var offset = 6 + 16 * icoImages.Count;

        foreach (var (size, png) in icoImages)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)png.Length);
            bw.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in icoImages)
            bw.Write(png);

        bw.Flush();
        fs.Flush(true);

        var fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"\n  输出: {outputPath} ({FormatFileSize(fileSize)}, {icoImages.Count} 张)");
        Console.WriteLine("✓ 完成。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// favicon 命令：一键生成 Web 全套
// ═══════════════════════════════════════════════════════════════════

static int RunFavicon(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool favicon <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    string? outputDir = null;

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputDir = args[++i];
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    outputDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".", "favicon");
    Directory.CreateDirectory(outputDir);

    Console.WriteLine($"── 生成 Web Favicon 全套 ──");
    Console.WriteLine($"  输入: {filePath}");
    Console.WriteLine($"  输出目录: {outputDir}");
    Console.WriteLine();

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        using var square = PadToSquare(image);

        // favicon.ico (16 + 32 + 48)
        BuildAndSaveIco(square, [16, 32, 48], Path.Combine(outputDir, "favicon.ico"));
        Console.WriteLine("  ✓ favicon.ico (16/32/48)");

        // PNG 各尺寸
        var pngSizes = new (int Size, string Name)[]
        {
            (16, "favicon-16x16.png"),
            (32, "favicon-32x32.png"),
            (48, "favicon-48x48.png"),
            (64, "favicon-64x64.png"),
            (96, "favicon-96x96.png"),
            (128, "favicon-128x128.png"),
            (180, "apple-touch-icon.png"),
            (192, "android-chrome-192x192.png"),
            (256, "favicon-256x256.png"),
            (512, "android-chrome-512x512.png"),
        };

        foreach (var (size, name) in pngSizes)
        {
            using var resized = square.Clone(ctx => ctx.Resize(size, size, KnownResamplers.Lanczos3));
            resized.SaveAsPng(Path.Combine(outputDir, name), new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
            Console.WriteLine($"  ✓ {name}");
        }

        // site.webmanifest
        var manifestJson = """
            {
              "name": "",
              "short_name": "",
              "icons": [
                { "src": "/android-chrome-192x192.png", "sizes": "192x192", "type": "image/png" },
                { "src": "/android-chrome-512x512.png", "sizes": "512x512", "type": "image/png" }
              ],
              "theme_color": "#ffffff",
              "background_color": "#ffffff",
              "display": "standalone"
            }
            """;
        File.WriteAllText(Path.Combine(outputDir, "site.webmanifest"), manifestJson, Encoding.UTF8);
        Console.WriteLine("  ✓ site.webmanifest");

        // HTML 片段
        var html = """
            <!-- Favicon -->
            <link rel="icon" type="image/x-icon" href="/favicon.ico">
            <link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png">
            <link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png">
            <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
            <link rel="manifest" href="/site.webmanifest">
            """;
        File.WriteAllText(Path.Combine(outputDir, "_head.html"), html, Encoding.UTF8);
        Console.WriteLine("  ✓ _head.html (HTML link 标签片段)");

        Console.WriteLine();
        Console.WriteLine($"✓ 共生成 {pngSizes.Length + 3} 个文件。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// sheet 命令：Sprite Sheet 合并
// ═══════════════════════════════════════════════════════════════════

static int RunSheet(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool sheet [选项] <文件1> <文件2> ...");

    string? outputPath = null;
    string? jsonPath = null;
    int cellSize = 0; // 0 = auto
    var files = new List<string>();

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o 需要一个参数。");
                outputPath = args[++i];
                break;
            case "--json":
                if (i + 1 >= args.Length) return Error("--json 需要一个参数。");
                jsonPath = args[++i];
                break;
            case "-s" or "--size":
                if (i + 1 >= args.Length) return Error("-s 需要一个参数。");
                if (!int.TryParse(args[++i], out cellSize) || cellSize < 1)
                    return Error("单元格尺寸必须 >= 1。");
                break;
            default:
                if (args[i].StartsWith('-'))
                    return Error($"未知选项: {args[i]}");
                files.Add(args[i]);
                break;
        }
    }

    if (files.Count == 0)
        return Error("至少需要一个图片文件或目录。");

    // 展开目录为其中的图片文件
    var expanded = new List<string>();
    foreach (var f in files)
    {
        if (Directory.Exists(f))
        {
            expanded.AddRange(Directory.EnumerateFiles(f, "*.*")
                .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }
        else
            expanded.Add(f);
    }
    files = expanded;

    if (files.Count == 0)
        return Error("展开目录后没有找到任何图片文件。");

    outputPath ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(files[0])) ?? ".",
        "spritesheet.png");

    Console.WriteLine($"── Sprite Sheet 合并 ──");
    Console.WriteLine($"  输入: {files.Count} 个文件");

    try
    {
        // 加载所有图片
        var images = new List<(string Name, Image<Rgba32> Img)>();
        foreach (var f in files)
        {
            if (!File.Exists(f))
            {
                Console.WriteLine($"  ⚠ 跳过不存在的文件: {f}");
                continue;
            }
            images.Add((Path.GetFileNameWithoutExtension(f), Image.Load<Rgba32>(f)));
        }

        if (images.Count == 0)
            return Error("没有有效的图片文件。");

        // 确定单元格大小
        if (cellSize == 0)
            cellSize = images.Max(i => Math.Max(i.Img.Width, i.Img.Height));

        // 计算网格
        int cols = (int)Math.Ceiling(Math.Sqrt(images.Count));
        int rows = (int)Math.Ceiling((double)images.Count / cols);
        int sheetW = cols * cellSize;
        int sheetH = rows * cellSize;

        Console.WriteLine($"  单元格: {cellSize}x{cellSize}");
        Console.WriteLine($"  网格: {cols}x{rows} ({sheetW}x{sheetH})");

        using var sheet = new Image<Rgba32>(sheetW, sheetH, new Rgba32(0, 0, 0, 0));
        var spriteJsonEntries = new List<string>();

        for (int idx = 0; idx < images.Count; idx++)
        {
            var (name, img) = images[idx];
            int col = idx % cols;
            int row = idx / cols;
            int x = col * cellSize;
            int y = row * cellSize;

            // 居中绘制到单元格
            int drawX = x + (cellSize - img.Width) / 2;
            int drawY = y + (cellSize - img.Height) / 2;

            sheet.Mutate(ctx => ctx.DrawImage(img, new Point(drawX, drawY), 1f));

            spriteJsonEntries.Add($"    {{ \"name\": \"{name}\", \"x\": {x}, \"y\": {y}, \"width\": {cellSize}, \"height\": {cellSize}, \"sourceWidth\": {img.Width}, \"sourceHeight\": {img.Height} }}");

            Console.WriteLine($"  [{idx + 1}] {name} ({img.Width}x{img.Height}) → ({col},{row})");
        }

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        sheet.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        Console.WriteLine($"\n  输出: {outputPath} ({FormatFileSize(new FileInfo(outputPath).Length)})");

        // 输出 JSON 坐标映射
        jsonPath ??= Path.ChangeExtension(outputPath, ".json");
        var jsonSb = new StringBuilder();
        jsonSb.AppendLine("{");
        jsonSb.AppendLine($"  \"image\": \"{Path.GetFileName(outputPath)}\",");
        jsonSb.AppendLine($"  \"cellSize\": {cellSize},");
        jsonSb.AppendLine($"  \"cols\": {cols},");
        jsonSb.AppendLine($"  \"rows\": {rows},");
        jsonSb.AppendLine("  \"sprites\": [");
        jsonSb.AppendLine(string.Join(",\n", spriteJsonEntries));
        jsonSb.AppendLine("  ]");
        jsonSb.AppendLine("}");
        File.WriteAllText(jsonPath, jsonSb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  坐标: {jsonPath}");

        // 输出 CSS
        var cssPath = Path.ChangeExtension(outputPath, ".css");
        var sb = new StringBuilder();
        sb.AppendLine($".sprite {{ display: inline-block; background-image: url('{Path.GetFileName(outputPath)}'); background-repeat: no-repeat; width: {cellSize}px; height: {cellSize}px; }}");
        for (int idx = 0; idx < images.Count; idx++)
        {
            var (name, _) = images[idx];
            int col = idx % cols;
            int row = idx / cols;
            sb.AppendLine($".sprite-{name} {{ background-position: -{col * cellSize}px -{row * cellSize}px; }}");
        }
        File.WriteAllText(cssPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  CSS: {cssPath}");

        // 清理
        foreach (var (_, img) in images)
            img.Dispose();

        Console.WriteLine($"\n✓ 共合并 {images.Count} 个图标。");
        return 0;
    }
    catch (Exception ex) { return Error($"处理失败: {ex.Message}"); }
}

// ═══════════════════════════════════════════════════════════════════
// 辅助函数
// ═══════════════════════════════════════════════════════════════════

static TransparencyReport AnalyzeTransparency(Image<Rgba32> image)
{
    int w = image.Width;
    int h = image.Height;
    int totalPixels = w * h;
    int transparentCount = 0;
    int semiTransparentCount = 0;

    // 统计透明像素
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x].A == 0) transparentCount++;
                else if (row[x].A < 255) semiTransparentCount++;
            }
        }
    });

    // 角点采样
    var cornerPoints = new (int X, int Y)[]
    {
        (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)
    };

    // 近角点采样（向内偏移 min(20, 10% 边长)
    int offset = Math.Min(20, Math.Min(w, h) / 10);
    var nearCornerPoints = new (int X, int Y)[]
    {
        (offset, offset), (w - 1 - offset, offset),
        (offset, h - 1 - offset), (w - 1 - offset, h - 1 - offset)
    };

    // 中心采样（用于判断主体是否不透明）
    var centerPoints = new (int X, int Y)[]
    {
        (w / 2, h / 2),
        (w / 4, h / 4), (w * 3 / 4, h / 4),
        (w / 4, h * 3 / 4), (w * 3 / 4, h * 3 / 4)
    };

    static (int, int, byte, byte, byte, byte) SamplePixel(Image<Rgba32> img, int x, int y)
    {
        var px = img[x, y];
        return (x, y, px.A, px.R, px.G, px.B);
    }

    var cornerSamples = new (int X, int Y, byte A, byte R, byte G, byte B)[8];
    for (int i = 0; i < 4; i++)
        cornerSamples[i] = SamplePixel(image, cornerPoints[i].X, cornerPoints[i].Y);
    for (int i = 0; i < 4; i++)
        cornerSamples[4 + i] = SamplePixel(image, nearCornerPoints[i].X, nearCornerPoints[i].Y);

    var centerSamples = new (int X, int Y, byte A, byte R, byte G, byte B)[centerPoints.Length];
    for (int i = 0; i < centerPoints.Length; i++)
        centerSamples[i] = SamplePixel(image, centerPoints[i].X, centerPoints[i].Y);

    bool cornersTransparent = cornerSamples[0].A == 0 && cornerSamples[1].A == 0
                            && cornerSamples[2].A == 0 && cornerSamples[3].A == 0;
    bool nearCornersTransparent = cornerSamples[4].A == 0 && cornerSamples[5].A == 0
                                && cornerSamples[6].A == 0 && cornerSamples[7].A == 0;

    return new TransparencyReport(
        totalPixels, transparentCount, semiTransparentCount,
        cornersTransparent, nearCornersTransparent,
        cornerSamples, centerSamples
    );
}

static void PrintTransparencyReport(TransparencyReport r, int width, int height, string indent = "")
{
    double transRatio = Math.Round((double)r.TransparentPixels / r.TotalPixels * 100, 2);
    double semiRatio = Math.Round((double)r.SemiTransparentPixels / r.TotalPixels * 100, 2);
    bool hasAnyTransparency = r.TransparentPixels + r.SemiTransparentPixels > 0;

    Console.WriteLine($"{indent}尺寸: {width}x{height}");
    Console.WriteLine($"{indent}总像素: {r.TotalPixels:N0}");
    Console.WriteLine($"{indent}完全透明像素 (A=0): {r.TransparentPixels:N0} ({transRatio}%)");
    Console.WriteLine($"{indent}半透明像素 (0<A<255): {r.SemiTransparentPixels:N0} ({semiRatio}%)");
    Console.WriteLine($"{indent}包含透明通道: {(hasAnyTransparency ? "是" : "否")}");
    Console.WriteLine();

    // 角点信息
    Console.WriteLine($"{indent}■ 角点检测:");
    string[] cornerLabels = ["左上", "右上", "左下", "右下", "左上(内)", "右上(内)", "左下(内)", "右下(内)"];
    for (int i = 0; i < r.CornerSamples.Length; i++)
    {
        var s = r.CornerSamples[i];
        var status = s.A == 0 ? "透明" : s.A == 255 ? "不透明" : $"半透明(A={s.A})";
        Console.WriteLine($"{indent}  {cornerLabels[i],-8} ({s.X,4},{s.Y,4}) A={s.A,3} RGB=({s.R},{s.G},{s.B}) → {status}");
    }
    Console.WriteLine();

    // 中心采样
    Console.WriteLine($"{indent}■ 主体区域采样:");
    foreach (var s in r.CenterSamples)
    {
        var status = s.A == 255 ? "不透明" : s.A == 0 ? "透明" : $"半透明(A={s.A})";
        Console.WriteLine($"{indent}  ({s.X,4},{s.Y,4}) A={s.A,3} RGB=({s.R},{s.G},{s.B}) → {status}");
    }
    Console.WriteLine();

    // 综合判定
    Console.WriteLine($"{indent}■ 判定结果:");
    if (!hasAnyTransparency)
    {
        Console.WriteLine($"{indent}  ✗ 不透明图片 — 没有任何透明像素。");
    }
    else if (r.CornersTransparent && r.NearCornersTransparent)
    {
        bool centerOpaque = true;
        foreach (var s in r.CenterSamples)
            if (s.A < 255) { centerOpaque = false; break; }

        if (centerOpaque)
            Console.WriteLine($"{indent}  ✓ 圆角透明图标 — 四角透明且主体不透明。");
        else
            Console.WriteLine($"{indent}  △ 含透明通道 — 四角透明，但主体区域存在透明/半透明像素。");
    }
    else
    {
        Console.WriteLine($"{indent}  △ 含透明通道 — 但四角不全是透明 (可能不是圆角图标)。");
    }
}

static bool TryParseColor(string value, out Rgba32 color)
{
    color = default;

    switch (value.ToLowerInvariant())
    {
        case "white":
            color = new Rgba32(255, 255, 255, 255);
            return true;
        case "black":
            color = new Rgba32(0, 0, 0, 255);
            return true;
    }

    // #RRGGBB or #RRGGBBAA
    if (value.StartsWith('#') && (value.Length == 7 || value.Length == 9))
    {
        try
        {
            var hex = value[1..];
            byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
            byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
            byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
            byte a = hex.Length == 8 ? byte.Parse(hex[6..8], NumberStyles.HexNumber) : (byte)255;
            color = new Rgba32(r, g, b, a);
            return true;
        }
        catch { return false; }
    }

    return false;
}

static bool IsColorMatch(Rgba32 pixel, Rgba32 target, int threshold)
{
    return Math.Abs(pixel.R - target.R) <= threshold
        && Math.Abs(pixel.G - target.G) <= threshold
        && Math.Abs(pixel.B - target.B) <= threshold;
}

static bool IsBackgroundCandidate(Rgba32 pixel, Rgba32 target, int threshold)
{
    // 已经透明/几乎透明的边缘像素视为可穿透背景，避免扫描在抗锯齿处提前中断。
    if (pixel.A <= 8) return true;
    return IsColorMatch(pixel, target, threshold);
}

static double ColorDistance(Rgba32 a, Rgba32 b)
{
    int dr = a.R - b.R;
    int dg = a.G - b.G;
    int db = a.B - b.B;
    return Math.Sqrt(dr * dr + dg * dg + db * db);
}

/// <summary>
/// 使用四向边缘扫描线 + 交集策略移除背景。
/// 水平方向（从左或从右）和垂直方向（从上或从下）各自独立扫描，
/// 只有同时被水平和垂直方向标记的像素才算背景。
/// 这确保只有真正在角落/边缘浅层的背景被移除，图标内部的深色内容不受影响。
/// </summary>
static int FloodFillTransparent(Image<Rgba32> image, Rgba32 bgColor, int threshold)
{
    int w = image.Width, h = image.Height;
    var verticalBg = new bool[w, h];   // 从上或从下可达
    var horizontalBg = new bool[w, h]; // 从左或从右可达

    // 从上往下扫（每列）
    for (int x = 0; x < w; x++)
    {
        for (int y = 0; y < h; y++)
        {
            if (IsBackgroundCandidate(image[x, y], bgColor, threshold))
                verticalBg[x, y] = true;
            else
                break;
        }
    }

    // 从下往上扫（每列）
    for (int x = 0; x < w; x++)
    {
        for (int y = h - 1; y >= 0; y--)
        {
            if (IsBackgroundCandidate(image[x, y], bgColor, threshold))
                verticalBg[x, y] = true;
            else
                break;
        }
    }

    // 从左往右扫（每行）
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            if (IsBackgroundCandidate(image[x, y], bgColor, threshold))
                horizontalBg[x, y] = true;
            else
                break;
        }
    }

    // 从右往左扫（每行）
    for (int y = 0; y < h; y++)
    {
        for (int x = w - 1; x >= 0; x--)
        {
            if (IsBackgroundCandidate(image[x, y], bgColor, threshold))
                horizontalBg[x, y] = true;
            else
                break;
        }
    }

    // 仅水平和垂直方向交集处才是真正的背景
    var isBackground = new bool[w, h];
    int removedCount = 0;
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            if (verticalBg[x, y] && horizontalBg[x, y])
            {
                isBackground[x, y] = true;
                image[x, y] = new Rgba32(0, 0, 0, 0);
                removedCount++;
            }
        }
    }

    // 对背景-内容交界处做半透明渐变（抗锯齿）
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            if (isBackground[x, y]) continue;
            var pixel = image[x, y];
            if (pixel.A == 0) continue;

            bool adjacentToBackground = false;
            for (int dy = -1; dy <= 1 && !adjacentToBackground; dy++)
                for (int dx = -1; dx <= 1 && !adjacentToBackground; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && isBackground[nx, ny])
                        adjacentToBackground = true;
                }

            if (adjacentToBackground)
            {
                var distance = ColorDistance(pixel, bgColor);
                if (distance < threshold * 3)
                {
                    var alpha = (byte)Math.Clamp((int)(255.0 * distance / (threshold * 3)), 0, 255);
                    image[x, y] = new Rgba32(pixel.R, pixel.G, pixel.B, alpha);
                    if (alpha == 0) removedCount++;
                }
            }
        }
    }

    return removedCount;
}

static int AutoFloodFillTransparent(Image<Rgba32> image, Rgba32 bgColor, out int usedThreshold)
{
    var thresholds = new[] { 3, 6, 10, 14 };
    var totalPixels = image.Width * image.Height;

    int bestRemoved = 0;
    int bestThreshold = thresholds[0];
    Image<Rgba32>? bestResult = null;

    try
    {
        foreach (var t in thresholds)
        {
            using var candidate = image.Clone();
            var removed = FloodFillTransparent(candidate, bgColor, t);
            if (removed <= 0)
                continue;

            var ratio = (double)removed / totalPixels;
            // 防止误伤主体：若透明化比例过高，视为风险方案。
            if (ratio > 0.55)
                continue;

            if (removed > bestRemoved)
            {
                bestRemoved = removed;
                bestThreshold = t;

                bestResult?.Dispose();
                bestResult = candidate.Clone();
            }
        }

        if (bestResult is not null)
        {
            CopyPixels(bestResult, image);
            usedThreshold = bestThreshold;
            return bestRemoved;
        }

        usedThreshold = thresholds[0];
        return 0;
    }
    finally
    {
        bestResult?.Dispose();
    }
}

static void CopyPixels(Image<Rgba32> source, Image<Rgba32> destination)
{
    if (source.Width != destination.Width || source.Height != destination.Height)
        throw new ArgumentException("源图与目标图尺寸不一致，无法复制像素。");

    for (int y = 0; y < source.Height; y++)
    {
        for (int x = 0; x < source.Width; x++)
        {
            destination[x, y] = source[x, y];
        }
    }
}

static string FormatFileSize(long bytes)
{
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
    return $"{bytes / (1024.0 * 1024):F1} MB";
}

static int Error(string message)
{
    Console.Error.WriteLine($"错误: {message}");
    return 1;
}

// ═══════════════════════════════════════════════════════════════════
// crop 命令：居中裁剪缩放
// ═══════════════════════════════════════════════════════════════════

static int RunCrop(string[] args)
{
    if (args.Length < 2)
        return Error("用法: IconTool crop <文件路径> [选项]");

    var filePath = args[1];
    if (!File.Exists(filePath))
        return Error($"文件不存在: {filePath}");

    // 解析选项
    string? outputPath = null;
    int targetSize = 512;

    for (int i = 2; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        switch (arg)
        {
            case "-o" or "--output":
                if (i + 1 >= args.Length) return Error("-o/--output 需要一个参数。");
                outputPath = args[++i];
                break;
            case "-s" or "--size":
                if (i + 1 >= args.Length) return Error("-s/--size 需要一个参数。");
                if (!int.TryParse(args[++i], out targetSize) || targetSize < 1 || targetSize > 4096)
                    return Error("目标尺寸必须为 1-4096 之间的整数。");
                break;
            default:
                return Error($"未知选项: {args[i]}");
        }
    }

    // 确定输出路径
    if (string.IsNullOrEmpty(outputPath))
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(filePath);
        outputPath = Path.Combine(dir, $"{name}_cropped.png");
    }

    Console.WriteLine($"── 居中裁剪缩放 ──");
    Console.WriteLine($"  输入: {filePath}");
    Console.WriteLine($"  目标尺寸: {targetSize}x{targetSize}");
    Console.WriteLine($"  输出: {outputPath}");
    Console.WriteLine();

    try
    {
        using var image = Image.Load<Rgba32>(filePath);
        Console.WriteLine($"  原始尺寸: {image.Width}x{image.Height}");

        using var cropped = CenterCropAndResize(image, targetSize);
        Console.WriteLine($"  裁剪后尺寸: {cropped.Width}x{cropped.Height}");

        // 确保输出目录存在
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
        cropped.SaveAsPng(outputPath, rgbaEncoder);
        var fileSize = new FileInfo(outputPath).Length;
        Console.WriteLine($"  输出文件大小: {FormatFileSize(fileSize)}");
        Console.WriteLine();
        Console.WriteLine("✓ 裁剪完成。");

        return 0;
    }
    catch (Exception ex)
    {
        return Error($"处理失败: {ex.Message}");
    }
}

/// <summary>
/// 居中裁剪并缩放到目标正方形尺寸。
/// 对于非正方形图像，先取短边为正方形边长居中裁剪，再缩放到 targetSize。
/// 对于已是正方形的图像，直接缩放。
/// </summary>
static Image<Rgba32> CenterCropAndResize(Image<Rgba32> source, int targetSize)
{
    int w = source.Width, h = source.Height;
    int side = Math.Min(w, h);

    // 居中裁剪为正方形
    int cropX = (w - side) / 2;
    int cropY = (h - side) / 2;

    var result = source.Clone(ctx =>
    {
        if (w != h)
            ctx.Crop(new Rectangle(cropX, cropY, side, side));
        ctx.Resize(targetSize, targetSize, KnownResamplers.Lanczos3);
    });

    return result;
}

// ═══════════════════════════════════════════════════════════════════
// browseicons 命令：浏览目录中 EXE 的图标并设置为目录图标
// ═══════════════════════════════════════════════════════════════════

static int RunBrowseIcons(string[] args)
{
    var targetDir = args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory();
    targetDir = Path.GetFullPath(targetDir);

    if (!Directory.Exists(targetDir))
        return Error($"目录不存在: {targetDir}");

    Console.WriteLine($"── 浏览目录中的 EXE 图标 ──");
    Console.WriteLine($"  目录: {targetDir}");
    Console.WriteLine();

    // 扫描 EXE 文件
    var exeFiles = Directory.GetFiles(targetDir, "*.exe", SearchOption.TopDirectoryOnly);
    if (exeFiles.Length == 0)
    {
        Console.WriteLine("  该目录下没有找到 EXE 文件。");
        return 0;
    }

    // 提取所有图标
    var allIcons = new List<(string ExeName, int GroupId, byte[] IcoData, string SizeInfo)>();

    foreach (var exePath in exeFiles)
    {
        try
        {
            var peBytes = File.ReadAllBytes(exePath);
            var icons = ExtractIconsFromPE(peBytes);
            var exeName = Path.GetFileName(exePath);

            foreach (var (index, icoData) in icons)
            {
                var sizeInfo = DescribeIcoSizes(icoData);
                allIcons.Add((exeName, index, icoData, sizeInfo));
            }
        }
        catch
        {
            // 跳过无法解析的文件
        }
    }

    if (allIcons.Count == 0)
    {
        Console.WriteLine("  未从任何 EXE 中找到图标资源。");
        return 0;
    }

    // 列出所有图标
    Console.WriteLine($"  找到 {allIcons.Count} 个图标:");
    Console.WriteLine();

    for (int i = 0; i < allIcons.Count; i++)
    {
        var (exeName, groupId, icoData, sizeInfo) = allIcons[i];
        // 计算最大尺寸以标记高清图标
        int maxSize = GetIcoMaxSize(icoData);
        var hdTag = maxSize >= 256 ? " ★HD" : "";
        Console.WriteLine($"  [{i + 1}] {exeName} → 图标组 {groupId}: {sizeInfo}{hdTag}  ({FormatFileSize(icoData.Length)})");
    }

    Console.WriteLine();
    Console.WriteLine("  输入编号选择图标设为目录图标，输入 0 或 q 退出:");
    Console.Write("  > ");

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input) || input == "0" || input.Equals("q", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  已取消。");
        return 0;
    }

    if (!int.TryParse(input, out var selection) || selection < 1 || selection > allIcons.Count)
        return Error($"无效的选择: {input}");

    var selected = allIcons[selection - 1];
    Console.WriteLine();
    Console.WriteLine($"  选中: {selected.ExeName} 图标组 {selected.GroupId} ({selected.SizeInfo})");

    // 生成 ICO 文件名并保存
    var icoFileName = $"{Path.GetFileNameWithoutExtension(selected.ExeName)}_icon{selected.GroupId}.ico";
    var icoPath = Path.Combine(targetDir, icoFileName);
    File.WriteAllBytes(icoPath, selected.IcoData);
    Console.WriteLine($"  已保存图标: {icoFileName}");

    // 设置 ICO 文件为隐藏+系统
    File.SetAttributes(icoPath, FileAttributes.Hidden | FileAttributes.System);

    // 创建/更新 desktop.ini
    var desktopIniPath = Path.Combine(targetDir, "desktop.ini");
    if (File.Exists(desktopIniPath))
    {
        var existingAttr = File.GetAttributes(desktopIniPath);
        File.SetAttributes(desktopIniPath,
            existingAttr & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
    }

    var iniContent = $"[.ShellClassInfo]\nIconResource={icoFileName},0\n";
    File.WriteAllText(desktopIniPath, iniContent, Encoding.UTF8);
    Console.WriteLine($"  写入: desktop.ini");

    File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);

    var dirInfo = new DirectoryInfo(targetDir);
    dirInfo.Attributes |= FileAttributes.ReadOnly;
    Console.WriteLine($"  设置目录属性: ReadOnly");

    NotifyShellIconChanged(targetDir);
    Console.WriteLine($"  已通知资源管理器刷新图标缓存");

    Console.WriteLine();
    Console.WriteLine("✓ 目录图标设置完成。");
    return 0;
}

/// <summary>
/// 获取 ICO 中最大尺寸。
/// </summary>
static int GetIcoMaxSize(byte[] icoData)
{
    if (icoData.Length < 6) return 0;
    var count = BitConverter.ToUInt16(icoData, 4);
    int maxSize = 0;
    for (int i = 0; i < count; i++)
    {
        var entryOffset = 6 + i * 16;
        if (entryOffset + 16 > icoData.Length) break;
        var w = icoData[entryOffset] == 0 ? 256 : icoData[entryOffset];
        var h = icoData[entryOffset + 1] == 0 ? 256 : icoData[entryOffset + 1];
        maxSize = Math.Max(maxSize, Math.Max(w, h));
    }
    return maxSize;
}

// ═══════════════════════════════════════════════════════════════════
// 自动处理模式（无参数）
// ═══════════════════════════════════════════════════════════════════

static int RunAutoProcess()
{
    var currentDir = Directory.GetCurrentDirectory();
    Console.WriteLine($"══ IconTool 自动处理模式 ══");
    Console.WriteLine($"工作目录: {currentDir}");
    Console.WriteLine();

    // 收集 png/jpg/jpeg 文件
    var extensions = new[] { "*.png", "*.jpg", "*.jpeg" };
    var files = new List<string>();
    foreach (var ext in extensions)
        files.AddRange(Directory.GetFiles(currentDir, ext, SearchOption.TopDirectoryOnly));

    if (files.Count == 0)
    {
        Console.WriteLine("当前目录没有找到 PNG/JPG/JPEG 文件。");
        return 0;
    }

    Console.WriteLine($"找到 {files.Count} 个图片文件。");
    Console.WriteLine();

    // 创建按时间命名的备份目录
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var backupDir = Path.Combine(currentDir, $"backup_{timestamp}");
    Directory.CreateDirectory(backupDir);

    // 日志
    var logPath = Path.Combine(backupDir, "处理日志.txt");
    var logLines = new List<string>
    {
        $"IconTool 自动处理日志",
        $"处理时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        $"工作目录: {currentDir}",
        $"备份目录: {backupDir}",
        $"待处理文件数: {files.Count}",
        new string('─', 60)
    };

    int successCount = 0, skipCount = 0, failCount = 0;

    foreach (var filePath in files)
    {
        var fileName = Path.GetFileName(filePath);
        var nameNoExt = Path.GetFileNameWithoutExtension(filePath);
        Console.WriteLine($"── 处理: {fileName} ──");
        logLines.Add("");
        logLines.Add($"[文件] {fileName}");

        try
        {
            using var image = Image.Load<Rgba32>(filePath);
            logLines.Add($"  尺寸: {image.Width}x{image.Height}");

            // 1. 检查是否已透明
            var report = AnalyzeTransparency(image);
            bool alreadyTransparent = report.TransparentPixels + report.SemiTransparentPixels > 0;

            if (alreadyTransparent && report.CornersTransparent)
            {
                Console.WriteLine($"  已是透明图片（透明像素 {report.TransparentPixels}），跳过透明化。");
                logLines.Add($"  结果: 跳过透明化 — 已是透明图片（透明像素={report.TransparentPixels}, 占比={Math.Round((double)report.TransparentPixels / report.TotalPixels * 100, 2)}%）");

                // 备份源文件
                var backupPathSkip = Path.Combine(backupDir, fileName);
                File.Copy(filePath, backupPathSkip, overwrite: true);
                logLines.Add($"  备份源文件: {fileName}");

                // 居中裁剪缩放到 512x512
                using var croppedSkip = CenterCropAndResize(image, 512);
                Console.WriteLine($"  裁剪缩放: {image.Width}x{image.Height} → {croppedSkip.Width}x{croppedSkip.Height}");
                logLines.Add($"  裁剪缩放: {image.Width}x{image.Height} → {croppedSkip.Width}x{croppedSkip.Height}");

                // 保存裁剪后的 PNG
                var extSkip = Path.GetExtension(filePath).ToLowerInvariant();
                string outputPngPathSkip;
                if (extSkip is ".jpg" or ".jpeg")
                {
                    outputPngPathSkip = Path.Combine(currentDir, nameNoExt + ".png");
                    File.Delete(filePath);
                    logLines.Add($"  源文件为 JPG，转换为 PNG: {nameNoExt}.png");
                }
                else
                {
                    outputPngPathSkip = filePath;
                }

                var rgbaEncoderSkip = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
                croppedSkip.SaveAsPng(outputPngPathSkip, rgbaEncoderSkip);
                var pngFileSizeSkip = new FileInfo(outputPngPathSkip).Length;
                Console.WriteLine($"  输出: {Path.GetFileName(outputPngPathSkip)} ({FormatFileSize(pngFileSizeSkip)})");
                logLines.Add($"  输出文件: {Path.GetFileName(outputPngPathSkip)} ({FormatFileSize(pngFileSizeSkip)})");

                // 生成 ICO
                GenerateIcoForFile(outputPngPathSkip, croppedSkip, backupDir, logLines, currentDir);

                logLines.Add($"  结果: 成功（跳过透明化，执行裁剪）");
                successCount++;
                Console.WriteLine();
                continue;
            }

            // 2. 采样四周像素判断背景色
            var borderColor = DetectBorderColor(image);
            if (borderColor is null)
            {
                Console.WriteLine($"  四周颜色不一致，不适合自动透明化，跳过。");
                logLines.Add($"  结果: 跳过 — 四周像素颜色不一致，无法确定单一背景色");
                skipCount++;
                Console.WriteLine();
                continue;
            }

            var (bgColor, bgName) = borderColor.Value;
            Console.WriteLine($"  检测到背景色: {bgName} (R={bgColor.R}, G={bgColor.G}, B={bgColor.B})");
            logLines.Add($"  检测到背景色: {bgName} (R={bgColor.R}, G={bgColor.G}, B={bgColor.B})");

            // 3. 备份源文件
            var backupPath = Path.Combine(backupDir, fileName);
            File.Copy(filePath, backupPath, overwrite: true);
            logLines.Add($"  备份源文件: {fileName} → {Path.GetFileName(backupPath)}");

            // 4. 透明化处理（自动策略）
            // - 四向扫描交集，避免误删主体
            // - 使用自适应阈值（3/6/10/14）提升近白/近黑边框命中率
            // - 允许穿透已透明像素，减少抗锯齿边缘造成的扫描中断
            int removedCount = AutoFloodFillTransparent(image, bgColor, out var usedThreshold);
            int totalPixels = image.Width * image.Height;

            double ratio = Math.Round((double)removedCount / totalPixels * 100, 2);
            Console.WriteLine($"  自动阈值: {usedThreshold}");
            Console.WriteLine($"  透明化: {removedCount}/{totalPixels} 像素 ({ratio}%)");
            logLines.Add($"  自动阈值: {usedThreshold}");
            logLines.Add($"  透明化像素: {removedCount}/{totalPixels} ({ratio}%)");

            // 5. 居中裁剪缩放到 512x512
            using var cropped = CenterCropAndResize(image, 512);
            Console.WriteLine($"  裁剪缩放: {image.Width}x{image.Height} → {cropped.Width}x{cropped.Height}");
            logLines.Add($"  裁剪缩放: {image.Width}x{image.Height} → {cropped.Width}x{cropped.Height}");

            // 6. 覆盖写入为 PNG（如果源是 jpg 则写为同名 .png）
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string outputPngPath;
            if (ext is ".jpg" or ".jpeg")
            {
                outputPngPath = Path.Combine(currentDir, nameNoExt + ".png");
                // 删除原 jpg 文件（已备份）
                File.Delete(filePath);
                logLines.Add($"  源文件为 JPG，转换为 PNG: {nameNoExt}.png（原 JPG 已备份并删除）");
            }
            else
            {
                outputPngPath = filePath;
            }

            var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
            cropped.SaveAsPng(outputPngPath, rgbaEncoder);
            var pngFileSize = new FileInfo(outputPngPath).Length;
            Console.WriteLine($"  输出: {Path.GetFileName(outputPngPath)} ({FormatFileSize(pngFileSize)})");
            logLines.Add($"  输出文件: {Path.GetFileName(outputPngPath)} ({FormatFileSize(pngFileSize)})");

            // 7. 生成 ICO
            GenerateIcoForFile(outputPngPath, cropped, backupDir, logLines, currentDir);

            logLines.Add($"  结果: 成功");
            successCount++;
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  处理失败: {ex.Message}");
            logLines.Add($"  结果: 失败 — {ex.Message}");
            failCount++;
            Console.WriteLine();
        }
    }

    // 写入汇总
    logLines.Add("");
    logLines.Add(new string('═', 60));
    logLines.Add($"汇总: 共 {files.Count} 个文件, 成功 {successCount}, 跳过 {skipCount}, 失败 {failCount}");

    File.WriteAllLines(logPath, logLines, Encoding.UTF8);

    Console.WriteLine(new string('═', 60));
    Console.WriteLine($"处理完成: 成功={successCount}, 跳过={skipCount}, 失败={failCount}");
    Console.WriteLine($"备份目录: {backupDir}");
    Console.WriteLine($"处理日志: {logPath}");

    return failCount > 0 ? 1 : 0;
}

/// <summary>
/// 采样图像四个角区域的像素，判断是否为单一背景色（白/黑）。
/// 改为角区域采样而非整条边，适配圆角矩形图标（图标内容延伸到边缘中段）。
/// 返回 null 表示四角颜色不一致、不适合自动处理。
/// </summary>
static (Rgba32 Color, string Name)? DetectBorderColor(Image<Rgba32> image)
{
    int w = image.Width, h = image.Height;

    // 角区域大小：图像短边的 5%，对于紧贴边缘的圆角矩形图标足够覆盖角落背景
    // 同时不至于深入到圆角弧线内侧的图标内容
    int regionSize = Math.Max(4, Math.Min(w, h) * 5 / 100);
    int step = Math.Max(1, regionSize / 10); // 每个角区域约 10x10=100 个采样点

    var cornerPixels = new List<Rgba32>();

    // 采样四个角区域（矩形块而非单条边线）
    // 左上角
    for (int y = 0; y < regionSize; y += step)
        for (int x = 0; x < regionSize; x += step)
            cornerPixels.Add(image[x, y]);

    // 右上角
    for (int y = 0; y < regionSize; y += step)
        for (int x = w - regionSize; x < w; x += step)
            cornerPixels.Add(image[x, y]);

    // 左下角
    for (int y = h - regionSize; y < h; y += step)
        for (int x = 0; x < regionSize; x += step)
            cornerPixels.Add(image[x, y]);

    // 右下角
    for (int y = h - regionSize; y < h; y += step)
        for (int x = w - regionSize; x < w; x += step)
            cornerPixels.Add(image[x, y]);

    if (cornerPixels.Count == 0) return null;

    // 统计：看是否绝大多数接近白色或黑色
    int whiteCount = 0, blackCount = 0;
    int otherCount = 0;
    int tolerance = 30;

    foreach (var px in cornerPixels)
    {
        if (px.A < 128) continue; // 已透明的忽略
        if (px.R >= (255 - tolerance) && px.G >= (255 - tolerance) && px.B >= (255 - tolerance))
            whiteCount++;
        else if (px.R <= tolerance && px.G <= tolerance && px.B <= tolerance)
            blackCount++;
        else
            otherCount++;
    }

    double totalOpaque = cornerPixels.Count;
    double whiteRatio = whiteCount / totalOpaque;
    double blackRatio = blackCount / totalOpaque;

    // >=80% 的角区域像素为同一颜色才认为可处理
    if (whiteRatio >= 0.80)
        return (new Rgba32(255, 255, 255, 255), "white");
    if (blackRatio >= 0.80)
        return (new Rgba32(0, 0, 0, 255), "black");

    return null;
}

/// <summary>
/// 用透明化后的图像生成同名 ICO（16/32/48/256），备份已有 ICO。
/// </summary>
static void GenerateIcoForFile(string pngPath, Image<Rgba32> sourceImage,
    string backupDir, List<string> logLines, string workDir)
{
    var nameNoExt = Path.GetFileNameWithoutExtension(pngPath);
    var icoPath = Path.Combine(workDir, nameNoExt + ".ico");

    // 如果已有 ICO，先备份
    if (File.Exists(icoPath))
    {
        var backupIcoPath = Path.Combine(backupDir, nameNoExt + ".ico");
        File.Copy(icoPath, backupIcoPath, overwrite: true);
        Console.WriteLine($"  备份已有 ICO: {nameNoExt}.ico");
        logLines.Add($"  备份已有 ICO: {nameNoExt}.ico → {Path.GetFileName(backupIcoPath)}");
    }

    try
    {
        // 补齐为正方形
        using var square = PadToSquare(sourceImage);

        var sizes = new[] { 16, 32, 48, 256 };
        var icoImages = new List<(int Size, byte[] PngBytes)>(sizes.Length);

        foreach (var size in sizes)
        {
            using var resized = square.Clone(ctx => ctx.Resize(size, size, KnownResamplers.NearestNeighbor));
            icoImages.Add((size, EncodePng(resized)));
        }

        // 写 ICO
        var tempIco = icoPath + ".tmp";
        using (var fs = new FileStream(tempIco, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            // ICONDIR
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)icoImages.Count);

            var offset = 6 + 16 * icoImages.Count;
            foreach (var (size, png) in icoImages)
            {
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)png.Length);
                bw.Write((uint)offset);
                offset += png.Length;
            }

            foreach (var (_, png) in icoImages)
            {
                bw.Write(png);
            }

            bw.Flush();
            fs.Flush(true);
        }

        File.Move(tempIco, icoPath, overwrite: true);
        var icoSize = new FileInfo(icoPath).Length;
        Console.WriteLine($"  生成 ICO: {nameNoExt}.ico ({FormatFileSize(icoSize)}, 尺寸: {string.Join("/", sizes)})");
        logLines.Add($"  生成 ICO: {nameNoExt}.ico ({FormatFileSize(icoSize)}, 尺寸: {string.Join("/", sizes)})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  生成 ICO 失败: {ex.Message}");
        logLines.Add($"  生成 ICO: 失败 — {ex.Message}");
    }
}

static Image<Rgba32> PadToSquare(Image<Rgba32> source)
{
    var size = Math.Max(source.Width, source.Height);
    if (source.Width == size && source.Height == size)
        return source.Clone();

    var dest = new Image<Rgba32>(size, size);
    var offsetX = (size - source.Width) / 2;
    var offsetY = (size - source.Height) / 2;
    dest.Mutate(ctx => ctx.DrawImage(source, new Point(offsetX, offsetY), 1f));
    return dest;
}

static byte[] EncodePng(Image<Rgba32> image)
{
    using var ms = new MemoryStream();
    var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
    image.SaveAsPng(ms, rgbaEncoder);
    return ms.ToArray();
}

// ═══════════════════════════════════════════════════════════════════
// 类型声明（必须在顶级语句之后）
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Shell 通知 P/Invoke 封装。
/// </summary>
static partial class ShellNotify
{
    // SHCNE_UPDATEDIR = 0x00001000, SHCNF_PATHW = 0x0005
    // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int wEventId, int uFlags, string? dwItem1, string? dwItem2);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static void UpdateDirectoryIcon(string directoryPath)
    {
        SHChangeNotify(0x00001000, 0x0005, directoryPath, null);
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }
}

record TransparencyReport(
    int TotalPixels,
    int TransparentPixels,
    int SemiTransparentPixels,
    bool CornersTransparent,
    bool NearCornersTransparent,
    (int X, int Y, byte A, byte R, byte G, byte B)[] CornerSamples,
    (int X, int Y, byte A, byte R, byte G, byte B)[] CenterSamples
);

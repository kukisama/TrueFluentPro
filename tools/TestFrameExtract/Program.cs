using System;
using System.IO;
using System.Runtime.InteropServices;
using MediaFoundationApi = NAudio.MediaFoundation.MediaFoundationApi;
using SkiaSharp;
using Vortice.MediaFoundation;

const string videoPath = @"C:\Users\a9y\AppData\Roaming\TrueFluentPro\Sessions\library\video\2026\04\vid_001_38895c18.mp4";
const string outputDir = @"c:\Scripts\TranslationToolUI\tools\TestFrameExtract\output";

Directory.CreateDirectory(outputDir);

if (!File.Exists(videoPath))
{
    Console.WriteLine($"[ERROR] 视频文件不存在: {videoPath}");
    return;
}

Console.WriteLine($"视频文件: {videoPath}");
Console.WriteLine($"文件大小: {new FileInfo(videoPath).Length:N0} bytes");
Console.WriteLine();

// 1. 初始化 MF
Console.WriteLine("=== 初始化 Media Foundation ===");
try
{
    MediaFoundationApi.Startup();
    Console.WriteLine("[OK] MF 初始化成功");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] MF 初始化失败: {ex}");
    return;
}

// 2. 尝试创建 SourceReader
Console.WriteLine();
Console.WriteLine("=== 创建 SourceReader (不设置处理选项) ===");
try
{
    var attribs = MediaFactory.MFCreateAttributes(0);
    var reader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, attribs);
    Console.WriteLine("[OK] SourceReader 创建成功");

    // 打印原始媒体类型
    var nativeType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
    var majorType = nativeType.GetGUID(MediaTypeAttributeKeys.MajorType);
    var subType = nativeType.GetGUID(MediaTypeAttributeKeys.Subtype);
    Console.WriteLine($"  MajorType: {majorType}");
    Console.WriteLine($"  SubType:   {subType}");
    Console.WriteLine($"  SubType 是 Video? {majorType == MediaTypeGuids.Video}");

    // 匹配已知视频子类型
    if (subType == VideoFormatGuids.H264) Console.WriteLine("  编码: H.264");
    else if (subType == VideoFormatGuids.Hevc) Console.WriteLine("  编码: HEVC/H.265");
    else if (subType == VideoFormatGuids.NV12) Console.WriteLine("  编码: NV12");
    else Console.WriteLine($"  编码: 未知子类型 {subType}");

    var frameSize = nativeType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
    int w = (int)(frameSize >> 32);
    int h = (int)(frameSize & 0xFFFFFFFF);
    Console.WriteLine($"  帧尺寸: {w} x {h}");

    nativeType.Dispose();
    reader.Dispose();
    attribs.Dispose();
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
}

// 3. 尝试创建配置好 RGB32 输出的 reader
Console.WriteLine();
Console.WriteLine("=== 创建配置好的 SourceReader (RGB32 输出) ===");
IMFSourceReader? configuredReader = null;
int width = 0, height = 0;
long durationTicks = 0;
try
{
    var attributes = MediaFactory.MFCreateAttributes(1);
    attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);

    configuredReader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, attributes);
    attributes.Dispose();

    using var requestedMediaType = MediaFactory.MFCreateMediaType();
    requestedMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
    requestedMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
    configuredReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, requestedMediaType);
    configuredReader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

    Console.WriteLine("[OK] RGB32 输出配置成功");

    using var currentMediaType = configuredReader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
    var frameSize = currentMediaType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
    width = (int)(frameSize >> 32);
    height = (int)(frameSize & 0xFFFFFFFF);
    Console.WriteLine($"  输出帧尺寸: {width} x {height}");

    var durationObj = configuredReader.GetPresentationAttribute(
        SourceReaderIndex.MediaSource,
        PresentationDescriptionAttributeKeys.Duration);
    Console.WriteLine($"  Duration 返回类型: {durationObj.GetType().FullName}");
    Console.WriteLine($"  Duration 值: {durationObj}");
    // 尝试不同方式提取值
    if (durationObj is SharpGen.Runtime.Win32.Variant variant)
    {
        Console.WriteLine($"  Variant.ElementType: {variant.ElementType}");
        Console.WriteLine($"  Variant.Value: {variant.Value} (type: {variant.Value?.GetType().FullName})");
        if (variant.Value is long l) durationTicks = l;
        else if (variant.Value is ulong ul) durationTicks = (long)ul;
        else if (variant.Value is int i2) durationTicks = i2;
        else if (variant.Value is uint ui) durationTicks = ui;
        else durationTicks = Convert.ToInt64(variant.Value);
    }
    else
    {
        durationTicks = Convert.ToInt64(durationObj);
    }
    var durationSec = durationTicks / 10_000_000.0;
    Console.WriteLine($"  视频时长: {durationTicks} ticks ({durationSec:F2}s)");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return;
}

// 4. 读取首帧
Console.WriteLine();
Console.WriteLine("=== 读取首帧 (timestamp=0) ===");
try
{
    var firstPath = Path.Combine(outputDir, "first_frame.png");
    var ok = TrySaveFrame(configuredReader, 0L, width, height, firstPath);
    Console.WriteLine(ok ? $"[OK] 首帧已保存: {firstPath}" : "[FAIL] 首帧保存失败");
    if (ok)
    {
        Console.WriteLine($"  文件大小: {new FileInfo(firstPath).Length:N0} bytes");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    configuredReader.Dispose();
}

// 5. 读取尾帧 (需要新 reader)
Console.WriteLine();
Console.WriteLine("=== 读取尾帧 ===");
try
{
    var attributes2 = MediaFactory.MFCreateAttributes(1);
    attributes2.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
    var reader2 = MediaFactory.MFCreateSourceReaderFromURL(videoPath, attributes2);
    attributes2.Dispose();

    using var reqType2 = MediaFactory.MFCreateMediaType();
    reqType2.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
    reqType2.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
    reader2.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, reqType2);
    reader2.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

    const long backoffTicks = 2_000_000; // 200ms
    var lastTimestamp = durationTicks > backoffTicks ? durationTicks - backoffTicks : 0L;
    Console.WriteLine($"  目标尾帧时间戳: {lastTimestamp} ticks ({lastTimestamp / 10_000_000.0:F2}s)");

    var lastPath = Path.Combine(outputDir, "last_frame.png");
    var ok = TrySaveFrame(reader2, lastTimestamp, width, height, lastPath);
    Console.WriteLine(ok ? $"[OK] 尾帧已保存: {lastPath}" : "[FAIL] 尾帧保存失败");
    if (ok)
    {
        Console.WriteLine($"  文件大小: {new FileInfo(lastPath).Length:N0} bytes");
    }

    reader2.Dispose();
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine();
Console.WriteLine("=== 测试完成 ===");

// ────────────────────────────────────────────
static bool TrySaveFrame(IMFSourceReader sourceReader, long targetTimestamp, int width, int height, string filePath)
{
    if (targetTimestamp > 0)
    {
        sourceReader.SetCurrentPosition(targetTimestamp);
        Console.WriteLine($"  已 Seek 到 {targetTimestamp} ticks");
    }

    const int maxReadAttempts = 256;
    const long backoff = 2_000_000;

    for (var i = 0; i < maxReadAttempts; i++)
    {
        var sample = sourceReader.ReadSample(
            SourceReaderIndex.FirstVideoStream,
            SourceReaderControlFlag.None,
            out var actualIndex,
            out var streamFlags,
            out var sampleTimestamp);

        Console.WriteLine($"  ReadSample[{i}]: flags={streamFlags}, timestamp={sampleTimestamp}, sample={sample != null}");

        using (sample)
        {
            if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
            {
                Console.WriteLine("  到达流末尾");
                return false;
            }

            if (sample == null)
            {
                Console.WriteLine("  sample 为 null，继续读取...");
                continue;
            }

            if (targetTimestamp > 0 && sampleTimestamp + backoff < targetTimestamp)
            {
                Console.WriteLine($"  跳过早期帧 (sampleTs={sampleTimestamp}, target={targetTimestamp})");
                continue;
            }

            // 写入 PNG
            return WriteSampleToPng(sample, width, height, filePath);
        }
    }

    Console.WriteLine("  超过最大读取次数");
    return false;
}

static bool WriteSampleToPng(IMFSample sample, int width, int height, string filePath)
{
    IMFMediaBuffer? mediaBuffer = null;
    IntPtr sourcePointer = IntPtr.Zero;

    try
    {
        mediaBuffer = sample.ConvertToContiguousBuffer();
        mediaBuffer.Lock(out sourcePointer, out var maxLen, out var currentLength);

        var rowBytes = width * 4;
        var requiredBytes = rowBytes * height;

        Console.WriteLine($"  Buffer: ptr={sourcePointer}, maxLen={maxLen}, currentLen={currentLength}, required={requiredBytes}");

        if (sourcePointer == IntPtr.Zero || currentLength < requiredBytes)
        {
            Console.WriteLine($"  [FAIL] Buffer 大小不足: {currentLength} < {requiredBytes}");
            return false;
        }

        // RGB32 from MF is bottom-up BGR, need to flip vertically
        var pixels = new byte[requiredBytes];
        Marshal.Copy(sourcePointer, pixels, 0, requiredBytes);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var destinationPointer = bitmap.GetPixels();
        if (destinationPointer == IntPtr.Zero)
        {
            Console.WriteLine("  [FAIL] SKBitmap.GetPixels() 返回空指针");
            return false;
        }

        // 测试：不翻转，直接拷贝（如果MF video processor已经输出top-down）
        Marshal.Copy(pixels, 0, destinationPointer, requiredBytes);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        if (data == null)
        {
            Console.WriteLine("  [FAIL] PNG 编码失败");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
        using var fileStream = File.Create(filePath);
        data.SaveTo(fileStream);
        Console.WriteLine($"  [OK] PNG 写入成功: {data.Size} bytes");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [ERROR] {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"  {ex.StackTrace}");
        return false;
    }
    finally
    {
        try { mediaBuffer?.Unlock(); } catch { }
        mediaBuffer?.Dispose();
    }
}

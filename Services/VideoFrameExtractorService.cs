using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if !MACOS_BUILD
using MediaFoundationApi = NAudio.MediaFoundation.MediaFoundationApi;
using SharpGen.Runtime.Win32;
using SkiaSharp;
using Vortice.MediaFoundation;
#endif

namespace TrueFluentPro.Services
{
    public sealed class VideoFrameExtractResult
    {
        public string? FirstFramePath { get; init; }
        public string? LastFramePath { get; init; }
    }

    /// <summary>
    /// 使用 Windows 系统自带媒体能力抽取视频首帧/尾帧。
    /// 不依赖 ffmpeg，额外体积最小。
    /// </summary>
    public static class VideoFrameExtractorService
    {
        private const string FirstFrameSuffix = ".first.png";
        private const string LastFrameSuffix = ".last.png";
        private const long LastFrameBackoffTicks = 2_000_000; // 200ms，Media Foundation 使用 100ns 为单位

    #if !MACOS_BUILD
        private static int _mfStarted;
        private static readonly object StartupLock = new();
    #endif

        public static async Task<VideoFrameExtractResult> TryExtractFirstAndLastFrameAsync(
            string videoFilePath,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoFrameExtractResult();

            if (!OperatingSystem.IsWindows())
            {
                return result;
            }

#if !MACOS_BUILD
            if (string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
            {
                return result;
            }

            try
            {
                return await Task.Run(() =>
                {
                    EnsureMediaFoundationStarted();
                    cancellationToken.ThrowIfCancellationRequested();

                    var basePath = Path.Combine(
                        Path.GetDirectoryName(videoFilePath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(videoFilePath));

                    var firstPath = basePath + FirstFrameSuffix;
                    var lastPath = basePath + LastFrameSuffix;

                    if (!TryGetVideoMetadata(videoFilePath, out var width, out var height, out var durationTicks))
                    {
                        return result;
                    }

                    var lastTimestamp = durationTicks > LastFrameBackoffTicks
                        ? durationTicks - LastFrameBackoffTicks
                        : 0L;

                    cancellationToken.ThrowIfCancellationRequested();
                    var hasFirst = TrySaveFrameAsPng(videoFilePath, 0L, width, height, firstPath, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    var hasLast = TrySaveFrameAsPng(videoFilePath, lastTimestamp, width, height, lastPath, cancellationToken);

                    return new VideoFrameExtractResult
                    {
                        FirstFramePath = hasFirst && File.Exists(firstPath) ? firstPath : null,
                        LastFramePath = hasLast && File.Exists(lastPath) ? lastPath : null
                    };
                }, cancellationToken);
            }
            catch
            {
                return result;
            }
#else
            await Task.CompletedTask;
            return result;
#endif
        }

        public static bool TryResolveVideoPathFromFirstFrame(string? imagePath, out string videoPath)
        {
            videoPath = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            if (!imagePath.EndsWith(FirstFrameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var prefix = imagePath[..^FirstFrameSuffix.Length];
            var candidate = prefix + ".mp4";
            if (!File.Exists(candidate))
            {
                return false;
            }

            videoPath = candidate;
            return true;
        }

        public static bool IsFirstFrameImagePath(string? imagePath)
        {
            return !string.IsNullOrWhiteSpace(imagePath)
                && imagePath.EndsWith(FirstFrameSuffix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLastFrameImagePath(string? imagePath)
        {
            return !string.IsNullOrWhiteSpace(imagePath)
                && imagePath.EndsWith(LastFrameSuffix, StringComparison.OrdinalIgnoreCase);
        }

#if !MACOS_BUILD
        private static void EnsureMediaFoundationStarted()
        {
            if (Volatile.Read(ref _mfStarted) == 1)
            {
                return;
            }

            lock (StartupLock)
            {
                if (_mfStarted == 1)
                {
                    return;
                }

                MediaFoundationApi.Startup();
                _mfStarted = 1;
            }
        }

        private static bool TryGetVideoMetadata(string videoFilePath, out int width, out int height, out long durationTicks)
        {
            width = 0;
            height = 0;
            durationTicks = 0;

            try
            {
                using var sourceReader = CreateConfiguredSourceReader(videoFilePath);
                using var currentMediaType = sourceReader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);

                var frameSize = currentMediaType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                width = (int)(frameSize >> 32);
                height = (int)(frameSize & 0xFFFFFFFF);
                var durationVariant = sourceReader.GetPresentationAttribute(
                    SourceReaderIndex.MediaSource,
                    PresentationDescriptionAttributeKeys.Duration);
                durationTicks = durationVariant is Variant v
                    ? Convert.ToInt64(v.Value)
                    : Convert.ToInt64(durationVariant);

                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static IMFSourceReader CreateConfiguredSourceReader(string videoFilePath)
        {
            var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);

            try
            {
                var sourceReader = MediaFactory.MFCreateSourceReaderFromURL(videoFilePath, attributes);
                using var requestedMediaType = MediaFactory.MFCreateMediaType();
                requestedMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                requestedMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                sourceReader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, requestedMediaType);
                sourceReader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                return sourceReader;
            }
            finally
            {
                attributes.Dispose();
            }
        }

        private static bool TrySaveFrameAsPng(
            string videoFilePath,
            long targetTimestamp,
            int width,
            int height,
            string filePath,
            CancellationToken cancellationToken)
        {
            try
            {
                using var sourceReader = CreateConfiguredSourceReader(videoFilePath);
                if (targetTimestamp > 0)
                {
                    sourceReader.SetCurrentPosition(targetTimestamp);
                }

                const int maxReadAttempts = 256;
                for (var i = 0; i < maxReadAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var sample = sourceReader.ReadSample(
                        SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None,
                        out _,
                        out var streamFlags,
                        out var sampleTimestamp);

                    using (sample)
                    {
                        if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                        {
                            return false;
                        }

                        if (sample == null)
                        {
                            continue;
                        }

                        if (targetTimestamp > 0 && sampleTimestamp + LastFrameBackoffTicks < targetTimestamp)
                        {
                            continue;
                        }

                        return TryWriteSampleToPng(sample, width, height, filePath);
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryWriteSampleToPng(IMFSample sample, int width, int height, string filePath)
        {
            IMFMediaBuffer? mediaBuffer = null;
            IntPtr sourcePointer = IntPtr.Zero;

            try
            {
                mediaBuffer = sample.ConvertToContiguousBuffer();
                mediaBuffer.Lock(out sourcePointer, out _, out var currentLength);

                var rowBytes = width * 4;
                var requiredBytes = rowBytes * height;
                if (sourcePointer == IntPtr.Zero || currentLength < requiredBytes)
                {
                    return false;
                }

                var pixels = new byte[requiredBytes];
                Marshal.Copy(sourcePointer, pixels, 0, requiredBytes);

                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                var destinationPointer = bitmap.GetPixels();
                if (destinationPointer == IntPtr.Zero)
                {
                    return false;
                }

                Marshal.Copy(pixels, 0, destinationPointer, requiredBytes);

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 95);
                if (data == null)
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                using var fileStream = File.Create(filePath);
                data.SaveTo(fileStream);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    mediaBuffer?.Unlock();
                }
                catch
                {
                    // ignore
                }

                mediaBuffer?.Dispose();
            }
        }
#endif
    }
}

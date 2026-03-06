using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
#if !MACOS_BUILD
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
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
                cancellationToken.ThrowIfCancellationRequested();

                var basePath = Path.Combine(
                    Path.GetDirectoryName(videoFilePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(videoFilePath));

                var firstPath = basePath + FirstFrameSuffix;
                var lastPath = basePath + LastFrameSuffix;

                var file = await StorageFile.GetFileFromPathAsync(videoFilePath);
                var clip = await MediaClip.CreateFromFileAsync(file);
                var composition = new MediaComposition();
                composition.Clips.Add(clip);

                var duration = clip.OriginalDuration;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                var firstTimestamp = TimeSpan.Zero;
                var lastTimestamp = duration > TimeSpan.FromMilliseconds(200)
                    ? duration - TimeSpan.FromMilliseconds(200)
                    : TimeSpan.Zero;

                cancellationToken.ThrowIfCancellationRequested();
                var hasFirst = await SaveThumbnailAsync(composition, firstTimestamp, firstPath);

                cancellationToken.ThrowIfCancellationRequested();
                var hasLast = await SaveThumbnailAsync(composition, lastTimestamp, lastPath);

                return new VideoFrameExtractResult
                {
                    FirstFramePath = hasFirst && File.Exists(firstPath) ? firstPath : null,
                    LastFramePath = hasLast && File.Exists(lastPath) ? lastPath : null
                };
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
        private static async Task<bool> SaveThumbnailAsync(MediaComposition composition, TimeSpan position, string filePath)
        {
            IRandomAccessStream? stream = null;
            DataReader? reader = null;
            try
            {
                stream = await composition.GetThumbnailAsync(position, 0, 0, VideoFramePrecision.NearestFrame);
                if (stream == null || stream.Size == 0)
                {
                    return false;
                }

                stream.Seek(0);
                reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);

                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);

                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                await File.WriteAllBytesAsync(filePath, bytes);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                reader?.Dispose();
                stream?.Dispose();
            }
        }
#endif
    }
}

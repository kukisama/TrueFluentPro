using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Lame;
using NAudio.MediaFoundation;
using NAudio.Wave;
#if !MACOS_BUILD
using Vortice.MediaFoundation;
#endif

namespace TrueFluentPro.Services
{
    public sealed class AudioMp3ConversionPlan
    {
        public string SourcePath { get; init; } = "";
        public string TargetMp3Path { get; init; } = "";
        public string SourceFormatLabel { get; init; } = "";
        public int SourceBitrateKbps { get; init; }
        public int TargetBitrateKbps { get; init; }
    }

    public static class AudioFormatConverter
    {
        private static int _mfStarted;
        private static readonly object StartupLock = new();

        /// <summary>
        /// 微软 Speech Fast / Batch Transcription API 官方直接支持的音频容器/编码扩展名。
        /// 参考：https://learn.microsoft.com/en-us/azure/ai-services/speech-service/batch-transcription-audio-data#supported-input-formats-and-codecs
        /// 注意：.m4a / .mp4 等 MPEG-4 容器不在直接支持列表中（服务端返回 InvalidAudioFormat）。
        /// </summary>
        private static readonly HashSet<string> SupportedSpeechTranscriptionExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav",
            ".mp3",
            ".ogg",
            ".opus",
            ".flac",
            ".wma",
            ".aac",
            ".amr",
            ".webm",
            ".spx"
        };

        private static readonly int[] StandardMp3BitratesKbps =
        {
            8, 16, 18, 20, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320
        };

        public static bool IsMicrosoftSpeechSupportedFormat(string audioPath, out string formatLabel)
        {
            var ext = Path.GetExtension(audioPath)?.Trim() ?? "";
            formatLabel = string.IsNullOrWhiteSpace(ext) ? "未知格式" : ext.ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(ext) || !SupportedSpeechTranscriptionExtensions.Contains(ext))
            {
                return false;
            }

            if (IsIsoBmffMp4Container(audioPath))
            {
                formatLabel = $"{ext.ToLowerInvariant()} (MP4/M4A 容器)";
                return false;
            }

            return true;
        }

        public static AudioMp3ConversionPlan BuildMp3ConversionPlan(string sourcePath)
        {
            IsMicrosoftSpeechSupportedFormat(sourcePath, out var formatLabel);
            var targetMp3Path = BuildConvertedMp3Path(sourcePath);
            var sourceBitrateBps = ProbeSourceAudioBitrateBps(sourcePath, out var sampleRate, out var channels);
            var sourceKbps = Math.Max(8, (int)Math.Round(sourceBitrateBps / 1000.0));
            var targetKbps = SelectCappedMp3BitrateKbps(sourceBitrateBps, sampleRate, channels);

            return new AudioMp3ConversionPlan
            {
                SourcePath = sourcePath,
                TargetMp3Path = targetMp3Path,
                SourceFormatLabel = formatLabel,
                SourceBitrateKbps = sourceKbps,
                TargetBitrateKbps = targetKbps
            };
        }

        public static string BuildConvertedMp3Path(string sourcePath)
        {
            var dir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                dir = PathManager.Instance.SessionsPath;
            }

            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "audio";
            }

            if (!stem.EndsWith("-convert", StringComparison.OrdinalIgnoreCase))
            {
                stem += "-convert";
            }

            return Path.Combine(dir, $"{stem}.mp3");
        }

        public static Task<AudioMp3ConversionPlan> ConvertToMp3CappedBitrateAsync(
            string sourcePath,
            AudioMp3ConversionPlan? plan = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("未找到需要转换的音频文件。", sourcePath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureMediaFoundationStarted();

                var resolvedPlan = plan ?? BuildMp3ConversionPlan(sourcePath);
                var outputPath = resolvedPlan.TargetMp3Path;
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                var tempOutputPath = Path.Combine(
                    outputDir ?? Path.GetTempPath(),
                    $".{Path.GetFileNameWithoutExtension(outputPath)}_{Guid.NewGuid():N}.tmp.mp3");

                try
                {
                    var maxBitrateBps = resolvedPlan.SourceBitrateKbps * 1000;
                    using var reader = new MediaFoundationReader(sourcePath);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (OperatingSystem.IsWindows() && TryEncodeWithMediaFoundation(reader, tempOutputPath, maxBitrateBps, out var actualKbps))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(tempOutputPath, outputPath, overwrite: true);
                        return new AudioMp3ConversionPlan
                        {
                            SourcePath = resolvedPlan.SourcePath,
                            TargetMp3Path = outputPath,
                            SourceFormatLabel = resolvedPlan.SourceFormatLabel,
                            SourceBitrateKbps = resolvedPlan.SourceBitrateKbps,
                            TargetBitrateKbps = actualKbps
                        };
                    }

                    var targetKbps = Math.Min(resolvedPlan.TargetBitrateKbps, resolvedPlan.SourceBitrateKbps);
                    targetKbps = Math.Clamp(targetKbps, 32, 320);
                    EncodeWithLame(reader, tempOutputPath, targetKbps, cancellationToken);
                    File.Move(tempOutputPath, outputPath, overwrite: true);

                    return new AudioMp3ConversionPlan
                    {
                        SourcePath = resolvedPlan.SourcePath,
                        TargetMp3Path = outputPath,
                        SourceFormatLabel = resolvedPlan.SourceFormatLabel,
                        SourceBitrateKbps = resolvedPlan.SourceBitrateKbps,
                        TargetBitrateKbps = targetKbps
                    };
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempOutputPath))
                        {
                            File.Delete(tempOutputPath);
                        }
                    }
                    catch
                    {
                        // ignore cleanup failure
                    }
                    throw;
                }
            }, cancellationToken);
        }

        private static bool TryEncodeWithMediaFoundation(
            WaveStream reader,
            string outputPath,
            int maxBitrateBps,
            out int actualKbps)
        {
            actualKbps = 0;
            try
            {
                var allTypes = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_MP3);
                if (allTypes == null || allTypes.Length == 0)
                {
                    return false;
                }

                var inputSampleRate = reader.WaveFormat.SampleRate;
                var inputChannels = Math.Clamp(reader.WaveFormat.Channels, 1, 2);

                // 1. 优先选择同采样率、同声道数且码率不超过原始码率的最高档位
                var selectedType = allTypes
                    .Where(t => t.SampleRate == inputSampleRate
                                && t.ChannelCount == inputChannels
                                && (long)t.AverageBytesPerSecond * 8 <= maxBitrateBps)
                    .OrderByDescending(t => t.AverageBytesPerSecond)
                    .FirstOrDefault();

                // 2. 若立体声最低档位仍高于原始码率，允许降为单声道但保持不超原始码率
                selectedType ??= allTypes
                    .Where(t => t.SampleRate == inputSampleRate
                                && t.ChannelCount == 1
                                && (long)t.AverageBytesPerSecond * 8 <= maxBitrateBps)
                    .OrderByDescending(t => t.AverageBytesPerSecond)
                    .FirstOrDefault();

                // 3. 若输入采样率不在 MP3 标准采样率中，选择最接近采样率且不超原始码率的最高档位
                selectedType ??= allTypes
                    .Where(t => t.ChannelCount <= inputChannels
                                && (long)t.AverageBytesPerSecond * 8 <= maxBitrateBps)
                    .OrderByDescending(t => t.AverageBytesPerSecond)
                    .ThenBy(t => Math.Abs(t.SampleRate - inputSampleRate))
                    .FirstOrDefault();

                // 4. 极端低码率兜底：选择最低可用码率档位
                selectedType ??= allTypes
                    .OrderBy(t => t.AverageBytesPerSecond)
                    .FirstOrDefault();

                if (selectedType == null)
                {
                    return false;
                }

                actualKbps = Math.Max(8, (int)Math.Round(selectedType.AverageBytesPerSecond * 8 / 1000.0));
                using var encoder = new MediaFoundationEncoder(selectedType);

                if (reader.WaveFormat.SampleRate == selectedType.SampleRate
                    && reader.WaveFormat.Channels == selectedType.ChannelCount
                    && reader.WaveFormat.BitsPerSample == 16)
                {
                    encoder.Encode(outputPath, reader);
                }
                else
                {
                    var targetFormat = new WaveFormat(selectedType.SampleRate, 16, selectedType.ChannelCount);
                    using var resampler = new MediaFoundationResampler(reader, targetFormat)
                    {
                        ResamplerQuality = 60
                    };
                    encoder.Encode(outputPath, resampler);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EncodeWithLame(
            WaveStream reader,
            string outputPath,
            int targetKbps,
            CancellationToken cancellationToken)
        {
            IWaveProvider source = reader;
            MediaFoundationResampler? resampler = null;

            try
            {
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels > 2)
                {
                    var targetChannels = Math.Clamp(reader.WaveFormat.Channels, 1, 2);
                    var targetSampleRate = reader.WaveFormat.SampleRate is 48000 or 44100 or 32000 or 24000 or 22050 or 16000
                        ? reader.WaveFormat.SampleRate
                        : 44100;
                    resampler = new MediaFoundationResampler(reader, new WaveFormat(targetSampleRate, 16, targetChannels))
                    {
                        ResamplerQuality = 60
                    };
                    source = resampler;
                }

                using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                using var writer = new LameMP3FileWriter(outStream, source.WaveFormat, targetKbps);
                var buffer = new byte[source.WaveFormat.AverageBytesPerSecond];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(buffer, 0, read);
                }
            }
            finally
            {
                resampler?.Dispose();
            }
        }

        private static int ProbeSourceAudioBitrateBps(string sourcePath, out int sampleRate, out int channels)
        {
            sampleRate = 48000;
            channels = 1;
            int nativeBitrateBps = 0;
            int containerBitrateBps = 0;

            EnsureMediaFoundationStarted();

#if !MACOS_BUILD
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    MediaFactory.MFStartup();
                    using var sourceReader = MediaFactory.MFCreateSourceReaderFromURL(sourcePath, null);
                    using var nativeType = sourceReader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                    try
                    {
                        var avgBytes = nativeType.GetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond);
                        if (avgBytes > 0)
                        {
                            nativeBitrateBps = (int)Math.Min((long)avgBytes * 8, 320_000);
                        }
                    }
                    catch
                    {
                        // 部分容器未写入 AudioAvgBytesPerSecond，回退到容器时长估算
                    }

                    try
                    {
                        var sr = nativeType.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                        if (sr > 0) sampleRate = (int)sr;
                    }
                    catch { }

                    try
                    {
                        var ch = nativeType.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                        if (ch > 0) channels = (int)ch;
                    }
                    catch { }
                }
                catch
                {
                    // ignore and fallback to MediaFoundationReader
                }
            }
#endif

            try
            {
                using var reader = new MediaFoundationReader(sourcePath);
                if (reader.WaveFormat != null)
                {
                    sampleRate = reader.WaveFormat.SampleRate;
                    channels = reader.WaveFormat.Channels;
                }

                var totalSeconds = reader.TotalTime.TotalSeconds;
                var fileInfo = new FileInfo(sourcePath);
                if (totalSeconds > 0.5 && fileInfo.Exists && fileInfo.Length > 0)
                {
                    var estimatedBps = (long)Math.Round(fileInfo.Length * 8.0 / totalSeconds);
                    if (estimatedBps > 0)
                    {
                        containerBitrateBps = (int)Math.Clamp(estimatedBps, 8_000, 320_000);
                    }
                }
            }
            catch
            {
                // ignore
            }

            if (nativeBitrateBps > 0 && containerBitrateBps > 0)
            {
                return Math.Min(nativeBitrateBps, containerBitrateBps);
            }

            if (nativeBitrateBps > 0)
            {
                return nativeBitrateBps;
            }

            if (containerBitrateBps > 0)
            {
                return containerBitrateBps;
            }

            return 128_000;
        }

        private static int SelectCappedMp3BitrateKbps(int sourceBitrateBps, int sampleRate, int channels)
        {
            var maxBps = Math.Max(8_000, sourceBitrateBps);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    EnsureMediaFoundationStarted();
                    var allTypes = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_MP3);
                    if (allTypes != null && allTypes.Length > 0)
                    {
                        var clampedChannels = Math.Clamp(channels, 1, 2);
                        var match = allTypes
                            .Where(t => t.SampleRate == sampleRate
                                        && t.ChannelCount == clampedChannels
                                        && (long)t.AverageBytesPerSecond * 8 <= maxBps)
                            .OrderByDescending(t => t.AverageBytesPerSecond)
                            .FirstOrDefault()
                            ?? allTypes
                                .Where(t => t.ChannelCount <= clampedChannels
                                            && (long)t.AverageBytesPerSecond * 8 <= maxBps)
                                .OrderByDescending(t => t.AverageBytesPerSecond)
                                .FirstOrDefault();

                        if (match != null)
                        {
                            return Math.Max(8, (int)Math.Round(match.AverageBytesPerSecond * 8 / 1000.0));
                        }
                    }
                }
                catch
                {
                    // ignore and use standard table
                }
            }

            var sourceKbps = Math.Max(8, sourceBitrateBps / 1000);
            var capped = StandardMp3BitratesKbps.Where(k => k <= sourceKbps).DefaultIfEmpty(StandardMp3BitratesKbps[0]).Max();
            return Math.Min(capped, 320);
        }

        private static bool IsIsoBmffMp4Container(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                Span<byte> header = stackalloc byte[12];
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Read(header) < 8) return false;
                return header[4] == (byte)'f'
                       && header[5] == (byte)'t'
                       && header[6] == (byte)'y'
                       && header[7] == (byte)'p';
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureMediaFoundationStarted()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

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

        public static string PrepareBatchUploadAudioPath(
            string audioPath,
            Action<string>? onStatus,
            CancellationToken token,
            out string? tempUploadPath,
            out bool converted)
        {
            tempUploadPath = null;
            converted = false;

            if (IsPcm16kMonoWav(audioPath))
            {
                return audioPath;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "TrueFluentPro", "BatchAudio");
            Directory.CreateDirectory(tempDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var unique = Guid.NewGuid().ToString("N");
            tempUploadPath = Path.Combine(
                tempDir,
                $"{Path.GetFileNameWithoutExtension(audioPath)}_{stamp}_{unique}_pcm16k_mono.wav");

            onStatus?.Invoke("批量转写：转换 WAV(16kHz/16-bit/mono)...");

            try
            {
                ConvertToPcm16kMonoWav(audioPath, tempUploadPath, token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"音频转换失败: {ex.Message}", ex);
            }

            converted = true;

            return tempUploadPath;
        }

        public static bool IsPcm16kMonoWav(string audioPath)
        {
            if (!string.Equals(Path.GetExtension(audioPath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using var reader = new WaveFileReader(audioPath);
                var format = reader.WaveFormat;
                return format.Encoding == WaveFormatEncoding.Pcm
                       && format.SampleRate == 16000
                       && format.BitsPerSample == 16
                       && format.Channels == 1;
            }
            catch
            {
                return false;
            }
        }

        public static void ConvertToPcm16kMonoWav(string sourcePath, string outputPath, CancellationToken token)
        {
            using var reader = new AudioFileReader(sourcePath);
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
            {
                ResamplerQuality = 60
            };
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using var writer = new WaveFileWriter(outputPath, resampler.WaveFormat);
            var buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
            }
        }
    }
}

using System;
using System.IO;
using NAudio.Lame;
using NAudio.Wave;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>把处理后的单声道 PCM16 直接写成 MP3，避免再独立采集一遍设备。</summary>
    /// <remarks>
    /// 自行持有 FileStream 并定期刷盘，以防进程崩溃丢失数据。
    /// 注意：LameMP3FileWriter.Flush() 是终结操作（会置空内部 _outStream），
    /// 因此录制过程中只能刷底层 FileStream，不能调 LAME 的 Flush。
    /// </remarks>
    public sealed class ProcessedAudioMp3RecordingSink : IAudioRecordingSink
    {
        private readonly object _writeLock = new();
        private readonly FileStream _fileStream;
        private readonly LameMP3FileWriter _writer;
        private readonly AutoGainProcessor? _autoGainProcessor;
        private long _unflushedBytes;
        private const long FlushThresholdBytes = 64_000; // ≈2秒 @16kHz/16bit/mono

        public ProcessedAudioMp3RecordingSink(
            string mp3Path,
            int sampleRate,
            int bitrateKbps,
            bool autoGainEnabled,
            double targetRms,
            double minGain,
            double maxGain,
            double smoothing)
        {
            _fileStream = new FileStream(mp3Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _writer = new LameMP3FileWriter(_fileStream, new WaveFormat(sampleRate, 16, 1), Math.Clamp(bitrateKbps, 32, 320));
            _autoGainProcessor = autoGainEnabled
                ? new AutoGainProcessor(targetRms, minGain, maxGain, smoothing)
                : null;
        }

        public void WriteChunk(byte[] pcm16Chunk)
        {
            if (pcm16Chunk.Length == 0)
            {
                return;
            }

            lock (_writeLock)
            {
                var chunk = new byte[pcm16Chunk.Length];
                Buffer.BlockCopy(pcm16Chunk, 0, chunk, 0, pcm16Chunk.Length);
                _autoGainProcessor?.ProcessInPlace(chunk, chunk.Length);
                _writer.Write(chunk, 0, chunk.Length);
                _unflushedBytes += chunk.Length;
                if (_unflushedBytes >= FlushThresholdBytes)
                {
                    _fileStream.Flush();
                    _unflushedBytes = 0;
                }
            }
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                _writer.Dispose();
                _fileStream.Dispose();
            }
        }
    }

}


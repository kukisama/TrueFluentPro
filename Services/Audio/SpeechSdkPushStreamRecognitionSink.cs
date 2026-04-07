using System;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TrueFluentPro.Services.Audio
{
    public sealed class SpeechSdkPushStreamRecognitionSink : IAudioRecognitionSink
    {
        private readonly PushAudioInputStream _pushStream;
        private readonly MasAudioPipeline? _masPipeline;
        private bool _disposed;

        public AudioConfig AudioConfig { get; }

        public SpeechSdkPushStreamRecognitionSink(
            int sampleRate,
            bool enableMasEnhancement,
            bool masNoiseSuppressionEnabled,
            Action<string>? log = null)
        {
            if (enableMasEnhancement)
            {
                _masPipeline = MasAudioPipeline.CreateRecognitionEnhancement(sampleRate, masNoiseSuppressionEnabled, log);
                _pushStream = _masPipeline.PushStream;
                AudioConfig = _masPipeline.AudioConfig;
            }
            else
            {
                var streamFormat = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 1);
                _pushStream = AudioInputStream.CreatePushStream(streamFormat);
                AudioConfig = AudioConfig.FromStreamInput(_pushStream);
            }
        }

        public void WriteChunk(byte[] pcm16Chunk)
        {
            if (_disposed || pcm16Chunk.Length == 0)
            {
                return;
            }

            _pushStream.Write(pcm16Chunk);
        }

        public void Close()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _pushStream.Close();
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_masPipeline != null)
            {
                _masPipeline.Dispose();
                return;
            }

            try
            {
                _pushStream.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                AudioConfig.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

}


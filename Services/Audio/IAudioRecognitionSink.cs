using System;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TrueFluentPro.Services.Audio
{
    public interface IAudioRecognitionSink : IDisposable
    {
        AudioConfig AudioConfig { get; }
        void WriteChunk(byte[] pcm16Chunk);
        void Close();
    }
}

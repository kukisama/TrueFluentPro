using System;

namespace TrueFluentPro.Services.Audio
{
    public interface IAudioRecordingSink : IDisposable
    {
        void WriteChunk(byte[] pcm16Chunk);
    }
}

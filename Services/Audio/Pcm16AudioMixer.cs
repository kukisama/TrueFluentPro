using System;

namespace TrueFluentPro.Services.Audio
{
    public static class Pcm16AudioMixer
    {
        public static byte[] MixMono(byte[] first, byte[] second)
        {
            var length = Math.Max(first?.Length ?? 0, second?.Length ?? 0);
            if (length <= 0)
            {
                return Array.Empty<byte>();
            }

            if ((length & 1) != 0)
            {
                length--;
            }

            var result = new byte[length];
            for (var i = 0; i + 1 < length; i += 2)
            {
                var a = ReadSample(first, i);
                var b = ReadSample(second, i);
                var mixed = a + b;
                if (mixed > short.MaxValue) mixed = short.MaxValue;
                else if (mixed < short.MinValue) mixed = short.MinValue;
                result[i] = (byte)(mixed & 0xFF);
                result[i + 1] = (byte)((mixed >> 8) & 0xFF);
            }

            return result;
        }

        public static byte[] CloneOrSilence(byte[]? source, int fallbackLength)
        {
            if (source == null || source.Length == 0)
            {
                return fallbackLength > 0 ? new byte[fallbackLength] : Array.Empty<byte>();
            }

            var clone = new byte[source.Length];
            Buffer.BlockCopy(source, 0, clone, 0, source.Length);
            return clone;
        }

        public static void ApplyGainInPlace(byte[] pcm16, float gain)
        {
            if (pcm16.Length < 2 || Math.Abs(gain - 1f) < 0.0001f)
            {
                return;
            }

            if (gain <= 0.0001f)
            {
                Array.Clear(pcm16, 0, pcm16.Length);
                return;
            }

            for (var i = 0; i + 1 < pcm16.Length; i += 2)
            {
                var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                var scaled = (int)Math.Round(sample * gain);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                pcm16[i] = (byte)(scaled & 0xFF);
                pcm16[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        private static int ReadSample(byte[]? buffer, int offset)
        {
            if (buffer == null || offset + 1 >= buffer.Length)
            {
                return 0;
            }

            return (short)(buffer[offset] | (buffer[offset + 1] << 8));
        }
    }
}

using System;
using System.Security.Cryptography;

namespace TrueFluentPro.Helpers
{
    /// <summary>
    /// 轻量级 ULID（Universally Unique Lexicographically Sortable Identifier）生成器。
    /// 格式：26 字符 Crockford Base32 编码 = 10 字符时间戳 + 16 字符随机数。
    /// </summary>
    public static class UlidGenerator
    {
        private static readonly char[] CrockfordBase32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

        /// <summary>生成一个新的 ULID 字符串。</summary>
        public static string NewUlid()
        {
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();

            Span<char> result = stackalloc char[26];

            // 编码 48-bit 时间戳（前 10 字符，高位在前）
            for (int i = 9; i >= 0; i--)
            {
                result[i] = CrockfordBase32[timestamp & 0x1F];
                timestamp >>= 5;
            }

            // 编码 80-bit 随机数（后 16 字符）
            Span<byte> randomBytes = stackalloc byte[10];
            RandomNumberGenerator.Fill(randomBytes);

            // 将 10 字节 = 80 bits 编码为 16 个 Base32 字符
            int bitBuffer = 0;
            int bitsInBuffer = 0;
            int charIndex = 10;

            for (int i = 0; i < randomBytes.Length; i++)
            {
                bitBuffer = (bitBuffer << 8) | randomBytes[i];
                bitsInBuffer += 8;

                while (bitsInBuffer >= 5)
                {
                    bitsInBuffer -= 5;
                    result[charIndex++] = CrockfordBase32[(bitBuffer >> bitsInBuffer) & 0x1F];
                }
            }

            return new string(result);
        }
    }
}

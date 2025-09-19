using System.Security.Cryptography;
using System.Text;

namespace CodeCortex.Core.Util;

internal static class HashUtil {
    // 32-char alphabet (RFC4648) - keeping standard set for correctness; visual disambiguation can be applied at presentation layer.
    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public static string Sha256Base32Trunc(string input, int length = 8) {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder();
        // 5 bits per char; consume until we have enough
        int bitBuffer = 0;
        int bitCount = 0;
        int idx = 0;
        int outLen = 0;
        while (outLen < length && idx < bytes.Length) {
            bitBuffer = (bitBuffer << 8) | bytes[idx++];
            bitCount += 8;
            while (bitCount >= 5 && outLen < length) {
                bitCount -= 5;
                int val = (bitBuffer >> bitCount) & 0x1F;
                sb.Append(Base32Alphabet[val]);
                outLen++;
            }
        }
        return sb.ToString();
    }
}

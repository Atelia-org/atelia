using System.Globalization;
using System.Text;

namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodRecallQueryRenderer {
    private static ReadOnlySpan<byte> Prefix
        => "{\"schema\":\"atelia.memo-pod.recall-query.v1\",\"query\":\""u8;
    private static ReadOnlySpan<byte> Middle => "\",\"maxResults\":"u8;
    private static ReadOnlySpan<byte> Suffix => "}\n"u8;

    internal static string Render(string query, int maxResults) {
        int encodedQueryLength =
            MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                query,
                nameof(query)
            );
        string decimalMaxResults = maxResults.ToString(
            CultureInfo.InvariantCulture
        );
        int finalLength = checked(
            Prefix.Length
            + encodedQueryLength
            + Middle.Length
            + decimalMaxResults.Length
            + Suffix.Length
        );
        byte[] bytes = GC.AllocateUninitializedArray<byte>(finalLength);
        int written = 0;
        Write(Prefix);
        written += MemoPodPromptJsonStringEncoder.WriteEncodedUtf8(
            query,
            bytes.AsSpan(written, encodedQueryLength),
            nameof(query)
        );
        Write(Middle);
        foreach (char character in decimalMaxResults) {
            bytes[written++] = checked((byte)character);
        }
        Write(Suffix);
        if (written != bytes.Length) {
            throw new InvalidOperationException(
                "MemoPod recall query byte pre-count did not match rendering."
            );
        }
        return Encoding.UTF8.GetString(bytes);

        void Write(ReadOnlySpan<byte> value) {
            value.CopyTo(bytes.AsSpan(written));
            written += value.Length;
        }
    }
}

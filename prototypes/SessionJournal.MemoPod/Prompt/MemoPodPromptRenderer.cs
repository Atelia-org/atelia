namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodPromptRenderer {
    internal const string Schema = "atelia.memo-pod.prompt.v1";

    private static ReadOnlySpan<byte> HeaderPrefix
        => "{\"schema\":\"atelia.memo-pod.prompt.v1\",\"pod_id\":\""u8;
    private static ReadOnlySpan<byte> HeaderMiddle
        => "\",\"topic\":\""u8;
    private static ReadOnlySpan<byte> MemoPrefix => "{\"id\":\""u8;
    private static ReadOnlySpan<byte> MemoMiddle
        => "\",\"exact_text\":\""u8;
    private static ReadOnlySpan<byte> LineSuffix => "\"}\n"u8;

    internal static MemoPodFrozenPrompt Render(MemoPodDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        int topicLength =
            MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                document.Topic,
                nameof(document)
            );
        int[] memoTextLengths = GC.AllocateUninitializedArray<int>(
            document.Memos.Length
        );
        long finalLength = checked(
            (long)HeaderPrefix.Length
            + document.PodId.Value.Length
            + HeaderMiddle.Length
            + topicLength
            + LineSuffix.Length
        );
        for (int index = 0; index < document.Memos.Length; index++) {
            Memo memo = document.Memos[index];
            int memoTextLength =
                MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                    memo.ExactText,
                    nameof(document)
                );
            memoTextLengths[index] = memoTextLength;
            finalLength = checked(
                finalLength
                + MemoPrefix.Length
                + memo.Id.Value.Length
                + MemoMiddle.Length
                + memoTextLength
                + LineSuffix.Length
            );
        }
        if (finalLength > MemoPodLimits.MaximumRenderedPromptUtf8Bytes) {
            throw new InvalidOperationException(
                $"MemoPod rendered prompt exceeds {MemoPodLimits.MaximumRenderedPromptUtf8Bytes} UTF-8 bytes."
            );
        }

        byte[] finalBytes = GC.AllocateUninitializedArray<byte>(
            checked((int)finalLength)
        );
        int written = 0;
        WriteLiteral(HeaderPrefix);
        WriteAscii(document.PodId.Value);
        WriteLiteral(HeaderMiddle);
        WriteJsonString(document.Topic, topicLength);
        WriteLiteral(LineSuffix);
        for (int index = 0; index < document.Memos.Length; index++) {
            Memo memo = document.Memos[index];
            WriteLiteral(MemoPrefix);
            WriteAscii(memo.Id.Value);
            WriteLiteral(MemoMiddle);
            WriteJsonString(memo.ExactText, memoTextLengths[index]);
            WriteLiteral(LineSuffix);
        }
        if (written != finalBytes.Length) {
            throw new InvalidOperationException(
                "MemoPod prompt byte pre-count did not match the rendered bytes."
            );
        }
        return MemoPodFrozenPrompt.FromOwnedUtf8(finalBytes);

        void WriteLiteral(ReadOnlySpan<byte> literal) {
            literal.CopyTo(finalBytes.AsSpan(written));
            written += literal.Length;
        }

        void WriteAscii(string value) {
            Span<byte> destination = finalBytes.AsSpan(written, value.Length);
            for (int index = 0; index < value.Length; index++) {
                char character = value[index];
                if (character > 0x7F) {
                    throw new InvalidOperationException(
                        "MemoPod canonical identifiers must be ASCII."
                    );
                }
                destination[index] = (byte)character;
            }
            written += value.Length;
        }

        void WriteJsonString(string value, int encodedLength) {
            int actual = MemoPodPromptJsonStringEncoder.WriteEncodedUtf8(
                value,
                finalBytes.AsSpan(written, encodedLength),
                nameof(document)
            );
            if (actual != encodedLength) {
                throw new InvalidOperationException(
                    "MemoPod JSON string byte pre-count did not match its encoding."
                );
            }
            written += actual;
        }
    }
}

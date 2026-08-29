namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodPromptRenderer {
    internal const string Schema = "atelia.memo-pod.prompt.v2";

    private static ReadOnlySpan<byte> HeaderPrefix
        => "{\"schema\":\"atelia.memo-pod.prompt.v2\",\"pod_id\":\""u8;
    private static ReadOnlySpan<byte> HeaderMiddle
        => "\",\"topic\":\""u8;
    private static ReadOnlySpan<byte> MemoPrefix => "{\"id\":\""u8;
    private static ReadOnlySpan<byte> TitleName => "\",\"title\":"u8;
    private static ReadOnlySpan<byte> GistName => ",\"gist\":"u8;
    private static ReadOnlySpan<byte> SummaryName => ",\"summary\":"u8;
    private static ReadOnlySpan<byte> ExactTextName => ",\"exact_text\":\""u8;
    private static ReadOnlySpan<byte> NullLiteral => "null"u8;
    private static ReadOnlySpan<byte> Quote => "\""u8;
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
        int[] titleLengths = GC.AllocateUninitializedArray<int>(
            document.Memos.Length
        );
        int[] gistLengths = GC.AllocateUninitializedArray<int>(
            document.Memos.Length
        );
        int[] summaryLengths = GC.AllocateUninitializedArray<int>(
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
            int titleLength = GetOptionalJsonStringLength(memo.Title);
            int gistLength = GetOptionalJsonStringLength(memo.Gist);
            int summaryLength = GetOptionalJsonStringLength(memo.Summary);
            int memoTextLength =
                MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                    memo.ExactText,
                    nameof(document)
                );
            titleLengths[index] = titleLength;
            gistLengths[index] = gistLength;
            summaryLengths[index] = summaryLength;
            memoTextLengths[index] = memoTextLength;
            finalLength = checked(
                finalLength
                + MemoPrefix.Length
                + memo.Id.Value.Length
                + TitleName.Length
                + GetNullableJsonStringValueLength(memo.Title, titleLength)
                + GistName.Length
                + GetNullableJsonStringValueLength(memo.Gist, gistLength)
                + SummaryName.Length
                + GetNullableJsonStringValueLength(memo.Summary, summaryLength)
                + ExactTextName.Length
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
            WriteLiteral(TitleName);
            WriteNullableJsonStringValue(memo.Title, titleLengths[index]);
            WriteLiteral(GistName);
            WriteNullableJsonStringValue(memo.Gist, gistLengths[index]);
            WriteLiteral(SummaryName);
            WriteNullableJsonStringValue(memo.Summary, summaryLengths[index]);
            WriteLiteral(ExactTextName);
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

        void WriteNullableJsonStringValue(
            string? value,
            int encodedLength
        ) {
            if (value is null) {
                WriteLiteral(NullLiteral);
                return;
            }
            WriteLiteral(Quote);
            WriteJsonString(value, encodedLength);
            WriteLiteral(Quote);
        }
    }

    private static int GetOptionalJsonStringLength(string? value)
        => value is null
            ? 0
            : MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                value,
                nameof(value)
            );

    private static int GetNullableJsonStringValueLength(
        string? value,
        int encodedLength
    ) {
        return value is null
            ? NullLiteral.Length
            : checked(Quote.Length + encodedLength + Quote.Length);
    }
}

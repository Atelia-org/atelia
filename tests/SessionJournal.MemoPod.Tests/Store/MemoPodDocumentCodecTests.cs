using System.Text;
using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests.Store;

public sealed class MemoPodDocumentCodecTests {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "44444444444444444444444444444444"
    );

    [Fact]
    public void EncodeProducesExactCanonicalV2GoldenWithoutTrailingLf() {
        var document = new MemoPodDocument(
            PodId,
            "customer details",
            4,
            [
                new Memo(
                    MemoId.FromOrdinal(1),
                    "line\n\"quoted\"",
                    title: "Order 17",
                    gist: "Quoted line",
                    summary: "Line item stays quoted."
                ),
                new Memo(MemoId.FromOrdinal(3), "\0tail")
            ]
        );

        byte[] first = MemoPodDocumentCodec.Encode(document);
        byte[] second = MemoPodDocumentCodec.Encode(document);

        const string expected = "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"customer details\",\"nextMemoId\":4,\"memos\":[{\"id\":\"m1:00000001\",\"title\":\"Order 17\",\"gist\":\"Quoted line\",\"summary\":\"Line item stays quoted.\",\"exactText\":\"line\\n\\\"quoted\\\"\"},{\"id\":\"m1:00000003\",\"title\":null,\"gist\":null,\"summary\":null,\"exactText\":\"\\u0000tail\"}]}";
        Assert.Equal(expected, Encoding.UTF8.GetString(first));
        Assert.Equal(first, second);
        Assert.NotEqual((byte)'\n', first[^1]);

        MemoPodDocument decoded = MemoPodDocumentCodec.Decode(first);
        Assert.Equal(document.PodId, decoded.PodId);
        Assert.Equal(document.Topic, decoded.Topic);
        Assert.Equal(document.NextMemoOrdinal, decoded.NextMemoOrdinal);
        Assert.Equal(document.Memos.ToArray(), decoded.Memos.ToArray());
    }

    [Fact]
    public void EncodeRepresentsExhaustedNextMemoIdAsJsonInteger() {
        var document = new MemoPodDocument(
            PodId,
            "topic",
            MemoPodDocument.ExhaustedNextMemoOrdinal,
            [new Memo(MemoId.FromOrdinal(uint.MaxValue), "last")]
        );

        string json = Encoding.UTF8.GetString(
            MemoPodDocumentCodec.Encode(document)
        );

        Assert.Contains(
            "\"nextMemoId\":4294967296",
            json,
            StringComparison.Ordinal
        );
        Assert.Equal(
            MemoPodDocument.ExhaustedNextMemoOrdinal,
            MemoPodDocumentCodec.Decode(
                MemoPodDocumentCodec.Encode(document)
            ).NextMemoOrdinal
        );
    }

    [Theory]
    [MemberData(nameof(NonCanonicalOrMalformedDocuments))]
    public void DecodeRejectsNonCanonicalMalformedOrOutOfContractBytes(
        string json
    ) {
        Assert.Throws<MemoPodDocumentFormatException>(() =>
            MemoPodDocumentCodec.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DecodeRejectsBomAndDocumentHardCapBeforeParsing() {
        byte[] canonical = MemoPodDocumentCodec.Encode(EmptyDocument());
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. canonical];
        byte[] oversized = new byte[
            MemoPodLimits.MaximumDocumentUtf8Bytes + 1
        ];

        Assert.Throws<MemoPodDocumentFormatException>(() =>
            MemoPodDocumentCodec.Decode(withBom));
        Assert.Throws<MemoPodDocumentLimitException>(() =>
            MemoPodDocumentCodec.Decode(oversized));
    }

    [Fact]
    public void MaximumActiveRawTextStateFitsAndRoundTripsDocumentCap() {
        int exactTextBytesPerMemo =
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
            / MemoPodLimits.MaximumActiveMemoCount;
        string exactText = new(
            '\0',
            exactTextBytesPerMemo
        );
        Memo[] memos = Enumerable.Range(
                1,
                MemoPodLimits.MaximumActiveMemoCount
            )
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal((uint)ordinal),
                exactText
            ))
            .ToArray();
        var document = new MemoPodDocument(
            PodId,
            "topic",
            (ulong)MemoPodLimits.MaximumActiveMemoCount + 1,
            memos
        );

        byte[] encoded = MemoPodDocumentCodec.Encode(document);
        MemoPodDocument decoded = MemoPodDocumentCodec.Decode(encoded);

        Assert.True(encoded.Length < MemoPodLimits.MaximumDocumentUtf8Bytes);
        Assert.Equal(
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes,
            decoded.ActiveExactTextUtf8Bytes
        );
        Assert.Equal(
            MemoPodLimits.MaximumActiveMemoCount,
            decoded.Memos.Length
        );
        Assert.Equal(exactText, decoded.Memos[^1].ExactText);
    }

    public static TheoryData<string> NonCanonicalOrMalformedDocuments() {
        string canonical = Encoding.UTF8.GetString(
            MemoPodDocumentCodec.Encode(EmptyDocument())
        );
        return new TheoryData<string> {
            canonical + "\n",
            " " + canonical,
            canonical + "{}",
            canonical[..^1] + ",\"unknown\":0}",
            "{\"podId\":\"44444444444444444444444444444444\",\"schema\":\"atelia.memo-pod.document.v2\",\"topic\":\"topic\",\"nextMemoId\":1,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":1,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v3\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":1,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":1.0,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":0,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":4294967297,\"memos\":[]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":2,\"memos\":[{\"id\":\"m1:00000001\",\"title\":null,\"gist\":null,\"summary\":null,\"exactText\":\"x\",\"extra\":true}]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":2,\"memos\":[{\"exactText\":\"x\",\"id\":\"m1:00000001\"}]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":2,\"memos\":[{\"id\":\"m1:00000001\",\"exactText\":\"x\"}]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":2,\"memos\":[{\"id\":\"m1:00000001\",\"title\":\"\",\"gist\":null,\"summary\":null,\"exactText\":\"x\"}]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":3,\"memos\":[{\"id\":\"m1:00000002\",\"title\":null,\"gist\":null,\"summary\":null,\"exactText\":\"two\"},{\"id\":\"m1:00000001\",\"title\":null,\"gist\":null,\"summary\":null,\"exactText\":\"one\"}]}",
            "{\"schema\":\"atelia.memo-pod.document.v2\",\"podId\":\"44444444444444444444444444444444\",\"topic\":\"topic\",\"nextMemoId\":1,\"memos\":[{\"id\":\"m1:00000001\",\"title\":null,\"gist\":null,\"summary\":null,\"exactText\":\"uncommitted\"}]}",
            "{",
            "[]",
            "null"
        };
    }

    private static MemoPodDocument EmptyDocument()
        => new(PodId, "topic", 1, Array.Empty<Memo>());
}

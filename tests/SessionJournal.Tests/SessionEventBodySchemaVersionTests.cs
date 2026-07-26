using System.Text;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionEventBodySchemaVersionTests {
    public static TheoryData<SessionEventKind, int> DeclaredKinds {
        get {
            var data = new TheoryData<SessionEventKind, int>();
            foreach (SessionEventKind kind in Enum.GetValues<SessionEventKind>()) {
                data.Add(
                    kind,
                    kind == SessionEventKind.CompletionRequestPrepared ? 2 : 1
                );
            }
            return data;
        }
    }

    [Fact]
    public void ExpectedVersionMap_DefinesPreparedV2AndV1ForEveryOtherKind() {
        SessionEventKind[] kinds = Enum.GetValues<SessionEventKind>();

        Assert.NotEmpty(kinds);
        Assert.All(
            kinds,
            kind => Assert.Equal(
                kind == SessionEventKind.CompletionRequestPrepared ? 2 : 1,
                SessionEventCodec.GetExpectedBodySchemaVersion(kind)
            )
        );
    }

    [Fact]
    public void Encode_UsesKindVersionWithoutChangingCanonicalEnvelope() {
        byte[] payload = SessionEventCodec.Encode(
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("hello")
        );

        Assert.Equal(
            """{"v":1,"body":{"content":"hello"}}""",
            Encoding.UTF8.GetString(payload)
        );
    }

    [Theory]
    [MemberData(nameof(DeclaredKinds))]
    public void Decode_UnsupportedVersionReportsKindActualAndExpected(
        SessionEventKind kind,
        int expected
    ) {
        int actual = expected + 1;
        byte[] payload = Encoding.UTF8.GetBytes(
            $"{{\"v\":{actual},\"body\":{{}}}}"
        );
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(kind, payload, out _)
        );

        Assert.Contains(kind.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains($"actual={actual}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"expected={expected}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedV1_IsUnsupportedBeforeMalformedBodyIsParsed() {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                """{"v":1,"body":"malformed-v1"}"""u8,
                out _
            )
        );

        Assert.Contains("actual=1", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected=2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKind_IsRejectedBeforeBodyDispatchOrPayloadParsing() {
        var unknown = (SessionEventKind)uint.MaxValue;

        Assert.Throws<NotSupportedException>(
            () => SessionEventCodec.GetExpectedBodySchemaVersion(unknown)
        );
        Assert.Throws<NotSupportedException>(
            () => SessionEventCodec.Encode(unknown, new object())
        );
        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(unknown, "not-json"u8, out _)
        );
    }
}

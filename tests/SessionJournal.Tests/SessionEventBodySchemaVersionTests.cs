using System.Text;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionEventBodySchemaVersionTests {
    public static TheoryData<SessionEventKind> DeclaredKinds
        => new(Enum.GetValues<SessionEventKind>());

    [Fact]
    public void ExpectedVersionMap_DefinesCurrentV1ForEveryDeclaredKind() {
        SessionEventKind[] kinds = Enum.GetValues<SessionEventKind>();

        Assert.NotEmpty(kinds);
        Assert.All(
            kinds,
            kind => Assert.Equal(1, SessionEventCodec.GetExpectedBodySchemaVersion(kind))
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
        SessionEventKind kind
    ) {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                kind,
                """{"v":2,"body":{}}"""u8,
                out _
            )
        );

        Assert.Contains(kind.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("actual=2", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected=1", error.Message, StringComparison.Ordinal);
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

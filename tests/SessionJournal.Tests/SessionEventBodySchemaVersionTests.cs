using System.Text;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionEventBodySchemaVersionTests {
    public static TheoryData<SessionEventKind, int> SingleVersionKinds {
        get {
            var data = new TheoryData<SessionEventKind, int>();
            foreach (SessionEventKind kind in Enum.GetValues<SessionEventKind>()) {
                if (kind == SessionEventKind.CompletionRequestPrepared) {
                    continue;
                }
                data.Add(
                    kind,
                    ExpectedVersion(kind)
                );
            }
            return data;
        }
    }

    [Fact]
    public void ExpectedVersionMap_DefinesOnlySingleVersionKinds() {
        SessionEventKind[] kinds = Enum.GetValues<SessionEventKind>()
            .Where(static kind => kind != SessionEventKind.CompletionRequestPrepared)
            .ToArray();

        Assert.NotEmpty(kinds);
        Assert.All(
            kinds,
            kind => Assert.Equal(
                ExpectedVersion(kind),
                SessionEventCodec.GetExpectedBodySchemaVersion(kind)
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            SessionEventCodec.GetExpectedBodySchemaVersion(
                SessionEventKind.CompletionRequestPrepared
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
    [InlineData(SessionCreationOrigin.Native, "native")]
    [InlineData(SessionCreationOrigin.LegacyImport, "legacy-import")]
    public void SessionCreatedV2_RequiresCanonicalOrigin(
        SessionCreationOrigin origin,
        string token
    ) {
        byte[] payload = SessionEventCodec.Encode(
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(origin)
        );

        Assert.Equal(
            $"{{\"v\":2,\"body\":{{\"origin\":\"{token}\"}}}}",
            Encoding.UTF8.GetString(payload)
        );
        var decoded = Assert.IsType<SessionCreatedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.SessionCreated,
                payload,
                out int version
            )
        );
        Assert.Equal(2, version);
        Assert.Equal(origin, decoded.Origin);
    }

    [Fact]
    public void SessionCreatedV1AndUnknownOrigin_AreRejected() {
        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.SessionCreated,
                """{"v":1,"body":{}}"""u8,
                out _
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.SessionCreated,
                """{"v":2,"body":{"origin":"unknown"}}"""u8,
                out _
            )
        );
    }

    [Theory]
    [MemberData(nameof(SingleVersionKinds))]
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
    public void PreparedV2_IsUnsupportedBeforeMalformedBodyIsParsed() {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                """{"v":2,"body":"malformed-v2"}"""u8,
                out _
            )
        );

        Assert.Contains("actual=2", error.Message, StringComparison.Ordinal);
        Assert.Contains("supported=5|6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedV3_IsUnsupportedBeforeMalformedBodyIsParsed() {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                """{"v":3,"body":"malformed-v3"}"""u8,
                out _
            )
        );

        Assert.Contains("actual=3", error.Message, StringComparison.Ordinal);
        Assert.Contains("supported=5|6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedV4_IsUnsupportedBeforeMalformedBodyIsParsed() {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                """{"v":4,"body":"malformed-v4"}"""u8,
                out _
            )
        );

        Assert.Contains("actual=4", error.Message, StringComparison.Ordinal);
        Assert.Contains("supported=5|6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparedV7_IsUnsupportedBeforeMalformedBodyIsParsed() {
        var error = Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                """{"v":7,"body":"malformed-v7"}"""u8,
                out _
            )
        );

        Assert.Contains("actual=7", error.Message, StringComparison.Ordinal);
        Assert.Contains("supported=5|6", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6.0")]
    [InlineData("6e0")]
    [InlineData("\"6\"")]
    public void PreparedVersion_RejectsNumericAliases(string versionLiteral) {
        byte[] payload = Encoding.UTF8.GetBytes(
            $"{{\"v\":{versionLiteral},\"body\":{{}}}}"
        );

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                payload,
                out _
            )
        );

        Assert.Contains("canonical integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedV1_IsUnsupported() {
        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptFailed,
                """{"v":1,"body":{}}"""u8,
                out _
            )
        );
    }

    [Theory]
    [InlineData(11u)]
    [InlineData(12u)]
    public void RetiredRawKindIds_ArePermanentlyUnsupported(uint rawKind) {
        var kind = (SessionEventKind)rawKind;

        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.GetExpectedBodySchemaVersion(kind)
        );
        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Encode(kind, new object())
        );
        Assert.Throws<NotSupportedException>(() =>
            SessionEventCodec.Decode(kind, "not-json"u8, out _)
        );
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

    private static int ExpectedVersion(SessionEventKind kind)
        => kind switch {
            SessionEventKind.RuntimeConfigSetup => 2,
            SessionEventKind.SessionCreated => 2,
            SessionEventKind.CompletionAttemptFailed => 2,
            _ => 1
        };
}

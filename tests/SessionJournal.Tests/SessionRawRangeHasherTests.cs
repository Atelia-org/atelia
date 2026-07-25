using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionRawRangeHasherTests {
    private static readonly EventAddress A = EventAddressTextCodec.Parse(
        "ej1:00000000000000010000000100000000"
    );
    private static readonly EventAddress A2 = EventAddressTextCodec.Parse(
        "ej1:00000000000000030000000100000000"
    );
    private static readonly EventAddress B = EventAddressTextCodec.Parse(
        "ej1:00000000000000020000000100000000"
    );

    [Fact]
    public void Compute_HasStableCanonicalGolden() {
        SessionRawRangeHashEntry[] entries = [
            new(A, null, EventKind: 4, BodySchemaVersion: 1, new string('1', 64)),
            new(B, A, EventKind: 8, BodySchemaVersion: 1, new string('2', 64))
        ];

        Assert.Equal(
            "04f9fb29d17f12c8573a3623ffd347055b080d4f005c34d6524183af7d6ff268",
            SessionRawRangeHasher.Compute(rawStartExclusive: null, B, entries)
        );
    }

    [Fact]
    public void Compute_IsSensitiveToAddressKindSchemaAndPayload() {
        SessionRawRangeHashEntry[] baseline = [
            new(A, null, 4, 1, new string('1', 64)),
            new(B, A, 8, 1, new string('2', 64))
        ];
        string expected = SessionRawRangeHasher.Compute(null, B, baseline);

        Assert.NotEqual(expected, SessionRawRangeHasher.Compute(null, B, [
            baseline[0] with { Address = A2 },
            baseline[1] with { Parent = A2 }
        ]));
        Assert.NotEqual(expected, SessionRawRangeHasher.Compute(null, B, [
            baseline[0],
            baseline[1] with { EventKind = 9 }
        ]));
        Assert.NotEqual(expected, SessionRawRangeHasher.Compute(null, B, [
            baseline[0],
            baseline[1] with { BodySchemaVersion = 2 }
        ]));
        Assert.NotEqual(expected, SessionRawRangeHasher.Compute(null, B, [
            baseline[0],
            baseline[1] with { PayloadSha256 = new string('3', 64) }
        ]));
    }

    [Fact]
    public void Compute_RejectsBrokenParentContinuity() {
        SessionRawRangeHashEntry[] disconnected = [
            new(A, null, 4, 1, new string('1', 64)),
            new(B, A2, 8, 1, new string('2', 64))
        ];

        Assert.Throws<ArgumentException>(() => SessionRawRangeHasher.Compute(null, B, disconnected));
    }
}

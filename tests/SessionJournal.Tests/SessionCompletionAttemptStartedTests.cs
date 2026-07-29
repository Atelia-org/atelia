using System.Text;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionCompletionAttemptStartedTests {
    [Fact]
    public void Codec_RoundtripsStrictEmptyBody() {
        var body = new CompletionAttemptStartedBody();

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.CompletionAttemptStarted,
            body
        );
        var decoded = Assert.IsType<CompletionAttemptStartedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptStarted,
                encoded,
                out int version
            )
        );

        Assert.Equal(
            """{"v":1,"body":{}}""",
            Encoding.UTF8.GetString(encoded)
        );
        Assert.Equal(1, version);
        Assert.Equal(body, decoded);
    }

    [Theory]
    [InlineData("""{"v":1,"unknown":true,"body":{}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"opaque"}}""")]
    public void Codec_RejectsNonEmptyOrNonExactPayload(string json) {
        Assert.Throws<InvalidDataException>(() =>
            SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptStarted,
                Encoding.UTF8.GetBytes(json),
                out _
            )
        );
    }
}

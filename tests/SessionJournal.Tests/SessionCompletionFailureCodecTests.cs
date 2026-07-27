using System.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionCompletionFailureCodecTests {
    [Fact]
    public void CompletionAttemptFailed_RoundtripsCanonicalPayload() {
        var body = new CompletionAttemptFailedBody(
            CompletionTerminationKind.Incomplete,
            "length",
            "max tokens",
            Array.AsReadOnly(["stream warning"])
        );

        byte[] encoded = SessionEventCodec.Encode(SessionEventKind.CompletionAttemptFailed, body);
        var decoded = Assert.IsType<CompletionAttemptFailedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionAttemptFailed, encoded, out int version)
        );

        Assert.Equal(2, version);
        Assert.Equal(body.TerminationKind, decoded.TerminationKind);
        Assert.Equal(body.ProviderReason, decoded.ProviderReason);
        Assert.Equal(body.Detail, decoded.Detail);
        Assert.Equal(body.Errors, decoded.Errors);
        Assert.Equal(encoded, SessionEventCodec.Encode(SessionEventKind.CompletionAttemptFailed, decoded));
    }

    [Theory]
    [InlineData("{\"v\":2,\"unknown\":true,\"body\":{\"terminationKind\":\"failed\",\"providerReason\":null,\"detail\":null,\"errors\":[]}}")]
    [InlineData("{\"v\":2,\"body\":{\"terminationKind\":\"failed\",\"terminationKind\":\"incomplete\",\"providerReason\":null,\"detail\":null,\"errors\":[]}}")]
    [InlineData("{\"v\":2,\"body\":{\"terminationKind\":\"completed\",\"providerReason\":null,\"detail\":null,\"errors\":[]}}")]
    [InlineData("{\"v\":2,\"body\":{\"terminationKind\":\"failed\",\"providerReason\":null,\"detail\":null,\"errors\":[],\"unknown\":true}}")]
    public void CompletionAttemptFailed_StrictDecodeRejectsInvalidPayload(string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionAttemptFailed,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }
}

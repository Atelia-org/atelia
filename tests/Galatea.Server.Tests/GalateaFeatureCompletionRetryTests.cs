using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaFeatureCompletionRetryTests {
    [Theory]
    [InlineData("normalizer")]
    [InlineData("mail")]
    [InlineData("note")]
    [InlineData("recall")]
    public async Task ProductionFeatureBindingRetriesOnlyGenerationAndKeepsRequest(string feature) {
        var factory = new ScriptedFactory();
        await using var fixture = GalateaTestHost.Create(factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            inputNormalizerConnectionId: "test", outboundMailExtractorConnectionId: "test",
            characterNoteExtractorConnectionId: "test", memoRecallConnectionId: "test");
        var clock = new GalateaLabClock();
        await using var owner = new GalateaCompletionOwner(
            GalateaConfigLoader.Load(fixture.ConfigPath), factory, clock);
        ICompletionClient client = feature switch {
            "normalizer" => owner.GetInputNormalizerClient(),
            "mail" => owner.GetOutboundMailExtractorClient(),
            "note" => owner.GetCharacterNoteExtractorClient(),
            "recall" => owner.GetMemoRecallClient(),
            _ => throw new InvalidOperationException()
        };
        var request = new CompletionRequest("model", new("system",
            new([], CompletionToolChoice.None), []), []);
        Task<CompletionResult> pending = client.StreamCompletionAsync(request, null);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, factory.Client.Calls);
        clock.Advance(TimeSpan.FromSeconds(6));
        CompletionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Termination.IsSuccess);
        Assert.Equal(2, factory.Client.Calls);
        Assert.All(factory.Client.Requests, value => Assert.Same(request, value));
        Assert.Equal(factory.Client.Name, client.Name);
        Assert.Equal(factory.Client.ApiSpecId, client.ApiSpecId);
        Assert.Equal(1, factory.Creates);
    }

    private sealed class ScriptedFactory : ICompletionClientFactory {
        internal ScriptedClient Client { get; } = new();
        internal int Creates;
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Creates++;
            return Client;
        }
    }

    private sealed class ScriptedClient : ICompletionClient {
        internal int Calls;
        internal List<CompletionRequest> Requests { get; } = [];
        public string Name => "feature-test";
        public string ApiSpecId => "test-api";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Calls++;
            Requests.Add(request);
            if (Calls == 1) {
                throw new CompletionFailureException(new(CompletionFailureKind.Http, 503), "temporary");
            }
            return Task.FromResult(new CompletionResult(new([new ActionBlock.Text("result")]),
                CompletionDescriptor.From(this, request)));
        }
    }
}

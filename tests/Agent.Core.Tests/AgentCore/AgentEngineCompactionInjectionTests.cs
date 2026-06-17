using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Agent.Core.Tests;

public sealed class AgentEngineCompactionInjectionTests {
    [Fact]
    public async Task StepAsync_CompactionCanSummarizePrefixContainingInjectionEntries() {
        var client = new RecordingCompletionClient(
            new ActionMessage([new ActionBlock.Text("summary from compaction")])
        );
        var profile = new LlmProfile(client, "model-a", "test-profile", SoftContextTokenCap: 4096);
        var engine = new AgentEngine();

        engine.State.AppendObservation(new ObservationEntry());
        engine.State.AppendAction(CreateAction("alpha", profile));
        engine.InjectActionContent(
            new ActionInjectionRequest(
                "injected-thought",
                new InjectionSource(InjectionSourceKind.Wizard),
                InjectedActionContentMode.Text
            )
        );
        engine.State.AppendAction(CreateAction("omega", profile));
        engine.State.AppendObservation(new ObservationEntry());
        engine.State.AppendAction(CreateAction("tail", profile));

        Assert.True(engine.RequestCompaction("compact-system", "compact-now"));

        await engine.StepAsync(profile);

        var request = Assert.Single(client.Requests);
        var actionMessages = request.Context.OfType<ActionMessage>().ToArray();
        Assert.Contains(actionMessages, static message => message.GetFlattenedText().Contains("injected-thought", StringComparison.Ordinal));
        Assert.IsType<RecapEntry>(engine.State.RecentHistory[0]);
    }

    [Fact]
    public async Task StepAsync_CompactionAcceptsInjectionAsSuffixStart() {
        var client = new RecordingCompletionClient(
            new ActionMessage([new ActionBlock.Text("summary from compaction")])
        );
        var profile = new LlmProfile(client, "model-a", "test-profile", SoftContextTokenCap: 4096);
        var engine = new AgentEngine();

        engine.State.AppendObservation(new ObservationEntry());
        engine.State.AppendAction(CreateAction("turn-one", profile));
        engine.State.AppendObservation(new ObservationEntry());
        engine.InjectActionContent(
            new ActionInjectionRequest(
                "continue-here",
                new InjectionSource(InjectionSourceKind.Wizard),
                InjectedActionContentMode.Text
            )
        );

        Assert.True(engine.RequestCompaction("compact-system", "compact-now"));

        var result = await engine.StepAsync(profile);

        Assert.True(result.ProgressMade);
        Assert.IsType<RecapEntry>(engine.State.RecentHistory[0]);
        Assert.IsType<InjectionEntry>(engine.State.RecentHistory[1]);
    }

    private static ActionEntry CreateAction(string content, LlmProfile profile) {
        return new ActionEntry(
            new ActionMessage([new ActionBlock.Text(content)]),
            new CompletionDescriptor(profile.Client.Name, profile.Client.ApiSpecId, profile.ModelId)
        );
    }

    private sealed class RecordingCompletionClient : ICompletionClient {
        private readonly ActionMessage _message;

        public RecordingCompletionClient(ActionMessage message) {
            _message = message;
        }

        public string Name => "test-provider";

        public string ApiSpecId => "test-spec";

        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            Requests.Add(request);
            return Task.FromResult(
                new CompletionResult(
                    _message,
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                )
            );
        }
    }
}

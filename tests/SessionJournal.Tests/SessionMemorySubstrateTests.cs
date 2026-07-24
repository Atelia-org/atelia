using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionMemorySubstrateTests {
    [Fact]
    public void RenderedMemoryPack_ToSessionContextHeader_UsesSessionJournalHeaderType() {
        var pack = new MemoryPack();
        pack.System.Add("policy", new MemoryPackBlock("stay focused"));
        pack.Observation.Add("summary", new MemoryPackBlock("observed facts"));
        pack.Action.Add("self", new MemoryPackBlock("acted carefully"));

        var header = pack.Render().ToSessionContextHeader();

        Assert.IsType<SessionContextHeader>(header);
        Assert.Contains("## policy", header.SystemPromptFragment);
        Assert.Contains("observed facts", header.ObservationMessage);
        Assert.Equal("## self\n\nacted carefully", header.ActionMessage?.GetFlattenedText());
    }

    [Fact]
    public void ContextHeaderSnapshot_FromSessionContextHeader_ReadsAllHeaderSegments() {
        var header = new SessionContextHeader(
            "system fragment",
            "observation fragment",
            new ActionMessage([new ActionBlock.Text("action fragment")])
        );

        var snapshot = ContextHeaderSnapshot.FromSessionContextHeader(header);

        Assert.Equal("system fragment", snapshot.SystemPromptFragment);
        Assert.Equal("observation fragment", snapshot.ObservationMessage);
        Assert.Equal("action fragment", snapshot.ActionMessage);
    }

    [Fact]
    public async Task MemoryMaintenanceOrchestrator_RunAsync_UpdatesTargetBlock() {
        var pack = new MemoryPack();
        pack.Observation.Add("summary", new MemoryPackBlock("old"));
        var maintainer = new StubMaintainer(
            "maintainer.summary",
            new MemoryPackBlockPath(MemoryPackCarrier.Observation, "summary"),
            "new"
        );

        var result = await MemoryMaintenanceOrchestrator.RunAsync(
            pack,
            new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("hello")]),
            [maintainer],
            CancellationToken.None
        );

        Assert.Single(result.Results);
        Assert.Equal("new", result.Results[0].NewBlock.Text);
        Assert.True(result.UpdatedMemoryPack.TryGetBlock(maintainer.Target, out var updated));
        Assert.Equal("new", updated.Text);
        Assert.True(pack.TryGetBlock(maintainer.Target, out var original));
        Assert.Equal("old", original.Text);
    }

    [Fact]
    public async Task RewriteMemoryBlockMaintainer_MaintainAsync_ExpandsSessionContextHeader() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(
            request => {
                Assert.Contains(request.Context, message => message is ObservationMessage observation && observation.Content == "system fragment");
                Assert.Contains(request.Context, message => message is ObservationMessage observation && observation.Content == "observation fragment");
                Assert.Contains(request.Context, message => message is ActionMessage action && action.GetFlattenedText() == "action fragment");
                return new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("updated block")]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );
        var maintainer = CreateRewriteMaintainer(client);

        var result = await maintainer.MaintainAsync(
            new MemoryBlockMaintenanceRequest(
                new RecentHistorySlice(
                    ContextHeaderSnapshot.Empty,
                    [
                        new SessionContextHeader(
                            "system fragment",
                            "observation fragment",
                            new ActionMessage([new ActionBlock.Text("action fragment")])
                        )
                    ]
                ),
                new MemoryPackBlock("old")
            ),
            CancellationToken.None
        );

        Assert.Equal("updated block", result.NewBlock.Text);
    }

    [Fact]
    public async Task RewriteMemoryBlockMaintainer_MaintainAsync_IncompleteCompletionThrowsSessionJournalException() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("partial")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId),
                termination: CompletionTermination.Incomplete("length", "scripted truncation")
            )
        );
        var maintainer = CreateRewriteMaintainer(client);

        var ex = await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            async () => await maintainer.MaintainAsync(
                new MemoryBlockMaintenanceRequest(
                    new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("hello")]),
                    new MemoryPackBlock("old")
                ),
                CancellationToken.None
            )
        );

        Assert.Equal(CompletionTerminationKind.Incomplete, ex.Termination.Kind);
    }

    private static RewriteMemoryBlockMaintainer CreateRewriteMaintainer(ICompletionClient client)
        => new(
            new MemoryRewriteProfile(
                "maintainer.summary",
                new MemoryPackBlockPath(MemoryPackCarrier.Observation, "summary"),
                "system prompt",
                "user prompt"
            ),
            client,
            "model-a"
        );

    private sealed class StubMaintainer : IMemoryBlockMaintainer {
        private readonly string _newText;

        public StubMaintainer(string id, MemoryPackBlockPath target, string newText) {
            Id = id;
            Target = target;
            _newText = newText;
        }

        public string Id { get; }

        public MemoryPackBlockPath Target { get; }

        public ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MemoryBlockMaintenanceResult(
                Id,
                Target,
                new MemoryPackBlock(_newText)
            ));
        }
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = new();

        public string Name => "scripted";

        public string ApiSpecId => "openai-chat-v1";

        public void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0) { throw new InvalidOperationException("No scripted response remaining."); }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}

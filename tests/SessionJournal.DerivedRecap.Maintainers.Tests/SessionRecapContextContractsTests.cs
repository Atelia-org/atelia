using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class SessionRecapContextContractsTests {
    [Fact]
    public void ContextHeaderPack_Render_UsesThreeCarriersInStableOrder() {
        var pack = new ContextHeaderPack();
        pack.System.Add("system.a", new ContextHeaderBlock("alpha"));
        pack.System.Add("system.b", new ContextHeaderBlock("beta"));
        pack.Observation.Add("observation.a", new ContextHeaderBlock("gamma"));
        pack.Action.Add("action.a", new ContextHeaderBlock("delta"));

        var rendered = pack.Render();

        Assert.Equal("## system.a\n\nalpha\n\n## system.b\n\nbeta", rendered.SystemPromptFragment);
        Assert.Equal("## observation.a\n\ngamma", rendered.ObservationMessage);
        Assert.Equal("## action.a\n\ndelta", rendered.ActionMessage);
    }

    [Fact]
    public void ContextHeaderPackDraft_DoesNotMutateBaseAndPreservesExistingPosition() {
        var pack = new ContextHeaderPack();
        pack.System.Add("a", new ContextHeaderBlock("old-a"));
        pack.System.Add("b", new ContextHeaderBlock("old-b"));
        pack.System.Add("c", new ContextHeaderBlock("old-c"));

        var draft = new ContextHeaderPackDraft(pack);
        draft.ReplaceBlock(new ContextHeaderBlockPath(ContextHeaderCarrier.System, "a"), "new-a");
        draft.UpsertBlock(new ContextHeaderBlockPath(ContextHeaderCarrier.System, "b"), "new-b");
        draft.UpsertBlock(new ContextHeaderBlockPath(ContextHeaderCarrier.System, "d"), "new-d", order: 2);
        Assert.True(draft.RemoveBlock(new ContextHeaderBlockPath(ContextHeaderCarrier.System, "c")));
        var built = draft.Build();

        Assert.Equal(["a", "b", "c"], pack.System.Keys.ToArray());
        Assert.Equal("old-a", pack.System["a"].Text);
        Assert.Equal("old-b", pack.System["b"].Text);
        Assert.Equal(["a", "b", "d"], built.System.Keys.ToArray());
        Assert.Equal("new-a", built.System["a"].Text);
        Assert.Equal("new-b", built.System["b"].Text);
    }

    [Fact]
    public void ContextHeaderSnapshot_ToSessionContextHeader_UsesSessionJournalHeaderType() {
        var pack = new ContextHeaderPack();
        pack.System.Add("policy", new ContextHeaderBlock("stay focused"));
        pack.Observation.Add("summary", new ContextHeaderBlock("observed facts"));
        pack.Action.Add("self", new ContextHeaderBlock("acted carefully"));

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
    public async Task RecapMaintenanceOrchestrator_RunAsync_UpdatesTargetBlock() {
        var pack = new ContextHeaderPack();
        pack.Observation.Add("summary", new ContextHeaderBlock("old"));
        var maintainer = new StubMaintainer(
            "maintainer.summary",
            new ContextHeaderBlockPath(ContextHeaderCarrier.Observation, "summary"),
            "new"
        );

        var result = await RecapMaintenanceOrchestrator.RunAsync(
            pack,
            new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("hello")]),
            [maintainer],
            CancellationToken.None
        );

        Assert.Single(result.Results);
        Assert.Equal("new", result.Results[0].NewBlock.Text);
        Assert.True(result.UpdatedContextHeaderPack.TryGetBlock(maintainer.Target, out var updated));
        Assert.Equal("new", updated.Text);
        Assert.True(pack.TryGetBlock(maintainer.Target, out var original));
        Assert.Equal("old", original.Text);
    }

    [Fact]
    public async Task RecapMaintenanceOrchestrator_RunAsync_CreatesMissingTargetFromEmptyOldBlock() {
        var pack = new ContextHeaderPack();
        var target = new ContextHeaderBlockPath(ContextHeaderCarrier.Action, "new-block");
        var maintainer = new StubMaintainer(
            "maintainer.new",
            target,
            request => {
                Assert.Equal(string.Empty, request.OldBlock.Text);
                return "created";
            }
        );

        var result = await RecapMaintenanceOrchestrator.RunAsync(
            pack,
            new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("hello")]),
            [maintainer],
            CancellationToken.None
        );

        Assert.False(pack.Action.ContainsKey(target.BlockKey));
        Assert.Equal("created", result.UpdatedContextHeaderPack.Action[target.BlockKey].Text);
    }

    [Fact]
    public async Task RecapMaintenanceOrchestrator_RunAsync_RejectsDuplicateTargets() {
        var target = new ContextHeaderBlockPath(ContextHeaderCarrier.System, "same");

        await Assert.ThrowsAsync<ArgumentException>(
            () => RecapMaintenanceOrchestrator.RunAsync(
                new ContextHeaderPack(),
                new RecentHistorySlice(ContextHeaderSnapshot.Empty, Array.Empty<IHistoryMessage>()),
                [
                    new StubMaintainer("first", target, "one"),
                    new StubMaintainer("second", target, "two"),
                ],
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task RecapMaintenanceOrchestrator_RunAsync_RejectsEmptyMaintainerList() {
        await Assert.ThrowsAsync<ArgumentException>(
            () => RecapMaintenanceOrchestrator.RunAsync(
                new ContextHeaderPack(),
                new RecentHistorySlice(ContextHeaderSnapshot.Empty, Array.Empty<IHistoryMessage>()),
                Array.Empty<IRecapBlockMaintainer>(),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task RewriteRecapBlockMaintainer_MaintainAsync_ExpandsSessionContextHeader() {
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
            new RecapBlockMaintenanceRequest(
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
                new ContextHeaderBlock("old")
            ),
            CancellationToken.None
        );

        Assert.Equal("updated block", result.NewBlock.Text);
    }

    [Fact]
    public async Task RewriteRecapBlockMaintainer_MaintainAsync_IncompleteCompletionThrowsSessionJournalException() {
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
                new RecapBlockMaintenanceRequest(
                    new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("hello")]),
                    new ContextHeaderBlock("old")
                ),
                CancellationToken.None
            )
        );

        Assert.Equal(CompletionTerminationKind.Incomplete, ex.Termination.Kind);
    }

    private static RewriteRecapBlockMaintainer CreateRewriteMaintainer(ICompletionClient client)
        => new(
            new RecapRewriteProfile(
                "maintainer.summary",
                new ContextHeaderBlockPath(ContextHeaderCarrier.Observation, "summary"),
                "system prompt",
                "user prompt"
            ),
            "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            client,
            "model-a"
        );

    private sealed class StubMaintainer : IRecapBlockMaintainer {
        private readonly Func<RecapBlockMaintenanceRequest, string> _maintain;

        public StubMaintainer(string id, ContextHeaderBlockPath target, string newText)
            : this(id, target, _ => newText) {
        }

        public StubMaintainer(
            string id,
            ContextHeaderBlockPath target,
            Func<RecapBlockMaintenanceRequest, string> maintain
        ) {
            Id = id;
            Target = target;
            _maintain = maintain;
        }

        public string Id { get; }
        public string CapabilityFingerprint { get; } =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";

        public ContextHeaderBlockPath Target { get; }

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RecapBlockMaintenanceResult(
                Id,
                Target,
                new ContextHeaderBlock(_maintain(request))
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

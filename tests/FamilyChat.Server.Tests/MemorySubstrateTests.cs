using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.ChatSession.Tests;

public sealed class MemorySubstrateTests {
    [Fact]
    public void MemoryPack_Render_UsesThreeCarriersInStableOrder() {
        var pack = new MemoryPack();
        pack.System.Add("system.a", new MemoryPackBlock("alpha"));
        pack.System.Add("system.b", new MemoryPackBlock("beta"));
        pack.Observation.Add("observation.a", new MemoryPackBlock("gamma"));
        pack.Action.Add("action.a", new MemoryPackBlock("delta"));

        var rendered = pack.Render();

        Assert.Equal("## system.a\n\nalpha\n\n## system.b\n\nbeta", rendered.SystemPromptFragment);
        Assert.Equal("## observation.a\n\ngamma", rendered.ObservationMessage);
        Assert.Equal("## action.a\n\ndelta", rendered.ActionMessage);

        var header = rendered.ToContextHeader();
        Assert.Equal(rendered.SystemPromptFragment, header.SystemPromptFragment);
        Assert.Equal(rendered.ObservationMessage, header.ObservationMessage);
        Assert.Equal(rendered.ActionMessage, header.ActionMessage!.GetFlattenedText());
    }

    [Fact]
    public void MemoryPackDraft_DoesNotMutateBasePack() {
        var pack = new MemoryPack();
        pack.System.Add("a", new MemoryPackBlock("old-a"));
        pack.System.Add("b", new MemoryPackBlock("old-b"));

        var draft = new MemoryPackDraft(pack);
        draft.ReplaceBlock(new MemoryPackBlockPath(MemoryPackCarrier.System, "a"), "new-a");
        draft.UpsertBlock(new MemoryPackBlockPath(MemoryPackCarrier.System, "c"), "new-c", order: 1);
        Assert.True(draft.RemoveBlock(new MemoryPackBlockPath(MemoryPackCarrier.System, "b")));

        var built = draft.Build();

        Assert.Equal("old-a", pack.System["a"].Text);
        Assert.True(pack.System.ContainsKey("b"));
        Assert.False(pack.System.ContainsKey("c"));

        Assert.Equal(["a", "c"], built.System.Keys.ToArray());
        Assert.Equal("new-a", built.System["a"].Text);
        Assert.Equal("new-c", built.System["c"].Text);
    }

    [Fact]
    public void MemoryPackDraft_UpsertExistingBlockWithoutOrderPreservesPosition() {
        var pack = new MemoryPack();
        pack.System.Add("a", new MemoryPackBlock("old-a"));
        pack.System.Add("b", new MemoryPackBlock("old-b"));
        pack.System.Add("c", new MemoryPackBlock("old-c"));

        var draft = new MemoryPackDraft(pack);
        draft.UpsertBlock(new MemoryPackBlockPath(MemoryPackCarrier.System, "b"), "new-b");

        var built = draft.Build();

        Assert.Equal(["a", "b", "c"], built.System.Keys.ToArray());
        Assert.Equal("new-b", built.System["b"].Text);
    }

    [Fact]
    public void RecentHistorySlice_CarriesEmptyAndNonEmptyPriorContext() {
        var empty = new RecentHistorySlice(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("hello")],
            SourceId: "source-a",
            EstimatedTokens: 12
        );

        Assert.True(empty.PriorContext.IsEmpty);
        Assert.Equal("source-a", empty.SourceId);
        Assert.Equal<ulong>(12, empty.EstimatedTokens!.Value);

        var snapshot = ContextHeaderSnapshot.FromContextHeader(
            new ContextHeader(
                "system",
                "observation",
                new ActionMessage([new ActionBlock.Text("action")])
            )
        );

        Assert.False(snapshot.IsEmpty);
        Assert.Equal("system", snapshot.SystemPromptFragment);
        Assert.Equal("observation", snapshot.ObservationMessage);
        Assert.Equal("action", snapshot.ActionMessage);
    }

    [Fact]
    public async Task Orchestrator_AllowsMissingOldBlockAndCreatesBlockThroughDraft() {
        var pack = new MemoryPack();
        var target = new MemoryPackBlockPath(MemoryPackCarrier.Action, "new-block");
        var maintainer = new FakeMemoryBlockMaintainer("fake", target, request => {
            Assert.Equal(string.Empty, request.OldBlock.Text);
            Assert.Single(request.RecentHistory.Messages);
            return "created";
        });

        var results = await MemoryMaintenanceOrchestrator.RunAsync(
            pack,
            new RecentHistorySlice(ContextHeaderSnapshot.Empty, [new ObservationMessage("recent")]),
            [maintainer],
            CancellationToken.None
        );
        var updated = MemoryMaintenanceOrchestrator.ApplyResults(pack, results);

        Assert.False(pack.Action.ContainsKey("new-block"));
        Assert.Equal("created", updated.Action["new-block"].Text);
    }

    [Fact]
    public async Task Orchestrator_RejectsDuplicateTargets() {
        var target = new MemoryPackBlockPath(MemoryPackCarrier.System, "same");

        await Assert.ThrowsAsync<ArgumentException>(
            () => MemoryMaintenanceOrchestrator.RunAsync(
                new MemoryPack(),
                new RecentHistorySlice(ContextHeaderSnapshot.Empty, Array.Empty<IHistoryMessage>()),
                [
                    new FakeMemoryBlockMaintainer("first", target, _ => "one"),
                    new FakeMemoryBlockMaintainer("second", target, _ => "two"),
                ],
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task Orchestrator_RejectsEmptyMaintainerList() {
        await Assert.ThrowsAsync<ArgumentException>(
            () => MemoryMaintenanceOrchestrator.RunAsync(
                new MemoryPack(),
                new RecentHistorySlice(ContextHeaderSnapshot.Empty, Array.Empty<IHistoryMessage>()),
                Array.Empty<IMemoryBlockMaintainer>(),
                CancellationToken.None
            )
        );
    }

    private sealed class FakeMemoryBlockMaintainer(
        string id,
        MemoryPackBlockPath target,
        Func<MemoryBlockMaintenanceRequest, string> maintain
    ) : IMemoryBlockMaintainer {
        public string Id { get; } = id;
        public MemoryPackBlockPath Target { get; } = target;

        public ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new MemoryBlockMaintenanceResult(
                    Id,
                    Target,
                    new MemoryPackBlock(maintain(request)),
                    Array.Empty<MemoryMaintenanceNotice>(),
                    Array.Empty<string>()
                )
            );
        }
    }
}

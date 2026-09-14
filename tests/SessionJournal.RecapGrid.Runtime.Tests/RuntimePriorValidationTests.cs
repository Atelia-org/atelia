using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RuntimePriorValidationTests {
    [Theory]
    [InlineData("view", "PriorProjectionMissing")]
    [InlineData("count", "PriorProjectionMissing")]
    [InlineData("column", "PriorViewMismatch")]
    [InlineData("definition", "PriorViewMismatch")]
    [InlineData("cell", "PriorViewMismatch")]
    public async Task InvalidPriorMembersRejectBeforeAnyProviderCall(string mutation, string code) {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        RecapCellArtifact cell = Assert.Single(valid.PreviousCells);
        RecapRowView? view = mutation == "view" ? null : valid.PreviousView;
        IReadOnlyList<RecapCellArtifact> cells = mutation switch {
            "count" => [],
            "column" => [ReplaceCell(cell, column: new LogicalColumnId("case.other"))],
            "definition" => [ReplaceCell(cell, definition: new MaintainerDefinitionDigest(new string('e', 64)))],
            "cell" => [ReplaceCell(cell, content: "different content")],
            _ => valid.PreviousCells
        };
        await AssertRejected(Copy(valid, view, cells), code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorMemberOrderOrDuplicateRejectsBeforeDispatch(bool duplicate) {
        FrozenRowBatch firstRow = RuntimeTestFixture.Batch(columnCount: 2);
        RecapCellArtifact[] cells = firstRow.OrderedMissingWork.Select(work =>
            RecapCellArtifact.Create(work.LogicalColumnId, work.Definition.Digest,
                work.EvaluationKey, RecapCellOutcome.Updated, "content", work.Definition.MaxContentUtf8Bytes))
            .ToArray();
        RecapRowView previous = RecapRowView.Create(firstRow.Spec, cells);
        FrozenRowBatch projected = RuntimeTestFixture.BatchWithPrior();
        RecapCellArtifact[] invalid = duplicate ? [cells[0], cells[0]] : [cells[1], cells[0]];
        await AssertRejected(Copy(projected, previous, invalid), "PriorViewMismatch");
    }

    [Fact]
    public async Task ActualCellsMustMatchIndependentSpecDigest() {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        // Keep actual previous view/cells intact. Independently alter the expected
        // spec and its work so the failure cannot come from work/spec disagreement.
        var expected = new PriorInputReference.Projection(new PriorInputProjectionDigest(new string('f', 64)));
        FrozenRecapCellWork original = Assert.Single(valid.OrderedMissingWork);
        EvaluationKey key = EvaluationKey.Create(valid.Spec.HistorySegmentDigest, original.Definition.Digest, expected);
        RowBuildSpec spec = RowBuildSpec.CreateNormal(valid.Recipe, valid.Spec.Coordinate, expected,
            [new RowBuildAssignment.Evaluate(original.LogicalColumnId, key)]);
        FrozenRecapCellWork work = new(original.Ordinal, original.LogicalColumnId, key,
            original.Definition, original.Family);
        await AssertRejected(Copy(valid, valid.PreviousView, valid.PreviousCells, spec, [work]),
            "PriorProjectionMismatch");
    }

    [Fact]
    public async Task WorkWithDifferentPriorCannotBypassSpecAuthority() {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        FrozenRecapCellWork original = Assert.Single(valid.OrderedMissingWork);
        EvaluationKey changedKey = EvaluationKey.Create(valid.Spec.HistorySegmentDigest,
            original.Definition.Digest, PriorInputReference.FirstRow.Value);
        FrozenRecapCellWork changed = new(original.Ordinal, original.LogicalColumnId,
            changedKey, original.Definition, original.Family);
        // The earlier exact assignment check detects this disagreement before
        // the defensive WorkPriorMismatch check becomes reachable.
        await AssertRejected(Copy(valid, valid.PreviousView, valid.PreviousCells, work: [changed]),
            "WorkAuthorityMismatch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstRowRejectsPreviousViewOrCells(bool includeView) {
        FrozenRowBatch first = RuntimeTestFixture.Batch();
        FrozenRowBatch previous = RuntimeTestFixture.BatchWithPrior();
        await AssertRejected(Copy(first, includeView ? previous.PreviousView : null,
            includeView ? [] : previous.PreviousCells), "FirstRowPriorInvalid");
    }

    private static FrozenRowBatch Copy(FrozenRowBatch original, RecapRowView? previous,
        IReadOnlyList<RecapCellArtifact> cells, RowBuildSpec? spec = null,
        IReadOnlyList<FrozenRecapCellWork>? work = null) => new(
        original.TimelineHead, original.ControlHead, original.StoreIdentity, original.Recipe,
        original.HistorySegment, spec ?? original.Spec, previous, cells, work ?? original.OrderedMissingWork);

    private static RecapCellArtifact ReplaceCell(RecapCellArtifact original,
        LogicalColumnId? column = null, MaintainerDefinitionDigest? definition = null, string? content = null) {
        MaintainerDefinitionDigest target = definition ?? original.DefinitionDigest;
        EvaluationKey key = EvaluationKey.Create(original.EvaluationKey.HistorySegmentDigest,
            target, original.EvaluationKey.PriorInput);
        return RecapCellArtifact.Create(column ?? original.LogicalColumnId, target, key,
            original.Outcome, content ?? original.Content, 16 * 1024);
    }

    private static async Task AssertRejected(FrozenRowBatch batch, string code) {
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("must not dispatch"));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        var resolver = new ScriptedResolver(_ => new RecapCompletionRouteResolution.Bound(route));
        using var runtime = new RecapCompletionRuntime(resolver);
        var rejected = Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await runtime.ExecuteAsync(batch, default));
        Assert.Equal(code, rejected.Code);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, invoker.CallCount);
    }
}

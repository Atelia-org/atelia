using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Fact]
    public void ContextCompositionPolicyMatchesNeutralCarrierLimit() {
        Assert.Equal(
            SessionContextContributionContract.MaxContributionUtf8Bytes,
            ControlStorageLimits.MaximumContextComposableContentUtf8Bytes
        );
    }

    [Fact]
    public void ActivationAcceptsExactContentCapAndRejectsCapPlusOneWithoutMutation() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        ).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(
                path,
                journal.BranchRefId,
                values.Admission
            )
        ).Handle;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutFamilyDefinition(head, values.Family)
        ).Head;

        MaintainerDefinitionRevision exact = Definition(
            values,
            "case.exact",
            "exact",
            ControlStorageLimits.MaximumContextComposableContentUtf8Bytes
        );
        MaintainerDefinitionRevision tooLarge = Definition(
            values,
            "case.large",
            "large",
            ControlStorageLimits.MaximumContextComposableContentUtf8Bytes + 1
        );
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutMaintainerDefinition(head, exact)
        ).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutMaintainerDefinition(head, tooLarge)
        ).Head;
        GridBuildRecipe exactRecipe = Recipe(values.TimelineHead, exact);
        GridBuildRecipe largeRecipe = Recipe(values.TimelineHead, tooLarge);
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutBuildRecipe(
                head,
                values.TimelineHead,
                exactRecipe,
                bootstrapWitness: null
            )
        ).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutBuildRecipe(
                head,
                values.TimelineHead,
                largeRecipe,
                bootstrapWitness: null
            )
        ).Head;
        head = Assert.IsType<RecapGridControlActivateResult.Applied>(
            handle.Coordinator.CompareExchangeActiveRecipe(
                head,
                values.TimelineHead,
                exactRecipe.Digest,
                RecapGridControlActivationPurpose.Direct
            )
        ).Head;

        var paths = new ControlPaths(
            path,
            journal.BranchRefId,
            values.TimelineHead.TimelineId
        );
        byte[] before = File.ReadAllBytes(paths.StatePath);
        RecapGridControlActivateResult.Unauthorized rejected = Assert.IsType<
            RecapGridControlActivateResult.Unauthorized>(
            handle.Coordinator.CompareExchangeActiveRecipe(
                head,
                values.TimelineHead,
                largeRecipe.Digest,
                RecapGridControlActivationPurpose.Direct
            )
        );
        Assert.Equal("ActiveRecipeContentLimit", rejected.Rule);
        Assert.Equal(before, File.ReadAllBytes(paths.StatePath));
        Assert.Equal(head, Assert.IsType<
            RecapGridControlSnapshotResult.Available>(
            handle.Reader.ReadSnapshot()
        ).Snapshot.Head);
    }

    [Fact]
    public void ActivationRejectsDuplicateContextTargetWithoutMutation() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        ).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(
                path,
                journal.BranchRefId,
                values.Admission
            )
        ).Handle;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutFamilyDefinition(head, values.Family)
        ).Head;
        MaintainerDefinitionRevision first = Definition(
            values,
            "case.first",
            "shared",
            1024
        );
        MaintainerDefinitionRevision second = Definition(
            values,
            "case.second",
            "shared",
            1024
        );
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutMaintainerDefinition(head, first)
        ).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutMaintainerDefinition(head, second)
        ).Head;
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            values.TimelineHead.TimelineId,
            bootstrapThroughRowId: null,
            BuildTarget.Create([
                new BuildTargetColumn(first.LogicalColumnId, first.Digest),
                new BuildTargetColumn(second.LogicalColumnId, second.Digest)
            ])
        );
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutBuildRecipe(
                head,
                values.TimelineHead,
                recipe,
                bootstrapWitness: null
            )
        ).Head;
        var paths = new ControlPaths(
            path,
            journal.BranchRefId,
            values.TimelineHead.TimelineId
        );
        byte[] before = File.ReadAllBytes(paths.StatePath);

        RecapGridControlActivateResult.Unauthorized rejected = Assert.IsType<
            RecapGridControlActivateResult.Unauthorized>(
            handle.Coordinator.CompareExchangeActiveRecipe(
                head,
                values.TimelineHead,
                recipe.Digest,
                RecapGridControlActivationPurpose.Direct
            )
        );
        Assert.Equal("ActiveRecipeDuplicateContextTarget", rejected.Rule);
        Assert.Equal(before, File.ReadAllBytes(paths.StatePath));
    }

    private static MaintainerDefinitionRevision Definition(
        Values values,
        string column,
        string block,
        int maximumContentUtf8Bytes
    ) => MaintainerDefinitionRevision.Create(
        new LogicalColumnId(column),
        values.Family.Digest,
        new ContextHeaderBlockTarget(
            ContextHeaderCarrier.System,
            block,
            $"Derived context from prior history: {block}"
        ),
        values.Definition.Capability,
        values.Definition.DeclarativeSpec,
        maximumContentUtf8Bytes
    );

    private static GridBuildRecipe Recipe(
        TimelineHeadRef timelineHead,
        MaintainerDefinitionRevision definition
    ) => GridBuildRecipe.CreateFull(
        timelineHead.TimelineId,
        bootstrapThroughRowId: null,
        BuildTarget.Create([
            new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest
            )
        ])
    );
}

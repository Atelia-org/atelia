using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.Data;
using Atelia.SessionJournal.HistoryTimeline;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.RecapGrid.WalkingSkeleton.Tests;

/// <summary>
/// This fixture is deliberately test-only. Timeline identity uses the formal
/// WP-01A owner; WP-02 must replace the remaining Grid-only shapes.
/// </summary>
public sealed class GridWalkingSkeletonTests {
    private const string FirstRowSentinel = "first-row-v1";
    private static readonly TimelineId SkeletonTimeline = new(
        "00112233445566778899aabbccddeeff"
    );
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    [Fact]
    public void FirstAndSecondRowFlowThroughTheIdentityChain() {
        DefinitionShape culprit = Definition("culprit", "family-analysis-v1");
        DefinitionShape world = Definition("world", "family-analysis-v1");
        RecipeShape recipe = Recipe("full", culprit, world);

        HistorySegmentDescriptor row0 = Descriptor(
            rowId: "row-0",
            predecessor: null,
            rawRangeCommitment: "raw-range-0"
        );
        RowBuildSpecShape spec0 = BuildSpec(row0, recipe, null);
        RowViewShape view0 = View(
            spec0,
            [Evaluate(spec0, culprit), Evaluate(spec0, world)]
        );
        ContextValueShape context0 = Context(view0);

        Assert.Equal(row0.DescriptorDigest, context0.RowDescriptorDigest);
        Assert.Equal(
            ["culprit", "world"],
            context0.Contributions.Select(static item => item.LogicalColumnId)
        );

        PriorProjectionShape prior = Project(view0);
        HistorySegmentDescriptor row1 = Descriptor(
            rowId: "row-1",
            predecessor: row0,
            rawRangeCommitment: "raw-range-1"
        );
        RowBuildSpecShape spec1 = BuildSpec(row1, recipe, view0);
        ImmutableArray<CellShape> row1Cells = [
            Evaluate(spec1, culprit),
            Evaluate(spec1, world)
        ];
        RowViewShape view1 = View(spec1, row1Cells);
        ContextValueShape context1 = Context(view1);

        Assert.Equal(view0.ViewDigest, view1.PreviousViewDigest);
        Assert.Equal(prior.ProjectionDigest, spec1.PriorProjectionDigest);
        Assert.All(row1Cells, cell =>
            Assert.Equal(prior.ProjectionDigest, cell.PriorProjectionDigest));
        Assert.Equal(row1.DescriptorDigest, context1.RowDescriptorDigest);
        Assert.Equal(2, context1.Contributions.Length);
    }

    [Fact]
    public void ContentEquivalentViewsShareProjectionAndEvaluationIdentity() {
        DefinitionShape culprit = Definition("culprit", "family-analysis-v1");
        DefinitionShape world = Definition("world", "family-analysis-v1");
        RecipeShape firstRecipe = Recipe("full-a", culprit, world);
        RecipeShape secondRecipe = Recipe("full-b", culprit, world);
        HistorySegmentDescriptor priorRow = Descriptor(
            "row-prior",
            null,
            "raw-prior"
        );

        RowBuildSpecShape firstPriorSpec = BuildSpec(
            priorRow,
            firstRecipe,
            null
        );
        RowBuildSpecShape secondPriorSpec = BuildSpec(
            priorRow,
            secondRecipe,
            null
        );
        RowViewShape firstView = View(
            firstPriorSpec,
            [
                Evaluate(firstPriorSpec, culprit),
                Evaluate(firstPriorSpec, world)
            ]
        );
        RowViewShape secondView = View(
            secondPriorSpec,
            [
                Evaluate(secondPriorSpec, culprit),
                Evaluate(secondPriorSpec, world)
            ]
        );

        Assert.NotEqual(firstView.ViewDigest, secondView.ViewDigest);
        Assert.Equal(
            Project(firstView).ProjectionDigest,
            Project(secondView).ProjectionDigest
        );

        HistorySegmentDescriptor nextRow = Descriptor(
            "row-next",
            priorRow,
            "raw-next"
        );
        RowBuildSpecShape firstSpec = BuildSpec(
            nextRow,
            firstRecipe,
            firstView
        );
        RowBuildSpecShape secondSpec = BuildSpec(
            nextRow,
            secondRecipe,
            secondView
        );
        Assert.Equal(
            EvaluationKey(firstSpec, culprit).EvaluationKeyDigest,
            EvaluationKey(secondSpec, culprit).EvaluationKeyDigest
        );
    }

    [Fact]
    public void VisibleOrderContentOrDefinitionChangesEvaluationIdentity() {
        DefinitionShape culprit = Definition("culprit", "family-analysis-v1");
        DefinitionShape world = Definition("world", "family-analysis-v1");
        HistorySegmentDescriptor priorRow = Descriptor(
            "row-prior",
            null,
            "raw-prior"
        );
        RowBuildSpecShape orderedSpec = BuildSpec(
            priorRow,
            Recipe("ordered", culprit, world),
            null
        );
        RowViewShape ordered = View(
            orderedSpec,
            [Evaluate(orderedSpec, culprit), Evaluate(orderedSpec, world)]
        );
        RowBuildSpecShape reversedSpec = BuildSpec(
            priorRow,
            Recipe("reversed", world, culprit),
            null
        );
        RowViewShape reversed = View(
            reversedSpec,
            [Evaluate(reversedSpec, world), Evaluate(reversedSpec, culprit)]
        );
        HistorySegmentDescriptor changedRow = Descriptor(
            "row-prior-changed",
            null,
            "raw-prior-changed"
        );
        RowBuildSpecShape changedContentSpec = BuildSpec(
            changedRow,
            Recipe("ordered", culprit, world),
            null
        );
        RowViewShape changedContent = View(
            changedContentSpec,
            [
                Evaluate(changedContentSpec, culprit),
                Evaluate(changedContentSpec, world)
            ]
        );

        PriorProjectionShape orderedProjection = Project(ordered);
        Assert.NotEqual(
            orderedProjection.ProjectionDigest,
            Project(reversed).ProjectionDigest
        );
        Assert.NotEqual(
            orderedProjection.ProjectionDigest,
            Project(changedContent).ProjectionDigest
        );

        DefinitionShape culpritV2 = Definition("culprit", "family-analysis-v2");
        RowBuildSpecShape changedDefinitionSpec = BuildSpec(
            priorRow,
            Recipe("definition-v2", culpritV2, world),
            null
        );
        Assert.NotEqual(
            EvaluationKey(orderedSpec, culprit).EvaluationKeyDigest,
            EvaluationKey(changedDefinitionSpec, culpritV2)
                .EvaluationKeyDigest
        );
    }

    [Fact]
    public void SameRowEvaluationUsesOneFrozenPriorWithoutSiblingInputs() {
        DefinitionShape culprit = Definition("culprit", "family-analysis-v1");
        DefinitionShape world = Definition("world", "family-analysis-v1");
        RecipeShape recipe = Recipe("full", culprit, world);
        HistorySegmentDescriptor priorRow = Descriptor(
            "row-0",
            null,
            "raw-range-0"
        );
        RowBuildSpecShape priorSpec = BuildSpec(priorRow, recipe, null);
        RowViewShape priorView = View(
            priorSpec,
            [Evaluate(priorSpec, culprit), Evaluate(priorSpec, world)]
        );
        HistorySegmentDescriptor row = Descriptor(
            "row-1",
            priorRow,
            "raw-range-1"
        );
        RowBuildSpecShape spec = BuildSpec(row, recipe, priorView);

        CellShape culpritFirst = Evaluate(spec, culprit);
        CellShape worldSecond = Evaluate(spec, world);
        CellShape worldFirst = Evaluate(spec, world);
        CellShape culpritSecond = Evaluate(spec, culprit);

        Assert.Equal(culpritFirst, culpritSecond);
        Assert.Equal(worldFirst, worldSecond);
        Assert.Equal(spec.PriorProjectionDigest, culpritFirst.PriorProjectionDigest);
        Assert.Equal(spec.PriorProjectionDigest, worldFirst.PriorProjectionDigest);
        Assert.DoesNotContain(
            worldFirst.ContentDigest,
            culpritFirst.Content,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            culpritFirst.ContentDigest,
            worldFirst.Content,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void RowViewRejectsMismatchedMembershipIdentityAndPredecessor() {
        DefinitionShape culprit = Definition("culprit", "family-analysis-v1");
        DefinitionShape world = Definition("world", "family-analysis-v1");
        DefinitionShape worldV2 = Definition("world", "family-analysis-v2");
        RecipeShape recipe = Recipe("full", culprit, world);
        HistorySegmentDescriptor row = Descriptor(
            "row-0",
            null,
            "raw-range-0"
        );
        RowBuildSpecShape spec = BuildSpec(row, recipe, null);
        CellShape culpritCell = Evaluate(spec, culprit);
        CellShape worldCell = Evaluate(spec, world);

        Assert.Throws<InvalidDataException>(() =>
            View(spec, [culpritCell]));
        Assert.Throws<InvalidDataException>(() =>
            View(spec, [culpritCell, worldCell, worldCell]));
        Assert.Throws<InvalidDataException>(() => View(
            spec,
            [culpritCell, worldCell with {
                DefinitionDigest = worldV2.DefinitionDigest
            }]
        ));
        Assert.Throws<InvalidDataException>(() => View(
            spec,
            [culpritCell with {
                RowDescriptorDigest =
                    new HistorySegmentDescriptorDigest(new string('f', 64))
            }, worldCell]
        ));
        Assert.Throws<InvalidDataException>(() => View(
            spec,
            [
                culpritCell with { PriorProjectionDigest = "wrong-prior" },
                worldCell
            ]
        ));
        Assert.Throws<InvalidDataException>(() => View(
            spec,
            [
                culpritCell with { EvaluationKeyDigest = "wrong-key" },
                worldCell
            ]
        ));

        HistorySegmentDescriptor secondRow = Descriptor(
            "row-1",
            row,
            "raw-range-1"
        );
        Assert.Throws<InvalidDataException>(() =>
            BuildSpec(secondRow, recipe, null));
        HistorySegmentDescriptor sibling = Descriptor(
            "row-sibling",
            null,
            "raw-sibling"
        );
        RowBuildSpecShape siblingSpec = BuildSpec(sibling, recipe, null);
        RowViewShape siblingView = View(
            siblingSpec,
            [Evaluate(siblingSpec, culprit), Evaluate(siblingSpec, world)]
        );
        Assert.Throws<InvalidDataException>(() =>
            BuildSpec(secondRow, recipe, siblingView));
        Assert.Throws<InvalidDataException>(() => BuildSpec(
            row,
            RecipeForTimeline(
                new TimelineId("ffeeddccbbaa99887766554433221100"),
                "full",
                culprit,
                world
            ),
            null
        ));
        RecipeShape otherRecipe = Recipe("other-mode", culprit, world);
        RowBuildSpecShape otherSpec = BuildSpec(row, otherRecipe, null);
        RowViewShape otherView = View(
            otherSpec,
            [Evaluate(otherSpec, culprit), Evaluate(otherSpec, world)]
        );
        Assert.Throws<InvalidDataException>(() =>
            BuildSpec(secondRow, recipe, otherView));
    }

    private static DefinitionShape Definition(
        string logicalColumnId,
        string familyDigest
    ) => new(
        logicalColumnId,
        Digest("definition-v1", logicalColumnId, familyDigest)
    );

    private static RecipeShape Recipe(
        string mode,
        params DefinitionShape[] definitions
    ) => RecipeForTimeline(SkeletonTimeline, mode, definitions);

    private static RecipeShape RecipeForTimeline(
        TimelineId timelineId,
        string mode,
        params DefinitionShape[] definitions
    ) {
        if (definitions.Select(static item => item.LogicalColumnId)
            .Distinct(StringComparer.Ordinal).Count() != definitions.Length) {
            throw new InvalidDataException(
                "A skeleton BuildTarget must have unique logical columns."
            );
        }
        var targetFields = new List<string>(definitions.Length * 2);
        foreach (DefinitionShape definition in definitions) {
            targetFields.Add(definition.LogicalColumnId);
            targetFields.Add(definition.DefinitionDigest);
        }
        var target = new BuildTargetShape(
            [.. definitions],
            Digest("build-target-v1", [.. targetFields])
        );
        return new RecipeShape(
            timelineId,
            mode,
            target,
            Digest(
                "recipe-v1",
                timelineId.Value,
                mode,
                target.TargetDigest
            )
        );
    }

    private static HistorySegmentDescriptor Descriptor(
        string rowId,
        HistorySegmentDescriptor? predecessor,
        string rawRangeCommitment
    ) {
        PartitionPolicyRevision policy =
            PartitionPolicyRevision.Create(
                SkeletonTimeline,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                "atelia.walking-skeleton.history-load.v1",
                new HistoryLoadUnit(1),
                maxRawEvents: 8,
                maxRenderedBytes: 1024
            );
        EventAddress start = predecessor?.EndInclusive
            ?? Address(100);
        EventAddress end = Address(RowAddressToken(rowId));
        SJ.SessionContextAnchorSetupReferences setups = Setups();
        var point = new HistoryPartitionPoint(
            policy.TimelineId,
            policy.PolicyDigest,
            start,
            end,
            predecessor?.EndSetups ?? setups,
            setups,
            baselineCompletedUnitCount: predecessor is null ? 0 : 1,
            endCompletedUnitCount: predecessor is null ? 1 : 2,
            new HistoryLoadUnit(1),
            rawEventCount: 1,
            measuredRenderedUtf8Bytes: 1
        );
        var range = new BoundHistorySegmentRange(
            new RefId(1),
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            point.BaselineCompletedUnitCount,
            point.EndCompletedUnitCount,
            point.RawEventCount,
            Digest("raw-range-fixture-v1", rawRangeCommitment)
        );
        return HistorySegmentDescriptorFactory.Create(
            point,
            range,
            policy,
            predecessor
        );
    }

    private static ulong RowAddressToken(string rowId) => rowId switch {
        "row-0" => 101,
        "row-1" => 102,
        "row-prior" => 103,
        "row-next" => 104,
        "row-prior-changed" => 105,
        "row-sibling" => 106,
        _ => throw new ArgumentOutOfRangeException(nameof(rowId))
    };

    private static EventAddress Address(ulong packed) => new(
        SizedPtr.FromPacked(packed),
        1,
        AddressHint.None
    );

    private static SJ.SessionContextAnchorSetupReferences Setups() => new(
        new SJ.SessionContextSetupReference(
            Address(10),
            2,
            new string('a', 64)
        ),
        new SJ.SessionContextSetupReference(
            Address(11),
            1,
            new string('b', 64)
        )
    );

    private static RowBuildSpecShape BuildSpec(
        HistorySegmentDescriptor row,
        RecipeShape recipe,
        RowViewShape? previousView
    ) {
        if (row.TimelineId != recipe.TimelineId) {
            throw new InvalidDataException(
                "A skeleton RowBuildSpec must use its recipe timeline."
            );
        }
        if ((row.PreviousRowId is null) != (previousView is null)) {
            throw new InvalidDataException(
                "A skeleton RowBuildSpec predecessor does not match its row."
            );
        }
        if (previousView is not null
            && (row.TimelineId != previousView.TimelineId
                || row.PreviousRowId != previousView.RowId
                || !string.Equals(
                    recipe.RecipeDigest,
                    previousView.RecipeDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    recipe.Target.TargetDigest,
                    previousView.BuildTargetDigest,
                    StringComparison.Ordinal))) {
            throw new InvalidDataException(
                "A skeleton RowBuildSpec must use its exact predecessor view."
            );
        }
        string priorProjectionDigest = previousView is null
            ? FirstRowSentinel
            : Project(previousView).ProjectionDigest;
        var specFields = new List<string> {
            recipe.RecipeDigest,
            row.DescriptorDigest.Value,
            previousView?.ViewDigest ?? FirstRowSentinel,
            priorProjectionDigest
        };
        foreach (DefinitionShape assignment in recipe.Target.Definitions) {
            specFields.Add(assignment.LogicalColumnId);
            specFields.Add(assignment.DefinitionDigest);
        }
        return new RowBuildSpecShape(
            recipe,
            row,
            previousView?.ViewDigest,
            priorProjectionDigest,
            recipe.Target.Definitions,
            Digest("row-build-spec-v1", [.. specFields])
        );
    }

    private static EvaluationKeyShape EvaluationKey(
        RowBuildSpecShape spec,
        DefinitionShape definition
    ) {
        if (!spec.Assignments.Any(item =>
                string.Equals(
                    item.LogicalColumnId,
                    definition.LogicalColumnId,
                    StringComparison.Ordinal)
                && string.Equals(
                    item.DefinitionDigest,
                    definition.DefinitionDigest,
                    StringComparison.Ordinal))) {
            throw new InvalidDataException(
                "A skeleton evaluation must be assigned by its RowBuildSpec."
            );
        }
        return new EvaluationKeyShape(
            spec.Row.DescriptorDigest,
            definition.DefinitionDigest,
            spec.PriorProjectionDigest,
            Digest(
                "evaluation-key-v1",
                spec.Row.DescriptorDigest.Value,
                definition.DefinitionDigest,
                spec.PriorProjectionDigest
            )
        );
    }

    private static CellShape Evaluate(
        RowBuildSpecShape spec,
        DefinitionShape definition
    ) {
        EvaluationKeyShape key = EvaluationKey(spec, definition);
        return CellWithContent(
            spec.Row.DescriptorDigest,
            spec.PriorProjectionDigest,
            definition,
            key.EvaluationKeyDigest,
            $"summary:{definition.LogicalColumnId}:"
            + $"{spec.Row.RawRangeSha256}:{spec.PriorProjectionDigest}"
        );
    }

    private static CellShape CellWithContent(
        HistorySegmentDescriptorDigest rowDescriptorDigest,
        string priorProjectionDigest,
        DefinitionShape definition,
        string evaluationKeyDigest,
        string content
    ) {
        string contentDigest = Digest("content-v1", content);
        return new CellShape(
            RowDescriptorDigest: rowDescriptorDigest,
            LogicalColumnId: definition.LogicalColumnId,
            DefinitionDigest: definition.DefinitionDigest,
            PriorProjectionDigest: priorProjectionDigest,
            EvaluationKeyDigest: evaluationKeyDigest,
            Content: content,
            ContentDigest: contentDigest,
            CellDigest: Digest(
                "cell-v1",
                evaluationKeyDigest,
                definition.DefinitionDigest,
                contentDigest
            )
        );
    }

    private static RowViewShape View(
        RowBuildSpecShape spec,
        ImmutableArray<CellShape> cells
    ) {
        if (spec.Assignments.Length != cells.Length) {
            throw new InvalidDataException(
                "A skeleton RowView must exactly cover its RowBuildSpec."
            );
        }
        var fields = new List<string> {
            spec.Recipe.RecipeDigest,
            spec.Recipe.Target.TargetDigest,
            spec.Row.DescriptorDigest.Value,
            spec.PreviousViewDigest ?? FirstRowSentinel
        };
        for (int index = 0; index < cells.Length; index++) {
            DefinitionShape assignment = spec.Assignments[index];
            CellShape cell = cells[index];
            EvaluationKeyShape expectedKey = EvaluationKey(spec, assignment);
            string expectedContentDigest = Digest("content-v1", cell.Content);
            string expectedCellDigest = Digest(
                "cell-v1",
                expectedKey.EvaluationKeyDigest,
                assignment.DefinitionDigest,
                expectedContentDigest
            );
            if (spec.Row.DescriptorDigest != cell.RowDescriptorDigest
                || !string.Equals(
                    spec.PriorProjectionDigest,
                    cell.PriorProjectionDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    assignment.LogicalColumnId,
                    cell.LogicalColumnId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    assignment.DefinitionDigest,
                    cell.DefinitionDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedKey.EvaluationKeyDigest,
                    cell.EvaluationKeyDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedContentDigest,
                    cell.ContentDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedCellDigest,
                    cell.CellDigest,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "A skeleton RowView member does not match its RowBuildSpec."
                );
            }
            fields.Add(cell.LogicalColumnId);
            fields.Add(cell.DefinitionDigest);
            fields.Add(cell.CellDigest);
        }
        return new RowViewShape(
            spec.Row.TimelineId,
            spec.Row.RowId,
            spec.Recipe.RecipeDigest,
            spec.Recipe.Target.TargetDigest,
            spec.Row.DescriptorDigest,
            spec.PreviousViewDigest,
            cells,
            Digest("row-view-v1", [.. fields])
        );
    }

    private static PriorProjectionShape Project(RowViewShape view) {
        var fields = new List<string>(view.Cells.Length * 2);
        var projected = ImmutableArray.CreateBuilder<ProjectedCellShape>(
            view.Cells.Length
        );
        foreach (CellShape cell in view.Cells) {
            fields.Add(cell.LogicalColumnId);
            fields.Add(cell.ContentDigest);
            projected.Add(new ProjectedCellShape(
                cell.LogicalColumnId,
                cell.ContentDigest
            ));
        }
        return new PriorProjectionShape(
            projected.MoveToImmutable(),
            Digest("prior-projection-v1", [.. fields])
        );
    }

    private static ContextValueShape Context(RowViewShape view) => new(
        view.RowDescriptorDigest,
        [.. view.Cells.Select(static cell => new ContextContributionShape(
            cell.LogicalColumnId,
            cell.Content,
            cell.ContentDigest
        ))]
    );

    private static string Digest(string domain, params string[] fields) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(domain);
        foreach (string field in fields) {
            Append(field);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) {
            byte[] bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private sealed record DefinitionShape(
        string LogicalColumnId,
        string DefinitionDigest
    );

    private sealed record BuildTargetShape(
        ImmutableArray<DefinitionShape> Definitions,
        string TargetDigest
    );

    private sealed record RecipeShape(
        TimelineId TimelineId,
        string Mode,
        BuildTargetShape Target,
        string RecipeDigest
    );

    private sealed record RowBuildSpecShape(
        RecipeShape Recipe,
        HistorySegmentDescriptor Row,
        string? PreviousViewDigest,
        string PriorProjectionDigest,
        ImmutableArray<DefinitionShape> Assignments,
        string SpecDigest
    );

    private sealed record EvaluationKeyShape(
        HistorySegmentDescriptorDigest RowDescriptorDigest,
        string DefinitionDigest,
        string PriorProjectionDigest,
        string EvaluationKeyDigest
    );

    private sealed record CellShape(
        HistorySegmentDescriptorDigest RowDescriptorDigest,
        string LogicalColumnId,
        string DefinitionDigest,
        string PriorProjectionDigest,
        string EvaluationKeyDigest,
        string Content,
        string ContentDigest,
        string CellDigest
    );

    private sealed record RowViewShape(
        TimelineId TimelineId,
        HistoryRowId RowId,
        string RecipeDigest,
        string BuildTargetDigest,
        HistorySegmentDescriptorDigest RowDescriptorDigest,
        string? PreviousViewDigest,
        ImmutableArray<CellShape> Cells,
        string ViewDigest
    );

    private sealed record ProjectedCellShape(
        string LogicalColumnId,
        string ContentDigest
    );

    private sealed record PriorProjectionShape(
        ImmutableArray<ProjectedCellShape> Cells,
        string ProjectionDigest
    );

    private sealed record ContextContributionShape(
        string LogicalColumnId,
        string Content,
        string ContentDigest
    );

    private sealed record ContextValueShape(
        HistorySegmentDescriptorDigest RowDescriptorDigest,
        ImmutableArray<ContextContributionShape> Contributions
    );
}

using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Store.CrashHarness;

internal static class Program {
    public static int Main(string[] args) {
        if (args.Length != 3
            || args[0] is not ("cell" or "row-view" or "fulfilled" or "reset")) {
            Console.Error.WriteLine(
                "usage: <cell|row-view|fulfilled|reset> <failpoint> <repository>"
            );
            return 2;
        }
        string operation = args[0];
        string failpoint = args[1];
        string repository = Path.GetFullPath(args[2]);
        Action crash = () => Environment.FailFast(
            $"Intentional RecapGrid Store crash at {operation}/{failpoint}."
        );
        StorePersistenceTestHooks hooks = Hooks(
            operation,
            failpoint,
            crash
        );
        if (operation == "reset") {
            RecapGridStorePhysicalWitness witness =
                (RecapGridStoreMaintenance.PrepareReset(repository)
                    as RecapGridStorePrepareResetResult.Prepared)?.Witness
                ?? throw new InvalidDataException(
                    "Reset fixture has no exact witness."
                );
            _ = RecapGridStoreMaintenance.ResetForTest(
                repository,
                witness,
                StoreStorageLimits.Production,
                hooks
            );
        }
        else {
            using RecapGridStoreHandle handle =
                (RecapGridStoreFactory.OpenForTest(
                    repository,
                    StoreStorageLimits.Production,
                    hooks
                ) as RecapGridStoreOpenResult.Opened)?.Handle
                ?? throw new InvalidDataException(
                    "Store crash fixture could not be opened."
                );
            (RowBuildSpec spec, RecapCellDraft draft, FulfilledViewKey fulfilled) = Values();
            switch (operation) {
                case "cell":
                    _ = handle.Writer.PutCell(spec, draft);
                    break;
                case "row-view":
                    _ = handle.Writer.PutRowView(spec, [(handle.Reader.TryReadCell(draft.Slot) as RecapGridStoreReadResult<RecapCellArtifact>.Found)?.Value
                        ?? throw new InvalidDataException("Missing stored cell")]);
                    break;
                case "fulfilled":
                    _ = handle.Writer.PutFulfilled(
                        fulfilled,
                        (handle.Reader.ReadViewAt(spec.Coordinate.AssignmentKey) as RecapGridStoreReadResult<RecapRowView>.Found)?.Value.Id
                        ?? throw new InvalidDataException("Missing stored row")
                    );
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
        Console.Error.WriteLine("Crash failpoint was not reached.");
        return 3;
    }

    private static StorePersistenceTestHooks Hooks(
        string operation,
        string failpoint,
        Action crash
    ) => operation switch {
        "cell" => new StorePersistenceTestHooks(
            BeforeCellBegin: failpoint == "before-begin" ? crash : null,
            BeforeCellCommit: failpoint == "before-commit" ? crash : null,
            AfterCellCommit: failpoint == "after-commit" ? crash : null
        ),
        "row-view" => new StorePersistenceTestHooks(
            BeforeRowViewBegin: failpoint == "before-begin" ? crash : null,
            BeforeRowViewCommit: failpoint == "before-commit" ? crash : null,
            AfterRowViewCommit: failpoint == "after-commit" ? crash : null
        ),
        "fulfilled" => new StorePersistenceTestHooks(
            BeforeFulfilledBegin: failpoint == "before-begin" ? crash : null,
            BeforeFulfilledCommit: failpoint == "before-commit" ? crash : null,
            AfterFulfilledCommit: failpoint == "after-commit" ? crash : null
        ),
        "reset" => new StorePersistenceTestHooks(
            BeforeResetPublish: failpoint == "before-publish"
                ? _ => crash()
                : null,
            AfterResetPublish: failpoint == "after-publish"
                ? _ => crash()
                : null
        ),
        _ => throw new InvalidOperationException()
    };

    internal static (RowBuildSpec Spec, RecapCellDraft Draft, FulfilledViewKey Fulfilled) Values() {
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var rowId = new HistoryRowId(new string('c', 64));
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(timeline, rowId,
            BuildTarget.Create([new BuildTargetColumn(column, definition)]));
        var slot = new CellSlot(recipe.Digest, rowId, column);
        RowBuildSpec spec = RowBuildSpec.CreateFull(recipe, new RowViewCoordinate(
            new RefId(1), timeline, rowId, new HistorySegmentDescriptorDigest(rowId.Value), recipe.Digest,
            recipe.Target.Digest, null, null, bootstrapCompleted: true), [new RowBuildAssignment.Evaluate(slot)]);
        var head = new TimelineHeadRef(timeline, new RefId(1), null, new string('d', 64), null,
            0, HistoryTimelineSelectedPath.EmptyDigest, generation: 1);
        return (spec, RecapCellDraft.Create(slot, definition, RecapCellOutcome.Updated,
            "crash fixture answer", RecapGridLimits.MaximumContentUtf8Bytes),
            FulfilledViewKey.Create(head.RefId, head, spec.HistorySegmentDigest, recipe));
    }
}

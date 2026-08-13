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
            (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view,
                FulfilledViewKey fulfilled) = Values();
            switch (operation) {
                case "cell":
                    _ = handle.Writer.PutCell(cell);
                    break;
                case "row-view":
                    _ = handle.Writer.PutRowView(spec, view);
                    break;
                case "fulfilled":
                    _ = handle.Writer.PutFulfilled(
                        fulfilled,
                        view.Digest
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

    internal static (
        RowBuildSpec Spec,
        RecapCellArtifact Cell,
        RecapRowView View,
        FulfilledViewKey Fulfilled
    ) Values() {
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var rowId = new HistoryRowId(new string('c', 64));
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline,
            rowId,
            BuildTarget.Create([new BuildTargetColumn(column, definition)])
        );
        var descriptor = new HistorySegmentDescriptorDigest(new string('b', 64));
        EvaluationKey evaluation = EvaluationKey.Create(
            descriptor,
            definition,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact cell = RecapCellArtifact.Create(
            column,
            definition,
            evaluation,
            RecapCellOutcome.Updated,
            "crash fixture answer",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            new RowViewCoordinate(
                new RefId(1),
                timeline,
                rowId,
                descriptor,
                recipe.Digest,
                recipe.Target.Digest,
                previousHistoryRowId: null,
                previousViewDigest: null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(column, evaluation)]
        );
        RecapRowView view = RecapRowView.Create(spec, [cell]);
        var head = new TimelineHeadRef(
            timeline,
            new RefId(1),
            null,
            new string('d', 64),
            null,
            generation: 1
        );
        return (
            spec,
            cell,
            view,
            FulfilledViewKey.Create(
                head.RefId,
                head,
                view.RowDescriptorDigest,
                recipe
            )
        );
    }
}

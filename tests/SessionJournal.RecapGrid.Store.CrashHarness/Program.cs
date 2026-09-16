using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Store.CrashHarness;

internal static class Program {
    public static int Main(string[] args) {
        if (args.Length < 3
            || args[0] is not ("cell" or "row-view" or "fulfilled"
                or "reset" or "upgrade-v4" or "restore-v4")
            || !HasValidArgumentCount(args)) {
            Console.Error.WriteLine(
                "usage: <cell|row-view|fulfilled|reset|upgrade-v4> <failpoint> <repository> [proof-bundle]\n"
                + "   or: restore-v4 <failpoint> <repository> <backup> <active-length> <active-sha256> <backup-length> <backup-sha256> [proof-bundle]"
            );
            return 2;
        }
        string operation = args[0];
        string failpoint = args[1];
        string repository = Path.GetFullPath(args[2]);
        Action crash = () => Environment.FailFast(
            $"Intentional RecapGrid Store crash at {operation}/{failpoint}."
        );
        StorePersistenceTestHooks hooks = operation is "upgrade-v4"
            or "restore-v4"
            ? StorePersistenceTestHooks.None
            : Hooks(operation, failpoint, crash);
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
        else if (operation == "upgrade-v4") {
            IReadOnlyList<RowWork> proofs = args.Length == 4
                ? ReadProofBundle(repository, args[3])
                : [];
            _ = RecapGridStoreMaintenance.UpgradeV4ForTest(
                repository,
                apply: true,
                () => proofs,
                UpgradeHooks(failpoint, crash)
            );
        }
        else if (operation == "restore-v4") {
            IReadOnlyList<RowWork> proofs = args.Length == 9
                ? ReadProofBundle(repository, args[8])
                : [];
            var activeWitness = new RecapGridStorePhysicalWitness(
                ParseLength(args[4]), args[5]);
            var backupWitness = new RecapGridStorePhysicalWitness(
                ParseLength(args[6]), args[7]);
            _ = RecapGridStoreMaintenance.RestoreV4ForTest(
                repository,
                Path.GetFullPath(args[3]),
                activeWitness,
                backupWitness,
                () => proofs,
                RestoreHooks(failpoint, crash));
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
            if (handle.Writer.PutRowWork(spec.Work!) is not (
                RecapGridRowWorkPutResult.Inserted or RecapGridRowWorkPutResult.AlreadyPresent)) {
                throw new InvalidDataException("RowWork could not be persisted.");
            }
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

    private static bool HasValidArgumentCount(string[] args)
        => args[0] switch {
            "upgrade-v4" => args.Length is 3 or 4,
            "restore-v4" => args.Length is 8 or 9,
            _ => args.Length == 3
        };

    private static long ParseLength(string value)
        => long.Parse(value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<RowWork> ReadProofBundle(
        string repository,
        string proofPath
    ) {
        string canonicalRepository = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repository));
        string canonicalProof = Path.GetFullPath(proofPath);
        if (canonicalProof.StartsWith(
                canonicalRepository + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The upgrade proof bundle must be outside the repository.");
        }
        string[] encoded = JsonSerializer.Deserialize<string[]>(
            File.ReadAllBytes(canonicalProof))
            ?? throw new InvalidDataException(
                "The upgrade proof bundle must be a JSON string array.");
        return encoded.Select(static value =>
            RowWork.DecodeCanonical(Convert.FromBase64String(value))).ToArray();
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

    private static StoreUpgradeTestHooks UpgradeHooks(
        string failpoint,
        Action crash
    ) => new(
        AfterTempVerified: failpoint == "after-temp-verified" ? crash : null,
        AfterBackupDurable: failpoint == "after-backup-durable" ? crash : null,
        AfterReplaceBeforeDirectoryFsync: failpoint == "after-replace-before-directory-fsync" ? crash : null,
        AfterDirectoryFsyncBeforeVerify: failpoint == "after-directory-fsync-before-verify" ? crash : null,
        AfterVerify: failpoint == "after-verify" ? crash : null
    );

    private static StoreRestoreTestHooks RestoreHooks(
        string failpoint,
        Action crash
    ) => new(
        AfterTempVerified: failpoint == "after-temp-verified" ? crash : null,
        AfterReplaceBeforeDirectoryFsync:
            failpoint == "after-replace-before-directory-fsync"
                ? crash
                : null,
        AfterDirectoryFsyncBeforeVerify:
            failpoint == "after-directory-fsync-before-verify"
                ? crash
                : null,
        AfterVerify: failpoint == "after-verify" ? crash : null
    );

    internal static (RowBuildSpec Spec, RecapCellDraft Draft, FulfilledViewKey Fulfilled) Values() {
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var rowId = new HistoryRowId(new string('c', 64));
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(timeline, rowId,
            BuildTarget.Create([new BuildTargetColumn(column, definition)]));
        var key = new RowWorkKey(new RefId(1), timeline, recipe.Digest, rowId);
        var work = new RowWork(key, recipe.Target, null, null,
            [new RowWorkAssignment(column, null)]);
        var slot = new CellSlot(recipe.Digest, rowId, work.WorkId, column);
        RowBuildSpec spec = RowBuildSpec.CreateFull(recipe, new RowViewCoordinate(
            new RefId(1), timeline, rowId, recipe.Digest,
            recipe.Target.Digest, null, null, bootstrapCompleted: true), [new RowBuildAssignment.Evaluate(slot)], work);
        var head = new TimelineHeadRef(timeline, new RefId(1), null, new string('d', 64), null,
            0, HistoryTimelineSelectedPath.EmptyDigest, generation: 1);
        return (spec, RecapCellDraft.Create(slot, definition, RecapCellOutcome.Updated,
            "crash fixture answer", RecapGridLimits.MaximumContentUtf8Bytes),
            FulfilledViewKey.Create(head.RefId, head, spec.HistoryRowId, recipe));
    }
}

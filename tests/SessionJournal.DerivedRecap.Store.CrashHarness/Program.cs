using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace SessionJournal.DerivedRecap.Store.CrashHarness;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "usage: <final|publish|reset> <failpoint> <repository>"
            );
            return 2;
        }

        string operation = args[0];
        string failpoint = args[1];
        string repositoryPath = Path.GetFullPath(args[2]);
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(repositoryPath);
        Action crash = () => Environment.FailFast(
            $"Intentional DerivedRecap v8 crash at '{failpoint}'."
        );
        var hooks = new RecapEpochStoreTestHooks(
            BeforeRawHeadRecheck:
                failpoint == "raw-head-recheck" ? crash : null,
            BeforeBuildingPromotion:
                failpoint == "building-promotion" ? crash : null,
            BeforeFinalReplace:
                failpoint == "final-before-replace" ? crash : null,
            AfterFinalReplace:
                failpoint == "final-after-replace" ? crash : null,
            BeforePublicationInstall:
                failpoint == "publication-install" ? crash : null,
            BeforePublishedPromotion:
                failpoint == "published-promotion" ? crash : null,
            AfterResetQuarantineRename:
                failpoint == "reset-quarantine-renamed" ? crash : null
        );
        DerivedRecapEpochStore store =
            DerivedRecapEpochStore.OpenForTest(
                repositoryPath,
                engine.BranchRefId,
                limits: null,
                hooks
            );

        switch (operation) {
            case "final":
                await WriteFirstPendingFinalAsync(store);
                break;
            case "publish":
                await PublishBuildingAsync(engine, store);
                break;
            case "reset":
                await store.ResetAsync();
                break;
            default:
                Console.Error.WriteLine($"unknown operation '{operation}'");
                return 2;
        }

        Console.Error.WriteLine($"failpoint '{failpoint}' was not reached");
        return 3;
    }

    private static async ValueTask WriteFirstPendingFinalAsync(
        DerivedRecapEpochStore store
    ) {
        RecapEpochStoreSnapshot snapshot = await ReadBuildingAsync(store);
        RecapEpochBlockInspection block = snapshot.Blocks
            .FirstOrDefault(static item => item.WriteAuthority is not null)
            ?? throw new InvalidDataException(
                "Crash fixture Building has no writable final slot."
            );
        WriteRecapEpochFinalResult result = await store.WriteFinalAsync(
            block.WriteAuthority!,
            DerivedRecapV8Codec.CreateFinalBlock(
                snapshot.Manifest,
                block.Definition,
                "crash-harness-final"
            )
        );
        if (result is not WriteRecapEpochFinalResult.Installed
            and not WriteRecapEpochFinalResult.AlreadyHealthy) {
            throw new InvalidDataException(
                $"Crash fixture final failed: {result}."
            );
        }
    }

    private static async ValueTask PublishBuildingAsync(
        SessionJournalEngine engine,
        DerivedRecapEpochStore store
    ) {
        RecapEpochStoreSnapshot snapshot = await ReadBuildingAsync(store);
        EventAddress capturedHead =
            engine.ReadCurrentLineageHeaders().CapturedHead;
        PublishRecapEpochResult result = await store.PublishBuildingAsync(
            snapshot.Descriptor,
            capturedHead,
            () => engine.ReadCurrentLineageHeaders().CapturedHead
        );
        if (result is not PublishRecapEpochResult.Published
            and not PublishRecapEpochResult.AlreadyPublished) {
            throw new InvalidDataException(
                $"Crash fixture publish failed: {result}."
            );
        }
    }

    private static async ValueTask<RecapEpochStoreSnapshot>
        ReadBuildingAsync(DerivedRecapEpochStore store) {
        return await store.SelectBuildingAsync()
            is RecapEpochBuildingSelectionResult.Selected selected
                ? selected.Snapshot
                : throw new InvalidDataException(
                    "Crash fixture Building is unavailable."
                );
    }
}

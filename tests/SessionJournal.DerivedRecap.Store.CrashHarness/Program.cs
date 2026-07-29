using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace SessionJournal.DerivedRecap.Store.CrashHarness;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "usage: <create|publish|reset> <failpoint> <repository>"
            );
            return 2;
        }

        string operation = args[0];
        string failpoint = args[1];
        string repositoryPath = Path.GetFullPath(args[2]);
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(repositoryPath);
        Action crash = () => Environment.FailFast(
            $"Intentional DerivedRecap crash at '{failpoint}'."
        );
        var hooks = new RecapStoreTestHooks(
            AfterPublicationSealed:
                failpoint == "publication-sealed" ? crash : null,
            BeforePublishedPromotion:
                failpoint == "promotion-before" ? crash : null,
            AfterPublishedPromotion:
                failpoint == "promotion-after" ? crash : null,
            BeforeRootCommit:
                failpoint == "root-before-commit" ? crash : null,
            AfterRootCommit:
                failpoint == "root-after-commit" ? crash : null,
            AfterResetQuarantine:
                failpoint == "reset-after-quarantine" ? crash : null,
            AfterResetNewRootCommit:
                failpoint == "reset-after-new-root-commit"
                    ? crash
                    : null,
            BeforePublicationSealInstall:
                failpoint == "publication-before-seal" ? crash : null
        );
        DerivedRecapStore store = DerivedRecapStore.OpenForTest(
            repositoryPath,
            engine.BranchRefId,
            hooks
        );

        switch (operation) {
            case "create":
                await store.CreateAsync();
                break;
            case "publish":
                var publisher = new DerivedRecapPublisher(store, engine);
                await publisher.PublishAsync(
                    engine.ReadCurrentLineageHeaders().CapturedHead
                );
                break;
            case "reset":
                await store.ResetAsync();
                break;
            default:
                Console.Error.WriteLine(
                    $"unknown operation '{operation}'"
                );
                return 2;
        }

        Console.Error.WriteLine(
            $"failpoint '{failpoint}' was not reached"
        );
        return 3;
    }
}

using Atelia.MemoPod;

namespace Atelia.MemoPod.CrashHarness;

internal static class Program {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "77777777777777777777777777777777"
    );

    public static int Main(string[] args) {
        if (args.Length != 3
            || args[0] is not ("create" or "replace" or "correction")
            || args[1] is not (
                "before-publish"
                or "after-install-before-fsync"
                or "after-fsync")) {
            Console.Error.WriteLine(
                "usage: <create|replace|correction> <before-publish|after-install-before-fsync|after-fsync> <root>"
            );
            return 2;
        }

        string operation = args[0];
        string failpoint = args[1];
        string root = Path.GetFullPath(args[2]);
        Action crash = () => Environment.FailFast(
            $"Intentional MemoPod crash at {operation}/{failpoint}."
        );
        var hooks = new MemoPodPublisherTestHooks(
            BeforePublish: failpoint == "before-publish"
                ? _ => crash()
                : null,
            AfterInstallBeforeDirectoryFsync:
                failpoint == "after-install-before-fsync"
                    ? _ => crash()
                    : null,
            AfterDirectoryFsync: failpoint == "after-fsync"
                ? _ => crash()
                : null
        );
        if (operation == "correction") {
            MemoPod pod = MemoPod.OpenForTesting(
                root,
                PodId,
                new MemoPodLifecycleTestHooks(PublisherHooks: hooks)
            );
            pod.ResumeEditing();
            pod.Remove(MemoId.FromOrdinal(1));
            MemoId newId = pod.Append("new");
            if (newId != MemoId.FromOrdinal(2)) {
                throw new InvalidOperationException(
                    "Correction crash fixture did not allocate m1:00000002."
                );
            }
            pod.FreezeAsync().GetAwaiter().GetResult();
        }
        else {
            MemoPodPublishMode mode = operation == "create"
                ? MemoPodPublishMode.CreateNew
                : MemoPodPublishMode.ReplaceExisting;
            _ = MemoPodDocumentPublisher.Publish(
                root,
                NewDocument(),
                mode,
                hooks
            );
        }

        Console.Error.WriteLine("MemoPod crash failpoint was not reached.");
        return 3;
    }

    private static MemoPodDocument NewDocument()
        => new(
            PodId,
            "crash fixture",
            2,
            [new Memo(MemoId.FromOrdinal(1), "new")]
        );
}

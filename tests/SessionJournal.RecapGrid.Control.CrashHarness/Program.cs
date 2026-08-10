using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.RecapGrid.Control.CrashHarness;

internal static class Program {
    public static int Main(string[] args) {
        if (args.Length != 2
            || args[0] is not ("before-state-publish"
                or "after-state-publish")) {
            Console.Error.WriteLine(
                "usage: <before-state-publish|after-state-publish> <repository>"
            );
            return 2;
        }
        string failpoint = args[0];
        string repositoryPath = Path.GetFullPath(args[1]);
        Action crash = () => Environment.FailFast(
            $"Intentional RecapGrid Control crash at '{failpoint}'."
        );
        var hooks = new ControlPersistenceTestHooks(
            BeforeStatePublish: failpoint == "before-state-publish"
                ? crash
                : null,
            AfterStatePublish: failpoint == "after-state-publish"
                ? _ => crash()
                : null
        );
        FamilyDefinition family = CrashFamily();
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterFamily,
            [family.Digest],
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        using SessionJournalEngine journal = SessionJournalEngine.Open(
            repositoryPath
        );
        using RecapGridControlHandle handle =
            (RecapGridControlFactory.OpenForTest(
                repositoryPath,
                journal.BranchRefId,
                admission,
                hooks
            ) as RecapGridControlOpenResult.Opened)?.Handle
            ?? throw new InvalidDataException(
                "The Control crash fixture could not be opened."
            );
        ControlHeadRef expected = (handle.Reader.ReadSnapshot()
            as RecapGridControlSnapshotResult.Available)?.Snapshot.Head
            ?? throw new InvalidDataException(
                "The Control crash fixture head is unavailable."
            );
        _ = handle.Coordinator.PutFamilyDefinition(expected, family);
        Console.Error.WriteLine("Control crash failpoint was not reached.");
        return 3;
    }

    internal static FamilyDefinition CrashFamily() => FamilyDefinition.Create(
        "Crash fixture family.",
        [new FamilyToolDefinition(
            "submit",
            "Submit.",
            new FamilyObjectInputSchema([
                new FamilyToolProperty(
                    "content",
                    new FamilyScalarInputSchema(FamilyScalarType.String),
                    required: true
                )
            ])
        )],
        new FamilyOutputProtocol(
            "output-v1",
            "submit",
            FamilyToolChoice.Required,
            allowParallel: false
        ),
        new FamilyInputRenderingProtocol(
            "input-v1",
            "prior-v1",
            "history-v1"
        )
    );
}

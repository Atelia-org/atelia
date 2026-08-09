using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal sealed class RecapStoreFixture : IDisposable {
    private RecapStoreFixture(
        string path,
        SessionJournalEngine engine
    ) {
        Path = path;
        Engine = engine;
    }

    public string Path { get; }
    public SessionJournalEngine Engine { get; private set; }
    public SessionJournalReadView ReadView => Engine.ReadView;

    public static ValueTask<RecapStoreFixture> CreateAsync(
        int historyPairs = 3
    ) {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-derived-recap-store-tests",
            Guid.NewGuid().ToString("N")
        );
        SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        for (int index = 0; index < historyPairs; index++) {
            engine.AppendObservation($"observation {index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {index}")
                ]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        return ValueTask.FromResult(new RecapStoreFixture(path, engine));
    }

    public SessionCurrentLineageSnapshot RawLineage()
        => Engine.ReadCurrentLineageHeaders();

    public SessionContextAnchorSetupReferences Setups(
        EventAddress address
    ) => Engine.ResolveContextAnchorSetupReferences(address);

    public EventAddress AppendPair(string suffix) {
        Engine.AppendObservation($"observation {suffix}");
        return Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text($"answer {suffix}")
            ]),
            new CompletionDescriptor("import", "v1", "model-a")
        );
    }

    public void ReopenEngine() {
        Engine.Dispose();
        Engine = SessionJournalEngine.Open(Path);
    }

    public void CloseEngine() => Engine.Dispose();

    public void Dispose() {
        Engine.Dispose();
        try {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch {
        }
    }
}

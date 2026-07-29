using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal sealed class RecapStoreFixture : IDisposable {
    private RecapStoreFixture(
        string path,
        SessionJournalEngine engine,
        DerivedRecapStore store
    ) {
        Path = path;
        Engine = engine;
        Store = store;
        Publisher = new DerivedRecapPublisher(store, engine);
    }

    public string Path { get; }
    public SessionJournalEngine Engine { get; private set; }
    public DerivedRecapStore Store { get; }
    public DerivedRecapPublisher Publisher { get; private set; }

    public static async ValueTask<RecapStoreFixture> CreateAsync(
        RecapStoreTestHooks? hooks = null,
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
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
        }
        DerivedRecapStore store = hooks is null
            ? DerivedRecapStore.Open(path, engine.BranchRefId)
            : DerivedRecapStore.OpenForTest(
                path,
                engine.BranchRefId,
                hooks
            );
        await store.CreateAsync();
        return new RecapStoreFixture(path, engine, store);
    }

    public SessionCurrentLineageSnapshot Lineage()
        => Engine.ReadCurrentLineageHeaders();

    public async ValueTask<PublishedRecapDescriptor> PublishAsync(
        EventAddress anchor,
        EventAddress replayStart,
        string blockId = "roleplay.self",
        string content = "recap"
    ) {
        RecapBlockPlan plan = CreateMaintainPlan(
            anchor,
            replayStart,
            blockId
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                Engine.BranchRefId,
                anchor,
                [plan]
            );
        await Store.CreateBuildingAsync(manifest, []);
        await Store.WriteFinalBlockAsync(
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, content)
        );
        return await Publisher.PublishAsync(anchor);
    }

    public RecapBlockPlan CreateMaintainPlan(
        EventAddress anchor,
        EventAddress replayStart,
        string blockId = "roleplay.self"
    ) => new MaintainRecapBlockPlan(
        new RecapBlockId(blockId),
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            blockId
        ),
        "roleplay.autobiographical",
        new EmptyRecapMaintainSource(replayStart),
        [anchor],
        EmptyRecapPriorContext.Instance
    );

    public EventAddress AppendPair(string suffix) {
        Engine.AppendObservation($"observation {suffix}");
        return Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text($"answer {suffix}")
            ]),
            new CompletionDescriptor(
                "import",
                "v1",
                "model-a"
            )
        );
    }

    public void ReopenEngine() {
        Engine.Dispose();
        Engine = SessionJournalEngine.Open(Path);
        Publisher = new DerivedRecapPublisher(Store, Engine);
    }

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

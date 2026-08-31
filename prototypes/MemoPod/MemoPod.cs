using System.Collections.Immutable;

namespace Atelia.MemoPod;

public sealed partial class MemoPod {
    private readonly string _rootPath;
    private readonly MemoPodWorkingAggregate _working;
    private readonly MemoPodLifecycleTestHooks _testHooks;

    private MemoPodPhase _phase;
    private MemoPodPublishMode _nextPublishMode;
    private MemoPodFrozenPrompt? _frozenPrompt;
    private bool _dirty;
    private bool _invalidated;

    private MemoPod(
        string rootPath,
        MemoPodWorkingAggregate working,
        MemoPodPhase phase,
        MemoPodPublishMode nextPublishMode,
        bool dirty,
        MemoPodFrozenPrompt? frozenPrompt,
        MemoPodLifecycleTestHooks testHooks
    ) {
        _rootPath = rootPath;
        _working = working;
        _phase = phase;
        _nextPublishMode = nextPublishMode;
        _dirty = dirty;
        _frozenPrompt = frozenPrompt;
        _testHooks = testHooks;
    }

    public MemoPodId PodId {
        get {
            ThrowIfInvalidated();
            return _working.PodId;
        }
    }

    public string Topic {
        get {
            ThrowIfInvalidated();
            return _working.Topic;
        }
    }

    public MemoPodPhase Phase {
        get {
            ThrowIfInvalidated();
            return _phase;
        }
    }

    public static MemoPod Create(
        string rootPath,
        MemoPodId podId,
        string topic
    ) => CreateCore(
        rootPath,
        podId,
        topic,
        MemoPodLifecycleTestHooks.None
    );

    public static MemoPod Open(string rootPath, MemoPodId podId)
        => OpenCore(rootPath, podId, MemoPodLifecycleTestHooks.None);

    public MemoId Append(
        string exactText,
        string? title = null,
        string? gist = null,
        string? summary = null
    ) {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Editable, nameof(Append));

        MemoId id = _working.Append(exactText, title, gist, summary);
        _dirty = true;
        return id;
    }

    public void Remove(MemoId id) {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Editable, nameof(Remove));

        _working.Remove(id);
        _dirty = true;
    }

    public void UpdateDerivedInfo(
        MemoId id,
        string? title,
        string? gist,
        string? summary
    ) {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Editable, nameof(UpdateDerivedInfo));

        if (_working.UpdateDerivedInfo(id, title, gist, summary)) {
            _dirty = true;
        }
    }

    public Memo Get(MemoId id) {
        ThrowIfInvalidated();
        return _working.Get(id);
    }

    public bool TryGet(MemoId id, out Memo? memo) {
        ThrowIfInvalidated();
        return _working.TryGet(id, out memo);
    }

    public ImmutableArray<Memo> List() {
        ThrowIfInvalidated();
        return _working.List();
    }

    internal MemoPodFrozenPrompt FrozenPrompt {
        get {
            ThrowIfInvalidated();
            RequirePhase(MemoPodPhase.Frozen, nameof(FrozenPrompt));
            return _frozenPrompt
                ?? throw new InvalidOperationException(
                    "The Frozen MemoPod has no cached prompt."
                );
        }
    }

    internal static MemoPod CreateForTesting(
        string rootPath,
        MemoPodId podId,
        string topic,
        MemoPodLifecycleTestHooks testHooks
    ) {
        ArgumentNullException.ThrowIfNull(testHooks);
        return CreateCore(rootPath, podId, topic, testHooks);
    }

    internal static MemoPod OpenForTesting(
        string rootPath,
        MemoPodId podId,
        MemoPodLifecycleTestHooks testHooks
    ) {
        ArgumentNullException.ThrowIfNull(testHooks);
        return OpenCore(rootPath, podId, testHooks);
    }

    private static MemoPod CreateCore(
        string rootPath,
        MemoPodId podId,
        string topic,
        MemoPodLifecycleTestHooks testHooks
    ) {
        MemoPodStoreLayout.RequireLinux();
        MemoPodWorkingAggregate working =
            MemoPodWorkingAggregate.CreateNew(podId, topic);
        MemoPodStorePaths paths;
        try {
            paths = MemoPodStoreLayout.Resolve(rootPath, podId);
            MemoPodStoreLayout.RequireExistingRoot(paths);
        }
        catch (Exception exception)
            when (MemoPodPersistenceErrors.CanMap(exception)) {
            throw MemoPodPersistenceErrors.FromException(
                exception,
                "MemoPod could not validate its caller root."
            );
        }

        return new MemoPod(
            paths.RootPath,
            working,
            MemoPodPhase.Editable,
            MemoPodPublishMode.CreateNew,
            dirty: true,
            frozenPrompt: null,
            testHooks
        );
    }

    private static MemoPod OpenCore(
        string rootPath,
        MemoPodId podId,
        MemoPodLifecycleTestHooks testHooks
    ) {
        MemoPodStoreLayout.RequireLinux();
        MemoPodSyntax.RequirePodId(podId, nameof(podId));

        MemoPodStorePaths paths;
        MemoPodDocument document;
        try {
            paths = MemoPodStoreLayout.Resolve(rootPath, podId);
            document = MemoPodDocumentStore.Read(paths.RootPath, podId);
        }
        catch (Exception exception)
            when (MemoPodPersistenceErrors.CanMap(exception)) {
            throw MemoPodPersistenceErrors.FromException(
                exception,
                "MemoPod could not open its durable document."
            );
        }

        testHooks.BeforeRender?.Invoke(document);
        MemoPodFrozenPrompt prompt = MemoPodPromptRenderer.Render(document);
        return new MemoPod(
            paths.RootPath,
            MemoPodWorkingAggregate.FromDocument(document),
            MemoPodPhase.Frozen,
            MemoPodPublishMode.ReplaceExisting,
            dirty: false,
            prompt,
            testHooks
        );
    }

    private void ThrowIfInvalidated() {
        if (_invalidated) {
            throw new MemoPodInvalidatedException();
        }
    }

    private void RequirePhase(MemoPodPhase required, string operation) {
        if (_phase != required) {
            throw new InvalidOperationException(
                $"MemoPod operation '{operation}' requires phase {required}, but the current phase is {_phase}."
            );
        }
    }
}

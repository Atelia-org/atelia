using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteDefaultPodReconcilerTests {
    private static readonly CompletionDescriptor Invocation = new(
        "fixture",
        "fixture-v1",
        "model-a"
    );

    [Fact]
    public void CodeOwnedEmptyIdentityMatchesActualMemoPodCandidate() {
        using var paths = new FixturePaths();
        Directory.CreateDirectory(paths.StatePath);
        var pod = global::Atelia.MemoPod.MemoPod.Create(
            paths.StatePath,
            CharacterNoteDefaultPodV1.PodId,
            CharacterNoteDefaultPodV1.Topic
        );

        Assert.Equal(
            CharacterNoteDefaultPodV1.EmptyStateIdentity,
            pod.ComputeStateIdentity()
        );
        Assert.Equal(
            "00000000000000000000000000000001",
            CharacterNoteDefaultPodV1.PodId.Value
        );
    }

    [Fact]
    public async Task CreateAndOpenProvisionExactEmptyDefaultPod() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var extractor = new RecordingExtractor(_ => []);

        using (CharacterNoteDefaultPodReconciler created =
            await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                paths.StatePath,
                Owner(engine),
                Baseline(engine),
                extractor
            )) {
            CharacterMemoryStatusSnapshot status =
                created.ReadStatusSnapshot();
            Assert.Equal(CharacterMemoryStoreState.Ready,
                status.StoreState);
            Assert.Equal(CharacterNoteDefaultPodV1.EmptyStateIdentity,
                status.SettledDefaultPodStateIdentity);
            Assert.IsType<CharacterNotePendingReconcileResult.NoPending>(
                await created.ReconcilePendingAsync()
            );
        }

        using CharacterNoteDefaultPodReconciler reopened =
            await CharacterNoteDefaultPodReconciler.OpenExistingAsync(
                paths.StatePath,
                Owner(engine),
                extractor
            );
        Assert.Equal(CharacterMemoryStoreState.Ready,
            reopened.ReadStatusSnapshot().StoreState);
        Assert.Equal(0, extractor.CallCount);
    }

    [Fact]
    public async Task ProvisioningRecoversAbsentInstalledAndRejectsOther() {
        using var absentPaths = new FixturePaths();
        using SessionJournalEngine absentEngine = CreateEngine(
            absentPaths.SessionPath
        );
        CharacterMemorySqliteStore absentStore =
            CharacterMemorySqliteStore.CreateNew(
                absentPaths.StatePath,
                Owner(absentEngine),
                Baseline(absentEngine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        using (CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                absentStore,
                new RecordingExtractor(_ => [])
            )) {
            Assert.Equal(CharacterMemoryStoreState.Ready,
                reconciler.ReadStatusSnapshot().StoreState);
        }

        using var installedPaths = new FixturePaths();
        using SessionJournalEngine installedEngine = CreateEngine(
            installedPaths.SessionPath
        );
        CharacterMemorySqliteStore installedStore =
            CharacterMemorySqliteStore.CreateNew(
                installedPaths.StatePath,
                Owner(installedEngine),
                Baseline(installedEngine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        var installed = global::Atelia.MemoPod.MemoPod.Create(
            installedPaths.StatePath,
            CharacterNoteDefaultPodV1.PodId,
            CharacterNoteDefaultPodV1.Topic
        );
        await installed.FreezeAsync();
        using (CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                installedStore,
                new RecordingExtractor(_ => [])
            )) {
            Assert.Equal(CharacterMemoryStoreState.Ready,
                reconciler.ReadStatusSnapshot().StoreState);
        }

        using var otherPaths = new FixturePaths();
        using SessionJournalEngine otherEngine = CreateEngine(
            otherPaths.SessionPath
        );
        CharacterMemorySqliteStore otherStore =
            CharacterMemorySqliteStore.CreateNew(
                otherPaths.StatePath,
                Owner(otherEngine),
                Baseline(otherEngine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        var other = global::Atelia.MemoPod.MemoPod.Create(
            otherPaths.StatePath,
            CharacterNoteDefaultPodV1.PodId,
            CharacterNoteDefaultPodV1.Topic
        );
        _ = other.Append("unexpected");
        await other.FreezeAsync();
        using CharacterNoteDefaultPodReconciler quarantined =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                otherStore,
                new RecordingExtractor(_ => [])
            );
        Assert.Equal(CharacterMemoryStoreState.Quarantined,
            quarantined.ReadStatusSnapshot().StoreState);
        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.ProvisionStateMismatch,
            quarantined.ReadStatusSnapshot().QuarantineCode
        );
    }

    [Fact]
    public async Task ZeroCaptureIsDurableAndExistingCaptureSkipsExtractor() {
        using var fixture = await RuntimeFixture.CreateAsync(_ => []);
        (_, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("no note request");

        CharacterNoteDefaultPodReconcileResult first =
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            );
        CharacterNoteDefaultPodReconcileResult second =
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            );

        Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.ZeroCaptured
        >(first);
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.ZeroCaptured>(
            second
        );
        Assert.Equal(1, fixture.Extractor.CallCount);
    }

    [Fact]
    public async Task OrderedBatchPlansThenPublishesOnceAndBecomesAlreadyApplied() {
        var access = FakePodAccess.ReadyEmpty();
        using var fixture = await RuntimeFixture.CreateWithAccessAsync(
            _ => [
                new CharacterNoteIntent("first exact", "submitted first"),
                new CharacterNoteIntent("second exact", "submitted second")
            ],
            access
        );
        (EventAddress action, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget(
                "first exact submitted first second exact submitted second"
            );

        var applied = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        ));

        Assert.Equal(["m1:00000001", "m1:00000002"],
            applied.Memos.Select(static memo => memo.MemoId.Value));
        Assert.Equal(["first exact", "second exact"],
            applied.Memos.Select(static memo => memo.ExactText));
        Assert.Equal(2, access.AppendCount);
        Assert.Equal(1, access.FreezeCount);
        Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AlreadyApplied
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        ));
        Assert.Equal(1, fixture.Extractor.CallCount);
        Assert.Equal(1, access.FreezeCount);
        Assert.Null(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveSourceAction);
        Assert.Equal(action, applied.SourceAction);
    }

    [Fact]
    public async Task PlannedBaseReplaysIdsAndTargetWithoutExtractor() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        CharacterMemorySqliteStore store = ReadyStore(
            paths.StatePath,
            engine
        );
        EventAddress source = AppendAction(engine, "stored batch");
        CharacterMemoryCaptureSnapshot capture = store.CaptureNew(new(
            EventAddressTextCodec.Format(source),
            Sha256("stored batch"),
            Encoding.UTF8.GetByteCount("stored batch"),
            "stored-contract",
            ["first", "second"]
        )).Capture!;
        FakePodPreview preview = access.Preview(["first", "second"]);
        _ = store.PlanApply(new(
            capture.SourceActionAddress,
            capture.ExtractionCommitment,
            CharacterNoteDefaultPodV1.EmptyStateIdentity,
            preview.TargetIdentity,
            preview.MemoIds
        ));
        access.ResetCounters();
        var extractor = new RecordingExtractor(_ => throw new Xunit.Sdk
            .XunitException("Stored Planned capture must not rerun extractor."));
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                extractor,
                access
            );

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await reconciler.ReconcilePendingAsync());
        var applied = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(pending.Result);

        Assert.Equal(source, applied.SourceAction);
        Assert.Equal(preview.MemoIds,
            applied.Memos.Select(static memo => memo.MemoId.Value));
        Assert.Equal(2, access.AppendCount);
        Assert.Equal(1, access.FreezeCount);
        Assert.Equal(0, extractor.CallCount);
    }

    [Fact]
    public async Task PlannedInstalledTargetConfirmsBeforeSettlement() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        CharacterMemorySqliteStore store = ReadyStore(
            paths.StatePath,
            engine
        );
        EventAddress source = AppendAction(engine, "planned target");
        CharacterMemoryCaptureSnapshot capture = store.CaptureNew(new(
            EventAddressTextCodec.Format(source),
            Sha256("planned target"),
            Encoding.UTF8.GetByteCount("planned target"),
            "stored-contract",
            ["saved"]
        )).Capture!;
        FakePodPreview preview = access.Preview(["saved"]);
        _ = store.PlanApply(new(
            capture.SourceActionAddress,
            capture.ExtractionCommitment,
            CharacterNoteDefaultPodV1.EmptyStateIdentity,
            preview.TargetIdentity,
            preview.MemoIds
        ));
        access.Install(preview);
        access.ResetCounters();
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => throw new Xunit.Sdk
                    .XunitException("Planned capture is stored.")),
                access
            );

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await reconciler.ReconcilePendingAsync());

        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            pending.Result
        );
        Assert.Equal(1, access.ConfirmCount);
        Assert.Equal(0, access.AppendCount);
        Assert.Equal(0, access.FreezeCount);
        Assert.Equal(preview.TargetIdentity, reconciler.ReadStatusSnapshot()
            .SettledDefaultPodStateIdentity);
    }

    [Fact]
    public async Task InstalledIndeterminateTargetIsConfirmedAndNotReappended() {
        var access = FakePodAccess.ReadyEmpty();
        access.NextFreeze = FakeFreeze.InstallThenThrow;
        using var fixture = await RuntimeFixture.CreateWithAccessAsync(
            _ => [new CharacterNoteIntent("exact", "submitted")],
            access
        );
        (_, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("exact submitted");

        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            )
        );

        Assert.Equal(1, access.AppendCount);
        Assert.Equal(1, access.FreezeCount);
        Assert.Equal(1, access.ConfirmCount);
        Assert.Null(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveSourceAction);
    }

    [Fact]
    public async Task NotPublishedBaseStaysPlannedAndRetriesWithoutDurableDuplicate() {
        var access = FakePodAccess.ReadyEmpty();
        access.NextFreeze = FakeFreeze.LeaveBaseThenThrow;
        using var fixture = await RuntimeFixture.CreateWithAccessAsync(
            _ => [new CharacterNoteIntent("exact", "submitted")],
            access
        );
        (_, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("exact submitted");

        var deferred = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        ));
        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.PublishNotSettled,
            deferred.Code
        );
        Assert.Equal(CharacterMemoryCaptureState.Planned,
            fixture.Reconciler.ReadStatusSnapshot().ActiveCapture!.State);

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await fixture.Reconciler.ReconcilePendingAsync());
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            pending.Result
        );
        Assert.Equal(1, access.ActiveMemoCount);
        Assert.Equal(2, access.AppendCount);
        Assert.Equal(2, access.FreezeCount);
    }

    [Fact]
    public async Task PlannedMemoIdMismatchQuarantinesBeforeFreeze() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        CharacterMemorySqliteStore store = ReadyStore(
            paths.StatePath,
            engine
        );
        EventAddress source = AppendAction(engine, "planned mismatch");
        CharacterMemoryCaptureSnapshot capture = store.CaptureNew(new(
            EventAddressTextCodec.Format(source),
            Sha256("planned mismatch"),
            Encoding.UTF8.GetByteCount("planned mismatch"),
            "stored-contract",
            ["saved"]
        )).Capture!;
        FakePodPreview preview = access.Preview(["saved"]);
        _ = store.PlanApply(new(
            capture.SourceActionAddress,
            capture.ExtractionCommitment,
            CharacterNoteDefaultPodV1.EmptyStateIdentity,
            preview.TargetIdentity,
            ["m1:00000009"]
        ));
        access.ResetCounters();
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            );

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await reconciler.ReconcilePendingAsync());
        var result = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.Quarantined
        >(pending.Result);

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.PlannedMemoIdMismatch,
            result.Code
        );
        Assert.Equal(0, access.FreezeCount);
        Assert.Equal(CharacterMemoryStoreState.Quarantined,
            reconciler.ReadStatusSnapshot().StoreState);
    }

    [Fact]
    public async Task CapacityFailureIsTerminalRejectedWithoutAppendOrFreeze() {
        var access = FakePodAccess.ReadyEmpty();
        access.ActiveMemoCount = MemoPodLimits.MaximumActiveMemoCount;
        using var fixture = await RuntimeFixture.CreateWithAccessAsync(
            _ => [new CharacterNoteIntent("exact", "submitted")],
            access
        );
        (EventAddress action, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("exact submitted");

        var rejected = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.Rejected
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        ));

        Assert.Equal(CharacterNoteDefaultPodOutcomeCodes.CapacityExceeded,
            rejected.Code);
        Assert.Equal(0, access.AppendCount);
        Assert.Equal(0, access.FreezeCount);
        Assert.Equal(action, rejected.SourceAction);
    }

    private static CharacterMemorySqliteStore ReadyStore(
        string path,
        SessionJournalEngine engine
    ) {
        CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.CreateNew(
                path,
                Owner(engine),
                Baseline(engine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        _ = store.RecordInitialDefaultPod(
            CharacterNoteDefaultPodV1.EmptyStateIdentity
        );
        return store;
    }

    private static CharacterMemoryStoreOwner Owner(
        SessionJournalEngine engine
    ) => new("user", engine.Path);

    private static CharacterMemoryStoreBaseline Baseline(
        SessionJournalEngine engine
    ) => new(
        engine.ReadView.ReadPhysicalAppendFrontier(),
        EventAddressTextCodec.FormatNullable(engine.ReadCurrentHead())
    );

    private static SessionJournalEngine CreateEngine(string path) =>
        SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );

    private static EventAddress AppendAction(
        SessionJournalEngine engine,
        string visibleText
    ) {
        _ = engine.AppendObservation("observation");
        return engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text(visibleText)]),
            Invocation
        );
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))
    );

    private sealed class RecordingExtractor(
        Func<string, IReadOnlyList<CharacterNoteIntent>> extract
    ) : ICharacterNoteExtractor {
        public string ContractId => "character-note-test-contract";
        internal int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(extract(visibleActionText));
        }
    }

    private sealed class RuntimeFixture : IDisposable {
        private readonly FixturePaths _paths;

        private RuntimeFixture(
            FixturePaths paths,
            SessionJournalEngine engine,
            CharacterNoteDefaultPodReconciler reconciler,
            RecordingExtractor extractor
        ) {
            _paths = paths;
            Engine = engine;
            Reconciler = reconciler;
            Extractor = extractor;
        }

        internal SessionJournalEngine Engine { get; }
        internal CharacterNoteDefaultPodReconciler Reconciler { get; }
        internal RecordingExtractor Extractor { get; }

        internal static async ValueTask<RuntimeFixture> CreateAsync(
            Func<string, IReadOnlyList<CharacterNoteIntent>> extract
        ) {
            var paths = new FixturePaths();
            SessionJournalEngine engine = CreateEngine(paths.SessionPath);
            var extractor = new RecordingExtractor(extract);
            CharacterNoteDefaultPodReconciler reconciler =
                await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                    paths.StatePath,
                    Owner(engine),
                    Baseline(engine),
                    extractor
                );
            return new RuntimeFixture(paths, engine, reconciler, extractor);
        }

        internal static async ValueTask<RuntimeFixture>
            CreateWithAccessAsync(
            Func<string, IReadOnlyList<CharacterNoteIntent>> extract,
            FakePodAccess access
        ) {
            var paths = new FixturePaths();
            SessionJournalEngine engine = CreateEngine(paths.SessionPath);
            CharacterMemorySqliteStore store = ReadyStore(
                paths.StatePath,
                engine
            );
            var extractor = new RecordingExtractor(extract);
            CharacterNoteDefaultPodReconciler reconciler =
                await CharacterNoteDefaultPodReconciler.AttachAsync(
                    store,
                    extractor,
                    access
                );
            return new RuntimeFixture(paths, engine, reconciler, extractor);
        }

        internal (
            EventAddress Action,
            GalateaTerminalActionExtractionTarget Target
        ) AppendTarget(string visibleText) {
            EventAddress action = AppendAction(Engine, visibleText);
            return (action, new GalateaTerminalActionExtractionTarget(
                action,
                visibleText
            ));
        }

        public void Dispose() {
            Reconciler.Dispose();
            Engine.Dispose();
            _paths.Dispose();
        }
    }

    private enum FakeFreeze {
        Normal,
        InstallThenThrow,
        LeaveBaseThenThrow,
    }

    private sealed record FakePodPreview(
        string TargetIdentity,
        string[] MemoIds,
        int ActiveMemoCount,
        int ActiveExactTextUtf8Bytes,
        uint NextMemoOrdinal
    );

    private sealed class FakePodAccess : ICharacterNoteDefaultPodAccess {
        internal bool Exists { get; private set; } = true;
        internal string DurableIdentity { get; private set; } =
            CharacterNoteDefaultPodV1.EmptyStateIdentity;
        internal int ActiveMemoCount { get; set; }
        internal int ActiveExactTextUtf8Bytes { get; private set; }
        internal uint NextMemoOrdinal { get; private set; } = 1;
        internal int AppendCount { get; private set; }
        internal int FreezeCount { get; private set; }
        internal int ConfirmCount { get; private set; }
        internal FakeFreeze NextFreeze { get; set; }

        internal static FakePodAccess ReadyEmpty() => new();

        public ICharacterNoteDefaultPodHandle Create(
            string rootPath,
            MemoPodId podId,
            string topic
        ) {
            _ = rootPath;
            Assert.Equal(CharacterNoteDefaultPodV1.PodId, podId);
            Assert.Equal(CharacterNoteDefaultPodV1.Topic, topic);
            return new Handle(this, create: true);
        }

        public ICharacterNoteDefaultPodHandle Open(
            string rootPath,
            MemoPodId podId
        ) {
            _ = rootPath;
            Assert.Equal(CharacterNoteDefaultPodV1.PodId, podId);
            if (!Exists) { throw new FileNotFoundException(); }
            return new Handle(this, create: false);
        }

        internal FakePodPreview Preview(IReadOnlyList<string> exactTexts) {
            var handle = new Handle(this, create: false);
            handle.ResumeEditing();
            string[] ids = exactTexts.Select(handle.Append)
                .Select(static id => id.Value).ToArray();
            return handle.Preview(ids);
        }

        internal void Install(FakePodPreview preview) {
            Exists = true;
            DurableIdentity = preview.TargetIdentity;
            ActiveMemoCount = preview.ActiveMemoCount;
            ActiveExactTextUtf8Bytes = preview.ActiveExactTextUtf8Bytes;
            NextMemoOrdinal = preview.NextMemoOrdinal;
        }

        internal void ResetCounters() {
            AppendCount = 0;
            FreezeCount = 0;
            ConfirmCount = 0;
        }

        private sealed class Handle : ICharacterNoteDefaultPodHandle {
            private readonly FakePodAccess _owner;
            private readonly string _baseIdentity;
            private readonly List<string> _appended = [];
            private int _activeCount;
            private int _activeBytes;
            private uint _next;

            internal Handle(FakePodAccess owner, bool create) {
                _owner = owner;
                _baseIdentity = create
                    ? CharacterNoteDefaultPodV1.EmptyStateIdentity
                    : owner.DurableIdentity;
                _activeCount = create ? 0 : owner.ActiveMemoCount;
                _activeBytes = create
                    ? 0
                    : owner.ActiveExactTextUtf8Bytes;
                _next = create ? 1 : owner.NextMemoOrdinal;
                Phase = create ? MemoPodPhase.Editable : MemoPodPhase.Frozen;
            }

            public MemoPodId PodId => CharacterNoteDefaultPodV1.PodId;
            public MemoPodPhase Phase { get; private set; }
            public int ActiveMemoCount => _activeCount;
            public int ActiveExactTextUtf8Bytes => _activeBytes;

            public MemoId Append(string exactText) {
                Assert.Equal(MemoPodPhase.Editable, Phase);
                MemoId id = MemoId.Parse($"m1:{_next:x8}");
                _next++;
                _activeCount++;
                _activeBytes += Encoding.UTF8.GetByteCount(exactText);
                _appended.Add(exactText);
                _owner.AppendCount++;
                return id;
            }

            public void ResumeEditing() {
                Assert.Equal(MemoPodPhase.Frozen, Phase);
                Phase = MemoPodPhase.Editable;
            }

            public string ComputeStateIdentity() => _appended.Count == 0
                ? _baseIdentity
                : "fake-pod-state:"
                    + Convert.ToHexStringLower(SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            _baseIdentity + "\n"
                            + _next + "\n"
                            + string.Join("\n", _appended)
                        )
                    ));

            public Task FreezeAsync(
                CancellationToken cancellationToken = default
            ) {
                cancellationToken.ThrowIfCancellationRequested();
                _owner.FreezeCount++;
                FakePodPreview preview = Preview([]);
                FakeFreeze mode = _owner.NextFreeze;
                _owner.NextFreeze = FakeFreeze.Normal;
                if (mode is not FakeFreeze.LeaveBaseThenThrow) {
                    _owner.Install(preview);
                }
                if (mode is FakeFreeze.InstallThenThrow
                    or FakeFreeze.LeaveBaseThenThrow) {
                    throw new IOException("fake publish failure");
                }
                Phase = MemoPodPhase.Frozen;
                return Task.CompletedTask;
            }

            public void ConfirmCurrentDocumentDurability() {
                Assert.Equal(MemoPodPhase.Frozen, Phase);
                _owner.ConfirmCount++;
            }

            internal FakePodPreview Preview(string[] memoIds) => new(
                ComputeStateIdentity(),
                memoIds,
                _activeCount,
                _activeBytes,
                _next
            );
        }
    }

    private sealed class FixturePaths : IDisposable {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "atelia-default-pod-tests-" + Guid.NewGuid().ToString("N")
        );

        internal string SessionPath => Path.Combine(_root, "session");
        internal string StatePath => Path.Combine(_root, "memory");

        internal FixturePaths() => Directory.CreateDirectory(_root);

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }
}

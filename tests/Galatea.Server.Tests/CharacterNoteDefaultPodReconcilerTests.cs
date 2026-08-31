using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Data;
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
    public async Task ProvisioningNotFoundFailureStaysRebuildable() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        CharacterMemoryStoreOwner owner = Owner(engine);
        CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.CreateNew(
                paths.StatePath,
                owner,
                Baseline(engine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        var access = FakePodAccess.Absent();
        access.NextCreateFailure =
            CharacterNoteDefaultPodFailureKind.NotFound;

        await Assert.ThrowsAsync<CharacterNoteDefaultPodAccessException>(
            async () => await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            )
        );

        CharacterMemorySqliteStore reopened =
            CharacterMemorySqliteStore.OpenExisting(
                paths.StatePath,
                owner
            );
        Assert.Equal(CharacterMemoryStoreState.Provisioning,
            reopened.ReadStatusSnapshot().StoreState);
        using CharacterNoteDefaultPodReconciler rebuilt =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                reopened,
                new RecordingExtractor(_ => []),
                access
            );
        Assert.Equal(CharacterMemoryStoreState.Ready,
            rebuilt.ReadStatusSnapshot().StoreState);
    }

    [Theory]
    [InlineData("UnsafePath")]
    [InlineData("InvalidDocument")]
    public async Task ProvisioningUnsafeOrInvalidOpenQuarantines(
        string failureName
    ) {
        CharacterNoteDefaultPodFailureKind failure =
            Enum.Parse<CharacterNoteDefaultPodFailureKind>(failureName);
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.CreateNew(
                paths.StatePath,
                Owner(engine),
                Baseline(engine),
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        var access = FakePodAccess.ReadyEmpty();
        access.NextOpenFailure = failure;

        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            );

        Assert.Equal(CharacterMemoryStoreState.Quarantined,
            reconciler.ReadStatusSnapshot().StoreState);
        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.ProvisionStateMismatch,
            reconciler.ReadStatusSnapshot().QuarantineCode
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
    public async Task OriginBarrierMapsExactAppliedMultiMemoCapture() {
        using var fixture = await RuntimeFixture.CreateAsync(_ => [
            new CharacterNoteIntent("first note", "first submitted"),
            new CharacterNoteIntent("second note", "second submitted"),
        ]);
        (EventAddress action, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("two submitted notes");

        var applied = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        ));
        CharacterNoteOriginBarrier barrier =
            fixture.Reconciler.ReadOriginBarrier([
                new CharacterNoteVisibleActionIdentity(
                    action,
                    GalateaVisibleActionFingerprint.Derive(
                        target.VisibleText
                    )
                ),
            ]);

        Assert.Equal(2, barrier.Entries.Count);
        Assert.All(applied.Memos, memo => Assert.True(barrier.Contains(
            memo.PodId,
            memo.MemoId
        )));
        Assert.Equal(
            applied.Memos.Select(static memo => memo.MemoId),
            barrier.Entries.Select(static key => key.MemoId)
        );
    }

    [Fact]
    public async Task OriginBarrierIgnoresAbsentZeroAndRejectedCaptures() {
        using (var zero = await RuntimeFixture.CreateAsync(_ => [])) {
            CharacterNoteOriginBarrier absent =
                zero.Reconciler.ReadOriginBarrier([
                    new CharacterNoteVisibleActionIdentity(
                        new EventAddress(
                            SizedPtr.Create(400, 4),
                            1,
                            AddressHint.None
                        ),
                        GalateaVisibleActionFingerprint.Derive("absent")
                    ),
                ]);
            Assert.Empty(absent.Entries);

            (EventAddress action,
                GalateaTerminalActionExtractionTarget target) =
                zero.AppendTarget("no note request");
            Assert.IsType<
                CharacterNoteDefaultPodReconcileResult.ZeroCaptured
            >(await zero.Reconciler.ReconcileTargetAsync(
                zero.Engine,
                target
            ));
            Assert.Empty(zero.Reconciler.ReadOriginBarrier([
                new CharacterNoteVisibleActionIdentity(
                    action,
                    GalateaVisibleActionFingerprint.Derive(
                        target.VisibleText
                    )
                ),
            ]).Entries);
        }

        var access = FakePodAccess.ReadyEmpty();
        access.ActiveMemoCount = MemoPodLimits.MaximumActiveMemoCount;
        using var rejected = await RuntimeFixture.CreateWithAccessAsync(
            _ => [new CharacterNoteIntent("exact", "submitted")],
            access
        );
        (EventAddress rejectedAction,
            GalateaTerminalActionExtractionTarget rejectedTarget) =
            rejected.AppendTarget("exact submitted");
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.Rejected>(
            await rejected.Reconciler.ReconcileTargetAsync(
                rejected.Engine,
                rejectedTarget
            )
        );
        Assert.Empty(rejected.Reconciler.ReadOriginBarrier([
            new CharacterNoteVisibleActionIdentity(
                rejectedAction,
                GalateaVisibleActionFingerprint.Derive(
                    rejectedTarget.VisibleText
                )
            ),
        ]).Entries);
    }

    [Fact]
    public async Task OriginBarrierRejectsFingerprintMismatch() {
        using var fixture = await RuntimeFixture.CreateAsync(_ => [
            new CharacterNoteIntent("exact", "submitted"),
        ]);
        (EventAddress action, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("exact submitted");
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            )
        );

        Assert.Throws<InvalidDataException>(() =>
            fixture.Reconciler.ReadOriginBarrier([
                new CharacterNoteVisibleActionIdentity(
                    action,
                    GalateaVisibleActionFingerprint.Derive(
                        "changed visible Action"
                    )
                ),
            ])
        );
        Assert.Throws<InvalidDataException>(() =>
            fixture.Reconciler.ReadOriginBarrier([
                new CharacterNoteVisibleActionIdentity(
                    action,
                    new GalateaVisibleActionFingerprint(
                        target.VisibleTextSha256,
                        target.VisibleTextUtf8Bytes + 1
                    )
                ),
            ])
        );
    }

    [Fact]
    public async Task OriginBarrierRejectsCapturedPlannedAndQuarantinedStore() {
        using (var capturedPaths = new FixturePaths())
        using (SessionJournalEngine capturedEngine = CreateEngine(
                   capturedPaths.SessionPath
               )) {
            CharacterMemorySqliteStore store = ReadyStore(
                capturedPaths.StatePath,
                capturedEngine
            );
            EventAddress action = AppendAction(
                capturedEngine,
                "captured pending"
            );
            GalateaVisibleActionFingerprint fingerprint =
                GalateaVisibleActionFingerprint.Derive(
                    "captured pending"
                );
            _ = store.CaptureNew(new CharacterMemoryCaptureRequest(
                EventAddressTextCodec.Format(action),
                fingerprint.Sha256,
                fingerprint.Utf8Bytes,
                "test-contract",
                ["pending note"]
            ));
            using CharacterNoteDefaultPodReconciler reconciler =
                await CharacterNoteDefaultPodReconciler.AttachAsync(
                    store,
                    new RecordingExtractor(_ => []),
                    FakePodAccess.ReadyEmpty()
                );
            Assert.Throws<InvalidDataException>(() =>
                reconciler.ReadOriginBarrier([
                    new CharacterNoteVisibleActionIdentity(
                        action,
                        fingerprint
                    ),
                ])
            );
        }

        var plannedAccess = FakePodAccess.ReadyEmpty();
        plannedAccess.NextFreeze = FakeFreeze.LeaveBaseThenThrow;
        using (var planned = await RuntimeFixture.CreateWithAccessAsync(
                   _ => [new CharacterNoteIntent("exact", "submitted")],
                   plannedAccess
               )) {
            (EventAddress action,
                GalateaTerminalActionExtractionTarget target) =
                planned.AppendTarget("exact submitted");
            Assert.IsType<
                CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
            >(await planned.Reconciler.ReconcileTargetAsync(
                planned.Engine,
                target
            ));
            Assert.Throws<InvalidDataException>(() =>
                planned.Reconciler.ReadOriginBarrier([
                    new CharacterNoteVisibleActionIdentity(
                        action,
                        GalateaVisibleActionFingerprint.Derive(
                            target.VisibleText
                        )
                    ),
                ])
            );
        }

        using var quarantinedPaths = new FixturePaths();
        using SessionJournalEngine quarantinedEngine = CreateEngine(
            quarantinedPaths.SessionPath
        );
        CharacterMemorySqliteStore quarantinedStore = ReadyStore(
            quarantinedPaths.StatePath,
            quarantinedEngine
        );
        CharacterMemoryStatusSnapshot status =
            quarantinedStore.ReadStatusSnapshot();
        _ = quarantinedStore.Quarantine(
            new CharacterMemoryQuarantineRequest(
                status.StoreRevision,
                "TEST_QUARANTINE"
            )
        );
        using CharacterNoteDefaultPodReconciler quarantined =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                quarantinedStore,
                new RecordingExtractor(_ => []),
                FakePodAccess.ReadyEmpty()
            );
        Assert.Throws<CharacterMemoryStoreQuarantinedException>(() =>
            quarantined.ReadOriginBarrier([])
        );
    }

    [Fact]
    public async Task PreCapturePodIoFailureThrowsWithoutCapture() {
        var access = FakePodAccess.ReadyEmpty();
        using var fixture = await RuntimeFixture.CreateWithAccessAsync(
            _ => [new CharacterNoteIntent("exact", "submitted")],
            access
        );
        (_, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTarget("exact submitted");
        access.NextOpenFailure =
            CharacterNoteDefaultPodFailureKind.IoFailure;

        await Assert.ThrowsAsync<CharacterNoteDefaultPodAccessException>(
            async () => await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            )
        );

        CharacterMemoryStatusSnapshot status =
            fixture.Reconciler.ReadStatusSnapshot();
        Assert.Null(status.ActiveSourceAction);
        Assert.Null(status.ActiveCapture);
        Assert.Equal(0, fixture.Extractor.CallCount);
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

    [Theory]
    [InlineData("NotFound")]
    [InlineData("UnsafePath")]
    [InlineData("InvalidDocument")]
    public async Task PlannedOpenMissingUnsafeOrInvalidQuarantines(
        string failureName
    ) {
        CharacterNoteDefaultPodFailureKind failure =
            Enum.Parse<CharacterNoteDefaultPodFailureKind>(failureName);
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        (CharacterMemorySqliteStore store, _) =
            CreateInstalledPlannedTargetStore(
                paths.StatePath,
                engine,
                access
            );
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            );
        access.NextOpenFailure = failure;

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await reconciler.ReconcilePendingAsync());
        var quarantined = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.Quarantined
        >(pending.Result);

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
            quarantined.Code
        );
        Assert.Equal(0, access.AppendCount);
        Assert.Equal(0, access.FreezeCount);
        Assert.Equal(CharacterMemoryStoreState.Quarantined,
            reconciler.ReadStatusSnapshot().StoreState);
    }

    [Theory]
    [InlineData("NotFound", true)]
    [InlineData("UnsafePath", true)]
    [InlineData("InvalidDocument", true)]
    [InlineData("IoFailure", false)]
    public async Task PlannedTargetConfirmClassifiesFailureKind(
        string failureName,
        bool expectsQuarantine
    ) {
        CharacterNoteDefaultPodFailureKind failure =
            Enum.Parse<CharacterNoteDefaultPodFailureKind>(failureName);
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        (CharacterMemorySqliteStore store, _) =
            CreateInstalledPlannedTargetStore(
                paths.StatePath,
                engine,
                access
            );
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            );
        access.NextConfirmFailure = failure;

        var pending = Assert.IsType<
            CharacterNotePendingReconcileResult.Reconciled
        >(await reconciler.ReconcilePendingAsync());

        if (expectsQuarantine) {
            var quarantined = Assert.IsType<
                CharacterNoteDefaultPodReconcileResult.Quarantined
            >(pending.Result);
            Assert.Equal(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                quarantined.Code
            );
            Assert.Equal(CharacterMemoryStoreState.Quarantined,
                reconciler.ReadStatusSnapshot().StoreState);
        }
        else {
            var deferred = Assert.IsType<
                CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
            >(pending.Result);
            Assert.Equal(
                CharacterNoteDefaultPodOutcomeCodes.DurabilityUnconfirmed,
                deferred.Code
            );
            Assert.Equal(CharacterMemoryStoreState.Ready,
                reconciler.ReadStatusSnapshot().StoreState);
            Assert.Equal(CharacterMemoryCaptureState.Planned,
                reconciler.ReadStatusSnapshot().ActiveCapture!.State);
        }
        Assert.Equal(0, access.AppendCount);
        Assert.Equal(0, access.FreezeCount);
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
    public async Task PlannedBaseTipMismatchQuarantinesBeforePodEffects() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        var access = FakePodAccess.ReadyEmpty();
        (CharacterMemorySqliteStore store, _) =
            CreateInstalledPlannedTargetStore(
                paths.StatePath,
                engine,
                access
            );
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                new RecordingExtractor(_ => []),
                access
            );
        CharacterMemoryStatusSnapshot status =
            reconciler.ReadStatusSnapshot();
        CharacterMemoryCaptureSnapshot mismatched =
            status.ActiveCapture! with {
                BasePodStateIdentity = "fake-mismatched-base"
            };

        var result = Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.Quarantined
        >(await reconciler.ReconcileCapturedBatchAsync(
            status,
            mismatched
        ));

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
            result.Code
        );
        Assert.Equal(0, access.AppendCount);
        Assert.Equal(0, access.FreezeCount);
        Assert.Equal(0, access.ConfirmCount);
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

    private static (
        CharacterMemorySqliteStore Store,
        FakePodPreview Preview
    ) CreateInstalledPlannedTargetStore(
        string path,
        SessionJournalEngine engine,
        FakePodAccess access
    ) {
        CharacterMemorySqliteStore store = ReadyStore(path, engine);
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
        return (store, preview);
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
        internal CharacterNoteDefaultPodFailureKind? NextCreateFailure {
            get;
            set;
        }
        internal CharacterNoteDefaultPodFailureKind? NextOpenFailure {
            get;
            set;
        }
        internal CharacterNoteDefaultPodFailureKind? NextConfirmFailure {
            get;
            set;
        }

        internal static FakePodAccess ReadyEmpty() => new();
        internal static FakePodAccess Absent() => new() { Exists = false };

        public ICharacterNoteDefaultPodHandle Create(
            string rootPath,
            MemoPodId podId,
            string topic
        ) {
            _ = rootPath;
            Assert.Equal(CharacterNoteDefaultPodV1.PodId, podId);
            Assert.Equal(CharacterNoteDefaultPodV1.Topic, topic);
            if (NextCreateFailure is { } failure) {
                NextCreateFailure = null;
                throw Failure(failure, "create");
            }
            return new Handle(this, create: true);
        }

        public ICharacterNoteDefaultPodHandle Open(
            string rootPath,
            MemoPodId podId
        ) {
            _ = rootPath;
            Assert.Equal(CharacterNoteDefaultPodV1.PodId, podId);
            if (NextOpenFailure is { } failure) {
                NextOpenFailure = null;
                throw Failure(failure, "open");
            }
            if (!Exists) {
                throw Failure(
                    CharacterNoteDefaultPodFailureKind.NotFound,
                    "open"
                );
            }
            return new Handle(this, create: false);
        }

        private static CharacterNoteDefaultPodAccessException Failure(
            CharacterNoteDefaultPodFailureKind kind,
            string operation
        ) => new(kind, $"fake {operation} failure");

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
            public int ActiveDerivedInfoUtf8Bytes => 0;

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

            public Memo Get(MemoId id) => throw new NotSupportedException(
                $"Fake ExactText-only Pod does not expose memo '{id}'."
            );

            public void UpdateDerivedInfo(
                MemoId id,
                string title,
                string gist,
                string summary
            ) => throw new NotSupportedException(
                $"Fake ExactText-only Pod cannot update memo '{id}'."
            );

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
                    throw Failure(
                        CharacterNoteDefaultPodFailureKind.IoFailure,
                        "publish"
                    );
                }
                Phase = MemoPodPhase.Frozen;
                return Task.CompletedTask;
            }

            public void ConfirmCurrentDocumentDurability() {
                Assert.Equal(MemoPodPhase.Frozen, Phase);
                _owner.ConfirmCount++;
                if (_owner.NextConfirmFailure is { } failure) {
                    _owner.NextConfirmFailure = null;
                    throw Failure(failure, "confirm");
                }
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

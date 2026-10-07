using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCharacterMailDeliveryTests {
    [Fact]
    public async Task SenderOutbox_BindsAgainstTargetAndProofResetsThenDelivers() {
        await using var fixture = await Fixture.CreateAsync();
        GalateaInternalMailSourceOutbox pending = fixture.Source;
        MailboxMessage message =
            GalateaCharacterMailDeliveryReconciler.RestoreMessage(pending);
        SessionInputContent observation = Observation(pending);
        EventAddress baseHead = fixture.Target.Engine.ReadCurrentHead()!.Value;

        _ = pending.Store.BindInternalMailObservation(
            pending.Outbox.DispatchId, pending.Outbox.Revision,
            EventAddressTextCodec.Format(baseHead), observation);

        // The first target gate models a crash after source bind but before
        // SendAsync: it is the only state that may return to Pending.
        GalateaCharacterMailDeliveryReconciler.Reconcile(
            fixture.Supervisor, fixture.Target);
        Assert.Equal(GalateaInternalMailState.Pending, fixture.Source.Outbox.State);

        GalateaInternalMailSourceOutbox rebound = fixture.Source;
        _ = rebound.Store.BindInternalMailObservation(
            rebound.Outbox.DispatchId, rebound.Outbox.Revision,
            EventAddressTextCodec.Format(baseHead), observation);
        EventAddress appended = fixture.Target.Engine.AppendObservation(observation);

        // A second gate models a crash after target append but before sender
        // acknowledgement. Durable Observation append is Delivered even
        // though no Completion has run.
        GalateaCharacterMailDeliveryReconciler.Reconcile(
            fixture.Supervisor, fixture.Target);
        GalateaInternalMailOutboxSnapshot delivered = fixture.Source.Outbox;
        Assert.Equal(GalateaInternalMailState.Delivered, delivered.State);
        Assert.Equal(EventAddressTextCodec.Format(appended),
            delivered.ObservationAddress);
        GalateaCharacterMailDeliveryReconciler.Reconcile(
            fixture.Supervisor, fixture.Target);
        Assert.Equal(delivered, fixture.Source.Outbox);
    }

    [Theory]
    [InlineData(SessionTurnEndReason.Stopped)]
    [InlineData(SessionTurnEndReason.Rejected)]
    [InlineData(SessionTurnEndReason.Incomplete)]
    public async Task TerminatedObservation_DeliversWithoutReissuing(SessionTurnEndReason reason) {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Source;
        SessionInputContent input = Observation(source);
        _ = source.Store.BindInternalMailObservation(
            source.Outbox.DispatchId, source.Outbox.Revision,
            EventAddressTextCodec.Format(fixture.Target.Engine.ReadCurrentHead()!.Value), input);
        EventAddress observation = fixture.Target.Engine.AppendObservation(input);
        _ = Assert.IsType<SessionTurnEndResult.Ended>(
            fixture.Target.Engine.EndPendingTurn(observation, reason));
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        var delivered = fixture.Source.Outbox;
        Assert.Equal(GalateaInternalMailState.Delivered, delivered.State);
        Assert.Equal(EventAddressTextCodec.Format(observation), delivered.ObservationAddress);
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        Assert.Equal(delivered, fixture.Source.Outbox);
    }

    [Fact]
    public void HttpInboundShape_CarriesNoInternalDeliveryCapability() {
        MailboxMessage message = MailboxMessage.CreateInbound(
            new GalateaCharacterName("Bob"), "outside", null, "hello");
        var input = new GalateaFreshInput.InboundMail(message,
            new GalateaInboundMailOrigin.PlayerInjection(new("player", "player", "Player")));

        Assert.Null(input.Origin.DeliveryBinding);
    }

    [Fact]
    public async Task BoundRowForDifferentRepository_BlocksWithoutResetting() {
        await using var fixture = await Fixture.CreateAsync(
            targetRepositoryId: "gdsr1-" + new string('0', 64));
        GalateaInternalMailSourceOutbox source = fixture.Source;
        _ = source.Store.BindInternalMailObservation(
            source.Outbox.DispatchId, source.Outbox.Revision,
            EventAddressTextCodec.Format(
                fixture.Target.Engine.ReadCurrentHead()!.Value),
            Observation(source));

        GalateaTurnException error = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(
                fixture.Supervisor, fixture.Target));

        Assert.Equal("character-mail-target-locator-mismatch",
            error.FailureReason);
        Assert.Equal(GalateaInternalMailState.ObservationBound,
            fixture.Source.Outbox.State);
    }

    [Fact]
    public async Task InternalBinding_RejectsChangedTargetHeadBeforeStoreCas() {
        await using var fixture = await Fixture.CreateAsync();
        GalateaInternalMailSourceOutbox source = fixture.Source;
        EventAddress stale = fixture.Target.Engine.ReadCurrentHead()!.Value;
        _ = fixture.Target.Engine.AppendObservation("intervening");
        var binding = new GalateaInternalMailDeliveryBinding(
            source.Store, source.Outbox.DispatchId, source.Outbox.Revision);

        GalateaTurnException error = Assert.Throws<GalateaTurnException>(() =>
            binding.BindObservationBase(fixture.Target.Engine, stale,
                "frozen observation"));

        Assert.Equal("character-mail-stale-session-head", error.FailureReason);
        Assert.Equal(GalateaInternalMailState.Pending, fixture.Source.Outbox.State);
    }

    [Fact]
    public async Task RelayTargetCheck_RejectsSameRepositoryAfterCharacterRename() {
        await using var fixture = await Fixture.CreateAsync();
        GalateaCharacterConfig renamed = fixture.TargetUser with {
            CharacterName = new GalateaCharacterName("Bob Renamed")
        };
        GalateaCharacterRecipientDirectory directory =
            GalateaCharacterRecipientDirectory.Create([
                fixture.SourceUser,
                renamed
            ]);

        Assert.False(GalateaHostService.IsCurrentInternalMailTarget(
            directory, fixture.Source));
    }

    [Fact]
    public async Task BoundRowForRenamedCharacter_BlocksWithoutResetting() {
        await using var fixture = await Fixture.CreateAsync(
            targetCharacterName: "Bob Renamed");
        GalateaInternalMailSourceOutbox source = fixture.Source;
        _ = source.Store.BindInternalMailObservation(
            source.Outbox.DispatchId, source.Outbox.Revision,
            EventAddressTextCodec.Format(
                fixture.Target.Engine.ReadCurrentHead()!.Value),
            Observation(source));

        GalateaTurnException error = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(
                fixture.Supervisor, fixture.Target));

        Assert.Equal("character-mail-target-recipient-mismatch",
            error.FailureReason);
        Assert.Equal(GalateaInternalMailState.ObservationBound,
            fixture.Source.Outbox.State);
    }

    [Fact]
    public async Task ImapBoundBeforeAppendResets_AndDurableAppendBecomesObservedWithoutGeneration() {
        await using var fixture = await Fixture.CreateAsync();
        GalateaExternalMailInboxSnapshot row = fixture.AcceptExternal();
        SessionInputContent input = ExternalObservation(fixture, row);
        string exactBase = EventAddressTextCodec.Format(fixture.Target.Engine.ReadCurrentHead()!.Value);
        var store = fixture.Target.DelegationHandle!.Store;
        _ = store.BindExternalMailObservation(row.InboxId, row.Revision, exactBase, input);
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        GalateaExternalMailInboxSnapshot reset = store.ReadExternalMail(row.InboxId)!;
        Assert.Equal(GalateaExternalMailInboxState.Pending, reset.State);

        _ = store.BindExternalMailObservation(row.InboxId, reset.Revision, exactBase, input);
        EventAddress appended = fixture.Target.Engine.AppendObservation(input);
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        var observed = store.ReadExternalMail(row.InboxId)!;
        Assert.Equal(GalateaExternalMailInboxState.Observed, observed.State);
        Assert.Equal(EventAddressTextCodec.Format(appended), observed.ObservationAddress);
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        Assert.Equal(observed, store.ReadExternalMail(row.InboxId));
    }

    [Theory]
    [InlineData(SessionTurnEndReason.Stopped)]
    [InlineData(SessionTurnEndReason.Rejected)]
    [InlineData(SessionTurnEndReason.Incomplete)]
    public async Task ImapTerminatedObservationAndUndoNeverReviveInbox(SessionTurnEndReason reason) {
        await using var fixture = await Fixture.CreateAsync();
        var row = fixture.AcceptExternal();
        var store = fixture.Target.DelegationHandle!.Store;
        var input = ExternalObservation(fixture, row);
        _ = store.BindExternalMailObservation(row.InboxId, row.Revision,
            EventAddressTextCodec.Format(fixture.Target.Engine.ReadCurrentHead()!.Value), input);
        EventAddress observation = fixture.Target.Engine.AppendObservation(input);
        var ended = Assert.IsType<SessionTurnEndResult.Ended>(fixture.Target.Engine.EndPendingTurn(observation, reason));
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        var observed = store.ReadExternalMail(row.InboxId)!;
        Assert.Equal(GalateaExternalMailInboxState.Observed, observed.State);
        _ = fixture.Target.Engine.RewindLatestCompletedTurn(fixture.Target.Engine.ReadCurrentHead()!.Value);
        GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target);
        Assert.Equal(observed, store.ReadExternalMail(row.InboxId));
        Assert.Null(store.ReadPendingExternalMail());
        Assert.Equal(1u, store.ReadImapCheckpoint(row.AccountReference)!.ScannedThroughUid);
    }

    [Fact]
    public async Task InternalAndImapBothBound_BlockTheSharedWriterGateWithoutResettingEither() {
        await using var fixture = await Fixture.CreateAsync();
        var internalSource = fixture.Source;
        var external = fixture.AcceptExternal();
        string exactBase = EventAddressTextCodec.Format(fixture.Target.Engine.ReadCurrentHead()!.Value);
        _ = internalSource.Store.BindInternalMailObservation(internalSource.Outbox.DispatchId,
            internalSource.Outbox.Revision, exactBase, Observation(internalSource));
        var store = fixture.Target.DelegationHandle!.Store;
        _ = store.BindExternalMailObservation(external.InboxId, external.Revision, exactBase,
            ExternalObservation(fixture, external));

        var error = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target));
        Assert.Equal("character-mail-multiple-bound", error.FailureReason);
        Assert.Equal(GalateaInternalMailState.ObservationBound, fixture.Source.Outbox.State);
        Assert.Equal(GalateaExternalMailInboxState.ObservationBound, store.ReadExternalMail(external.InboxId)!.State);
    }

    [Fact]
    public async Task ConflictingImapObservationQuarantines_AndAllLaterGateCallsRemainBlocked() {
        await using var fixture = await Fixture.CreateAsync();
        var row = fixture.AcceptExternal();
        var store = fixture.Target.DelegationHandle!.Store;
        _ = store.BindExternalMailObservation(row.InboxId, row.Revision,
            EventAddressTextCodec.Format(fixture.Target.Engine.ReadCurrentHead()!.Value), ExternalObservation(fixture, row));
        _ = fixture.Target.Engine.AppendObservation("different observation");
        var conflict = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target));
        Assert.Equal("character-mail-proof-conflict", conflict.FailureReason);
        Assert.Equal(GalateaExternalMailInboxState.Quarantined, store.ReadExternalMail(row.InboxId)!.State);
        var quarantined = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(fixture.Supervisor, fixture.Target));
        Assert.Equal("character-mail-quarantined", quarantined.FailureReason);
    }

    private static SessionInputContent ExternalObservation(Fixture fixture, GalateaExternalMailInboxSnapshot row) =>
        GalateaObservationContent.Create(new GalateaFreshInput.InboundMail(
            GalateaCharacterMailDeliveryReconciler.RestoreMessage(row),
            new GalateaInboundMailOrigin.ImapDelivery(
                new GalateaImapMailDeliveryBinding(fixture.Target.DelegationHandle!.Store, row.InboxId, row.Revision),
                row.AttachmentCount)), DateTimeOffset.UnixEpoch,
            new("character", fixture.Target.Character.CharacterId, fixture.Target.Character.CharacterName.Value),
            connectionState: new(null, "test", "test", null, "Test", "Test"));

    private static SessionInputContent Observation(GalateaInternalMailSourceOutbox source) =>
        GalateaObservationContent.Create(new GalateaFreshInput.InboundMail(
            GalateaCharacterMailDeliveryReconciler.RestoreMessage(source),
            new GalateaInboundMailOrigin.CharacterDelivery(
                new GalateaSenderSnapshot("character", source.Store.ReadSnapshot().Owner.CharacterId,
                    source.Outbox.FromCharacterName),
                new GalateaInternalMailDeliveryBinding(source.Store, source.Outbox.DispatchId, source.Outbox.Revision))),
            DateTimeOffset.UnixEpoch,
            new GalateaSenderSnapshot("character", source.Outbox.TargetCharacterId, GalateaCharacterMailDeliveryReconciler.RestoreMessage(source).To),
            connectionState: new(null, "test", "test", null, "Test", "Test"));

    private sealed class Fixture : IAsyncDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "atelia-character-mail-delivery-" + Guid.NewGuid().ToString("N"));
        private GalateaDelegationSessionHandle? _targetHandle;
        internal GalateaDelegationSupervisor Supervisor { get; private set; } = null!;
        internal CharacterSessionHost Target { get; private set; } = null!;
        private GalateaCharacterConfig Alice { get; set; } = null!;
        private GalateaCharacterConfig Bob { get; set; } = null!;
        internal GalateaCharacterConfig SourceUser => Alice;
        internal GalateaCharacterConfig TargetUser => Bob;

        internal GalateaInternalMailSourceOutbox Source => Assert.Single(
            Supervisor.ReadInternalMailOutboxesForTarget(Bob.CharacterId));

        internal GalateaExternalMailInboxSnapshot AcceptExternal() {
            var store = Target.DelegationHandle!.Store;
            string account = "imap:" + Bob.CharacterId + ":" + new string('b', 64);
            var checkpoint = store.EstablishImapBaseline(account, 7, 0, DateTimeOffset.UnixEpoch);
            return store.AcceptImapMail(checkpoint, 1, Bob.CharacterName.Value,
                "alice@example.test", "external", "From is an external claim, not Alice identity.", 2)!;
        }

        internal static Task<Fixture> CreateAsync(
            string? targetRepositoryId = null,
            string targetCharacterName = "Bob"
        ) {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture._root);
            Directory.CreateDirectory(Path.Combine(fixture._root, "delegation"));
            fixture.Alice = fixture.User("alice", "Alice");
            fixture.Bob = fixture.User("bob", targetCharacterName);
            using SessionJournalEngine aliceEngine = CreateEngine(
                fixture.Alice.SessionDir);
            SessionJournalEngine bobEngine = CreateEngine(fixture.Bob.SessionDir);
            GalateaConfig config = fixture.Config();
            GalateaDelegationStoreLimits limits =
                GalateaDelegationSupervisor.CreateLimits(
                    config.Delegates.CodexRoute);
            using (GalateaDelegationSqliteStore aliceStore =
                GalateaDelegationSqliteStore.CreateNew(
                    fixture.Alice.DelegationStateDir,
                    Owner(fixture.Alice), Baseline(aliceEngine), limits)) {
                _ = aliceStore.CaptureActionBatch(new(
                    "ej1:00000000000000010000000100000000",
                    new string('a', 64), 5, "character-mail-test-v1",
                    [new SendMailIntent("Bob", "subject", "hello", null, "sent")], GalateaDelegationTestInputs.Sender(aliceStore, "Alice"),
                    [new GalateaInternalMailTarget(
                        fixture.Bob.CharacterId,
                        targetRepositoryId
                            ?? GalateaDelegationSupervisor
                                .CreateSessionRepositoryId(
                                    fixture.Bob.SessionDir),
                        "Alice")]
                ));
            }
            using (GalateaDelegationSqliteStore.CreateNew(
                fixture.Bob.DelegationStateDir, Owner(fixture.Bob),
                Baseline(bobEngine), limits)) { }
            fixture.Supervisor = new GalateaDelegationSupervisor(
                config, new NoopTransport());
            fixture._targetHandle = fixture.Supervisor.AttachWritableSession(
                fixture.Bob.CharacterId, bobEngine);
            fixture.Target = new CharacterSessionHost(
                fixture.Bob, bobEngine,
                new RecentTurnsResponseDto([], null, ContextHeaderDto.Empty),
                GalateaRecapGridDefaultPolicy.ForCharacter(
                    fixture.Bob.CharacterName),
                null,
                fixture._targetHandle,
                DisabledOutboundMailExtractor.Instance,
                DisabledCharacterNoteExtractor.Instance,
                derivedInfoEnricher: null,
                derivedInfoProviderDeadline: null,
                DisabledGalateaPlayerTurnRecallProvider.Instance
            );
            fixture._targetHandle = null; // Target owns the handle now.
            return Task.FromResult(fixture);
        }

        private GalateaConfig Config() => new(
            [Alice, Bob], [], [], null,
            GalateaDelegateTestConfiguration.Create(_root)
        );

        private GalateaCharacterConfig User(string userId, string characterName) {
            string state = Path.Combine(_root, "delegation", userId);
            return new GalateaCharacterConfig(
                userId, new GalateaCharacterName(characterName),
                Path.Combine(_root, "session", userId), state,
                state + "-memory",
                GalateaDelegateTestConfiguration.CreateHomeDirectory(
                    Path.Combine(_root, "session", userId), userId),
                GalateaSessionProvisioning.ExistingOnly, "system", "unused",
                [new("unused", "", "")]);
        }

        public async ValueTask DisposeAsync() {
            if (Target is not null) { await Target.DisposeAsync(); }
            else { _targetHandle?.Dispose(); }
            if (Supervisor is not null) { await Supervisor.DisposeAsync(); }
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }

        private static SessionJournalEngine CreateEngine(string path) =>
            SessionJournalEngine.Create(path,
                new SessionCreateOptions("model", "system", "surface"));

        private static GalateaDelegationStoreOwner Owner(
            GalateaCharacterConfig user
        ) => new(user.CharacterId,
            GalateaDelegationSupervisor.CreateSessionRepositoryId(
                user.SessionDir));

        private static GalateaDelegationStoreBaseline Baseline(
            SessionJournalEngine engine
        ) => new(engine.ReadView.ReadPhysicalAppendFrontier(),
            EventAddressTextCodec.FormatNullable(engine.ReadCurrentHead()));
    }

    private sealed class NoopTransport : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct
        ) => throw new InvalidOperationException("Codex transport is not used.");

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct
        ) => throw new InvalidOperationException("Codex transport is not used.");

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct
        ) => throw new InvalidOperationException("Codex transport is not used.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

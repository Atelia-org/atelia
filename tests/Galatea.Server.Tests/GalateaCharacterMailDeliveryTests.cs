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
        string observation = GalateaMailboxObservationEnvelope.Wrap(message);
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

    [Fact]
    public void HttpInboundShape_CarriesNoInternalDeliveryCapability() {
        MailboxMessage message = MailboxMessage.CreateInbound(
            new GalateaCharacterName("Bob"), "outside", null, "hello");
        var input = new GalateaFreshInput.InboundMail(message);

        Assert.Null(input.InternalDelivery);
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
            GalateaMailboxObservationEnvelope.Wrap(
                GalateaCharacterMailDeliveryReconciler.RestoreMessage(source)));

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
        GalateaUserConfig renamed = fixture.TargetUser with {
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
            GalateaMailboxObservationEnvelope.Wrap(
                GalateaCharacterMailDeliveryReconciler.RestoreMessage(source)));

        GalateaTurnException error = Assert.Throws<GalateaTurnException>(() =>
            GalateaCharacterMailDeliveryReconciler.Reconcile(
                fixture.Supervisor, fixture.Target));

        Assert.Equal("character-mail-target-recipient-mismatch",
            error.FailureReason);
        Assert.Equal(GalateaInternalMailState.ObservationBound,
            fixture.Source.Outbox.State);
    }

    private sealed class Fixture : IAsyncDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "atelia-character-mail-delivery-" + Guid.NewGuid().ToString("N"));
        private GalateaDelegationSessionHandle? _targetHandle;
        internal GalateaDelegationSupervisor Supervisor { get; private set; } = null!;
        internal UserSessionHost Target { get; private set; } = null!;
        private GalateaUserConfig Alice { get; set; } = null!;
        private GalateaUserConfig Bob { get; set; } = null!;
        internal GalateaUserConfig SourceUser => Alice;
        internal GalateaUserConfig TargetUser => Bob;

        internal GalateaInternalMailSourceOutbox Source => Assert.Single(
            Supervisor.ReadInternalMailOutboxesForTarget(Bob.UserId));

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
                    [new SendMailIntent("Bob", "subject", "hello", null, "sent")],
                    [new GalateaInternalMailTarget(
                        fixture.Bob.UserId,
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
                fixture.Bob.UserId, bobEngine);
            fixture.Target = new UserSessionHost(
                fixture.Bob, bobEngine,
                new RecentTurnsResponseDto([], null, ContextHeaderDto.Empty),
                GalateaRecapGridTargetExpectation.ForNames(
                    fixture.Bob.CharacterName, fixture.Bob.PlayerName),
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

        private GalateaUserConfig User(string userId, string characterName) {
            string state = Path.Combine(_root, "delegation", userId);
            return new GalateaUserConfig(
                userId, "pw", new GalateaCharacterName(characterName),
                new GalateaPlayerName("Player"),
                Path.Combine(_root, "session", userId), state,
                state + "-memory",
                GalateaDelegateTestConfiguration.CreateHomeDirectory(
                    Path.Combine(_root, "session", userId), userId),
                GalateaSessionProvisioning.ExistingOnly, "system", "unused");
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
            GalateaUserConfig user
        ) => new(user.UserId,
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

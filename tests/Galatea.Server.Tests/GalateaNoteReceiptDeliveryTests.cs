using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaNoteReceiptDeliveryTests {
    private static readonly CompletionDescriptor Invocation = new("fixture", "fixture-v1", "model-a");
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 6, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task BoundWithoutObservation_ColdReopenRollsBackAndPreservesFrozenReceipt() {
        using var fixture = await Fixture.CreateAsync();
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Pending;
        EventAddress baseHead = fixture.Engine.ReadCurrentHead()!.Value;
        string rendered = fixture.Bind();
        Assert.Equal(CharacterNoteReceiptDeliveryState.ObservationBound, fixture.Exact.State);

        // Models a pre-dispatch stop or a crash after binding but before append.
        await fixture.ColdReopenAsync();
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);

        CharacterNoteReceiptDeliverySnapshot rolledBack = fixture.Pending;
        Assert.Equal(pending.NoticeBody, rolledBack.NoticeBody);
        Assert.Equal(pending.CreatedRevision, rolledBack.CreatedRevision);
        Assert.True(rolledBack.StateRevision > pending.StateRevision);
        Assert.Null(rolledBack.ExpectedSessionHead);
        Assert.Null(rolledBack.RenderedObservation);
        Assert.Null(rolledBack.ObservationAddress);
        Assert.Equal(baseHead, fixture.Engine.ReadCurrentHead());
        Assert.Null(fixture.Memory.ReadBoundReceiptDelivery());
        Assert.Equal(rendered, fixture.Bind());
        _ = fixture.Engine.AppendObservation(rendered);
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, fixture.Exact.State);
    }

    [Fact]
    public async Task AppendedObservationBeforeLedgerAck_ColdReopenDeliversInProgressExactlyOnce() {
        using var fixture = await Fixture.CreateAsync();
        string rendered = fixture.Bind();
        EventAddress observation = fixture.Engine.AppendObservation(rendered);
        Assert.Equal(CharacterNoteReceiptDeliveryState.ObservationBound, fixture.Exact.State);

        // Completion never ran: the durable Observation alone is the delivery boundary.
        await fixture.ColdReopenAsync();
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        CharacterNoteReceiptDeliverySnapshot delivered = fixture.Exact;
        Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, delivered.State);
        Assert.Equal(EventAddressTextCodec.Format(observation), delivered.ObservationAddress);
        Assert.Equal(rendered, delivered.RenderedObservation);
        Assert.Null(fixture.Memory.ReadPendingReceiptDelivery());
        Assert.Null(fixture.Memory.ReadBoundReceiptDelivery());

        await fixture.ColdReopenAsync();
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        Assert.Equal(delivered, fixture.Exact);
        Assert.Equal(observation, fixture.Engine.ReadCurrentHead());
        Assert.Equal(1, fixture.Extractor.Calls);
    }

    [Fact]
    public async Task TerminalObservationProof_ColdReopenDeliversWithoutReextracting() {
        using var fixture = await Fixture.CreateAsync();
        string rendered = fixture.Bind();
        EventAddress observation = fixture.Engine.AppendObservation(rendered);
        EventAddress terminal = AppendTerminal(fixture.Engine, "received");

        await fixture.ColdReopenAsync();
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);

        Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, fixture.Exact.State);
        Assert.Equal(EventAddressTextCodec.Format(observation), fixture.Exact.ObservationAddress);
        Assert.Equal(terminal, fixture.Engine.ReadCurrentHead());
        Assert.Equal(1, fixture.Extractor.Calls);
    }

    [Fact]
    public async Task DifferentAppendedObservation_FailsClosedAndKeepsBindingAcrossReopen() {
        using var fixture = await Fixture.CreateAsync();
        _ = fixture.Bind();
        CharacterNoteReceiptDeliverySnapshot bound = fixture.Exact;
        _ = fixture.Engine.AppendObservation(PlayerTurnObservationEnvelope.Wrap(
            new PlayerTurnObservation("not the bound turn", Timestamp)));

        await fixture.ColdReopenAsync();
        GalateaTurnException error = Assert.Throws<GalateaTurnException>(() =>
            GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine));
        Assert.Contains("exact Observation evidence", error.Message, StringComparison.Ordinal);
        Assert.Equal(bound, fixture.Exact);
        Assert.Null(fixture.Memory.ReadPendingReceiptDelivery());
        Assert.Equal(bound, fixture.Memory.ReadBoundReceiptDelivery());
    }

    [Fact]
    public async Task ChangedBaseBeforeBinding_RejectsWithoutClaimingPendingReceipt() {
        using var fixture = await Fixture.CreateAsync();
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Pending;
        EventAddress staleBase = fixture.Engine.ReadCurrentHead()!.Value;
        _ = fixture.Engine.AppendObservation("intervening observation");
        _ = AppendTerminal(fixture.Engine, "intervening action");

        Assert.Throws<GalateaTurnException>(() => GalateaNoteReceiptDelivery.Bind(
            fixture.Memory, fixture.Engine, pending, staleBase, fixture.Render(pending)));

        Assert.Equal(pending, fixture.Pending);
        Assert.Null(fixture.Memory.ReadBoundReceiptDelivery());
    }

    [Fact]
    public async Task DeliveredThenRewindAndReopen_DoesNotReissueNotificationOrAlreadyAppliedSave() {
        using var fixture = await Fixture.CreateAsync();
        EventAddress baseHead = fixture.Engine.ReadCurrentHead()!.Value;
        _ = fixture.Engine.AppendObservation(fixture.Bind());
        EventAddress terminal = AppendTerminal(fixture.Engine, "received");
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        CharacterNoteReceiptDeliverySnapshot delivered = fixture.Exact;

        Assert.IsType<SessionTurnRetractionResult.Moved>(fixture.Engine.RewindLatestCompletedTurn(terminal));
        Assert.Equal(baseHead, fixture.Engine.ReadCurrentHead());
        await fixture.ColdReopenAsync();
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AlreadyApplied>(
            await fixture.Memory.ReconcileTargetAsync(fixture.Engine, fixture.Target));

        Assert.Equal(delivered, fixture.Exact);
        Assert.Null(fixture.Memory.ReadPendingReceiptDelivery());
        Assert.Null(fixture.Memory.ReadBoundReceiptDelivery());
        Assert.Equal(1, fixture.Extractor.Calls);
    }

    [Fact]
    public async Task CancelledProof_DoesNotChangeBoundReceipt() {
        using var fixture = await Fixture.CreateAsync();
        _ = fixture.Engine.AppendObservation(fixture.Bind());
        CharacterNoteReceiptDeliverySnapshot bound = fixture.Exact;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => GalateaNoteReceiptDelivery.Reconcile(
            fixture.Memory, fixture.Engine, cancellation.Token));

        Assert.Equal(bound, fixture.Exact);
        GalateaNoteReceiptDelivery.Reconcile(fixture.Memory, fixture.Engine);
        Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, fixture.Exact.State);
    }

    private static EventAddress AppendTerminal(SessionJournalEngine engine, string text) =>
        engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text(text)]), Invocation);

    private sealed class Extractor : ICharacterNoteExtractor {
        internal int Calls { get; private set; }
        public string ContractId => "receipt-delivery-proof-test-v1";
        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText, CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>([
                new CharacterNoteIntent("Keep this exact note.", "saved-note")]);
        }
    }

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-note-delivery-tests-" + Guid.NewGuid().ToString("N"));
        private string SessionPath => Path.Combine(_root, "session");
        private string MemoryPath => Path.Combine(_root, "memory");
        private CharacterMemoryStoreOwner Owner => new("user", SessionPath);
        internal SessionJournalEngine Engine { get; private set; } = null!;
        internal CharacterNoteDefaultPodReconciler Memory { get; private set; } = null!;
        internal Extractor Extractor { get; } = new();
        internal GalateaTerminalActionExtractionTarget Target { get; private set; } = null!;
        internal CharacterNoteReceiptDeliverySnapshot Pending => Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(Memory.ReadPendingReceiptDelivery());
        internal CharacterNoteReceiptDeliverySnapshot Exact => Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
            Memory.ReadReceiptDeliveryExact(EventAddressTextCodec.Format(Target.SourceAction)));

        internal static async Task<Fixture> CreateAsync() {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture._root);
            fixture.Engine = SessionJournalEngine.Create(fixture.SessionPath, new SessionCreateOptions("model-a", "system-a", "surface-a"));
            fixture.Memory = await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                fixture.MemoryPath, fixture.Owner,
                new CharacterMemoryStoreBaseline(fixture.Engine.ReadView.ReadPhysicalAppendFrontier(),
                    EventAddressTextCodec.FormatNullable(fixture.Engine.ReadCurrentHead())), fixture.Extractor);
            const string visible = "Keep this exact note. saved-note";
            _ = fixture.Engine.AppendObservation("save this note");
            EventAddress source = AppendTerminal(fixture.Engine, visible);
            fixture.Target = new GalateaTerminalActionExtractionTarget(source, visible);
            Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
                await fixture.Memory.ReconcileTargetAsync(fixture.Engine, fixture.Target));
            Assert.Equal(CharacterNoteReceiptDeliveryState.Pending, fixture.Pending.State);
            return fixture;
        }

        internal string Render(CharacterNoteReceiptDeliverySnapshot receipt) =>
            PlayerTurnObservationEnvelope.Wrap(PlayerTurnObservation.CreateHeartbeatActivation(
                Timestamp, new GalateaCharacterName("Galatea"),
                [new PlayerTurnNotice.NoteSaveReceipt(receipt.NoticeBody)]));

        internal string Bind() {
            CharacterNoteReceiptDeliverySnapshot pending = Pending;
            string rendered = Render(pending);
            GalateaNoteReceiptDelivery.Bind(Memory, Engine, pending, Engine.ReadCurrentHead()!.Value, rendered);
            return rendered;
        }

        internal async Task ColdReopenAsync() {
            Memory.Dispose();
            Engine.Dispose();
            Engine = SessionJournalEngine.Open(SessionPath);
            Memory = await CharacterNoteDefaultPodReconciler.OpenExistingAsync(MemoryPath, Owner, Extractor);
        }

        public void Dispose() {
            Memory.Dispose();
            Engine.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}

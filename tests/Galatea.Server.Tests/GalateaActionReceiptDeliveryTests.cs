using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaActionReceiptDeliveryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondOwnerBindFails_ColdReopenRollsBackOnlyClaimedOwner(bool mailFirst) {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        var note = fixture.NotePending;
        var mail = fixture.MailPending;
        SessionInputContent input = fixture.Input;
        EventAddress head = fixture.Engine.ReadCurrentHead()!.Value;
        IActionReceiptDeliveryStore first = mailFirst ? fixture.Mail : fixture.Memory.ReceiptDeliveryStore;
        IActionReceiptDeliveryStore second = mailFirst ? fixture.Memory.ReceiptDeliveryStore : fixture.Mail;
        ActionReceiptDeliverySnapshot firstPending = mailFirst ? mail : note;
        ActionReceiptDeliverySnapshot secondPending = mailFirst ? note : mail;
        ActionReceiptDelivery.Bind(first, fixture.Engine, firstPending, head, input);
        Assert.Throws<IOException>(() => ActionReceiptDelivery.Bind(
            new RejectBindingStore(second), fixture.Engine, secondPending, head, input));
        Assert.Equal(ActionReceiptDeliveryState.ObservationBound, first.ReadBoundReceiptDelivery()!.State);
        Assert.Equal(secondPending, second.ReadPendingReceiptDelivery());

        await fixture.ReopenAsync();
        ActionReceiptDelivery.Reconcile(fixture.Mail, fixture.Engine);
        ActionReceiptDelivery.Reconcile(fixture.Memory.ReceiptDeliveryStore, fixture.Engine);

        Assert.Equal(head, fixture.Engine.ReadCurrentHead());
        Assert.Equal(note.FrozenBatch, fixture.NotePending.FrozenBatch);
        Assert.Equal(mail.FrozenBatch, fixture.MailPending.FrozenBatch);
        Assert.Null(fixture.Mail.ReadBoundReceiptDelivery());
        Assert.Null(fixture.Memory.ReadBoundReceiptDelivery());
        Assert.Equal(input, fixture.Input);
        fixture.BindBoth(input);
        EventAddress appended = fixture.Engine.AppendObservation(input);
        fixture.ReconcileBoth();
        AssertDelivered(fixture.MailExact, appended);
        AssertDelivered(fixture.NoteExact, appended);
        Assert.Equal(1, fixture.Extractor.Calls);
        Assert.Equal(ActionReceiptDeliveryFixture.NoteText, fixture.SavedNote.ExactText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactAppendThenOneOwnerSettles_ColdReopenSettlesOtherWithoutDuplicate(bool mailFirst) {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        SessionInputContent input = fixture.Input;
        EventAddress baseHead = fixture.Engine.ReadCurrentHead()!.Value;
        fixture.BindBoth(input);
        EventAddress observation = fixture.Engine.AppendObservation(input);
        ActionReceiptDelivery.Reconcile(mailFirst ? fixture.Mail : fixture.Memory.ReceiptDeliveryStore, fixture.Engine);
        ActionReceiptDeliverySnapshot first = mailFirst ? fixture.MailExact : fixture.NoteExact;
        AssertDelivered(first, observation);
        Assert.Equal(ActionReceiptDeliveryState.ObservationBound, (mailFirst ? fixture.NoteExact : fixture.MailExact).State);

        await fixture.ReopenAsync();
        fixture.ReconcileBoth();

        AssertDelivered(fixture.MailExact, observation);
        AssertDelivered(fixture.NoteExact, observation);
        Assert.Equal(first, mailFirst ? fixture.MailExact : fixture.NoteExact);
        Assert.Null(fixture.Mail.ReadPendingReceiptDelivery());
        Assert.Null(fixture.Memory.ReadPendingReceiptDelivery());
        Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(fixture.Engine.ReadView
            .ProveExpectedObservationTurnAtSelectedHead(new(observation, baseHead, input, observation)));
        var mailDelivered = fixture.MailExact;
        var noteDelivered = fixture.NoteExact;
        await fixture.ReopenAsync();
        fixture.ReconcileBoth();
        Assert.Equal(mailDelivered, fixture.MailExact);
        Assert.Equal(noteDelivered, fixture.NoteExact);
        Assert.Equal(observation, fixture.Engine.ReadCurrentHead());
        Assert.Equal(1, fixture.Extractor.Calls);
        Assert.Equal(ActionReceiptDeliveryFixture.NoteText, fixture.SavedNote.ExactText);
        Assert.Equal(ActionReceiptDeliveryFixture.MailText, Assert.Single(fixture.Mail.ReadSnapshot().Mails).Body);
    }

    private static void AssertDelivered(ActionReceiptDeliverySnapshot snapshot, EventAddress observation) {
        Assert.Equal(ActionReceiptDeliveryState.Delivered, snapshot.State);
        Assert.Equal(EventAddressTextCodec.Format(observation), snapshot.ObservationAddress);
        Assert.Null(snapshot.FrozenBatch);
        Assert.Null(snapshot.BoundInput);
    }

    private sealed class RejectBindingStore(IActionReceiptDeliveryStore inner) : IActionReceiptDeliveryStore {
        public ActionReceiptDeliverySnapshot? ReadPendingReceiptDelivery() => inner.ReadPendingReceiptDelivery();
        public ActionReceiptDeliverySnapshot? ReadBoundReceiptDelivery() => inner.ReadBoundReceiptDelivery();
        public ActionReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) => inner.ReadReceiptDeliveryExact(source);
        public ActionReceiptDeliverySnapshot BindReceiptDelivery(string source, long revision, string head, SessionInputContent input) =>
            throw new IOException("Synthetic second-owner bind failure.");
        public ActionReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long revision) => inner.RollbackReceiptDelivery(source, revision);
        public ActionReceiptDeliverySnapshot CompleteReceiptDelivery(string source, long revision, string observation) => inner.CompleteReceiptDelivery(source, revision, observation);
    }
}

/// <summary>Real Journal, Note apply/MemoPod and Mail capture transactions, with no external dispatch.</summary>
internal sealed class ActionReceiptDeliveryFixture : IDisposable {
    internal const string NoteMiddle = "UNIQUE_NOTE_MIDDLE_STORED_EXACTLY";
    internal const string MailMiddle = "UNIQUE_MAIL_MIDDLE_STORED_EXACTLY";
    internal const string NoteText = "Remember the first thirty two symbols and the full note body. "
        + NoteMiddle + " The remembered ending is still available.";
    internal const string MailText = "Please inspect the opening paragraphs of this long message. "
        + MailMiddle + " The mail ends with the original conclusion.";
    internal static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch.AddDays(100);
    internal static readonly CompletionDescriptor Invocation = new("fixture", "fixture-v1", "model-a");
    private readonly string _root = Directory.CreateTempSubdirectory("atelia-action-receipt-tests-").FullName;
    private string SessionPath => Path.Combine(_root, "session");
    private string MemoryPath => Path.Combine(_root, "memory");
    private string MailPath => Path.Combine(_root, "mail");
    private CharacterMemoryStoreOwner MemoryOwner => new("user", SessionPath);
    private GalateaDelegationStoreOwner MailOwner => new("user", SessionPath);
    internal static GalateaDelegationStoreLimits Limits { get; } = new(64, 100000, 100000, 32, 4 * 1024 * 1024);
    internal SessionJournalEngine Engine { get; private set; } = null!;
    internal CharacterNoteDefaultPodReconciler Memory { get; private set; } = null!;
    internal GalateaDelegationSqliteStore Mail { get; private set; } = null!;
    internal NoteExtractor Extractor { get; } = new();
    internal EventAddress SourceAction { get; private set; }
    internal ActionReceiptDeliverySnapshot NotePending => Assert.IsType<ActionReceiptDeliverySnapshot>(Memory.ReadPendingReceiptDelivery());
    internal ActionReceiptDeliverySnapshot MailPending => Assert.IsType<ActionReceiptDeliverySnapshot>(Mail.ReadPendingReceiptDelivery());
    internal ActionReceiptDeliverySnapshot NoteExact => Assert.IsType<ActionReceiptDeliverySnapshot>(Memory.ReadReceiptDeliveryExact(EventAddressTextCodec.Format(SourceAction)));
    internal ActionReceiptDeliverySnapshot MailExact => Assert.IsType<ActionReceiptDeliverySnapshot>(Mail.ReadReceiptDeliveryExact(EventAddressTextCodec.Format(SourceAction)));
    internal Memo SavedNote => Assert.Single(global::Atelia.MemoPod.MemoPod.Open(MemoryPath, CharacterNoteDefaultPodV1.PodId).List());
    internal SessionInputContent Input => GalateaObservationContent.Create(
        new GalateaFreshInput.PlayerAction("Continue", GalateaDelegateTestConfiguration.PlayerSender), Timestamp,
        new GalateaSenderSnapshot("character", "user", "Galatea"),
        [new PlayerTurnNotice.ActionReceipt(MailPending.FrozenBatch!), new PlayerTurnNotice.ActionReceipt(NotePending.FrozenBatch!)], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));

    internal static async Task<ActionReceiptDeliveryFixture> CreateAsync() {
        var fixture = new ActionReceiptDeliveryFixture();
        fixture.Engine = SessionJournalEngine.Create(fixture.SessionPath, new SessionCreateOptions("model-a", "system-a", "surface-a"));
        fixture.Memory = await CharacterNoteDefaultPodReconciler.CreateNewAsync(fixture.MemoryPath, fixture.MemoryOwner,
            new CharacterMemoryStoreBaseline(fixture.Engine.ReadView.ReadPhysicalAppendFrontier(), EventAddressTextCodec.FormatNullable(fixture.Engine.ReadCurrentHead())), fixture.Extractor);
        fixture.Mail = GalateaDelegationSqliteStore.CreateNew(fixture.MailPath, fixture.MailOwner,
            new GalateaDelegationStoreBaseline(fixture.Engine.ReadView.ReadPhysicalAppendFrontier(), EventAddressTextCodec.FormatNullable(fixture.Engine.ReadCurrentHead())), Limits);
        const string visible = NoteText + "\n" + MailText;
        fixture.Engine.AppendObservation("Submit the Note and Mail.");
        fixture.SourceAction = fixture.Engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text(visible)]), Invocation);
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(await fixture.Memory.ReconcileTargetAsync(
            fixture.Engine, new(fixture.SourceAction, visible)));
        fixture.Mail.CaptureActionBatch(new(EventAddressTextCodec.Format(fixture.SourceAction),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visible))).ToLowerInvariant(), Encoding.UTF8.GetByteCount(visible),
            "fixture-mail-v1", [new SendMailIntent("Nobody", null, MailText, null, "evidence")],
            new GalateaSenderSnapshot("character", "user", "Galatea")));
        return fixture;
    }

    internal void BindBoth(SessionInputContent input) {
        EventAddress head = Engine.ReadCurrentHead()!.Value;
        ActionReceiptDelivery.Bind(Mail, Engine, MailPending, head, input);
        ActionReceiptDelivery.Bind(Memory.ReceiptDeliveryStore, Engine, NotePending, head, input);
    }
    internal void ReconcileBoth() {
        ActionReceiptDelivery.Reconcile(Mail, Engine);
        ActionReceiptDelivery.Reconcile(Memory.ReceiptDeliveryStore, Engine);
    }
    internal async Task ReopenAsync() {
        Memory.Dispose();
        Mail.Dispose();
        Engine.Dispose();
        Engine = SessionJournalEngine.Open(SessionPath);
        Mail = GalateaDelegationSqliteStore.OpenExisting(MailPath, MailOwner, Limits);
        Memory = await CharacterNoteDefaultPodReconciler.OpenExistingAsync(MemoryPath, MemoryOwner, Extractor);
    }
    public void Dispose() {
        Memory?.Dispose();
        Mail?.Dispose();
        Engine?.Dispose();
        TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }
    internal sealed class NoteExtractor : ICharacterNoteExtractor {
        internal int Calls { get; private set; }
        public string ContractId => "fixture-note-v1";
        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(string visible, CancellationToken cancellationToken, TextExtractionSource? source = null) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>([new(NoteText)]);
        }
    }
}

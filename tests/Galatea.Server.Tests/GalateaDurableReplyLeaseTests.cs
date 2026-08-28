using System.Security.Cryptography;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableReplyLeaseTests {
    private static readonly CompletionDescriptor ImportedInvocation = new(
        "import",
        "legacy-import-v1",
        "model-a"
    );

    [Fact]
    public void BeginCutoff_NoReadyIsTypedEmptyAndWritesNothing() {
        using var fixture = new Fixture();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();

        GalateaDurableReplyLeaseBeginResult result = fixture.Reconciler
            .BeginCutoff("player");

        Assert.IsType<GalateaDurableReplyLeaseBeginResult.Empty>(result);
        GalateaDelegationStateSnapshot after = fixture.Store.ReadSnapshot();
        Assert.Null(after.ActiveLease);
        Assert.Equal(before.StoreRevision, after.StoreRevision);
        Assert.Empty(after.Notices);
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.None>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
    }

    [Fact]
    public void BeginCutoff_FreezesFifoAndProjectsExactNoticeKinds() {
        using var fixture = new Fixture();
        fixture.ProduceReadyReply("reply-1");
        fixture.ProduceReadyFailure("failure-2");

        GalateaDurableReplyLease lease = BeginCreated(
            fixture.Reconciler,
            "player"
        );
        Assert.Matches(
            "^galatea-reply-lease-[0-9a-f]{32}$",
            lease.LeaseId
        );
        IReadOnlyList<GalateaReadyNotice> frozen = lease.ReadNotices();
        Assert.Collection(
            frozen,
            value => Assert.Equal(
                "reply-1",
                Assert.IsType<GalateaReadyNotice.Reply>(value).Body
            ),
            value => Assert.Equal(
                "failure-2",
                Assert.IsType<GalateaReadyNotice.DeliveryFailure>(value).Body
            )
        );

        fixture.ProduceReadyReply("reply-later");
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(
            2,
            snapshot.Notices.Count(static notice =>
                notice.State == GalateaReplyNoticeState.Leased)
        );
        Assert.Equal(
            "reply-later",
            Assert.Single(snapshot.Notices, static notice =>
                notice.State == GalateaReplyNoticeState.Ready).Body
        );
        Assert.Equal(2, snapshot.ActiveLease!.NoticeIds.Count);

        string rendered = lease.RenderObservation();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            rendered,
            out GalateaPlayerObservation observation
        ));
        Assert.Equal("player", observation.PlayerText);
        Assert.Equal(2, observation.ReadyNotices.Count);
    }

    [Fact]
    public void BeginCutoff_CapsTheEarliestFifoPrefixAtCodeOwnedLimit() {
        using var fixture = new Fixture(maximumInboxReplies: 24);
        for (int index = 0;
             index < GalateaPlayerObservationEnvelope.MaximumNoticeCount + 1;
             index++) {
            fixture.ProduceReadyReply("reply-" + index);
        }

        GalateaDurableReplyLease lease = BeginCreated(
            fixture.Reconciler,
            "player"
        );
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();

        Assert.Equal(
            GalateaPlayerObservationEnvelope.MaximumNoticeCount,
            lease.ReadNotices().Count
        );
        Assert.Equal(
            GalateaPlayerObservationEnvelope.MaximumNoticeCount,
            snapshot.Notices.Count(static notice =>
                notice.State == GalateaReplyNoticeState.Leased)
        );
        Assert.Equal(
            "reply-16",
            Assert.Single(snapshot.Notices, static notice =>
                notice.State == GalateaReplyNoticeState.Ready).Body
        );
    }

    [Fact]
    public void BindRequiresExactCurrentBaseAndCanonicalBody() {
        using var fixture = new Fixture();
        fixture.ProduceReadyReply("reply");
        GalateaDurableReplyLease lease = BeginCreated(
            fixture.Reconciler,
            "player"
        );
        EventAddress baseHead = fixture.Engine.ReadCurrentHead()!.Value;
        string rendered = lease.RenderObservation();

        GalateaDurableReplyLeaseHeadMismatchException mismatch =
            Assert.Throws<GalateaDurableReplyLeaseHeadMismatchException>(() =>
                lease.BindObservationBase(
                    fixture.Engine,
                    ParseAddress(999),
                    rendered
                ));
        Assert.Equal(ParseAddress(999), mismatch.ExpectedHead);
        Assert.Equal(baseHead, mismatch.ObservedHead);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            lease.BindObservationBase(
                fixture.Engine,
                baseHead,
                rendered + " changed"
            ));
        Assert.Equal(
            GalateaReplyLeaseState.CutoffFrozen,
            fixture.Store.ReadSnapshot().ActiveLease!.State
        );

        GalateaReplyLeaseSnapshot bound = lease.BindObservationBase(
            fixture.Engine,
            baseHead,
            rendered
        );
        Assert.Equal(GalateaReplyLeaseState.ObservationBound, bound.State);
        Assert.Equal(EventAddressTextCodec.Format(baseHead),
            bound.ExpectedSessionHead);
        Assert.Equal(rendered, bound.RenderedObservation);
    }

    [Fact]
    public void ReconcileBound_NotAppendedRollsBack() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);

        GalateaDurableReplyLeaseReconcileResult result = fixture.Reconciler
            .ReconcileActiveLease(fixture.Engine);

        Assert.Equal(
            bound.Lease.LeaseId,
            Assert.IsType<GalateaDurableReplyLeaseReconcileResult.RolledBack>(
                result
            ).LeaseId
        );
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Ready,
            fixture.Store.ReadSnapshot().Notices.Single().State
        );
    }

    [Fact]
    public void ReconcileBound_InProgressRecordsAndRehydratesAfterReopen() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        EventAddress observation = fixture.Engine.AppendObservation(
            bound.RenderedObservation
        );

        var retained = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Retained>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.Equal(observation, retained.ObservationAddress);
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction,
            retained.Phase);
        Assert.Equal(
            GalateaReplyLeaseState.ObservationCommitted,
            fixture.Store.ReadSnapshot().ActiveLease!.State
        );
        Assert.False(typeof(IDisposable).IsAssignableFrom(
            typeof(GalateaDurableReplyLease)
        ));
        Assert.DoesNotContain(
            typeof(GalateaDurableReplyLease).GetMethods(),
            static method => method.Name == "Dispose"
        );

        fixture.ReopenStore();
        var rehydrated = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Retained>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.Equal(bound.Lease.LeaseId, rehydrated.Lease.LeaseId);
        Assert.Equal(observation, rehydrated.ObservationAddress);
        Assert.Equal(
            GalateaReplyLeaseState.ObservationCommitted,
            fixture.Store.ReadSnapshot().ActiveLease!.State
        );
    }

    [Fact]
    public void LegacyBoundObservation_ColdReopenValidatesThenRollsBack() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        string legacy = ToLegacyObservation(bound.RenderedObservation);
        ReplaceRenderedObservation(fixture.DatabasePath, legacy);

        fixture.ReopenStore();

        GalateaReplyLeaseSnapshot reopened = Assert.IsType<
            GalateaReplyLeaseSnapshot>(
            fixture.Store.ReadSnapshot().ActiveLease
        );
        Assert.Equal(GalateaReplyLeaseState.ObservationBound, reopened.State);
        Assert.Equal(legacy, reopened.RenderedObservation);
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.RolledBack>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
    }

    [Fact]
    public void LegacyCommittedObservation_ColdReopenConsumesExactTerminal() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        string legacy = ToLegacyObservation(bound.RenderedObservation);
        ReplaceRenderedObservation(fixture.DatabasePath, legacy);
        EventAddress observation = fixture.Engine.AppendObservation(legacy);
        _ = bound.Lease.RecordObservationCommitted(observation);

        fixture.ReopenStore();
        EventAddress terminal = AppendTerminal(fixture.Engine, "terminal");

        var consumed = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Consumed>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.Equal(terminal, consumed.TerminalActionAddress);
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Consumed,
            Assert.Single(fixture.Store.ReadSnapshot().Notices).State
        );
    }

    [Fact]
    public void ReconcileBound_TerminalRecordsAndConsumes() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        _ = fixture.Engine.AppendObservation(bound.RenderedObservation);
        EventAddress action = AppendTerminal(fixture.Engine, "terminal");

        var consumed = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Consumed>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );

        Assert.Equal(action, consumed.TerminalActionAddress);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Null(snapshot.ActiveLease);
        Assert.Equal(GalateaReplyNoticeState.Consumed,
            snapshot.Notices.Single().State);
        Assert.Equal(EventAddressTextCodec.Format(action),
            snapshot.Notices.Single().ConsumedActionAddress);
    }

    [Fact]
    public void ReconcileCommitted_TerminalConsumesAfterPriorRetention() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        _ = fixture.Engine.AppendObservation(bound.RenderedObservation);
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Retained>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        EventAddress action = AppendTerminal(fixture.Engine, "terminal");

        var consumed = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Consumed>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );

        Assert.Equal(action, consumed.TerminalActionAddress);
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
    }

    [Fact]
    public void ReconcileCommitted_ExactAbandonRollsBack() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        EventAddress observation = fixture.Engine.AppendObservation(
            bound.RenderedObservation
        );
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Retained>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.True(fixture.Engine.MoveCurrentHeadForTest(
            observation,
            bound.BaseHead
        ));

        var rolledBack = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.RolledBack>(
            new GalateaDurableReplyLeaseReconciler(fixture.Store)
                .ReconcileActiveLease(fixture.Engine)
        );

        Assert.Equal(bound.Lease.LeaseId, rolledBack.LeaseId);
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(GalateaReplyNoticeState.Ready,
            fixture.Store.ReadSnapshot().Notices.Single().State);
    }

    [Fact]
    public void ReconcileCommitted_ForkConflictQuarantinesAndBlocksCutoff() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        EventAddress observation = fixture.Engine.AppendObservation(
            bound.RenderedObservation
        );
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Retained>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.True(fixture.Engine.MoveCurrentHeadForTest(
            observation,
            bound.BaseHead
        ));
        _ = fixture.Engine.AppendObservation(
            bound.RenderedObservation + " fork"
        );

        var quarantined = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Quarantined>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.Equal(
            SessionExpectedObservationConflictReason
                .ObservationAddressMismatch,
            quarantined.ConflictReason
        );
        Assert.Equal(
            GalateaReplyLeaseState.Quarantined,
            fixture.Store.ReadSnapshot().ActiveLease!.State
        );
        Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Quarantined>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Reconciler.BeginCutoff("another player turn")
        );
    }

    [Fact]
    public void ReconcileCutoffFrozen_ClassifiesUncertainRollbackCommit() {
        bool injectUncertain = true;
        using var fixture = new Fixture(
            hooks: new GalateaDelegationStoreTestHooks(
                AfterCommitBeforeReturn: operation => {
                    if (injectUncertain
                        && operation == "rollback-reply-lease") {
                        injectUncertain = false;
                        throw new IOException("injected after rollback");
                    }
                }
            )
        );
        fixture.ProduceReadyReply("reply");
        _ = BeginCreated(fixture.Reconciler, "player");

        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.RolledBack>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );

        Assert.False(injectUncertain);
        fixture.ReopenStore();
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(GalateaReplyNoticeState.Ready,
            fixture.Store.ReadSnapshot().Notices.Single().State);
    }

    [Fact]
    public void ReconcileCorruptStoreReturnsTypedFailureWithoutSettlement() {
        using var fixture = new Fixture();
        BoundLease bound = CreateBoundLease(fixture);
        ExecuteSql(fixture.DatabasePath, """
            UPDATE reply_lease SET observation_sha256 =
                'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff';
            """);

        var corruption = Assert.IsType<
            GalateaDurableReplyLeaseReconcileResult.Corruption>(
            fixture.Reconciler.ReconcileActiveLease(fixture.Engine)
        );

        Assert.False(string.IsNullOrWhiteSpace(corruption.Detail));
        Assert.Equal(1, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease;"
        ));
        Assert.Equal(1, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_notice WHERE state = 'Leased';"
        ));
        _ = bound;
    }

    private static BoundLease CreateBoundLease(Fixture fixture) {
        fixture.ProduceReadyReply("reply");
        GalateaDurableReplyLease lease = BeginCreated(
            fixture.Reconciler,
            "player"
        );
        EventAddress baseHead = fixture.Engine.ReadCurrentHead()!.Value;
        string rendered = lease.RenderObservation();
        _ = lease.BindObservationBase(
            fixture.Engine,
            baseHead,
            rendered
        );
        return new BoundLease(lease, baseHead, rendered);
    }

    private static GalateaDurableReplyLease BeginCreated(
        GalateaDurableReplyLeaseReconciler reconciler,
        string playerText
    ) => Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(
        reconciler.BeginCutoff(playerText)
    ).Lease;

    private static EventAddress AppendTerminal(
        SessionJournalEngine engine,
        string text
    ) => engine.AppendImportedAgentAction(
        new ActionMessage([new ActionBlock.Text(text)]),
        ImportedInvocation
    );

    private static EventAddress ParseAddress(int value) =>
        EventAddressTextCodec.Parse(Address(value));

    private static string Address(int value) =>
        $"ej1:{value:x16}0000000100000000";

    private static void ExecuteSql(string databasePath, string sql) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ToLegacyObservation(string current) => current
        .Replace(
            GalateaPlayerObservationEnvelope.ReplyHeading,
            "外界代行者 Codex 给 Galatea 的回信",
            StringComparison.Ordinal
        )
        .Replace(
            GalateaPlayerObservationEnvelope.FailureHeading,
            "Galatea 发给外界代行者 Codex 的信未能送达",
            StringComparison.Ordinal
        );

    private static void ReplaceRenderedObservation(
        string databasePath,
        string rendered
    ) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reply_lease
            SET rendered_observation = $rendered,
                observation_utf8_bytes = $bytes,
                observation_sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$rendered", rendered);
        command.Parameters.AddWithValue(
            "$bytes",
            GalateaBoundedJson.StrictUtf8.GetByteCount(rendered)
        );
        command.Parameters.AddWithValue(
            "$sha256",
            Convert.ToHexString(SHA256.HashData(
                GalateaBoundedJson.StrictUtf8.GetBytes(rendered)
            )).ToLowerInvariant()
        );
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static long ExecuteScalarLong(string databasePath, string sql) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed record BoundLease(
        GalateaDurableReplyLease Lease,
        EventAddress BaseHead,
        string RenderedObservation
    );

    private sealed class Fixture : IDisposable {
        private readonly GalateaDelegationStoreLimits _limits;
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly string _root;
        private readonly string _storePath;
        private int _mailOrdinal;

        internal Fixture(
            int maximumInboxReplies = 20,
            GalateaDelegationStoreTestHooks? hooks = null
        ) {
            _root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-durable-reply-lease-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(
                _root
            );
            TestDirectorySafety.CreateDirectoryNew(_root);
            string sessionPath = Path.Combine(_root, "session");
            Engine = SessionJournalEngine.Create(
                sessionPath,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a"
                )
            );
            _limits = new GalateaDelegationStoreLimits(
                MaximumQueuedMails: 32,
                MaximumTaskUtf8Bytes: 100_000,
                MaximumReplyUtf8Bytes:
                    GalateaPlayerObservationEnvelope.MaximumReplyUtf8Bytes,
                maximumInboxReplies,
                MaximumInboxUtf8Bytes: 8 * 1024 * 1024
            );
            var route = new GalateaDelegateRouteConfig(
                GalateaDelegateConfigReader.CanonicalRecipient,
                GalateaDelegateConfigReader.CodexAppServerKind,
                "/repos/focus/atelia",
                GalateaDelegateMode.Work,
                LocalCommandNetwork: true,
                Tools: new GalateaDelegateToolConfig(
                    GalateaDelegateWebSearchMode.Live,
                    ImageGeneration: true,
                    ViewImage: true
                ),
                _limits.MaximumQueuedMails,
                _limits.MaximumTaskUtf8Bytes,
                _limits.MaximumReplyUtf8Bytes,
                _limits.MaximumInboxReplies,
                _limits.MaximumInboxUtf8Bytes
            );
            _owner = new GalateaDelegationStoreOwner(
                "user",
                Engine.Path,
                GalateaDelegationDurableContract
                    .CreateRoutePolicyFingerprint(route)
            );
            _storePath = Path.Combine(_root, "delegation");
            Store = GalateaDelegationSqliteStore.CreateNew(
                _storePath,
                _owner,
                new GalateaDelegationStoreBaseline(
                    Engine.ReadView.ReadPhysicalAppendFrontier(),
                    EventAddressTextCodec.FormatNullable(
                        Engine.ReadCurrentHead()
                    )
                ),
                _limits,
                hooks
            );
            Reconciler = new GalateaDurableReplyLeaseReconciler(Store);
        }

        internal SessionJournalEngine Engine { get; }
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal GalateaDurableReplyLeaseReconciler Reconciler {
            get;
            private set;
        }
        internal string DatabasePath => Path.Combine(
            _storePath,
            GalateaDelegationSqliteStore.DatabaseFileName
        );

        internal void ProduceReadyReply(string body) =>
            ProduceReadyNotice(body, failure: false);

        internal void ProduceReadyFailure(string body) =>
            ProduceReadyNotice(body, failure: true);

        internal void ReopenStore() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(
                _storePath,
                _owner,
                _limits
            );
            Reconciler = new GalateaDurableReplyLeaseReconciler(Store);
        }

        private void ProduceReadyNotice(string body, bool failure) {
            int ordinal = checked(++_mailOrdinal);
            string source = Address(1000 + ordinal);
            GalateaDelegationCaptureResult captured =
                Store.CaptureActionBatch(new(
                    source,
                    Convert.ToHexString(SHA256.HashData(
                        GalateaBoundedJson.StrictUtf8.GetBytes(
                            "action-" + ordinal
                        )
                    )).ToLowerInvariant(),
                    VisibleActionUtf8Bytes: 12,
                    "extractor-contract-v1",
                    [new SendMailIntent(
                        GalateaDelegateConfigReader.CanonicalRecipient,
                        Subject: null,
                        Body: "task-" + ordinal,
                        InReplyToMessageId: null,
                        EvidenceQuote: "evidence"
                    )]
                ));
            GalateaDelegationStateSnapshot snapshot = Store.ReadSnapshot();
            if (snapshot.Route.State == GalateaDelegationRouteState.Unbound) {
                GalateaRouteBindingSnapshot binding =
                    Store.BeginThreadBinding(
                        "binding-operation",
                        snapshot.Route.Revision
                    );
                _ = Store.CompleteThreadBinding(
                    binding.BindingOperationId!,
                    "thread",
                    binding.Revision
                );
                snapshot = Store.ReadSnapshot();
            }
            string dispatchId = Assert.Single(captured.DispatchIds);
            GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value =>
                string.Equals(
                    value.DispatchId,
                    dispatchId,
                    StringComparison.Ordinal
                )
            );
            GalateaOutboundMailSnapshot started = Store.StartQueuedMail(
                dispatchId,
                mail.Revision,
                snapshot.Route.Revision
            );
            if (failure) {
                _ = Store.RecordFailedMail(
                    dispatchId,
                    started.Revision,
                    "thread",
                    "turn-" + ordinal,
                    "turn",
                    "FAILED",
                    body
                );
            }
            else {
                _ = Store.RecordCompletedMail(
                    dispatchId,
                    started.Revision,
                    "thread",
                    "turn-" + ordinal,
                    body
                );
            }
        }

        public void Dispose() {
            Store.Dispose();
            Engine.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}

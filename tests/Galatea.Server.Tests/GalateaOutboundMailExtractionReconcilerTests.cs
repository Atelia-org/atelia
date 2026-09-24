using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaOutboundMailExtractionReconcilerTests {
    private static readonly CompletionDescriptor Invocation = new(
        "fixture",
        "fixture-v1",
        "model-a"
    );

    [Fact]
    public async Task BaselineExistingHistory_IsIgnoredWithoutExtractor() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        EventAddress historical = AppendAction(engine, "historical action");
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        var extractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "Historical Action must not reach the extractor."
            ));
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        GalateaOutboundMailExtractionReconcileResult result =
            await reconciler.ReconcileAsync(engine);

        Assert.Equal(
            historical,
            Assert.IsType<
                GalateaOutboundMailExtractionReconcileResult.BaselineCovered
            >(result).SelectedHead
        );
        Assert.Equal(0, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
    }

    [Fact]
    public async Task PostBaselineAction_CapturesExactIdentityAndIntent() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        const string Target = "[Galatea] I sent exact text to Codex.";
        EventAddress action = AppendAction(engine, Target);
        SendMailIntent intent = Mail("Codex", "exact text");
        var extractor = new RecordingExtractor(target => {
            Assert.Equal(Target, target);
            return [intent];
        });
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(action, captured.SourceAction);
        Assert.Equal(1, captured.ArtifactCount);
        Assert.Single(captured.DispatchIds);
        Assert.Equal(1, extractor.CallCount);
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaActionCaptureSnapshot durable = Assert.Single(
            snapshot.Captures
        );
        Assert.Equal(EventAddressTextCodec.Format(action),
            durable.SourceActionAddress);
        Assert.Equal(Sha256(Target), durable.VisibleActionSha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(Target),
            durable.VisibleActionUtf8Bytes);
        Assert.Equal(extractor.ContractId,
            durable.ExtractorContractId);
        GalateaOutboundMailSnapshot mail = Assert.Single(snapshot.Mails);
        Assert.Equal(intent, new SendMailIntent(
            mail.Recipient,
            mail.Subject,
            mail.Body!,
            mail.InReplyToMessageId,
            mail.EvidenceQuote!
        ));
    }

    [CapturedMailDiagnosticsBuildFact(true)]
    public async Task TwoCapturedMails_EmitDistinctContentFreeDurableIdentities() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "synthetic Action");
        var intents = new[] {
            new SendMailIntent("Codex", "secret-subject-one",
                "secret-body-one", null, "secret-evidence-one"),
            new SendMailIntent("Peer", "secret-subject-two",
                "secret-body-two", null, "secret-evidence-two"),
        };
        var extractor = new RecordingExtractor(_ => intents);
        var diagnostics = new List<string>();
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor,
            GalateaDelegationTestInputs.Sender(store, "Galatea"),
            intent => intent.Recipient == "Peer"
                ? new GalateaInternalMailTarget(
                    "peer-id", "peer-repository", "Galatea")
                : null
        ) { CapturedMailDiagnosticSinkForTest = diagnostics.Add };

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(2, captured.DispatchIds.Count);
        Assert.Equal(2, diagnostics.Count);
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(2, snapshot.Mails.Count);
        string sourceAction = EventAddressTextCodec.Format(action);
        Assert.Equal(2, captured.DispatchIds.Distinct().Count());
        for (int ordinal = 0; ordinal < intents.Length; ordinal++) {
            GalateaOutboundMailSnapshot mail = Assert.Single(
                snapshot.Mails,
                value => value.SourceActionAddress == sourceAction
                    && value.ArtifactOrdinal == ordinal
            );
            Assert.Equal(captured.DispatchIds[ordinal], mail.DispatchId);
            Assert.Equal(intents[ordinal].Recipient, mail.Recipient);
            Assert.Equal(ordinal == 0, mail.IsCodexRouted);
        }
        GalateaInternalMailOutboxSnapshot outbox = Assert.Single(
            snapshot.InternalMailOutboxes);
        Assert.Equal(sourceAction, outbox.SourceActionAddress);
        Assert.Equal(1, outbox.ArtifactOrdinal);
        Assert.Equal(captured.DispatchIds[1], outbox.DispatchId);
        Assert.Equal(captured.StoreRevision, outbox.CaptureSequence);
        Assert.Equal("peer-id", outbox.TargetCharacterId);
        Assert.Equal("peer-repository", outbox.TargetSessionRepositoryId);
        for (int ordinal = 0; ordinal < diagnostics.Count; ordinal++) {
            using JsonDocument document = JsonDocument.Parse(
                diagnostics[ordinal]);
            JsonElement record = document.RootElement;
            Assert.Equal("outbound-mail-captured",
                record.GetProperty("event").GetString());
            Assert.Equal("user", record.GetProperty("characterId").GetString());
            Assert.Equal(sourceAction,
                record.GetProperty("sourceAction").GetString());
            Assert.Equal(ordinal,
                record.GetProperty("artifactOrdinal").GetInt32());
            Assert.Equal(captured.DispatchIds[ordinal],
                record.GetProperty("dispatchId").GetString());
            Assert.Equal(captured.StoreRevision,
                record.GetProperty("captureSequence").GetInt64());
            Assert.Equal(ordinal == 0 ? "Codex" : "Character",
                record.GetProperty("resolvedRecipientKind").GetString());
            Assert.Equal(ordinal == 0 ? "Codex" : "peer-id",
                record.GetProperty("recipientId").GetString());
        }
        string joined = string.Join("\n", diagnostics);
        foreach (SendMailIntent intent in intents) {
            Assert.DoesNotContain(intent.Body, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(intent.Subject!, joined,
                StringComparison.Ordinal);
            Assert.DoesNotContain(intent.EvidenceQuote, joined,
                StringComparison.Ordinal);
        }

        Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured
        >(await reconciler.ReconcileAsync(engine));
        Assert.Equal(2, diagnostics.Count);
    }

    [CapturedMailDiagnosticsBuildFact(true)]
    public async Task UnroutedCapture_DoesNotLogUnverifiedRecipient() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath, engine);
        _ = AppendAction(engine, "synthetic unrouted Action");
        const string UnverifiedRecipient = "unverified-recipient-secret";
        var diagnostics = new List<string>();
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            new RecordingExtractor(_ => [Mail(UnverifiedRecipient,
                "private-body")]),
            GalateaDelegationTestInputs.Sender(store, "Galatea"),
            _ => null
        ) { CapturedMailDiagnosticSinkForTest = diagnostics.Add };

        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.Captured>(
            await reconciler.ReconcileAsync(engine));

        string diagnostic = Assert.Single(diagnostics);
        using JsonDocument document = JsonDocument.Parse(diagnostic);
        Assert.Equal("Unrouted", document.RootElement
            .GetProperty("resolvedRecipientKind").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement
            .GetProperty("recipientId").ValueKind);
        Assert.DoesNotContain(UnverifiedRecipient, diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-body", diagnostic,
            StringComparison.Ordinal);
    }

    [CapturedMailDiagnosticsBuildFact(true)]
    public async Task DiagnosticSinkFailure_DoesNotUndoCommittedCapture() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath, engine);
        _ = AppendAction(engine, "synthetic Action");
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            new RecordingExtractor(_ => [Mail("Codex", "private-body")]),
            GalateaDelegationTestInputs.Sender(store, "Galatea")
        ) { CapturedMailDiagnosticSinkForTest = _ =>
            throw new IOException("diagnostic sink unavailable") };

        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.Captured>(
            await reconciler.ReconcileAsync(engine));
        Assert.Single(store.ReadSnapshot().Mails);
    }

    [CapturedMailDiagnosticsBuildFact(false)]
    public async Task ReleaseCapture_PersistsWithoutDiagnostic() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath, engine);
        EventAddress action = AppendAction(engine, "synthetic Action");
        var diagnostics = new List<string>();
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            new RecordingExtractor(_ => [Mail("Codex", "private-body")]),
            GalateaDelegationTestInputs.Sender(store, "Galatea")
        ) { CapturedMailDiagnosticSinkForTest = diagnostics.Add };

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        GalateaOutboundMailSnapshot mail = Assert.Single(
            store.ReadSnapshot().Mails);
        Assert.Equal(EventAddressTextCodec.Format(action),
            mail.SourceActionAddress);
        Assert.Equal(0, mail.ArtifactOrdinal);
        Assert.Equal(Assert.Single(captured.DispatchIds), mail.DispatchId);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TargetReader_FreezesExactVisibleActionIdentity() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        const string VisibleText = "[Galatea] 记下雪原\n第二行。";
        EventAddress action = AppendAction(engine, VisibleText);

        GalateaTerminalActionExtractionTarget target = ReadTarget(
            engine,
            action
        );

        Assert.Equal(action, target.SourceAction);
        Assert.Equal(VisibleText, target.VisibleText);
        Assert.Equal(Sha256(VisibleText), target.VisibleTextSha256);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(VisibleText),
            target.VisibleTextUtf8Bytes
        );
    }

    [Fact]
    public void TargetReader_ReadsCallerSelectedImmutableHead() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        const string VisibleText = "orphan remains addressable";
        EventAddress action = AppendAction(engine, VisibleText);
        _ = Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.RewindLatestCompletedTurn(action)
        );
        Assert.NotEqual(action, engine.ReadCurrentHead());

        GalateaTerminalActionExtractionTarget target = ReadTarget(
            engine,
            action
        );

        Assert.Equal(action, target.SourceAction);
        Assert.Equal(VisibleText, target.VisibleText);
    }

    [Fact]
    public void Target_RejectsInvalidSourceOrVisibleText() {
        Assert.Throws<ArgumentException>(() =>
            new GalateaTerminalActionExtractionTarget(
                default,
                "visible"
            )
        );
        EventAddress sourceAction = new(
            SizedPtr.Create(4, 32),
            1,
            default
        );
        Assert.Throws<ArgumentNullException>(() =>
            new GalateaTerminalActionExtractionTarget(
                sourceAction,
                null!
            )
        );
        Assert.Throws<EncoderFallbackException>(() =>
            new GalateaTerminalActionExtractionTarget(
                sourceAction,
                "\ud800"
            )
        );
    }

    [Fact]
    public async Task TargetOverload_CapturesWithoutProjectingHistory() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        const string VisibleText = "[Galatea] I sent from one frozen target.";
        EventAddress action = AppendAction(engine, VisibleText);
        GalateaTerminalActionExtractionTarget target = ReadTarget(
            engine,
            action
        );
        SendMailIntent intent = Mail("Codex", "one frozen target");
        var extractor = new RecordingExtractor(text => {
            Assert.Equal(VisibleText, text);
            return [intent];
        });
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileTargetAsync(engine, target));

        SessionJournalReadDiagnostics reads =
            engine.CaptureReadDiagnostics() - before;
        Assert.Equal(default, reads);
        Assert.Equal(action, captured.SourceAction);
        Assert.Equal(1, captured.ArtifactCount);
        Assert.Equal(1, extractor.CallCount);
        GalateaActionCaptureSnapshot durable = Assert.Single(
            store.ReadSnapshot().Captures
        );
        Assert.Equal(target.VisibleTextSha256,
            durable.VisibleActionSha256);
        Assert.Equal(target.VisibleTextUtf8Bytes,
            durable.VisibleActionUtf8Bytes);
    }

    [Fact]
    public async Task TargetOverload_HeadChangeKeepsFinalFence() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "send target then rewind");
        GalateaTerminalActionExtractionTarget target = ReadTarget(
            engine,
            action
        );
        EventAddress? rewoundHead = null;
        var extractor = new RecordingExtractor(_ => {
            SessionTurnRetractionResult.Moved moved = Assert.IsType<
                SessionTurnRetractionResult.Moved
            >(engine.RewindLatestCompletedTurn(action));
            rewoundHead = moved.NewHead;
            return [Mail("Codex", "must be discarded")];
        });

        var stale = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.SelectedHeadChanged
        >(await new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileTargetAsync(engine, target));

        Assert.Equal(action, stale.ExpectedHead);
        Assert.Equal(rewoundHead, stale.ObservedHead);
        Assert.Equal(1, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
    }

    [Fact]
    public async Task NonblankZeroIntent_CapturesDurableTombstone() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "nothing was sent");
        var extractor = new RecordingExtractor(_ => []);
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(action, captured.SourceAction);
        Assert.Equal(0, captured.ArtifactCount);
        Assert.Empty(captured.DispatchIds);
        Assert.Equal(1, extractor.CallCount);
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(0, Assert.Single(snapshot.Captures).ArtifactCount);
        Assert.Empty(snapshot.Mails);
    }

    [Fact]
    public async Task DisabledExtractor_CapturesDistinctProvenance() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "disabled extraction");
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            DisabledOutboundMailExtractor.Instance
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(action, captured.SourceAction);
        Assert.Equal(0, captured.ArtifactCount);
        Assert.Equal(
            DisabledOutboundMailExtractor.DisabledContractId,
            Assert.Single(store.ReadSnapshot().Captures)
                .ExtractorContractId
        );
    }

    [Fact]
    public async Task BlankVisibleAction_SkipsLlmAndCapturesTombstone() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, " \n\t ");
        var extractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "Blank visible Action must not reach the extractor."
            ));
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(action, captured.SourceAction);
        Assert.Equal(0, captured.ArtifactCount);
        Assert.Equal(0, extractor.CallCount);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(" \n\t "),
            Assert.Single(store.ReadSnapshot().Captures)
                .VisibleActionUtf8Bytes
        );
    }

    [Fact]
    public async Task ExactExistingCapture_AcceptsHistoricalContractWithoutExtractorCall() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "send once");
        _ = store.CaptureActionBatch(new GalateaDelegationCaptureRequest(
            EventAddressTextCodec.Format(action),
            Sha256("send once"),
            Encoding.UTF8.GetByteCount("send once"),
            "atelia.galatea.outbound-mail-extractor.v0",
            [Mail("Codex", "once")]
        , GalateaDelegationTestInputs.Sender(store, "Galatea")));
        var noCallExtractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "Exact durable capture must suppress re-extraction."
            ));

        var already = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured
        >(await new GalateaOutboundMailExtractionReconciler(
            store,
            noCallExtractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileAsync(engine));

        Assert.Equal(action, already.SourceAction);
        Assert.Equal(1, already.ArtifactCount);
        Assert.Equal(0, noCallExtractor.CallCount);
        Assert.Equal(
            "atelia.galatea.outbound-mail-extractor.v0",
            Assert.Single(store.ReadSnapshot().Captures)
                .ExtractorContractId
        );
    }

    [Fact]
    public async Task ExistingCaptureIdentityMismatch_FailsClosed() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "exact Action");
        _ = store.CaptureActionBatch(new GalateaDelegationCaptureRequest(
            EventAddressTextCodec.Format(action),
            Sha256("different Action"),
            Encoding.UTF8.GetByteCount("different Action"),
            "atelia.galatea.outbound-mail-extractor.fixture.v1",
            []
        , GalateaDelegationTestInputs.Sender(store, "Galatea")));
        var extractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "Mismatched durable capture must fail before extraction."
            ));

        await Assert.ThrowsAsync<
            GalateaOutboundMailExtractionCaptureMismatchException>(async () =>
                await new GalateaOutboundMailExtractionReconciler(
                    store,
                    extractor
                , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileAsync(engine)
            );

        Assert.Equal(0, extractor.CallCount);
    }

    [Fact]
    public async Task HeadChangedDuringExtraction_ReturnsRetryWithoutCapture() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress action = AppendAction(engine, "send then rewind");
        EventAddress? rewoundHead = null;
        var extractor = new RecordingExtractor(_ => {
            SessionTurnRetractionResult.Moved moved = Assert.IsType<
                SessionTurnRetractionResult.Moved
            >(engine.RewindLatestCompletedTurn(action));
            rewoundHead = moved.NewHead;
            return [Mail("Codex", "must be discarded")];
        });

        var diagnostics = new List<string>();
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor,
            GalateaDelegationTestInputs.Sender(store, "Galatea")
        ) { CapturedMailDiagnosticSinkForTest = diagnostics.Add };

        var stale = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.SelectedHeadChanged
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(action, stale.ExpectedHead);
        Assert.Equal(rewoundHead, stale.ObservedHead);
        Assert.Equal(1, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ExtractorFailure_PropagatesWithoutEmptyTombstone() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        _ = AppendAction(engine, "extractor must fail");
        var extractor = new RecordingExtractor(_ =>
            throw new IOException("extractor unavailable")
        );

        IOException failure = await Assert.ThrowsAsync<IOException>(async () =>
            await new GalateaOutboundMailExtractionReconciler(
                store,
                extractor
            , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileAsync(engine)
        );

        Assert.Equal("extractor unavailable", failure.Message);
        Assert.Equal(1, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
    }

    [Fact]
    public async Task ObservationHead_HasNoPendingTerminalAction() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress observation = engine.AppendObservation("pending");
        var extractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "An Observation head is not a terminal Action gap."
            ));

        var none = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.NoTerminalActionAtHead
        >(await new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileAsync(engine));

        Assert.Equal(observation, none.SelectedHead);
        Assert.Null(none.LatestTerminalAction);
        Assert.Equal(0, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
    }

    [Theory]
    [InlineData(uint.MaxValue,
        (int)GalateaOutboundMailExtractionReadFailureKind.UnsupportedSchema)]
    [InlineData((uint)SessionEventKind.AgentActionProduced,
        (int)GalateaOutboundMailExtractionReadFailureKind.Corruption)]
    public async Task LatestTurnReadFailure_FailsClosedWithoutExtraction(
        uint eventKind,
        int expectedKindValue
    ) {
        using var paths = new FixturePaths();
        RawRepository raw = CreateRawInvalidRepository(
            paths.SessionPath,
            eventKind
        );
        using SessionJournalEngine engine = SessionJournalEngine.Open(
            paths.SessionPath
        );
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine,
            baseline: new GalateaDelegationStoreBaseline(
                raw.Frontier,
                EventAddressTextCodec.Format(raw.BaselineHead)
            )
        );
        var extractor = new RecordingExtractor(_ =>
            throw new Xunit.Sdk.XunitException(
                "Unreadable latest turn must not reach extraction."
            ));

        GalateaOutboundMailExtractionReadException exception =
            await Assert.ThrowsAsync<
                GalateaOutboundMailExtractionReadException>(async () =>
                    await new GalateaOutboundMailExtractionReconciler(
                        store,
                        extractor
                    , GalateaDelegationTestInputs.Sender(store, "Galatea")).ReconcileAsync(engine)
                );

        Assert.Equal(
            (GalateaOutboundMailExtractionReadFailureKind)expectedKindValue,
            exception.Kind
        );
        Assert.Equal(raw.InvalidHead, exception.SelectedHead);
        Assert.Equal(0, extractor.CallCount);
        Assert.Empty(store.ReadSnapshot().Captures);
    }

    [Fact]
    public async Task RewindLeavesOrphanIgnored_AndNewBranchActionCaptures() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine
        );
        EventAddress orphan = AppendAction(engine, "orphan after rewind");
        _ = Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.RewindLatestCompletedTurn(orphan)
        );
        var extractor = new RecordingExtractor(_ => []);
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor
        , GalateaDelegationTestInputs.Sender(store, "Galatea"));

        Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.BaselineCovered
        >(await reconciler.ReconcileAsync(engine));
        EventAddress selected = AppendAction(engine, "new branch Action");
        var captured = Assert.IsType<
            GalateaOutboundMailExtractionReconcileResult.Captured
        >(await reconciler.ReconcileAsync(engine));

        Assert.Equal(selected, captured.SourceAction);
        Assert.Equal(1, extractor.CallCount);
        GalateaActionCaptureSnapshot durable = Assert.Single(
            store.ReadSnapshot().Captures
        );
        Assert.Equal(EventAddressTextCodec.Format(selected),
            durable.SourceActionAddress);
        Assert.NotEqual(EventAddressTextCodec.Format(orphan),
            durable.SourceActionAddress);
    }

    [Fact]
    public async Task StoreConflict_PropagatesWithoutActionCapture() {
        using var paths = new FixturePaths();
        using SessionJournalEngine engine = CreateEngine(paths.SessionPath);
        GalateaDelegationStoreLimits limits = Limits(maximumQueuedMails: 1);
        using GalateaDelegationSqliteStore store = CreateStore(
            paths.StorePath,
            engine,
            limits
        );
        EventAddress occupied = new(
            SizedPtr.Create(4, 32),
            99,
            default
        );
        _ = store.CaptureActionBatch(new GalateaDelegationCaptureRequest(
            EventAddressTextCodec.Format(occupied),
            Sha256("occupied"),
            Encoding.UTF8.GetByteCount("occupied"),
            "atelia.galatea.outbound-mail-extractor.fixture.v1",
            [Mail("Codex", "occupied")]
        , GalateaDelegationTestInputs.Sender(store, "Galatea")));
        EventAddress action = AppendAction(engine, "second mail");
        var extractor = new RecordingExtractor(_ => [
            Mail("Codex", "second")
        ]);

        var diagnostics = new List<string>();
        var reconciler = new GalateaOutboundMailExtractionReconciler(
            store,
            extractor,
            GalateaDelegationTestInputs.Sender(store, "Galatea")
        ) { CapturedMailDiagnosticSinkForTest = diagnostics.Add };

        InvalidOperationException conflict = await Assert.ThrowsAsync<
            InvalidOperationException>(async () =>
                await reconciler.ReconcileAsync(engine)
            );

        Assert.Equal(
            "The durable delegation candidate capacity is full.",
            conflict.Message
        );
        Assert.Equal(1, extractor.CallCount);
        Assert.DoesNotContain(
            store.ReadSnapshot().Captures,
            capture => string.Equals(
                capture.SourceActionAddress,
                EventAddressTextCodec.Format(action),
                StringComparison.Ordinal
            )
        );
        Assert.Empty(diagnostics);
    }

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

    private static GalateaTerminalActionExtractionTarget ReadTarget(
        SessionJournalEngine engine,
        EventAddress selectedHead
    ) => Assert.IsType<
        GalateaTerminalActionExtractionReadResult.Available
    >(GalateaTerminalActionExtractionTargetReader.ReadAt(
        engine,
        selectedHead
    )).Target;

    private static GalateaDelegationSqliteStore CreateStore(
        string path,
        SessionJournalEngine engine,
        GalateaDelegationStoreLimits? limits = null,
        GalateaDelegationStoreBaseline? baseline = null
    ) {
        limits ??= Limits();
        EventAddress? head = engine.ReadCurrentHead();
        baseline ??= new GalateaDelegationStoreBaseline(
            engine.ReadView.ReadPhysicalAppendFrontier(),
            EventAddressTextCodec.FormatNullable(head)
        );
        return GalateaDelegationSqliteStore.CreateNew(
            path,
            Owner(engine.Path),
            baseline,
            limits
        );
    }

    private static GalateaDelegationStoreOwner Owner(
        string repository
    ) => new(
        "user",
        repository
    );

    private static GalateaDelegationStoreLimits Limits(
        int maximumQueuedMails = 32
    ) => new(
        maximumQueuedMails,
        MaximumTaskUtf8Bytes: 100_000,
        MaximumReplyUtf8Bytes: 1024,
        MaximumInboxReplies: 16,
        MaximumInboxUtf8Bytes: 16 * 1024
    );

    private static SendMailIntent Mail(string recipient, string body) => new(
        recipient,
        Subject: null,
        body,
        InReplyToMessageId: null,
        EvidenceQuote: "sent"
    );

    private static string Sha256(string text) => Convert.ToHexString(
        SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text))
    ).ToLowerInvariant();

    private static RawRepository CreateRawInvalidRepository(
        string path,
        uint invalidKind
    ) {
        using var journal = Atelia.EventJournal.EventJournal.CreateNew(path);
        EventAddress baselineHead = journal.AppendEventFrame(
            null,
            new byte[] { 1 },
            opaqueEventKind: (uint)SessionEventKind.SessionCreated
        ).Unwrap();
        RefId main = journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            baselineHead
        ).Unwrap();
        EventJournalPhysicalAppendFrontier frontier =
            journal.ReadPhysicalAppendFrontier();
        EventAddress invalidHead = journal.AppendEventFrame(
            baselineHead,
            "{"u8,
            opaqueEventKind: invalidKind
        ).Unwrap();
        Assert.True(journal.MoveRef(
            main,
            baselineHead,
            invalidHead
        ).Unwrap());
        return new RawRepository(frontier, baselineHead, invalidHead);
    }

    private sealed class RecordingExtractor(
        Func<string, IReadOnlyList<SendMailIntent>> handler
    ) : IOutboundMailExtractor {
        public string ContractId =>
            "atelia.galatea.outbound-mail-extractor.fixture.v1";

        private readonly List<string> _targets = [];

        internal int CallCount => _targets.Count;

        public ValueTask<IReadOnlyList<SendMailIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _targets.Add(visibleActionText);
            return ValueTask.FromResult(handler(visibleActionText));
        }
    }

    private sealed record RawRepository(
        EventJournalPhysicalAppendFrontier Frontier,
        EventAddress BaselineHead,
        EventAddress InvalidHead
    );

    private sealed class FixturePaths : IDisposable {
        internal FixturePaths() {
            Root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-extraction-reconciler-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(
                Root
            );
            TestDirectorySafety.CreateDirectoryNew(Root);
        }

        private string Root { get; }
        internal string SessionPath => Path.Combine(Root, "session");
        internal string StorePath => Path.Combine(Root, "delegation");

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Root);
    }
}

// xUnit v2 cannot dynamically skip inside a test. Decide applicability using
// the compiled service assembly, which need not share the test assembly's DEBUG.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CapturedMailDiagnosticsBuildFactAttribute : FactAttribute {
    public CapturedMailDiagnosticsBuildFactAttribute(bool expectedEnabled) {
        if (GalateaOutboundMailExtractionReconciler
                .CompilesCapturedMailDiagnostics != expectedEnabled) {
            Skip = expectedEnabled
                ? "Requires a DEBUG build of Galatea.Server."
                : "Requires a non-DEBUG build of Galatea.Server.";
        }
    }
}

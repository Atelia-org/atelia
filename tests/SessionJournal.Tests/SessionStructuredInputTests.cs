using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionStructuredInputTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "session-structured-" + Guid.NewGuid().ToString("N"));
    private static SessionInputContent Input(string text) {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new {
            sender = new { kind = "player", id = "p-1", name = "玩家" }, body = text
        }));
        return SessionInputContent.Structured("test.observation.v1", document.RootElement);
    }

    [Fact]
    public void ContentOwnsJsonAndRefusesImplicitText() {
        SessionInputContent input = Input("\r\n```\n汉字😀\n  ");
        Assert.Equal("\r\n```\n汉字😀\n  ", input.JsonValue.GetProperty("body").GetString());
        Assert.Throws<InvalidOperationException>(() => input.TextValue);
        Assert.Equal(input, Input("\r\n```\n汉字😀\n  "));
        using JsonDocument duplicate = JsonDocument.Parse("{\"body\":1,\"body\":2}");
        Assert.Throws<InvalidDataException>(() => SessionInputContent.Structured("test", duplicate.RootElement));
    }

    [Fact]
    public void StructuredInputRejectsExcessiveDepthAndInvalidUnicode() {
        string nested = new string('[', 70) + "0" + new string(']', 70);
        using JsonDocument deep = JsonDocument.Parse("{\"body\":" + nested + "}", new JsonDocumentOptions { MaxDepth = 128 });
        Assert.Throws<InvalidDataException>(() => SessionInputContent.Structured("test", deep.RootElement));
        using JsonDocument invalid = JsonDocument.Parse("{\"body\":\"\\ud800\"}");
        Assert.Throws<InvalidDataException>(() => SessionInputContent.Structured("test", invalid.RootElement));
    }

    [Fact]
    public void PublicSerializerUsesStableMachineEnvelopeForBothKinds() {
        foreach (SessionInputContent input in new[] { SessionInputContent.Text("raw\r\n```"), Input("body") }) {
            string json = JsonSerializer.Serialize(input);
            Assert.Equal(input, JsonSerializer.Deserialize<SessionInputContent>(json));
            using var expected = JsonDocument.Parse(input.ToUtf8Json());
            using var actual = JsonDocument.Parse(json);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
            Assert.DoesNotContain("TextValue", json);
        }
    }

    [Fact]
    public void MaximumContentDepthRoundTripsAndOldPreparedRetainsOriginalDepthLimit() {
        string arrays = new string('[', SessionInputContent.MaximumStructuredDepth - 1)
            + new string(']', SessionInputContent.MaximumStructuredDepth - 1);
        using JsonDocument value = JsonDocument.Parse("{\"body\":" + arrays + "}", new JsonDocumentOptions { MaxDepth = 128 });
        SessionInputContent content = SessionInputContent.Structured("test.depth.v1", value.RootElement);
        string json = JsonSerializer.Serialize(content);
        Assert.Equal(content, JsonSerializer.Deserialize<SessionInputContent>(json));
        byte[] payload = SessionEventCodec.Encode(SessionEventKind.ObservationAccepted, new ObservationAcceptedBody(content));
        Assert.Equal(content, Assert.IsType<ObservationAcceptedBody>(SessionEventCodec.Decode(SessionEventKind.ObservationAccepted, payload, out _)).Content);
        string deeper = "{\"body\":[" + arrays + "]}";
        using JsonDocument rejected = JsonDocument.Parse(deeper, new JsonDocumentOptions { MaxDepth = 128 });
        Assert.Throws<InvalidDataException>(() => SessionInputContent.Structured("test.depth.v1", rejected.RootElement));
        // Build an otherwise valid v8 manifest, with matching tool hash, whose nested
        // schema exceeds the old parser limit. Version widening must not admit it.
        ToolSchema nestedSchema = new ToolSchema.Value(ToolParamType.String);
        for (int index = 0; index < 60; index++) { nestedSchema = new ToolSchema.Array(nestedSchema); }
        var tool = new ToolDefinition("deep", "deep schema", new ToolSchema.Object([
            new ToolSchema.Property("value", nestedSchema, isRequired: true)
        ]));
        EventAddress address = EventAddressTextCodec.Parse("ej1:00000000000000010000000100000000");
        var legacy = PreparedFixture.Create("correlation", "observation", address, address, address, address,
            "model", [tool], new SessionToolRuntimeIdentity("host", "implementation", "capabilities"));
        byte[] legacyPayload = Encoding.UTF8.GetBytes("{\"v\":8,\"body\":" + Encoding.UTF8.GetString(SessionRequestManifestCodec.Encode(legacy)) + "}");
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared, legacyPayload, out _));
        Assert.Contains("64-level", error.Message);
    }

    [Fact]
    public async Task StructuredTurnPersistsFactsAndQueriesWithoutProjector() {
        var client = new Client();
        var projector = new Projector("layout-A");
        SessionInputContent observation = Input("a\r\n```text\n\"b\"\n  ");
        EventAddress originalHead;
        using (var engine = Create(client, projector)) {
            originalHead = engine.ReadCurrentHead()!.Value;
            await engine.SendAsync(observation, CancellationToken.None);
            Assert.Contains("layout-A", client.Requests.Single().PromptPrefix.SystemPrompt);
            Assert.Contains("layout-A", Assert.IsType<ObservationMessage>(client.Requests[0].PromptPrefix.SharedContextMessages.Last()).Content);
            foreach (EventAddress address in ReadEvents(engine).Select(static e => e.Address)) {
                Assert.DoesNotContain("layout-A", Encoding.UTF8.GetString(engine.ReadPayloadBytes(address)));
            }
        }
        using (var audit = SessionJournalEngine.OpenReadOnly(_path)) { audit.ScanCheckedAuditEvents(_ => { }); }
        using var reopened = SessionJournalEngine.Open(_path);
        var events = new List<DecodedSessionEvent>();
        events.AddRange(ReadEvents(reopened));
        Assert.Equal(9, events.Single(e => e.Kind == SessionEventKind.CompletionRequestPrepared).BodySchemaVersion);
        Assert.Equal(2, events.Single(e => e.Kind == SessionEventKind.CompletionAttemptStarted).BodySchemaVersion);
        var snapshot = Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(reopened.ReadRecentCompletedTurns(1)).Value;
        Assert.Equal(observation, snapshot.Turns.Single().ObservationContent);
        EventAddress observationAddress = snapshot.Turns.Single().ObservationAddress;
        Assert.IsType<SessionExpectedObservationTurnReadResult.Terminal>(reopened.ProveExpectedObservationTurnAtSelectedHead(
            new(reopened.ReadCurrentHead()!.Value, originalHead, observation, observationAddress)));
        Assert.IsType<SessionExpectedObservationTurnReadResult.Conflict>(reopened.ProveExpectedObservationTurnAtSelectedHead(
            new(reopened.ReadCurrentHead()!.Value, originalHead, Input("different input"), observationAddress)));
        Assert.IsType<SessionTurnRetractionResult.Moved>(reopened.RewindLatestCompletedTurn(reopened.ReadCurrentHead()!.Value));
        Assert.Equal(originalHead, reopened.ReadCurrentHead());
        Assert.IsType<SessionExpectedObservationTurnReadResult.Abandoned>(reopened.ProveExpectedObservationTurnAtSelectedHead(
            new(originalHead, originalHead, observation, observationAddress)));
    }

    [Fact]
    public async Task PreparedRecoveryUsesCurrentProjectorWithoutReselectingContent() {
        var client = new Client();
        EventAddress prepared;
        byte[] original;
        using (var engine = Create(client, new Projector("layout-A"), new(SessionJournalFailpoint.AfterRequestPreparedCommitted))) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
            prepared = engine.ReadCurrentHead()!.Value;
            original = engine.ReadPayloadBytes(prepared);
        }
        Assert.Empty(client.Requests);
        using var reopened = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("layout-B")));
        Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(reopened.InspectRuntimeRecoveryRequirements());
        await reopened.ResumeAsync(CancellationToken.None);
        Assert.Equal(original, reopened.ReadPayloadBytes(prepared));
        Assert.Contains("layout-B", client.Requests.Single().PromptPrefix.SystemPrompt);
        var events = ReadEvents(reopened);
        Assert.Single(events, e => e.Kind == SessionEventKind.CompletionRequestPrepared);
        var started = events.Single(e => e.Kind == SessionEventKind.CompletionAttemptStarted);
        var body = Assert.IsType<CompletionAttemptStartedBody>(SessionEventCodec.Decode(started.Kind, reopened.ReadPayloadBytes(started.Address), out _));
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(client.Requests.Single()), body.Commitment);
    }

    [Fact]
    public async Task UncertainRefusesBeforeProjectionAndExplicitRetryKeepsBothEvidenceRecords() {
        var client = new Client();
        using (var engine = Create(client, new Projector("layout-A"), new(SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted))) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
        }
        EventAddress started;
        byte[] evidence;
        using (var refused = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("throw", true)))) {
            started = refused.ReadCurrentHead()!.Value;
            evidence = refused.ReadPayloadBytes(started);
            _ = ReadEvents(refused);
            refused.InspectRuntimeRecoveryRequirements();
            await Assert.ThrowsAsync<InvalidOperationException>(() => refused.ResumeAsync(CancellationToken.None));
            Assert.Equal(started, refused.ReadCurrentHead());
        }
        using var retried = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("layout-B")) with {
            UncertainCompletionRecoveryPolicy = SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
        });
        await retried.ResumeAsync(CancellationToken.None);
        Assert.Single(client.Requests);
        Assert.Equal(evidence, retried.ReadPayloadBytes(started));
        var attempts = ReadEvents(retried).Where(e => e.Kind == SessionEventKind.CompletionAttemptStarted).ToArray();
        Assert.Equal(2, attempts.Length);
        var commitments = attempts.Select(e => Assert.IsType<CompletionAttemptStartedBody>(SessionEventCodec.Decode(e.Kind, retried.ReadPayloadBytes(e.Address), out _)).Commitment).ToArray();
        Assert.NotEqual(commitments[0], commitments[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedCommitExceptionNeverCallsProviderAndReopenUsesDurableHead(bool afterCommit) {
        var client = new Client();
        Action<SessionEventKind, EventJournal.EventJournal> fail = (kind, _) => {
            if (kind == SessionEventKind.CompletionAttemptStarted) { throw new IOException("commit outcome unavailable"); }
        };
        var hooks = afterCommit ? new SessionJournalTestHooks(AfterCommitBeforeReturn: fail) : new SessionJournalTestHooks(BeforeCommit: fail);
        using (var engine = Create(client, new Projector("layout-A"), hooks)) {
            await Assert.ThrowsAsync<IOException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
        }
        Assert.Empty(client.Requests);
        using var reopened = SessionJournalEngine.OpenReadOnly(_path);
        Assert.Equal(afterCommit ? SessionExecutionPhase.AwaitingCompletion : SessionExecutionPhase.AwaitingCompletionDispatch,
            reopened.InspectExecutionBoundary().Phase);
        reopened.ScanCheckedAuditEvents(_ => { });
    }

    [Fact]
    public async Task SemanticPreparedRejectsLegacyStartedWithoutEvidence() {
        var client = new Client();
        EventAddress prepared;
        using (var engine = Create(client, new Projector("layout-A"), new(SessionJournalFailpoint.AfterRequestPreparedCommitted))) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
            prepared = engine.ReadCurrentHead()!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(_path)) {
            journal.CommitToRef(SessionJournalDefaults.MainBranchName, prepared,
                SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted, new CompletionAttemptStartedBody()),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap();
        }
        using var reader = SessionJournalEngine.OpenReadOnly(_path);
        Assert.Throws<InvalidDataException>(() => reader.InspectExecutionBoundary());
        Assert.Throws<InvalidDataException>(() => reader.ScanCheckedAuditEvents(_ => { }));
    }

    [Fact]
    public async Task PreparedProjectionFailurePreservesPlanAndHasNoStarted() {
        var client = new Client();
        EventAddress prepared;
        using (var engine = Create(client, new Projector("layout-A"), new(SessionJournalFailpoint.AfterRequestPreparedCommitted))) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
            prepared = engine.ReadCurrentHead()!.Value;
        }
        using var reader = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("too-long")) with { MaximumCanonicalRequestBytes = 1 });
        await Assert.ThrowsAsync<SessionJournalNotReadyException>(() => reader.ResumeAsync(CancellationToken.None));
        Assert.Equal(prepared, reader.ReadCurrentHead());
        Assert.Empty(client.Requests);
        _ = ReadEvents(reader);
    }

    [Fact]
    public async Task NonemptyContributionsMayAbsorbBeyondAdmissionAnchor() {
        var client = new Client();
        using var engine = Create(client, new Projector("layout-A"));
        var fixture = ContextCandidateTestFixture.CreateAtCurrentHead(engine, "late-source");
        EventAddress observation = engine.AppendObservation(Input("body"));
        var source = new TestContextCandidateSource(fixture.Candidate with {
            Contributions = fixture.Candidate.Contributions.Select(c => c with { AbsorbedThrough = observation }).ToArray()
        });
        engine.UseRuntime(Runtime(client, new Projector("layout-B")) with { ContextCandidateSource = source });
        await engine.ResumeAsync(CancellationToken.None);
        Assert.Single(client.Requests);
        var prepared = ReadEvents(engine).Single(e => e.Kind == SessionEventKind.CompletionRequestPrepared);
        var body = Assert.IsType<CompletionRequestPreparedBody>(SessionEventCodec.Decode(prepared.Kind, engine.ReadPayloadBytes(prepared.Address), out _));
        Assert.Empty(body.Plan.ExactContextInputs);
        Assert.Equal(2, body.Plan.SemanticContributions.Length);
        Assert.All(body.Plan.SemanticContributions, c => Assert.Equal(observation, c.AbsorbedThrough));
        Assert.DoesNotContain("recap-block", Encoding.UTF8.GetString(engine.ReadPayloadBytes(prepared.Address)));
        _ = ReadEvents(engine);
    }

    [Fact]
    public async Task FinalProjectionFailureOccursAfterSemanticPreparedIsDurable() {
        var client = new Client();
        var projector = new Projector("layout-A");
        using var engine = Create(client, projector, new(AfterCommitBeforeReturn: (kind, _) => {
            if (kind == SessionEventKind.CompletionRequestPrepared) { projector.Fails = true; }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
        Assert.Equal(SessionExecutionPhase.AwaitingCompletionDispatch, engine.InspectExecutionBoundary().Phase);
        var events = ReadEvents(engine);
        Assert.Single(events, e => e.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.DoesNotContain(events, e => e.Kind == SessionEventKind.CompletionAttemptStarted);
        Assert.Empty(client.Requests);
        engine.InspectRuntimeRecoveryRequirements();
        engine.Dispose();
        using var audit = SessionJournalEngine.OpenReadOnly(_path);
        audit.ScanCheckedAuditEvents(_ => { });
    }

    [Fact]
    public void DesiredSetupAndOfflineAuditDoNotNeedSchemaProjector() {
        using var engine = SessionJournalEngine.Create(_path, new("model", Input("instructions"), "surface"));
        EventAddress head = engine.ReadCurrentHead()!.Value;
        var result = Assert.IsType<SessionDesiredSetupReconciliationResult.Ready>(
            engine.ReconcileDesiredSetup(head, new("model", "surface", Input("instructions"))));
        Assert.False(result.SystemPromptChanged);
        Assert.Equal(head, engine.ReadCurrentHead());
        _ = ReadEvents(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefuseStillRejectsCorruptSemanticPlan(bool wrongSetup) {
        var client = new Client();
        CompletionRequestPreparedBody manifest;
        CompletionAttemptStartedBody evidence;
        EventAddress head;
        EventAddress rawEnd;
        using (var engine = Create(client, new Projector("layout-A"), new(SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted))) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.SendAsync(Input("body"), CancellationToken.None));
            var events = ReadEvents(engine);
            var prepared = events.Single(e => e.Kind == SessionEventKind.CompletionRequestPrepared);
            manifest = Assert.IsType<CompletionRequestPreparedBody>(prepared.Body);
            evidence = Assert.IsType<CompletionAttemptStartedBody>(events.Single(e => e.Kind == SessionEventKind.CompletionAttemptStarted).Body);
            rawEnd = prepared.Parent!.Value;
            head = engine.ReadCurrentHead()!.Value;
        }
        manifest = wrongSetup
            ? manifest with { Setups = manifest.Setups with { SystemPrompt = manifest.Setups.SystemPrompt with { Address = rawEnd } } }
            : manifest with { Plan = manifest.Plan with { RawRangeSha256 = new string('0', 64) } };
        using (var journal = EventJournal.EventJournal.OpenExisting(_path)) {
            RefId branch = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            Assert.True(journal.MoveRef(branch, head, rawEnd).Unwrap());
            var prepared = journal.CommitToRef(branch, rawEnd, SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, manifest),
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared, hint: default).Unwrap().EventAddress;
            journal.CommitToRef(branch, prepared, SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted, evidence),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap();
        }
        using var reopened = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("disabled", true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ResumeAsync(CancellationToken.None));
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public async Task ExplicitLegacyNonemptyPreparedRetainsExactRecoveryWithoutCurrentProjector(int version, bool uncertain) {
        var client = new Client();
        var source = new TestContextCandidateSource();
        EventAddress currentPrepared;
        EventAddress created;
        // Literal v1 raw inputs ensure this exercises actual old input schemas, not v2 Text.
        using (var journal = EventJournal.EventJournal.CreateNew(_path)) {
            RefId branch = journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
            EventAddress runtimeSetup = journal.CommitToRef(branch, null,
                SessionEventCodec.Encode(SessionEventKind.RuntimeConfigSetup,
                    new SessionRuntimeConfiguration("model", "surface", SessionJournalDefaults.Schema, new(0))),
                opaqueEventKind: (uint)SessionEventKind.RuntimeConfigSetup, hint: default).Unwrap().EventAddress;
            EventAddress prompt = journal.CommitToRef(branch, runtimeSetup,
                """{"v":1,"body":{"content":"legacy instructions"}}"""u8,
                opaqueEventKind: (uint)SessionEventKind.SystemPromptSetup, hint: default).Unwrap().EventAddress;
            created = journal.CommitToRef(branch, prompt,
                SessionEventCodec.Encode(SessionEventKind.SessionCreated, new SessionCreatedBody(SessionCreationOrigin.Native)),
                opaqueEventKind: (uint)SessionEventKind.SessionCreated, hint: default).Unwrap().EventAddress;
            journal.CommitToRef(branch, created,
                """{"v":1,"body":{"content":"legacy body"}}"""u8,
                opaqueEventKind: (uint)SessionEventKind.ObservationAccepted, hint: default).Unwrap();
        }
        using (var engine = SessionJournalEngine.OpenForTest(_path,
                   Runtime(client, new Projector("disabled", true)) with { ContextCandidateSource = source },
                   new(SessionJournalFailpoint.AfterRequestPreparedCommitted))) {
            source.Candidate = ContextCandidateTestFixture.CreateCandidate(engine, created, engine.ResolveGoverningSetup(created),
                ContextCandidateTestFixture.Contribution(ContextHeaderCarrier.Observation, "world", "historic world content", created),
                ContextCandidateTestFixture.Contribution(ContextHeaderCarrier.Action, "self", "historic first-person content", created));
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() => engine.ResumeAsync(CancellationToken.None));
            currentPrepared = engine.ReadCurrentHead()!.Value;
        }
        byte[] expectedRequest;
        byte[] legacyPayload;
        EventAddress legacyPrepared;
        using (var journal = EventJournal.EventJournal.OpenExisting(_path)) {
            var reconstruction = SessionPreparedRequestReconstructor.Reconstruct(journal, currentPrepared);
            expectedRequest = reconstruction.CanonicalBytes;
            var original = reconstruction.Manifest;
            var snapshots = original.Plan.SemanticContributions.Select(c => SessionContextContributionRenderer.RenderOneHot(c.Target, c.ExactText));
            var legacy = original with {
                Plan = original.Plan with {
                    SemanticContributions = [],
                    ExactContextInputs = [.. snapshots.Select(snapshot => new SessionRequestContextInput(SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot))]
                },
                Recipe = original.Recipe with { RecipeId = SessionRequestManifestDefaults.RecipeId },
                Commitment = SessionRequestCanonicalizer.CreateCommitment(reconstruction.Request)
            };
            legacyPayload = version == 7 ? LegacyPreparedV7TestFixture.Encode(legacy, "historic-adapter")
                : SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, legacy);
            RefId branch = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            Assert.True(journal.MoveRef(branch, currentPrepared, reconstruction.RawEndInclusive).Unwrap());
            legacyPrepared = journal.CommitToRef(branch, reconstruction.RawEndInclusive, legacyPayload,
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared, hint: default).Unwrap().EventAddress;
            if (uncertain) {
                journal.CommitToRef(branch, legacyPrepared,
                    SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted, new CompletionAttemptStartedBody()),
                    opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap();
            }
        }
        var recoverySource = new TestContextCandidateSource { ForcedStatus = SessionContextCandidateSelectionStatus.StoreUnavailable };
        using (var reopened = SessionJournalTestRuntime.Attach(SessionJournalEngine.Open(_path), Runtime(client, new Projector("disabled", true)) with {
            ContextCandidateSource = recoverySource,
            UncertainCompletionRecoveryPolicy = SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
        })) {
            reopened.InspectRuntimeRecoveryRequirements();
            await reopened.ResumeAsync(CancellationToken.None);
            Assert.Equal(expectedRequest, SessionRequestCanonicalizer.Canonicalize(client.Requests.Single()));
            Assert.Equal(legacyPayload, reopened.ReadPayloadBytes(legacyPrepared));
            Assert.Equal(0, recoverySource.SelectionCount);
            Assert.Equal(0, recoverySource.MaterializationCount);
        }
        using var audit = SessionJournalEngine.OpenReadOnly(_path);
        audit.ScanCheckedAuditEvents(_ => { });
        var historicalEvents = ReadEvents(audit);
        Assert.Equal(version, historicalEvents.Single(e => e.Kind == SessionEventKind.CompletionRequestPrepared).BodySchemaVersion);
        Assert.Equal(1, historicalEvents.Single(e => e.Kind == SessionEventKind.SystemPromptSetup).BodySchemaVersion);
        Assert.Equal(1, historicalEvents.Single(e => e.Kind == SessionEventKind.ObservationAccepted).BodySchemaVersion);
    }

    private SessionJournalEngine Create(Client client, Projector projector, SessionJournalTestHooks? hooks = null) =>
        SessionJournalEngine.CreateForTest(_path, new("model", Input("instructions"), "surface"), Runtime(client, projector), hooks ?? new());
    private static SessionRuntime Runtime(Client client, Projector projector) => new(client,
        CompletionTarget: new("connection", "test", "identity"),
        ContextCandidateSource: new TestContextCandidateSource { IsEmptyLineage = true }, InputProjector: projector);
    private static List<DecodedSessionEvent> ReadEvents(SessionJournalEngine engine) =>
        engine.ReadCurrentLineageHeaders().HeadToRoot.Reverse().Select(header => {
            object body = SessionEventCodec.Decode(header.Kind, engine.ReadPayloadBytes(header.Address), out int version);
            return new DecodedSessionEvent(header.Kind, version, body, header.Address, header.Parent);
        }).ToList();
    private sealed class Projector(string prefix, bool throws = false) : ISessionInputProjector {
        public bool Fails { get; set; }
        public string Project(SessionInputContent input) => throws || Fails ? throw new NotSupportedException("projector disabled")
            : prefix + "\n" + input.JsonValue.GetProperty("body").GetString();
    }
    private sealed class Client : ICompletionClient {
        public string Name => "structured-test";
        public string ApiSpecId => "test-v1";
        public List<CompletionRequest> Requests { get; } = [];
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("answer")]), new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
        }
    }
    public void Dispose() { if (Directory.Exists(_path)) { Directory.Delete(_path, true); } }
}

using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalAuditScanTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void EmptySelectedBranch_IsAValidExactScan() {
        string path = NewPath();
        RefId emptyRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            emptyRef = journal.CreateBranch(
                "empty",
                startPoint: null
            ).Unwrap();
        }

        var events = new List<SessionJournalAuditEvent>();
        using var engine =
            SessionJournalEngine.OpenReadOnly(path, "empty");
        SessionJournalAuditScanResult result =
            engine.ScanCheckedAuditEvents(events.Add);

        Assert.Equal("empty", result.BranchName);
        Assert.Equal(emptyRef, result.BranchRefId);
        Assert.Null(result.CapturedHead);
        Assert.Equal(
            new SessionExecutionState(
                SessionExecutionPhase.Empty,
                HeadKind: null
            ),
            result.ExecutionStateAtCapturedHead
        );
        Assert.Equal(0, result.EventCount);
        Assert.Equal(0, result.LogicalPayloadBytes);
        Assert.Empty(events);
        Assert.Equal(
            new SessionJournalAuditScanDiagnostics(
                CapturedEventCount: 0,
                RepositoryEventReadCount: 0,
                IndexedHeaderLookupCount: 0,
                IndexedEventLookupCount: 0,
                DecodedPayloadBytes: 0,
                PreparedReconstructionCount: 0
            ),
            result.Diagnostics
        );
    }

    [Fact]
    public void NonMainBranch_BindsExactRefAndDoesNotScanMainSuffix() {
        string path = NewPath();
        EventAddress forkPoint;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-main",
                "system-main",
                "surface-main"
            )
        )) {
            mainRef = created.BranchRefId;
            forkPoint =
                created.InspectExecutionBoundary().Head!.Value;
        }

        RefId featureRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                forkPoint
            ).Unwrap();
        }

        EventAddress featureHead;
        using (var feature =
               SessionJournalEngine.Open(path, "feature")) {
            featureHead = feature.AppendSystemPromptSetup(
                "system-feature"
            );
        }
        EventAddress mainHead;
        using (var main = SessionJournalEngine.Open(path)) {
            mainHead = main.AppendSystemPromptSetup(
                "system-main-later"
            );
        }

        var events = new List<SessionJournalAuditEvent>();
        using var readOnly =
            SessionJournalEngine.OpenReadOnly(path, "feature");
        SessionJournalAuditScanResult result =
            readOnly.ScanCheckedAuditEvents(events.Add);

        Assert.Equal(featureRef, result.BranchRefId);
        Assert.Equal(featureHead, result.CapturedHead);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            result.ExecutionStateAtCapturedHead.Phase
        );
        Assert.Equal(
            SessionEventKind.SystemPromptSetup,
            result.ExecutionStateAtCapturedHead.HeadKind
        );
        Assert.NotEqual(mainHead, result.CapturedHead);
        Assert.Equal(featureHead, events[^1].Address);
        Assert.Equal(
            "system-feature",
            Assert.IsType<SessionJournalAuditSystemPromptFact>(
                events[^1].Fact
            ).SystemPrompt
        );
        Assert.DoesNotContain(
            events,
            ev => ev.Address == mainHead
        );
        Assert.Null(events[0].Parent);
        for (int i = 1; i < events.Count; i++) {
            Assert.Equal(events[i - 1].Address, events[i].Parent);
        }
        Assert.Equal(
            result.EventCount,
            result.Diagnostics.RepositoryEventReadCount
        );
    }

    [Fact]
    public void PreVisitorRefMutation_DoesNotChangeCapturedSnapshot() {
        string path = NewPath();
        EventAddress originalHead;
        RefId originalRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            originalRef = created.BranchRefId;
            originalHead =
                created.InspectExecutionBoundary().Head!.Value;
        }

        EventAddress? advancedHead = null;
        var originalEvents =
            new List<SessionJournalAuditEvent>();
        SessionJournalAuditScanResult originalResult;
        var source = new TestContextCandidateSource();
        using (var readOnly =
               SessionJournalEngine.OpenReadOnlyForTest(
                   path,
                   CreateRuntime(
                       new TextCompletionClient(),
                       source
                   ),
                   new SessionJournalTestHooks(
                       AfterAuditSnapshotValidated:
                           auditJournal => {
                               // EventJournal currently holds an exclusive
                               // read handle. Release only this test-owned
                               // handle after the snapshot is fully
                               // materialized, then emulate an external
                               // writer moving the same exact ref.
                               auditJournal.Dispose();
                               using var writer =
                                   SessionJournalEngine.Open(path);
                               Assert.Equal(
                                   originalHead,
                                   writer
                                       .InspectExecutionBoundary()
                                       .Head
                               );
                               Assert.Equal(
                                   originalRef,
                                   writer.BranchRefId
                               );
                               advancedHead =
                                   writer.AppendSystemPromptSetup(
                                       "system-after-snapshot"
                                   );
                           }
                   )
               )) {
            originalResult = readOnly.ScanCheckedAuditEvents(
                auditEvent => {
                    Assert.NotNull(advancedHead);
                    originalEvents.Add(auditEvent);
                }
            );
        }

        Assert.NotNull(advancedHead);
        Assert.Equal(originalRef, originalResult.BranchRefId);
        Assert.Equal(
            originalHead,
            originalResult.CapturedHead
        );
        Assert.Equal(
            SessionEventKind.SessionCreated,
            originalResult.ExecutionStateAtCapturedHead.HeadKind
        );
        Assert.Equal(3, originalResult.EventCount);
        Assert.Equal(3, originalEvents.Count);
        Assert.DoesNotContain(
            originalEvents,
            ev => ev.Address == advancedHead
        );

        var advancedEvents =
            new List<SessionJournalAuditEvent>();
        using var reopened =
            SessionJournalEngine.OpenReadOnly(path);
        SessionJournalAuditScanResult advancedResult =
            reopened.ScanCheckedAuditEvents(advancedEvents.Add);

        Assert.Equal(advancedHead, advancedResult.CapturedHead);
        Assert.Equal(4, advancedResult.EventCount);
        Assert.Equal(
            SessionEventKind.SystemPromptSetup,
            advancedResult.ExecutionStateAtCapturedHead.HeadKind
        );
        Assert.Equal(
            advancedHead,
            advancedEvents[^1].Address
        );
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("hint")]
    [InlineData("schema")]
    public void CorruptHeaderOrSchema_FailsBeforeVisiting(
        string corruption
    ) {
        string path = NewPath();
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            uint kind = corruption == "kind"
                ? 999u
                : (uint)SessionEventKind.RuntimeConfigSetup;
            AddressHint hint = corruption == "hint"
                ? new AddressHint(1)
                : default;
            byte[] payload = Encoding.UTF8.GetBytes(
                corruption == "schema"
                    ? """
                      {"v":1,"body":{"modelId":"model-A","completionSurfaceId":"surface-A","schema":"atelia.session-journal.trunk.v1"}}
                      """
                    : """
                      {"v":2,"body":{"modelId":"model-A","completionSurfaceId":"surface-A","schema":"atelia.session-journal.trunk.v1","derivedContext":{"nthPrevious":0}}}
                      """
            );
            EventAddress head = journal.AppendEventFrame(
                parent: null,
                payload,
                opaqueEventKind: kind,
                hint
            ).Unwrap();
            _ = journal.CreateBranch("main", head).Unwrap();
        }

        var visited = new List<SessionJournalAuditEvent>();
        using var engine =
            SessionJournalEngine.OpenReadOnly(path);
        Assert.ThrowsAny<Exception>(
            () => engine.ScanCheckedAuditEvents(visited.Add)
        );
        Assert.Empty(visited);
    }

    [Fact]
    public async Task ValidPreparedHead_CapturesFullExecutionState() {
        (
            string path,
            EventAddress prepared
        ) = await CreatePreparedHeadAsync();

        using var engine =
            SessionJournalEngine.OpenReadOnly(path);
        SessionJournalAuditScanResult result =
            engine.ScanCheckedAuditEvents(_ => { });

        Assert.Equal(prepared, result.CapturedHead);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletionDispatch,
            result.ExecutionStateAtCapturedHead.Phase
        );
        Assert.Equal(
            SessionEventKind.CompletionRequestPrepared,
            result.ExecutionStateAtCapturedHead.HeadKind
        );
        Assert.Equal(
            prepared,
            result.ExecutionStateAtCapturedHead
                .PendingRequestPreparedAddress
        );
        Assert.False(string.IsNullOrWhiteSpace(
            result.ExecutionStateAtCapturedHead
                .ActiveCorrelationId
        ));
    }

    [Fact]
    public async Task PreparedCommitmentCorruption_FailsBeforeVisiting() {
        (
            string path,
            EventAddress prepared
        ) = await CreatePreparedHeadAsync();
        RewritePrepared(
            path,
            prepared,
            body => body with {
                Commitment = body.Commitment with {
                    Sha256 = new string('0', 64)
                }
            }
        );

        var visited = new List<SessionJournalAuditEvent>();
        using var engine =
            SessionJournalEngine.OpenReadOnly(path);
        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => engine.ScanCheckedAuditEvents(visited.Add)
            );

        Assert.Contains(
            "commitment",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Empty(visited);
    }

    [Fact]
    public async Task PreparedForeignRawStart_FailsIndexedParentProof() {
        (
            string path,
            EventAddress prepared
        ) = await CreatePreparedHeadAsync();
        EventAddress orphan;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            orphan = journal.AppendEventFrame(
                parent: null,
                Encoding.UTF8.GetBytes(
                    """
                    {"v":2,"body":{"modelId":"orphan","completionSurfaceId":"orphan","schema":"atelia.session-journal.trunk.v1","derivedContext":{"nthPrevious":0}}}
                    """
                ),
                opaqueEventKind:
                    (uint)SessionEventKind.RuntimeConfigSetup
            ).Unwrap();
        }
        RewritePrepared(
            path,
            prepared,
            body => body with {
                Plan = body.Plan with {
                    RawStartExclusive = orphan
                }
            }
        );

        using var engine =
            SessionJournalEngine.OpenReadOnly(path);
        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => engine.ScanCheckedAuditEvents(_ => { })
            );

        Assert.Contains(
            "is not an ancestor",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ManyPrepared_UsesOneRepositoryReadPerCapturedEvent() {
        const int turnCount = 12;
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new TextCompletionClient();
        using (var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-A",
                    "system-A",
                    "surface-A"
                )
            ),
            CreateRuntime(client, source)
        )) {
            await CoherentArtifactSetTestFixture
                .ActivateAtCurrentHeadAsync(
                    path,
                    engine,
                    source,
                    fixtureId: "audit-many-prepared"
                );
            for (int i = 0; i < turnCount; i++) {
                _ = await engine.SendAsync(
                    $"turn-{i}",
                    CancellationToken.None
                );
            }
        }

        int visitedCount = 0;
        using var readOnly =
            SessionJournalEngine.OpenReadOnly(path);
        SessionJournalAuditScanResult result =
            readOnly.ScanCheckedAuditEvents(_ => visitedCount++);

        Assert.Equal(result.EventCount, visitedCount);
        Assert.Equal(
            result.EventCount,
            result.Diagnostics.CapturedEventCount
        );
        Assert.Equal(
            result.EventCount,
            result.Diagnostics.RepositoryEventReadCount
        );
        Assert.Equal(
            turnCount,
            result.Diagnostics.PreparedReconstructionCount
        );
        Assert.True(result.Diagnostics.IndexedEventLookupCount > 0);
        Assert.Equal(turnCount, client.CallCount);
    }

    [Fact]
    public async Task MixedPreparedVersions_CheckedAuditReconstructsWithoutRewrite() {
        string path = NewPath();
        PreparedV6Fixture.MixedWriterRepository fixture =
            await PreparedV6Fixture.CreateMixedWriterRepositoryAsync(
                path
            );
        Assert.Equal(
            new[] { 5, 6, 5 },
            fixture.PreparedBodySchemaVersions.ToArray()
        );
        PreparedV6Fixture.PreparedRawRangeEvidence raw =
            PreparedV6Fixture.ReadPreparedRawRange(
                path,
                fixture.PreparedAddresses[^1]
            );
        string before =
            PreparedV6Fixture.ComputeRepositoryTreeDigest(path);

        var events = new List<SessionJournalAuditEvent>();
        SessionJournalAuditScanResult result;
        using (var readOnly =
               SessionJournalEngine.OpenReadOnly(path)) {
            result = readOnly.ScanCheckedAuditEvents(events.Add);
        }

        SessionJournalAuditEvent[] preparedEvents = [
            .. events.Where(static item =>
                item.Kind
                == SessionEventKind.CompletionRequestPrepared
            )
        ];
        Assert.Equal(
            new[] { 5, 6, 5 },
            preparedEvents.Select(static item =>
                item.BodySchemaVersion
            ).ToArray()
        );
        Assert.Equal(
            3,
            result.Diagnostics.PreparedReconstructionCount
        );

        Assert.Equal(5, raw.BodySchemaVersion);
        string actualRawRangeHash = SessionRawRangeHasher.Compute(
            raw.Manifest.Plan.RawStartExclusive,
            raw.RawEndInclusive,
            raw.Entries
        );
        Assert.Equal(
            raw.Manifest.Plan.RawRangeSha256,
            actualRawRangeHash
        );
        SessionRawRangeHashEntry v6Entry = Assert.Single(
            raw.Entries.Where(static entry =>
                entry.EventKind
                    == (uint)SessionEventKind
                        .CompletionRequestPrepared
                && entry.BodySchemaVersion == 6
            )
        );
        SessionRawRangeHashEntry[] wrongVersionEntries = [
            .. raw.Entries.Select(entry =>
                entry == v6Entry
                    ? entry with { BodySchemaVersion = 5 }
                    : entry
            )
        ];
        Assert.NotEqual(
            actualRawRangeHash,
            SessionRawRangeHasher.Compute(
                raw.Manifest.Plan.RawStartExclusive,
                raw.RawEndInclusive,
                wrongVersionEntries
            )
        );

        Assert.Equal(
            before,
            PreparedV6Fixture.ComputeRepositoryTreeDigest(path)
        );
    }

    [Fact]
    public void CachedFrame_DisposeOwnsLogicalPayloadLease() {
        string path = NewPath();
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        EventAddress address = journal.AppendEventFrame(
            parent: null,
            new byte[] { 1, 2, 3, 4 },
            opaqueEventKind:
                (uint)SessionEventKind.ObservationAccepted
        ).Unwrap();
        SessionJournalCachedEvent cached =
            ReadCachedEvent(journal, address);
        var reader = new SessionJournalEventReader(
            journal,
            new Dictionary<
                EventAddress,
                SessionJournalCachedEvent
            > {
                [address] = cached
            },
            cacheOnly: true
        );

        SessionJournalEventFrame lease =
            reader.ReadEvent(address).Unwrap();
        Assert.Equal(4, ReadPayloadLength(lease));
        Assert.Equal(
            4,
            reader.CapturePayloadLifetimeDiagnostics()
                .CurrentLiveLogicalPayloadBytes
        );

        lease.Dispose();
        Assert.Equal(
            0,
            reader.CapturePayloadLifetimeDiagnostics()
                .CurrentLiveLogicalPayloadBytes
        );
        Assert.Throws<ObjectDisposedException>(
            () => ReadPayloadLength(lease)
        );

        lease.Dispose();
        Assert.Equal(
            0,
            reader.CapturePayloadLifetimeDiagnostics()
                .CurrentLiveLogicalPayloadBytes
        );
    }

    [Fact]
    public void CacheOnlyMiss_PerformsNoRepositoryRead() {
        string path = NewPath();
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        EventAddress cachedAddress = journal.AppendEventFrame(
            parent: null,
            new byte[] { 1 },
            opaqueEventKind:
                (uint)SessionEventKind.ObservationAccepted
        ).Unwrap();
        EventAddress missingAddress = journal.AppendEventFrame(
            cachedAddress,
            new byte[] { 2 },
            opaqueEventKind:
                (uint)SessionEventKind.ObservationAccepted
        ).Unwrap();
        SessionJournalCachedEvent cached =
            ReadCachedEvent(journal, cachedAddress);
        var reader = new SessionJournalEventReader(
            journal,
            new Dictionary<
                EventAddress,
                SessionJournalCachedEvent
            > {
                [cachedAddress] = cached
            },
            cacheOnly: true
        );

        _ = Assert.Throws<InvalidDataException>(
            () => reader.ReadEventHeaderPreview(missingAddress)
        );
        _ = Assert.Throws<InvalidDataException>(
            () => reader.ReadEvent(missingAddress)
        );

        SessionJournalReaderStorageDiagnostics diagnostics =
            reader.CaptureStorageDiagnostics();
        Assert.Equal(0, diagnostics.StorageHeaderPreviewReadCount);
        Assert.Equal(0, diagnostics.StoragePayloadReadCount);
        Assert.Equal(0, diagnostics.CachedHeaderReadCount);
        Assert.Equal(0, diagnostics.CachedPayloadReadCount);
        Assert.Equal(
            0,
            reader.CapturePayloadLifetimeDiagnostics()
                .CurrentLiveLogicalPayloadBytes
        );
    }

    [Fact]
    public void WritableEngine_RejectsOfflineOnlyAuditScan() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                () => engine.ScanCheckedAuditEvents(_ => { })
            );
        Assert.Contains(
            "read-only",
            error.Message,
            StringComparison.Ordinal
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for audit fixtures.
            }
        }
    }

    private async Task<(string Path, EventAddress Prepared)>
        CreatePreparedHeadAsync() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            ),
            CreateRuntime(new TextCompletionClient(), source),
            new SessionJournalTestHooks(
                SessionJournalFailpoint
                    .AfterRequestPreparedCommitted
            )
        );
        await CoherentArtifactSetTestFixture
            .ActivateAtCurrentHeadAsync(
                path,
                engine,
                source,
                fixtureId: "audit-corrupt-prepared"
            );
        _ = await Assert.ThrowsAsync<
            SessionJournalFailpointException
        >(
            () => engine.SendAsync(
                "prepare",
                CancellationToken.None
            )
        );
        EventAddress prepared =
            engine.InspectExecutionBoundary().Head!.Value;
        return (path, prepared);
    }

    private static void RewritePrepared(
        string path,
        EventAddress prepared,
        Func<
            CompletionRequestPreparedBody,
            CompletionRequestPreparedBody
        > rewrite
    ) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        using EventFrame preparedFrame =
            journal.ReadEvent(prepared).Unwrap();
        EventAddress parent = preparedFrame.Header.Parent
            ?? throw new InvalidDataException(
                "Prepared fixture has no raw parent."
            );
        var body = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                preparedFrame.Payload,
                out _
            )
        );
        Assert.True(
            journal.MoveRef(main, prepared, parent).Unwrap()
        );
        _ = journal.CommitToRef(
            main,
            parent,
            SessionEventCodec.Encode(
                SessionEventKind.CompletionRequestPrepared,
                rewrite(body)
            ),
            opaqueEventKind:
                (uint)SessionEventKind.CompletionRequestPrepared,
            hint: default
        ).Unwrap();
    }

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        TestContextCandidateSource source
    ) => new(
        client,
        CompletionTarget:
            new SessionCompletionTargetIdentity(
                "audit-test",
                "test",
                "audit-test-v1",
                "audit-adapter-v1"
            ),
        ContextCandidateSource: source
    );

    private static SessionJournalCachedEvent ReadCachedEvent(
        EventJournal.EventJournal journal,
        EventAddress address
    ) {
        using EventFrame frame =
            journal.ReadEvent(address).Unwrap();
        return new SessionJournalCachedEvent(
            address,
            frame.Header,
            frame.Payload.ToArray()
        );
    }

    private static int ReadPayloadLength(
        SessionJournalEventFrame frame
    ) => frame.Payload.Length;

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-audit-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class TextCompletionClient : ICompletionClient {
        public string Name => "audit-test";
        public string ApiSpecId => "audit-test-v1";
        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{CallCount}")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }
}

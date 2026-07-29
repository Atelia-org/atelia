using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionContextCandidateProviderRouteTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "candidate-test-host",
        "candidate-test-implementations-v1",
        "candidate-test-capabilities-v1"
    );

    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned temporary directories.
            }
        }
    }

    [Fact]
    public async Task MissingCandidate_FailsBeforeObservation_ThenSendCanRetry() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        using (var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        )) {
            TestContextCandidateFixture fixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(engine);

            SessionJournalNotReadyException error =
                await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                    () => engine.SendAsync("remember this", CancellationToken.None)
                );

            Assert.Equal(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                error.Reason
            );
            SessionExecutionRecovery waiting =
                engine.ResolveExecutionTail();
            Assert.Equal(
                SessionExecutionPhase.Idle,
                waiting.State.Phase
            );
            Assert.Equal(1, source.SelectionCount);
            Assert.Equal(
                fixture.Anchor,
                source.Requests[0].CompletionBoundary
            );
            Assert.Equal(0, client.Calls);

            source.Candidate = fixture.Candidate;
            TurnResult outcome = await engine.SendAsync(
                "remember this",
                CancellationToken.None
            );

            Assert.Equal(
                "done",
                outcome.Message.GetFlattenedText()
            );
            Assert.Equal(3, source.SelectionCount);
            Assert.Equal(1, client.Calls);
        }
    }

    [Theory]
    [InlineData(
        SessionContextCandidateSelectionStatus.ExactPublishedSetInvalid,
        SessionJournalNotReadyReason.ContextCandidateInvalid
    )]
    [InlineData(
        SessionContextCandidateSelectionStatus.StoreUnavailable,
        SessionJournalNotReadyReason.ContextStoreUnavailable
    )]
    public async Task TypedSelectionUnavailable_FailsBeforeObservation(
        SessionContextCandidateSelectionStatus status,
        SessionJournalNotReadyReason expectedReason
    ) {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource {
            ForcedStatus = status,
            SelectionDetail = "typed selection failure"
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        _ = ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        EventAddress head =
            engine.InspectExecutionBoundary().Head!.Value;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => engine.SendAsync(
                    "must remain ephemeral",
                    CancellationToken.None
                )
            );

        Assert.Equal(expectedReason, error.Reason);
        Assert.Contains("typed selection failure", error.Message);
        Assert.Equal(head, engine.InspectExecutionBoundary().Head);
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(0, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task InvalidCanonicalRequestByteGuard_FailsBeforeObservationOrSelection() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        SessionRuntime runtime = CreateRuntime(client, source) with {
            MaximumCanonicalRequestBytes = 0
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            runtime
        );
        EventAddress head = engine.ResolveExecutionTail().Head!.Value;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => engine.SendAsync("must not persist", CancellationToken.None)
        );

        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(0, source.SelectionCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void InvalidDurableOrdinal_FailsCreate() {
        string path = NewJournalPath();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SessionJournalEngine.Create(
                path,
                CreateOptions() with {
                    DerivedContextNthPrevious = -1
                }
            )
        );
    }

    [Fact]
    public async Task DivergentCandidateAnchor_FailsRawAuthorityBeforeObservation() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        TestContextCandidateFixture fixture;
        using (var setup = SessionJournalEngine.Create(
            path,
            CreateOptions()
        )) {
            fixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(setup);
        }
        EventAddress divergent;
        using (var journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            journal.CreateBranch("off", fixture.Anchor).Unwrap();
            divergent = journal.CommitToRef(
                "off",
                fixture.Anchor,
                SessionEventCodec.Encode(
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("off-main")
                ),
                opaqueEventKind:
                    (uint)SessionEventKind.ObservationAccepted,
                hint: default
            ).Unwrap().EventAddress;
        }
        source.Candidate = fixture.Candidate with {
            SetAdmissionAnchor = divergent,
            Contributions = new[] {
                ContextCandidateTestFixture.Contribution(
                    ContextHeaderCarrier.Observation,
                    "fixture.world-understanding",
                    "divergent memory",
                    divergent
                )
            }
        };
        using var engine = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, source)
        );
        EventAddress head =
            engine.ResolveExecutionTail().Head!.Value;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SendAsync(
                "must not persist",
                CancellationToken.None
            )
        );

        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(0, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ForgedAnchorSetupHash_FailsRawAuthorityBeforeObservation() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate with {
            AnchorSetups = fixture.Candidate.AnchorSetups with {
                SystemPrompt =
                    fixture.Candidate.AnchorSetups.SystemPrompt with {
                        PayloadSha256 = new string('0', 64)
                    }
            }
        };
        EventAddress head =
            engine.ResolveExecutionTail().Head!.Value;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SendAsync(
                "must not persist",
                CancellationToken.None
            )
        );

        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(0, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ProjectedCanonicalRequestByteGuardFailure_DoesNotAppendObservation() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                MaximumCanonicalRequestBytes = 1
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;
        EventAddress head =
            engine.InspectExecutionBoundary().Head!.Value;
        int eventCount =
            engine.ReadCurrentLineageHeaders()
                .HeadToRoot.Count;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<
                SessionJournalNotReadyException
            >(
                () => engine.SendAsync(
                    "must remain ephemeral",
                    CancellationToken.None
                )
            );

        Assert.Equal(
            SessionJournalNotReadyReason
                .ContextCandidateUnavailable,
            error.Reason
        );
        Assert.Contains(
            SessionJournalEngine.CanonicalRequestBytesMetricId,
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("actualBytes=", error.Message);
        Assert.Contains("maximumBytes=1", error.Message);
        Assert.Equal(head, engine.InspectExecutionBoundary().Head);
        Assert.Equal(
            eventCount,
            engine.ReadCurrentLineageHeaders()
                .HeadToRoot.Count
        );
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(1, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task EmptyLineageBootstrap_CommitsPreparedV5AndReopensWithoutDerivedSource() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        SessionRuntime runtime = CreateRuntime(client, source);
        using (var engine =
               SessionJournalEngine.CreateForTest(
                   path,
                   CreateOptions(),
                   runtime,
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint
                           .AfterRequestPreparedCommitted
                   )
               )) {
            engine.AppendRuntimeConfigSetup(
                CreateOptions().ToRuntimeConfiguration()
            );
            engine.AppendSystemPromptSetup("updated bootstrap prompt");
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<
                    SessionJournalFailpointException
                >(
                    () => engine.SendAsync(
                        "bootstrap",
                        CancellationToken.None
                    )
                );
            Assert.Equal(
                SessionJournalFailpoint
                    .AfterRequestPreparedCommitted,
                error.Failpoint
            );
            Assert.Equal(2, source.SelectionCount);
            Assert.Equal(0, source.MaterializationCount);
        }
        EventAddress prepared = Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
        CompletionRequestPreparedBody manifest =
            ReadBody<CompletionRequestPreparedBody>(
                path,
                prepared,
                SessionEventKind.CompletionRequestPrepared
            );
        Assert.Empty(manifest.Plan.ExactContextInputs);

        client.Enqueue(Terminal("resumed"));
        using var reopened = SessionJournalEngine.Open(
            path,
            runtime with {
                ContextCandidateSource = null,
                ContextLifecycle = null
            }
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(
            CancellationToken.None
        );

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed", outcome.Message!.GetFlattenedText());
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, source.SelectionCount);
    }

    [Fact]
    public async Task EmptyLineageBootstrap_ObservationCrashReopensExactFirstObservation() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        SessionRuntime runtime = CreateRuntime(client, source);
        using (var engine =
               SessionJournalEngine.CreateForTest(
                   path,
                   CreateOptions(),
                   runtime,
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint
                           .AfterObservationCommitted
                   )
               )) {
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<
                    SessionJournalFailpointException
                >(
                    () => engine.SendAsync(
                        "crash after observation",
                        CancellationToken.None
                    )
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterObservationCommitted,
                error.Failpoint
            );
            Assert.Equal(
                SessionExecutionPhase.AwaitingAgentAction,
                engine.InspectExecutionBoundary().Phase
            );
        }

        client.Enqueue(Terminal("resumed"));
        using (var reopened = SessionJournalEngine.Open(path, runtime)) {
            ResumeOutcome outcome = await reopened.ResumeAsync(
                CancellationToken.None
            );

            Assert.True(outcome.Advanced);
            Assert.Equal(
                "resumed",
                outcome.Message!.GetFlattenedText()
            );
            Assert.Equal(1, client.Calls);
        }
        Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyImportEmptyLineage_RejectsBootstrapBeforeLifecycle(
        bool observationAlreadyPersisted
    ) {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        var lifecycle = new TestContextLifecycle();
        SessionRuntime runtime = CreateRuntime(client, source) with {
            ContextLifecycle = lifecycle
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions() with {
                Origin = SessionCreationOrigin.LegacyImport
            }
        );
        if (observationAlreadyPersisted) {
            engine.AppendObservation("imported pending observation");
        }
        engine.UseRuntime(runtime);
        EventAddress head = engine.InspectExecutionBoundary().Head!.Value;
        int eventCount =
            engine.ReadCurrentLineageHeaders().HeadToRoot.Count;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => observationAlreadyPersisted
                    ? engine.ResumeAsync(CancellationToken.None)
                    : engine.SendAsync(
                        "must remain ephemeral",
                        CancellationToken.None
                    )
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            error.Reason
        );
        Assert.Contains(
            "native",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(0, lifecycle.InvocationCount);
        Assert.Equal(0, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
        Assert.Equal(head, engine.InspectExecutionBoundary().Head);
        Assert.Equal(
            eventCount,
            engine.ReadCurrentLineageHeaders().HeadToRoot.Count
        );
    }

    [Fact]
    public async Task CanonicalRequestByteGuard_AllowsExactBootstrapBoundary() {
        const string observation = "exact byte boundary";
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        int exactBytes = SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest(
                "model-A",
                "system-A",
                [new ObservationMessage(observation)],
                [],
                MaxTokens: 256
            )
        ).Length;
        SessionRuntime runtime = CreateRuntime(client, source) with {
            MaximumCanonicalRequestBytes = exactBytes
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            runtime
        );

        TurnResult result = await engine.SendAsync(
            observation,
            CancellationToken.None
        );

        Assert.Equal("done", result.Message.GetFlattenedText());
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task PreparedHistoryWithDeletedDerivedLineage_CannotBootstrapAgain() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("first"));
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        source.Candidate = ContextCandidateTestFixture
            .CreateAtCurrentHead(engine)
            .Candidate;
        _ = await engine.SendAsync(
            "first observation",
            CancellationToken.None
        );
        EventAddress settledHead =
            engine.InspectExecutionBoundary().Head!.Value;
        int eventCount =
            engine.ReadCurrentLineageHeaders().HeadToRoot.Count;
        source.Candidate = null;
        source.IsEmptyLineage = true;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => engine.SendAsync(
                    "must not bootstrap twice",
                    CancellationToken.None
                )
            );

        Assert.Contains(
            nameof(SessionEventKind.AgentActionProduced),
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(settledHead, engine.InspectExecutionBoundary().Head);
        Assert.Equal(
            eventCount,
            engine.ReadCurrentLineageHeaders().HeadToRoot.Count
        );
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task PublishedButUnusedCandidateCanDisappearBeforeFreshBootstrap() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("bootstrapped"));
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        source.Candidate = ContextCandidateTestFixture
            .CreateAtCurrentHead(engine)
            .Candidate;
        source.Candidate = null;
        source.IsEmptyLineage = true;

        TurnResult result = await engine.SendAsync(
            "fresh after deletion",
            CancellationToken.None
        );

        Assert.Equal(
            "bootstrapped",
            result.Message.GetFlattenedText()
        );
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task EmptyLineageToolResultContinuation_IsNotFreshBootstrap() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(ToolCall("lookup", "call-1"));
        var source = new TestContextCandidateSource();
        var tool = new RecordingTool("lookup");
        SessionRuntime runtime = CreateRuntime(
            client,
            source,
            new ToolRegistry([tool]).CreateSession()
        );
        using (var engine =
               SessionJournalEngine.CreateForTest(
                   path,
                   CreateOptions(),
                   runtime,
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint
                           .AfterToolResultCommitted
                   )
               )) {
            source.Candidate = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine)
                .Candidate;
            _ = await Assert.ThrowsAsync<
                SessionJournalFailpointException
            >(
                () => engine.SendAsync(
                    "use tool",
                    CancellationToken.None
                )
            );
        }
        source.Candidate = null;
        source.IsEmptyLineage = true;
        using var reopened = SessionJournalEngine.Open(path, runtime);
        EventAddress toolResultHead =
            reopened.InspectExecutionBoundary().Head!.Value;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

        Assert.Contains(
            "active first ObservationAccepted",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(
            toolResultHead,
            reopened.InspectExecutionBoundary().Head
        );
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RuntimeSetupUpdate_ReopenSelectsDurableExactOrdinal() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        SessionContextCandidate older;
        SessionContextCandidate newer;
        using (var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        )) {
            older = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "older")
                .Candidate;
            engine.AppendObservation("prior turn");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("prior answer")
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-A"
                )
            );
            newer = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "newer")
                .Candidate;
            engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration(
                    "model-A",
                    "surface-A",
                    SessionJournalDefaults.Schema,
                    new SessionDerivedContextConfiguration(1)
                )
            );
        }
        source.Candidates = [
            newer,
            older
        ];
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, source)
        )) {
            _ = await reopened.SendAsync(
                "choose older",
                CancellationToken.None
            );
        }

        Assert.All(
            source.MaterializedHandles,
            handle => Assert.Equal(
                "test-candidate-1",
                handle
            )
        );
    }

    [Fact]
    public async Task LifecyclePublicationPrecedesDurableOrdinalSelection() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        var lifecycle = new TestContextLifecycle();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions() with {
                DerivedContextNthPrevious = 1
            },
            CreateRuntime(client, source) with {
                ContextLifecycle = lifecycle
            }
        );
        SessionContextCandidate older =
            ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "older")
                .Candidate;
        SessionContextCandidate newer = older with {
            Contributions = older.Contributions.Select(
                contribution => contribution with {
                    ExactText = contribution.ExactText + " newer",
                    ContentSha256 =
                        SessionContextContributionHasher.ComputeSha256(
                            contribution.ExactText + " newer"
                        )
                }
            ).ToArray()
        };
        source.Candidates = [older];
        lifecycle.OnPrepare = (_, _) =>
            source.Candidates = [newer, older];

        _ = await engine.SendAsync(
            "select after lifecycle",
            CancellationToken.None
        );

        Assert.NotEmpty(source.MaterializedHandles);
        Assert.All(
            source.MaterializedHandles,
            handle => Assert.Equal("test-candidate-1", handle)
        );
    }

    [Fact]
    public async Task ExactCandidateBudgetFailureDoesNotFallBack() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions() with {
                DerivedContextNthPrevious = 1
            },
            CreateRuntime(client, source)
        );
        TestContextCandidateFixture oldBase =
            ContextCandidateTestFixture.CreateAtCurrentHead(
                engine,
                "older"
            );
        string oversizedMemory = new('x', 6_000);
        SessionContextCandidate older =
            ContextCandidateTestFixture.CreateCandidate(
                engine,
                oldBase.Anchor,
                engine.ResolveGoverningSetup(
                    oldBase.Anchor
                ),
                ContextCandidateTestFixture.Contribution(
                    ContextHeaderCarrier.Observation,
                    "fixture.large",
                    oversizedMemory,
                    oldBase.Anchor
                )
            );
        engine.AppendObservation("prior turn");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("prior answer")
            ]),
            new CompletionDescriptor(
                "import",
                "v1",
                "model-A"
            )
        );
        SessionContextCandidate newer =
            ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "newer")
                .Candidate;
        source.Candidates = [newer, older];
        engine.UseRuntime(
            CreateRuntime(client, source) with {
                MaximumCanonicalRequestBytes = 1_000
            }
        );

        await Assert.ThrowsAsync<SessionJournalNotReadyException>(
            () => engine.SendAsync(
                "fit total budget",
                CancellationToken.None
            )
        );

        Assert.All(
            source.MaterializedHandles,
            handle => Assert.Equal("test-candidate-1", handle)
        );
        Assert.DoesNotContain(
            "test-candidate-0",
            source.MaterializedHandles
        );
    }

    [Fact]
    public async Task ObservationAndToolContinuations_SelectAtEveryExactBoundary() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(ToolCall("lookup", "call-1"));
        client.Enqueue(ToolCall("lookup", "call-2"));
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        var tool = new RecordingTool("lookup");
        using (var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            )
        )) {
            TestContextCandidateFixture fixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(engine);
            source.Candidate = fixture.Candidate;

            TurnResult result = await engine.SendAsync(
                "use the tool twice",
                CancellationToken.None
            );

            Assert.Equal("done", result.Message.GetFlattenedText());
            Assert.Equal(4, source.SelectionCount);
        }
        EventAddress observation = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.ObservationAccepted)
        );
        EventAddress[] toolResults = ReadAddressesByKind(
            path,
            SessionEventKind.ToolResultObserved
        );
        Assert.Equal(2, toolResults.Length);
        Assert.Equal(
            new[] { observation, toolResults[0], toolResults[1] },
            source.Requests.Skip(1).Select(
                static request =>
                    request.CompletionBoundary
            )
        );
        Assert.Equal(3, client.Calls);
        Assert.Equal(2, tool.Calls);
    }

    [Fact]
    public async Task SendAndToolContinuation_InvokeLifecycleOnlyAtSafeUnpreparedBoundaries() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(ToolCall("lookup", "call-1"));
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        var lifecycle = new TestContextLifecycle();
        var tool = new RecordingTool("lookup");
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            ) with {
                ContextLifecycle = lifecycle
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;

        _ = await engine.SendAsync(
            "use one tool",
            CancellationToken.None
        );

        Assert.Collection(
            lifecycle.Requests,
            idle => {
                Assert.Equal(SessionExecutionPhase.Idle, idle.Phase);
                Assert.Equal("use one tool", idle.PendingObservation);
            },
            observation => {
                Assert.Equal(
                    SessionExecutionPhase.AwaitingAgentAction,
                    observation.Phase
                );
                Assert.Null(observation.PendingObservation);
            },
            toolResult => {
                Assert.Equal(
                    SessionExecutionPhase.AwaitingAgentAction,
                    toolResult.Phase
                );
                Assert.Null(toolResult.PendingObservation);
            }
        );
        Assert.Equal(6, source.SelectionCount);
    }

    [Fact]
    public async Task LifecycleRawMutationIsRejectedByPostCallbackHeadCas() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        var lifecycle = new TestContextLifecycle {
            OnPrepare = static (engine, _) =>
                engine.AppendObservation("intruder")
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                ContextLifecycle = lifecycle
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SendAsync(
                    "must not be appended",
                    CancellationToken.None
                )
            );

        Assert.Contains(
            "stale",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(1, lifecycle.InvocationCount);
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(0, client.Calls);
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            engine.ResolveExecutionTail().State.Phase
        );
    }

    [Fact]
    public async Task CandidateWithToolActionAnchor_FailsOnPublicResumeRoute() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        var tool = new RecordingTool("lookup");
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            )
        );
        engine.AppendObservation("start imported tool");
        EventAddress toolAction = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        await Assert.ThrowsAsync<SessionJournalNotReadyException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(toolAction);
        source.Candidate = ContextCandidateTestFixture.CreateCandidate(
            engine,
            toolAction,
            setup,
            ContextCandidateTestFixture.Contribution(
                ContextHeaderCarrier.Action,
                "fixture.autobiography",
                "unsafe action anchor",
                toolAction
            )
        );

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains(
            "not replay-safe",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(2, source.SelectionCount);
        Assert.Equal(0, client.Calls);
    }

    private static SessionCreateOptions CreateOptions()
        => new("model-A", "system-A", "surface-A");

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        ICoherentContextCandidateSource source,
        ToolSession? toolSession = null
    ) => new(
        client,
        toolSession,
        new SessionCompletionTargetIdentity(
            "candidate-test-connection",
            "test",
            "candidate-test-connection-v1",
            "candidate-test-adapter-v1"
        ),
        MaxTokens: 256,
        ToolRuntimeIdentity: ToolRuntimeIdentity,
        ContextCandidateSource: source
    );

    private static Func<CompletionRequest, CompletionResult> Terminal(string text)
        => request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text(text)]),
            Descriptor(request)
        );

    private static Func<CompletionRequest, CompletionResult> ToolCall(
        string toolName,
        string callId
    ) => request => new CompletionResult(
        new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall(toolName, callId, "{}"))
        ]),
        Descriptor(request)
    );

    private static CompletionDescriptor Descriptor(CompletionRequest request)
        => new("candidate-test-client", "candidate-test-api-v1", request.ModelId);

    private static EventAddress[] ReadAddressesByKind(
        string path,
        SessionEventKind kind
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName)
            .Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        return [
            .. journal.ReadChronologicalChain(head, checkedRead: true)
                .Unwrap()
                .Where(address =>
                    journal.ReadEventHeaderPreview(address)
                        .Unwrap()
                        .OpaqueEventKind == (uint)kind
                )
        ];
    }

    private static EventAddress[] ReadAllAddresses(string path) {
        using var journal =
            EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        return [
            .. journal.ReadChronologicalChain(
                head,
                checkedRead: true
            ).Unwrap()
        ];
    }

    private static T ReadBody<T>(
        string path,
        EventAddress address,
        SessionEventKind kind
    ) where T : class {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        return Assert.IsType<T>(
            SessionEventCodec.Decode(kind, frame.Payload.ToArray(), out _)
        );
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-context-provider-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class ScriptedClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = [];

        public string Name => "candidate-test-client";

        public string ApiSpecId => "candidate-test-api-v1";

        public int Calls { get; private set; }

        internal void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted response.");
            }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class RecordingTool(string name) : ITool {
        public ToolDefinition Definition { get; } =
            new(name, $"Tool {name}.", new ToolSchema.Object());

        public int Calls { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    $"tool result {Calls}"
                )
            );
        }
    }
}

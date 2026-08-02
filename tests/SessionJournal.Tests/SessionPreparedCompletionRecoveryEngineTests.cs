using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionPreparedCompletionRecoveryEngineTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "recovery-tool-host",
        "recovery-tool-implementations-v1",
        "recovery-tool-capabilities-v1"
    );
    private static readonly SessionCompletionTargetIdentity DefaultTarget = new(
        "recovery-connection",
        "test",
        "recovery-connection-v1",
        "recovery-adapter-v1"
    );

    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task ResumeAsync_DefaultRefuse_ValidatesPreparedButDoesNotMutateOrCallProvider() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        EventAddress prepared = await CreateUncertainAsync(
            path,
            CreateRuntime(client)
        );
        var recoverySource = new TestContextCandidateSource();
        var lifecycle = new TestContextLifecycle {
            Result = new(
                SessionContextLifecycleStatus.Unavailable,
                "must not run"
            )
        };
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client) with {
                ContextCandidateSource = recoverySource,
                ContextLifecycle = lifecycle
            }
        )) {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Contains("Refuse", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(prepared, ReadHead(path));
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted));
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, recoverySource.SelectionCount);
        Assert.Equal(0, lifecycle.InvocationCount);
    }

    [Fact]
    public async Task ResumeAsync_DefaultRefuse_RejectsCorruptPreparedBeforePolicyRefusal() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        EventAddress validPrepared = await CreatePreparedAsync(
            path,
            CreateRuntime(client)
        );
        EventAddress observation = ReadParent(path, validPrepared)!.Value;
        CompletionRequestPreparedBody validManifest =
            ReadBody<CompletionRequestPreparedBody>(
                path,
                validPrepared,
                SessionEventKind.CompletionRequestPrepared
            );
        CompletionRequestPreparedBody malformedManifest = validManifest with {
            Commitment = validManifest.Commitment with { Sha256 = new string('0', 64) }
        };
        EventAddress malformedPrepared;
        EventAddress malformedStarted;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            Assert.True(journal.MoveRef(main, validPrepared, observation).Unwrap());
            malformedPrepared = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                observation,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionRequestPrepared,
                    malformedManifest
                ),
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared,
                hint: default
            ).Unwrap().EventAddress;
            malformedStarted = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                malformedPrepared,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody()
                ),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted,
                hint: default
            ).Unwrap().EventAddress;
        }
        using (var reopened = SessionJournalEngine.Open(path, CreateRuntime(client))) {
            InvalidDataException inspectionError = Assert.Throws<InvalidDataException>(
                () => reopened.InspectRuntimeRecoveryRequirements()
            );
            InvalidDataException resumeError = await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Contains(
                "commitment",
                inspectionError.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                "commitment",
                resumeError.Message,
                StringComparison.OrdinalIgnoreCase
            );
        }

        Assert.Equal(malformedStarted, ReadHead(path));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ResumeAsync_PreCanceledPrepared_DoesNotAppendStartedOrCallProvider() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        EventAddress prepared = await CreatePreparedAsync(
            path,
            CreateRuntime(client)
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (var reopened = SessionJournalEngine.Open(path, CreateRuntime(client))) {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reopened.ResumeAsync(cancellation.Token)
            );
            SessionExecutionRecovery recovery = reopened.ResolveExecutionTail();
            Assert.Equal(prepared, recovery.Head);
            Assert.Null(recovery.State.ActiveCompletionAttemptAddress);
        }
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted)
        );
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ResumeAsync_RestartSuccess_AppendsNewAttemptAndBindsActionToIt() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreateUncertainAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "recovered"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
            Assert.True(outcome.Advanced);
            Assert.Equal("recovered", outcome.Message?.GetFlattenedText());
        }
        EventAddress restarted =
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted)[1];
        EventAddress action = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.AgentActionProduced)
        );
        Assert.Equal(prepared, ReadParent(path, restarted));
        Assert.Equal(restarted, ReadParent(path, action));
        Assert.Equal(1, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_RestartUsesPreparedRequestInsteadOfCurrentRuntimeParameters() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateUncertainAsync(
            path,
            CreateRuntime(sourceClient, maxTokens: 111)
        );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "reconstructed"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt,
                maxTokens: 999
            )
        )) {
            _ = await reopened.ResumeAsync(CancellationToken.None);
        }

        CompletionRequest request = Assert.Single(recoveryClient.Requests);
        Assert.Equal(111, request.MaxTokens);
    }

    [Fact]
    public async Task ResumeAsync_RestartKnownFailure_BindsFailureToRestartAttempt() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateUncertainAsync(path, CreateRuntime(sourceClient));
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("unused")]),
            Descriptor(request),
            termination: CompletionTermination.Failed("provider-failed", "known")
        ));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Equal(
                SessionExecutionPhase.TurnFailed,
                reopened.InspectExecutionBoundary().Phase
            );
        }

        EventAddress restarted =
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted)[1];
        EventAddress failureAddress = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptFailed)
        );
        Assert.Equal(restarted, ReadParent(path, failureAddress));
    }

    [Fact]
    public async Task PreparedRetryAndFailureDoNotAddHistoryUnits() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateUncertainAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("unused")]),
            Descriptor(request),
            termination:
                CompletionTermination.Failed("provider-failed")
        ));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy:
                    SessionUncertainCompletionRecoveryPolicy
                        .RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<
                SessionJournalTurnAbortedException
            >(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

            SessionHistoryPlanningWindow window =
                reopened.ReadHistoryPlanningWindow();
            SessionHistoryPlanningUnit unit =
                Assert.Single(window.Units);
            Assert.IsType<ObservationMessage>(unit.Message);
            Assert.Equal(
                1,
                Assert.Single(
                    window.ReplaySafeBoundaries,
                    boundary =>
                        boundary.Address
                        == reopened.ReadCurrentHead()!.Value
                ).CompletedUnitCount
            );
        }

        Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
        Assert.Equal(
            2,
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptStarted
            ).Length
        );
        Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptFailed
            )
        );
    }

    [Fact]
    public async Task ResumeAsync_RestartWithoutTools_ProviderToolCallDurablyFails() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateUncertainAsync(path, CreateRuntime(sourceClient));
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("unexpected", "call-1", "{}")
                )
            ]),
            Descriptor(request)
        ));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy:
                    SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            SessionJournalTurnAbortedException error =
                await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                    () => reopened.ResumeAsync(CancellationToken.None)
                );
            Assert.Equal(
                "atelia.host.unsupported-tool-call",
                error.Termination.ProviderReason
            );
            Assert.Equal(
                SessionExecutionPhase.TurnFailed,
                reopened.ResolveExecutionTail().State.Phase
            );

            ResumeOutcome settled =
                await reopened.ResumeAsync(CancellationToken.None);
            Assert.False(settled.Advanced);
        }

        Assert.Equal(
            2,
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptStarted
            ).Length
        );
        Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptFailed
            )
        );
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.AgentActionProduced)
        );
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.ToolExecutionStarted)
        );
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.ToolResultObserved)
        );
        Assert.Equal(1, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_RestartWithTools_CompletesToolLoopAndReturnsIdle() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        var sourceTool = new RecordingTool("lookup");
        SessionRuntime sourceRuntime = CreateRuntime(
            sourceClient,
            new ToolRegistry([sourceTool]).CreateSession()
        );
        _ = await CreateUncertainAsync(
            path,
            sourceRuntime
        );
        SessionContextCandidate candidate =
            Assert.IsType<TestContextCandidateSource>(
                sourceRuntime.ContextCandidateSource
            ).Candidate
            ?? throw new InvalidOperationException(
                "Prepared recovery fixture did not publish its context candidate."
            );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            Descriptor(request)
        ));
        recoveryClient.Enqueue(request => Success(request, "done"));
        var recoveryTool = new RecordingTool("lookup");
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                new ToolRegistry([recoveryTool]).CreateSession(),
                recoveryPolicy:
                    SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt,
                contextCandidate: candidate
            )
        )) {
            ResumeOutcome outcome =
                await reopened.ResumeAsync(CancellationToken.None);

            Assert.True(outcome.Advanced);
            Assert.Equal("done", outcome.Message?.GetFlattenedText());
            Assert.Equal(
                SessionExecutionPhase.Idle,
                reopened.ResolveExecutionTail().State.Phase
            );
        }

        Assert.Equal(2, recoveryClient.Calls);
        Assert.Equal(1, recoveryTool.Calls);
        Assert.NotNull(recoveryTool.LastContext);
        Assert.False(string.IsNullOrWhiteSpace(
            recoveryTool.LastContext!.OperationId
        ));
        EventAddress firstAction = ReadAddressesByKind(
            path,
            SessionEventKind.AgentActionProduced
        )[0];
        AgentActionProducedBody action = ReadBody<AgentActionProducedBody>(
            path,
            firstAction,
            SessionEventKind.AgentActionProduced
        );
        Assert.Equal(ToolRuntimeIdentity, action.ToolRuntimeIdentity);
        Assert.Equal(
            2,
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            ).Length
        );
        Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.ToolExecutionStarted)
        );
        Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.ToolResultObserved)
        );
    }

    [Fact]
    public async Task ResumeAsync_RestartFailpointThenReopen_AppendsSecondAuditableRestart() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreateUncertainAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        using (var firstRecovery = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted
            )
        )) {
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<SessionJournalFailpointException>(
                    () => firstRecovery.ResumeAsync(CancellationToken.None)
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted,
                error.Failpoint
            );
        }
        Assert.Equal(0, recoveryClient.Calls);

        recoveryClient.Enqueue(request => Success(request, "second restart"));
        using (var secondRecovery = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await secondRecovery.ResumeAsync(CancellationToken.None);
            Assert.True(outcome.Advanced);
        }

        EventAddress[] restarts =
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted);
        Assert.Equal(3, restarts.Length);
        Assert.Equal(prepared, restarts[0]);
        Assert.Equal(restarts[0], ReadParent(path, restarts[1]));
        Assert.Equal(restarts[1], ReadParent(path, restarts[2]));
    }

    [Fact]
    public async Task SendFailpoint_AfterProviderBeforeAction_CanRestartWithNewAttempt() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var candidateSource = new TestContextCandidateSource();
        client.Enqueue(request => Success(request, "uncertain first result"));
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client) with {
                ContextCandidateSource = candidateSource
            },
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionBeforeActionCommitted
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource,
                fixtureId: "provider-before-action"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
        }
        Assert.Equal(1, client.Calls);

        client.Enqueue(request => Success(request, "restarted result"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
            Assert.Equal("restarted result", outcome.Message?.GetFlattenedText());
        }
        Assert.Equal(2, client.Calls);
        Assert.Equal(
            2,
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted).Length
        );
    }

    [Theory]
    [InlineData("target")]
    [InlineData("client")]
    [InlineData("tools")]
    public async Task ResumeAsync_RuntimeDispatchMismatch_HasZeroMutationAndZeroProviderCall(
        string mismatch
    ) {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreatePreparedAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = mismatch == "client"
            ? new ScriptedClient("other-client", "other-api")
            : new ScriptedClient();
        SessionCompletionTargetIdentity target = mismatch == "target"
            ? DefaultTarget with { ConnectionFingerprint = "different" }
            : DefaultTarget;
        ToolSession? tools = mismatch == "tools"
            ? new ToolRegistry([new RecordingTool("unexpected")]).CreateSession()
            : null;
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                tools,
                target,
                SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
        }

        Assert.Equal(prepared, ReadHead(path));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted));
        Assert.Equal(0, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_UncertainRuntimeMismatch_DoesNotAppendReplacementStarted() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress started = await CreateUncertainAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                target: DefaultTarget with {
                    ConnectionFingerprint = "different-connection-v2"
                },
                recoveryPolicy:
                    SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Equal(started, reopened.ResolveExecutionTail().Head);
        }

        Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted)
        );
        Assert.Equal(0, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_TailPreparedWithoutCandidateProvider_ReconstructsInlineWithoutProject() {
        string path = NewJournalPath();
        TestContextCandidateFixture candidateFixture;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            setup.AppendObservation("old");
            _ = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            candidateFixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(setup);
        }
        var sourceClient = new ScriptedClient();
        using (var source = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(
                sourceClient,
                contextCandidate: candidateFixture.Candidate
            ),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => source.SendAsync("tail observation", CancellationToken.None)
            );
        }
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "inline recovery"));
        var recoverySource = new TestContextCandidateSource();
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
            ) with { ContextCandidateSource = recoverySource }
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.Equal("inline recovery", outcome.Message?.GetFlattenedText());
        Assert.Single(recoveryClient.Requests);
        Assert.Equal(0, recoverySource.SelectionCount);
        Assert.Equal(0, recoverySource.MaterializationCount);
    }

    [Fact]
    public async Task ResumeAsync_ToolContinuationTerminal_AllowsNextCoherentSend() {
        string path = NewJournalPath();
        TestContextCandidateFixture candidateFixture;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            setup.AppendObservation("old");
            _ = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            candidateFixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(setup);
        }

        var tool = new RecordingTool("lookup");
        ToolSession initialTools = new ToolRegistry([tool]).CreateSession();
        var client = new ScriptedClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
            ]),
            Descriptor(request)
        ));
        client.Enqueue(_ => throw new IOException("transport after tool result"));
        using (var engine = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                initialTools,
                contextCandidate: candidateFixture.Candidate
            )
        )) {
            await Assert.ThrowsAsync<IOException>(
                () => engine.SendAsync("tool turn", CancellationToken.None)
            );
            Assert.Equal(
                SessionExecutionPhase.AwaitingCompletion,
                engine.InspectExecutionBoundary().Phase
            );
        }

        CompletionRequestPreparedBody sourceManifest =
            ReadBody<CompletionRequestPreparedBody>(
                path,
                ReadParent(path, ReadHead(path))!.Value,
                SessionEventKind.CompletionRequestPrepared
            );
        Assert.Equal("tool-continuation", sourceManifest.Origin.Reason);

        ToolSession recoveryTools = new ToolRegistry([tool]).CreateSession();
        client.Enqueue(request => Success(request, "recovered terminal"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                recoveryTools,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt,
                contextCandidate: candidateFixture.Candidate
            )
        )) {
            ResumeOutcome recovered = await reopened.ResumeAsync(CancellationToken.None);
            Assert.Equal("recovered terminal", recovered.Message?.GetFlattenedText());

            client.Enqueue(request => Success(request, "next tail answer"));
            reopened.UseRuntime(CreateRuntime(
                client,
                    contextCandidate: candidateFixture.Candidate
            ));
            TurnResult next = await reopened.SendAsync(
                "next tail observation",
                CancellationToken.None
            );

            Assert.Equal("next tail answer", next.Message.GetFlattenedText());
        }
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task ResumeAsync_ToolCall_RuntimeIdentityMismatchFailsBeforeProviderOrTool() {
        string path = NewJournalPath();
        var sourceTool = new RecordingTool("lookup");
        ToolSession sourceTools = new ToolRegistry([sourceTool]).CreateSession();
        var client = new ScriptedClient();
        _ = await CreatePreparedAsync(
            path,
            CreateRuntime(client, sourceTools)
        );
        var recoveryTool = new RecordingTool("lookup");
        ToolSession recoveryTools = new ToolRegistry([recoveryTool]).CreateSession();
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                recoveryTools,
                recoveryPolicy: SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt,
                toolRuntimeIdentity: ToolRuntimeIdentity with {
                    ImplementationSetFingerprint = "different-implementations-v2"
                }
            )
        )) {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Contains("do not exactly match", error.Message, StringComparison.Ordinal);
            Assert.Equal(
                SessionExecutionPhase.AwaitingCompletionDispatch,
                reopened.InspectExecutionBoundary().Phase
            );
        }
        Assert.Equal(0, sourceTool.Calls);
        Assert.Equal(0, recoveryTool.Calls);
        Assert.Equal(0, client.Calls);
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptStarted));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.ToolExecutionStarted));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.ToolResultObserved));
    }

    private async Task<EventAddress> CreatePreparedAsync(
        string path,
        SessionRuntime runtime
    ) {
        var candidateSource = Assert.IsType<TestContextCandidateSource>(
            runtime.ContextCandidateSource
        );
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            runtime,
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            candidateSource,
            fixtureId: "prepared-recovery"
        );
        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => engine.SendAsync("prepared observation", CancellationToken.None)
        );
        return engine.ResolveExecutionTail().Head!.Value;
    }

    private async Task<EventAddress> CreateUncertainAsync(
        string path,
        SessionRuntime runtime
    ) {
        EventAddress prepared = await CreatePreparedAsync(path, runtime);
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            prepared,
            SessionEventCodec.Encode(
                SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody()
            ),
            opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted,
            hint: default
        ).Unwrap().EventAddress;
    }

    private static SessionRuntime CreateRuntime(
        ScriptedClient client,
        ToolSession? tools = null,
        SessionCompletionTargetIdentity? target = null,
        SessionUncertainCompletionRecoveryPolicy recoveryPolicy =
            SessionUncertainCompletionRecoveryPolicy.Refuse,
        int? maxTokens = 256,
        SessionToolRuntimeIdentity? toolRuntimeIdentity = null,
        SessionContextCandidate? contextCandidate = null
    ) => new(
        CompletionClient: client,
        ToolSession: tools,
        CompletionTarget: target ?? DefaultTarget,
        MaxTokens: maxTokens,
        UncertainCompletionRecoveryPolicy: recoveryPolicy,
        ToolRuntimeIdentity: toolRuntimeIdentity ?? ToolRuntimeIdentity,
        ContextCandidateSource: new TestContextCandidateSource(contextCandidate)
    );

    private static CompletionResult Success(CompletionRequest request, string text)
        => new(
            new ActionMessage([new ActionBlock.Text(text)]),
            Descriptor(request)
        );

    private static CompletionDescriptor Descriptor(CompletionRequest request)
        => new("recovery-client", "recovery-api-v1", request.ModelId);

    private static EventAddress ReadHead(string path) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        return journal.GetHead(main)!.Value;
    }

    private static EventAddress[] ReadAddressesByKind(string path, SessionEventKind kind) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        return chain.Where(address =>
            journal.ReadEventHeaderPreview(address).Unwrap().OpaqueEventKind == (uint)kind
        ).ToArray();
    }

    private static EventAddress? ReadParent(string path, EventAddress address) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.ReadEventHeaderPreview(address).Unwrap().Parent;
    }

    private static T ReadBody<T>(
        string path,
        EventAddress address,
        SessionEventKind kind
    ) where T : class {
        using var engine = SessionJournalEngine.Open(path);
        return Assert.IsType<T>(
            SessionEventCodec.Decode(kind, engine.ReadPayloadBytes(address), out _)
        );
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-recovery-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class ScriptedClient(
        string name = "recovery-client",
        string apiSpecId = "recovery-api-v1"
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = [];

        public string Name { get; } = name;

        public string ApiSpecId { get; } = apiSpecId;

        public int Calls { get; private set; }

        public List<CompletionRequest> Requests { get; } = [];

        public void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Requests.Add(request);
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

        public ToolExecutionContext? LastContext { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastContext = context;
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(ToolExecutionStatus.Success, "tool result")
            );
        }
    }
}

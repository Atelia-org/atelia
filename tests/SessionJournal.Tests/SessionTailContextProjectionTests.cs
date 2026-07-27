using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionTailContextProjectionTests : IDisposable {
    private readonly List<string> _tempDirectories = new();

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    [Fact]
    public async Task SendAsync_ExactArtifactTail_ExpandsSnapshotFoldsSetupSuffixAndCommitsExactManifest() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("tail answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        SessionContextCandidate candidate;
        EventAddress anchor;
        EventAddress runtimeB;
        EventAddress promptB;
        int fullProjectionCountBeforeSend;

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            candidate = CreateCandidate(engine, anchor);

            runtimeB = engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema)
            );
            promptB = engine.AppendSystemPromptSetup("system-B");
            engine.UseRuntime(CreateRuntime(client, candidate));
            fullProjectionCountBeforeSend = engine.FullProjectionInvocationCount;

            TurnResult result = await engine.SendAsync("new observation", CancellationToken.None);

            Assert.Equal("tail answer", result.Message.GetFlattenedText());
            Assert.Equal(
                new SessionTailProjectionDiagnostics(
                    HeaderVisitCount: 4,
                    SuffixPayloadReadCount: 3,
                    SuffixEventCount: 3
                ),
                engine.LastTailProjectionDiagnostics
            );
            Assert.Equal(fullProjectionCountBeforeSend, engine.FullProjectionInvocationCount);
        }

        CompletionRequest request = Assert.Single(client.Requests);
        Assert.Equal("model-B", request.ModelId);
        Assert.Equal("system-B", request.SystemPrompt);
        Assert.DoesNotContain("stale", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Context, static message => message is SessionContextHeader);
        Assert.Collection(
            request.Context,
            first => Assert.Equal(
                "## roleplay.world-understanding\n\nmemory observation",
                Assert.IsType<ObservationMessage>(first).Content
            ),
            second => Assert.Equal(
                "## roleplay.first-person-autobiography\n\nmemory action",
                Assert.IsType<ActionMessage>(second).GetFlattenedText()
            ),
            third => Assert.Equal("new observation", Assert.IsType<ObservationMessage>(third).Content)
        );
        Assert.DoesNotContain(
            request.Context,
            message => (
                message is ActionMessage action
                    ? action.GetFlattenedText()
                    : Assert.IsAssignableFrom<ObservationMessage>(message).Content ?? ""
            ).Contains("stale", StringComparison.Ordinal)
        );

        EventAddress prepared = Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        CompletionRequestPreparedBody manifest;
        using (var engine = SessionJournalEngine.Open(path)) {
            manifest = Assert.IsType<CompletionRequestPreparedBody>(
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionRequestPrepared,
                    engine.ReadPayloadBytes(prepared),
                    out _
                )
            );
        }
        Assert.Equal(SessionRequestManifestDefaults.RecipeId, manifest.Recipe.RecipeId);
        Assert.Equal(anchor, manifest.Plan.RawStartExclusive);
        Assert.Equal(2, manifest.Plan.ExactContextInputs.Length);
        Assert.Collection(
            manifest.Plan.ExactContextInputs,
            observation => {
                Assert.Equal("", observation.ContextSnapshot.SystemPromptFragment);
                Assert.Equal(
                    "## roleplay.world-understanding\n\nmemory observation",
                    observation.ContextSnapshot.ObservationMessage
                );
                Assert.Equal("", observation.ContextSnapshot.ActionMessage);
            },
            autobiography => {
                Assert.Equal("", autobiography.ContextSnapshot.SystemPromptFragment);
                Assert.Equal("", autobiography.ContextSnapshot.ObservationMessage);
                Assert.Equal(
                    "## roleplay.first-person-autobiography\n\nmemory action",
                    autobiography.ContextSnapshot.ActionMessage
                );
            }
        );
        Assert.All(
            manifest.Plan.ExactContextInputs,
            input => Assert.Equal(
                SessionArtifactContextSnapshotHasher.ComputeSha256(input.ContextSnapshot),
                input.ContentSha256
            )
        );
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(request), manifest.Commitment);
        Assert.Equal(runtimeB, manifest.Setups.RuntimeConfig.Address);
        Assert.Equal(promptB, manifest.Setups.SystemPrompt.Address);
    }

    [Fact]
    public async Task ResumeAsync_ExactObservationTail_DoesNotInvokeFullProjection() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            EventAddress anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            candidate = CreateCandidate(engine, anchor);
            engine.AppendObservation("resume observation");
        }

        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("resumed answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, candidate)
        );
        int projectionCountBeforeResume = reopened.FullProjectionInvocationCount;

        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed answer", outcome.Message?.GetFlattenedText());
        Assert.Equal(projectionCountBeforeResume, reopened.FullProjectionInvocationCount);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task SendAsync_CoherentArtifactTail_ToolContinuationKeepsVisibleToolsAndNeverProjects() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            setup.AppendObservation("old");
            EventAddress anchor = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            candidate = CreateCandidate(setup, anchor);
        }

        int response = 0;
        var client = new CapturingCompletionClient(request => {
            int currentResponse = response++;
            return currentResponse switch {
            0 => new CompletionResult(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall("noop", "call-1", "{}")
                    )
                ]),
                new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
            ),
            1 => new CompletionResult(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall("noop", "call-2", "{}")
                    )
                ]),
                new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
            ),
            _ => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("after tool")]),
                new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
            )
            };
        });
        ToolSession tools = new ToolRegistry([new NoopTool()]).CreateSession();
        using var engine = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, candidate, tools, TestToolRuntimeIdentity)
        );
        int projectionCount = engine.FullProjectionInvocationCount;

        TurnResult result = await engine.SendAsync("use tool", CancellationToken.None);

        Assert.Equal("after tool", result.Message.GetFlattenedText());
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.Single(request.Tools));
        AssertToolDependencyTail(client.Requests[1], "call-1");
        AssertToolDependencyTail(client.Requests[2], "call-2");
        Assert.IsType<ActionMessage>(client.Requests[2].Context[^4]);
        Assert.IsType<ToolResultsMessage>(client.Requests[2].Context[^3]);
        engine.Dispose();
        CompletionRequestPreparedBody[] manifests = ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
            .Select(address => ReadPrepared(path, address))
            .ToArray();
        Assert.Equal(
            ["observation", "tool-continuation", "tool-continuation"],
            manifests.Select(static manifest => manifest.Origin.Reason).ToArray()
        );
        Assert.All(
            manifests,
            manifest => {
                Assert.Equal(SessionRequestManifestDefaults.RecipeId, manifest.Recipe.RecipeId);
                Assert.Equal(2, manifest.Plan.ExactContextInputs.Length);
                Assert.Single(manifest.ToolSet.Definitions);
                Assert.Equal(TestToolRuntimeIdentity, manifest.ToolSet.RuntimeIdentity);
            }
        );
    }

    private static void AssertToolDependencyTail(
        CompletionRequest request,
        string expectedCallId
    ) {
        ActionMessage action = Assert.IsType<ActionMessage>(request.Context[^2]);
        Assert.Equal(expectedCallId, Assert.Single(action.ToolCalls).ToolCallId);
        ToolResultsMessage results =
            Assert.IsType<ToolResultsMessage>(request.Context[^1]);
        Assert.Equal(expectedCallId, Assert.Single(results.Results).ToolCallId);
    }

    [Fact]
    public async Task ResumeAsync_ObservationWhoseParentIsNotIdle_RejectsWithoutFullProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        engine.AppendObservation("first observation");
        engine.AppendObservation("invalid second observation");
        engine.UseRuntime(CreateRuntime(client, "missing-artifact"));
        int projectionCountBeforeResume = engine.FullProjectionInvocationCount;

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains("idle or failed boundary", error.Message, StringComparison.Ordinal);
        Assert.Equal(projectionCountBeforeResume, engine.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_TailRuntimeWithVisibleToolsWithoutIdentity_FailsBeforeObservationOrFullProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        ToolSession toolSession = new ToolRegistry([new NoopTool()]).CreateSession();
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, "missing-artifact", toolSession)
        )) {
            int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SendAsync("new observation", CancellationToken.None)
            );

            Assert.Contains("ToolRuntimeIdentity", error.Message, StringComparison.Ordinal);
            Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);
            Assert.Empty(client.Requests);
        }
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.ObservationAccepted));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("implementation")]
    [InlineData("capability")]
    public async Task SendAsync_InvalidToolRuntimeIdentityFailsBeforeMutation(
        string invalidField
    ) {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        ToolSession toolSession =
            new ToolRegistry([new NoopTool()]).CreateSession();
        SessionToolRuntimeIdentity identity = invalidField switch {
            "host" => TestToolRuntimeIdentity with { HostId = " " },
            "implementation" => TestToolRuntimeIdentity with {
                ImplementationSetFingerprint = ""
            },
            "capability" => TestToolRuntimeIdentity with {
                CapabilitySetFingerprint = "\t"
            },
            _ => throw new Xunit.Sdk.XunitException(
                $"Unknown invalid field '{invalidField}'."
            )
        };
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(
                client,
                "unused",
                toolSession
            ) with { ToolRuntimeIdentity = identity }
        )) {
            EventAddress? head = engine.ResolveExecutionTail().Head;
            int projectionCount = engine.FullProjectionInvocationCount;

            ArgumentException error =
                await Assert.ThrowsAsync<ArgumentException>(
                    () => engine.SendAsync(
                        "must not append",
                        CancellationToken.None
                    )
                );

            Assert.Contains("ToolRuntimeIdentity", error.ParamName);
            Assert.Equal(head, engine.ResolveExecutionTail().Head);
            Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
            Assert.Empty(client.Requests);
        }
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.ObservationAccepted)
        );
        Assert.Empty(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
    }

    [Fact]
    public async Task SendAsync_TailProviderToolCall_PersistsKnownFailureAndAllowsNextObservation() {
        string path = NewJournalPath();
        int responseIndex = 0;
        var client = new CapturingCompletionClient(request => {
            ActionMessage message = responseIndex++ == 0
                ? new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
                ])
                : new ActionMessage([new ActionBlock.Text("recovered answer")]);
            return new CompletionResult(
                message,
                new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
            );
        });
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            EventAddress anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            SessionContextCandidate candidate =
                CreateCandidate(engine, anchor);
            engine.UseRuntime(CreateRuntime(client, candidate));
            int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

            SessionJournalTurnAbortedException error =
                await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => engine.SendAsync("new observation", CancellationToken.None)
            );

            Assert.Equal(CompletionTerminationKind.Failed, error.Termination.Kind);
            Assert.Equal("atelia.host.unsupported-tool-call", error.Termination.ProviderReason);
            Assert.Contains("supports no tools", error.Termination.Detail, StringComparison.Ordinal);
            Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);

            SessionProjection failed = engine.Project();
            Assert.Equal(SessionExecutionPhase.TurnFailed, failed.ExecutionState.Phase);
            EventAddress failureAddress = failed.Head!.Value;
            CompletionAttemptFailedBody failure = Assert.IsType<CompletionAttemptFailedBody>(
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionAttemptFailed,
                    engine.ReadPayloadBytes(failureAddress),
                    out _
                )
            );
            Assert.Equal(CompletionTerminationKind.Failed, failure.TerminationKind);
            Assert.Equal("atelia.host.unsupported-tool-call", failure.ProviderReason);

            int projectionCountBeforeRecovery = engine.FullProjectionInvocationCount;
            TurnResult recovered = await engine.SendAsync(
                "recovery observation",
                CancellationToken.None
            );
            Assert.Equal("recovered answer", recovered.Message.GetFlattenedText());
            Assert.Equal(projectionCountBeforeRecovery, engine.FullProjectionInvocationCount);
        }
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(2, ReadAddressesByKind(path, SessionEventKind.CompletionRequestPrepared).Length);
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.AgentActionProduced));
    }

    [Fact]
    public async Task SendAsync_BootstrapThenLiveActionBoundaries_PassLocalCausalityWithoutProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("tail answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        EventAddress created = engine.Project().Head!.Value;
        SessionContextCandidate candidate =
            CreateCandidate(engine, created);
        engine.UseRuntime(CreateRuntime(client, candidate));
        int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

        await engine.SendAsync("first observation", CancellationToken.None);
        await engine.SendAsync("second observation", CancellationToken.None);

        Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_NullCandidateLeavesDurableAwaitingAgentActionWithoutProviderCall() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, "unused")
        );
        EventAddress head = engine.ResolveExecutionTail().Head!.Value;
        int projectionCount = engine.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => engine.SendAsync("durable observation", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            error.Reason
        );
        Assert.NotEqual(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.ResolveExecutionTail().State.Phase);
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_MissingCandidateSourceFailsBeforeObservationAppend() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        SessionRuntime runtime = CreateRuntime(client, "unused") with {
            ContextCandidateSource = null
        };
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            runtime
        );
        EventAddress head = engine.ResolveExecutionTail().Head!.Value;

        SessionJournalNotReadyException error = await Assert.ThrowsAsync<SessionJournalNotReadyException>(
            () => engine.SendAsync("must not append", CancellationToken.None)
        );

        Assert.Equal(SessionJournalNotReadyReason.ContextCandidateSourceRequired, error.Reason);
        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_MissingRuntimeFailsBeforeReadinessOrObservation() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        EventAddress? head = engine.ResolveExecutionTail().Head;
        int projectionCount = engine.FullProjectionInvocationCount;

        ArgumentException argumentError =
            await Assert.ThrowsAsync<ArgumentException>(
                () => engine.SendAsync(" ", CancellationToken.None)
            );
        Assert.Equal("observation", argumentError.ParamName);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Contains("runtime is required", error.Message, StringComparison.Ordinal);
        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("invalid-target-field")]
    [InlineData("invalid-client-name")]
    [InlineData("invalid-client-api")]
    [InlineData("nonpositive-max-tokens")]
    public async Task SendAsync_InvalidPlanningPrerequisiteFailsBeforeObservation(
        string scenario
    ) {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        EventAddress head;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = setup.Project().Head!.Value;
            candidate = CreateCandidate(setup, anchor);
            head = setup.ResolveExecutionTail().Head!.Value;
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider"),
            name: scenario == "invalid-client-name" ? " " : "tail-client",
            apiSpecId: scenario == "invalid-client-api" ? "" : "tail-api-v1"
        );
        SessionRuntime runtime = CreateRuntime(client, candidate);
        runtime = scenario switch {
            "missing-target" => runtime with { CompletionTarget = null },
            "invalid-target-field" => runtime with {
                CompletionTarget = runtime.CompletionTarget! with {
                    ConnectionId = " "
                }
            },
            "nonpositive-max-tokens" => runtime with { MaxTokens = 0 },
            _ => runtime
        };
        using (var reopened = SessionJournalEngine.Open(
            path,
            runtime
        )) {
            int projectionCount = reopened.FullProjectionInvocationCount;

            Exception? error = await Record.ExceptionAsync(
                () => reopened.SendAsync(
                    "must not append",
                    CancellationToken.None
                )
            );

            Assert.NotNull(error);
            if (scenario == "missing-target") {
                Assert.IsType<InvalidOperationException>(error);
            }
            else {
                Assert.IsAssignableFrom<ArgumentException>(error);
            }
            Assert.Equal(head, reopened.ResolveExecutionTail().Head);
            Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
            Assert.Empty(client.Requests);
        }
        Assert.Empty(
            ReadAddressesByKind(path, SessionEventKind.ObservationAccepted)
        );
        Assert.Empty(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
    }


    [Fact]
    public async Task ResumeAsync_CrossBranchPreparedRawStartSetupIsRawCorruptionBeforeProvider() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        EventAddress mainHead;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            mainHead = setup.Project().Head!.Value;
            candidate = CreateCandidate(setup, mainHead);
        }
        EventAddress offBranchRuntimeSetup;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            journal.CreateBranch("off-setup", mainHead).Unwrap();
            offBranchRuntimeSetup = CommitToBranch(
                journal,
                "off-setup",
                mainHead,
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration(
                    "off-model",
                    "off-surface",
                    SessionJournalDefaults.Schema
                )
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        CompletionRequestPreparedBody validPreparedBody;
        EventAddress validPrepared;
        using (var preparing = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(client, candidate),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => preparing.SendAsync(
                    "prepare cross-branch fixture",
                    CancellationToken.None
                )
            );
            validPrepared = preparing.ResolveExecutionTail().Head!.Value;
            validPreparedBody = Assert.IsType<CompletionRequestPreparedBody>(
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionRequestPrepared,
                    preparing.ReadPayloadBytes(validPrepared),
                    out _
                )
            );
        }
        EventAddress forgedPrepared;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            EventFrameHeader preparedHeader =
                journal.ReadEventHeaderPreview(validPrepared).Unwrap();
            EventAddress observation = preparedHeader.Parent!.Value;
            RefId main = journal
                .OpenBranch(SessionJournalDefaults.MainBranchName)
                .Unwrap();
            Assert.True(
                journal.MoveRef(main, validPrepared, observation).Unwrap()
            );
            forgedPrepared = Commit(
                journal,
                observation,
                SessionEventKind.CompletionRequestPrepared,
                validPreparedBody with {
                    Plan = validPreparedBody.Plan with {
                        RawStartSetups = validPreparedBody.Plan.RawStartSetups with {
                            RuntimeConfig = new SessionSetupReference(
                                offBranchRuntimeSetup,
                                1,
                                new string('e', 64)
                            )
                        }
                    }
                }
            );
        }

        SessionRuntime recoveryRuntime = CreateRuntime(client, candidate) with {
            UncertainCompletionRecoveryPolicy =
                SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt
        };
        using var reopened = SessionJournalEngine.Open(path, recoveryRuntime);
        int projectionCount = reopened.FullProjectionInvocationCount;

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

        Assert.Contains(
            "Setup reference payload hash mismatch",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(forgedPrepared, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_SetupMutationAfterActivationUsesLatestSetup(
        bool mutateRuntime
    ) {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("tail answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        SessionContextCandidate candidate;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = engine.Project().Head!.Value;
            candidate = CreateCandidate(engine, anchor);
            if (mutateRuntime) {
                engine.AppendRuntimeConfigSetup(
                    new SessionRuntimeConfiguration(
                        "model-B",
                        "surface-B",
                        SessionJournalDefaults.Schema
                    )
                );
            }
            else {
                engine.AppendSystemPromptSetup("system-B");
            }
            engine.UseRuntime(CreateRuntime(client, candidate));

            _ = await engine.SendAsync("new observation", CancellationToken.None);
        }

        CompletionRequest request = Assert.Single(client.Requests);
        Assert.Equal(mutateRuntime ? "model-B" : "model-A", request.ModelId);
        Assert.Equal(mutateRuntime ? "system-A" : "system-B", request.SystemPrompt);
    }

    [Theory]
    [InlineData(SessionEventKind.AgentActionProduced)]
    [InlineData(SessionEventKind.SessionCreated)]
    public async Task SendAsync_ForgedIdleBoundary_FailsLocalCausalityProof(
        SessionEventKind forgedKind
    ) {
        string path = NewJournalPath();
        EventAddress observation;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            observation = engine.AppendObservation("orphan observation");
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            object body = forgedKind == SessionEventKind.AgentActionProduced
                ? new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text("orphan action")]),
                    new CompletionDescriptor("tail-client", "tail-api-v1", "model-A"),
                    $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}",
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
                : new SessionCreatedBody();
            _ = Commit(journal, observation, forgedKind, body);
        }

        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, "missing-artifact")
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reopened.SendAsync("must reject", CancellationToken.None)
        );
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_FailedBoundaryWithoutStarted_FailsLocalCausalityProof() {
        string path = NewJournalPath();
        var sourceClient = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("failpoint must run first")
        );
        var candidateSource = new TestContextCandidateSource();
        EventAddress prepared;
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(sourceClient, "unused") with {
                ContextCandidateSource = candidateSource
            },
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource,
                fixtureId: "failed-boundary-attempt-mismatch"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("source observation", CancellationToken.None)
            );
            prepared = engine.Project().Head!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = Commit(
                journal,
                prepared,
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    CompletionTerminationKind.Failed,
                    "test",
                    "mismatch",
                    Array.AsReadOnly(Array.Empty<string>())
                )
            );
        }

        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, "missing-artifact")
        );
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => reopened.SendAsync("must reject", CancellationToken.None)
        );

        Assert.Contains("latest active attempt", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_CandidateSourceHeadEarlierThanAnchor_FailsBeforeCompletion() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        engine.AppendObservation("old");
        EventAddress anchor = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("old answer")]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        SessionGoverningSetup setup =
            engine.ResolveGoverningSetup(anchor);
        SessionContextCandidate candidate = CreateCandidate(
            engine,
            anchor,
            setup.RuntimeConfigSetupAddress
        );
        engine.UseRuntime(CreateRuntime(client, candidate));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SendAsync(
                "invalid candidate",
                CancellationToken.None
            )
        );

        Assert.Contains("sourceRawHead", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("tool-action")]
    [InlineData("tool-result")]
    public async Task SendAsync_ImportedToolResultContinuation_RejectsNonReplaySafeCandidateAnchor(
        string boundary
    ) {
        string path = NewJournalPath();
        EventAddress toolAction;
        EventAddress toolResult;
        EventAddress finalAction;
        EventAddress sourceObservation;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(
                new CapturingCompletionClient(_ => throw new InvalidOperationException("unused")),
                new ToolRegistry([new NoopTool("lookup")]).CreateSession(),
                ToolRuntimeIdentity: new SessionToolRuntimeIdentity(
                    "test-tool-host",
                    "test-tool-implementations-v1",
                    "test-tool-capabilities-v1"
                )
            )
        )) {
            sourceObservation = engine.AppendObservation("use tool");
            toolAction = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
                ]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            EventAddress started = Commit(
                journal,
                toolAction,
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-1",
                    "lookup",
                    "{}",
                    "op-1",
                    1,
                    new SessionToolRuntimeIdentity(
                        "test-tool-host",
                        "test-tool-implementations-v1",
                        "test-tool-capabilities-v1"
                    )
                )
            );
            toolResult = Commit(
                journal,
                started,
                SessionEventKind.ToolResultObserved,
                new ToolResultObservedBody(
                    "call-1",
                    "lookup",
                    1,
                    ToolExecutionStatus.Success,
                    [new ToolResultBlock.Text("result")]
                )
            );
            finalAction = Commit(
                journal,
                toolResult,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text("done")]),
                    new CompletionDescriptor("import", "import-v1", "model-A"),
                    $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(sourceObservation)}",
                    new SessionExecutionCheckpoint(1),
                    ToolRuntimeIdentity: null
                )
            );
        }

        EventAddress anchor = boundary == "tool-action" ? toolAction : toolResult;
        InvalidDataException error;
        using (var engine = SessionJournalEngine.Open(path)) {
            SessionContextCandidate candidate = CreateCandidate(
                engine,
                anchor,
                finalAction
            );
            engine.UseRuntime(CreateRuntime(
                new CapturingCompletionClient(
                    _ => throw new InvalidOperationException(
                        "must not call completion"
                    )
                ),
                candidate
            ));
            error = await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.SendAsync(
                    "invalid anchor",
                    CancellationToken.None
                )
            );
        }

        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));

        Assert.Contains(
            boundary == "tool-action" ? "action with" : "ToolResultObserved",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Empty(client.Requests);
    }


    [Fact]
    public void CoherentRecipeExpand_NeverReturnsHeaderMessage() {
        var snapshot = new SessionRequestArtifactContextSnapshot(
            "  memory system  ",
            "memory observation",
            "memory action"
        );

        var (systemPrompt, context) = SessionCoherentRequestRecipe.Expand(
            "base system",
            snapshot
        );

        Assert.Equal("base system\n\nmemory system", systemPrompt);
        Assert.DoesNotContain('\r', systemPrompt);
        Assert.Collection(
            context,
            first => Assert.IsType<ObservationMessage>(first),
            second => Assert.IsType<ActionMessage>(second)
        );
        Assert.DoesNotContain(context, static message => message is SessionContextHeader);
    }


    private static SessionContextCandidate CreateCandidate(
        SessionJournalEngine engine,
        EventAddress anchor,
        EventAddress? sourceRawHead = null
    ) => new(
        anchor,
        engine.ResolveContextAnchorSetupReferences(anchor),
        [
            ContextCandidateTestFixture.Contribution(
                MemoryPackCarrier.Observation,
                "roleplay.world-understanding",
                "memory observation",
                sourceRawHead ?? anchor
            ),
            ContextCandidateTestFixture.Contribution(
                MemoryPackCarrier.Action,
                "roleplay.first-person-autobiography",
                "memory action",
                sourceRawHead ?? anchor
            )
        ]
    );

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        SessionContextCandidate candidate,
        ToolSession? toolSession = null,
        SessionToolRuntimeIdentity? toolRuntimeIdentity = null
    ) => new(
        CompletionClient: client,
        ToolSession: toolSession,
        CompletionTarget: new SessionCompletionTargetIdentity(
            "tail-connection",
            "test",
            "tail-connection-v1",
            "tail-adapter-v1"
        ),
        MaxTokens: 512,
        ToolRuntimeIdentity: toolRuntimeIdentity,
        ContextCandidateSource: new TestContextCandidateSource(candidate)
    );

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        string unusedCandidateId,
        ToolSession? toolSession = null
    ) {
        _ = unusedCandidateId;
        return new SessionRuntime(
            CompletionClient: client,
            ToolSession: toolSession,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "tail-connection",
                "test",
                "tail-connection-v1",
                "tail-adapter-v1"
            ),
            MaxTokens: 512,
            ContextCandidateSource: new TestContextCandidateSource()
        );
    }

    private static CompletionRequestPreparedBody ReadPrepared(
        string path,
        EventAddress address
    ) {
        using var engine = SessionJournalEngine.Open(path);
        return Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                engine.ReadPayloadBytes(address),
                out _
            )
        );
    }

    private static SessionToolRuntimeIdentity TestToolRuntimeIdentity { get; } =
        new("tail-tool-host", "tail-tools-v1", "tail-capabilities-v1");

    private sealed class NoopTool(string name = "noop") : ITool {
        public ToolDefinition Definition { get; } =
            new(name, "No-op tool.", new ToolSchema.Object());

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(ToolExecutionStatus.Success, "unused")
            );
        }
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress expectedHead,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedHead,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static EventAddress CommitToBranch(
        EventJournal.EventJournal journal,
        string branchName,
        EventAddress expectedHead,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        branchName,
        expectedHead,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static EventAddress[] ReadAddressesByKind(string path, SessionEventKind kind) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        return chain.Where(address =>
            journal.ReadEventHeaderPreview(address).Unwrap().OpaqueEventKind == (uint)kind
        ).ToArray();
    }

    private string NewJournalPath() {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-session-tail-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class CapturingCompletionClient(
        Func<CompletionRequest, CompletionResult> response,
        string name = "tail-client",
        string apiSpecId = "tail-api-v1"
    ) : ICompletionClient {
        private readonly Func<CompletionRequest, CompletionResult> _response = response;

        public string Name { get; } = name;

        public string ApiSpecId { get; } = apiSpecId;

        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_response(request));
        }
    }
}

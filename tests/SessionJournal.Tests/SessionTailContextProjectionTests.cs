using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
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
        TestArtifactSet artifact;
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
            SessionGoverningSetup anchorSetup = engine.ResolveGoverningSetup(anchor);
            var memoryPack = CreateMemoryPack();
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                anchorSetup,
                memoryPack
            );

            runtimeB = engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema)
            );
            promptB = engine.AppendSystemPromptSetup("system-B");
            await engine.CommitArtifactSetAsync(Selections(artifact));
            engine.UseRuntime(CreateRuntime(client, artifact));
            fullProjectionCountBeforeSend = engine.FullProjectionInvocationCount;

            TurnResult result = await engine.SendAsync("new observation", CancellationToken.None);

            Assert.Equal("tail answer", result.Message.GetFlattenedText());
            Assert.Equal(
                new SessionTailProjectionDiagnostics(
                    HeaderVisitCount: 5,
                    SuffixPayloadReadCount: 4,
                    SuffixEventCount: 4
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
        Assert.Equal(SessionRequestManifestDefaults.CoherentArtifactTailSelectionPolicyId, manifest.Plan.SelectionPolicyId);
        Assert.Equal(anchor, manifest.Plan.RawStartExclusive);
        Assert.Equal(2, manifest.Plan.ArtifactInputs.Length);
        Assert.Collection(
            manifest.Plan.ArtifactInputs,
            observation => {
                Assert.Equal(
                    artifact.WorldUnderstanding.ArtifactId,
                    observation.ArtifactId
                );
                Assert.Equal("", observation.ContextSnapshot.SystemPromptFragment);
                Assert.Equal(
                    "## roleplay.world-understanding\n\nmemory observation",
                    observation.ContextSnapshot.ObservationMessage
                );
                Assert.Equal("", observation.ContextSnapshot.ActionMessage);
            },
            autobiography => {
                Assert.Equal(
                    artifact.Autobiography.ArtifactId,
                    autobiography.ArtifactId
                );
                Assert.Equal("", autobiography.ContextSnapshot.SystemPromptFragment);
                Assert.Equal("", autobiography.ContextSnapshot.ObservationMessage);
                Assert.Equal(
                    "## roleplay.first-person-autobiography\n\nmemory action",
                    autobiography.ContextSnapshot.ActionMessage
                );
            }
        );
        Assert.All(
            manifest.Plan.ArtifactInputs,
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
        TestArtifactSet artifact;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            EventAddress anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                engine.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            await engine.CommitArtifactSetAsync(Selections(artifact));
            engine.AppendObservation("resume observation");
        }

        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("resumed answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(client, artifact));
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
        TestArtifactSet artifact;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            setup.AppendObservation("old");
            EventAddress anchor = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                anchor,
                setup.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            await setup.CommitArtifactSetAsync(Selections(artifact));
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
            CreateRuntime(client, artifact, tools, TestToolRuntimeIdentity)
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
            manifests.Select(static manifest => manifest.Attempt.Reason).ToArray()
        );
        Assert.All(
            manifests,
            manifest => {
                Assert.Equal(
                    SessionRequestManifestDefaults.CoherentArtifactTailSelectionPolicyId,
                    manifest.Plan.SelectionPolicyId
                );
                Assert.Equal(2, manifest.Plan.ArtifactInputs.Length);
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

    [Fact]
    public async Task SendAsync_TailProviderToolCall_PersistsKnownFailureAndAllowsNextObservation() {
        string path = NewJournalPath();
        int responseIndex = 0;
        string? failedAttemptId = null;
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
            TestArtifactSet artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                engine.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            await engine.CommitArtifactSetAsync(Selections(artifact));
            engine.UseRuntime(CreateRuntime(client, artifact));
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
            failedAttemptId = failure.AttemptId;

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
        EventAddress firstPrepared =
            ReadAddressesByKind(path, SessionEventKind.CompletionRequestPrepared)[0];
        using var inspection = SessionJournalEngine.Open(path);
        CompletionRequestPreparedBody prepared = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                inspection.ReadPayloadBytes(firstPrepared),
                out _
            )
        );
        Assert.Equal(prepared.Attempt.AttemptId, failedAttemptId);
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
        TestArtifactSet artifact = await WriteArtifactAsync(
            path,
            created,
            sourceRawHead: created,
            engine.ResolveGoverningSetup(created),
            CreateMemoryPack()
        );
        await engine.CommitArtifactSetAsync(Selections(artifact));
        engine.UseRuntime(CreateRuntime(client, artifact));
        int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

        await engine.SendAsync("first observation", CancellationToken.None);
        await engine.SendAsync("second observation", CancellationToken.None);

        Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_DefaultPolicyWithoutActivationFailsBeforeMutationProviderOrProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, "unused")
        );
        EventAddress? head = engine.ResolveExecutionTail().Head;
        int projectionCount = engine.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => engine.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ActiveArtifactSetRequired,
            error.Reason
        );
        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
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

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Contains("runtime is required", error.Message, StringComparison.Ordinal);
        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
    }

    [Fact]
    public async Task ResumeAsync_ObservationWithoutActivationIsTypedNotReadyBeforePreparedProviderOrProjection() {
        string path = NewJournalPath();
        EventAddress observation;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            observation = setup.AppendObservation("resume without artifacts");
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, "unused")
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ActiveArtifactSetRequired,
            error.Reason
        );
        Assert.Equal(observation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_AfterRewindBeforeActivationIsTypedNotReady() {
        string path = NewJournalPath();
        EventAddress preActivation;
        EventAddress activation;
        TestArtifactSet artifact;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            preActivation = setup.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                preActivation,
                sourceRawHead: preActivation,
                setup.ResolveGoverningSetup(preActivation),
                CreateMemoryPack()
            );
            activation = await setup.CommitArtifactSetAsync(Selections(artifact));
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal
                .OpenBranch(SessionJournalDefaults.MainBranchName)
                .Unwrap();
            Assert.True(journal.MoveRef(main, activation, preActivation).Unwrap());
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ActiveArtifactSetRequired,
            error.Reason
        );
        Assert.Equal(preActivation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_MissingActiveMemberIsTypedNotReadyBeforeObservation() {
        string path = NewJournalPath();
        EventAddress activation;
        TestArtifactSet artifact;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = setup.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                setup.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            activation = await setup.CommitArtifactSetAsync(Selections(artifact));
        }
        DerivedRecapStore store = DerivedRecapStore.Open(path);
        File.Delete(
            System.IO.Path.Combine(
                store.ArtifactsDirectory,
                $"{artifact.WorldUnderstanding.ArtifactId}.json"
            )
        );

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ArtifactSetMemberUnavailable,
            error.Reason
        );
        Assert.Equal(artifact.WorldUnderstanding.ArtifactId, error.ArtifactId);
        Assert.Equal(activation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_ActivationMemberIdentityMismatchIsTypedNotReadyBeforeObservation() {
        string path = NewJournalPath();
        EventAddress validActivation;
        ArtifactSetCommittedBody validBody;
        TestArtifactSet artifact;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = setup.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                setup.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            validActivation =
                await setup.CommitArtifactSetAsync(Selections(artifact));
            validBody = ReadArtifactSetBody(setup, validActivation);
        }
        ArtifactSetCommittedBody forgedBody = validBody with {
            Members = [
                validBody.Members[0] with {
                    ArtifactKind = $"{validBody.Members[0].ArtifactKind}-forged"
                },
                .. validBody.Members.Skip(1)
            ]
        };
        EventAddress forgedActivation;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            forgedActivation = Commit(
                journal,
                validActivation,
                SessionEventKind.ArtifactSetCommitted,
                forgedBody
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
            error.Reason
        );
        Assert.Equal(validBody.Members[0].ArtifactId, error.ArtifactId);
        Assert.Equal(forgedActivation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_SourceRawHeadBeforeAnchorIsTypedNotReadyBeforeObservation() {
        string path = NewJournalPath();
        EventAddress beforeAnchor;
        EventAddress anchor;
        TestArtifactSet artifact;
        ArtifactSetCommittedBody body;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            beforeAnchor = setup.Project().Head!.Value;
            setup.AppendObservation("covered observation");
            anchor = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("covered answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            SessionGoverningSetup anchorSetup =
                setup.ResolveGoverningSetup(anchor);
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: beforeAnchor,
                anchorSetup,
                CreateMemoryPack()
            );
            body = CreateArtifactSetBody(
                setup,
                anchor,
                anchorSetup,
                anchorSetup,
                artifact
            );
        }
        EventAddress activation;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            activation = Commit(
                journal,
                anchor,
                SessionEventKind.ArtifactSetCommitted,
                body
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
            error.Reason
        );
        Assert.NotNull(error.ArtifactId);
        Assert.Equal(activation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_SourceRawHeadOffActivationLineageIsTypedNotReadyBeforeObservation() {
        string path = NewJournalPath();
        EventAddress anchor;
        SessionGoverningSetup anchorSetup;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            anchor = setup.Project().Head!.Value;
            anchorSetup = setup.ResolveGoverningSetup(anchor);
        }
        EventAddress offBranchSource;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            journal.CreateBranch("off", anchor).Unwrap();
            offBranchSource = CommitToBranch(
                journal,
                "off",
                anchor,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("off branch")
            );
        }
        TestArtifactSet artifact = await WriteArtifactAsync(
            path,
            anchor,
            sourceRawHead: offBranchSource,
            anchorSetup,
            CreateMemoryPack()
        );
        ArtifactSetCommittedBody body;
        using (var setup = SessionJournalEngine.Open(path)) {
            body = CreateArtifactSetBody(
                setup,
                anchor,
                anchorSetup,
                anchorSetup,
                artifact
            );
        }
        EventAddress activation;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            activation = Commit(
                journal,
                anchor,
                SessionEventKind.ArtifactSetCommitted,
                body
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
            error.Reason
        );
        Assert.NotNull(error.ArtifactId);
        Assert.Equal(activation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SourceRawHeadAtActivationAddressIsRejectedAsAfterCoverageInterval() {
        string path = NewJournalPath();
        TestArtifactSet artifact;
        EventAddress anchor;
        EventAddress activation;
        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, "unused")
        );
        anchor = engine.Project().Head!.Value;
        artifact = await WriteArtifactAsync(
            path,
            anchor,
            sourceRawHead: anchor,
            engine.ResolveGoverningSetup(anchor),
            CreateMemoryPack()
        );
        activation = await engine.CommitArtifactSetAsync(Selections(artifact));
        // A persisted activation cannot commit an artifact whose identity refers
        // to that activation without creating a content-address cycle. Exercise
        // the upper-bound predicate directly with the activation address.
        DerivedRecapArtifact afterCoverage =
            artifact.Autobiography with { SourceRawHead = activation };
        int projectionCount = engine.FullProjectionInvocationCount;

        SessionJournalNotReadyException error =
            Assert.Throws<SessionJournalNotReadyException>(
                () => SessionJournalEngine.ValidateArtifactSourceHead(
                    new HashSet<EventAddress> { anchor },
                    afterCoverage
                )
            );

        Assert.Equal(
            SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
            error.Reason
        );
        Assert.Equal(afterCoverage.ArtifactId, error.ArtifactId);
        Assert.Equal(activation, engine.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, engine.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_StaleActivationCurrentSetupsIsRawCorruptionBeforeObservation() {
        string path = NewJournalPath();
        TestArtifactSet artifact;
        ArtifactSetCommittedBody staleBody;
        EventAddress setupMutation;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = setup.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                setup.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            EventAddress validActivation =
                await setup.CommitArtifactSetAsync(Selections(artifact));
            staleBody = ReadArtifactSetBody(setup, validActivation);
            setupMutation = setup.AppendSystemPromptSetup("system-B");
        }
        EventAddress staleActivation;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            staleActivation = Commit(
                journal,
                setupMutation,
                SessionEventKind.ArtifactSetCommitted,
                staleBody
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, artifact)
        );
        int projectionCount = reopened.FullProjectionInvocationCount;

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.SendAsync("must not append", CancellationToken.None)
            );

        Assert.Contains("currentSetups", error.Message, StringComparison.Ordinal);
        Assert.Equal(staleActivation, reopened.ResolveExecutionTail().Head);
        Assert.Equal(projectionCount, reopened.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task ResumeAsync_CrossBranchPreparedActivationReferenceIsRawCorruptionBeforeProvider() {
        string path = NewJournalPath();
        TestArtifactSet artifact;
        EventAddress mainActivation;
        ArtifactSetCommittedBody activationBody;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = setup.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                setup.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            mainActivation =
                await setup.CommitArtifactSetAsync(Selections(artifact));
            activationBody = ReadArtifactSetBody(setup, mainActivation);
        }
        EventAddress offBranchActivation;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            journal.CreateBranch("off-activation", mainActivation).Unwrap();
            offBranchActivation = CommitToBranch(
                journal,
                "off-activation",
                mainActivation,
                SessionEventKind.ArtifactSetCommitted,
                activationBody
            );
        }

        var client = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("must not call provider")
        );
        CompletionRequestPreparedBody validPreparedBody;
        SessionArtifactSetReference offBranchReference;
        EventAddress validPrepared;
        using (var preparing = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(client, artifact),
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
            offBranchReference = CreateArtifactSetReference(
                preparing,
                offBranchActivation
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
                        ActiveArtifactSet = offBranchReference
                    }
                }
            );
        }

        SessionRuntime recoveryRuntime = CreateRuntime(client, artifact) with {
            PreparedCompletionRecoveryPolicy =
                SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
        };
        using var reopened = SessionJournalEngine.Open(path, recoveryRuntime);
        int projectionCount = reopened.FullProjectionInvocationCount;

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

        Assert.Contains("latest activation", error.Message, StringComparison.Ordinal);
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
        TestArtifactSet artifact;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            EventAddress anchor = engine.Project().Head!.Value;
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                engine.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            await engine.CommitArtifactSetAsync(Selections(artifact));
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
            engine.UseRuntime(CreateRuntime(client, artifact));

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
    public async Task SendAsync_FailedBoundaryAttemptMismatch_FailsLocalCausalityProof() {
        string path = NewJournalPath();
        var sourceClient = new CapturingCompletionClient(
            _ => throw new InvalidOperationException("failpoint must run first")
        );
        EventAddress prepared;
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateFullRuntime(sourceClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
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
                    "different-attempt",
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

        Assert.Contains("does not match active attempt", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_ArtifactSourceHeadEarlierThanAnchor_FailsBeforeProvider() {
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
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(anchor);
        TestArtifactSet artifact = await WriteArtifactAsync(
            path,
            anchor,
            sourceRawHead: setup.RuntimeConfigSetupAddress,
            setup,
            CreateMemoryPack()
        );
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await engine.CommitArtifactSetAsync(
                Selections(artifact)
            )
        );

        Assert.Contains("sourceRawHead", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("tool-action")]
    [InlineData("tool-result")]
    public async Task SendAsync_ImportedToolResultContinuation_RejectsNonReplaySafeArtifactAnchorBeforeProvider(
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
        TestArtifactSet artifact;
        InvalidDataException error;
        using (var engine = SessionJournalEngine.Open(path)) {
            SessionGoverningSetup setup = engine.ResolveGoverningSetup(anchor);
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: finalAction,
                setup,
                CreateMemoryPack()
            );
            error = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await engine.CommitArtifactSetAsync(
                    Selections(artifact)
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
    public void ExpandContextSnapshot_MatchesLegacyFixedRendererAndNeverReturnsHeaderMessage() {
        var snapshot = new SessionRequestArtifactContextSnapshot(
            "  memory system  ",
            "memory observation",
            "memory action"
        );

        var (systemPrompt, context) = SessionTailContextProjection.ExpandContextSnapshot(
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

    private static MemoryPack CreateMemoryPack() {
        var memoryPack = new MemoryPack();
        memoryPack.System.Add("stale.system", new MemoryPackBlock("stale system"));
        memoryPack.Observation.Add(
            "roleplay.world-understanding",
            new MemoryPackBlock("memory observation")
        );
        memoryPack.Observation.Add(
            "stale.observation",
            new MemoryPackBlock("stale observation")
        );
        memoryPack.Action.Add(
            "roleplay.first-person-autobiography",
            new MemoryPackBlock("memory action")
        );
        memoryPack.Action.Add("stale.action", new MemoryPackBlock("stale action"));
        return memoryPack;
    }

    private static async ValueTask<TestArtifactSet> WriteArtifactAsync(
        string path,
        EventAddress anchor,
        EventAddress sourceRawHead,
        SessionGoverningSetup setup,
        MemoryPack memoryPack
    ) {
        var autobiographyTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.Action,
            "roleplay.first-person-autobiography"
        );
        DerivedRecapArtifact autobiographyArtifact = await DerivedRecapStore.Open(path).WriteProducedAsync(
            new DerivedRecapWriteRequest(
                ArtifactKind: "autobiography",
                ProfileId: "tail-tests-autobiography",
                Producer: "tests",
                ProducerFingerprint: "tail-tests-v1",
                SourceRawHead: sourceRawHead,
                SourceStartExclusive: null,
                SourceEndInclusive: anchor,
                AnchorRawEvent: anchor,
                GoverningRuntimeConfigSetup: setup.RuntimeConfigSetupAddress,
                GoverningSystemPromptSetup: setup.SystemPromptSetupAddress,
                PreviousArtifact: null,
                Target: autobiographyTarget,
                MemoryPack: memoryPack
            )
        );
        var target = new MemoryPackBlockPath(
            MemoryPackCarrier.Observation,
            "roleplay.world-understanding"
        );
        DerivedRecapArtifact worldUnderstandingArtifact =
            await DerivedRecapStore.Open(path).WriteProducedAsync(new DerivedRecapWriteRequest(
            ArtifactKind: "world-understanding",
            ProfileId: "tail-tests-world",
            Producer: "tests",
            ProducerFingerprint: "tail-tests-v1",
            SourceRawHead: sourceRawHead,
            SourceStartExclusive: null,
            SourceEndInclusive: anchor,
            AnchorRawEvent: anchor,
            GoverningRuntimeConfigSetup: setup.RuntimeConfigSetupAddress,
            GoverningSystemPromptSetup: setup.SystemPromptSetupAddress,
            PreviousArtifact: null,
            Target: target,
            MemoryPack: memoryPack
        ));
        return new TestArtifactSet(
            worldUnderstandingArtifact,
            autobiographyArtifact
        );
    }

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        TestArtifactSet artifact,
        ToolSession? toolSession = null,
        SessionToolRuntimeIdentity? toolRuntimeIdentity = null
    )
        => new(
            CompletionClient: client,
            ToolSession: toolSession,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "tail-connection",
                "test",
                "tail-connection-v1",
                "tail-adapter-v1"
            ),
            MaxTokens: 512,
            ToolRuntimeIdentity: toolRuntimeIdentity
        );

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

    private static ArtifactSetCommittedBody ReadArtifactSetBody(
        SessionJournalEngine engine,
        EventAddress address
    ) => Assert.IsType<ArtifactSetCommittedBody>(
        SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            engine.ReadPayloadBytes(address),
            out _
        )
    );

    private static SessionArtifactSetReference CreateArtifactSetReference(
        SessionJournalEngine engine,
        EventAddress address
    ) {
        byte[] payload = engine.ReadPayloadBytes(address);
        _ = SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            payload,
            out int version
        );
        return new SessionArtifactSetReference(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(payload)
        );
    }

    private static ArtifactSetCommittedBody CreateArtifactSetBody(
        SessionJournalEngine engine,
        EventAddress commonAnchor,
        SessionGoverningSetup coverageSetup,
        SessionGoverningSetup currentSetup,
        TestArtifactSet artifact
    ) {
        ImmutableArray<SessionArtifactSetMember> members = [
            .. new[] {
                (
                    RoleId: "world-understanding",
                    Artifact: artifact.WorldUnderstanding
                ),
                (
                    RoleId: "autobiography",
                    Artifact: artifact.Autobiography
                )
            }
                .OrderBy(static item => item.RoleId, StringComparer.Ordinal)
                .Select(static item => {
                    SessionRequestArtifactInput input =
                        SessionTailContextProjection.CreateArtifactInput(
                            item.Artifact
                        );
                    return new SessionArtifactSetMember(
                        item.RoleId,
                        item.Artifact.ArtifactId,
                        item.Artifact.ArtifactKind,
                        item.Artifact.Target,
                        input.ContentSha256
                    );
                })
        ];
        return new ArtifactSetCommittedBody(
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
            commonAnchor,
            CreateSetupReferences(engine, coverageSetup),
            CreateSetupReferences(engine, currentSetup),
            members
        );
    }

    private static SessionGoverningSetupReferences CreateSetupReferences(
        SessionJournalEngine engine,
        SessionGoverningSetup setup
    ) => new(
        CreateSetupReference(
            engine,
            setup.RuntimeConfigSetupAddress,
            SessionEventKind.RuntimeConfigSetup
        ),
        CreateSetupReference(
            engine,
            setup.SystemPromptSetupAddress,
            SessionEventKind.SystemPromptSetup
        )
    );

    private static SessionSetupReference CreateSetupReference(
        SessionJournalEngine engine,
        EventAddress address,
        SessionEventKind kind
    ) {
        byte[] payload = engine.ReadPayloadBytes(address);
        _ = SessionEventCodec.Decode(kind, payload, out int version);
        return new SessionSetupReference(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(payload)
        );
    }

    private static SessionToolRuntimeIdentity TestToolRuntimeIdentity { get; } =
        new("tail-tool-host", "tail-tools-v1", "tail-capabilities-v1");

    private sealed record TestArtifactSet(
        DerivedRecapArtifact WorldUnderstanding,
        DerivedRecapArtifact Autobiography
    );

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        string artifactId,
        ToolSession? toolSession = null
    ) => new(
        CompletionClient: client,
        ToolSession: toolSession,
        CompletionTarget: new SessionCompletionTargetIdentity(
            "tail-connection",
            "test",
            "tail-connection-v1",
            "tail-adapter-v1"
        ),
        MaxTokens: 512
    );

    private static SessionRuntime CreateFullRuntime(CapturingCompletionClient client)
        => new(
            CompletionClient: client,
            ToolSession: null,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "tail-connection",
                "test",
                "tail-connection-v1",
                "tail-adapter-v1"
            ),
            MaxTokens: 512,
            RequestContextPolicy: SessionRequestContextPolicy.LegacyFullRaw
        );

    private static SessionArtifactSetMemberSelection[] Selections(
        TestArtifactSet artifact
    ) => [
        new(
            "world-understanding",
            artifact.WorldUnderstanding.ArtifactId
        ),
        new("autobiography", artifact.Autobiography.ArtifactId)
    ];

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
        Func<CompletionRequest, CompletionResult> response
    ) : ICompletionClient {
        private readonly Func<CompletionRequest, CompletionResult> _response = response;

        public string Name => "tail-client";

        public string ApiSpecId => "tail-api-v1";

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

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
        DerivedRecapArtifact artifact;
        EventAddress anchor;
        EventAddress runtimeB;
        EventAddress promptB;
        RenderedMemoryPack rendered;
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
            rendered = memoryPack.Render();
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
            engine.UseRuntime(CreateRuntime(client, artifact.ArtifactId));
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
        Assert.Equal($"system-B\n\n{rendered.SystemPromptFragment}", request.SystemPrompt);
        Assert.DoesNotContain(request.Context, static message => message is SessionContextHeader);
        Assert.Collection(
            request.Context,
            first => Assert.Equal(rendered.ObservationMessage, Assert.IsType<ObservationMessage>(first).Content),
            second => Assert.Equal(rendered.ActionMessage, Assert.IsType<ActionMessage>(second).GetFlattenedText()),
            third => Assert.Equal("new observation", Assert.IsType<ObservationMessage>(third).Content)
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
        Assert.Equal(SessionRequestManifestDefaults.ExplicitArtifactTailSelectionPolicyId, manifest.Plan.SelectionPolicyId);
        Assert.Equal(anchor, manifest.Plan.RawStartExclusive);
        SessionRequestArtifactInput input = Assert.Single(manifest.Plan.ArtifactInputs);
        Assert.Equal(artifact.ArtifactId, input.ArtifactId);
        Assert.Equal(rendered.SystemPromptFragment, input.ContextSnapshot.SystemPromptFragment);
        Assert.Equal(rendered.ObservationMessage, input.ContextSnapshot.ObservationMessage);
        Assert.Equal(rendered.ActionMessage, input.ContextSnapshot.ActionMessage);
        Assert.Equal(SessionArtifactContextSnapshotHasher.ComputeSha256(input.ContextSnapshot), input.ContentSha256);
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(request), manifest.Commitment);
        Assert.Equal(runtimeB, manifest.Setups.RuntimeConfig.Address);
        Assert.Equal(promptB, manifest.Setups.SystemPrompt.Address);
    }

    [Fact]
    public async Task ResumeAsync_ExactObservationTail_DoesNotInvokeFullProjection() {
        string path = NewJournalPath();
        DerivedRecapArtifact artifact;
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
            engine.AppendObservation("resume observation");
        }

        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("resumed answer")]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(client, artifact.ArtifactId));
        int projectionCountBeforeResume = reopened.FullProjectionInvocationCount;

        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed answer", outcome.Message?.GetFlattenedText());
        Assert.Equal(projectionCountBeforeResume, reopened.FullProjectionInvocationCount);
        Assert.Single(client.Requests);
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

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains("idle boundary", error.Message, StringComparison.Ordinal);
        Assert.Equal(projectionCountBeforeResume, engine.FullProjectionInvocationCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task SendAsync_TailRuntimeWithVisibleTools_FailsBeforeObservationOrFullProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        ToolSession toolSession = new ToolRegistry([new NoopTool()]).CreateSession();
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, "missing-artifact", toolSession)
        )) {
            int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
                () => engine.SendAsync("new observation", CancellationToken.None)
            );

            Assert.Contains("without tools", error.Message, StringComparison.Ordinal);
            Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);
            Assert.Empty(client.Requests);
        }
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.ObservationAccepted));
    }

    [Fact]
    public async Task SendAsync_TailProviderToolCall_FailsBeforeActionCommitWithoutFullProjection() {
        string path = NewJournalPath();
        var client = new CapturingCompletionClient(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
            ]),
            new CompletionDescriptor("tail-client", "tail-api-v1", request.ModelId)
        ));
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            EventAddress anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            DerivedRecapArtifact artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: anchor,
                engine.ResolveGoverningSetup(anchor),
                CreateMemoryPack()
            );
            engine.UseRuntime(CreateRuntime(client, artifact.ArtifactId));
            int projectionCountBeforeSend = engine.FullProjectionInvocationCount;

            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
                () => engine.SendAsync("new observation", CancellationToken.None)
            );

            Assert.Contains("tool calls", error.Message, StringComparison.Ordinal);
            Assert.Equal(projectionCountBeforeSend, engine.FullProjectionInvocationCount);
        }
        Assert.Single(client.Requests);
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.AgentActionProduced));
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
        DerivedRecapArtifact artifact = await WriteArtifactAsync(
            path,
            anchor,
            sourceRawHead: setup.RuntimeConfigSetupAddress,
            setup,
            CreateMemoryPack()
        );
        engine.UseRuntime(CreateRuntime(client, artifact.ArtifactId));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SendAsync("new", CancellationToken.None)
        );

        Assert.Contains("sourceRawHead", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("tool-action")]
    [InlineData("tool-result")]
    public async Task SendAsync_UnsafeArtifactBoundary_FailsBeforeProvider(string boundary) {
        string path = NewJournalPath();
        EventAddress toolAction;
        EventAddress toolResult;
        EventAddress finalAction;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("use tool");
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
                new ToolExecutionStartedBody("call-1", "lookup", "{}", "op-1")
            );
            toolResult = Commit(
                journal,
                started,
                SessionEventKind.ToolResultObserved,
                new ToolResultObservedBody(
                    "call-1",
                    "lookup",
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
                    new CompletionDescriptor("import", "import-v1", "model-A")
                )
            );
        }

        EventAddress anchor = boundary == "tool-action" ? toolAction : toolResult;
        DerivedRecapArtifact artifact;
        using (var engine = SessionJournalEngine.Open(path)) {
            SessionGoverningSetup setup = engine.ResolveGoverningSetup(anchor);
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                sourceRawHead: finalAction,
                setup,
                CreateMemoryPack()
            );
        }

        var client = new CapturingCompletionClient(_ => throw new InvalidOperationException("must not call provider"));
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(client, artifact.ArtifactId));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => reopened.SendAsync("next", CancellationToken.None)
        );

        Assert.Contains("anchor", error.Message, StringComparison.OrdinalIgnoreCase);
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
        memoryPack.System.Add("policy", new MemoryPackBlock("memory system"));
        memoryPack.Observation.Add("summary", new MemoryPackBlock("memory observation"));
        memoryPack.Action.Add("self", new MemoryPackBlock("memory action"));
        return memoryPack;
    }

    private static async ValueTask<DerivedRecapArtifact> WriteArtifactAsync(
        string path,
        EventAddress anchor,
        EventAddress sourceRawHead,
        SessionGoverningSetup setup,
        MemoryPack memoryPack
    ) {
        var target = new MemoryPackBlockPath(MemoryPackCarrier.Observation, "summary");
        return await DerivedRecapStore.Open(path).WriteProducedAsync(new DerivedRecapWriteRequest(
            ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
            ProfileId: "tail-tests",
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
    }

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        string artifactId,
        ToolSession? toolSession = null
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
            TailProjection: new SessionTailProjectionOptions(artifactId)
        );

    private sealed class NoopTool : ITool {
        public ToolDefinition Definition { get; } =
            new("noop", "No-op tool.", new ToolSchema.Object());

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

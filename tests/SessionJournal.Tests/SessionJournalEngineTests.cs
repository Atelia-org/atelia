using System.Security.Cryptography;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalEngineTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "test-tool-host",
        "test-tool-implementations-v1",
        "test-tool-capabilities-v1"
    );
    private readonly List<string> _tempDirectories = new();
    private readonly TestContextCandidateSource _candidateSource = new();

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
    public void Create_WritesSetupEventsThenSessionCreatedAndRestoresStateFromJournal() {
        string path = NewJournalPath();

        SessionExecutionBoundaryInspection boundary;
        SessionGoverningSetup setup;
        SessionHistoryPlanningWindow history;
        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            boundary = engine.InspectExecutionBoundary();
            setup = engine.ResolveGoverningSetup(
                boundary.Head!.Value
            );
            history = engine.ReadHistoryPlanningWindow();
        }

        Assert.NotNull(boundary.Head);
        string[] payloads = ReadJournalPayloadJson(path);
        Assert.Equal(3, payloads.Length);
        Assert.Equal("{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}", payloads[0]);
        Assert.Equal("{\"v\":1,\"body\":{\"content\":\"system-A\"}}", payloads[1]);
        Assert.Equal(
            "{\"v\":2,\"body\":{\"origin\":\"native\"}}",
            payloads[2]
        );
        Assert.Equal("model-A", setup.RuntimeConfig.ModelId);
        Assert.Equal("system-A", setup.SystemPrompt);
        Assert.Equal(
            "surface-A",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal(
            SessionJournalDefaults.Schema,
            setup.RuntimeConfig.Schema
        );
        Assert.Empty(history.Units);
        Assert.Equal(SessionExecutionPhase.Idle, boundary.Phase);
        Assert.Equal(SessionEventKind.SessionCreated, boundary.HeadKind);
    }

    [Fact]
    public void RuntimeConfigSetupV1_IsRejectedWithoutFallback() {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}"
        );

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => SessionEventCodec.Decode(
                SessionEventKind.RuntimeConfigSetup,
                payload,
                out _
            )
        );

        Assert.Contains("actual=1, expected=2", error.Message);
    }

    [Theory]
    [InlineData("{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}")]
    [InlineData("{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0},\"extra\":true}}")]
    [InlineData("{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0,\"extra\":true}}}")]
    [InlineData("{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":-1}}}")]
    public void RuntimeConfigSetupV2_RequiresStrictNonNegativeDerivedContext(
        string json
    ) {
        Assert.Throws<InvalidDataException>(
            () => SessionEventCodec.Decode(
                SessionEventKind.RuntimeConfigSetup,
                System.Text.Encoding.UTF8.GetBytes(json),
                out _
            )
        );
    }

    [Fact]
    public void Open_SelectedBranchBindsIdentityAndIsolatesProjectionLineageAndPlanning() {
        string path = NewJournalPath();
        EventAddress mainHead;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            Assert.Equal(SessionJournalDefaults.MainBranchName, created.BranchName);
            mainRef = created.BranchRefId;
            mainHead = created.InspectExecutionBoundary().Head!.Value;
        }

        RefId featureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                mainHead
            ).Unwrap();
        }

        EventAddress featureHead;
        using (var feature = SessionJournalEngine.Open(path, "feature")) {
            Assert.Equal("feature", feature.BranchName);
            Assert.Equal(featureRef, feature.BranchRefId);
            feature.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration(
                    "model-feature",
                    "surface-feature",
                    SessionJournalDefaults.Schema,
                    new(0)
                )
            );
            featureHead = feature.AppendSystemPromptSetup("system-feature");

            SessionGoverningSetup featureSetup =
                feature.ResolveGoverningSetup(featureHead);
            Assert.Equal(
                "model-feature",
                featureSetup.RuntimeConfig.ModelId
            );
            Assert.Equal("system-feature", featureSetup.SystemPrompt);
            Assert.Equal(
                featureHead,
                feature.ReadCurrentLineageHeaders().CapturedHead
            );
            SessionHistoryPlanningSeedBatch seeds =
                feature.ReadHistoryPlanningSeeds([featureHead]);
            Assert.Equal(featureHead, seeds.Lineage.CapturedHead);
            Assert.Equal(
                "model-feature",
                Assert.Single(seeds.Seeds).GoverningSetup.RuntimeConfig.ModelId
            );
        }

        using var main = SessionJournalEngine.Open(path);
        Assert.Equal(SessionJournalDefaults.MainBranchName, main.BranchName);
        Assert.Equal(mainRef, main.BranchRefId);
        Assert.Equal(mainHead, main.InspectExecutionBoundary().Head);
        SessionGoverningSetup mainSetup =
            main.ResolveGoverningSetup(mainHead);
        Assert.Equal("model-A", mainSetup.RuntimeConfig.ModelId);
        Assert.Equal("system-A", mainSetup.SystemPrompt);
    }

    [Fact]
    public void OpenReadOnly_SelectedBranchBindsAndRejectsMutationWithoutFileChanges() {
        string path = NewJournalPath();
        EventAddress mainHead;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            Assert.False(created.IsReadOnly);
            mainHead = created.InspectExecutionBoundary().Head!.Value;
            mainRef = created.BranchRefId;
        }
        RefId featureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                mainHead
            ).Unwrap();
        }
        using (SessionJournalEngine writable =
               SessionJournalEngine.Open(path, "feature")) {
            Assert.False(writable.IsReadOnly);
        }
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(path);

        using (SessionJournalEngine readOnly =
               SessionJournalEngine.OpenReadOnly(path, "feature")) {
            Assert.True(readOnly.IsReadOnly);
            Assert.Equal("feature", readOnly.BranchName);
            Assert.Equal(featureRef, readOnly.BranchRefId);
            Assert.Equal(
                mainHead,
                readOnly.InspectExecutionBoundary().Head
            );
            AssertReadOnlyMutationRejected(
                () => readOnly.UseRuntime(
                    CreateRuntime(new ScriptedCompletionClient())
                ),
                nameof(SessionJournalEngine.UseRuntime)
            );
            AssertReadOnlyMutationRejected(
                () => readOnly.AppendObservation("must not append"),
                nameof(SessionJournalEngine.AppendObservation)
            );
            AssertReadOnlyMutationRejected(
                () => readOnly.AppendRuntimeConfigSetup(
                    new SessionRuntimeConfiguration(
                        "model-B",
                        "surface-B",
                        SessionJournalDefaults.Schema,
                        new(0)
                    )
                ),
                nameof(
                    SessionJournalEngine.AppendRuntimeConfigSetup
                )
            );
            AssertReadOnlyMutationRejected(
                () => readOnly.AppendSystemPromptSetup(
                    "must not append"
                ),
                nameof(
                    SessionJournalEngine.AppendSystemPromptSetup
                )
            );
            AssertReadOnlyMutationRejected(
                () => readOnly.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text("must not append")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-A"
                    )
                ),
                nameof(
                    SessionJournalEngine.AppendImportedAgentAction
                )
            );
        }

        Assert.Equal(before, CaptureRepositoryFiles(path));
    }

    [Fact]
    public async Task OpenReadOnly_SendAndResumeOverloadsFailBeforeRuntimeCollaborators() {
        string path = NewJournalPath();
        using (SessionJournalEngine created =
               SessionJournalEngine.Create(
                   path,
                   new SessionCreateOptions(
                       "model-A",
                       "system-A",
                       "surface-A"
                   )
               )) {
        }
        var client = new ScriptedCompletionClient();
        var candidateSource = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        var lifecycle = new TestContextLifecycle();
        SessionRuntime runtime = CreateRuntime(
            client,
            candidateSource: candidateSource
        ) with {
            ContextLifecycle = lifecycle
        };
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(path);

        using SessionJournalEngine readOnly =
            SessionJournalEngine.OpenReadOnlyForTest(
                path,
                runtime
            );
        await AssertReadOnlyMutationRejectedAsync(
            () => readOnly.SendAsync("must not send"),
            nameof(SessionJournalEngine.SendAsync)
        );
        await AssertReadOnlyMutationRejectedAsync(
            () => readOnly.SendAsync(
                "must not send",
                observer: null,
                CancellationToken.None
            ),
            nameof(SessionJournalEngine.SendAsync)
        );
        await AssertReadOnlyMutationRejectedAsync(
            () => readOnly.ResumeAsync(),
            nameof(SessionJournalEngine.ResumeAsync)
        );
        await AssertReadOnlyMutationRejectedAsync(
            () => readOnly.ResumeAsync(
                observer: null,
                CancellationToken.None
            ),
            nameof(SessionJournalEngine.ResumeAsync)
        );

        Assert.Equal(0, lifecycle.InvocationCount);
        Assert.Equal(0, candidateSource.SelectionCount);
        Assert.Equal(0, candidateSource.MaterializationCount);
        Assert.Equal(0, client.Calls);
        Assert.Equal(before, CaptureRepositoryFiles(path));
    }

    [Fact]
    public void OpenReadOnly_MalformedEventTailFailsWithoutRecovery() {
        string path = NewJournalPath();
        using (SessionJournalEngine created =
               SessionJournalEngine.Create(
                   path,
                   new SessionCreateOptions(
                       "model-A",
                       "system-A",
                       "surface-A"
                   )
               )) {
            created.AppendObservation("observation");
        }
        string activeEventPath = Directory.EnumerateFiles(
                Path.Combine(path, "events"),
                "*.rbf",
                SearchOption.AllDirectories
            )
            .Single();
        File.AppendAllBytes(
            activeEventPath,
            new byte[] { 0, 0, 0, 0 }
        );
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(path);

        _ = Assert.ThrowsAny<Exception>(
            () => SessionJournalEngine.OpenReadOnly(path)
        );

        Assert.Equal(before, CaptureRepositoryFiles(path));
    }

    [Fact]
    public void Open_MissingOrArchivedBranchFailsBeforeEventMutation() {
        string path = NewJournalPath();
        EventAddress mainHead;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            mainRef = created.BranchRefId;
            mainHead = created.InspectExecutionBoundary().Head!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId archivedRef = journal.ForkBranch(
                "archived",
                mainRef,
                mainHead
            ).Unwrap();
            Assert.True(journal.ArchiveRef(archivedRef, mainHead).Unwrap());
        }
        long eventBytesBefore = GetEventStoreLength(path);

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
            () => SessionJournalEngine.Open(path, "missing")
        );
        InvalidOperationException archivedError = Assert.Throws<InvalidOperationException>(
            () => SessionJournalEngine.Open(path, "archived")
        );

        Assert.Contains("EventJournal.BranchNotFound", missing.Message);
        Assert.Contains("EventJournal.BranchNotFound", archivedError.Message);
        Assert.Equal(eventBytesBefore, GetEventStoreLength(path));
        using var journalAfter = EventJournal.EventJournal.OpenExisting(path);
        Assert.Equal(mainHead, journalAfter.GetHead(mainRef));
    }

    [Fact]
    public void Open_AfterDisposedEngineObservesExternalMoveOfSelectedBranch() {
        string path = NewJournalPath();
        EventAddress forkPoint;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            mainRef = created.BranchRefId;
            forkPoint = created.InspectExecutionBoundary().Head!.Value;
        }
        RefId featureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                forkPoint
            ).Unwrap();
        }

        EventAddress advancedHead;
        using (var feature = SessionJournalEngine.Open(path, "feature")) {
            advancedHead = feature.AppendObservation("temporary feature work");
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.True(journal.MoveRef(
                featureRef,
                advancedHead,
                forkPoint
            ).Unwrap());
        }

        using var reopened = SessionJournalEngine.Open(path, "feature");
        Assert.Equal(featureRef, reopened.BranchRefId);
        Assert.Equal(forkPoint, reopened.InspectExecutionBoundary().Head);
        Assert.Empty(reopened.ReadHistoryPlanningWindow().Units);
    }

    [Fact]
    public async Task SelectedBranch_SendThenResumeAdvancesOnlyBoundRef() {
        string path = NewJournalPath();
        EventAddress mainHead;
        RefId mainRef;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            mainRef = created.BranchRefId;
            mainHead = created.InspectExecutionBoundary().Head!.Value;
        }
        RefId featureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                mainHead
            ).Unwrap();
        }

        _candidateSource.IsEmptyLineage = true;
        var client = new ScriptedCompletionClient();
        SessionRuntime runtime = CreateRuntime(client);
        EventAddress preparedHead;
        using (var preparing = SessionJournalEngine.OpenForTest(
            path,
            "feature",
            runtime,
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<SessionJournalFailpointException>(
                    () => preparing.SendAsync(
                        "feature observation",
                        CancellationToken.None
                    )
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterRequestPreparedCommitted,
                error.Failpoint
            );
            preparedHead = preparing.InspectExecutionBoundary().Head!.Value;
            Assert.Equal(0, client.Calls);
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.Equal(mainHead, journal.GetHead(mainRef));
            Assert.Equal(preparedHead, journal.GetHead(featureRef));
        }

        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("feature resumed")]),
            new CompletionDescriptor(
                "scripted",
                "test-api-v1",
                request.ModelId
            )
        ));
        using (var resumed = SessionJournalEngine.Open(
            path,
            "feature",
            runtime with {
                ContextCandidateSource = null,
                ContextLifecycle = null
            }
        )) {
            ResumeOutcome outcome = await resumed.ResumeAsync(
                CancellationToken.None
            );
            Assert.True(outcome.Advanced);
            Assert.Equal(
                "feature resumed",
                outcome.Message!.GetFlattenedText()
            );
            Assert.Equal(SessionExecutionPhase.Idle, resumed.ResolveExecutionTail().State.Phase);
        }

        using var journalAfter = EventJournal.EventJournal.OpenExisting(path);
        Assert.Equal(mainHead, journalAfter.GetHead(mainRef));
        Assert.NotEqual(preparedHead, journalAfter.GetHead(featureRef));
    }

    [Fact]
    public void AppendObservationAndAction_ReopenReadsHistoryAndConfigFromJournal() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("answer"),
                new ActionBlock.Text(" continued")
        }
        );

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("hello");
            engine.AppendImportedAgentAction(action, invocation);
        }

        Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.ImportedAgentAction));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionRecovery recovery =
            reopened.ResolveExecutionTail();
        SessionGoverningSetup setup =
            reopened.ResolveGoverningSetup(recovery.Head!.Value);
        IReadOnlyList<SessionHistoryPlanningUnit> units =
            reopened.ReadHistoryPlanningWindow().Units;

        Assert.Equal("model-A", setup.RuntimeConfig.ModelId);
        Assert.Equal("system-A", setup.SystemPrompt);
        Assert.Equal(
            "surface-A",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal(2, units.Count);

        var observation =
            Assert.IsType<ObservationMessage>(units[0].Message);
        Assert.Equal("hello", observation.Content);

        var projectedAction =
            Assert.IsType<ActionMessage>(units[1].Message);
        Assert.Equal("answer continued", projectedAction.GetFlattenedText());
        Assert.Empty(projectedAction.ToolCalls);
        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
        Assert.Equal(
            SessionEventKind.ImportedAgentAction,
            recovery.State.HeadKind
        );
    }

    [Fact]
    public void HistoryPlanningWindow_ObservationAndAction_CarriesSourceAddresses() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(
            [
                new ActionBlock.Text("answer"),
                new ActionBlock.Text(" continued")
            ]
        );

        EventAddress observationAddress;
        EventAddress actionAddress;
        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            observationAddress = engine.AppendObservation("hello");
            actionAddress = engine.AppendImportedAgentAction(action, invocation);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionHistoryPlanningWindow window =
            reopened.ReadHistoryPlanningWindow();

        Assert.Equal(
            reopened.InspectExecutionBoundary().Head,
            window.ObservedRawHead
        );
        Assert.Equal(2, window.Units.Count);

        SessionHistoryPlanningUnit observationEntry = window.Units[0];
        var observation = Assert.IsType<ObservationMessage>(observationEntry.Message);
        Assert.Equal("hello", observation.Content);
        Assert.Equal(observationAddress, observationEntry.SourceStartInclusive);
        Assert.Equal(observationAddress, observationEntry.SourceEndInclusive);

        SessionHistoryPlanningUnit actionEntry = window.Units[1];
        var projectedAction = Assert.IsType<ActionMessage>(actionEntry.Message);
        Assert.Equal("answer continued", projectedAction.GetFlattenedText());
        Assert.Equal(actionAddress, actionEntry.SourceStartInclusive);
        Assert.Equal(actionAddress, actionEntry.SourceEndInclusive);
    }

    [Fact]
    public void HistoryPlanningWindow_SetupAndSessionCreated_DoNotEmitHistoryMessages() {
        string path = NewJournalPath();
        EventAddress setupHead;

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            setupHead = engine.AppendSystemPromptSetup("system-B");
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionHistoryPlanningWindow window =
            reopened.ReadHistoryPlanningWindow();
        SessionExecutionBoundaryInspection boundary =
            reopened.InspectExecutionBoundary();

        Assert.Empty(window.Units);
        Assert.Equal(setupHead, window.ObservedRawHead);
        Assert.Equal(SessionExecutionPhase.Idle, boundary.Phase);
        Assert.Equal(
            SessionEventKind.SystemPromptSetup,
            boundary.HeadKind
        );
    }

    [Fact]
    public void AppendRuntimeConfigSetup_ReopenReplacesConfigAndKeepsContext() {
        string path = NewJournalPath();

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("hello");
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A")
            );
            EventAddress address = engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0))
            );

            string configJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(address));
            Assert.Equal("{\"v\":2,\"body\":{\"modelId\":\"model-B\",\"completionSurfaceId\":\"surface-B\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}", configJson);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionRecovery recovery =
            reopened.ResolveExecutionTail();
        SessionGoverningSetup setup =
            reopened.ResolveGoverningSetup(recovery.Head!.Value);
        IReadOnlyList<SessionHistoryPlanningUnit> units =
            reopened.ReadHistoryPlanningWindow().Units;

        Assert.Equal("model-B", setup.RuntimeConfig.ModelId);
        Assert.Equal("system-A", setup.SystemPrompt);
        Assert.Equal(
            "surface-B",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal(2, units.Count);
        Assert.Equal(
            "hello",
            Assert.IsType<ObservationMessage>(units[0].Message).Content
        );
        Assert.Equal(
            "answer",
            Assert.IsType<ActionMessage>(
                units[1].Message
            ).GetFlattenedText()
        );
        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
        Assert.Equal(
            SessionEventKind.RuntimeConfigSetup,
            recovery.State.HeadKind
        );
    }

    [Fact]
    public void AppendSystemPromptSetup_ReopenReplacesPromptAndKeepsContext() {
        string path = NewJournalPath();

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("hello");
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A")
            );
            EventAddress address = engine.AppendSystemPromptSetup("system-B");

            string promptJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(address));
            Assert.Equal("{\"v\":1,\"body\":{\"content\":\"system-B\"}}", promptJson);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionRecovery recovery =
            reopened.ResolveExecutionTail();
        SessionGoverningSetup setup =
            reopened.ResolveGoverningSetup(recovery.Head!.Value);
        IReadOnlyList<SessionHistoryPlanningUnit> units =
            reopened.ReadHistoryPlanningWindow().Units;

        Assert.Equal("model-A", setup.RuntimeConfig.ModelId);
        Assert.Equal("system-B", setup.SystemPrompt);
        Assert.Equal(
            "surface-A",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal(2, units.Count);
        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
        Assert.Equal(
            SessionEventKind.SystemPromptSetup,
            recovery.State.HeadKind
        );
    }

    [Fact]
    public void ResolveGoverningSetup_FromHeadFindsLatestSetupWithoutReadingIntermediatePayloads() {
        string path = NewJournalPath();
        EventAddress runtimeA;
        EventAddress promptA;
        EventAddress runtimeB;
        EventAddress promptB;

        using (var journal = EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            runtimeA = CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}");
            promptA = CommitToMain(journal, runtimeA, SessionEventKind.SystemPromptSetup, "{\"v\":1,\"body\":{\"content\":\"system-A\"}}");
            EventAddress created = CommitToMain(journal, promptA, SessionEventKind.SessionCreated, "{\"v\":1,\"body\":{}}");
            EventAddress malformedObservation = CommitToMain(journal, created, SessionEventKind.ObservationAccepted, "this is intentionally not json");
            runtimeB = CommitToMain(journal, malformedObservation, SessionEventKind.RuntimeConfigSetup, "{\"v\":2,\"body\":{\"modelId\":\"model-B\",\"completionSurfaceId\":\"surface-B\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}");
            EventAddress malformedAction = CommitToMain(journal, runtimeB, SessionEventKind.AgentActionProduced, "also intentionally not json");
            promptB = CommitToMain(journal, malformedAction, SessionEventKind.SystemPromptSetup, "{\"v\":1,\"body\":{\"content\":\"system-B\"}}");
        }

        using var engine = SessionJournalEngine.Open(path);
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(promptB);

        Assert.Equal(promptB, setup.Head);
        Assert.Equal(runtimeB, setup.RuntimeConfigSetupAddress);
        Assert.Equal(promptB, setup.SystemPromptSetupAddress);
        Assert.Equal("model-B", setup.RuntimeConfig.ModelId);
        Assert.Equal("surface-B", setup.RuntimeConfig.CompletionSurfaceId);
        Assert.Equal(SessionJournalDefaults.Schema, setup.RuntimeConfig.Schema);
        Assert.Equal("system-B", setup.SystemPrompt);
        Assert.NotEqual(runtimeA, setup.RuntimeConfigSetupAddress);
        Assert.NotEqual(promptA, setup.SystemPromptSetupAddress);
    }

    [Fact]
    public void ResolveGoverningSetup_WhenSetupIsMissing_Throws() {
        string missingPromptPath = NewJournalPath();
        EventAddress runtimeOnlyHead;
        using (var journal = EventJournal.EventJournal.CreateNew(missingPromptPath)) {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            runtimeOnlyHead = CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}");
        }

        using (var engine = SessionJournalEngine.Open(missingPromptPath)) {
            var ex = Assert.Throws<InvalidDataException>(() => engine.ResolveGoverningSetup(runtimeOnlyHead));
            Assert.Contains("missing system-prompt-setup", ex.Message, StringComparison.Ordinal);
        }

        string missingRuntimePath = NewJournalPath();
        EventAddress promptOnlyHead;
        using (var journal = EventJournal.EventJournal.CreateNew(missingRuntimePath)) {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            promptOnlyHead = CommitToMain(journal, null, SessionEventKind.SystemPromptSetup, "{\"v\":1,\"body\":{\"content\":\"system-A\"}}");
        }

        using (var engine = SessionJournalEngine.Open(missingRuntimePath)) {
            var ex = Assert.Throws<InvalidDataException>(() => engine.ResolveGoverningSetup(promptOnlyHead));
            Assert.Contains("missing runtime-config-setup", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ResolveGoverningSetup_UsesRecentPreparedCheckpointAndMergesOneSidedUpdates() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            _candidateSource
        );
        await engine.SendAsync("hello", CancellationToken.None);

        EventAddress actionHead = engine.InspectExecutionBoundary().Head!.Value;
        SessionGoverningSetup fromCheckpoint = engine.ResolveGoverningSetup(actionHead);
        Assert.Equal("model-A", fromCheckpoint.RuntimeConfig.ModelId);
        Assert.Equal("system-A", fromCheckpoint.SystemPrompt);
        Assert.Equal(3, engine.LastGoverningSetupResolutionDiagnostics.HeaderVisitCount);
        Assert.Equal(1, engine.LastGoverningSetupResolutionDiagnostics.ManifestPayloadReadCount);

        EventAddress runtimeB = engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0))
        );
        SessionGoverningSetup runtimeMerged = engine.ResolveGoverningSetup(runtimeB);
        Assert.Equal(runtimeB, runtimeMerged.RuntimeConfigSetupAddress);
        Assert.Equal("model-B", runtimeMerged.RuntimeConfig.ModelId);
        Assert.Equal("system-A", runtimeMerged.SystemPrompt);
        Assert.Equal(1, engine.LastGoverningSetupResolutionDiagnostics.ManifestPayloadReadCount);

        EventAddress promptB = engine.AppendSystemPromptSetup("system-B");
        SessionGoverningSetup bothDirect = engine.ResolveGoverningSetup(promptB);
        Assert.Equal(runtimeB, bothDirect.RuntimeConfigSetupAddress);
        Assert.Equal(promptB, bothDirect.SystemPromptSetupAddress);
        Assert.Equal("system-B", bothDirect.SystemPrompt);
        Assert.Equal(2, engine.LastGoverningSetupResolutionDiagnostics.HeaderVisitCount);
        Assert.Equal(0, engine.LastGoverningSetupResolutionDiagnostics.ManifestPayloadReadCount);

        Assert.ThrowsAny<Exception>(() => engine.ResolveGoverningSetup(default));
        Assert.Equal(default, engine.LastGoverningSetupResolutionDiagnostics);
    }

    [Fact]
    public async Task ResolveGoverningSetup_UsesCheckpointRuntimeAfterPromptOnlyUpdate() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            _candidateSource
        );
        await engine.SendAsync("hello", CancellationToken.None);
        EventAddress promptB = engine.AppendSystemPromptSetup("system-B");

        SessionGoverningSetup setup = engine.ResolveGoverningSetup(promptB);
        Assert.Equal("model-A", setup.RuntimeConfig.ModelId);
        Assert.Equal(promptB, setup.SystemPromptSetupAddress);
        Assert.Equal("system-B", setup.SystemPrompt);
        Assert.Equal(1, engine.LastGoverningSetupResolutionDiagnostics.ManifestPayloadReadCount);
    }

    [Fact]
    public async Task GoverningSetupCursor_CreateBinds_OpenIsLazy_AndPreparedPlanningBindsExactHead() {
        string path = NewJournalPath();
        EventAddress observation;
        SessionContextCandidate candidate;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            Assert.Equal(created.InspectExecutionBoundary().Head, created.GoverningSetupCursorHeadForTest);
            ActivatedCoherentArtifactSet activated =
                await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                created,
                _candidateSource
            );
            candidate = activated.Candidate;
            observation = created.AppendObservation("hello");
            Assert.Equal(observation, created.GoverningSetupCursorHeadForTest);
        }

        var client = new ScriptedCompletionClient();
        using var reopened = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(client, contextCandidate: candidate),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        );
        Assert.Null(reopened.GoverningSetupCursorHeadForTest);
        _ = reopened.ResolveGoverningSetup(observation);
        Assert.Null(reopened.GoverningSetupCursorHeadForTest);

        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );
        Assert.Equal(reopened.InspectExecutionBoundary().Head, reopened.GoverningSetupCursorHeadForTest);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("kind")]
    [InlineData("schema")]
    public async Task ResolveGoverningSetup_CorruptCheckpointReference_FailsFast(string corruption) {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        EventAddress actionHead;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, candidateSource: candidateSource)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );
            await engine.SendAsync("hello", CancellationToken.None);
            actionHead = engine.InspectExecutionBoundary().Head!.Value;
        }

        EventAddress prepared = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        CompletionRequestPreparedBody sourceManifest;
        using (var inspection = SessionJournalEngine.Open(path)) {
            sourceManifest = Assert.IsType<CompletionRequestPreparedBody>(
                SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, inspection.ReadPayloadBytes(prepared), out _)
            );
        }

        EventAddress corruptHead;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            SessionSetupReference runtimeRef = sourceManifest.Setups.RuntimeConfig;
            if (corruption == "hash") {
                runtimeRef = runtimeRef with { PayloadSha256 = new string('0', 64) };
            }
            else if (corruption == "schema") {
                runtimeRef = runtimeRef with { BodySchemaVersion = checked(runtimeRef.BodySchemaVersion + 1) };
            }
            else {
                using EventFrame actionFrame = journal.ReadEvent(actionHead).Unwrap();
                runtimeRef = new SessionSetupReference(
                    actionHead,
                    BodySchemaVersion: 1,
                    SessionRequestCanonicalizer.Sha256Hex(actionFrame.Payload)
                );
            }

            CompletionRequestPreparedBody corrupt = sourceManifest with {
                Setups = sourceManifest.Setups with { RuntimeConfig = runtimeRef }
            };
            corruptHead = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                actionHead,
                SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, corrupt),
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared,
                hint: default
            ).Unwrap().EventAddress;
        }

        using var reopened = SessionJournalEngine.Open(path);
        Assert.Throws<InvalidDataException>(() => reopened.ResolveGoverningSetup(corruptHead));
    }

    [Fact]
    public void AppendSetupEvents_WhenNotIdle_Throw() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        );

        engine.AppendObservation("hello");

        var configEx = Assert.Throws<InvalidOperationException>(
            () => engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0))
            )
        );
        Assert.Contains("requires an idle or explicitly failed turn boundary", configEx.Message, StringComparison.Ordinal);

        var promptEx = Assert.Throws<InvalidOperationException>(
            () => engine.AppendSystemPromptSetup("system-B")
        );
        Assert.Contains("requires an idle or explicitly failed turn boundary", promptEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExecutionTail_WhenSetupEventAppearsInsidePendingTurn_Throws() {
        string path = NewJournalPath();

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("hello");
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            EventAddress expectedHead = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":1,\"body\":{\"content\":\"system-B\"}}"
            );
            journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                expectedHead,
                payload,
                opaqueEventKind: (uint)SessionEventKind.SystemPromptSetup,
                hint: default
            ).Unwrap();
        }

        using var reopened = SessionJournalEngine.Open(path);
        var ex = Assert.Throws<InvalidDataException>(
            () => reopened.ResolveExecutionTail()
        );
        Assert.Contains(
            "Setup run",
            ex.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ResolveExecutionTail_WhenBusinessEventAppearsBeforeSessionCreatedMarker_Throws() {
        string path = NewJournalPath();
        using (var journal = EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":2,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\",\"derivedContext\":{\"nthPrevious\":0}}}");
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
            CommitToMain(journal, head, SessionEventKind.ObservationAccepted, "{\"v\":1,\"body\":{\"content\":\"hello\"}}");
        }

        using var reopened = SessionJournalEngine.Open(path);
        var ex = Assert.Throws<InvalidDataException>(
            () => reopened.ResolveExecutionTail()
        );
        Assert.Contains(
            nameof(SessionEventKind.ObservationAccepted),
            ex.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ObservationPayload_UsesCanonicalEnvelopeBytesWithoutHeaderDuplication() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        );

        var address = engine.AppendObservation("你好，Atelia <session>");
        byte[] payload = engine.ReadPayloadBytes(address);
        string json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.Equal("{\"v\":1,\"body\":{\"content\":\"你好，Atelia <session>\"}}", json);
        Assert.DoesNotContain("\\u4F60", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u597D", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opaqueEventKind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("utcUnixTimeMilliseconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadLength", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationPayload_CompressedEventJournalStillReadsLogicalPayload() {
        string path = NewJournalPath();
        var journalOptions = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Brotli with {
                MinimumPayloadLength = 0,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0.01
            }
        };
        string content = string.Concat(Enumerable.Repeat("这是一段用于验证 SessionJournal logical payload 透明读取的中文内容。", 128));
        EventAddress observationAddress;

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            runtime: null,
            new SessionJournalTestHooks(),
            journalOptions
        )) {
            observationAddress = engine.AppendObservation(content);
        }

        using (var reopened = SessionJournalEngine.OpenForTest(path, runtime: null, new SessionJournalTestHooks(), journalOptions)) {
            SessionHistoryPlanningUnit unit = Assert.Single(
                reopened.ReadHistoryPlanningWindow().Units
            );

            var observation =
                Assert.IsType<ObservationMessage>(unit.Message);
            Assert.Equal(content, observation.Content);
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path, journalOptions);
        EventFrameHeader header = journal.ReadEventHeaderChecked(observationAddress).Unwrap();
        Assert.Equal(EventPayloadCodecId.Brotli, header.PayloadCodecId);
    }

    [Fact]
    public void ObservationPayload_DefaultSessionJournalCompressionUsesZlib() {
        string path = NewJournalPath();
        string content = string.Concat(Enumerable.Repeat("SessionJournal 默认压缩应适合中文 LLM 输出和 JSON payload。", 160));
        EventAddress observationAddress;

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            observationAddress = engine.AppendObservation(content);
        }

        using (var reopened = SessionJournalEngine.Open(path)) {
            SessionHistoryPlanningUnit unit = Assert.Single(
                reopened.ReadHistoryPlanningWindow().Units
            );

            var observation =
                Assert.IsType<ObservationMessage>(unit.Message);
            Assert.Equal(content, observation.Content);
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        EventFrameHeader header = journal.ReadEventHeaderChecked(observationAddress).Unwrap();
        Assert.Equal(EventPayloadCodecId.Zlib, header.PayloadCodecId);
    }

    [Fact]
    public void ActionPayload_RoundTripsToolCallAndRestoresPendingToolState() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("I will call a tool."),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
        }
        );

        EventAddress actionAddress;
        var toolSession = new ToolRegistry([
            new RecordingTool(
                "lookup",
                _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "unused")
            )
        ]).CreateSession();
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            ),
            CreateRuntime(new ScriptedCompletionClient(), toolSession)
        )) {
            EventAddress observationAddress = engine.AppendObservation("need lookup");
            actionAddress = engine.AppendImportedAgentAction(action, invocation);
            string actionJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(actionAddress));
            Assert.Equal(
                "{\"v\":1,\"body\":{\"action\":[{\"kind\":\"text\",\"content\":\"I will call a tool.\"},{\"kind\":\"tool-call\",\"toolName\":\"lookup\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\"}],\"invocation\":{\"providerId\":\"fake-provider\",\"apiSpecId\":\"fake-api-v1\",\"model\":\"model-A\"},\"correlationId\":\"atelia.session-journal.turn.v1:"
                    + EventAddressTextCodec.Format(observationAddress)
                    + "\",\"execution\":{\"lastIssuedToolExecutionSequence\":0},\"toolRuntimeIdentity\":{\"hostId\":\"test-tool-host\",\"implementationSetFingerprint\":\"test-tool-implementations-v1\",\"capabilitySetFingerprint\":\"test-tool-capabilities-v1\"}}}",
                actionJson
            );
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionState state =
            reopened.ResolveExecutionTail().State;
        Assert.Equal(
            SessionExecutionPhase.AwaitingToolExecution,
            state.Phase
        );
        RawToolCall call = Assert.IsType<RawToolCall>(
            state.PendingToolCall
        );
        Assert.Equal("lookup", call.ToolName);
        Assert.Equal("call-1", call.ToolCallId);
        Assert.Equal("{\"q\":\"x\"}", call.RawArgumentsJson);
    }

    [Fact]
    public async Task SendAsync_CommitsObservationThenActionAndUsesJournalConfig() {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var client = new ScriptedCompletionClient();
        client.Enqueue(
            request => {
                Assert.Equal("model-A", request.ModelId);
                Assert.Equal("system-A", request.SystemPrompt);
                Assert.Empty(request.Tools);
                var observation = Assert.IsType<ObservationMessage>(
                    Assert.Single(
                        CoherentArtifactSetTestFixture.RawSuffix(request)
                    )
                );
                Assert.Equal("hello", observation.Content);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("answer") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, candidateSource: candidateSource)
        );
        ActivatedCoherentArtifactSet activated =
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );

        TurnResult result = await engine.SendAsync("hello", CancellationToken.None);
        SessionExecutionState state =
            engine.ResolveExecutionTail().State;

        Assert.Equal("answer", result.Message.GetFlattenedText());
        Assert.Equal("scripted", result.Invocation.ProviderId);
        Assert.Equal(SessionExecutionPhase.Idle, state.Phase);
        Assert.Equal(0, client.RemainingResponses);
        engine.Dispose();

        EventAddress observationAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.ObservationAccepted));
        EventAddress preparedAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        EventAddress startedAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptStarted));
        EventAddress actionAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.Equal(observationAddress, journal.ReadEventHeaderChecked(preparedAddress).Unwrap().Parent);
            Assert.Equal(preparedAddress, journal.ReadEventHeaderChecked(startedAddress).Unwrap().Parent);
            Assert.Equal(startedAddress, journal.ReadEventHeaderChecked(actionAddress).Unwrap().Parent);
        }

        using var inspection = SessionJournalEngine.Open(path);
        var manifest = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, inspection.ReadPayloadBytes(preparedAddress), out _)
        );
        Assert.Equal(SessionRequestManifestDefaults.RecipeId, manifest.Recipe.RecipeId);
        Assert.Equal(activated.CommonAnchor, manifest.Plan.RawStartExclusive);
        Assert.Equal(2, manifest.Plan.ExactContextInputs.Length);
        Assert.Equal(64, manifest.Plan.RawRangeSha256.Length);
        Assert.Equal("model-A", manifest.Parameters.ModelId);
        Assert.Empty(manifest.ToolSet.Definitions);
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(client.Requests.Single()), manifest.Commitment);
    }

    [Fact]
    public async Task SendAsync_WithoutTools_ProviderToolCallDurablyFails() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("unexpected", "call-1", "{}")
                )
            ]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            _candidateSource
        );

        SessionJournalTurnAbortedException error =
            await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );

        Assert.Equal(
            "atelia.host.unsupported-tool-call",
            error.Termination.ProviderReason
        );
        Assert.Equal(
            SessionExecutionPhase.TurnFailed,
            engine.ResolveExecutionTail().State.Phase
        );
        engine.Dispose();
        Assert.Single(
            ReadJournalAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
        Assert.Single(
            ReadJournalAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptFailed
            )
        );
        Assert.Empty(
            ReadJournalAddressesByKind(
                path,
                SessionEventKind.AgentActionProduced
            )
        );
        Assert.Empty(
            ReadJournalAddressesByKind(
                path,
                SessionEventKind.ToolExecutionStarted
            )
        );
        Assert.Empty(
            ReadJournalAddressesByKind(
                path,
                SessionEventKind.ToolResultObserved
            )
        );
    }

    [Fact]
    public void BoundaryMutations_UseTailRecovery() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0))
        );
        engine.AppendSystemPromptSetup("system-B");
        engine.AppendObservation("imported observation");
        engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("imported answer")]),
            new CompletionDescriptor("import", "import-v1", "model-B")
        );
        Assert.Equal(SessionExecutionPhase.Idle, engine.ResolveExecutionTail().State.Phase);
    }

    [Fact]
    public async Task SendAsync_AfterRequestPreparedCommitted_LeavesSafeDispatchBoundary() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );

            Assert.Equal(SessionJournalFailpoint.AfterRequestPreparedCommitted, ex.Failpoint);
            SessionExecutionState state = engine.ResolveExecutionTail().State;
            Assert.Equal(SessionExecutionPhase.AwaitingCompletionDispatch, state.Phase);
            Assert.Equal(SessionEventKind.CompletionRequestPrepared, state.HeadKind);
            Assert.Equal(engine.InspectExecutionBoundary().Head, state.PendingRequestPreparedAddress);
            Assert.Null(state.ActiveCompletionAttemptAddress);
            Assert.False(string.IsNullOrWhiteSpace(state.ActiveCorrelationId));
            Assert.Equal(0, client.Calls);
        }

        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("resumed")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(client));
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletionDispatch,
            reopened.ResolveExecutionTail().State.Phase
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
        Assert.True(outcome.Advanced);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(CompletionTerminationKind.Incomplete)]
    [InlineData(CompletionTerminationKind.Failed)]
    public async Task SendAsync_KnownNonSuccess_PersistsAttemptFailureAndReopensAsTurnFailed(
        CompletionTerminationKind terminationKind
    ) {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        CompletionTermination termination = terminationKind == CompletionTerminationKind.Incomplete
            ? CompletionTermination.Incomplete("length", "truncated")
            : CompletionTermination.Failed("provider-error", "rejected");
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("partial")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId),
            errors: ["stream warning"],
            termination: termination
        ));
        var candidateSource = new TestContextCandidateSource();

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, candidateSource: candidateSource)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );
            SessionJournalTurnAbortedException ex = await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(terminationKind, ex.Termination.Kind);
            Assert.Contains("known failure outcome were persisted", ex.Message, StringComparison.Ordinal);
            SessionExecutionState state = engine.ResolveExecutionTail().State;
            Assert.Equal(SessionExecutionPhase.TurnFailed, state.Phase);
            Assert.Null(state.PendingRequestPreparedAddress);
            Assert.Null(state.ActiveCorrelationId);
        }

        EventAddress prepared = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        EventAddress started = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptStarted));
        EventAddress failed = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.Equal(prepared, journal.ReadEventHeaderChecked(started).Unwrap().Parent);
            Assert.Equal(started, journal.ReadEventHeaderChecked(failed).Unwrap().Parent);
        }
        using var reopened = SessionJournalEngine.Open(path);
        Assert.Equal(SessionExecutionPhase.TurnFailed, reopened.ResolveExecutionTail().State.Phase);
        ResumeOutcome resume = await reopened.ResumeAsync(CancellationToken.None);
        Assert.False(resume.Advanced);
    }

    [Fact]
    public async Task SendAsync_AfterTurnFailed_StartsANewObservationWithoutRetryingFailedAttempt() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("partial")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId),
            termination: CompletionTermination.Failed("known")
        ));
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("recovered")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        var candidateSource = new TestContextCandidateSource();

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, candidateSource: candidateSource)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            candidateSource
        );
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("first", CancellationToken.None)
        );

        TurnResult recovered = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("recovered", recovered.Message.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, engine.ResolveExecutionTail().State.Phase);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task TurnFailed_AllowsSetupReplacementAndNextRequestUsesLatestSetup() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("failed")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId),
            termination: CompletionTermination.Failed("known")
        ));
        client.Enqueue(request => {
            Assert.Equal("model-B", request.ModelId);
            Assert.Equal("system-B", request.SystemPrompt);
            return new CompletionResult(
                new ActionMessage([new ActionBlock.Text("recovered")]),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            );
        });
        var candidateSource = new TestContextCandidateSource();

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, candidateSource: candidateSource)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            candidateSource
        );
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("first", CancellationToken.None)
        );

        engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0))
        );
        engine.AppendSystemPromptSetup("system-B");
        TurnResult result = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("recovered", result.Message.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, engine.ResolveExecutionTail().State.Phase);
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_TransportExceptionOrCancellation_LeavesPreparedWithoutKnownFailure(bool cancellation) {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(_ => cancellation
            ? throw new OperationCanceledException("transport cancellation")
            : throw new IOException("transport failure"));

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            await Assert.ThrowsAnyAsync<Exception>(() => engine.SendAsync("hello", CancellationToken.None));
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, engine.ResolveExecutionTail().State.Phase);
        }

        Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
    }

    [Fact]
    public async Task SendAsync_MismatchedCompletionInvocation_PersistsHostKnownFailure() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("wrong-provider", "wrong-api", request.ModelId)
        ));

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            SessionJournalTurnAbortedException error =
                await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(
                "atelia.host.invalid-completion-invocation",
                error.Termination.ProviderReason
            );
            Assert.Equal(SessionExecutionPhase.TurnFailed, engine.ResolveExecutionTail().State.Phase);
        }

        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        EventAddress failureAddress = Assert.Single(
            ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed)
        );
        using var inspection = SessionJournalEngine.Open(path);
        CompletionAttemptFailedBody failure = Assert.IsType<CompletionAttemptFailedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptFailed,
                inspection.ReadPayloadBytes(failureAddress),
                out _
            )
        );
        Assert.Equal(CompletionTerminationKind.Failed, failure.TerminationKind);
        Assert.Equal("atelia.host.invalid-completion-invocation", failure.ProviderReason);
    }

    [Fact]
    public async Task SendAsync_PreparedCommitFailure_InvalidatesCursorAndDoesNotCallProvider() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var hooks = new SessionJournalTestHooks(
            BeforeCommit: kind => {
                if (kind == SessionEventKind.CompletionRequestPrepared) {
                    throw new IOException("simulated CommitToRef failure");
                }
            }
        );
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            hooks
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            _candidateSource
        );

        await Assert.ThrowsAsync<IOException>(() => engine.SendAsync("hello", CancellationToken.None));

        Assert.Null(engine.GoverningSetupCursorHeadForTest);
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.ResolveExecutionTail().State.Phase);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ResolveExecutionTail_CompletionAttemptFailedWithoutStarted_Throws() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        EventAddress prepared;
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            prepared = engine.InspectExecutionBoundary().Head!.Value;
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                prepared,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionAttemptFailed,
                    new CompletionAttemptFailedBody(
                        CompletionTerminationKind.Failed,
                        null,
                        null,
                        Array.AsReadOnly(Array.Empty<string>())
                    )
                ),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptFailed,
                hint: default
            ).Unwrap();
        }

        using var reopened = SessionJournalEngine.Open(path);
        Assert.Throws<InvalidDataException>(
            () => reopened.ResolveExecutionTail()
        );
    }

    [Fact]
    public async Task ResolveExecutionTail_PreparedReasonMustMatchDirectCompletionBoundary() {
        string sourcePath = NewJournalPath();
        var client = new ScriptedCompletionClient();
        using (var source = SessionJournalEngine.CreateForTest(
            sourcePath,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                sourcePath,
                source,
                _candidateSource
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => source.SendAsync("source", CancellationToken.None)
            );
        }
        EventAddress sourcePrepared = Assert.Single(
            ReadJournalAddressesByKind(sourcePath, SessionEventKind.CompletionRequestPrepared)
        );
        CompletionRequestPreparedBody sourceBody;
        using (var source = SessionJournalEngine.Open(sourcePath)) {
            sourceBody = Assert.IsType<CompletionRequestPreparedBody>(
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionRequestPrepared,
                    source.ReadPayloadBytes(sourcePrepared),
                    out _
                )
            );
        }

        string targetPath = NewJournalPath();
        EventAddress targetObservation;
        using (var target = SessionJournalEngine.Create(
            targetPath,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            targetObservation = target.AppendObservation("target");
        }
        CompletionRequestPreparedBody forged = sourceBody with {
            Origin = sourceBody.Origin with {
                CorrelationId = $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(targetObservation)}",
                Reason = "tool-continuation"
            }
        };
        using (var journal = EventJournal.EventJournal.OpenExisting(targetPath)) {
            journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                targetObservation,
                SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, forged),
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared,
                hint: default
            ).Unwrap();
        }

        using var reopened = SessionJournalEngine.Open(targetPath);
        Assert.Throws<InvalidDataException>(
            () => reopened.ResolveExecutionTail()
        );
    }

    [Fact]
    public async Task SendAsync_AfterRuntimeConfigAndSystemPromptSetup_UsesLatestJournalState() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(
            request => {
                Assert.Equal("model-B", request.ModelId);
                Assert.Equal("system-B", request.SystemPrompt);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("answer-B") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            _candidateSource
        );
        engine.AppendRuntimeConfigSetup(new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema, new(0)));
        engine.AppendSystemPromptSetup("system-B");

        TurnResult result = await engine.SendAsync("hello", CancellationToken.None);

        Assert.Equal("answer-B", result.Message.GetFlattenedText());
        Assert.Equal(0, client.RemainingResponses);
    }

    [Fact]
    public async Task ResumeAsync_AfterObservationCommitted_ReplaysCompletionAndCommitsAction() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        SessionContextCandidate candidate;

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterObservationCommitted)
        )) {
            ActivatedCoherentArtifactSet activated = await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            candidate = activated.Candidate;
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterObservationCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.ResolveExecutionTail().State.Phase);
            Assert.Equal(0, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        resumeClient.Enqueue(
            request => {
                var observation = Assert.IsType<ObservationMessage>(
                    Assert.Single(
                        CoherentArtifactSetTestFixture.RawSuffix(request)
                    )
                );
                Assert.Equal("hello", observation.Content);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("resumed") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(resumeClient, contextCandidate: candidate)
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed", outcome.Message!.GetFlattenedText());
        Assert.Equal(
            SessionExecutionPhase.Idle,
            reopened.ResolveExecutionTail().State.Phase
        );
        Assert.Equal(1, resumeClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_AfterCompletionBeforeAction_DoesNotReplanOrResendPreparedRequest() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("not-yet-persisted") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted, ex.Failpoint);
            SessionExecutionState state = engine.ResolveExecutionTail().State;
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, state.Phase);
            Assert.NotNull(state.PendingRequestPreparedAddress);
            Assert.NotNull(state.ActiveCompletionAttemptAddress);
            Assert.Equal(1, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient));
        InvalidOperationException resumeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains("Refuse", resumeError.Message, StringComparison.Ordinal);
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, reopened.ResolveExecutionTail().State.Phase);
        Assert.Equal(0, resumeClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_WhenIdle_DoesNotCallCompletion() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );

        ResumeOutcome outcome = await engine.ResumeAsync(CancellationToken.None);

        Assert.False(outcome.Advanced);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task SendAsync_ToolLoop_PersistsStartResultAndFeedsCompletion() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"result:{context.RawToolCall.RawArgumentsJson}"));
        ToolSession toolSession = new ToolRegistry([tool]).CreateSession();

        client.Enqueue(
            request => {
                Assert.Single(request.Tools);
                var observation = Assert.IsType<ObservationMessage>(
                    Assert.Single(
                        CoherentArtifactSetTestFixture.RawSuffix(request)
                    )
                );
                Assert.Equal("need lookup", observation.Content);
                return new CompletionResult(
                    new ActionMessage(
                        new ActionBlock[] {
                            new ActionBlock.Text("calling"),
                            new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
                    }
                    ),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );
        client.Enqueue(
            request => {
                var rawSuffix =
                    CoherentArtifactSetTestFixture.RawSuffix(request);
                Assert.Equal(3, rawSuffix.Length);
                var action = Assert.IsType<ActionMessage>(rawSuffix[1]);
                Assert.Single(action.ToolCalls);
                var results =
                    Assert.IsType<ToolResultsMessage>(rawSuffix[2]);
                ToolResult result = Assert.Single(results.Results);
                Assert.Equal("lookup", result.ToolName);
                Assert.Equal("call-1", result.ToolCallId);
                Assert.Equal(ToolExecutionStatus.Success, result.Status);
                Assert.Equal("result:{\"q\":\"x\"}", result.GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("final") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, toolSession)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            TurnResult turn = await engine.SendAsync("need lookup", CancellationToken.None);
            SessionExecutionState state =
                engine.ResolveExecutionTail().State;

            Assert.Equal("final", turn.Message.GetFlattenedText());
            Assert.Equal(SessionExecutionPhase.Idle, state.Phase);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(2, client.Calls);
        }

        string startedPayload = Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted));
        string resultPayload = Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolResultObserved));
        Assert.Equal(2, ReadJournalPayloadJsonByKind(path, SessionEventKind.CompletionRequestPrepared).Length);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\",\"operationId\":\"" + ExtractOperationId(startedPayload) + "\",\"executionSequence\":1,\"toolRuntimeIdentity\":{\"hostId\":\"test-tool-host\",\"implementationSetFingerprint\":\"test-tool-implementations-v1\",\"capabilitySetFingerprint\":\"test-tool-capabilities-v1\"}}}", startedPayload);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"executionSequence\":1,\"status\":\"success\",\"blocks\":[{\"kind\":\"text\",\"content\":\"result:{\\\"q\\\":\\\"x\\\"}\"}]}}", resultPayload);
        Assert.DoesNotContain("opaqueEventKind", startedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", resultPayload, StringComparison.Ordinal);

        using var reopened = SessionJournalEngine.Open(path);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            reopened.ResolveExecutionTail().State.Phase
        );
        SessionHistoryPlanningWindow window =
            reopened.ReadHistoryPlanningWindow();
        var replayedResults = Assert.IsType<ToolResultsMessage>(
            window.Units[2].Message
        );
        Assert.Equal("result:{\"q\":\"x\"}", Assert.Single(replayedResults.Results).GetFlattenedText());
    }

    [Fact]
    public async Task ResumeAsync_AfterToolStarted_ReexecutesToolAndUsesPersistedOperationId() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        var firstClient = new ScriptedCompletionClient();
        var firstTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "not-persisted"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient, new ToolRegistry([firstTool]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolStartedCommitted)
        )) {
            ActivatedCoherentArtifactSet activated =
                await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            candidate = activated.Candidate;
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolStartedCommitted, ex.Failpoint);
            SessionExecutionState state = engine.ResolveExecutionTail().State;
            Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, state.Phase);
            Assert.True(state.PendingToolExecutionStarted);
            Assert.NotNull(state.PendingOperationId);
            Assert.Equal(0, firstTool.Calls);
        }

        string persistedOperationId;
        using (var inspection = SessionJournalEngine.Open(path)) {
            persistedOperationId = inspection.ResolveExecutionTail().State.PendingOperationId!;
        }

        Assert.Equal(persistedOperationId, ExtractOperationId(ReadJournalPayloadJson(path)[^1]));

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "resumed-result"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(
                    CoherentArtifactSetTestFixture.RawSuffix(request)[2]
                );
                Assert.Equal("resumed-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                resumeClient,
                new ToolRegistry([resumeTool]).CreateSession(),
                contextCandidate: candidate
            )
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(
            SessionExecutionPhase.Idle,
            reopened.ResolveExecutionTail().State.Phase
        );
        Assert.Equal(1, resumeTool.Calls);
        Assert.False(string.IsNullOrWhiteSpace(persistedOperationId));
    }

    [Fact]
    public async Task ResumeAsync_AfterActionCommitted_ReopensWithMatchingRuntime() {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var firstClient = new ScriptedCompletionClient();
        firstClient.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall(
                    "lookup",
                    "call-1",
                    "{}"
                ))
            ]),
            new CompletionDescriptor(
                "scripted",
                "test-api-v1",
                request.ModelId
            )
        ));
        using (var engine = SessionJournalEngine.CreateForTest(
                   path,
                   new SessionCreateOptions(
                       "model-A",
                       "system-A",
                       "surface-A"
                   ),
                   CreateRuntime(
                       firstClient,
                       new ToolRegistry([
                           new RecordingTool(
                               "lookup",
                               _ => ToolExecuteResult.FromText(
                                   ToolExecutionStatus.Success,
                                   "must-not-run-before-reopen"
                               )
                           )
                       ]).CreateSession(),
                       candidateSource: candidateSource
                   ),
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint.AfterActionCommitted
                   )
               )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
        }

        var recoveryClient = new ScriptedCompletionClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("done")]),
            new CompletionDescriptor(
                "scripted",
                "test-api-v1",
                request.ModelId
            )
        ));
        var recoveryTool = new RecordingTool(
            "lookup",
            _ => ToolExecuteResult.FromText(
                ToolExecutionStatus.Success,
                "reopened-result"
            )
        );
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                new ToolRegistry([recoveryTool]).CreateSession(),
                candidateSource: candidateSource
            )
        );
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired
            requirement = Assert.IsType<
                SessionRuntimeRecoveryRequirements.ToolContinuationRequired
            >(reopened.InspectRuntimeRecoveryRequirements());
        Assert.Equal(ToolRuntimeIdentity, requirement.ToolRuntimeIdentity);

        ResumeOutcome outcome = await reopened.ResumeAsync(
            requirement.CapturedHead!.Value,
            CancellationToken.None
        );

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(1, recoveryTool.Calls);
        Assert.Equal(1, recoveryClient.Calls);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            reopened.ResolveExecutionTail().State.Phase
        );
    }

    [Fact]
    public async Task ResumeAsync_PendingActionToolRuntimeIdentityMismatchFailsBeforeStartOrExecution() {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var firstClient = new ScriptedCompletionClient();
        firstClient.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
            ]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        var sourceTool = new RecordingTool(
            "lookup",
            _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "must-not-run")
        );
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(
                firstClient,
                new ToolRegistry([sourceTool]).CreateSession(),
                candidateSource: candidateSource
            ),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterActionCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
        }
        Assert.Equal(0, sourceTool.Calls);
        Assert.Empty(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted));

        var recoveryTool = new RecordingTool(
            "lookup",
            _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "must-not-run")
        );
        var differentIdentity = ToolRuntimeIdentity with {
            CapabilitySetFingerprint = "different-capabilities-v2"
        };
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                new ScriptedCompletionClient(),
                new ToolRegistry([recoveryTool]).CreateSession(),
                toolRuntimeIdentity: differentIdentity
            )
        )) {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );

            Assert.Contains("does not match the durable pending Action", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, recoveryTool.Calls);
            Assert.Equal(
                SessionExecutionPhase.AwaitingToolExecution,
                reopened.ResolveExecutionTail().State.Phase
            );
        }
        Assert.Empty(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted));
    }

    [Fact]
    public async Task ResumeAsync_AfterExternalToolExecutionBeforeResult_RetriesSameReservedSequenceAndOperation() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        var firstSequences = new List<long>();
        var firstOperationIds = new List<string?>();
        var firstClient = new ScriptedCompletionClient();
        firstClient.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
            ]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        var firstTool = new RecordingTool(
            "lookup",
            context => {
                firstSequences.Add(context.ExecutionSequence);
                firstOperationIds.Add(context.OperationId);
                return ToolExecuteResult.FromText(ToolExecutionStatus.Success, "uncertain-result");
            }
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient, new ToolRegistry([firstTool]).CreateSession()),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterToolExecutionBeforeResultCommitted
            )
        )) {
            ActivatedCoherentArtifactSet activated =
                await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            candidate = activated.Candidate;
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<SessionJournalFailpointException>(
                    () => engine.SendAsync("need lookup", CancellationToken.None)
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterToolExecutionBeforeResultCommitted,
                error.Failpoint
            );
            Assert.Equal([1L], firstSequences);
            Assert.Single(firstOperationIds);
            SessionExecutionState state = engine.ResolveExecutionTail().State;
            Assert.True(state.PendingToolExecutionStarted);
            Assert.Equal(1, state.ToolExecutionSequenceCheckpoint);
        }

        string startedPayload = Assert.Single(
            ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted)
        );
        string operationId = ExtractOperationId(startedPayload);
        Assert.Empty(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolResultObserved));

        var retriedSequences = new List<long>();
        var retriedOperationIds = new List<string?>();
        var resumedTool = new RecordingTool(
            "lookup",
            context => {
                retriedSequences.Add(context.ExecutionSequence);
                retriedOperationIds.Add(context.OperationId);
                return ToolExecuteResult.FromText(ToolExecutionStatus.Success, "retried-result");
            }
        );
        var resumeClient = new ScriptedCompletionClient();
        resumeClient.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("done")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                resumeClient,
                new ToolRegistry([resumedTool]).CreateSession(),
                contextCandidate: candidate
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

            Assert.True(outcome.Advanced);
            Assert.Equal([1L], retriedSequences);
            Assert.Equal([operationId], firstOperationIds);
            Assert.Equal([operationId], retriedOperationIds);
        }
        Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted));
        Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolResultObserved));
        Assert.Equal(
            operationId,
            ExtractOperationId(
                Assert.Single(
                    ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted)
                )
            )
        );
    }

    [Fact]
    public async Task ResumeAsync_AfterToolResult_CompletesWithoutReexecutingTool() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        var firstClient = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "persisted-result"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient, new ToolRegistry([tool]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolResultCommitted)
        )) {
            ActivatedCoherentArtifactSet activated =
                await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            candidate = activated.Candidate;
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.ResolveExecutionTail().State.Phase);
            Assert.Equal(1, tool.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(
                    CoherentArtifactSetTestFixture.RawSuffix(request)[2]
                );
                Assert.Equal("persisted-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                resumeClient,
                new ToolRegistry([resumeTool]).CreateSession(),
                contextCandidate: candidate
            )
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumeTool.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.ResolveExecutionTail().State.Phase);
    }

    [Fact]
    public async Task ResumeAsync_AfterFirstToolResult_RestoresExecutionSequenceForNextTool() {
        string path = NewJournalPath();
        SessionContextCandidate candidate;
        var firstClient = new ScriptedCompletionClient();
        var alpha = new RecordingTool("alpha", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        var beta = new RecordingTool("beta", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
            }
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient, new ToolRegistry([alpha, beta]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolResultCommitted)
        )) {
            ActivatedCoherentArtifactSet activated =
                await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            candidate = activated.Candidate;
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need two tools", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(1, alpha.Calls);
            Assert.Equal(0, beta.Calls);
            Assert.Equal(1, engine.ResolveExecutionTail().State.ToolExecutionSequenceCheckpoint);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumedAlpha = new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        var resumedBeta = new RecordingTool("beta", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(
                    CoherentArtifactSetTestFixture.RawSuffix(request)[2]
                );
                Assert.Collection(
                    results.Results,
                    first => Assert.Equal("seq:1", first.GetFlattenedText()),
                    second => Assert.Equal("seq:2", second.GetFlattenedText())
                );
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                resumeClient,
                new ToolRegistry([resumedAlpha, resumedBeta]).CreateSession(),
                contextCandidate: candidate
            )
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumedAlpha.Calls);
        Assert.Equal(1, resumedBeta.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.ResolveExecutionTail().State.Phase);
    }

    [Fact]
    public async Task SendAsync_LaterToolTurn_ContinuesExecutionSequence() {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var client = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        ToolSession toolSession = new ToolRegistry([tool]).CreateSession();

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("first done") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[^1]);
                Assert.Equal("seq:2", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("second done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, toolSession, candidateSource: candidateSource)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            candidateSource
        );

        await engine.SendAsync("first", CancellationToken.None);
        TurnResult second = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("second done", second.Message.GetFlattenedText());
        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, engine.ResolveExecutionTail().State.ToolExecutionSequenceCheckpoint);
    }

    [Fact]
    public async Task SendAsync_MultipleToolCalls_ProjectsResultsInDeclaredOrder() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var registry = new ToolRegistry(
            [
            new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "A")),
            new RecordingTool("beta", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "B"))
        ]
        );

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
            }
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(
                    CoherentArtifactSetTestFixture.RawSuffix(request)[2]
                );
                Assert.Collection(
                    results.Results,
                    first => Assert.Equal("call-A", first.ToolCallId),
                    second => Assert.Equal("call-B", second.ToolCallId)
                );
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, registry.CreateSession())
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            await engine.SendAsync("need two tools", CancellationToken.None);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionHistoryPlanningWindow window =
            reopened.ReadHistoryPlanningWindow();
        var results = Assert.IsType<ToolResultsMessage>(
            window.Units[2].Message
        );
        Assert.Collection(
            results.Results,
            first => Assert.Equal("call-A", first.ToolCallId),
            second => Assert.Equal("call-B", second.ToolCallId)
        );
        Assert.Equal(
            SessionExecutionPhase.Idle,
            reopened.ResolveExecutionTail().State.Phase
        );
    }

    [Theory]
    [InlineData("skip-current-start")]
    [InlineData("duplicate-start")]
    [InlineData("result-before-start")]
    [InlineData("out-of-order-result")]
    [InlineData("duplicate-result")]
    public void ResolveExecutionTail_InvalidToolEventOrder_Throws(string invalidCase) {
        string path = CreateImportedTwoToolPendingJournal();
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            EventAddress head = journal.GetHead(main)!.Value;
            const string identity = "\"toolRuntimeIdentity\":{\"hostId\":\"test-tool-host\",\"implementationSetFingerprint\":\"test-tool-implementations-v1\",\"capabilitySetFingerprint\":\"test-tool-capabilities-v1\"}";
            const string startA = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-A\",\"executionSequence\":1," + identity + "}}";
            const string startAAgain = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-A-2\",\"executionSequence\":2," + identity + "}}";
            const string startB = "{\"v\":1,\"body\":{\"toolCallId\":\"call-B\",\"toolName\":\"beta\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-B\",\"executionSequence\":1," + identity + "}}";
            const string resultA = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"executionSequence\":1,\"status\":\"success\",\"blocks\":[]}}";
            const string resultB = "{\"v\":1,\"body\":{\"toolCallId\":\"call-B\",\"toolName\":\"beta\",\"executionSequence\":1,\"status\":\"success\",\"blocks\":[]}}";

            if (invalidCase == "skip-current-start") {
                _ = CommitToMain(journal, head, SessionEventKind.ToolExecutionStarted, startB);
            }
            else if (invalidCase == "duplicate-start") {
                EventAddress started = CommitToMain(journal, head, SessionEventKind.ToolExecutionStarted, startA);
                _ = CommitToMain(journal, started, SessionEventKind.ToolExecutionStarted, startAAgain);
            }
            else if (invalidCase == "result-before-start") {
                _ = CommitToMain(journal, head, SessionEventKind.ToolResultObserved, resultA);
            }
            else if (invalidCase == "out-of-order-result") {
                EventAddress started = CommitToMain(journal, head, SessionEventKind.ToolExecutionStarted, startA);
                _ = CommitToMain(journal, started, SessionEventKind.ToolResultObserved, resultB);
            }
            else {
                EventAddress started = CommitToMain(journal, head, SessionEventKind.ToolExecutionStarted, startA);
                EventAddress firstResult = CommitToMain(journal, started, SessionEventKind.ToolResultObserved, resultA);
                _ = CommitToMain(journal, firstResult, SessionEventKind.ToolResultObserved, resultA);
            }
        }

        using var reopened = SessionJournalEngine.Open(path);
        Assert.Throws<InvalidDataException>(
            () => reopened.ResolveExecutionTail()
        );
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("empty-id")]
    [InlineData("empty-name")]
    [InlineData("empty-arguments")]
    public void ResolveExecutionTail_InvalidActionToolCallIdentity_Throws(string invalidCase) {
        string path = NewJournalPath();
        EventAddress observation;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            observation = engine.AppendObservation("run tool");
        }

        string actionBlocks = invalidCase switch {
            "duplicate-id" =>
                "[{\"kind\":\"tool-call\",\"toolName\":\"alpha\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{}\"},"
                + "{\"kind\":\"tool-call\",\"toolName\":\"beta\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{}\"}]",
            "empty-id" =>
                "[{\"kind\":\"tool-call\",\"toolName\":\"alpha\",\"toolCallId\":\"\",\"rawArgumentsJson\":\"{}\"}]",
            "empty-name" =>
                "[{\"kind\":\"tool-call\",\"toolName\":\"\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{}\"}]",
            _ =>
                "[{\"kind\":\"tool-call\",\"toolName\":\"alpha\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"\"}]"
        };
        string payload = "{\"v\":1,\"body\":{\"action\":" + actionBlocks
            + ",\"invocation\":{\"providerId\":\"import\",\"apiSpecId\":\"import-v1\",\"model\":\"model-A\"}"
            + ",\"correlationId\":\"atelia.session-journal.turn.v1:"
            + EventAddressTextCodec.Format(observation)
            + "\""
            + ",\"execution\":{\"lastIssuedToolExecutionSequence\":0}"
            + ",\"toolRuntimeIdentity\":{\"hostId\":\"test-tool-host\",\"implementationSetFingerprint\":\"test-tool-implementations-v1\",\"capabilitySetFingerprint\":\"test-tool-capabilities-v1\"}}}";
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = CommitToMain(journal, observation, SessionEventKind.ImportedAgentAction, payload);
        }

        using var reopened = SessionJournalEngine.Open(path);
        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => reopened.ResolveExecutionTail()
            );
        Assert.Contains("tool call", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HistoryPlanningWindow_MultipleToolCalls_UsesToolResultObservedRange() {
        string path = NewJournalPath();
        var candidateSource = new TestContextCandidateSource();
        var client = new ScriptedCompletionClient();
        var registry = new ToolRegistry(
            [
                new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "A")),
                new RecordingTool("beta", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "B"))
            ]
        );

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    [
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
                    ]
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("done")]),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(
                client,
                registry.CreateSession(),
                candidateSource: candidateSource
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource
            );
            await engine.SendAsync("need two tools", CancellationToken.None);
        }

        EventAddress[] toolResultAddresses = ReadJournalAddressesByKind(path, SessionEventKind.ToolResultObserved);
        Assert.Equal(2, toolResultAddresses.Length);

        using var reopened = SessionJournalEngine.Open(path);
        SessionHistoryPlanningWindow window =
            reopened.ReadHistoryPlanningWindow();

        Assert.Equal(4, window.Units.Count);
        Assert.IsType<ObservationMessage>(window.Units[0].Message);
        Assert.IsType<ActionMessage>(window.Units[1].Message);
        SessionHistoryPlanningUnit toolResultsEntry = window.Units[2];
        var toolResults = Assert.IsType<ToolResultsMessage>(toolResultsEntry.Message);
        Assert.Equal(toolResultAddresses[0], toolResultsEntry.SourceStartInclusive);
        Assert.Equal(toolResultAddresses[1], toolResultsEntry.SourceEndInclusive);
        Assert.Collection(
            toolResults.Results,
            first => Assert.Equal("call-A", first.ToolCallId),
            second => Assert.Equal("call-B", second.ToolCallId)
        );
        Assert.Equal(
            "done",
            Assert.IsType<ActionMessage>(
                window.Units[3].Message
            ).GetFlattenedText()
        );
    }

    [Fact]
    public async Task ResolveExecutionTail_UnclosedToolCalls_RestoresPendingCall() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var registry = new ToolRegistry(
            [
                new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "A")),
                new RecordingTool("beta", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "B"))
            ]
        );

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    [
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
                    ]
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, registry.CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolResultCommitted)
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                _candidateSource
            );
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need two tools", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionState state =
            reopened.ResolveExecutionTail().State;

        Assert.Equal(
            SessionExecutionPhase.AwaitingToolExecution,
            state.Phase
        );
        Assert.Equal("call-B", state.PendingToolCall?.ToolCallId);
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = new();
        private readonly List<CompletionRequest> _requests = new();

        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int Calls { get; private set; }

        public int RemainingResponses => _responses.Count;

        public IReadOnlyList<CompletionRequest> Requests => _requests;

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
            _requests.Add(request);
            if (_responses.Count == 0) { throw new InvalidOperationException("No scripted response remaining."); }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class RecordingTool : ITool {
        private readonly Func<ToolExecutionContext, ToolExecuteResult> _execute;

        public RecordingTool(string name, Func<ToolExecutionContext, ToolExecuteResult> execute) {
            Definition = new ToolDefinition(name, $"Tool {name}.", new ToolSchema.Object());
            _execute = execute;
        }

        public ToolDefinition Definition { get; }

        public int Calls { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(_execute(context));
        }
    }

    private static string[] ReadJournalPayloadJson(string path) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        var payloads = new string[chain.Count];
        for (int i = 0; i < chain.Count; i++) {
            using EventFrame frame = journal.ReadEvent(chain[i]).Unwrap();
            payloads[i] = System.Text.Encoding.UTF8.GetString(frame.Payload.ToArray());
        }

        return payloads;
    }

    private SessionRuntime CreateRuntime(
        ICompletionClient client,
        ToolSession? toolSession = null,
        int? maxTokens = null,
        SessionToolRuntimeIdentity? toolRuntimeIdentity = null,
        SessionContextCandidate? contextCandidate = null,
        TestContextCandidateSource? candidateSource = null
    ) => new(
        client,
        toolSession,
        new SessionCompletionTargetIdentity(
            ConnectionId: "test-connection",
            Kind: "test",
            ConnectionFingerprint: "test-connection-fingerprint-v1",
            RequestAdapterFingerprint: "test-request-adapter-v1"
        ),
        maxTokens,
        ToolRuntimeIdentity: toolRuntimeIdentity ?? ToolRuntimeIdentity,
        ContextCandidateSource:
            candidateSource
            ?? (contextCandidate is null
                ? _candidateSource
                : new TestContextCandidateSource(contextCandidate))
    );

    private string CreateImportedTwoToolPendingJournal() {
        string path = NewJournalPath();
        var tools = new ToolRegistry([
            new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "unused")),
            new RecordingTool("beta", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "unused"))
        ]).CreateSession();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(new ScriptedCompletionClient(), tools)
        );
        engine.AppendObservation("run two tools");
        engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
            ]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        return path;
    }

    private static EventAddress[] ReadJournalAddressesByKind(string path, SessionEventKind kind) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        var addresses = new List<EventAddress>();
        foreach (EventAddress address in chain) {
            EventFrameHeader header = journal.ReadEventHeaderPreview(address).Unwrap();
            if (header.OpaqueEventKind == (uint)kind) {
                addresses.Add(address);
            }
        }

        return addresses.ToArray();
    }

    private static string[] ReadJournalPayloadJsonByKind(string path, SessionEventKind kind) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        var payloads = new List<string>();
        foreach (EventAddress address in chain) {
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            if (frame.Header.OpaqueEventKind == (uint)kind) {
                payloads.Add(System.Text.Encoding.UTF8.GetString(frame.Payload));
            }
        }

        return payloads.ToArray();
    }

    private static EventAddress CommitToMain(
        EventJournal.EventJournal journal,
        EventAddress? expectedHead,
        SessionEventKind kind,
        string payloadJson
    )
        => journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            expectedHead,
            System.Text.Encoding.UTF8.GetBytes(payloadJson),
            opaqueEventKind: (uint)kind,
            hint: default
        ).Unwrap().EventAddress;

    private static string ExtractOperationId(string startedPayload) {
        using var document = System.Text.Json.JsonDocument.Parse(startedPayload);
        return document.RootElement.GetProperty("body").GetProperty("operationId").GetString()
            ?? throw new InvalidDataException("tool-execution-started payload is missing operationId.");
    }

    private string NewJournalPath() {
        string path = Path.Combine(System.IO.Path.GetTempPath(), "atelia-session-journal-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private static long GetEventStoreLength(string journalPath)
        => Directory.EnumerateFiles(
                Path.Combine(journalPath, "events"),
                "*.rbf",
                SearchOption.AllDirectories
            )
            .Sum(static path => new FileInfo(path).Length);

    private static IReadOnlyDictionary<string, FileSnapshot>
        CaptureRepositoryFiles(string path)
        => Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                file => Path.GetRelativePath(path, file),
                file => new FileSnapshot(
                    new FileInfo(file).Length,
                    Convert.ToHexStringLower(
                        SHA256.HashData(File.ReadAllBytes(file))
                    )
                ),
                StringComparer.Ordinal
            );

    private static void AssertReadOnlyMutationRejected(
        Action action,
        string operation
    ) {
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            $"SessionJournalEngine is read-only; mutation operation '{operation}' is not allowed.",
            error.Message
        );
    }

    private static async Task AssertReadOnlyMutationRejectedAsync(
        Func<Task> action,
        string operation
    ) {
        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(
            $"SessionJournalEngine is read-only; mutation operation '{operation}' is not allowed.",
            error.Message
        );
    }

    private sealed record FileSnapshot(long Length, string Sha256);
}

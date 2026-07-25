using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalEngineTests : IDisposable {
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
    public void Create_WritesSetupEventsThenSessionCreatedAndProjectsStateFromJournal() {
        string path = NewJournalPath();

        SessionProjection projection;
        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            projection = engine.Project();
        }

        Assert.NotNull(projection.Head);
        string[] payloads = ReadJournalPayloadJson(path);
        Assert.Equal(3, payloads.Length);
        Assert.Equal("{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}", payloads[0]);
        Assert.Equal("{\"v\":1,\"body\":{\"content\":\"system-A\"}}", payloads[1]);
        Assert.Equal("{\"v\":1,\"body\":{}}", payloads[2]);
        Assert.NotNull(projection.Config);
        Assert.Equal("model-A", projection.Config.ModelId);
        Assert.Equal("system-A", projection.SystemPrompt);
        Assert.Equal("surface-A", projection.Config.CompletionSurfaceId);
        Assert.Equal(SessionJournalDefaults.Schema, projection.Config.Schema);
        Assert.Empty(projection.Context);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.SessionCreated, projection.ExecutionState.HeadKind);
    }

    [Fact]
    public void AppendObservationAndAction_ReopenRebuildsContextAndConfigFromJournal() {
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
        SessionProjection projection = reopened.Project();

        Assert.NotNull(projection.Config);
        Assert.Equal("model-A", projection.Config.ModelId);
        Assert.Equal("system-A", projection.SystemPrompt);
        Assert.Equal("surface-A", projection.Config.CompletionSurfaceId);
        Assert.Equal(2, projection.Context.Count);

        var observation = Assert.IsType<ObservationMessage>(projection.Context[0]);
        Assert.Equal("hello", observation.Content);

        var projectedAction = Assert.IsType<ActionMessage>(projection.Context[1]);
        Assert.Equal("answer continued", projectedAction.GetFlattenedText());
        Assert.Empty(projectedAction.ToolCalls);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.ImportedAgentAction, projection.ExecutionState.HeadKind);
    }

    [Fact]
    public void ReplayHistory_ObservationAndAction_CarriesSourceAddresses() {
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
        SessionProjection projection = reopened.Project();
        SessionHistoryReplay replay = reopened.ReplayHistory();

        Assert.Equal(projection.Head, replay.SourceRawHead);
        Assert.Equal(projection.ExecutionState, replay.ExecutionState);
        Assert.Equal(projection.Context.Count, replay.Messages.Count);

        AddressedSessionHistoryMessage observationEntry = replay.Messages[0];
        var observation = Assert.IsType<ObservationMessage>(observationEntry.Message);
        Assert.Equal("hello", observation.Content);
        Assert.Equal(observationAddress, observationEntry.SourceStartInclusive);
        Assert.Equal(observationAddress, observationEntry.SourceEndInclusive);

        AddressedSessionHistoryMessage actionEntry = replay.Messages[1];
        var projectedAction = Assert.IsType<ActionMessage>(actionEntry.Message);
        Assert.Equal("answer continued", projectedAction.GetFlattenedText());
        Assert.Equal(actionAddress, actionEntry.SourceStartInclusive);
        Assert.Equal(actionAddress, actionEntry.SourceEndInclusive);
    }

    [Fact]
    public void ReplayHistory_SetupAndSessionCreated_DoNotEmitHistoryMessages() {
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
        SessionProjection projection = reopened.Project();
        SessionHistoryReplay replay = reopened.ReplayHistory();

        Assert.Empty(replay.Messages);
        Assert.Equal(setupHead, replay.SourceRawHead);
        Assert.Equal(projection.ExecutionState, replay.ExecutionState);
        Assert.Equal(SessionExecutionPhase.Idle, replay.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.SystemPromptSetup, replay.ExecutionState.HeadKind);
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
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema)
            );

            string configJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(address));
            Assert.Equal("{\"v\":1,\"body\":{\"modelId\":\"model-B\",\"completionSurfaceId\":\"surface-B\",\"schema\":\"atelia.session-journal.trunk.v1\"}}", configJson);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();

        Assert.NotNull(projection.Config);
        Assert.Equal("model-B", projection.Config.ModelId);
        Assert.Equal("system-A", projection.SystemPrompt);
        Assert.Equal("surface-B", projection.Config.CompletionSurfaceId);
        Assert.Equal(2, projection.Context.Count);
        Assert.Equal("hello", Assert.IsType<ObservationMessage>(projection.Context[0]).Content);
        Assert.Equal("answer", Assert.IsType<ActionMessage>(projection.Context[1]).GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.RuntimeConfigSetup, projection.ExecutionState.HeadKind);
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
        SessionProjection projection = reopened.Project();

        Assert.NotNull(projection.Config);
        Assert.Equal("model-A", projection.Config.ModelId);
        Assert.Equal("system-B", projection.SystemPrompt);
        Assert.Equal("surface-A", projection.Config.CompletionSurfaceId);
        Assert.Equal(2, projection.Context.Count);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.SystemPromptSetup, projection.ExecutionState.HeadKind);
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
            runtimeA = CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}");
            promptA = CommitToMain(journal, runtimeA, SessionEventKind.SystemPromptSetup, "{\"v\":1,\"body\":{\"content\":\"system-A\"}}");
            EventAddress created = CommitToMain(journal, promptA, SessionEventKind.SessionCreated, "{\"v\":1,\"body\":{}}");
            EventAddress malformedObservation = CommitToMain(journal, created, SessionEventKind.ObservationAccepted, "this is intentionally not json");
            runtimeB = CommitToMain(journal, malformedObservation, SessionEventKind.RuntimeConfigSetup, "{\"v\":1,\"body\":{\"modelId\":\"model-B\",\"completionSurfaceId\":\"surface-B\",\"schema\":\"atelia.session-journal.trunk.v1\"}}");
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
            runtimeOnlyHead = CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}");
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
        await engine.SendAsync("hello", CancellationToken.None);

        EventAddress actionHead = engine.Project().Head!.Value;
        SessionGoverningSetup fromCheckpoint = engine.ResolveGoverningSetup(actionHead);
        Assert.Equal("model-A", fromCheckpoint.RuntimeConfig.ModelId);
        Assert.Equal("system-A", fromCheckpoint.SystemPrompt);
        Assert.Equal(2, engine.LastGoverningSetupResolutionDiagnostics.HeaderVisitCount);
        Assert.Equal(1, engine.LastGoverningSetupResolutionDiagnostics.ManifestPayloadReadCount);

        EventAddress runtimeB = engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema)
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
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            Assert.Equal(created.Project().Head, created.GoverningSetupCursorHeadForTest);
            observation = created.AppendObservation("hello");
            Assert.Equal(observation, created.GoverningSetupCursorHeadForTest);
        }

        var client = new ScriptedCompletionClient();
        using var reopened = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        );
        Assert.Null(reopened.GoverningSetupCursorHeadForTest);
        _ = reopened.ResolveGoverningSetup(observation);
        Assert.Null(reopened.GoverningSetupCursorHeadForTest);

        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );
        Assert.Equal(reopened.Project().Head, reopened.GoverningSetupCursorHeadForTest);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("kind")]
    [InlineData("schema")]
    public async Task ResolveGoverningSetup_CorruptCheckpointReference_FailsFast(string corruption) {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        EventAddress actionHead;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        )) {
            await engine.SendAsync("hello", CancellationToken.None);
            actionHead = engine.Project().Head!.Value;
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
                new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema)
            )
        );
        Assert.Contains("requires an idle session", configEx.Message, StringComparison.Ordinal);

        var promptEx = Assert.Throws<InvalidOperationException>(
            () => engine.AppendSystemPromptSetup("system-B")
        );
        Assert.Contains("requires an idle session", promptEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_WhenSetupEventAppearsInsidePendingTurn_Throws() {
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
        var ex = Assert.Throws<InvalidDataException>(() => reopened.Project());
        Assert.Contains("must appear only at setup or idle session boundaries", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_WhenBusinessEventAppearsBeforeSessionCreatedMarker_Throws() {
        string path = NewJournalPath();
        using (var journal = EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            CommitToMain(journal, null, SessionEventKind.RuntimeConfigSetup, "{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}");
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
            CommitToMain(journal, head, SessionEventKind.ObservationAccepted, "{\"v\":1,\"body\":{\"content\":\"hello\"}}");
        }

        using var reopened = SessionJournalEngine.Open(path);
        var ex = Assert.Throws<InvalidDataException>(() => reopened.Project());
        Assert.Contains("requires a prior session-created marker", ex.Message, StringComparison.Ordinal);
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
    public void ObservationPayload_CompressedEventJournalStillProjectsLogicalPayload() {
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
            SessionProjection projection = reopened.Project();

            var observation = Assert.IsType<ObservationMessage>(Assert.Single(projection.Context));
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
            SessionProjection projection = reopened.Project();

            var observation = Assert.IsType<ObservationMessage>(Assert.Single(projection.Context));
            Assert.Equal(content, observation.Content);
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        EventFrameHeader header = journal.ReadEventHeaderChecked(observationAddress).Unwrap();
        Assert.Equal(EventPayloadCodecId.Zlib, header.PayloadCodecId);
    }

    [Fact]
    public void ActionPayload_RoundTripsToolCallAndProjectsPendingToolState() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("I will call a tool."),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
        }
        );

        EventAddress actionAddress;
        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("need lookup");
            actionAddress = engine.AppendImportedAgentAction(action, invocation);
            string actionJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(actionAddress));
            Assert.Equal("{\"v\":1,\"body\":{\"action\":[{\"kind\":\"text\",\"content\":\"I will call a tool.\"},{\"kind\":\"tool-call\",\"toolName\":\"lookup\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\"}],\"invocation\":{\"providerId\":\"fake-provider\",\"apiSpecId\":\"fake-api-v1\",\"model\":\"model-A\"}}}", actionJson);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();

        var projectedAction = Assert.IsType<ActionMessage>(projection.Context[1]);
        RawToolCall call = Assert.Single(projectedAction.ToolCalls);
        Assert.Equal("lookup", call.ToolName);
        Assert.Equal("call-1", call.ToolCallId);
        Assert.Equal("{\"q\":\"x\"}", call.RawArgumentsJson);
        Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, projection.ExecutionState.Phase);
        Assert.Equal(call, projection.ExecutionState.PendingToolCall);
    }

    [Fact]
    public async Task SendAsync_CommitsObservationThenActionAndUsesJournalConfig() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(
            request => {
                Assert.Equal("model-A", request.ModelId);
                Assert.Equal("system-A", request.SystemPrompt);
                Assert.Empty(request.Tools);
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
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
            CreateRuntime(client)
        );

        TurnResult result = await engine.SendAsync("hello", CancellationToken.None);
        SessionProjection projection = engine.Project();

        Assert.Equal("answer", result.Message.GetFlattenedText());
        Assert.Equal("scripted", result.Invocation.ProviderId);
        Assert.Equal(2, projection.Context.Count);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(0, client.RemainingResponses);
        engine.Dispose();

        EventAddress observationAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.ObservationAccepted));
        EventAddress preparedAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        EventAddress actionAddress = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.Equal(observationAddress, journal.ReadEventHeaderChecked(preparedAddress).Unwrap().Parent);
            Assert.Equal(preparedAddress, journal.ReadEventHeaderChecked(actionAddress).Unwrap().Parent);
        }

        using var inspection = SessionJournalEngine.Open(path);
        var manifest = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, inspection.ReadPayloadBytes(preparedAddress), out _)
        );
        Assert.Equal("full-raw", manifest.Plan.SelectionPolicyId);
        Assert.Null(manifest.Plan.RawStartExclusive);
        Assert.Equal(inspection.ComputeRawRangeSha256ForTest(observationAddress), manifest.Plan.RawRangeSha256);
        Assert.Equal("model-A", manifest.Parameters.ModelId);
        Assert.Empty(manifest.ToolSet.Definitions);
        Assert.Equal("surface-A", manifest.Target.CompletionSurfaceId);
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(client.Requests.Single()), manifest.Commitment);
    }

    [Fact]
    public async Task SendAsync_AfterRequestPreparedCommitted_LeavesDurableAwaitingCompletionBeforeProviderCall() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );

            Assert.Equal(SessionJournalFailpoint.AfterRequestPreparedCommitted, ex.Failpoint);
            SessionExecutionState state = engine.Project().ExecutionState;
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, state.Phase);
            Assert.Equal(SessionEventKind.CompletionRequestPrepared, state.HeadKind);
            Assert.Equal(engine.Project().Head, state.PendingRequestPreparedAddress);
            Assert.False(string.IsNullOrWhiteSpace(state.PendingCompletionAttemptId));
            Assert.False(string.IsNullOrWhiteSpace(state.ActiveCorrelationId));
            Assert.Equal(0, client.Calls);
        }

        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(client));
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, reopened.Project().ExecutionState.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ResumeAsync(CancellationToken.None));
        Assert.Equal(0, client.Calls);
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

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        )) {
            SessionJournalTurnAbortedException ex = await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(terminationKind, ex.Termination.Kind);
            Assert.Contains("known failure outcome were persisted", ex.Message, StringComparison.Ordinal);
            SessionExecutionState state = engine.Project().ExecutionState;
            Assert.Equal(SessionExecutionPhase.TurnFailed, state.Phase);
            Assert.Null(state.PendingRequestPreparedAddress);
            Assert.Null(state.PendingCompletionAttemptId);
            Assert.Null(state.ActiveCorrelationId);
        }

        EventAddress prepared = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        EventAddress failed = Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            Assert.Equal(prepared, journal.ReadEventHeaderChecked(failed).Unwrap().Parent);
        }
        using var reopened = SessionJournalEngine.Open(path);
        Assert.Equal(SessionExecutionPhase.TurnFailed, reopened.Project().ExecutionState.Phase);
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

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client)
        );
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("first", CancellationToken.None)
        );

        TurnResult recovered = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("recovered", recovered.Message.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, engine.Project().ExecutionState.Phase);
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
            await Assert.ThrowsAnyAsync<Exception>(() => engine.SendAsync("hello", CancellationToken.None));
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, engine.Project().ExecutionState.Phase);
        }

        Assert.Single(ReadJournalAddressesByKind(path, SessionEventKind.CompletionRequestPrepared));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
    }

    [Fact]
    public async Task SendAsync_MismatchedCompletionInvocation_LeavesPreparedWithoutAction() {
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
            await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, engine.Project().ExecutionState.Phase);
        }

        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.AgentActionProduced));
        Assert.Empty(ReadJournalAddressesByKind(path, SessionEventKind.CompletionAttemptFailed));
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

        await Assert.ThrowsAsync<IOException>(() => engine.SendAsync("hello", CancellationToken.None));

        Assert.Null(engine.GoverningSetupCursorHeadForTest);
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.Project().ExecutionState.Phase);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Project_CompletionAttemptFailedWithMismatchedAttemptId_Throws() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        EventAddress prepared;
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            prepared = engine.Project().Head!.Value;
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                prepared,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionAttemptFailed,
                    new CompletionAttemptFailedBody(
                        "different-attempt",
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
        Assert.Throws<InvalidDataException>(() => reopened.Project());
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
        engine.AppendRuntimeConfigSetup(new SessionRuntimeConfiguration("model-B", "surface-B", SessionJournalDefaults.Schema));
        engine.AppendSystemPromptSetup("system-B");

        TurnResult result = await engine.SendAsync("hello", CancellationToken.None);

        Assert.Equal("answer-B", result.Message.GetFlattenedText());
        Assert.Equal(0, client.RemainingResponses);
    }

    [Fact]
    public async Task ResumeAsync_AfterObservationCommitted_ReplaysCompletionAndCommitsAction() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(firstClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterObservationCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterObservationCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.Project().ExecutionState.Phase);
            Assert.Single(engine.Project().Context);
            Assert.Equal(0, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        resumeClient.Enqueue(
            request => {
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
                Assert.Equal("hello", observation.Content);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("resumed") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
        SessionProjection projection = reopened.Project();

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed", outcome.Message!.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(2, projection.Context.Count);
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
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted, ex.Failpoint);
            SessionExecutionState state = engine.Project().ExecutionState;
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, state.Phase);
            Assert.NotNull(state.PendingRequestPreparedAddress);
            Assert.False(string.IsNullOrWhiteSpace(state.PendingCompletionAttemptId));
            Assert.Equal(1, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient));
        InvalidOperationException resumeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains("CS-3C", resumeError.Message, StringComparison.Ordinal);
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, reopened.Project().ExecutionState.Phase);
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
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
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
                Assert.Equal(3, request.Context.Count);
                var action = Assert.IsType<ActionMessage>(request.Context[1]);
                Assert.Single(action.ToolCalls);
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
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
            TurnResult turn = await engine.SendAsync("need lookup", CancellationToken.None);
            SessionProjection projection = engine.Project();

            Assert.Equal("final", turn.Message.GetFlattenedText());
            Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
            Assert.Equal(4, projection.Context.Count);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(2, client.Calls);
        }

        string startedPayload = Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolExecutionStarted));
        string resultPayload = Assert.Single(ReadJournalPayloadJsonByKind(path, SessionEventKind.ToolResultObserved));
        Assert.Equal(2, ReadJournalPayloadJsonByKind(path, SessionEventKind.CompletionRequestPrepared).Length);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\",\"operationId\":\"" + ExtractOperationId(startedPayload) + "\"}}", startedPayload);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"status\":\"success\",\"blocks\":[{\"kind\":\"text\",\"content\":\"result:{\\\"q\\\":\\\"x\\\"}\"}]}}", resultPayload);
        Assert.DoesNotContain("opaqueEventKind", startedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", resultPayload, StringComparison.Ordinal);

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection replayed = reopened.Project();
        Assert.Equal(SessionExecutionPhase.Idle, replayed.ExecutionState.Phase);
        var replayedResults = Assert.IsType<ToolResultsMessage>(replayed.Context[2]);
        Assert.Equal("result:{\"q\":\"x\"}", Assert.Single(replayedResults.Results).GetFlattenedText());
    }

    [Fact]
    public async Task ResumeAsync_AfterToolStarted_ReexecutesToolAndUsesPersistedOperationId() {
        string path = NewJournalPath();
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
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolStartedCommitted, ex.Failpoint);
            SessionExecutionState state = engine.Project().ExecutionState;
            Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, state.Phase);
            Assert.True(state.PendingToolExecutionStarted);
            Assert.NotNull(state.PendingOperationId);
            Assert.Equal(0, firstTool.Calls);
        }

        string persistedOperationId;
        using (var inspection = SessionJournalEngine.Open(path)) {
            persistedOperationId = inspection.Project().ExecutionState.PendingOperationId!;
        }

        Assert.Equal(persistedOperationId, ExtractOperationId(ReadJournalPayloadJson(path)[^1]));

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "resumed-result"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Equal("resumed-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient, new ToolRegistry([resumeTool]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
        SessionProjection projection = reopened.Project();

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(1, resumeTool.Calls);
        Assert.False(string.IsNullOrWhiteSpace(persistedOperationId));
    }

    [Fact]
    public async Task ResumeAsync_AfterToolResult_CompletesWithoutReexecutingTool() {
        string path = NewJournalPath();
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
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, engine.Project().ExecutionState.Phase);
            Assert.Equal(1, tool.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Equal("persisted-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient, new ToolRegistry([resumeTool]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumeTool.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.Project().ExecutionState.Phase);
    }

    [Fact]
    public async Task ResumeAsync_AfterFirstToolResult_RestoresExecutionSequenceForNextTool() {
        string path = NewJournalPath();
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
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need two tools", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(1, alpha.Calls);
            Assert.Equal(0, beta.Calls);
            Assert.Equal(1, engine.Project().ExecutionState.ToolExecutionSequenceCheckpoint);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumedAlpha = new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        var resumedBeta = new RecordingTool("beta", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
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

        using var reopened = SessionJournalEngine.Open(path, CreateRuntime(resumeClient, new ToolRegistry([resumedAlpha, resumedBeta]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumedAlpha.Calls);
        Assert.Equal(1, resumedBeta.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.Project().ExecutionState.Phase);
    }

    [Fact]
    public async Task SendAsync_LaterToolTurn_ContinuesExecutionSequence() {
        string path = NewJournalPath();
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
            CreateRuntime(client, toolSession)
        );

        await engine.SendAsync("first", CancellationToken.None);
        TurnResult second = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("second done", second.Message.GetFlattenedText());
        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, engine.Project().ExecutionState.ToolExecutionSequenceCheckpoint);
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
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
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
            await engine.SendAsync("need two tools", CancellationToken.None);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();
        var results = Assert.IsType<ToolResultsMessage>(projection.Context[2]);
        Assert.Collection(
            results.Results,
            first => Assert.Equal("call-A", first.ToolCallId),
            second => Assert.Equal("call-B", second.ToolCallId)
        );
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
    }

    [Theory]
    [InlineData("skip-current-start")]
    [InlineData("duplicate-start")]
    [InlineData("result-before-start")]
    [InlineData("out-of-order-result")]
    [InlineData("duplicate-result")]
    public void Project_InvalidToolEventOrder_Throws(string invalidCase) {
        string path = CreateImportedTwoToolPendingJournal();
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            EventAddress head = journal.GetHead(main)!.Value;
            const string startA = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-A\"}}";
            const string startAAgain = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-A-2\"}}";
            const string startB = "{\"v\":1,\"body\":{\"toolCallId\":\"call-B\",\"toolName\":\"beta\",\"rawArgumentsJson\":\"{}\",\"operationId\":\"op-B\"}}";
            const string resultA = "{\"v\":1,\"body\":{\"toolCallId\":\"call-A\",\"toolName\":\"alpha\",\"status\":\"success\",\"blocks\":[]}}";
            const string resultB = "{\"v\":1,\"body\":{\"toolCallId\":\"call-B\",\"toolName\":\"beta\",\"status\":\"success\",\"blocks\":[]}}";

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
        Assert.Throws<InvalidDataException>(() => reopened.Project());
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("empty-id")]
    [InlineData("empty-name")]
    [InlineData("empty-arguments")]
    public void Project_InvalidActionToolCallIdentity_Throws(string invalidCase) {
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
            + ",\"invocation\":{\"providerId\":\"import\",\"apiSpecId\":\"import-v1\",\"model\":\"model-A\"}}}";
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = CommitToMain(journal, observation, SessionEventKind.ImportedAgentAction, payload);
        }

        using var reopened = SessionJournalEngine.Open(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => reopened.Project());
        Assert.Contains("tool call", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayHistory_MultipleToolCalls_UsesToolResultObservedRange() {
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
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("done")]),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client, registry.CreateSession())
        )) {
            await engine.SendAsync("need two tools", CancellationToken.None);
        }

        EventAddress[] toolResultAddresses = ReadJournalAddressesByKind(path, SessionEventKind.ToolResultObserved);
        Assert.Equal(2, toolResultAddresses.Length);

        using var reopened = SessionJournalEngine.Open(path);
        SessionHistoryReplay replay = reopened.ReplayHistory();

        Assert.Equal(4, replay.Messages.Count);
        Assert.IsType<ObservationMessage>(replay.Messages[0].Message);
        Assert.IsType<ActionMessage>(replay.Messages[1].Message);
        AddressedSessionHistoryMessage toolResultsEntry = replay.Messages[2];
        var toolResults = Assert.IsType<ToolResultsMessage>(toolResultsEntry.Message);
        Assert.Equal(toolResultAddresses[0], toolResultsEntry.SourceStartInclusive);
        Assert.Equal(toolResultAddresses[1], toolResultsEntry.SourceEndInclusive);
        Assert.Collection(
            toolResults.Results,
            first => Assert.Equal("call-A", first.ToolCallId),
            second => Assert.Equal("call-B", second.ToolCallId)
        );
        Assert.Equal("done", Assert.IsType<ActionMessage>(replay.Messages[3].Message).GetFlattenedText());
    }

    [Fact]
    public async Task ReplayHistory_UnclosedToolCalls_DoNotEmitToolResultsMessage() {
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
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need two tools", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();
        SessionHistoryReplay replay = reopened.ReplayHistory();

        Assert.Equal(projection.ExecutionState, replay.ExecutionState);
        Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, replay.ExecutionState.Phase);
        Assert.Equal("call-B", replay.ExecutionState.PendingToolCall?.ToolCallId);
        Assert.Equal(2, replay.Messages.Count);
        Assert.IsType<ObservationMessage>(replay.Messages[0].Message);
        Assert.IsType<ActionMessage>(replay.Messages[1].Message);
        Assert.DoesNotContain(replay.Messages, message => message.Message is ToolResultsMessage);
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

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        ToolSession? toolSession = null,
        int? maxTokens = null
    ) => new(
        client,
        toolSession,
        new SessionCompletionTargetIdentity(
            ConnectionId: "test-connection",
            Kind: "test",
            ConnectionFingerprint: "test-connection-fingerprint-v1",
            RequestAdapterFingerprint: "test-request-adapter-v1"
        ),
        maxTokens
    );

    private string CreateImportedTwoToolPendingJournal() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
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
}

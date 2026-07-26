using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionPreparedRequestReconstructorTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "test-tool-host",
        "test-tool-implementations-v1",
        "test-tool-capabilities-v1"
    );
    private readonly List<string> _tempDirectories = [];

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
    public void Reconstruct_SourcePrepared_FullRawReturnsExactCanonicalRequest() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema)
        );
        EventAddress prompt = Commit(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress created = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody()
        );
        EventAddress observation = Commit(
            journal,
            created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("hello")
        );

        var request = new CompletionRequest(
            "model-A",
            "system-A",
            [new ObservationMessage("hello")],
            ImmutableArray<ToolDefinition>.Empty,
            MaxTokens: 321
        );
        CompletionRequestPreparedBody manifest = CreateManifest(
            journal,
            request,
            runtime,
            prompt,
            rawStartExclusive: null,
            rawEndInclusive: observation,
            rawAddresses: [runtime, prompt, created, observation],
            SessionRequestManifestDefaults.FullRawSelectionPolicyId,
            artifactInputs: ImmutableArray<SessionRequestArtifactInput>.Empty
        );
        EventAddress prepared = Commit(
            journal,
            observation,
            SessionEventKind.CompletionRequestPrepared,
            manifest
        );

        SessionPreparedRequestReconstruction reconstructed =
            SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);

        Assert.Equal(prepared, reconstructed.SourcePreparedAddress);
        Assert.Equal(observation, reconstructed.RawEndInclusive);
        Assert.Equal(
            SessionRequestCanonicalizer.Canonicalize(request),
            reconstructed.CanonicalBytes
        );
        Assert.Equal(manifest, reconstructed.Manifest);
    }

    [Fact]
    public void Reconstruct_CoreExplicitArtifactTailUsesOnlyInlineSnapshotAndSuffix() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema)
        );
        EventAddress prompt = Commit(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress rawStart = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody()
        );
        EventAddress runtimeB = Commit(
            journal,
            rawStart,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-B", "surface-A", SessionJournalDefaults.Schema)
        );
        EventAddress promptB = Commit(
            journal,
            runtimeB,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-B")
        );
        EventAddress observation = Commit(
            journal,
            promptB,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("new observation")
        );
        var snapshot = new SessionRequestArtifactContextSnapshot(
            "memory system",
            "remembered observation",
            "remembered answer"
        );
        var artifactInput = new SessionRequestArtifactInput(
            "artifact-that-does-not-exist",
            "rolling-summary",
            SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
            snapshot
        );
        var request = new CompletionRequest(
            "model-B",
            "system-B\n\nmemory system",
            [
                new ObservationMessage("remembered observation"),
                new ActionMessage([new ActionBlock.Text("remembered answer")]),
                new ObservationMessage("new observation")
            ],
            ImmutableArray<ToolDefinition>.Empty,
            MaxTokens: null
        );
        CompletionRequestPreparedBody manifest = CreateManifest(
            journal,
            request,
            runtimeB,
            promptB,
            rawStart,
            observation,
            rawAddresses: [runtimeB, promptB, observation],
            SessionRequestManifestDefaults.ExplicitArtifactTailSelectionPolicyId,
            artifactInputs: [artifactInput]
        );

        SessionPreparedRequestReconstruction reconstructed =
            SessionPreparedRequestReconstructor.Reconstruct(journal, manifest, observation);

        Assert.Null(reconstructed.SourcePreparedAddress);
        Assert.Equal(
            SessionRequestCanonicalizer.Canonicalize(request),
            reconstructed.CanonicalBytes
        );
        Assert.Equal(
            ["remembered observation", "remembered answer", "new observation"],
            reconstructed.Request.Context.Select(Flatten).ToArray()
        );
    }

    [Fact]
    public void Reconstruct_CommittedLegacyExplicitTail_WithObservationSeedAndPreparedActionSuffix_IsExact() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema
            )
        );
        EventAddress prompt = Commit(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress created = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody()
        );
        EventAddress rawStartObservation = Commit(
            journal,
            created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("absorbed observation")
        );
        var firstRequest = new CompletionRequest(
            "model-A",
            "system-A",
            [new ObservationMessage("absorbed observation")],
            ImmutableArray<ToolDefinition>.Empty,
            MaxTokens: null
        );
        CompletionRequestPreparedBody firstManifest = CreateManifest(
            journal,
            firstRequest,
            runtime,
            prompt,
            rawStartExclusive: null,
            rawStartObservation,
            rawAddresses: [
                runtime,
                prompt,
                created,
                rawStartObservation
            ],
            SessionRequestManifestDefaults.FullRawSelectionPolicyId,
            artifactInputs: []
        );
        EventAddress firstPrepared = Commit(
            journal,
            rawStartObservation,
            SessionEventKind.CompletionRequestPrepared,
            firstManifest
        );
        EventAddress firstAction = Commit(
            journal,
            firstPrepared,
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("live answer")]),
                new CompletionDescriptor("client-A", "api-A", "model-A"),
                firstManifest.Attempt.CorrelationId,
                firstManifest.Execution,
                ToolRuntimeIdentity: null
            )
        );
        EventAddress finalObservation = Commit(
            journal,
            firstAction,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("next observation")
        );
        var snapshot = new SessionRequestArtifactContextSnapshot(
            "",
            "absorbed observation",
            "absorbed answer"
        );
        var artifactInput = new SessionRequestArtifactInput(
            "deleted-legacy-sidecar",
            "rolling-summary",
            SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
            snapshot
        );
        var exactRequest = new CompletionRequest(
            "model-A",
            "system-A",
            [
                new ObservationMessage("absorbed observation"),
                new ActionMessage([new ActionBlock.Text("absorbed answer")]),
                new ActionMessage([new ActionBlock.Text("live answer")]),
                new ObservationMessage("next observation")
            ],
            ImmutableArray<ToolDefinition>.Empty,
            MaxTokens: null
        );
        CompletionRequestPreparedBody legacyManifest = CreateManifest(
            journal,
            exactRequest,
            runtime,
            prompt,
            rawStartObservation,
            finalObservation,
            rawAddresses: [
                firstPrepared,
                firstAction,
                finalObservation
            ],
            SessionRequestManifestDefaults.ExplicitArtifactTailSelectionPolicyId,
            artifactInputs: [artifactInput]
        );
        EventAddress committedLegacyPrepared = Commit(
            journal,
            finalObservation,
            SessionEventKind.CompletionRequestPrepared,
            legacyManifest
        );

        SessionPreparedRequestReconstruction reconstructed =
            SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                committedLegacyPrepared
            );

        Assert.Equal(committedLegacyPrepared, reconstructed.SourcePreparedAddress);
        Assert.Equal(finalObservation, reconstructed.RawEndInclusive);
        Assert.Equal(
            SessionRequestCanonicalizer.Canonicalize(exactRequest),
            reconstructed.CanonicalBytes
        );
        Assert.Equal(legacyManifest.Commitment, reconstructed.Manifest.Commitment);
        Assert.False(Directory.Exists(
            Path.Combine(path, "derived")
        ));
    }

    [Fact]
    public void Reconstruct_FullRawSettledToolResultBuildsToolContinuationRequest() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema)
        );
        EventAddress prompt = Commit(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress created = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody()
        );
        EventAddress observation = Commit(
            journal,
            created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("look it up")
        );
        var action = new ActionMessage([
            new ActionBlock.Text("checking"),
            new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
        ]);
        EventAddress actionAddress = Commit(
            journal,
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                action,
                new CompletionDescriptor("import", "import-v1", "model-A"),
                $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}",
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity
            )
        );
        EventAddress started = Commit(
            journal,
            actionAddress,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-1",
                "lookup",
                "{\"q\":\"x\"}",
                "operation-1",
                1,
                ToolRuntimeIdentity
            )
        );
        EventAddress result = Commit(
            journal,
            started,
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-1",
                "lookup",
                1,
                ToolExecutionStatus.Success,
                [new ToolResultBlock.Text("found")]
            )
        );
        ImmutableArray<ToolDefinition> tools = [
            new ToolDefinition("lookup", "Lookup", new ToolSchema.Object([]))
        ];
        var request = new CompletionRequest(
            "model-A",
            "system-A",
            [
                new ObservationMessage("look it up"),
                action,
                new ToolResultsMessage(
                    content: null,
                    [ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "found")]
                )
            ],
            tools
        );
        CompletionRequestPreparedBody manifest = CreateManifest(
            journal,
            request,
            runtime,
            prompt,
            rawStartExclusive: null,
            rawEndInclusive: result,
            rawAddresses: [runtime, prompt, created, observation, actionAddress, started, result],
            SessionRequestManifestDefaults.FullRawSelectionPolicyId,
            artifactInputs: ImmutableArray<SessionRequestArtifactInput>.Empty,
            reason: "tool-continuation",
            correlationObservation: observation
        );

        SessionPreparedRequestReconstruction reconstructed =
            SessionPreparedRequestReconstructor.Reconstruct(journal, manifest, result);

        Assert.Equal(
            SessionRequestCanonicalizer.Canonicalize(request),
            reconstructed.CanonicalBytes
        );
        Assert.IsType<ToolResultsMessage>(reconstructed.Request.Context[^1]);
        Assert.Equal(tools, reconstructed.Request.Tools);
    }

    [Fact]
    public void Reconstruct_RejectsRangeSetupBoundaryTargetAndCommitmentCorruption() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema)
        );
        EventAddress prompt = Commit(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress created = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody()
        );
        EventAddress observation = Commit(
            journal,
            created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("hello")
        );
        var request = new CompletionRequest(
            "model-A",
            "system-A",
            [new ObservationMessage("hello")],
            ImmutableArray<ToolDefinition>.Empty
        );
        CompletionRequestPreparedBody manifest = CreateManifest(
            journal,
            request,
            runtime,
            prompt,
            rawStartExclusive: null,
            rawEndInclusive: observation,
            rawAddresses: [runtime, prompt, created, observation],
            SessionRequestManifestDefaults.FullRawSelectionPolicyId,
            artifactInputs: ImmutableArray<SessionRequestArtifactInput>.Empty
        );

        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest with { Plan = manifest.Plan with { RawRangeSha256 = new string('0', 64) } },
            observation
        ));
        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest with {
                Setups = manifest.Setups with {
                    RuntimeConfig = manifest.Setups.RuntimeConfig with {
                        PayloadSha256 = new string('0', 64)
                    }
                }
            },
            observation
        ));
        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest with {
                Attempt = manifest.Attempt with {
                    CorrelationId = "atelia.session-journal.turn.v1:wrong"
                }
            },
            observation
        ));
        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest with {
                Target = manifest.Target with { CompletionSurfaceId = "wrong-surface" }
            },
            observation
        ));
        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest with {
                Commitment = manifest.Commitment with { Sha256 = new string('0', 64) }
            },
            observation
        ));
        Assert.Throws<InvalidDataException>(() => SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            manifest,
            created
        ));
    }

    [Fact]
    public void Reconstruct_SourcePreparedRejectsAnotherEventKind() {
        string path = NewJournalPath();
        using var journal = CreateJournal(path);
        EventAddress runtime = Commit(
            journal,
            expectedParent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema)
        );

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SessionPreparedRequestReconstructor.Reconstruct(journal, runtime)
        );

        Assert.Contains("completion-request-prepared", error.Message, StringComparison.Ordinal);
    }

    private static CompletionRequestPreparedBody CreateManifest(
        EventJournal.EventJournal journal,
        CompletionRequest request,
        EventAddress runtimeSetup,
        EventAddress promptSetup,
        EventAddress? rawStartExclusive,
        EventAddress rawEndInclusive,
        IReadOnlyList<EventAddress> rawAddresses,
        string selectionPolicyId,
        ImmutableArray<SessionRequestArtifactInput> artifactInputs,
        string reason = "observation",
        EventAddress? correlationObservation = null
    ) {
        bool isFullRaw = string.Equals(
            selectionPolicyId,
            SessionRequestManifestDefaults.FullRawSelectionPolicyId,
            StringComparison.Ordinal
        );
        string correlation =
            $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(correlationObservation ?? rawEndInclusive)}";
        ImmutableArray<ToolDefinition> tools = request.Tools;
        SessionRequestCommitment commitment = SessionRequestCanonicalizer.CreateCommitment(request);
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt(
                "attempt-01",
                correlation,
                reason,
                ReplacesAttemptId: null
            ),
            new SessionExecutionCheckpoint(0),
            new SessionContextPlan(
                selectionPolicyId,
                isFullRaw
                    ? SessionRequestManifestDefaults.FullRawPlannerFingerprint
                    : SessionRequestManifestDefaults.ExplicitArtifactTailPlannerFingerprint,
                rawStartExclusive,
                ComputeRawRangeSha256(journal, rawStartExclusive, rawEndInclusive, rawAddresses),
                artifactInputs,
                ImmutableArray<SessionRequestRecalledInput>.Empty,
                isFullRaw
                    ? SessionRequestManifestDefaults.FullRawRenderingProfileId
                    : SessionRequestManifestDefaults.ExplicitArtifactTailRenderingProfileId,
                request.ModelId,
                EstimatedInputTokens: checked((commitment.ByteLength + 3) / 4),
                reason
            ),
            new SessionGoverningSetupReferences(
                CreateSetupReference(journal, runtimeSetup),
                CreateSetupReference(journal, promptSetup)
            ),
            new SessionRequestParameters(request.ModelId, request.MaxTokens),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools,
                tools.IsEmpty
                    ? null
                    : ToolRuntimeIdentity
            ),
            new SessionRequestRendering(
                isFullRaw
                    ? SessionRequestManifestDefaults.FullRawContextRendererId
                    : SessionRequestManifestDefaults.ExplicitArtifactTailContextRendererId,
                isFullRaw
                    ? SessionRequestManifestDefaults.FullRawContextRendererFingerprint
                    : SessionRequestManifestDefaults.ExplicitArtifactTailContextRendererFingerprint,
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestManifestDefaults.ReasoningCodecSetFingerprint
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity(
                    "connection-A",
                    "test",
                    "connection-fingerprint-A",
                    "adapter-fingerprint-A"
                ),
                "surface-A",
                "client-A",
                "api-A"
            ),
            commitment
        );
    }

    private static SessionSetupReference CreateSetupReference(
        EventJournal.EventJournal journal,
        EventAddress address
    ) {
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        _ = SessionEventCodec.Decode(kind, frame.Payload, out int bodySchemaVersion);
        return new SessionSetupReference(
            address,
            bodySchemaVersion,
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
        );
    }

    private static string ComputeRawRangeSha256(
        EventJournal.EventJournal journal,
        EventAddress? rawStartExclusive,
        EventAddress rawEndInclusive,
        IReadOnlyList<EventAddress> addresses
    ) {
        var entries = new List<SessionRawRangeHashEntry>(addresses.Count);
        foreach (EventAddress address in addresses) {
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            _ = SessionEventCodec.Decode(kind, frame.Payload, out int bodySchemaVersion);
            entries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }
        return SessionRawRangeHasher.Compute(rawStartExclusive, rawEndInclusive, entries);
    }

    private static EventJournal.EventJournal CreateJournal(string path) {
        EventJournal.EventJournal journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
        return journal;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static string Flatten(IHistoryMessage message)
        => message switch {
            ObservationMessage observation => observation.Content ?? string.Empty,
            ActionMessage action => action.GetFlattenedText(),
            _ => message.ToString() ?? string.Empty
        };

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-prepared-reconstructor-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }
}

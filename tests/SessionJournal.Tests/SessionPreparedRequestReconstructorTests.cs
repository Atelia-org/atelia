using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionPreparedRequestReconstructorTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolIdentity = new(
        "test-host",
        "test-implementations-v1",
        "test-capabilities-v1"
    );
    private readonly List<string> _tempDirectories = [];

    static SessionPreparedRequestReconstructorTests() {
        ReasoningBlockCodecRegistry.Shared.Register(new OpaqueReasoningCodec());
    }

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void Reconstruct_CoherentToolContinuationExactlyReopensOpaqueReasoningAndToolResult() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
        Scenario scenario = CreateToolContinuationScenario(journal);

        SessionPreparedRequestReconstruction direct =
            SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                scenario.Manifest,
                scenario.RawEnd
            );
        EventAddress prepared = Commit(
            journal,
            scenario.RawEnd,
            SessionEventKind.CompletionRequestPrepared,
            scenario.Manifest
        );
        SessionPreparedRequestReconstruction committed =
            SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);

        byte[] expected = SessionRequestCanonicalizer.Canonicalize(
            scenario.ExpectedRequest
        );
        Assert.Equal(expected, direct.CanonicalBytes);
        Assert.Equal(expected, committed.CanonicalBytes);
        Assert.Equal(scenario.Manifest.Commitment, SessionRequestCanonicalizer.CreateCommitment(
            committed.Request
        ));
        var action = Assert.IsType<ActionMessage>(committed.Request.Context[^2]);
        var reasoning = Assert.IsType<OpaqueReasoningBlock>(action.Blocks[0]);
        Assert.Equal([0, 1, 2, 254, 255], reasoning.Payload);
        Assert.Single(action.ToolCalls);
        var results = Assert.IsType<ToolResultsMessage>(
            committed.Request.Context[^1]
        );
        Assert.Equal("exact result", results.Results.Single().GetFlattenedText());
    }

    [Fact]
    public void Reconstruct_RejectsRawStartActivationOrderAndCommitmentDrift() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
        Scenario scenario = CreateToolContinuationScenario(journal);

        EventAddress promptParent = journal.ReadEventHeaderChecked(
            scenario.Created
        ).Unwrap().Parent!.Value;
        string expandedHash = ComputeRawRangeSha256(
            journal,
            promptParent,
            scenario.RawEnd,
            [scenario.Created, .. scenario.RawAddresses]
        );
        _ = SessionPreparedRequestReconstructor.Reconstruct(
            journal,
            scenario.Manifest with {
                Plan = scenario.Manifest.Plan with {
                    RawStartExclusive = promptParent,
                    RawRangeSha256 = expandedHash
                }
            },
            scenario.RawEnd
        );

        Assert.Throws<InvalidDataException>(() =>
            SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                scenario.Manifest with {
                    Plan = scenario.Manifest.Plan with {
                        ExactContextInputs = [
                            scenario.Manifest.Plan.ExactContextInputs[0] with {
                                ContentSha256 = new string('0', 64)
                            },
                            scenario.Manifest.Plan.ExactContextInputs[1]
                        ]
                    }
                },
                scenario.RawEnd
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                scenario.Manifest with {
                    Commitment = scenario.Manifest.Commitment with {
                        Sha256 = new string('0', 64)
                    }
                },
                scenario.RawEnd
            )
        );
    }

    [Fact]
    public void Reconstruct_RejectsPreparedSetupsFromSideBranchEvenWithRecomputedRequestCommitment() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
        Scenario scenario = CreateToolContinuationScenario(journal);
        const string sideBranch = "side";
        journal.CreateBranch(sideBranch, scenario.Prompt).Unwrap();
        EventAddress sideRuntime = Commit(
            journal,
            sideBranch,
            scenario.Prompt,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-side",
                "surface-side",
                SessionJournalDefaults.Schema
            )
        );
        EventAddress sidePrompt = Commit(
            journal,
            sideBranch,
            sideRuntime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-side")
        );
        var forgedRequest = new CompletionRequest(
            "model-side",
            scenario.ExpectedRequest.SystemPrompt.Replace(
                "system-A",
                "system-side",
                StringComparison.Ordinal
            ),
            scenario.ExpectedRequest.Context,
            scenario.ExpectedRequest.Tools,
            scenario.ExpectedRequest.MaxTokens
        );
        CompletionRequestPreparedBody forged = scenario.Manifest with {
            Setups = new SessionGoverningSetupReferences(
                CreateSetupReference(journal, sideRuntime),
                CreateSetupReference(journal, sidePrompt)
            ),
            Parameters = scenario.Manifest.Parameters with {
                ModelId = forgedRequest.ModelId
            },
            Commitment = SessionRequestCanonicalizer.CreateCommitment(
                forgedRequest
            )
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                forged,
                scenario.RawEnd
            )
        );

        Assert.Contains(
            "pinned setup",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Reconstruct_IgnoresCorruptLegacyActivationReferences() {
        string coveragePath = NewJournalPath();
        using (EventJournal.EventJournal journal = CreateJournal(coveragePath)) {
            Scenario scenario = CreateToolContinuationScenario(
                journal,
                body => body with {
                    CoverageSetups = body.CoverageSetups with {
                        RuntimeConfig = body.CoverageSetups.RuntimeConfig with {
                            PayloadSha256 = new string('0', 64)
                        }
                    }
                }
            );

            _ = SessionPreparedRequestReconstructor.Reconstruct(
                journal, scenario.Manifest, scenario.RawEnd
            );
        }

        string currentPath = NewJournalPath();
        using (EventJournal.EventJournal journal = CreateJournal(currentPath)) {
            Scenario scenario = CreateToolContinuationScenario(
                journal,
                body => body with {
                    CurrentSetups = body.CurrentSetups with {
                        SystemPrompt = body.CurrentSetups.SystemPrompt with {
                            PayloadSha256 = new string('0', 64)
                        }
                    }
                }
            );

            _ = SessionPreparedRequestReconstructor.Reconstruct(
                journal, scenario.Manifest, scenario.RawEnd
            );
        }
    }

    [Fact]
    public void Reconstruct_RejectsRawRangeSetupModelAndCorrelationCorruption() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
        Scenario scenario = CreateToolContinuationScenario(journal);
        SessionSetupReference promptReference =
            CreateSetupReference(journal, scenario.Prompt);

        var corruptions = new Dictionary<string, CompletionRequestPreparedBody> {
            ["raw range hash"] = scenario.Manifest with {
                Plan = scenario.Manifest.Plan with {
                    RawRangeSha256 = new string('0', 64)
                }
            },
            ["setup kind"] = scenario.Manifest with {
                Setups = scenario.Manifest.Setups with {
                    RuntimeConfig = promptReference
                }
            },
            ["setup hash"] = scenario.Manifest with {
                Setups = scenario.Manifest.Setups with {
                    RuntimeConfig =
                        scenario.Manifest.Setups.RuntimeConfig with {
                            PayloadSha256 = new string('0', 64)
                        }
                }
            },
            ["setup schema"] = scenario.Manifest with {
                Setups = scenario.Manifest.Setups with {
                    RuntimeConfig =
                        scenario.Manifest.Setups.RuntimeConfig with {
                            BodySchemaVersion = 2
                        }
                }
            },
            ["model"] = scenario.Manifest with {
                Parameters = scenario.Manifest.Parameters with {
                    ModelId = "model-corrupt"
                }
            },
            ["correlation"] = scenario.Manifest with {
                Origin = scenario.Manifest.Origin with {
                    CorrelationId = "correlation-corrupt"
                }
            }
        };

        foreach ((string name, CompletionRequestPreparedBody manifest)
            in corruptions) {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => SessionPreparedRequestReconstructor.Reconstruct(
                    journal,
                    manifest,
                    scenario.RawEnd
                )
            );
            Assert.NotEmpty(error.Message);
        }
    }

    [Fact]
    public void Reconstruct_RejectsNonAncestorRawStartAndBuriedMalformedToolSuffix() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
        Scenario scenario = CreateToolContinuationScenario(journal);
        const string sideBranch = "raw-start-side";
        journal.CreateBranch(sideBranch, scenario.Created).Unwrap();
        EventAddress sideObservation = Commit(
            journal,
            sideBranch,
            scenario.Created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("side")
        );

        InvalidDataException parentError =
            Assert.Throws<InvalidDataException>(() =>
                SessionPreparedRequestReconstructor.Reconstruct(
                    journal,
                    scenario.Manifest with {
                        Plan = scenario.Manifest.Plan with {
                            RawStartExclusive = sideObservation
                        }
                    },
                    scenario.RawEnd
                )
            );
        Assert.Contains(
            "not an ancestor",
            parentError.Message,
            StringComparison.Ordinal
        );

        string malformedPath = NewJournalPath();
        using EventJournal.EventJournal malformedJournal =
            CreateJournal(malformedPath);
        Scenario malformed = CreateToolContinuationScenario(
            malformedJournal,
            mutateStarted: body => body with {
                ToolCallId = "buried-wrong-call"
            }
        );

        Assert.Throws<InvalidDataException>(() =>
            SessionPreparedRequestReconstructor.Reconstruct(
                malformedJournal,
                malformed.Manifest,
                malformed.RawEnd
            )
        );
    }

    [Fact]
    public void PreparedV4_RejectsMultiCarrierExactContextInput() {
        CompletionRequestPreparedBody manifest = PreparedV4Fixture.Create(
            "correlation", "observation", Address(1), Address(2), Address(3), Address(4),
            "model", [], null
        );
        SessionRequestContextInput first = manifest.Plan.ExactContextInputs[0];
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            manifest with { Plan = manifest.Plan with {
                ExactContextInputs = [first with { ContextSnapshot = new("a", "b", "") }, manifest.Plan.ExactContextInputs[1]]
            }}
        ));
    }

    [Fact]
    public void Reconstruct_SourcePreparedRejectsAnotherEventKind() {
        string path = NewJournalPath();
        using EventJournal.EventJournal journal = CreateJournal(path);
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

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SessionPreparedRequestReconstructor.Reconstruct(journal, runtime)
        );

        Assert.Contains(
            "completion-request-prepared",
            error.Message,
            StringComparison.Ordinal
        );
    }

    private static Scenario CreateToolContinuationScenario(
        EventJournal.EventJournal journal,
        Func<ArtifactSetCommittedBody, ArtifactSetCommittedBody>?
            mutateActivation = null,
        Func<ToolExecutionStartedBody, ToolExecutionStartedBody>?
            mutateStarted = null
    ) {
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
        SessionRequestArtifactInput system = Artifact(
            "artifact-system",
            "rolling-summary",
            new SessionRequestArtifactContextSnapshot("memory system", "", "")
        );
        SessionRequestArtifactInput world = Artifact(
            "artifact-world",
            "world-understanding",
            new SessionRequestArtifactContextSnapshot("", "memory world", "")
        );
        ImmutableArray<SessionRequestArtifactInput> inputs = [system, world];
        SessionGoverningSetupReferences setups = new(
            CreateSetupReference(journal, runtime),
            CreateSetupReference(journal, prompt)
        );
        var activationBody = new ArtifactSetCommittedBody(
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
            created,
            setups,
            setups,
            [
                new SessionArtifactSetMember(
                    "system",
                    system.ArtifactId,
                    system.ArtifactKind,
                    new MemoryPackBlockPath(MemoryPackCarrier.System, "system"),
                    system.ContentSha256
                ),
                new SessionArtifactSetMember(
                    "world",
                    world.ArtifactId,
                    world.ArtifactKind,
                    new MemoryPackBlockPath(MemoryPackCarrier.Observation, "world"),
                    world.ContentSha256
                )
            ]
        );
        if (mutateActivation is not null) {
            activationBody = mutateActivation(activationBody);
        }
        EventAddress activation = Commit(
            journal,
            created,
            SessionEventKind.ArtifactSetCommitted,
            activationBody
        );
        SessionArtifactSetReference activationReference =
            CreateArtifactSetReference(journal, activation);
        EventAddress observation = Commit(
            journal,
            activation,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("hello")
        );
        ImmutableArray<ToolDefinition> tools = [
            new ToolDefinition("sample", "sample tool", new ToolSchema.Object())
        ];
        (string systemPrompt, ImmutableArray<IHistoryMessage> header) =
            SessionCoherentRequestRecipe.Expand(
                "system-A",
                SessionCoherentRequestRecipe.Aggregate(inputs.Select(static input => input.ContextSnapshot).ToArray())
            );
        var initialContext = ImmutableArray.CreateBuilder<IHistoryMessage>();
        initialContext.AddRange(header);
        initialContext.Add(new ObservationMessage("hello"));
        var initialRequest = new CompletionRequest(
            "model-A",
            systemPrompt,
            initialContext.ToImmutable(),
            tools
        );
        string correlation =
            $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";
        CompletionRequestPreparedBody initialManifest = CreateManifest(
            journal,
            initialRequest,
            runtime,
            prompt,
            created,
            observation,
            [activation, observation],
            inputs,
            activationReference,
            "observation",
            correlation,
            checkpoint: 0
        );
        EventAddress initialPrepared = Commit(
            journal,
            observation,
            SessionEventKind.CompletionRequestPrepared,
            initialManifest
        );
        var actionMessage = new ActionMessage([
            new OpaqueReasoningBlock(
                [0, 1, 2, 254, 255],
                new CompletionDescriptor("provider", "api", "model-A"),
                "opaque debug"
            ),
            new ActionBlock.ToolCall(
                new RawToolCall("sample", "call-1", """{"value":1}""")
            )
        ]);
        EventAddress completionStarted = Commit(
            journal,
            initialPrepared,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress action = Commit(
            journal,
            completionStarted,
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                actionMessage,
                new CompletionDescriptor("provider", "api", "model-A"),
                correlation,
                new SessionExecutionCheckpoint(0),
                ToolIdentity
            )
        );
        var startedBody = new ToolExecutionStartedBody(
            "call-1",
            "sample",
            """{"value":1}""",
            "operation-1",
            1,
            ToolIdentity
        );
        if (mutateStarted is not null) {
            startedBody = mutateStarted(startedBody);
        }
        EventAddress started = Commit(
            journal,
            action,
            SessionEventKind.ToolExecutionStarted,
            startedBody
        );
        ToolResult result = ToolResult.FromText(
            "sample",
            "call-1",
            ToolExecutionStatus.Success,
            "exact result"
        );
        EventAddress observed = Commit(
            journal,
            started,
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-1",
                "sample",
                1,
                ToolExecutionStatus.Success,
                result.Blocks
            )
        );
        var continuationContext = ImmutableArray.CreateBuilder<IHistoryMessage>();
        continuationContext.AddRange(header);
        continuationContext.Add(new ObservationMessage("hello"));
        continuationContext.Add(actionMessage);
        continuationContext.Add(new ToolResultsMessage(null, [result]));
        var continuationRequest = new CompletionRequest(
            "model-A",
            systemPrompt,
            continuationContext.ToImmutable(),
            tools
        );
        EventAddress[] rawAddresses = [
            activation,
            observation,
            initialPrepared,
            completionStarted,
            action,
            started,
            observed
        ];
        CompletionRequestPreparedBody continuationManifest = CreateManifest(
            journal,
            continuationRequest,
            runtime,
            prompt,
            created,
            observed,
            rawAddresses,
            inputs,
            activationReference,
            "tool-continuation",
            correlation,
            checkpoint: 1
        );
        return new Scenario(
            runtime,
            prompt,
            created,
            observed,
            rawAddresses,
            continuationRequest,
            continuationManifest
        );
    }

    private static CompletionRequestPreparedBody CreateManifest(
        EventJournal.EventJournal journal,
        CompletionRequest request,
        EventAddress runtime,
        EventAddress prompt,
        EventAddress rawStart,
        EventAddress rawEnd,
        IReadOnlyList<EventAddress> rawAddresses,
        ImmutableArray<SessionRequestArtifactInput> inputs,
        SessionArtifactSetReference activation,
        string reason,
        string correlation,
        long checkpoint
    ) {
        _ = activation;
        return new(
        new SessionRequestOrigin(correlation, reason),
        new SessionExecutionCheckpoint(checkpoint),
        new SessionContextPlan(
            rawStart,
            ComputeRawRangeSha256(journal, rawStart, rawEnd, rawAddresses),
            new SessionGoverningSetupReferences(
                CreateSetupReference(journal, runtime),
                CreateSetupReference(journal, prompt)
            ),
            inputs.Select(static input => new SessionRequestContextInput(
                input.ContentSha256, input.ContextSnapshot
            )).ToImmutableArray()
        ),
        new SessionGoverningSetupReferences(
            CreateSetupReference(journal, runtime),
            CreateSetupReference(journal, prompt)
        ),
        new SessionRequestParameters(request.ModelId, request.MaxTokens),
        new SessionRequestToolSet(
            SessionRequestManifestDefaults.ToolCodecId,
            SessionRequestCanonicalizer.ComputeToolSetSha256(request.Tools),
            request.Tools,
            request.Tools.IsEmpty ? null : ToolIdentity
        ),
        new SessionRequestRecipe(
            SessionRequestManifestDefaults.RecipeId,
            SessionRequestManifestDefaults.CanonicalRequestCodecId
        ),
        new SessionRequestTarget(
            new SessionCompletionTargetIdentity(
                "connection-A",
                "test",
                "connection-fingerprint-A",
                "adapter-fingerprint-A"
            ),
            "client-A",
            "api-A"
        ),
        SessionRequestCanonicalizer.CreateCommitment(request)
        );
    }

    private static SessionRequestArtifactInput Artifact(
        string artifactId,
        string artifactKind,
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        artifactId,
        artifactKind,
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );

    private static SessionSetupReference CreateSetupReference(
        EventJournal.EventJournal journal,
        EventAddress address
    ) {
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        _ = SessionEventCodec.Decode(kind, frame.Payload, out int version);
        return new(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
        );
    }

    private static SessionArtifactSetReference CreateArtifactSetReference(
        EventJournal.EventJournal journal,
        EventAddress address
    ) {
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        _ = SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            frame.Payload,
            out int version
        );
        return new(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
        );
    }

    private static string ComputeRawRangeSha256(
        EventJournal.EventJournal journal,
        EventAddress rawStart,
        EventAddress rawEnd,
        IReadOnlyList<EventAddress> addresses
    ) {
        var entries = new List<SessionRawRangeHashEntry>(addresses.Count);
        foreach (EventAddress address in addresses) {
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            _ = SessionEventCodec.Decode(kind, frame.Payload, out int version);
            entries.Add(new(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                version,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }
        return SessionRawRangeHasher.Compute(rawStart, rawEnd, entries);
    }

    private static EventJournal.EventJournal CreateJournal(string path) {
        EventJournal.EventJournal journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        return journal;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => Commit(
        journal,
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        kind,
        body
    );

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        string branchName,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        branchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:X16}0000000100000000".ToLowerInvariant()
        );

    private static SessionGoverningSetupReferences SetupReferences()
        => new(
            new SessionSetupReference(Address(2), 1, new string('a', 64)),
            new SessionSetupReference(Address(3), 1, new string('b', 64))
        );

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-prepared-v2-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed record Scenario(
        EventAddress Runtime,
        EventAddress Prompt,
        EventAddress Created,
        EventAddress RawEnd,
        IReadOnlyList<EventAddress> RawAddresses,
        CompletionRequest ExpectedRequest,
        CompletionRequestPreparedBody Manifest
    );

    private sealed record OpaqueReasoningBlock(
        byte[] Payload,
        CompletionDescriptor Origin,
        string? PlainTextForDebug
    ) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug);

    private sealed class OpaqueReasoningCodec : IReasoningBlockCodec {
        public string CodecId => "atelia.tests.opaque-reasoning.v1";

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is OpaqueReasoningBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            var opaque = Assert.IsType<OpaqueReasoningBlock>(block);
            return SerializedReasoningBlock.Create(
                CodecId,
                opaque.Origin,
                opaque.Payload,
                opaque.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(
            SerializedReasoningBlock serialized
        ) => new OpaqueReasoningBlock(
            serialized.Payload,
            serialized.ToOrigin(),
            serialized.PlainTextForDebug
        );
    }
}

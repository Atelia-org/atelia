using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalOfflineValidatorTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task RejectsPreparedThatReferencesSupersededActivation() {
        string path = NewPath();
        EventAddress activationA;
        using (EventJournal.EventJournal journal = CreateJournal(path)) {
            (
                EventAddress runtime,
                EventAddress prompt,
                EventAddress created
            ) = CommitBootstrap(journal);
            SessionGoverningSetupReferences setups = new(
                CreateSetupReference(journal, runtime),
                CreateSetupReference(journal, prompt)
            );
            ImmutableArray<SessionRequestArtifactInput> artifactInputs =
                CreateArtifactInputs();
            var activationBody = new ArtifactSetCommittedBody(
                SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
                SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
                created,
                setups,
                setups,
                CreateMembers(artifactInputs)
            );
            activationA = Commit(
                journal,
                created,
                SessionEventKind.ArtifactSetCommitted,
                activationBody
            );
            EventAddress activationB = Commit(
                journal,
                activationA,
                SessionEventKind.ArtifactSetCommitted,
                activationBody
            );
            EventAddress observation = Commit(
                journal,
                activationB,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("hello")
            );
            CompletionRequestPreparedBody prepared = CreateCoherentPrepared(
                journal,
                runtime,
                prompt,
                created,
                observation,
                [activationA, activationB, observation],
                artifactInputs,
                CreateArtifactSetReference(journal, activationA)
            );
            _ = Commit(
                journal,
                observation,
                SessionEventKind.CompletionRequestPrepared,
                prepared
            );
        }

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await SessionJournalOfflineValidator.ValidateAsync(
                    path
                )
            );
        Assert.Contains(
            "latest active",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task RejectsHistoricalActivationWithStaleCurrentSetup() {
        string path = NewPath();
        using (EventJournal.EventJournal journal = CreateJournal(path)) {
            (
                EventAddress runtime,
                EventAddress prompt,
                EventAddress created
            ) = CommitBootstrap(journal);
            EventAddress promptB = Commit(
                journal,
                created,
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system-B")
            );
            SessionGoverningSetupReferences coverage = new(
                CreateSetupReference(journal, runtime),
                CreateSetupReference(journal, prompt)
            );
            SessionGoverningSetupReferences current = new(
                coverage.RuntimeConfig,
                CreateSetupReference(journal, promptB)
            );
            ImmutableArray<SessionArtifactSetMember> members =
                CreateMembers(CreateArtifactInputs());
            EventAddress stale = Commit(
                journal,
                promptB,
                SessionEventKind.ArtifactSetCommitted,
                new ArtifactSetCommittedBody(
                    SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
                    SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
                    created,
                    coverage,
                    coverage,
                    members
                )
            );
            _ = Commit(
                journal,
                stale,
                SessionEventKind.ArtifactSetCommitted,
                new ArtifactSetCommittedBody(
                    SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
                    SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
                    created,
                    coverage,
                    current,
                    members
                )
            );
        }

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await SessionJournalOfflineValidator.ValidateAsync(
                    path
                )
            );
        Assert.Contains(
            "setup references",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ReconstructsEveryHistoricalPreparedCommitment() {
        string path = NewPath();
        var client = new NeverCalledCompletionClient();
        EventAddress prepared;
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            ),
            new SessionRuntime(
                client,
                CompletionTarget: new SessionCompletionTargetIdentity(
                    "offline-validation",
                    "test",
                    "offline-validation-v1",
                    "offline-adapter-v1"
                )
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                fixtureId: "offline-validator-historical-prepared"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync(
                    "prepared corruption",
                    CancellationToken.None
                )
            );
            prepared = engine.ResolveExecutionTail().Head
                ?? throw new Xunit.Sdk.XunitException(
                    "Failpoint did not commit Prepared."
                );
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            using EventFrame preparedFrame =
                journal.ReadEvent(prepared).Unwrap();
            EventAddress observation = preparedFrame.Header.Parent
                ?? throw new Xunit.Sdk.XunitException(
                    "Prepared has no observation parent."
                );
            var body = (CompletionRequestPreparedBody)
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionRequestPrepared,
                    preparedFrame.Payload,
                    out _
                );
            Assert.True(journal.MoveRef(main, prepared, observation).Unwrap());
            CompletionRequestPreparedBody corrupt = body with {
                Commitment = body.Commitment with {
                    Sha256 = new string('0', 64)
                }
            };
            EventAddress corruptPrepared = Commit(
                journal,
                observation,
                SessionEventKind.CompletionRequestPrepared,
                corrupt
            );
            EventAddress started = Commit(
                journal,
                corruptPrepared,
                SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody()
            );
            _ = Commit(
                journal,
                started,
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    CompletionTerminationKind.Failed,
                    "offline-test",
                    "terminal",
                    Array.AsReadOnly(Array.Empty<string>())
                )
            );
        }

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await SessionJournalOfflineValidator.ValidateAsync(
                    path
                )
            );
        Assert.Contains(
            "commitment",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(0, client.Calls);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for validator fixtures.
            }
        }
    }

    private static CompletionRequestPreparedBody CreateCoherentPrepared(
        EventJournal.EventJournal journal,
        EventAddress runtime,
        EventAddress prompt,
        EventAddress rawStartExclusive,
        EventAddress rawEndInclusive,
        IReadOnlyList<EventAddress> rawAddresses,
        ImmutableArray<SessionRequestArtifactInput> artifactInputs,
        SessionArtifactSetReference activeArtifactSet
    ) {
        SessionRequestArtifactContextSnapshot aggregate =
            SessionCoherentRequestRecipe.Aggregate(
                artifactInputs
            );
        (string systemPrompt, ImmutableArray<IHistoryMessage> headerContext) =
            SessionCoherentRequestRecipe.Expand(
                "system-A",
                aggregate
            );
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>();
        context.AddRange(headerContext);
        context.Add(new ObservationMessage("hello"));
        var request = new CompletionRequest(
            "model-A",
            systemPrompt,
            context.ToImmutable(),
            ImmutableArray<ToolDefinition>.Empty
        );
        SessionRequestCommitment commitment =
            SessionRequestCanonicalizer.CreateCommitment(request);
        string reason = "observation";
        return new CompletionRequestPreparedBody(
            new SessionRequestOrigin(
                $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(rawEndInclusive)}",
                reason
            ),
            new SessionExecutionCheckpoint(0),
            new SessionContextPlan(
                rawStartExclusive,
                ComputeRawRangeSha256(
                    journal,
                    rawStartExclusive,
                    rawEndInclusive,
                    rawAddresses
                ),
                artifactInputs,
                activeArtifactSet
            ),
            new SessionGoverningSetupReferences(
                CreateSetupReference(journal, runtime),
                CreateSetupReference(journal, prompt)
            ),
            new SessionRequestParameters("model-A", MaxTokens: null),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(
                    ImmutableArray<ToolDefinition>.Empty
                ),
                ImmutableArray<ToolDefinition>.Empty,
                RuntimeIdentity: null
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
            commitment
        );
    }

    private static (
        EventAddress Runtime,
        EventAddress Prompt,
        EventAddress Created
    ) CommitBootstrap(EventJournal.EventJournal journal) {
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
        return (runtime, prompt, created);
    }

    private static ImmutableArray<SessionRequestArtifactInput>
        CreateArtifactInputs()
        => [
            CreateArtifactInput(
                "artifact-autobiography",
                "rolling-summary",
                new SessionRequestArtifactContextSnapshot(
                    "",
                    "",
                    "## autobiography\n\nself"
                )
            ),
            CreateArtifactInput(
                "artifact-world",
                "rolling-summary",
                new SessionRequestArtifactContextSnapshot(
                    "",
                    "## world-understanding\n\nworld",
                    ""
                )
            )
        ];

    private static ImmutableArray<SessionArtifactSetMember> CreateMembers(
        ImmutableArray<SessionRequestArtifactInput> inputs
    ) => [
        new(
            "autobiography",
            inputs[0].ArtifactId,
            inputs[0].ArtifactKind,
            new MemoryPackBlockPath(
                MemoryPackCarrier.Action,
                "autobiography"
            ),
            inputs[0].ContentSha256
        ),
        new(
            "world-understanding",
            inputs[1].ArtifactId,
            inputs[1].ArtifactKind,
            new MemoryPackBlockPath(
                MemoryPackCarrier.Observation,
                "world-understanding"
            ),
            inputs[1].ContentSha256
        )
    ];

    private static SessionRequestArtifactInput CreateArtifactInput(
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
        _ = SessionEventCodec.Decode(
            kind,
            frame.Payload,
            out int bodySchemaVersion
        );
        return new SessionSetupReference(
            address,
            bodySchemaVersion,
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
            out int bodySchemaVersion
        );
        return new SessionArtifactSetReference(
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
            _ = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int bodySchemaVersion
            );
            entries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }
        return SessionRawRangeHasher.Compute(
            rawStartExclusive,
            rawEndInclusive,
            entries
        );
    }

    private static EventJournal.EventJournal CreateJournal(string path) {
        EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            startPoint: null
        ).Unwrap();
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

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-offline-validator-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class NeverCalledCompletionClient : ICompletionClient {
        public string Name => "offline-validation-client";

        public string ApiSpecId => "offline-validation-api-v1";

        public int Calls { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            throw new Xunit.Sdk.XunitException(
                "Prepared failpoint must run before provider invocation."
            );
        }
    }
}

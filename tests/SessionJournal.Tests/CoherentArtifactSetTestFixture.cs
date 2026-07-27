using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
using Xunit;

namespace Atelia.SessionJournal.Tests;

internal static class CoherentArtifactSetTestFixture {
    internal const int ArtifactContextMessageCount = 2;

    internal static async ValueTask<ActivatedCoherentArtifactSet>
        ActivateAtCurrentHeadAsync(
            string journalPath,
            SessionJournalEngine engine,
            TestContextCandidateSource candidateSource,
            string fixtureId = "default",
            CancellationToken cancellationToken = default
        ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(candidateSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);

        SessionExecutionRecovery recovery =
            engine.ResolveExecutionTail(cancellationToken);
        EventAddress anchor = recovery.Head
            ?? throw new InvalidOperationException(
                "A coherent test fixture requires a non-empty SessionJournal."
            );
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                "A coherent test fixture can only be activated at an idle head."
            );
        }
        SessionGoverningSetup setup =
            engine.ResolveGoverningSetup(anchor, cancellationToken);

        var memoryPack = new MemoryPack();
        memoryPack.Observation.Add(
            "fixture.world-understanding",
            new MemoryPackBlock($"bounded world {fixtureId}")
        );
        memoryPack.Action.Add(
            "fixture.autobiography",
            new MemoryPackBlock($"bounded self {fixtureId}")
        );
        DerivedRecapStore store = DerivedRecapStore.Open(journalPath);
        DerivedRecapArtifact world = await store.WriteProducedAsync(
            CreateWriteRequest(
                "world-understanding",
                $"coherent-fixture-world-{fixtureId}",
                new MemoryPackBlockPath(
                    MemoryPackCarrier.Observation,
                    "fixture.world-understanding"
                ),
                anchor,
                setup,
                memoryPack
            ),
            cancellationToken
        ).ConfigureAwait(false);
        DerivedRecapArtifact autobiography =
            await store.WriteProducedAsync(
                CreateWriteRequest(
                    "autobiography",
                    $"coherent-fixture-autobiography-{fixtureId}",
                    new MemoryPackBlockPath(
                        MemoryPackCarrier.Action,
                        "fixture.autobiography"
                    ),
                    anchor,
                    setup,
                    memoryPack
                ),
                cancellationToken
            ).ConfigureAwait(false);
        EventAddress activation = await engine.CommitArtifactSetAsync(
            [
                new SessionArtifactSetMemberSelection(
                    "world-understanding",
                    world.ArtifactId
                ),
                new SessionArtifactSetMemberSelection(
                    "autobiography",
                    autobiography.ArtifactId
                )
            ],
            cancellationToken
        ).ConfigureAwait(false);
        SessionContextCandidate candidate = CreateCandidate(anchor, setup, world, autobiography);
        candidateSource.Candidate = candidate;
        return new ActivatedCoherentArtifactSet(
            anchor,
            activation,
            world,
            autobiography,
            candidate
        );
    }

    internal static SessionContextCandidate CreateCandidate(
        EventAddress anchor,
        SessionGoverningSetup setup,
        params DerivedRecapArtifact[] artifacts
    ) {
        return new SessionContextCandidate(
            anchor,
            new SessionContextAnchorSetupReferences(
                CreateSetupReference(
                    setup.RuntimeConfigSetupAddress,
                    SessionEventKind.RuntimeConfigSetup,
                    setup.RuntimeConfig
                ),
                CreateSetupReference(
                    setup.SystemPromptSetupAddress,
                    SessionEventKind.SystemPromptSetup,
                    new SystemPromptSetupBody(setup.SystemPrompt)
                )
            ),
            artifacts.Select(static artifact => {
                Assert.True(
                    artifact.MemoryPack.TryGetBlock(
                        artifact.Target,
                        out MemoryPackBlock block
                    )
                );
                return new SessionContextContribution(
                    artifact.Target,
                    block.Text,
                    SessionContextContributionHasher.CodecId,
                    SessionContextContributionHasher.ComputeSha256(block.Text),
                    artifact.SourceRawHead
                );
            }).ToImmutableArray()
        );
    }

    private static SessionContextSetupReference CreateSetupReference(
        EventAddress address,
        SessionEventKind kind,
        object body
    ) {
        byte[] payload = SessionEventCodec.Encode(kind, body);
        _ = SessionEventCodec.Decode(kind, payload, out int version);
        return new SessionContextSetupReference(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(payload)
        );
    }

    internal static ImmutableArray<IHistoryMessage> RawSuffix(
        CompletionRequest request
    ) {
        AssertArtifactPrefix(request);
        return [.. request.Context.Skip(ArtifactContextMessageCount)];
    }

    internal static void AssertArtifactPrefix(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        Assert.True(
            request.Context.Count >= ArtifactContextMessageCount,
            "Coherent request is missing the exact artifact context prefix."
        );
        var world = Assert.IsType<ObservationMessage>(request.Context[0]);
        Assert.Contains(
            "## fixture.world-understanding",
            world.Content,
            StringComparison.Ordinal
        );
        var autobiography =
            Assert.IsType<ActionMessage>(request.Context[1]);
        Assert.Contains(
            "## fixture.autobiography",
            autobiography.GetFlattenedText(),
            StringComparison.Ordinal
        );
    }

    private static DerivedRecapWriteRequest CreateWriteRequest(
        string artifactKind,
        string profileId,
        MemoryPackBlockPath target,
        EventAddress anchor,
        SessionGoverningSetup setup,
        MemoryPack memoryPack
    ) => new(
        ArtifactKind: artifactKind,
        ProfileId: profileId,
        Producer: "coherent-test-fixture",
        ProducerFingerprint: "coherent-test-fixture-v1",
        SourceRawHead: anchor,
        SourceStartExclusive: null,
        SourceEndInclusive: anchor,
        AnchorRawEvent: anchor,
        GoverningRuntimeConfigSetup:
            setup.RuntimeConfigSetupAddress,
        GoverningSystemPromptSetup:
            setup.SystemPromptSetupAddress,
        PreviousArtifact: null,
        Target: target,
        MemoryPack: memoryPack
    );
}

internal sealed record ActivatedCoherentArtifactSet(
    EventAddress CommonAnchor,
    EventAddress Activation,
    DerivedRecapArtifact WorldUnderstanding,
    DerivedRecapArtifact Autobiography,
    SessionContextCandidate Candidate
);

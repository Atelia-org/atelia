using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

/// <summary>
/// Provider-route fixture that creates a store-neutral candidate without writing a
/// DerivedRecap artifact.
/// </summary>
internal static class ContextCandidateTestFixture {
    internal static TestContextCandidateFixture CreateAtCurrentHead(
        SessionJournalEngine engine,
        string fixtureId = "default"
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);
        SessionExecutionRecovery recovery = engine.ResolveExecutionTail();
        EventAddress anchor = recovery.Head
            ?? throw new InvalidOperationException(
                "A context-candidate fixture requires a non-empty SessionJournal."
            );
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                "A context-candidate fixture requires an idle anchor."
            );
        }
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(anchor);
        string worldText = $"bounded world {fixtureId}";
        string selfText = $"bounded self {fixtureId}";
        SessionContextCandidate candidate = CreateCandidate(
            engine,
            anchor,
            setup,
            new SessionContextContribution(
                new MemoryPackBlockPath(
                    MemoryPackCarrier.Observation,
                    "fixture.world-understanding"
                ),
                worldText,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(worldText),
                anchor
            ),
            new SessionContextContribution(
                new MemoryPackBlockPath(
                    MemoryPackCarrier.Action,
                    "fixture.autobiography"
                ),
                selfText,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(selfText),
                anchor
            )
        );
        return new TestContextCandidateFixture(anchor, candidate);
    }

    internal static SessionContextCandidate CreateCandidate(
        SessionJournalEngine engine,
        EventAddress anchor,
        SessionGoverningSetup setup,
        params SessionContextContribution[] contributions
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(contributions);
        if (setup.Head != anchor) {
            throw new ArgumentException(
                "Candidate setup must be resolved at the requested anchor.",
                nameof(setup)
            );
        }
        return new SessionContextCandidate(
            anchor,
            new SessionContextAnchorSetupReferences(
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
            ),
            contributions
        );
    }

    internal static SessionContextContribution Contribution(
        MemoryPackCarrier carrier,
        string blockKey,
        string exactText,
        EventAddress sourceRawHead
    ) => new(
        new MemoryPackBlockPath(carrier, blockKey),
        exactText,
        SessionContextContributionHasher.CodecId,
        SessionContextContributionHasher.ComputeSha256(exactText),
        sourceRawHead
    );

    private static SessionContextSetupReference CreateSetupReference(
        SessionJournalEngine engine,
        EventAddress address,
        SessionEventKind kind
    ) {
        byte[] payload = engine.ReadPayloadBytes(address);
        _ = SessionEventCodec.Decode(kind, payload, out int schemaVersion);
        return new SessionContextSetupReference(
            address,
            schemaVersion,
            SessionRequestCanonicalizer.Sha256Hex(payload)
        );
    }
}

internal sealed record TestContextCandidateFixture(
    EventAddress Anchor,
    SessionContextCandidate Candidate
);

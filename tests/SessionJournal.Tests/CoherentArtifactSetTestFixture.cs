using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

/// <summary>
/// Historical test-fixture name retained only inside the test assembly. It now creates a
/// store-neutral provider candidate and writes no derived artifact.
/// </summary>
internal static class CoherentArtifactSetTestFixture {
    internal const int ArtifactContextMessageCount = 2;

    internal static ValueTask<ActivatedCoherentArtifactSet>
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
        cancellationToken.ThrowIfCancellationRequested();
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(
                engine,
                fixtureId
            );
        candidateSource.Candidate = fixture.Candidate;
        return ValueTask.FromResult(
            new ActivatedCoherentArtifactSet(
                fixture.Anchor,
                fixture.Candidate
            )
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
            "Coherent request is missing the exact derived context prefix."
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
}

internal sealed record ActivatedCoherentArtifactSet(
    EventAddress CommonAnchor,
    SessionContextCandidate Candidate
);

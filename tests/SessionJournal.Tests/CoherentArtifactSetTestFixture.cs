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
        return [.. request.PromptPrefix.SharedContextMessages.Skip(ArtifactContextMessageCount)];
    }

    internal static void AssertArtifactPrefix(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        Assert.True(
            request.PromptPrefix.SharedContextMessages.Length >= ArtifactContextMessageCount,
            "Coherent request is missing the exact derived context prefix."
        );
        var world = Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[0]);
        AssertRecapBlock(world.Content, "bounded world ");
        var autobiography =
            Assert.IsType<ActionMessage>(request.PromptPrefix.SharedContextMessages[1]);
        AssertRecapBlock(
            autobiography.GetFlattenedText(),
            "bounded self "
        );
    }

    private static void AssertRecapBlock(
        string? rendered,
        string expectedBodyPrefix
    ) {
        Assert.NotNull(rendered);
        const string InfoLineSuffix = "recap-block\n";
        int infoLineStart = rendered.IndexOf(
            InfoLineSuffix,
            StringComparison.Ordinal
        );
        Assert.True(
            infoLineStart >= 4,
            "Recap block is missing its minimum tilde fence."
        );
        string fence = rendered[..infoLineStart];
        Assert.All(fence, static character => Assert.Equal('~', character));
        int bodyStart = infoLineStart + InfoLineSuffix.Length;
        Assert.StartsWith(
            expectedBodyPrefix,
            rendered[bodyStart..],
            StringComparison.Ordinal
        );
        Assert.EndsWith(
            "\n" + fence,
            rendered,
            StringComparison.Ordinal
        );
    }
}

internal sealed record ActivatedCoherentArtifactSet(
    EventAddress CommonAnchor,
    SessionContextCandidate Candidate
);

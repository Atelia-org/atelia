using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapContextCandidateSourceTests {
    [Fact]
    public async Task AdapterResolvesRawSetupsAndMaterializesNeutralCandidate() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        await fixture.PublishAsync(
            lineage.CapturedHead,
            lineage.CurrentPrefix.HeadToOldest[2].Address,
            content: "adapter recap"
        );
        var source = new DerivedRecapContextCandidateSource(
            fixture.Store,
            fixture.Engine
        );

        SessionContextCandidateSelection selection =
            await source.SelectAsync(
                new SessionContextSelectionRequest(
                    lineage.CapturedHead,
                    0
                ),
                CancellationToken.None
            );
        SessionContextCandidateDescriptor descriptor =
            Assert.IsType<SessionContextCandidateDescriptor>(
                selection.Candidate
            );
        SessionContextCandidate candidate =
            await source.MaterializeAsync(
                descriptor,
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextCandidateSelectionStatus.Selected,
            selection.Status
        );
        Assert.Equal(
            lineage.CapturedHead,
            candidate.SetAdmissionAnchor
        );
        Assert.Equal(
            fixture.Engine.ResolveContextAnchorSetupReferences(
                lineage.CapturedHead
            ),
            candidate.AnchorSetups
        );
        Assert.Equal(
            "adapter recap",
            Assert.Single(candidate.Contributions).ExactText
        );
    }

    [Fact]
    public async Task AdapterRejectsStaleSelectionAndMaterialization() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        await fixture.PublishAsync(
            lineage.CapturedHead,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        var source = new DerivedRecapContextCandidateSource(
            fixture.Store,
            fixture.Engine
        );
        SessionContextCandidateSelection selection =
            await source.SelectAsync(
                new SessionContextSelectionRequest(
                    lineage.CapturedHead,
                    0
                ),
                CancellationToken.None
            );
        SessionContextCandidateDescriptor descriptor =
            selection.Candidate!;
        fixture.Engine.AppendObservation("new current head");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await source.MaterializeAsync(
                descriptor,
                CancellationToken.None
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await source.SelectAsync(
                new SessionContextSelectionRequest(
                    lineage.CapturedHead,
                    0
                ),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task AdapterMapsUnavailableAndInvalidWithoutFallback() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        await fixture.PublishAsync(
            lineage.CapturedHead,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(
                    lineage.CapturedHead
                ),
                "publication.json"
            ),
            "{}"
        );
        var source = new DerivedRecapContextCandidateSource(
            fixture.Store,
            fixture.Engine
        );

        SessionContextCandidateSelection invalid =
            await source.SelectAsync(
                new SessionContextSelectionRequest(
                    lineage.CapturedHead,
                    0
                ),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextCandidateSelectionStatus
                .ExactPublishedSetInvalid,
            invalid.Status
        );
        Assert.Null(invalid.Candidate);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Detail));
    }

    [Fact]
    public async Task AdapterMapsBeyondPrefixWithDeterministicEvidence() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 257);
        DerivedRecapLineageView lineage = fixture.Lineage();
        var source = new DerivedRecapContextCandidateSource(
            fixture.Store,
            fixture.Engine
        );

        SessionContextCandidateSelection selection =
            await source.SelectAsync(
                new SessionContextSelectionRequest(
                    lineage.CapturedHead,
                    0
                ),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextCandidateSelectionStatus.BeyondPrefix,
            selection.Status
        );
        Assert.Null(selection.Candidate);
        Assert.Contains("HeaderCount=513", selection.Detail);
        selection.ValidateShape();
    }
}

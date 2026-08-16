using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Getter;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.Tests;

public sealed class GetterEvidenceRecordContractTests {
    [Fact]
    public void ContextProvenanceRetainsRecordValueSemantics() {
        var first = new RecapGridContextProvenance(
            RecapGridProvenanceStatus.Verified,
            RecapGridProvenanceStatus.NotSatisfied,
            RecapGridProvenanceStatus.Incomplete,
            1,
            2,
            3
        );
        var second = new RecapGridContextProvenance(
            RecapGridProvenanceStatus.Verified,
            RecapGridProvenanceStatus.NotSatisfied,
            RecapGridProvenanceStatus.Incomplete,
            1,
            2,
            3
        );
        RecapGridContextProvenance clone = first with { };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotSame(first, clone);
        Assert.Equal(first, clone);
        Assert.Equal(
            "RecapGridContextProvenance { MembershipComplete = Verified, "
                + "PriorInputAligned = NotSatisfied, FullRebuildChain = "
                + "Incomplete, ExaminedRows = 1, ExaminedCells = 2, "
                + "ExaminedCanonicalUtf8Bytes = 3 }",
            first.ToString()
        );
    }

    [Fact]
    public void ReserveBootstrapEvidenceRetainsRecordValueSemantics() {
        var metrics = new HistoryRecentReserveAnchorMetrics(4, 5, 6, 7);
        var first = new RecapGridReserveBootstrapEvidence(
            null!,
            null!,
            null!,
            null!,
            new HistoryLoadUnit(1),
            new HistoryLoadUnit(2),
            3,
            metrics
        );
        var second = new RecapGridReserveBootstrapEvidence(
            null!,
            null!,
            null!,
            null!,
            new HistoryLoadUnit(1),
            new HistoryLoadUnit(2),
            3,
            new HistoryRecentReserveAnchorMetrics(4, 5, 6, 7)
        );
        RecapGridReserveBootstrapEvidence clone = first with { };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotSame(first, clone);
        Assert.Equal(first, clone);
        Assert.Equal(
            "RecapGridReserveBootstrapEvidence { TimelineHead = , "
                + "CadenceHead = , ControlHead = , StoreIdentity = , "
                + "RetainedHistoryLoad = HistoryLoadUnit { Value = 1 }, "
                + "RequiredHistoryLoad = HistoryLoadUnit { Value = 2 }, "
                + "VerifiedRows = 3, Metrics = "
                + "HistoryRecentReserveAnchorMetrics { "
                + "ExaminedTimelineRows = 4, ExaminedRawEvents = 5, "
                + "ExaminedHistoryUnits = 6, "
                + "ExaminedRenderedUtf8Bytes = 7 } }",
            first.ToString()
        );
    }
}

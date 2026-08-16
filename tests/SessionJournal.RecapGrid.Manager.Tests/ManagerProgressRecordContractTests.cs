using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed class ManagerProgressRecordContractTests {
    [Fact]
    public void AuthorityRetainsRecordValueSemantics() {
        var first = new RecapGridBuildProgressAuthority(
            null!,
            null!,
            null!,
            default,
            default,
            default
        );
        var second = new RecapGridBuildProgressAuthority(
            null!,
            null!,
            null!,
            default,
            default,
            default
        );
        RecapGridBuildProgressAuthority clone = first with { };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotSame(first, clone);
        Assert.Equal(first, clone);
        Assert.Equal(
            "RecapGridBuildProgressAuthority { TimelineHead = , "
                + "ControlHead = , StoreIdentity = , RecipeDigest = , "
                + "ThroughRowId = , ThroughDescriptorDigest =  }",
            first.ToString()
        );
    }

    [Fact]
    public void MissingAssignmentRetainsRecordValueSemantics() {
        var first = new RecapGridMissingAssignmentProgress(
            7,
            default,
            default,
            default,
            default
        );
        var second = new RecapGridMissingAssignmentProgress(
            7,
            default,
            default,
            default,
            default
        );
        RecapGridMissingAssignmentProgress clone = first with { };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotSame(first, clone);
        Assert.Equal(first, clone);
        Assert.Equal(
            "RecapGridMissingAssignmentProgress { Ordinal = 7, RowId = , "
                + "RecipeDigest = , LogicalColumnId = , "
                + "EvaluationKey =  }",
            first.ToString()
        );
    }

    [Fact]
    public void RecipeRowWorkRetainsRecordValueSemantics() {
        var first = new RecapGridRecipeRowWork(
            default,
            default,
            true
        );
        var second = new RecapGridRecipeRowWork(
            default,
            default,
            true
        );
        RecapGridRecipeRowWork clone = first with { };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotSame(first, clone);
        Assert.Equal(first, clone);
        Assert.Equal(
            "RecapGridRecipeRowWork { RowId = , RecipeDigest = , "
                + "IsOverlayBootstrap = True }",
            first.ToString()
        );
    }

    [Fact]
    public void ProgressMetricsRemainZeroByDefaultAndFriendWritableWith() {
        var original = new RecapGridBuildProgressResult.Disposed();
        var expected = new RecapGridBuildProgressMetrics(1, 2, 3, 4);

        RecapGridBuildProgressResult updated = original with {
            Metrics = expected
        };

        Assert.Equal(default, original.Metrics);
        Assert.Equal(expected, updated.Metrics);
        Assert.Equal(default(RecapGridBuildProgressMetrics),
            new RecapGridBuildProgressResult.Disposed().Metrics);
    }
}

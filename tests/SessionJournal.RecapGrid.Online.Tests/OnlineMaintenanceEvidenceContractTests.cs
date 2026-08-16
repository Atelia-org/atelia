using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Online.Tests;

public sealed class OnlineMaintenanceEvidenceContractTests {
    [Fact]
    public void RecordSemanticsRemainValueBased() {
        RecapGridOnlineMaintenanceEvidence first = Evidence();
        RecapGridOnlineMaintenanceEvidence second = Evidence();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(
            "RecapGridOnlineMaintenanceEvidence { Passes = 1, "
                + "EntryDebt = True, TimelineRowsCommitted = 2, "
                + "LastAttemptedRecipeRow = , LastAttemptedAuthority = , "
                + "RecipeRowSteps = 3, RowViewsCommitted = 4, "
                + "CellsCommitted = 5, NewCalls = 6, NextRecipeRow = , "
                + "NextAuthority = , ContinuationKind = GridDebtRemaining }",
            first.ToString()
        );
    }

    [Fact]
    public void OwnerCanWithEachMutableContinuationProperty() {
        RecapGridOnlineMaintenanceEvidence evidence = Evidence();
        var coordinate = new RecapGridRecipeRowCoordinate(
            default(HistoryRowId),
            default(GridBuildRecipeDigest)
        );
        var authority = new RecapGridBuildProgressAuthority(
            null!,
            null!,
            null!,
            default,
            default,
            default
        );

        RecapGridOnlineMaintenanceEvidence withRecipeRow = evidence with {
            NextRecipeRow = coordinate
        };
        RecapGridOnlineMaintenanceEvidence withAuthority = evidence with {
            NextAuthority = authority
        };
        RecapGridOnlineMaintenanceEvidence withKind = evidence with {
            ContinuationKind =
                RecapGridOnlineContinuationKind.CatchUpBudgetExhausted
        };

        Assert.Same(coordinate, withRecipeRow.NextRecipeRow);
        Assert.Same(authority, withAuthority.NextAuthority);
        Assert.Equal(
            RecapGridOnlineContinuationKind.CatchUpBudgetExhausted,
            withKind.ContinuationKind
        );
        Assert.Null(evidence.NextRecipeRow);
        Assert.Null(evidence.NextAuthority);
        Assert.Equal(
            RecapGridOnlineContinuationKind.GridDebtRemaining,
            evidence.ContinuationKind
        );
    }

    private static RecapGridOnlineMaintenanceEvidence Evidence() => new(
        1,
        true,
        2,
        null,
        null,
        3,
        4,
        5,
        6,
        null,
        null,
        RecapGridOnlineContinuationKind.GridDebtRemaining
    );
}

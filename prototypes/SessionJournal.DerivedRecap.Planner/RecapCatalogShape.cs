using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// The persisted catalog shape shared by active configuration and a frozen
/// plan. Maintainer/profile identity and plan execution details are
/// deliberately outside this projection.
/// </summary>
public sealed record RecapCatalogShapeEntry(
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    int MaxContentUtf8Bytes
);

public sealed record RecapCatalogShapeComparison(
    bool IsExactMatch,
    int? MismatchIndex,
    string Detail
);

public static class RecapCatalogShape {
    public static IReadOnlyList<RecapCatalogShapeEntry> ProjectActive(
        IReadOnlyList<RecapBlockCatalogEntry> orderedCatalog
    ) {
        ArgumentNullException.ThrowIfNull(orderedCatalog);
        return Array.AsReadOnly([
            .. orderedCatalog.Select(static entry => {
                ArgumentNullException.ThrowIfNull(entry);
                return new RecapCatalogShapeEntry(
                    entry.RecapBlockId,
                    entry.Target,
                    entry.MaxContentUtf8Bytes
                );
            })
        ]);
    }

    public static IReadOnlyList<RecapCatalogShapeEntry> ProjectFrozen(
        IReadOnlyList<RecapBlockPlan> orderedBlocks
    ) {
        ArgumentNullException.ThrowIfNull(orderedBlocks);
        return Array.AsReadOnly([
            .. orderedBlocks.Select(static block => {
                ArgumentNullException.ThrowIfNull(block);
                return new RecapCatalogShapeEntry(
                    block.RecapBlockId,
                    block.Target,
                    block.MaxContentUtf8Bytes
                );
            })
        ]);
    }

    public static RecapCatalogShapeComparison Compare(
        IReadOnlyList<RecapCatalogShapeEntry> expected,
        IReadOnlyList<RecapCatalogShapeEntry> observed
    ) {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        int commonCount = Math.Min(expected.Count, observed.Count);
        for (int index = 0; index < commonCount; index++) {
            ArgumentNullException.ThrowIfNull(expected[index]);
            ArgumentNullException.ThrowIfNull(observed[index]);
            if (expected[index] != observed[index]) {
                return new RecapCatalogShapeComparison(
                    IsExactMatch: false,
                    MismatchIndex: index,
                    $"Catalog entry at index {index} differs. "
                    + $"Expected '{Format(expected[index])}', "
                    + $"observed '{Format(observed[index])}'."
                );
            }
        }
        if (expected.Count != observed.Count) {
            return new RecapCatalogShapeComparison(
                IsExactMatch: false,
                MismatchIndex: commonCount,
                $"Catalog length differs. Expected {expected.Count}, "
                + $"observed {observed.Count}."
            );
        }
        return new RecapCatalogShapeComparison(
            IsExactMatch: true,
            MismatchIndex: null,
            "Catalog shapes match exactly."
        );
    }

    private static string Format(RecapCatalogShapeEntry entry) =>
        $"{entry.RecapBlockId}|{entry.Target}|"
        + $"{entry.MaxContentUtf8Bytes}";
}

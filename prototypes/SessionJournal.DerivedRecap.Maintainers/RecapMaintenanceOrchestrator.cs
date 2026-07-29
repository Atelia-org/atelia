namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public sealed record RecapMaintenanceBatchResult(
    IReadOnlyList<RecapBlockMaintenanceResult> Results,
    ContextHeaderPack UpdatedContextHeaderPack
);

public static class RecapMaintenanceOrchestrator {
    public static async Task<RecapMaintenanceBatchResult> RunAsync(
        ContextHeaderPack contextHeaderPack,
        RecentHistorySlice recentHistory,
        IReadOnlyList<IRecapBlockMaintainer> maintainers,
        CancellationToken ct = default
    ) {
        ArgumentNullException.ThrowIfNull(contextHeaderPack);
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(maintainers);

        ValidateMaintainers(maintainers);

        var tasks =
            new Task<RecapBlockMaintenanceResult>[maintainers.Count];
        for (int i = 0; i < maintainers.Count; i++) {
            var maintainer = maintainers[i];
            var oldBlock =
                contextHeaderPack.TryGetBlock(
                    maintainer.Target,
                    out var found
                )
                    ? found
                    : new ContextHeaderBlock(string.Empty);
            var request =
                new RecapBlockMaintenanceRequest(recentHistory, oldBlock);
            tasks[i] = maintainer.MaintainAsync(request, ct).AsTask();
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var draft = new ContextHeaderPackDraft(contextHeaderPack);
        for (int i = 0; i < results.Length; i++) {
            if (!string.Equals(
                    results[i].MaintainerId,
                    maintainers[i].Id,
                    StringComparison.Ordinal
                )) {
                throw new InvalidOperationException(
                    $"Recap maintainer '{maintainers[i].Id}' returned mismatched id '{results[i].MaintainerId}'."
                );
            }
            if (!Equals(results[i].Target, maintainers[i].Target)) {
                throw new InvalidOperationException(
                    $"Recap maintainer '{maintainers[i].Id}' returned a mismatched target."
                );
            }
            draft.UpsertBlock(
                results[i].Target,
                results[i].NewBlock.Text
            );
        }

        return new RecapMaintenanceBatchResult(results, draft.Build());
    }

    public static void ValidateMaintainers(
        IReadOnlyList<IRecapBlockMaintainer> maintainers
    ) {
        if (maintainers.Count == 0) {
            throw new ArgumentException(
                "At least one recap maintainer is required.",
                nameof(maintainers)
            );
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<ContextHeaderBlockPath>();
        for (int i = 0; i < maintainers.Count; i++) {
            var maintainer = maintainers[i]
                ?? throw new ArgumentException(
                    "Recap maintainer cannot be null.",
                    nameof(maintainers)
                );
            if (string.IsNullOrWhiteSpace(maintainer.Id)) {
                throw new ArgumentException(
                    "Recap maintainer id cannot be empty.",
                    nameof(maintainers)
                );
            }
            if (!ids.Add(maintainer.Id)) {
                throw new ArgumentException(
                    $"Duplicate recap maintainer id: {maintainer.Id}",
                    nameof(maintainers)
                );
            }
            if (!targets.Add(maintainer.Target)) {
                throw new ArgumentException(
                    $"Duplicate recap maintainer target: {maintainer.Target.Carrier}/{maintainer.Target.BlockKey}",
                    nameof(maintainers)
                );
            }
        }
    }
}

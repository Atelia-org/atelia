using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

internal static class RecentHistoryRules {
    internal static void ValidateAppendOrder(
        IReadOnlyList<HistoryEntry> recentHistory,
        HistoryEntry entry
    ) {
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(entry);

        if (recentHistory.Count == 0) {
            if (!entry.IsObservationLike) {
                throw new InvalidOperationException("The first history entry must be an observation-like entry.");
            }

            return;
        }

        var last = recentHistory[recentHistory.Count - 1];
        if (IsLegalHistoryTransition(last, entry)) {
            return;
        }

        throw new InvalidOperationException($"Illegal history transition. Last={last.Kind}, Next={entry.Kind}");
    }

    internal static int FindLatestActionIndex(IReadOnlyList<HistoryEntry> recentHistory) {
        ArgumentNullException.ThrowIfNull(recentHistory);

        for (var index = recentHistory.Count - 1; index >= 0; index--) {
            if (recentHistory[index] is ActionEntry) {
                return index;
            }
        }

        return -1;
    }

    internal static bool HasPendingActionContinuation(IReadOnlyList<HistoryEntry> recentHistory) {
        ArgumentNullException.ThrowIfNull(recentHistory);
        return recentHistory.Count > 0 && recentHistory[^1] is InjectionEntry;
    }

    internal static int FindIndexBySerial(IReadOnlyList<HistoryEntry> recentHistory, ulong serial) {
        ArgumentNullException.ThrowIfNull(recentHistory);

        for (var index = 0; index < recentHistory.Count; index++) {
            if (recentHistory[index].Serial == serial) {
                return index;
            }
        }

        return -1;
    }

    internal static void ValidateTailRewrite(
        IReadOnlyList<HistoryEntry> recentHistory,
        int anchorIndex,
        IReadOnlyList<HistoryEntry> replacementEntries
    ) {
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(replacementEntries);

        if ((uint)anchorIndex >= (uint)recentHistory.Count) {
            throw new ArgumentOutOfRangeException(nameof(anchorIndex), anchorIndex, "anchorIndex must identify an existing recent-history entry.");
        }

        var previous = recentHistory[anchorIndex];
        for (var index = 0; index < replacementEntries.Count; index++) {
            var replacementEntry = replacementEntries[index]
                ?? throw new InvalidOperationException("replacementEntries must not contain null entries.");

            if (!IsLegalHistoryTransition(previous, replacementEntry)) {
                throw new InvalidOperationException(
                    $"Illegal tail rewrite transition. Previous={previous.Kind}, Next={replacementEntry.Kind}"
                );
            }

            previous = replacementEntry;
        }
    }

    internal static ActionBlockKind ResolveInjectedBlockKind(
        IReadOnlyList<HistoryEntry> recentHistory,
        ActionInjectionRequest request
    ) {
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(request);

        return request.Mode switch {
            InjectedActionContentMode.Text => ActionBlockKind.Text,
            InjectedActionContentMode.Thinking => ActionBlockKind.Thinking,
            InjectedActionContentMode.MatchRecentActionTail => ResolveBlockKindFromRecentActionTail(recentHistory),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unsupported injected action content mode.")
        };
    }

    internal static void EnsureActionAcceptsInjection(ActionEntry action, string context) {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Message.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Cannot {context} because the trailing ActionEntry already contains tool calls. Injection only supports assistant content before tool execution begins."
            );
        }
    }

    private static bool IsLegalHistoryTransition(HistoryEntry last, HistoryEntry next) {
        if (last.IsObservationLike) {
            return next is ActionEntry or InjectionEntry;
        }

        return (last, next) switch {
            (ActionEntry, ObservationEntry) => true,
            (ActionEntry, RecapEntry) => true,
            (ActionEntry, InjectionEntry) => true,
            (InjectionEntry, ActionEntry) => true,
            (InjectionEntry, InjectionEntry) => true,
            _ => false
        };
    }

    private static ActionBlockKind ResolveBlockKindFromRecentActionTail(IReadOnlyList<HistoryEntry> recentHistory) {
        var referenceActionIndex = FindLatestActionIndex(recentHistory);
        if (referenceActionIndex < 0) {
            throw new InvalidOperationException("Cannot inject action content because no prior ActionEntry exists in history.");
        }

        var referenceAction = (ActionEntry)recentHistory[referenceActionIndex];
        for (var index = referenceAction.Message.Blocks.Count - 1; index >= 0; index--) {
            switch (referenceAction.Message.Blocks[index]) {
                case ActionBlock.ReasoningBlock:
                    return ActionBlockKind.Thinking;
                case ActionBlock.Text:
                    return ActionBlockKind.Text;
            }
        }

        return ActionBlockKind.Text;
    }
}

using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    public bool TryRemoveLatestCompletedTurn(out RemovedChatTurnResult? removedTurn) {
        ThrowIfDisposed();
        DebugUtil.Info(
            "ChatSession.Rewind",
            $"TryRemoveLatestCompletedTurn start: {GetDebugStateSummary()}",
            eventKind: DebugEventKind.Start
        );

        int latestObservationIndex = FindLatestUserObservationIndex();
        if (latestObservationIndex < 0) {
            removedTurn = null;
            DebugUtil.Info("ChatSession.Rewind", "TryRemoveLatestCompletedTurn skipped: no observation found.", eventKind: DebugEventKind.Skip);
            return false;
        }

        if (!_messages.TryGetAt<DurableDict<string>>(latestObservationIndex, out var record) || record is null) {
            removedTurn = null;
            DebugUtil.Warning("ChatSession.Rewind", $"TryRemoveLatestCompletedTurn failed: latest observation record missing at index {latestObservationIndex}.");
            return false;
        }

        var observation = MessageRecord.ToHistoryMessage(record) as ObservationMessage;
        if (observation is null || !LatestTurnContainsAssistantAction(latestObservationIndex)) {
            removedTurn = null;
            DebugUtil.Info(
                "ChatSession.Rewind",
                $"TryRemoveLatestCompletedTurn skipped: observation={observation is not null}, hasAssistantAction={LatestTurnContainsAssistantAction(latestObservationIndex)}.",
                eventKind: DebugEventKind.Skip
            );
            return false;
        }

        int removedCount = _messages.Count - latestObservationIndex;
        for (int i = 0; i < removedCount; i++) {
            _messages.PopBack<DurableObject>(out _);
        }

        Commit();
        removedTurn = new RemovedChatTurnResult(observation.Content ?? string.Empty, removedCount);
        DebugUtil.Info(
            "ChatSession.Rewind",
            $"TryRemoveLatestCompletedTurn success: removedCount={removedCount}, userPreview={Preview(observation.Content)}, {GetDebugStateSummary()}",
            eventKind: DebugEventKind.Success
        );
        return true;
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<null>"; }
        string normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }

    private int FindLatestUserObservationIndex() {
        for (int i = _messages.Count - 1; i >= 0; i--) {
            if (!_messages.TryGetAt<DurableDict<string>>(i, out var record) || record is null) { continue; }

            if (!record.TryGet<string>(MessageRecord.KeyKind, out var kind)) { continue; }

            if (kind == MessageRecord.KindObservation) { return i; }
        }

        return -1;
    }

    private bool LatestTurnContainsAssistantAction(int latestObservationIndex) {
        for (int i = latestObservationIndex + 1; i < _messages.Count; i++) {
            if (!_messages.TryGetAt<DurableDict<string>>(i, out var record) || record is null) { continue; }

            if (MessageRecord.ToHistoryMessage(record) is ActionMessage) { return true; }
        }

        return false;
    }
}

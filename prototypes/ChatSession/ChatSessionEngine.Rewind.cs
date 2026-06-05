using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    public bool TryRemoveLatestCompletedTurn(out RemovedChatTurnResult? removedTurn) {
        ThrowIfDisposed();

        int latestObservationIndex = FindLatestUserObservationIndex();
        if (latestObservationIndex < 0) {
            removedTurn = null;
            return false;
        }

        if (!_messages.TryGetAt<DurableDict<string>>(latestObservationIndex, out var record) || record is null) {
            removedTurn = null;
            return false;
        }

        var observation = MessageRecord.ToHistoryMessage(record) as ObservationMessage;
        if (observation is null || !LatestTurnContainsAssistantOutput(latestObservationIndex)) {
            removedTurn = null;
            return false;
        }

        int removedCount = _messages.Count - latestObservationIndex;
        for (int i = 0; i < removedCount; i++) {
            _messages.PopBack<DurableObject>(out _);
        }

        Commit();
        removedTurn = new RemovedChatTurnResult(observation.Content ?? string.Empty, removedCount);
        return true;
    }

    private int FindLatestUserObservationIndex() {
        for (int i = _messages.Count - 1; i >= 0; i--) {
            if (!_messages.TryGetAt<DurableDict<string>>(i, out var record) || record is null) {
                continue;
            }

            if (!record.TryGet<string>(MessageRecord.KeyKind, out var kind)) {
                continue;
            }

            if (kind == MessageRecord.KindObservation) {
                return i;
            }
        }

        return -1;
    }

    private bool LatestTurnContainsAssistantOutput(int latestObservationIndex) {
        for (int i = latestObservationIndex + 1; i < _messages.Count; i++) {
            if (!_messages.TryGetAt<DurableDict<string>>(i, out var record) || record is null) {
                continue;
            }

            if (MessageRecord.ToHistoryMessage(record) is ActionMessage action && HasAssistantContent(action)) {
                return true;
            }
        }

        return false;
    }

    private static bool HasAssistantContent(ActionMessage action) {
        for (int i = 0; i < action.Blocks.Count; i++) {
            if (action.Blocks[i] is ActionBlock.Text or ActionBlock.TextReasoningBlock) {
                return true;
            }
        }

        return false;
    }
}

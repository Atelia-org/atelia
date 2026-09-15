using System.Text.Json;

namespace Atelia.Galatea.Server;

internal static class GalateaDurableNoticeContent {
    internal static PlayerTurnNotice Project(GalateaReplyNoticeSnapshot notice) {
        if (notice.NoticeFormat == "legacy-text") {
            return notice.Kind switch {
                GalateaReplyNoticeKind.Reply => PlayerTurnNotice.Reply.FromLegacyDurable(notice.Body,
                    dispatchId: notice.DispatchId, threadId: notice.ThreadId, turnId: notice.TurnId, noticeId: notice.NoticeId),
                GalateaReplyNoticeKind.DeliveryFailure => PlayerTurnNotice.DeliveryFailure.FromLegacyDurable(notice.Body,
                    dispatchId: notice.DispatchId, threadId: notice.ThreadId, turnId: notice.TurnId, noticeId: notice.NoticeId,
                    stage: notice.Stage, code: notice.Code),
                _ => throw new InvalidDataException("Unknown legacy notice kind.")
            };
        }
        if (notice.NoticeFormat != "semantic-notice-v1" || notice.Sender is null) {
            throw new InvalidDataException("Unknown or incomplete notice content.");
        }
        return notice.Kind switch {
            GalateaReplyNoticeKind.Reply => new PlayerTurnNotice.Reply(notice.Body, notice.Sender, notice.DispatchId,
                notice.ThreadId, notice.TurnId, notice.NoticeId),
            GalateaReplyNoticeKind.DeliveryFailure => new PlayerTurnNotice.DeliveryFailure(
                notice.Code ?? throw new InvalidDataException("Failure code is missing."), notice.Detail,
                notice.Sender, notice.DispatchId, notice.Stage, notice.ThreadId, notice.TurnId, notice.NoticeId),
            _ => throw new InvalidDataException("Unknown semantic notice kind.")
        };
    }

    internal static bool SameContent(PlayerTurnNotice actual, PlayerTurnNotice expected, bool allowLegacyMissingProvenance) {
        if (actual.GetType() != expected.GetType()) { return false; }
        if (allowLegacyMissingProvenance && (actual is PlayerTurnNotice.Reply { Sender: null } && expected is PlayerTurnNotice.Reply { Sender: null }
            || actual is PlayerTurnNotice.DeliveryFailure { Sender: null } && expected is PlayerTurnNotice.DeliveryFailure { Sender: null })) {
            return string.Equals(actual.Body, expected.Body, StringComparison.Ordinal);
        }
        return JsonSerializer.Serialize(GalateaObservationContent.NoticeJson(actual))
            == JsonSerializer.Serialize(GalateaObservationContent.NoticeJson(expected));
    }
}

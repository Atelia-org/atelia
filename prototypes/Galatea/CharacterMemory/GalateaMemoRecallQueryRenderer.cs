using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.Diagnostics;
using Atelia.Galatea.Prompts;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class GalateaMemoRecallQueryRenderer {
    internal const string SchemaId =
        "atelia.galatea.memo-recall-context.v1";
    internal const string RetrievalGoal =
        "memories materially useful for the character's next narrative action";
    internal const string ReplyKind = "reply";
    internal const string DeliveryFailureKind = "delivery-failure";

    private const string DebugCategory = "Galatea.MemoRecall";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    internal static string Render(
        GalateaCharacterName characterName,
        PlayerTurnObservation currentObservation,
        GalateaPlayerTurnRecallContext context
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        ArgumentNullException.ThrowIfNull(currentObservation);
        ArgumentNullException.ThrowIfNull(context);
        if (currentObservation.Recalls.Count != 0) {
            throw new ArgumentException(
                "Memo recall query requires a preliminary Observation without recalls.",
                nameof(currentObservation)
            );
        }
        DateTimeOffset timestamp = currentObservation
            .ExternalLocalTimestamp
            ?? throw new ArgumentException(
                "Memo recall query requires the sampled Observation timestamp.",
                nameof(currentObservation)
            );

        PlayerTurnNotice[] externalNotices = currentObservation.Notices
            .Where(static notice => notice is PlayerTurnNotice.Reply
                or PlayerTurnNotice.DeliveryFailure)
            .ToArray();
        int includedNoticeCount = 0;
        byte[] rendered = RenderUtf8(
            characterName.Value,
            timestamp,
            currentObservation.PlayerText,
            externalNotices,
            includedNoticeCount,
            recentVisibleAction: null
        );
        RequireWithinHardLimit(rendered);

        while (includedNoticeCount < externalNotices.Length) {
            byte[] candidate = RenderUtf8(
                characterName.Value,
                timestamp,
                currentObservation.PlayerText,
                externalNotices,
                includedNoticeCount + 1,
                recentVisibleAction: null
            );
            if (candidate.Length
                    > GalateaMemoRecallMvpPolicy.MaximumQueryUtf8Bytes) {
                break;
            }
            rendered = candidate;
            includedNoticeCount++;
        }

        int omittedNoticeCount = externalNotices.Length
            - includedNoticeCount;
        long omittedNoticeUtf8Bytes = externalNotices
            .Skip(includedNoticeCount)
            .Sum(static notice => (long)GalateaBoundedJson.StrictUtf8
                .GetByteCount(notice.Body));

        GalateaRecentVisibleAction? latestAction = context
            .RecentVisibleActions.LastOrDefault();
        int omittedActionCount = Math.Max(
            0,
            context.RecentVisibleActions.Count
                - GalateaMemoRecallMvpPolicy
                    .MaximumRecentVisibleActionCount
        );
        long omittedActionUtf8Bytes = context.RecentVisibleActions
            .Take(omittedActionCount)
            .Sum(static action => (long)GalateaBoundedJson.StrictUtf8
                .GetByteCount(action.Text));
        if (latestAction is not null) {
            int actionUtf8Bytes = GalateaBoundedJson.StrictUtf8.GetByteCount(
                latestAction.Text
            );
            byte[] candidate = RenderUtf8(
                characterName.Value,
                timestamp,
                currentObservation.PlayerText,
                externalNotices,
                includedNoticeCount,
                latestAction.Text
            );
            if (candidate.Length
                    <= GalateaMemoRecallMvpPolicy
                        .MaximumQueryUtf8Bytes) {
                rendered = candidate;
            }
            else {
                omittedActionCount++;
                omittedActionUtf8Bytes += actionUtf8Bytes;
            }
        }

        if (omittedNoticeCount > 0 || omittedActionCount > 0) {
            DebugUtil.Trace(
                DebugCategory,
                "Memo recall query omitted optional evidence: "
                    + $"notices={omittedNoticeCount}, "
                    + $"noticeUtf8Bytes={omittedNoticeUtf8Bytes}, "
                    + $"actions={omittedActionCount}, "
                    + $"actionUtf8Bytes={omittedActionUtf8Bytes}."
            );
        }
        return GalateaBoundedJson.StrictUtf8.GetString(rendered);
    }

    private static byte[] RenderUtf8(
        string characterName,
        DateTimeOffset timestamp,
        string playerText,
        IReadOnlyList<PlayerTurnNotice> notices,
        int includedNoticeCount,
        string? recentVisibleAction
    ) {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new() {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })) {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaId);
            writer.WriteString("characterName", characterName);
            writer.WriteString("retrievalGoal", RetrievalGoal);
            writer.WriteStartObject("currentTurn");
            writer.WriteString(
                "externalLocalTimestamp",
                timestamp.ToString(
                    TimestampFormat,
                    CultureInfo.InvariantCulture
                )
            );
            writer.WriteString("playerText", playerText);
            writer.WriteStartArray("externalNotices");
            for (int index = 0; index < includedNoticeCount; index++) {
                PlayerTurnNotice notice = notices[index];
                writer.WriteStartObject();
                writer.WriteString("kind", notice switch {
                    PlayerTurnNotice.Reply => ReplyKind,
                    PlayerTurnNotice.DeliveryFailure =>
                        DeliveryFailureKind,
                    _ => throw new InvalidOperationException(
                        "Unsupported external notice kind."
                    )
                });
                writer.WriteString("text", notice.Body);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("recentVisibleActions");
            if (recentVisibleAction is not null) {
                writer.WriteStartObject();
                writer.WriteNumber("ordinalFromNewest", 0);
                writer.WriteString("text", recentVisibleAction);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    private static void RequireWithinHardLimit(byte[] rendered) {
        if (rendered.Length
                > GalateaMemoRecallMvpPolicy.MaximumQueryUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(rendered),
                $"Required Memo recall query exceeds {MemoPodLimits.MaximumRecallQueryUtf8Bytes} UTF-8 bytes."
            );
        }
    }
}

using System.Text;
using System.Text.Json;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaMemoRecallContractsTests {
    private static readonly DateTimeOffset Timestamp = new(
        2026,
        9,
        2,
        14,
        20,
        0,
        TimeSpan.FromHours(8)
    );

    [Fact]
    public void QueryRenderer_HasCanonicalGoldenJsonAndExactEvidence() {
        var observation = new PlayerTurnObservation(
            "继续\"追问\\她\n",
            Timestamp,
            [
                new PlayerTurnNotice.Reply("<线索>&\""),
                new PlayerTurnNotice.DeliveryFailure("失败\n原因"),
                new PlayerTurnNotice.NoteSaveReceipt("Note 已保存")
            ]
        );
        GalateaPlayerTurnRecallContext context = Context([
            new GalateaRecentVisibleAction("她说：\"蓝门\"\\旧城\n")
        ]);

        string rendered = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("伽拉忒亚"),
            observation,
            context
        );

        const string Expected =
            "{\"schema\":\"atelia.galatea.memo-recall-context.v1\","
            + "\"characterName\":\"伽拉忒亚\","
            + "\"retrievalGoal\":\"memories materially useful for the character's next narrative action\","
            + "\"currentTurn\":{"
            + "\"externalLocalTimestamp\":\"2026-09-02T14:20:00+08:00\","
            + "\"playerText\":\"继续\\\"追问\\\\她\\n\","
            + "\"externalNotices\":["
            + "{\"kind\":\"reply\",\"text\":\"<线索>&\\\"\"},"
            + "{\"kind\":\"delivery-failure\",\"text\":\"失败\\n原因\"}]},"
            + "\"recentVisibleActions\":[{"
            + "\"ordinalFromNewest\":0,"
            + "\"text\":\"她说：\\\"蓝门\\\"\\\\旧城\\n\"}]}";
        Assert.Equal(Expected, rendered);
        Assert.DoesNotContain("Note 已保存", rendered,
            StringComparison.Ordinal);
        Assert.Equal(
            rendered,
            GalateaMemoRecallQueryRenderer.Render(
                new GalateaCharacterName("伽拉忒亚"),
                observation,
                context
            )
        );
    }

    [Fact]
    public void QueryRenderer_UsesWholeNoticePrefixThenLatestAction() {
        string largeNotice = new('n', 250 * 1024);
        var observation = new PlayerTurnObservation(
            new string('p', 32 * 1024),
            Timestamp,
            [
                new PlayerTurnNotice.Reply(largeNotice),
                new PlayerTurnNotice.Reply(largeNotice)
            ]
        );

        string rendered = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("Galatea"),
            observation,
            Context([new GalateaRecentVisibleAction("latest")])
        );
        using JsonDocument parsed = JsonDocument.Parse(rendered);
        JsonElement root = parsed.RootElement;

        Assert.Equal(
            1,
            root.GetProperty("currentTurn")
                .GetProperty("externalNotices")
                .GetArrayLength()
        );
        Assert.Equal(
            "latest",
            root.GetProperty("recentVisibleActions")[0]
                .GetProperty("text")
                .GetString()
        );
        Assert.InRange(
            Encoding.UTF8.GetByteCount(rendered),
            1,
            MemoPodLimits.MaximumRecallQueryUtf8Bytes
        );
    }

    [Fact]
    public void QueryRenderer_ActionHasNoIndependentCapAndIsWholeOmitted() {
        string largerThanFormerCap = new('a', 100 * 1024);
        var noNoticeObservation = new PlayerTurnObservation(
            "act",
            Timestamp
        );
        string included = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("Galatea"),
            noNoticeObservation,
            Context([
                new GalateaRecentVisibleAction("older"),
                new GalateaRecentVisibleAction(largerThanFormerCap)
            ])
        );
        using (JsonDocument parsed = JsonDocument.Parse(included)) {
            Assert.Equal(
                largerThanFormerCap,
                parsed.RootElement.GetProperty("recentVisibleActions")[0]
                    .GetProperty("text")
                    .GetString()
            );
        }

        var crowdedObservation = new PlayerTurnObservation(
            new string('p', 8 * 1024),
            Timestamp,
            [new PlayerTurnNotice.Reply(new string('n', 256 * 1024))]
        );
        string omitted = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("Galatea"),
            crowdedObservation,
            Context([new GalateaRecentVisibleAction(
                new string('a', 256 * 1024)
            )])
        );
        using JsonDocument omittedParsed = JsonDocument.Parse(omitted);
        Assert.Empty(
            omittedParsed.RootElement.GetProperty("recentVisibleActions")
                .EnumerateArray()
        );
        Assert.Single(
            omittedParsed.RootElement.GetProperty("currentTurn")
                .GetProperty("externalNotices")
                .EnumerateArray()
        );
    }

    [Fact]
    public void QueryRenderer_PreservesWorstCaseRequiredPlayerText() {
        string playerText = new(
            '\u0001',
            GalateaHttpV1.MaximumMessageUtf8Bytes
        );
        string rendered = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("Galatea"),
            new PlayerTurnObservation(playerText, Timestamp),
            Context([])
        );
        using JsonDocument parsed = JsonDocument.Parse(rendered);

        Assert.Equal(
            playerText,
            parsed.RootElement.GetProperty("currentTurn")
                .GetProperty("playerText")
                .GetString()
        );
        Assert.InRange(
            Encoding.UTF8.GetByteCount(rendered),
            1,
            MemoPodLimits.MaximumRecallQueryUtf8Bytes
        );
    }

    [Fact]
    public void QueryRenderer_RequiresTimestampAndEmptyRecalls() {
        var character = new GalateaCharacterName("Galatea");
        GalateaPlayerTurnRecallContext context = Context([]);
        Assert.Throws<ArgumentException>(() =>
            GalateaMemoRecallQueryRenderer.Render(
                character,
                new PlayerTurnObservation("act"),
                context
            ));
        Assert.Throws<ArgumentException>(() =>
            GalateaMemoRecallQueryRenderer.Render(
                character,
                new PlayerTurnObservation(
                    "act",
                    Timestamp,
                    recalls: [new PlayerTurnRecall(
                        new RecallEntry(RecallType.MemoExactText, "source"),
                        "body"
                    )]
                ),
                context
            ));
    }

    [Fact]
    public void SourceIdCodec_StrictlyRoundTripsCanonicalIds() {
        MemoPodId podId = MemoPodId.Parse(
            "00000000000000000000000000000001"
        );
        MemoId memoId = MemoId.Parse("m1:0000002a");
        string sourceId = GalateaMemoRecallSourceIdCodec.Format(
            podId,
            memoId
        );

        Assert.Equal(
            "memo-pod:v1/00000000000000000000000000000001/m1:0000002a",
            sourceId
        );
        Assert.True(GalateaMemoRecallSourceIdCodec.TryParse(
            sourceId,
            out MemoPodId parsedPodId,
            out MemoId parsedMemoId
        ));
        Assert.Equal(podId, parsedPodId);
        Assert.Equal(memoId, parsedMemoId);
        _ = new RecallEntry(RecallType.MemoExactText, sourceId);

        foreach (string? invalid in new string?[] {
            null,
            "",
            "memo-pod:V1/00000000000000000000000000000001/m1:0000002a",
            "memo-pod:v1/00000000000000000000000000000000/m1:0000002a",
            "memo-pod:v1/0000000000000000000000000000000A/m1:0000002a",
            "memo-pod:v1/00000000000000000000000000000001/m1:0000002A",
            "memo-pod:v1/00000000000000000000000000000001/m1%3A0000002a",
            sourceId + "/extra"
        }) {
            Assert.False(GalateaMemoRecallSourceIdCodec.TryParse(
                invalid,
                out _,
                out _
            ));
        }
        Assert.Throws<ArgumentException>(() =>
            GalateaMemoRecallSourceIdCodec.Format(default, memoId));
        Assert.Throws<ArgumentException>(() =>
            GalateaMemoRecallSourceIdCodec.Format(podId, default));
        Assert.Throws<ArgumentException>(() =>
            new GalateaRecentVisibleAction("bad\ud800"));
    }

    [Fact]
    public void ExactTextBody_IsExactAndClosesMaximumLegalBound() {
        const string Title = "蓝门";
        const string ExactText = "第一行\n~~~~ fence-like\n末尾保留\n";
        string rendered = GalateaMemoExactTextBodyRenderer.Render(
            Title,
            ExactText
        );

        Assert.Equal(
            "标题：蓝门\n\n正文：\n" + ExactText,
            rendered
        );
        Assert.Equal(
            262_677,
            PlayerTurnObservationEnvelope.MaximumRecallBodyUtf8Bytes
        );

        string maximum = GalateaMemoExactTextBodyRenderer.Render(
            new string('t', MemoPodLimits.MaximumMemoTitleUtf8Bytes),
            new string(
                'x',
                MemoPodLimits.MaximumMemoExactTextUtf8Bytes
            )
        );
        Assert.Equal(
            PlayerTurnObservationEnvelope.MaximumRecallBodyUtf8Bytes,
            Encoding.UTF8.GetByteCount(maximum)
        );
        _ = new PlayerTurnRecall(
            new RecallEntry(RecallType.MemoExactText, "source"),
            maximum
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaMemoExactTextBodyRenderer.Render(
                new string(
                    't',
                    MemoPodLimits.MaximumMemoTitleUtf8Bytes + 1
                ),
                "body"
            ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaMemoExactTextBodyRenderer.Render(
                "title",
                new string(
                    'x',
                    MemoPodLimits.MaximumMemoExactTextUtf8Bytes + 1
                )
            ));
        Assert.Throws<ArgumentException>(() =>
            GalateaMemoExactTextBodyRenderer.Render("title", "bad\ud800"));
    }

    [Fact]
    public void MvpPolicy_IsNamedAndWithinMemoPodContracts() {
        Assert.Equal(8, GalateaMemoRecallMvpPolicy.MaxResults);
        Assert.Equal(256, GalateaMemoRecallMvpPolicy.DefaultMaxTokens);
        Assert.InRange(
            GalateaMemoRecallMvpPolicy.DefaultMaxTokens,
            1,
            MemoPodLimits.MaximumRecallMaxTokens
        );
        Assert.Equal(
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
            GalateaMemoRecallMvpPolicy.MaximumFrozenPromptUtf8Bytes
        );
        Assert.Equal(
            8 * MemoPodLimits.MaximumMemoExactTextUtf8Bytes,
            GalateaMemoRecallMvpPolicy
                .MaximumHydratedExactTextUtf8Bytes
        );
        Assert.InRange(
            GalateaMemoRecallMvpPolicy.MaximumHydratedExactTextUtf8Bytes,
            1,
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
        );
    }

    [Fact]
    public void StrongRequest_OwnsOnePreliminaryObservationAndContext() {
        GalateaUserConfig user = User();
        var observation = new PlayerTurnObservation("act", Timestamp);
        GalateaPlayerTurnRecallContext context = Context([]);
        EventAddress boundary = new(
            SizedPtr.Create(4, 4),
            1,
            AddressHint.None
        );

        var request = new GalateaPlayerTurnRecallRequest(
            user,
            boundary,
            observation,
            context
        );
        Assert.Same(observation, request.CurrentObservation);
        Assert.Same(context, request.Context);
        Assert.Same(user, request.User);
        Assert.Equal(boundary, request.CompletionBoundary);
        Assert.Throws<ArgumentException>(() =>
            new GalateaPlayerTurnRecallRequest(
                user,
                boundary,
                new PlayerTurnObservation("act"),
                context
            ));
        Assert.Throws<ArgumentException>(() =>
            new GalateaPlayerTurnRecallRequest(
                user,
                boundary,
                new PlayerTurnObservation(
                    "act",
                    Timestamp,
                    recalls: [new PlayerTurnRecall(
                        new RecallEntry(RecallType.MemoExactText, "source"),
                        "body"
                    )]
                ),
                context
            ));
    }

    private static GalateaPlayerTurnRecallContext Context(
        IEnumerable<GalateaRecentVisibleAction> actions
    ) => new(
        RecallBarrier.Empty,
        CharacterNoteOriginBarrier.Empty,
        actions
    );

    private static GalateaUserConfig User() => new(
        "alice",
        "password",
        new GalateaCharacterName("Galatea"),
        new GalateaPlayerName("Player"),
        "/session",
        "/delegation",
        "/memory",
        GalateaSessionProvisioning.ExistingOnly,
        "system"
    );
}

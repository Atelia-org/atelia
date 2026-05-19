using Atelia.Data;
using Atelia.StateJournal;
using Atelia.TextEditScript;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class PerceptionEvidenceRendererTests {
    [Fact]
    public void RenderForPrompt_And_Player_ShouldExposeSameCoreEvidence() {
        var perception = CreateSamplePerception();

        var promptText = PerceptionEvidenceRenderer.RenderForPrompt(perception);
        var playerText = PerceptionEvidenceRenderer.RenderForPlayer(perception);
        var visibleInteractionIds = PerceptionEvidenceRenderer.EnumerateVisibleInteractions(perception)
            .Select(static interaction => interaction.InteractionId)
            .ToArray();

        Assert.Equal(
            ["listen-water", "inspect-berries", "use-rope", "talk-guide"],
            visibleInteractionIds
        );

        var expectedSnippets = new[] {
            "确认它清凉无异味",
            "泉底可能有遗迹",
            "inspect-berries",
            "use-rope",
            "talk-guide",
            "listen-water",
            "先把可疑线索记下",
            "通过：前一步 grounded"
        };

        foreach (var snippet in expectedSnippets) {
            Assert.Contains(snippet, promptText);
            Assert.Contains(snippet, playerText);
        }

        Assert.Contains("turnCost: 0", promptText);
        Assert.Contains("effectSlots: immediate", promptText);
        Assert.Contains("turnCost: 1", promptText);
        Assert.Contains("顺手可做", playerText);
        Assert.Contains("会占用这一回合", playerText);

        Assert.DoesNotContain("[spring]", playerText);
        Assert.DoesNotContain("[berries]", playerText);
        Assert.DoesNotContain("[guide]", playerText);
    }

    [Fact]
    public void GamePresenter_RenderPerception_ShouldIncludeCanonicalEvidenceAndActionGuide() {
        var perception = CreateSamplePerception();

        var rendered = GamePresenter.RenderPerception(perception, TerminalHelpMode.On);

        Assert.Contains("🧩 你现在能直接尝试的动作:", rendered);
        Assert.Contains("inspect-berries", rendered);
        Assert.Contains("use-rope", rendered);
        Assert.Contains("talk-guide", rendered);
        Assert.Contains("listen-water", rendered);
        Assert.Contains("pmux game interact", rendered);
        Assert.Contains("<行动依据>", rendered);
        Assert.Contains("确认它清凉无异味", rendered);
    }

    [Fact]
    public void GamePresenter_RenderPerception_WhenVersionProvided_ShouldShowCommittedVersion() {
        var perception = CreateSamplePerception();
        var versionAddress = CommitAddress.Create(3, new CommitTicket(SizedPtr.Create(64, 16)));

        var rendered = GamePresenter.RenderPerception(perception, TerminalHelpMode.Off, versionAddress);

        Assert.Contains($"🔖 已提交版本: {versionAddress}", rendered);
        Assert.Contains("load-version", rendered);
        Assert.Contains("继续游玩", rendered);
    }

    [Fact]
    public void GamePresenter_RenderPerception_WhenHelpModeOff_ShouldShowMinimalHelpHint() {
        var perception = CreateSamplePerception();

        var rendered = GamePresenter.RenderPerception(perception, TerminalHelpMode.Off);

        Assert.Contains("🧭 帮助：pmux game help", rendered);
        Assert.DoesNotContain("🧭 操作速查:", rendered);
    }

    private static PerceptionBundle CreateSamplePerception() {
        return new PerceptionBundle(
            ActorId: "player",
            ActorKind: "player",
            ActorName: "失忆者",
            ActorProfileNote: "浑身湿透，头还有点疼。",
            Day: 1,
            Slot: 1,
            SlotsPerDay: 4,
            Location: new LocationPerception(
                LocationId: "spring",
                Name: "泉眼",
                Description: "一汪清泉从碎石间涌出，水面下似乎有模糊的纹路。",
                Exits: [
                    new LocationExitPerception("south", "forest", "密林")
                ],
                Items: [
                    new ItemPerception(
                        ItemId: "berries",
                        Name: "浆果丛",
                        Description: "枝头挂着几串深紫色浆果。",
                        Interactions: [
                            new InteractionPerception(
                                InteractionId: "inspect-berries",
                                TargetKind: "item",
                                TargetId: "berries",
                                ActionKind: "inspect",
                                VisibleLabel: "检查浆果",
                                PreconditionNote: "none",
                                EffectNote: null,
                                TurnCost: 1,
                                EffectScope: GameSimulation.RoomEffectScope,
                                EffectSlots: [GameSimulation.TurnEndEffectSlot]
                            )
                        ]
                    )
                ],
                Actors: [
                    new ActorPerception(
                        ActorId: "guide",
                        Kind: "npc",
                        Name: "陌生人",
                        ProfileNote: "披着湿斗篷，静静站在石边。",
                        Interactions: [
                            new InteractionPerception(
                                InteractionId: "talk-guide",
                                TargetKind: "actor",
                                TargetId: "guide",
                                ActionKind: "talk",
                                VisibleLabel: "询问陌生人",
                                PreconditionNote: "需要先靠近",
                                EffectNote: null,
                                TurnCost: 1,
                                EffectScope: GameSimulation.RoomEffectScope,
                                EffectSlots: [GameSimulation.TurnEndEffectSlot]
                            )
                        ]
                    )
                ],
                Interactions: [
                    new InteractionPerception(
                        InteractionId: "listen-water",
                        TargetKind: "location",
                        TargetId: "spring",
                        ActionKind: "inspect",
                        VisibleLabel: "倾听水声",
                        PreconditionNote: "none",
                        EffectNote: null,
                        TurnCost: 1,
                        EffectScope: GameSimulation.RoomEffectScope,
                        EffectSlots: [GameSimulation.TurnEndEffectSlot]
                    )
                ]
            ),
            InventoryItems: [
                new ItemPerception(
                    ItemId: "rope",
                    Name: "麻绳",
                    Description: "一截湿漉漉的麻绳。",
                    Interactions: [
                        new InteractionPerception(
                            InteractionId: "use-rope",
                            TargetKind: "item",
                            TargetId: "rope",
                            ActionKind: "use",
                            VisibleLabel: "用麻绳试探水深",
                            PreconditionNote: "none",
                            EffectNote: null,
                            TurnCost: 0,
                            EffectScope: GameSimulation.SelfEffectScope,
                            EffectSlots: [GameSimulation.ImmediateEffectSlot]
                        )
                    ]
                )
            ],
            NotebookBlocks: new TextBlockSnapshotDocument(
                [
                new TextBlockSnapshot(11, "记住：泉底可能有遗迹。")
            ]
            ),
            AcceptedSteps: [
                new TurnStep(
                    StepNumber: 1,
                    ActionKind: "small/edit-memory-notebook",
                    ActionSummary: "把泉眼记进 notebook",
                    ActionPayload: null,
                    PreActionReason: "先把可疑线索记下，免得下一回合忘掉。",
                    ValidatorFeedback: "通过：前一步 grounded。",
                    EndsTurn: false,
                    StepOutcomeSummary: "你把泉眼这条线索及时记了下来。",
                    StepOutcomeState: GameSimulation.StepOutcomeCommittedNow
                )
            ],
            LastResolution: "你俯身尝了泉水，确认它清凉无异味，暂时可以饮用。"
        );
    }
}

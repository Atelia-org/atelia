using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal enum TerminalHelpMode {
    Off,
    On
}

internal static class PlayerActionGuideText {
    internal const string SharedReasonRuleLine1 = "只要命令或工具里出现 '<行动依据>' / reason，就先写一句你此刻凭什么这么做。";
    internal const string SharedReasonRuleLine2 = "它应该像临动手前给自己的提醒，而不是事后解释。";
    internal const string SharedReasonRuleLine3 = "可以提到当前直接看到的东西、记事本里已有的记录、或上回合刚发生的事。";
    internal const string SharedReasonRuleLine4 = "不要写你没亲眼看到也没记在记事本里的内容。";
    internal const string NotebookRuleLine = "记录猜测时请写成“可能 / 怀疑 / 尚未确认”，不要把未证实内容写成已确认事实。";
    internal const string SharedReasoningReminder = "遵循共享行动依据规则：只引用当前可见信息、记事本与已确立的上一回合结算；不要把猜测写成已确认事实，也不要事后合理化。";

    internal const string ReasonArgumentDescriptionPrefix = "这一步动作的事前推理。";
    internal const string ReasonArgumentDescriptionCore = "应先根据当前可见信息、Memory-Notebook 和已确立的上回合结算，说明为什么准备这么做；不要写成事后合理化，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";
    internal const string EditMemoryNotebookReasonArgumentSuffix = "用来说明为什么此刻这样整理记事本。";
    internal const string ExploreReasonArgumentSuffix = "用来说明为什么准备探索这个方向。";
    internal const string InteractReasonArgumentSuffix = "用来说明为什么准备执行这个当前可见 interaction。";
    internal const string RestReasonArgumentSuffix = "用来说明为什么此刻选择原地休息。";
    internal const string EditMemoryNotebookReasonArgumentDescription = ReasonArgumentDescriptionPrefix + ReasonArgumentDescriptionCore + EditMemoryNotebookReasonArgumentSuffix;
    internal const string ExploreReasonArgumentDescription = ReasonArgumentDescriptionPrefix + ReasonArgumentDescriptionCore + ExploreReasonArgumentSuffix;
    internal const string InteractReasonArgumentDescription = ReasonArgumentDescriptionPrefix + ReasonArgumentDescriptionCore + InteractReasonArgumentSuffix;
    internal const string RestReasonArgumentDescription = ReasonArgumentDescriptionPrefix + ReasonArgumentDescriptionCore + RestReasonArgumentSuffix;

    internal const string ToolReasonParamDescriptionPrefix = "行动依据：先说明当前证据为什么支持这一步；不要写成事后解释，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";
    internal const string EditMemoryNotebookReasonToolParamSuffix = "说明为什么要这样更新记忆。";
    internal const string ExploreReasonToolParamSuffix = "说明为什么探索这个方向。";
    internal const string InteractReasonToolParamSuffix = "说明为什么执行这个 interaction。";
    internal const string RestReasonToolParamSuffix = "说明为什么此刻选择暂不移动。";
    internal const string EditMemoryNotebookReasonToolParamDescription = ToolReasonParamDescriptionPrefix + EditMemoryNotebookReasonToolParamSuffix;
    internal const string ExploreReasonToolParamDescription = ToolReasonParamDescriptionPrefix + ExploreReasonToolParamSuffix;
    internal const string InteractReasonToolParamDescription = ToolReasonParamDescriptionPrefix + InteractReasonToolParamSuffix;
    internal const string RestReasonToolParamDescription = ToolReasonParamDescriptionPrefix + RestReasonToolParamSuffix;

    internal const string EditMemoryNotebookReasonAttributeDescription = "事前推理：依据当前可见信息、Memory-Notebook 和已确立的上回合结算说明为什么要这样更新记忆。不要写成事后解释，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";
    internal const string ExploreReasonAttributeDescription = "事前推理：依据当前可见信息、Memory-Notebook 和已确立的上回合结算说明为什么探索这个方向。不要写成事后解释，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";
    internal const string InteractReasonAttributeDescription = "事前推理：依据当前可见 interaction、环境、Memory-Notebook 和已确立的上回合结算说明为什么执行它。不要写成事后解释，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";
    internal const string RestReasonAttributeDescription = "事前推理：依据当前可见信息、Memory-Notebook 和已确立的上回合结算说明为什么此刻选择暂不移动。不要写成事后解释，也不要提及你没看到或没记在 Memory-Notebook 里的内容。";

    internal const string EditMemoryNotebookToolDescription = "Small-Action：编辑自己的私人 Memory-Notebook，不结束当前回合。" + NotebookRuleLine;
    internal const string RestAWhileToolDescription = "Large-Action：谨慎观察并暂不移动。没有明确目标、信息不足或风险偏高时优先选择。";
    internal const string ExploreToolDescription = "Large-Action：向指定方向探索。可以沿已知出口前进，也可以朝未知方向试探；若有重点目标，可写进 focus。";
    internal const string InteractToolDescription = "当前 Perception-Bundle 中可见 interaction 的统一入口。系统会按该 interaction 的 turn cost 判定 small / large：small interaction 立即执行且不结束回合；large interaction 会作为本回合的 Large-Action proposal 暂存。";

    internal const string EditScriptParameterDescription = "TextEditScript 片段或完整 XML。可直接传 <insert side=\"after\" anchor=\"tail\">...</insert>；系统会自动补根节点。每个操作只处理一个 block，内容不能换行。";
    internal const string DirectionParameterDescription = "探索方向，例如 north/south/east/west/inside。可以探索已知出口，也可以试探未知方向。";
    internal const string FocusParameterDescription = "可选：希望重点寻找或确认的对象；没有则传 null。";
    internal const string InteractionIdParameterDescription = "当前 Perception-Bundle 中可见的 interaction_id。系统会根据该 interaction 判定这是 small 还是 large。";
}

internal static class PlayerActionGuideCatalog {
    internal sealed record PlayerToolMetadata(
        string Name,
        string Description,
        IReadOnlyList<ToolParamSpec> Parameters
    );

    private static readonly string[] s_sharedReasoningRuleLines = [
        PlayerActionGuideText.SharedReasonRuleLine1,
        PlayerActionGuideText.SharedReasonRuleLine2,
        PlayerActionGuideText.SharedReasonRuleLine3,
        PlayerActionGuideText.SharedReasonRuleLine4
    ];

    internal static IReadOnlyList<string> GetSharedReasoningRuleLines() => s_sharedReasoningRuleLines;

    internal static string GetNotebookRuleLine() => PlayerActionGuideText.NotebookRuleLine;

    internal static string BuildValidatorSharedReasoningSection() {
        var sb = new StringBuilder();
        sb.AppendLine("下面这些是 Terminal / LlmPlayer / Validator 共用的玩家事前推理要求：");
        for (var i = 0; i < s_sharedReasoningRuleLines.Length; i++) {
            sb.AppendLine($"{i + 1}. {s_sharedReasoningRuleLines[i]}");
        }

        sb.AppendLine($"{s_sharedReasoningRuleLines.Length + 1}. {PlayerActionGuideText.NotebookRuleLine}");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildSharedReasoningReminder() {
        return PlayerActionGuideText.SharedReasoningReminder;
    }

    internal static string GetEditMemoryNotebookReasonArgumentDescription() => PlayerActionGuideText.EditMemoryNotebookReasonArgumentDescription;

    internal static string GetExploreReasonArgumentDescription() => PlayerActionGuideText.ExploreReasonArgumentDescription;

    internal static string GetInteractReasonArgumentDescription() => PlayerActionGuideText.InteractReasonArgumentDescription;

    internal static string GetRestReasonArgumentDescription() => PlayerActionGuideText.RestReasonArgumentDescription;

    internal static string RenderTerminalHelpFooter(
        PerceptionBundle perception,
        TerminalHelpMode helpMode,
        bool forceShowFullHelp = false
    ) {
        return forceShowFullHelp || helpMode == TerminalHelpMode.On
            ? RenderTerminalFullHelp(perception)
            : RenderTerminalMinimalHelpHint();
    }

    internal static string RenderTerminalFullHelp(PerceptionBundle? perception) {
        var sb = new StringBuilder();
        var currentClock = perception is null
            ? "当前时段"
            : GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay);
        var nextClockText = perception is null
            ? "下一时段"
            : GameClock.FormatClock(
                GameClock.PreviewNextClock(perception.Day, perception.Slot, perception.SlotsPerDay).Day,
                GameClock.PreviewNextClock(perception.Day, perception.Slot, perception.SlotsPerDay).Slot,
                perception.SlotsPerDay
            );

        sb.AppendLine("🧭 操作速查:");
        sb.AppendLine("   临时显示这份完整速查：pmux game help");
        sb.AppendLine("   以后每次都显示完整速查：pmux game help on");
        sb.AppendLine("   只保留最简帮助提示：pmux game help off");
        sb.AppendLine();

        sb.AppendLine("   先记住这一条:");
        foreach (var line in s_sharedReasoningRuleLines) {
            sb.AppendLine($"   {line}");
        }
        sb.AppendLine();

        sb.AppendLine("   随时可用（不推进时间）:");
        sb.AppendLine("   - pmux game look-around");
        sb.AppendLine("     重新看看周围、记事本，以及这回合已经做过的事。");
        sb.AppendLine();

        sb.AppendLine("   整理记事本（不推进时间）:");
        sb.AppendLine("   - pmux game edit-memory-notebook '<行动依据>' '<编辑片段>'");
        sb.AppendLine("     用来增删改你的私人记事本。你可以直接写 insert / replace / delete 片段，系统会自动补根节点。");
        sb.AppendLine("     一次可以放一个或多个并列操作元素；每个操作只处理一个 block，内容不能换行。");
        sb.AppendLine($"     {PlayerActionGuideText.NotebookRuleLine}");
        sb.AppendLine("     如果你想先试写、先看改后预览和检查意见，可以改用:");
        sb.AppendLine("     pmux game edit-memory-notebook --dry-run '<行动依据>' '<编辑片段>'");
        sb.AppendLine("     如果检查没通过，按提示修改行动依据或编辑内容后重试即可；不会扣回合也不会丢数据。");
        AppendNotebookEditRecipes(sb, perception?.NotebookBlocks);
        sb.AppendLine();

        sb.AppendLine("   直接尝试眼前动作:");
        sb.AppendLine("   - pmux game interact '<行动依据>' '<动作编号>'");
        sb.AppendLine("     '<动作编号>' 来自上方“你现在能直接尝试的动作”。");
        sb.AppendLine("     每个动作后面都会写明它是“顺手可做”，还是“会占用这一回合”。");
        sb.AppendLine($"     若它会结束回合，时间会从 {currentClock} 推进到 {nextClockText}；若只是顺手动作，你还能继续做别的事。");
        sb.AppendLine("     执行后，世界会给出此刻你能感知到的结果。");
        AppendInteractionRecipes(sb, perception);
        sb.AppendLine();

        sb.AppendLine($"   以下动作都会结束这一回合，并将时间从 {currentClock} 推进到 {nextClockText}：");
        sb.AppendLine();

        sb.AppendLine("   前往别处:");
        sb.AppendLine("   - pmux game explore '<行动依据>' '<方向>'");
        sb.AppendLine("     你可以沿已知出口前进，也可以朝未知方向试探。");
        sb.AppendLine("     如果想提醒自己重点找什么，可选加 --focus '<目标>'，例如:");
        sb.AppendLine("     pmux game explore --focus '山洞入口' '北边已有密林，继续寻找遮蔽处或山洞入口有助于获得更稳定的庇护。' north");
        sb.AppendLine();

        sb.AppendLine("   暂时按兵不动:");
        sb.AppendLine("   - pmux game rest-a-while '<行动依据>'");
        sb.AppendLine("     原地缓一缓。当你想先观察、整理思路，或者眼下没有更稳妥的目标时，用它结束这一回合。");
        return sb.ToString();
    }

    internal static string RenderTerminalMinimalHelpHint() {
        return """
🧭 帮助：pmux game help
   如需以后每次都显示完整速查，可用 pmux game help on；若想恢复成只保留这条提示，可用 pmux game help off。
""";
    }

    internal static string RenderTerminalHelpStatus(TerminalHelpMode mode) {
        return mode switch {
            TerminalHelpMode.On => "当前设置：完整操作速查会在每次局面渲染后常驻显示。",
            _ => "当前设置：默认只显示最简帮助提示；完整操作速查需用 `pmux game help` 临时查看。"
        };
    }

    internal static string BuildLlmPlayerManual() {
        var sb = new StringBuilder();
        sb.AppendLine("[操作手册]");
        foreach (var line in s_sharedReasoningRuleLines) {
            sb.AppendLine($"- {line}");
        }
        sb.AppendLine($"- {PlayerActionGuideText.NotebookRuleLine}");
        sb.AppendLine("- 你可以先调用零到多个不推进时间的 player_edit_memory_notebook，也可以调用 player_interact 处理当前可见 interaction。");
        sb.AppendLine("- player_interact 是统一 interact 入口：系统会根据当前 interaction 判定 small / large；small interaction 立即执行且不结束回合，large interaction 会作为本回合的 Large-Action proposal 暂存。");
        sb.AppendLine("- 最终仍必须提交 exactly one Large-Action：可以是 player_rest_a_while、player_explore，或由 player_interact 提交的 large interaction。");
        sb.AppendLine("- 若没有明确目标，或证据不足以支持更积极的动作，优先选择 player_rest_a_while。");
        return sb.ToString();
    }

    internal static PlayerToolMetadata GetEditMemoryNotebookToolMetadata() {
        return new PlayerToolMetadata(
            "player_edit_memory_notebook",
            PlayerActionGuideText.EditMemoryNotebookToolDescription,
            [
                new ToolParamSpec(
                    "reason",
                    PlayerActionGuideText.EditMemoryNotebookReasonToolParamDescription,
                    ToolParamType.String
                ),
                new ToolParamSpec(
                    "edit_script",
                    PlayerActionGuideText.EditScriptParameterDescription,
                    ToolParamType.String
                )
            ]
        );
    }

    internal static PlayerToolMetadata GetRestAWhileToolMetadata() {
        return new PlayerToolMetadata(
            "player_rest_a_while",
            PlayerActionGuideText.RestAWhileToolDescription,
            [
                new ToolParamSpec(
                    "reason",
                    PlayerActionGuideText.RestReasonToolParamDescription,
                    ToolParamType.String
                )
            ]
        );
    }

    internal static PlayerToolMetadata GetExploreToolMetadata() {
        return new PlayerToolMetadata(
            "player_explore",
            PlayerActionGuideText.ExploreToolDescription,
            [
                new ToolParamSpec(
                    "reason",
                    PlayerActionGuideText.ExploreReasonToolParamDescription,
                    ToolParamType.String
                ),
                new ToolParamSpec(
                    "direction",
                    PlayerActionGuideText.DirectionParameterDescription,
                    ToolParamType.String
                ),
                new ToolParamSpec(
                    "focus",
                    PlayerActionGuideText.FocusParameterDescription,
                    ToolParamType.String,
                    isNullable: true,
                    defaultValue: new ParamDefault(null)
                )
            ]
        );
    }

    internal static PlayerToolMetadata GetInteractToolMetadata() {
        return new PlayerToolMetadata(
            "player_interact",
            PlayerActionGuideText.InteractToolDescription,
            [
                new ToolParamSpec(
                    "reason",
                    PlayerActionGuideText.InteractReasonToolParamDescription,
                    ToolParamType.String
                ),
                new ToolParamSpec(
                    "interaction_id",
                    PlayerActionGuideText.InteractionIdParameterDescription,
                    ToolParamType.String
                )
            ]
        );
    }

    private static void AppendInteractionRecipes(StringBuilder sb, PerceptionBundle? perception) {
        if (perception is null) {
            sb.AppendLine("     先 look-around 看当前可见 interaction，再从中选一个动作编号。");
            return;
        }

        var interactions = PerceptionEvidenceRenderer.EnumerateVisibleInteractions(perception).ToArray();
        if (interactions.Length == 0) {
            sb.AppendLine("     眼下没有可直接尝试的动作；通常先看看四周、记笔记，或换个方向探索。");
            return;
        }

        var first = interactions[0];
        sb.AppendLine($"     当前可用的动作编号: {string.Join(", ", interactions.Select(static interaction => interaction.InteractionId))}");
        sb.AppendLine($"     示例: pmux game interact '眼前已经有“{first.VisibleLabel}”这个选择，我想先顺着这条线索试一下。' {first.InteractionId}");
        sb.AppendLine($"     例如 [{first.InteractionId}] 现在属于：{GameSimulation.DescribeInteractionTurnCostForPlayer(first)}");
    }

    private static void AppendNotebookEditRecipes(StringBuilder sb, TextBlockSnapshotDocument? snapshot) {
        sb.AppendLine();
        sb.AppendLine("     编辑片段是 XML 格式。你只需要写操作元素，系统会自动补 <text-edit-script> 根节点:");
        sb.AppendLine("     - 插入: <insert side=\"after|before\" anchor=\"head|tail|数字\">新内容</insert>");
        sb.AppendLine("     - 替换: <replace anchor=\"head|tail|数字\">新内容</replace>");
        sb.AppendLine("     - 删除: <delete anchor=\"head|tail|数字\" />  （自闭合，不需要内容）");
        sb.AppendLine("     anchor 含义: head=第一条, tail=最后一条, 数字=指定 block id");
        sb.AppendLine("     side 含义: before=在 anchor 前面, after=在 anchor 后面");
        sb.AppendLine("     每个操作只处理一个 block，内容不能换行。多个操作可以并列写。");
        sb.AppendLine();

        if (snapshot is null || snapshot.Blocks.Count == 0) {
            sb.AppendLine("     你的记事本现在是空的。第一笔最常见是记下眼前最容易忘的事实:");
            sb.AppendLine("     pmux game edit-memory-notebook '这是当前直接可见，而且我很快可能会忘掉的信息。' '<insert side=\"after\" anchor=\"tail\">记住：这里是沙滩，北边通往密林。</insert>'");
            return;
        }

        var blockIdText = string.Join(", ", snapshot.Blocks.Select(static block => $"[{block.BlockId}]"));
        var sampleDeleteId = snapshot.Blocks[0].BlockId;

        sb.AppendLine($"     当前可直接引用的 block id: {blockIdText}");
        sb.AppendLine("     常见写法示例:");
        sb.AppendLine("     pmux game edit-memory-notebook '我想把新线索补到最后。' '<insert side=\"after\" anchor=\"tail\">记住：北边树林里可能有淡水，尚未确认。</insert>'");
        sb.AppendLine("     pmux game edit-memory-notebook '我想把最前面的旧记录改得更谨慎些。' '<replace anchor=\"head\">怀疑北边树林里可能有淡水，尚未确认。</replace>'");
        sb.AppendLine($"     pmux game edit-memory-notebook '这条记录已经明显过时或写错了。' '<delete anchor=\"{sampleDeleteId}\" />'");
    }
}

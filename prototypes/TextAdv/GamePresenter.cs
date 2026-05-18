using System.Text;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GamePresenter {
    internal static string RenderPerception(PerceptionBundle perception) {
        var sb = new StringBuilder();
        sb.Append(PerceptionEvidenceRenderer.RenderForPlayer(perception));
        sb.AppendLine();

        AppendActionGuide(sb, perception);

        return sb.ToString();
    }

    internal static string RenderNotebookEditDryRun(
        PerceptionBundle perception,
        NotebookEditProposal proposal,
        string preActionReason,
        GameActionValidator.ValidationResult validation
    ) {
        var sb = new StringBuilder();
        var statusLine = validation.Accepted
            ? "✅ 试写完成：已生成预览，这一步目前能通过检查。"
            : "⚠️ 试写完成：已生成预览，但这一步目前还过不了检查。";

        sb.AppendLine(statusLine);
        sb.AppendLine("这次只是预演，不会真的落笔，也不会记入本回合。");
        sb.AppendLine("如果局面没有变化，去掉 --dry-run 重跑，通常会得到相近结果。");
        sb.AppendLine();

        sb.AppendLine("🧠 你的行动依据:");
        AppendIndented(sb, preActionReason);
        sb.AppendLine();

        sb.AppendLine("📝 这次准备怎么改:");
        sb.AppendLine($"   {proposal.ActionSummary}");
        sb.AppendLine();

        sb.AppendLine("🧠 当前记事本:");
        AppendIndented(sb, NotebookBlockViewRenderer.RenderBlockView(perception.NotebookBlocks));
        sb.AppendLine();

        sb.AppendLine("🔧 系统整理后的编辑脚本:");
        AppendIndented(sb, proposal.CanonicalScriptXml);
        sb.AppendLine();

        sb.AppendLine("🔮 改完后大概会变成:");
        AppendIndented(sb, NotebookBlockViewRenderer.RenderPreviewBlockView(perception.NotebookBlocks, proposal.PredictedAfterSnapshot));
        sb.AppendLine("   注：新插入的块暂时显示为 [new-N]；真正提交后才会分配正式 block id。");
        sb.AppendLine();

        sb.AppendLine("🧪 检查结果:");
        AppendIndented(sb, validation.Feedback);
        return sb.ToString();
    }

    internal static string RenderTurnCollectionStatus(TurnCollectionStatus status) {
        var sb = new StringBuilder();
        sb.AppendLine($"🗓️ {GameClock.FormatClock(status.Day, status.Slot, status.SlotsPerDay)}");
        sb.AppendLine("⏳ 当前回合仍在等待其他同行。");
        sb.AppendLine($"🎭 现在轮到: {status.TurnOwnerActorId}");
        sb.AppendLine($"✅ 所有人都已决定本回合动作: {status.AllActiveActorsSubmittedLargeAction}");
        sb.AppendLine();

        sb.AppendLine("👥 当前同行状态:");
        if (status.Actors.Count == 0) {
            sb.AppendLine("   (none)");
            return sb.ToString();
        }

        foreach (var actor in status.Actors) {
            var submissionText = actor.HasSubmittedLargeAction
                ? actor.LargeActionSummary ?? actor.LargeActionKind ?? "(已决定)"
                : "(还没决定)";
            sb.AppendLine($"   {actor.Name} ({actor.Kind})");
            sb.AppendLine($"      当前状态: {(actor.Active ? "仍在本回合内" : "当前未参与")}");
            sb.AppendLine($"      本回合动作: {submissionText}");
        }

        return sb.ToString();
    }

    private static void AppendActionGuide(StringBuilder sb, PerceptionBundle perception) {
        var currentClock = GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay);
        var nextClock = GameClock.PreviewNextClock(perception.Day, perception.Slot, perception.SlotsPerDay);
        var nextClockText = GameClock.FormatClock(nextClock.Day, nextClock.Slot, perception.SlotsPerDay);

        sb.AppendLine("🧭 操作速查:");
        sb.AppendLine("   把下面当作失忆时随身带着的小抄。");
        sb.AppendLine();

        sb.AppendLine("   先记住这一条:");
        sb.AppendLine("   只要命令里出现 '<行动依据>'，就先写一句你此刻凭什么这么做。");
        sb.AppendLine("   它应该像临动手前给自己的提醒，而不是事后解释。");
        sb.AppendLine("   可以提到当前直接看到的东西、记事本里已有的记录、或上回合刚发生的事。");
        sb.AppendLine("   不要写你没亲眼看到也没记在记事本里的内容。");
        sb.AppendLine();

        sb.AppendLine("   随时可用（不推进时间）:");
        sb.AppendLine("   - pmux game look-around");
        sb.AppendLine("     重新看看周围、记事本，以及这回合已经做过的事。");
        sb.AppendLine();

        sb.AppendLine("   整理记事本（不推进时间）:");
        sb.AppendLine("   - pmux game edit-memory-notebook '<行动依据>' '<编辑片段>'");
        sb.AppendLine("     用来增删改你的私人记事本。你可以直接写 insert / replace / delete 片段，系统会自动补根节点。");
        sb.AppendLine("     一次可以放一个或多个并列操作元素；每个操作只处理一个 block，内容不能换行。");
        sb.AppendLine("     记录猜测时请写成“可能 / 怀疑 / 尚未确认”，不要把未证实内容写成已确认事实。");
        sb.AppendLine("     如果你想先试写、先看改后预览和检查意见，可以改用:");
        sb.AppendLine("     pmux game edit-memory-notebook --dry-run '<行动依据>' '<编辑片段>'");
        sb.AppendLine("     如果检查没通过，按提示修改行动依据或编辑内容后重试即可；不会扣回合也不会丢数据。");
        AppendNotebookEditRecipes(sb, perception.NotebookBlocks);
        sb.AppendLine();

        sb.AppendLine(
            "   以下动作都会结束这一回合，并将时间从 "
            + $"{currentClock} 推进到 {nextClockText}："
        );
        sb.AppendLine();

        sb.AppendLine("   直接尝试眼前动作:");
        sb.AppendLine("   - pmux game interact '<行动依据>' '<动作编号>'");
        sb.AppendLine("     '<动作编号>' 来自上方\u201c你现在能直接尝试的动作\u201d。");
        sb.AppendLine("     执行后，世界会给出此刻你能感知到的结果。");
        AppendInteractionRecipes(sb, perception);
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
    }

    private static void AppendInteractionRecipes(StringBuilder sb, PerceptionBundle perception) {
        var interactions = PerceptionEvidenceRenderer.EnumerateVisibleInteractions(perception).ToArray();
        if (interactions.Length == 0) {
            sb.AppendLine("     眼下没有可直接尝试的动作；通常先看看四周、记笔记，或换个方向探索。");
            return;
        }

        var first = interactions[0];
        sb.AppendLine($"     当前可用的动作编号: {string.Join(", ", interactions.Select(static interaction => interaction.InteractionId))}");
        sb.AppendLine($"     示例: pmux game interact '眼前已经有“{first.VisibleLabel}”这个选择，我想先顺着这条线索试一下。' {first.InteractionId}");
    }

    private static void AppendNotebookEditRecipes(StringBuilder sb, TextBlockSnapshotDocument snapshot) {
        sb.AppendLine();
        sb.AppendLine("     编辑片段是 XML 格式。你只需要写操作元素，系统会自动补 <text-edit-script> 根节点:");
        sb.AppendLine("     - 插入: <insert side=\"after|before\" anchor=\"head|tail|数字\">新内容</insert>");
        sb.AppendLine("     - 替换: <replace anchor=\"head|tail|数字\">新内容</replace>");
        sb.AppendLine("     - 删除: <delete anchor=\"head|tail|数字\" />  （自闭合，不需要内容）");
        sb.AppendLine("     anchor 含义: head=第一条, tail=最后一条, 数字=指定 block id");
        sb.AppendLine("     side 含义: before=在 anchor 前面, after=在 anchor 后面");
        sb.AppendLine("     每个操作只处理一个 block，内容不能换行。多个操作可以并列写。");
        sb.AppendLine();

        if (snapshot.Blocks.Count == 0) {
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

    private static void AppendIndented(StringBuilder sb, string text)
        => AppendIndented(sb, text, "   ");

    private static void AppendIndented(StringBuilder sb, string text, string indent) {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var line in normalized.Split('\n')) {
            sb.AppendLine($"{indent}{line}");
        }
    }

    private static void AppendLabeledBlock(StringBuilder sb, string label, string text, string indent) {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');

        if (lines.Length == 0) {
            sb.AppendLine($"{indent}{label}: ");
            return;
        }

        sb.AppendLine($"{indent}{label}: {lines[0]}");
        for (var i = 1; i < lines.Length; i++) {
            sb.AppendLine($"{indent}        {lines[i]}");
        }
    }
}

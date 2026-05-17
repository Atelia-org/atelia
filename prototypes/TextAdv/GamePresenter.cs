using System.Text;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GamePresenter {
    internal static string RenderPerception(PerceptionBundle perception) {
        var sb = new StringBuilder();

        sb.AppendLine($"🗓️ {GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay)}");

        if (!string.IsNullOrWhiteSpace(perception.LastResolution)) {
            sb.AppendLine("📣 上回合结算:");
            AppendIndented(sb, perception.LastResolution!);
            sb.AppendLine();
        }

        sb.AppendLine($"📍 {perception.Location.Name}");
        AppendIndented(sb, perception.Location.Description);
        sb.AppendLine();

        sb.AppendLine("🧠 Private Memory-Notebook (block view):");
        AppendIndented(sb, NotebookBlockViewRenderer.RenderBlockView(perception.NotebookBlocks));
        sb.AppendLine();

        sb.AppendLine("🚪 你目前看得到的出口:");
        if (perception.Location.Exits.Count == 0) {
            sb.AppendLine("   (none)");
        }
        else {
            foreach (var exit in perception.Location.Exits) {
                sb.AppendLine($"   {exit.Direction} → {exit.TargetName}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("🎒 你目前看得到的物品:");
        if (perception.Location.Items.Count == 0) {
            sb.AppendLine("   (none)");
        }
        else {
            foreach (var item in perception.Location.Items) {
                sb.AppendLine($"   [{item.ItemId}] {item.Name}");
                AppendIndented(sb, item.Description, "      ");
                AppendInteractions(sb, item.Interactions, "      ");
            }
        }
        sb.AppendLine();

        sb.AppendLine("🧩 你目前看得到的交互:");
        if (perception.Location.Interactions.Count == 0) {
            sb.AppendLine("   (none)");
        }
        else {
            AppendInteractions(sb, perception.Location.Interactions, "   ");
        }
        sb.AppendLine();

        sb.AppendLine("📝 当前回合已接受步骤:");
        if (perception.AcceptedSteps.Count == 0) {
            sb.AppendLine("   (none)");
        }
        else {
            foreach (var step in perception.AcceptedSteps) {
                sb.AppendLine($"   {step.StepNumber}. {step.ActionKind} — {step.ActionSummary}");
                AppendLabeledBlock(sb, "事前推理", step.PreActionReason, "      ");
                AppendLabeledBlock(sb, "validator", step.ValidatorFeedback, "      ");
            }
        }
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
            ? "✅ Dry-Run 完成：preview 已生成，validator 当前通过。"
            : "⚠️ Dry-Run 完成：preview 已生成，但 validator 当前未通过。";

        sb.AppendLine(statusLine);
        sb.AppendLine("当前没有写入 notebook，也没有记录 accepted step。");
        sb.AppendLine("如果当前局面保持不变，去掉 --dry-run 重跑通常会看到相近结果；但正式执行仍会按当下状态重新走一遍 validator。");
        sb.AppendLine();

        sb.AppendLine("🧠 你提交的事前推理:");
        AppendIndented(sb, preActionReason);
        sb.AppendLine();

        sb.AppendLine("📝 候选 Small-Action:");
        sb.AppendLine($"   {proposal.ActionSummary}");
        sb.AppendLine();

        sb.AppendLine("🧠 当前 Memory-Notebook (before):");
        AppendIndented(sb, NotebookBlockViewRenderer.RenderBlockView(perception.NotebookBlocks));
        sb.AppendLine();

        sb.AppendLine("🔧 Canonical TextEditScript:");
        AppendIndented(sb, proposal.CanonicalScriptXml);
        sb.AppendLine();

        sb.AppendLine("🔮 Predicted Memory-Notebook (after):");
        AppendIndented(sb, NotebookBlockViewRenderer.RenderPreviewBlockView(perception.NotebookBlocks, proposal.PredictedAfterSnapshot));
        sb.AppendLine("   注：新插入块暂以 [new-N] 显示；真正提交后系统才会分配实际 block id。");
        sb.AppendLine();

        sb.AppendLine("🧪 Validator:");
        AppendIndented(sb, validation.Feedback);
        return sb.ToString();
    }

    private static void AppendActionGuide(StringBuilder sb, PerceptionBundle perception) {
        var currentClock = GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay);
        var nextClock = GameClock.PreviewNextClock(perception.Day, perception.Slot, perception.SlotsPerDay);
        var nextClockText = GameClock.FormatClock(nextClock.Day, nextClock.Slot, perception.SlotsPerDay);

        sb.AppendLine("🧭 你现在可以做什么:");
        sb.AppendLine("   下面是给失忆状态准备的速查。你不需要记住以前的规则说明，照着这里做就行。");
        sb.AppendLine();

        sb.AppendLine("   信息命令（不会记入回合，也不会结束回合）:");
        sb.AppendLine("   - pmux game look-around");
        sb.AppendLine("     重新显示当前局面、当前 notebook，以及这回合已经做过的步骤。");
        sb.AppendLine();

        sb.AppendLine("   Small-Action（不会结束当前回合）:");
        sb.AppendLine("   - pmux game edit-memory-notebook '<事前推理>' '<编辑片段>'");
        sb.AppendLine("     第一个参数永远是事前推理：先说明你依据当前证据为什么准备这么做，再给出实际动作。不要把它写成动作做完后的解释词。 ");
        sb.AppendLine("     用来编辑你的私人 Memory-Notebook。你可以直接写 insert / replace / delete 片段，系统会自动补 <text-edit-script> 根节点。");
        sb.AppendLine("     一次可以放一个或多个并列操作元素；每个操作只处理一个 block，内容不能换行。");
        sb.AppendLine("     记录猜测时请写成“可能 / 怀疑 / 尚未确认”，不要把未证实内容写成已确认事实。");
        sb.AppendLine("     如果你想先试探 validator 和 after-view，可以改用: pmux game edit-memory-notebook --dry-run '<事前推理>' '<编辑片段>'");

        AppendNotebookEditRecipes(sb, perception.NotebookBlocks);
        sb.AppendLine();

        sb.AppendLine("   Large-Action（会结束当前回合）:");
        sb.AppendLine("   - pmux game explore '<事前推理>' '<方向>'");
        sb.AppendLine("     向某个方向探索。若该方向已有出口，你会沿已知出口移动；若没有，GM 账本可以创建一个新 Location 并记录连接。");
        sb.AppendLine("     可选加 --focus '<目标>'，例如:");
        sb.AppendLine("     pmux game explore --focus '山洞入口' '北边已有密林，继续寻找遮蔽处或山洞入口有助于获得更稳定的庇护。' north");
        sb.AppendLine();
        sb.AppendLine("   - pmux game rest-a-while '<事前推理>'");
        sb.AppendLine("     这个参数同样表示事前推理：先说明为什么你准备在此刻结束回合，再执行动作。 ");
        sb.AppendLine($"     原地休息一会。执行后会结束本回合，并把时间从 {currentClock} 推进到 {nextClockText}。");
        sb.AppendLine("     只有当你觉得没有更急的 small action 或探索目标时，再执行它。");
    }

    private static void AppendNotebookEditRecipes(StringBuilder sb, TextBlockSnapshotDocument snapshot) {
        if (snapshot.Blocks.Count == 0) {
            sb.AppendLine("     当前 notebook 为空。最自然的第一步通常是先新增一条:");
            sb.AppendLine("     pmux game edit-memory-notebook '这是当前直接可见、而且我可能很快会忘掉的信息。' '<insert side=\"after\" anchor=\"tail\">记住：这里是沙滩，北边通往密林。</insert>'");
            return;
        }

        var blockIdText = string.Join(", ", snapshot.Blocks.Select(static block => $"[{block.BlockId}]"));
        var sampleDeleteId = snapshot.Blocks[0].BlockId;

        sb.AppendLine($"     当前可直接引用的 block id: {blockIdText}");
        sb.AppendLine("     常用写法示例:");
        sb.AppendLine("     pmux game edit-memory-notebook '我想把新的线索补到笔记最后。' '<insert side=\"after\" anchor=\"tail\">记住：北边树林里可能有淡水，尚未确认。</insert>'");
        sb.AppendLine("     pmux game edit-memory-notebook '我想把最前面的旧笔记改成更谨慎的表述。' '<replace anchor=\"head\">怀疑北边树林里可能有淡水，尚未确认。</replace>'");
        sb.AppendLine($"     pmux game edit-memory-notebook '这条笔记已经明显过时或写错了。' '<delete anchor=\"{sampleDeleteId}\" />'");
        sb.AppendLine("     你也可以直接用 head / tail 作为 anchor；如果要一次连续改多条，就把多个操作元素并列写进同一个 <编辑片段> 参数里。");
    }

    private static void AppendInteractions(StringBuilder sb, IReadOnlyList<InteractionPerception> interactions, string indent) {
        if (interactions.Count == 0) {
            sb.AppendLine($"{indent}可交互: (none)");
            return;
        }

        sb.AppendLine($"{indent}可交互:");
        foreach (var interaction in interactions) {
            sb.AppendLine($"{indent}- [{interaction.InteractionId}] {interaction.VisibleLabel} ({interaction.ActionKind})");
        }
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

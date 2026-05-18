using System.Text;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GamePresenter {
    internal static string RenderPerception(
        PerceptionBundle perception,
        TerminalHelpMode helpMode,
        bool forceShowFullHelp = false
    ) {
        var sb = new StringBuilder();
        sb.Append(PerceptionEvidenceRenderer.RenderForPlayer(perception));
        sb.AppendLine();
        sb.Append(PlayerActionGuideCatalog.RenderTerminalHelpFooter(perception, helpMode, forceShowFullHelp));

        return sb.ToString();
    }

    internal static string RenderStandaloneHelp(PerceptionBundle? perception, TerminalHelpMode helpMode) {
        var sb = new StringBuilder();
        sb.AppendLine(PlayerActionGuideCatalog.RenderTerminalHelpStatus(helpMode));
        sb.AppendLine();
        sb.Append(PlayerActionGuideCatalog.RenderTerminalFullHelp(perception));
        return sb.ToString();
    }

    internal static string RenderStandaloneHelpStatus(TerminalHelpMode helpMode)
        => PlayerActionGuideCatalog.RenderTerminalHelpStatus(helpMode);

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
        sb.AppendLine($"⏳ Barrier: {DescribeBarrierState(status.BarrierState)}");
        if (!string.IsNullOrWhiteSpace(status.TurnOwnerActorId)) {
            sb.AppendLine($"🎭 当前待行动 actor: {status.TurnOwnerActorId}");
        }

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

    private static string DescribeBarrierState(string barrierState) {
        return barrierState switch {
            "awaiting-actions" => "尚未收到任何 Large-Action",
            "collecting-actions" => "已收到部分 Large-Action，仍在继续收集",
            "ready-for-gm" => "所有 active actor 已提交，等待 GM 统一结算",
            _ => barrierState
        };
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

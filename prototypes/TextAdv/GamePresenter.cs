using System.Text;

namespace Atelia.TextAdv;

internal static class GamePresenter
{
    internal static string RenderPerception(GameSimulation.PerceptionBundle perception)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"🗓️ Day {perception.Day}, Slot {perception.Slot}/{perception.SlotsPerDay}");

        if (!string.IsNullOrWhiteSpace(perception.LastResolution))
        {
            sb.AppendLine("📣 上回合结算:");
            AppendIndented(sb, perception.LastResolution!);
            sb.AppendLine();
        }

        sb.AppendLine($"📍 {perception.Location.Name}");
        AppendIndented(sb, perception.Location.Description);
        sb.AppendLine();

        sb.AppendLine("🧠 Private Memory-Notebook:");
        if (string.IsNullOrWhiteSpace(perception.NotebookContent))
        {
            sb.AppendLine("   (empty)");
        }
        else
        {
            AppendIndented(sb, perception.NotebookContent);
        }
        sb.AppendLine();

        sb.AppendLine("📝 当前回合已接受步骤:");
        if (perception.AcceptedSteps.Count == 0)
        {
            sb.AppendLine("   (none)");
        }
        else
        {
            foreach (var step in perception.AcceptedSteps)
            {
                sb.AppendLine($"   {step.StepNumber}. {step.ActionKind} — {step.ActionSummary}");
                AppendLabeledBlock(sb, "reason", step.ReasonTrace, "      ");
                AppendLabeledBlock(sb, "validator", step.ValidatorFeedback, "      ");
            }
        }
        sb.AppendLine();

        sb.AppendLine("🚪 可前往:");

        foreach (var exit in perception.Location.Exits)
        {
            sb.AppendLine($"   {exit.Direction} → {exit.TargetName}");
        }

        return sb.ToString();
    }

    private static void AppendIndented(StringBuilder sb, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var line in normalized.Split('\n'))
        {
            sb.AppendLine($"   {line}");
        }
    }

    private static void AppendLabeledBlock(StringBuilder sb, string label, string text, string indent)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');

        if (lines.Length == 0)
        {
            sb.AppendLine($"{indent}{label}: ");
            return;
        }

        sb.AppendLine($"{indent}{label}: {lines[0]}");
        for (var i = 1; i < lines.Length; i++)
        {
            sb.AppendLine($"{indent}        {lines[i]}");
        }
    }
}

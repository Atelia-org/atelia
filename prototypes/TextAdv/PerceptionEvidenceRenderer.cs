using System.Text;

namespace Atelia.TextAdv;

internal sealed record VisibleInteractionEntry(
    string SourceKind,
    string SourceId,
    string SourceLabel,
    InteractionPerception Interaction
);

internal sealed record PerceptionEvidenceView(
    string ActorId,
    string ActorKind,
    string ActorName,
    string ActorProfileNote,
    string TimeText,
    string LocationId,
    string LocationName,
    string LocationDescription,
    string? LastResolution,
    string NotebookBlockView,
    IReadOnlyList<LocationExitPerception> Exits,
    IReadOnlyList<ItemPerception> VisibleItems,
    IReadOnlyList<ItemPerception> InventoryItems,
    IReadOnlyList<ActorPerception> VisibleActors,
    IReadOnlyList<InteractionPerception> LocationInteractions,
    IReadOnlyList<VisibleInteractionEntry> AllVisibleInteractions,
    IReadOnlyList<TurnStep> AcceptedSteps
);

internal static class PerceptionEvidenceRenderer {
    internal static PerceptionEvidenceView Create(PerceptionBundle perception) {
        ArgumentNullException.ThrowIfNull(perception);

        var allVisibleInteractions = new List<VisibleInteractionEntry>();
        AddInteractions(
            allVisibleInteractions,
            sourceKind: "location",
            sourceId: perception.Location.LocationId,
            sourceLabel: perception.Location.Name,
            perception.Location.Interactions
        );

        foreach (var item in perception.Location.Items) {
            AddInteractions(
                allVisibleInteractions,
                sourceKind: "visible-item",
                sourceId: item.ItemId,
                sourceLabel: item.Name,
                item.Interactions
            );
        }

        foreach (var item in perception.InventoryItems) {
            AddInteractions(
                allVisibleInteractions,
                sourceKind: "inventory-item",
                sourceId: item.ItemId,
                sourceLabel: item.Name,
                item.Interactions
            );
        }

        foreach (var actor in perception.Location.Actors) {
            AddInteractions(
                allVisibleInteractions,
                sourceKind: "actor",
                sourceId: actor.ActorId,
                sourceLabel: actor.Name,
                actor.Interactions
            );
        }

        return new PerceptionEvidenceView(
            perception.ActorId,
            perception.ActorKind,
            perception.ActorName,
            perception.ActorProfileNote,
            GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay),
            perception.Location.LocationId,
            perception.Location.Name,
            perception.Location.Description,
            perception.LastResolution,
            NotebookBlockViewRenderer.RenderBlockView(perception.NotebookBlocks),
            perception.Location.Exits,
            perception.Location.Items,
            perception.InventoryItems,
            perception.Location.Actors,
            perception.Location.Interactions,
            allVisibleInteractions,
            perception.AcceptedSteps
        );
    }

    internal static string RenderForPlayer(PerceptionBundle perception)
        => RenderForPlayer(Create(perception));

    internal static string RenderForPrompt(PerceptionBundle perception)
        => RenderForPrompt(Create(perception));

    internal static IReadOnlyList<InteractionPerception> EnumerateVisibleInteractions(PerceptionBundle perception)
        => Create(perception).AllVisibleInteractions.Select(static entry => entry.Interaction).ToArray();

    internal static string RenderForPlayer(PerceptionEvidenceView evidence) {
        ArgumentNullException.ThrowIfNull(evidence);

        var sb = new StringBuilder();
        sb.AppendLine($"🗓️ {evidence.TimeText}");
        sb.AppendLine($"🎭 Actor: {evidence.ActorName} [{evidence.ActorId}, {evidence.ActorKind}]");
        if (!string.IsNullOrWhiteSpace(evidence.ActorProfileNote)) {
            AppendIndentedBlock(sb, evidence.ActorProfileNote, "   ");
        }

        if (!string.IsNullOrWhiteSpace(evidence.LastResolution)) {
            sb.AppendLine("📣 上回合结算:");
            AppendIndentedBlock(sb, evidence.LastResolution!, "   ");
            sb.AppendLine();
        }

        sb.AppendLine($"📍 {evidence.LocationName} [{evidence.LocationId}]");
        AppendIndentedBlock(sb, evidence.LocationDescription, "   ");
        sb.AppendLine();

        sb.AppendLine("🧠 Private Memory-Notebook (block view):");
        AppendIndentedBlock(sb, evidence.NotebookBlockView, "   ");
        sb.AppendLine();

        sb.AppendLine("🚪 你目前看得到的出口:");
        AppendPlayerExits(sb, evidence.Exits);
        sb.AppendLine();

        sb.AppendLine("🎒 你目前看得到的物品:");
        AppendPlayerItems(sb, evidence.VisibleItems);
        sb.AppendLine();

        sb.AppendLine("🧺 你目前持有的物品:");
        AppendPlayerItems(sb, evidence.InventoryItems);
        sb.AppendLine();

        sb.AppendLine("👥 你目前看得到的角色:");
        AppendPlayerActors(sb, evidence.VisibleActors);
        sb.AppendLine();

        sb.AppendLine("🧩 全部可见交互（速查）:");
        AppendPlayerInteractionQuickRef(sb, evidence.AllVisibleInteractions);
        sb.AppendLine();

        sb.AppendLine("📝 当前回合已接受步骤:");
        AppendAcceptedStepsForPlayer(sb, evidence.AcceptedSteps);
        return sb.ToString();
    }

    internal static string RenderForPrompt(PerceptionEvidenceView evidence) {
        ArgumentNullException.ThrowIfNull(evidence);

        var sb = new StringBuilder();
        sb.AppendLine("[Perception-Bundle]");
        sb.AppendLine($"- ActorId: {evidence.ActorId}");
        sb.AppendLine($"- ActorName: {evidence.ActorName}");
        sb.AppendLine($"- ActorKind: {evidence.ActorKind}");
        sb.AppendLine($"- ActorProfileNote: {OrNone(evidence.ActorProfileNote)}");
        sb.AppendLine($"- Time: {evidence.TimeText}");
        sb.AppendLine($"- LocationId: {evidence.LocationId}");
        sb.AppendLine($"- LocationName: {evidence.LocationName}");
        sb.AppendLine($"- LocationDescription: {evidence.LocationDescription}");

        sb.AppendLine();
        sb.AppendLine("[上回合结算结果]");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(evidence.LastResolution)
            ? "(这是第一回合，尚无上回合结算。)"
            : evidence.LastResolution
        );

        sb.AppendLine();
        sb.AppendLine("[Memory-Notebook 当前块视图]");
        sb.AppendLine(evidence.NotebookBlockView);

        sb.AppendLine();
        sb.AppendLine("[Exits]");
        if (evidence.Exits.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var exit in evidence.Exits) {
                sb.AppendLine($"- {exit.Direction} -> {exit.TargetLocationId} ({exit.TargetName})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[VisibleItems]");
        AppendPromptItems(sb, evidence.VisibleItems);

        sb.AppendLine();
        sb.AppendLine("[InventoryItems]");
        AppendPromptItems(sb, evidence.InventoryItems);

        sb.AppendLine();
        sb.AppendLine("[VisibleActors]");
        AppendPromptActors(sb, evidence.VisibleActors);

        sb.AppendLine();
        sb.AppendLine("[AllVisibleInteractions]");
        if (evidence.AllVisibleInteractions.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var entry in evidence.AllVisibleInteractions) {
                sb.AppendLine($"- {FormatPromptInteraction(entry)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前回合已接受步骤]");
        if (evidence.AcceptedSteps.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var step in evidence.AcceptedSteps) {
                sb.AppendLine($"- {step.StepNumber}. {step.ActionKind} | {step.ActionSummary}");
                sb.AppendLine($"  PreActionReason: {step.PreActionReason}");
                sb.AppendLine($"  ValidatorFeedback: {step.ValidatorFeedback}");
            }
        }

        return sb.ToString();
    }

    private static void AddInteractions(
        List<VisibleInteractionEntry> destination,
        string sourceKind,
        string sourceId,
        string sourceLabel,
        IReadOnlyList<InteractionPerception> interactions
    ) {
        foreach (var interaction in interactions) {
            destination.Add(new VisibleInteractionEntry(sourceKind, sourceId, sourceLabel, interaction));
        }
    }

    private static void AppendPlayerExits(StringBuilder sb, IReadOnlyList<LocationExitPerception> exits) {
        if (exits.Count == 0) {
            sb.AppendLine("   (none)");
            return;
        }

        foreach (var exit in exits) {
            sb.AppendLine($"   {exit.Direction} → {exit.TargetName}");
        }
    }

    private static void AppendPlayerItems(StringBuilder sb, IReadOnlyList<ItemPerception> items) {
        if (items.Count == 0) {
            sb.AppendLine("   (none)");
            return;
        }

        foreach (var item in items) {
            sb.AppendLine($"   [{item.ItemId}] {item.Name}");
            AppendIndentedBlock(sb, item.Description, "      ");
            AppendPlayerNestedInteractions(sb, item.Interactions, "      ");
        }
    }

    private static void AppendPlayerActors(StringBuilder sb, IReadOnlyList<ActorPerception> actors) {
        if (actors.Count == 0) {
            sb.AppendLine("   (none)");
            return;
        }

        foreach (var actor in actors) {
            sb.AppendLine($"   [{actor.ActorId}] {actor.Name} ({actor.Kind})");
            if (!string.IsNullOrWhiteSpace(actor.ProfileNote)) {
                AppendIndentedBlock(sb, actor.ProfileNote, "      ");
            }

            AppendPlayerNestedInteractions(sb, actor.Interactions, "      ");
        }
    }

    private static void AppendPlayerNestedInteractions(
        StringBuilder sb,
        IReadOnlyList<InteractionPerception> interactions,
        string indent
    ) {
        if (interactions.Count == 0) {
            sb.AppendLine($"{indent}可交互: (none)");
            return;
        }

        sb.AppendLine($"{indent}可交互:");
        foreach (var interaction in interactions) {
            sb.AppendLine($"{indent}- [{interaction.InteractionId}] {interaction.VisibleLabel} ({interaction.ActionKind})");
            if (!string.IsNullOrWhiteSpace(interaction.PreconditionNote)
                && !string.Equals(interaction.PreconditionNote, "none", StringComparison.OrdinalIgnoreCase)) {
                sb.AppendLine($"{indent}  条件: {interaction.PreconditionNote}");
            }
        }
    }

    private static void AppendPlayerInteractionQuickRef(StringBuilder sb, IReadOnlyList<VisibleInteractionEntry> interactions) {
        if (interactions.Count == 0) {
            sb.AppendLine("   (none)");
            return;
        }

        foreach (var entry in interactions) {
            sb.AppendLine(
                $"   - [{entry.Interaction.InteractionId}] {entry.Interaction.VisibleLabel} ({entry.Interaction.ActionKind}) @ {FormatPlayerSource(entry)}"
            );
            if (!string.IsNullOrWhiteSpace(entry.Interaction.PreconditionNote)
                && !string.Equals(entry.Interaction.PreconditionNote, "none", StringComparison.OrdinalIgnoreCase)) {
                sb.AppendLine($"     条件: {entry.Interaction.PreconditionNote}");
            }
        }
    }

    private static void AppendAcceptedStepsForPlayer(StringBuilder sb, IReadOnlyList<TurnStep> steps) {
        if (steps.Count == 0) {
            sb.AppendLine("   (none)");
            return;
        }

        foreach (var step in steps) {
            sb.AppendLine($"   {step.StepNumber}. {step.ActionKind} — {step.ActionSummary}");
            AppendLabeledBlock(sb, "事前推理", step.PreActionReason, "      ");
            AppendLabeledBlock(sb, "validator", step.ValidatorFeedback, "      ");
        }
    }

    private static void AppendPromptItems(StringBuilder sb, IReadOnlyList<ItemPerception> items) {
        if (items.Count == 0) {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var item in items) {
            sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
            AppendPromptNestedInteractions(sb, item.Interactions);
        }
    }

    private static void AppendPromptActors(StringBuilder sb, IReadOnlyList<ActorPerception> actors) {
        if (actors.Count == 0) {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var actor in actors) {
            sb.AppendLine($"- {actor.ActorId}: {actor.Name} ({actor.Kind}) | {OrNone(actor.ProfileNote)}");
            AppendPromptNestedInteractions(sb, actor.Interactions);
        }
    }

    private static void AppendPromptNestedInteractions(StringBuilder sb, IReadOnlyList<InteractionPerception> interactions) {
        if (interactions.Count == 0) {
            sb.AppendLine("  - Interactions: (none)");
            return;
        }

        sb.AppendLine("  - Interactions:");
        foreach (var interaction in interactions) {
            sb.AppendLine($"    - {FormatPromptInteraction(interaction)}");
        }
    }

    private static string FormatPromptInteraction(VisibleInteractionEntry entry) {
        return $"{FormatPromptInteraction(entry.Interaction)} | source: {entry.SourceKind}:{entry.SourceId} ({entry.SourceLabel})";
    }

    private static string FormatPromptInteraction(InteractionPerception interaction) {
        return $"{interaction.InteractionId}: {interaction.TargetKind}:{interaction.TargetId} | {interaction.ActionKind} | {interaction.VisibleLabel} | precondition: {OrNone(interaction.PreconditionNote)}";
    }

    private static string FormatPlayerSource(VisibleInteractionEntry entry) {
        return entry.SourceKind switch {
            "location" => $"地点 {entry.SourceLabel}",
            "visible-item" => $"地上物品 {entry.SourceLabel}",
            "inventory-item" => $"持有物品 {entry.SourceLabel}",
            "actor" => $"角色 {entry.SourceLabel}",
            _ => entry.SourceLabel
        };
    }

    private static string OrNone(string? text)
        => string.IsNullOrWhiteSpace(text) ? "(none)" : text;

    private static void AppendIndentedBlock(StringBuilder sb, string text, string indent) {
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

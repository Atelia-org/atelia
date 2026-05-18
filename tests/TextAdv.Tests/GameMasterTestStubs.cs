using Atelia.StateJournal;

namespace Atelia.TextAdv.Tests;

internal static class GameMasterTestStubs {
    internal static GameMasterStub CreateDeterministicLikeStub() {
        return new GameMasterStub(
            ExploreResolver: ResolveExploreAsync,
            InteractionResolver: ResolveInteractionAsync,
            InteractionEffectResolver: ResolveInteractionEffectAsync,
            ImmediateSelfInteractionResolver: ResolveImmediateSelfInteractionAsync,
            CollectedTurnResolver: ResolveCollectedTurnAsync
        );
    }

    private static Task<GmExploreResolution> ResolveExploreAsync(
        DurableDict<string> root,
        GmExploreContext context,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var gmTools = new GmWorldEditService(root);
        var actorId = context.Perception.ActorId;
        var sourceLocation = context.Perception.Location;
        var existingExit = sourceLocation.Exits.FirstOrDefault(exit => string.Equals(exit.Direction, context.Direction, StringComparison.Ordinal));
        string targetLocationId;
        string targetLocationName;

        if (existingExit is not null) {
            targetLocationId = existingExit.TargetLocationId;
            targetLocationName = existingExit.TargetName;
        }
        else {
            targetLocationId = $"{sourceLocation.LocationId}-{context.Direction}".ToLowerInvariant();
            targetLocationName = string.IsNullOrWhiteSpace(context.Focus)
                ? $"{context.Direction} 侧新区域"
                : context.Focus!.Trim();

            if (!gmTools.CreateLocation(targetLocationId, targetLocationName, $"从「{sourceLocation.Name}」向 {context.Direction} 延伸出去的一处区域。").IsSuccess) {
                targetLocationName = GameSimulation.DescribePerceptionForActor(root, actorId).Location.Name;
            }
            else {
                _ = gmTools.LinkLocations(sourceLocation.LocationId, context.Direction, targetLocationId, context.SuggestedReverseDirection);
            }
        }

        _ = gmTools.MoveActorTo(actorId, targetLocationId);
        var summary = existingExit is null
            ? $"你向 {context.Direction} 试探前行，确认了一处新的地点「{targetLocationName}」，并把这条通路记进了世界账本。"
            : $"你沿着已知出口从「{sourceLocation.Name}」向 {context.Direction} 前进，来到「{targetLocationName}」。";
        return Task.FromResult(new GmExploreResolution(summary, UsedLlm: false, FallbackReason: null));
    }

    private static Task<GmExploreResolution> ResolveInteractionAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = string.IsNullOrWhiteSpace(context.Interaction.EffectNote)
            ? $"你执行了「{context.Interaction.VisibleLabel}」。"
            : context.Interaction.EffectNote!;
        return Task.FromResult(new GmExploreResolution(summary, UsedLlm: false, FallbackReason: null));
    }

    private static Task<GmInteractionEffectResolution> ResolveInteractionEffectAsync(
        DurableDict<string> root,
        GmInteractionEffectContext context,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var actorFacing = string.IsNullOrWhiteSpace(context.Interaction.EffectNote)
            ? BuildGenericEffectActorFacing(context.Interaction.VisibleLabel, context.EffectSlot)
            : context.Interaction.EffectNote!;
        string? terminalVisible = null;
        if (context.TerminalCanObserveActor) {
            terminalVisible = string.Equals(context.ActorPerception.ActorId, "player", StringComparison.Ordinal)
                ? actorFacing
                : BuildObservedEffectSummary(root, context.ActorPerception.ActorId, context.Interaction.VisibleLabel, context.EffectSlot);
        }

        return Task.FromResult(
            new GmInteractionEffectResolution(
                actorFacing,
                terminalVisible,
                UsedLlm: false,
                FallbackReason: null
            )
        );
    }

    private static Task<GmExploreResolution> ResolveImmediateSelfInteractionAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var gmTools = new GmWorldEditService(root);
        var interaction = context.Interaction;

        if (string.Equals(interaction.TargetKind, "item", StringComparison.Ordinal)
            && InteractionActionKinds.IsPickup(interaction.ActionKind)) {
            _ = gmTools.MoveItemToActor(interaction.TargetId, context.Perception.ActorId);
            _ = gmTools.SetInteractionVisibility(interaction.InteractionId, "hidden");
        }

        var summary = string.IsNullOrWhiteSpace(interaction.EffectNote)
            ? $"你顺手试了试「{interaction.VisibleLabel}」。"
            : interaction.EffectNote!;
        return Task.FromResult(new GmExploreResolution(summary, UsedLlm: false, FallbackReason: null));
    }

    private static Task<GmExploreResolution> ResolveCollectedTurnAsync(
        DurableDict<string> root,
        GmCollectedTurnContext context,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var gmTools = new GmWorldEditService(root);
        var terminalSummaries = new List<string>();
        foreach (var intent in context.Intents) {
            var (actorFacing, terminalVisible) = ResolveCollectedIntent(root, gmTools, context.TerminalActorId, intent);
            if (!string.IsNullOrWhiteSpace(actorFacing)) {
                _ = gmTools.SetActorResolution(intent.ActorId, actorFacing);
            }

            if (!string.IsNullOrWhiteSpace(terminalVisible)) {
                terminalSummaries.Add(terminalVisible!);
            }
        }

        return Task.FromResult(
            new GmExploreResolution(
                terminalSummaries.Count == 0 ? "GM stub 完成了本回合多主体结算。" : string.Join("\n", terminalSummaries),
                UsedLlm: false,
                FallbackReason: null
            )
        );
    }

    private static (string ActorFacing, string? TerminalVisible) ResolveCollectedIntent(
        DurableDict<string> root,
        GmWorldEditService gmTools,
        string terminalActorId,
        GmCollectedTurnIntent intent
    ) {
        return intent.ActionKind switch {
            "large/rest-a-while" => ResolveCollectedRest(root, terminalActorId, intent),
            "large/explore" => ResolveCollectedExplore(root, gmTools, terminalActorId, intent),
            "large/interact" => ResolveCollectedInteraction(root, terminalActorId, intent),
            _ => ($"你完成了「{intent.ActionSummary}」。", null)
        };
    }

    private static (string ActorFacing, string? TerminalVisible) ResolveCollectedRest(
        DurableDict<string> root,
        string terminalActorId,
        GmCollectedTurnIntent intent
    ) {
        const string actorFacing = "你原地休息了一会。当前原型只推进时钟，不结算更复杂的世界后果。";
        if (string.Equals(intent.ActorId, terminalActorId, StringComparison.Ordinal)) { return (actorFacing, actorFacing); }

        return (
            actorFacing,
            IsSameVisibleRoomAsTerminal(root, terminalActorId, intent.Perception.Location.LocationId, intent.ActorId)
                ? $"你看见{intent.ActorName}原地歇了一会。"
                : null
        );
    }

    private static (string ActorFacing, string? TerminalVisible) ResolveCollectedExplore(
        DurableDict<string> root,
        GmWorldEditService gmTools,
        string terminalActorId,
        GmCollectedTurnIntent intent
    ) {
        var direction = ParsePayloadValue(intent.ActionPayload, "direction") ?? "north";
        var focus = ParsePayloadValue(intent.ActionPayload, "focus");
        var sourceLocation = intent.Perception.Location;
        var existingExit = sourceLocation.Exits.FirstOrDefault(exit => string.Equals(exit.Direction, direction, StringComparison.Ordinal));
        string targetLocationId;
        string targetLocationName;
        var createdNewLocation = false;

        if (existingExit is not null) {
            targetLocationId = existingExit.TargetLocationId;
            targetLocationName = existingExit.TargetName;
        }
        else {
            createdNewLocation = true;
            targetLocationId = $"{sourceLocation.LocationId}-{direction}".ToLowerInvariant();
            targetLocationName = string.IsNullOrWhiteSpace(focus) ? $"{direction} 侧新区域" : focus!;
            if (gmTools.CreateLocation(targetLocationId, targetLocationName, $"从「{sourceLocation.Name}」向 {direction} 延伸出去的一处区域。").IsSuccess) {
                _ = gmTools.LinkLocations(sourceLocation.LocationId, direction, targetLocationId, ReverseDirection(direction));
            }
        }

        _ = gmTools.MoveActorTo(intent.ActorId, targetLocationId);
        var actorFacing = createdNewLocation
            ? $"你向 {direction} 试探前行，确认了一处新的地点「{targetLocationName}」，并把这条通路记进了世界账本。"
            : $"你沿着已知出口从「{sourceLocation.Name}」向 {direction} 前进，来到「{targetLocationName}」。";
        if (string.Equals(intent.ActorId, terminalActorId, StringComparison.Ordinal)) { return (actorFacing, actorFacing); }

        if (!IsSameVisibleRoomAsTerminal(root, terminalActorId, sourceLocation.LocationId, intent.ActorId)) { return (actorFacing, null); }

        var terminalVisible = createdNewLocation
            ? $"你看见{intent.ActorName}朝 {direction} 方向试探前行，离开了「{sourceLocation.Name}」的视线范围。"
            : $"你看见{intent.ActorName}沿着通往「{targetLocationName}」的 {direction} 出口离开了「{sourceLocation.Name}」。";
        return (actorFacing, terminalVisible);
    }

    private static (string ActorFacing, string? TerminalVisible) ResolveCollectedInteraction(
        DurableDict<string> root,
        string terminalActorId,
        GmCollectedTurnIntent intent
    ) {
        var visibleLabel = ParsePayloadValue(intent.ActionPayload, "visibleLabel") ?? intent.ActionSummary;
        var effectNote = ParsePayloadValue(intent.ActionPayload, "effectNote");
        var actorFacing = string.IsNullOrWhiteSpace(effectNote)
            ? $"你执行了「{visibleLabel}」。"
            : effectNote!;
        if (string.Equals(intent.ActorId, terminalActorId, StringComparison.Ordinal)) { return (actorFacing, actorFacing); }

        if (!IsSameVisibleRoomAsTerminal(root, terminalActorId, intent.Perception.Location.LocationId, intent.ActorId)) { return (actorFacing, null); }

        return (actorFacing, $"你看见{intent.ActorName}试着去做「{visibleLabel}」。");
    }

    private static string BuildGenericEffectActorFacing(string visibleLabel, string effectSlot) {
        return effectSlot switch {
            "turn-end" => $"到本回合结束时，你此前试着进行的「{visibleLabel}」终于见了分晓。",
            "per-turn-end" => $"这回合末，你持续进行的「{visibleLabel}」又推进了一点。",
            "on-completion" => $"经过持续忙碌，「{visibleLabel}」终于做完了。",
            _ => $"「{visibleLabel}」有了新的结果。"
        };
    }

    private static string BuildObservedEffectSummary(
        DurableDict<string> root,
        string actorId,
        string visibleLabel,
        string effectSlot
    ) {
        var actorName = GameSimulation.DescribePerceptionForActor(root, actorId).ActorName;
        return effectSlot switch {
            "turn-end" => $"你注意到{actorName}刚才着手的「{visibleLabel}」这时见了分晓。",
            "per-turn-end" => $"你看见{actorName}手头的「{visibleLabel}」又往前推进了一点。",
            "on-completion" => $"你看见{actorName}手头的「{visibleLabel}」终于做完了。",
            _ => $"你注意到{actorName}那边的「{visibleLabel}」有了新的结果。"
        };
    }

    private static bool IsSameVisibleRoomAsTerminal(
        DurableDict<string> root,
        string terminalActorId,
        string actorLocationId,
        string actorId
    ) {
        var terminalPerception = GameSimulation.DescribePerceptionForActor(root, terminalActorId);
        return string.Equals(terminalPerception.Location.LocationId, actorLocationId, StringComparison.Ordinal);
    }

    private static string? ParsePayloadValue(string? payload, string key) {
        if (string.IsNullOrWhiteSpace(payload)) { return null; }

        foreach (var line in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) { continue; }

            if (!string.Equals(line[..separatorIndex].Trim(), key, StringComparison.Ordinal)) { continue; }

            var value = line[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? ReverseDirection(string direction) {
        return direction switch {
            "north" => "south",
            "south" => "north",
            "east" => "west",
            "west" => "east",
            "inside" => "outside",
            "outside" => "inside",
            "up" => "down",
            "down" => "up",
            _ => null
        };
    }
}

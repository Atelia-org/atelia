using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    internal static PerceptionBundle DescribeCurrentPerception(DurableDict<string> root)
        => DescribePerceptionForActor(root, TerminalPlayerActorId);

    internal static PerceptionBundle DescribePerceptionForActor(DurableDict<string> root, string actorId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actor = GetActor(root, actorId);
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var lastResolution = ReadLastResolutionForActor(game, actorId);
        var notebookSnapshot = GetNotebookSnapshot(root, actorId);
        var acceptedSteps = ReadAcceptedSteps(root, actorId);
        var locationId = actor.GetOrThrow<string>(LocationIdKey)!;
        var actorKind = GetActorKind(actor);
        var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
        var actorProfileNote = actor.TryGet(ProfileNoteKey, out string? rawProfileNote) && rawProfileNote is not null
            ? rawProfileNote
            : string.Empty;

        return new PerceptionBundle(
            actorId,
            actorKind,
            actorName,
            actorProfileNote,
            day,
            slot,
            slotsPerDay,
            DescribeLocation(root, locationId, actorId),
            EnumerateVisibleItemsOwnedByActor(root, actorId).ToArray(),
            notebookSnapshot,
            acceptedSteps,
            lastResolution
        );
    }

    internal static LocationPerception DescribeCurrentLocation(DurableDict<string> root)
        => DescribeLocation(root, GetActorLocationId(root, TerminalPlayerActorId), TerminalPlayerActorId);

    internal static TurnCollectionStatus DescribeCurrentTurnStatus(DurableDict<string> root) {
        var game = GetGame(root);
        var currentTurn = GetCurrentTurn(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        var actorIds = EnumerateActiveActorIds(root).ToArray();
        var actorStatuses = EnumerateActiveActorIds(root)
            .Select(
            actorId => {
                var actor = GetActor(root, actorId);
                var kind = GetActorKind(actor);
                var name = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : actorId;
                var active = !actor.TryGet(ActiveKey, out bool rawActive) || rawActive;
                var hasLargeAction = largeActionByActor.TryGet(actorId, out DurableDict<string>? action)
                    && action is not null;
                string? actionKind = null;
                string? actionSummary = null;
                if (hasLargeAction && action is not null) {
                    _ = action.TryGet(ActionKindKey, out actionKind);
                    _ = action.TryGet(ActionSummaryKey, out actionSummary);
                }

                return new TurnActorStatus(
                    actorId,
                    kind,
                    name,
                    active,
                    hasLargeAction,
                    actionKind,
                    actionSummary
                );
            }
        )
            .ToArray();

        var allSubmitted = actorStatuses.Length > 0
            && actorStatuses.All(static actor => actor.HasSubmittedLargeAction);
        var pendingActorId = actorIds.FirstOrDefault(
            actorId => !largeActionByActor.TryGet(actorId, out DurableDict<string>? action) || action is null
        );
        var barrierState = allSubmitted
            ? ReadyForGmBarrierState
            : largeActionByActor.Keys.Any()
                ? CollectingActionsBarrierState
                : AwaitingActionsBarrierState;
        return new TurnCollectionStatus(
            game.GetOrThrow<int>(DayKey),
            game.GetOrThrow<int>(SlotKey),
            game.GetOrThrow<int>(SlotsPerDayKey),
            string.IsNullOrWhiteSpace(pendingActorId) ? actorIds.FirstOrDefault() ?? string.Empty : pendingActorId,
            barrierState,
            allSubmitted,
            actorStatuses
        );
    }

    internal static AteliaResult<InteractionPerception> TryGetVisibleInteraction(
        PerceptionBundle perception,
        string interactionId
    ) {
        interactionId = NormalizeRequired(interactionId, nameof(interactionId));
        var interactions = EnumerateVisibleInteractions(perception)
            .Where(interaction => string.Equals(interaction.InteractionId, interactionId, StringComparison.Ordinal))
            .ToArray();

        if (interactions.Length == 1) { return interactions[0]; }

        if (interactions.Length > 1) {
            return AteliaResult<InteractionPerception>.Failure(
                new TextAdvError(
                    "TextAdv.AmbiguousInteraction",
                    $"InteractionId '{interactionId}' 在当前感知中出现了多次；请先修复世界账本。"
                )
            );
        }

        var available = string.Join(", ", EnumerateVisibleInteractions(perception).Select(static interaction => interaction.InteractionId));
        if (string.IsNullOrWhiteSpace(available)) { available = "(none)"; }
        return AteliaResult<InteractionPerception>.Failure(
            new TextAdvError(
                "TextAdv.InteractionNotVisible",
                $"当前看不到 Interaction '{interactionId}'。可见 interaction: {available}"
            )
        );
    }

    internal static string BuildInteractionPayload(InteractionPerception interaction) {
        var lines = new List<string>
        {
            $"interactionId={interaction.InteractionId}",
            $"target={interaction.TargetKind}:{interaction.TargetId}",
            $"actionKind={interaction.ActionKind}",
            $"visibleLabel={interaction.VisibleLabel}",
            $"turnCost={interaction.TurnCost}",
            $"effectScope={interaction.EffectScope}",
            $"effectSlots={string.Join(",", interaction.EffectSlots)}",
        };
        if (!string.IsNullOrWhiteSpace(interaction.PreconditionNote)) {
            lines.Add($"preconditionNote={interaction.PreconditionNote}");
        }
        if (!string.IsNullOrWhiteSpace(interaction.EffectNote)) {
            lines.Add($"effectNote={interaction.EffectNote}");
        }

        return string.Join("\n", lines);
    }

    private static LocationPerception DescribeLocation(DurableDict<string> root, string locationId, string observerActorId) {
        var location = GetLocation(root, locationId);
        var name = location.GetOrThrow<string>(NameKey)!;
        var description = location.GetOrThrow<string>(DescriptionKey)!;
        var exits = EnumerateExits(root, locationId).ToArray();
        var items = EnumerateVisibleItemsAtLocation(root, locationId).ToArray();
        var actors = EnumerateVisibleActorsAtLocation(root, locationId, observerActorId).ToArray();
        var interactions = EnumerateVisibleInteractions(root, "location", locationId).ToArray();
        return new LocationPerception(locationId, name, description, exits, items, actors, interactions);
    }

    private static IEnumerable<LocationExitPerception> EnumerateExits(
        DurableDict<string> root,
        string locationId
    ) {
        var location = GetLocation(root, locationId);
        var exits = location.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        foreach (var direction in exits.Keys) {
            var targetLocationId = exits.GetOrThrow<string>(direction)!;
            var targetLocation = GetLocation(root, targetLocationId);
            var targetName = targetLocation.GetOrThrow<string>(NameKey)!;
            yield return new LocationExitPerception(direction, targetLocationId, targetName);
        }
    }

    private static IEnumerable<ItemPerception> EnumerateVisibleItemsAtLocation(
        DurableDict<string> root,
        string locationId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items) || items is null) { yield break; }

        foreach (var itemId in items.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var item = items.GetOrThrow<DurableDict<string>>(itemId)!;
            if (item.TryGet(OwnerActorIdKey, out string? ownerActorId)
                && !string.IsNullOrWhiteSpace(ownerActorId)) { continue; }

            if (!item.TryGet(LocationIdKey, out string? itemLocationId)
                || !string.Equals(itemLocationId, locationId, StringComparison.Ordinal)) { continue; }

            var visibility = item.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            yield return new ItemPerception(
                itemId,
                item.GetOrThrow<string>(NameKey)!,
                item.GetOrThrow<string>(DescriptionKey)!,
                EnumerateVisibleInteractions(root, "item", itemId).ToArray()
            );
        }
    }

    private static IEnumerable<ItemPerception> EnumerateVisibleItemsOwnedByActor(
        DurableDict<string> root,
        string actorId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items) || items is null) { yield break; }

        foreach (var itemId in items.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var item = items.GetOrThrow<DurableDict<string>>(itemId)!;
            if (!item.TryGet(OwnerActorIdKey, out string? ownerActorId)
                || !string.Equals(ownerActorId, actorId, StringComparison.Ordinal)) { continue; }

            var visibility = item.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            yield return new ItemPerception(
                itemId,
                item.GetOrThrow<string>(NameKey)!,
                item.GetOrThrow<string>(DescriptionKey)!,
                EnumerateVisibleInteractions(root, "item", itemId).ToArray()
            );
        }
    }

    private static IEnumerable<ActorPerception> EnumerateVisibleActorsAtLocation(
        DurableDict<string> root,
        string locationId,
        string observerActorId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ActorsKey, out DurableDict<string>? actors) || actors is null) { yield break; }

        foreach (var actorId in actors.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            if (string.Equals(actorId, observerActorId, StringComparison.Ordinal)) { continue; }

            var actor = actors.GetOrThrow<DurableDict<string>>(actorId)!;
            if (!actor.TryGet(LocationIdKey, out string? actorLocationId)
                || !string.Equals(actorLocationId, locationId, StringComparison.Ordinal)) { continue; }

            var visibility = actor.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            var kind = GetActorKind(actor);
            var profileNote = actor.TryGet(ProfileNoteKey, out string? rawProfileNote)
                ? rawProfileNote ?? string.Empty
                : string.Empty;

            yield return new ActorPerception(
                actorId,
                kind,
                actor.GetOrThrow<string>(NameKey)!,
                profileNote,
                EnumerateVisibleInteractions(root, "actor", actorId).ToArray()
            );
        }
    }

    private static IEnumerable<InteractionPerception> EnumerateVisibleInteractions(
        DurableDict<string> root,
        string targetKind,
        string targetId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(InteractionsKey, out DurableDict<string>? interactions) || interactions is null) { yield break; }

        foreach (var interactionId in interactions.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var interaction = interactions.GetOrThrow<DurableDict<string>>(interactionId)!;
            var actualTargetKind = interaction.GetOrThrow<string>(TargetKindKey)!;
            var actualTargetId = interaction.GetOrThrow<string>(TargetIdKey)!;
            if (!string.Equals(actualTargetKind, targetKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actualTargetId, targetId, StringComparison.Ordinal)) { continue; }

            var visibility = interaction.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            _ = interaction.TryGet(PreconditionNoteKey, out string? preconditionNote);
            _ = interaction.TryGet(EffectNoteKey, out string? effectNote);
            var turnCost = interaction.TryGet(TurnCostLedgerKey, out int rawTurnCost)
                ? rawTurnCost
                : 1;
            var effectScope = interaction.TryGet(EffectScopeKey, out string? rawEffectScope)
                && !string.IsNullOrWhiteSpace(rawEffectScope)
                ? rawEffectScope
                : RoomEffectScope;
            var actionKind = interaction.GetOrThrow<string>(ActionKindLedgerKey)!;
            if (!InteractionProjectionPolicy.ShouldProject(root, actualTargetKind, actualTargetId, actionKind)) {
                continue;
            }

            yield return new InteractionPerception(
                interactionId,
                actualTargetKind,
                actualTargetId,
                actionKind,
                interaction.GetOrThrow<string>(VisibleLabelKey)!,
                preconditionNote,
                effectNote,
                turnCost,
                effectScope,
                ReadInteractionEffectSlots(interaction)
            );
        }
    }

    private static IEnumerable<InteractionPerception> EnumerateVisibleInteractions(PerceptionBundle perception) {
        foreach (var interaction in perception.Location.Interactions) {
            yield return interaction;
        }

        foreach (var item in perception.Location.Items) {
            foreach (var interaction in item.Interactions) {
                yield return interaction;
            }
        }

        foreach (var item in perception.InventoryItems) {
            foreach (var interaction in item.Interactions) {
                yield return interaction;
            }
        }

        foreach (var actor in perception.Location.Actors) {
            foreach (var interaction in actor.Interactions) {
                yield return interaction;
            }
        }
    }

    private static bool IsVisibleToPlayer(string? visibility)
        => string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, VisibleValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, DiscoveredValue, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<TurnStep> ReadAcceptedSteps(DurableDict<string> root, string actorId) {
        var acceptedSteps = GetAcceptedStepsForActor(root, actorId, createIfMissing: false);
        if (acceptedSteps is null) { return Array.Empty<TurnStep>(); }

        return ReadAcceptedStepList(acceptedSteps);
    }

    private static IReadOnlyList<TurnStep> ReadAcceptedStepList(DurableDict<string> acceptedSteps) {
        return acceptedSteps.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(
            key => {
                var step = acceptedSteps.GetOrThrow<DurableDict<string>>(key)!;
                var numberPart = key.Split('-').Last();
                var stepNumber = int.Parse(numberPart);
                _ = step.TryGet(ActionPayloadKey, out string? actionPayload);
                return new TurnStep(
                    stepNumber,
                    step.GetOrThrow<string>(ActionKindKey)!,
                    step.GetOrThrow<string>(ActionSummaryKey)!,
                    actionPayload,
                    step.GetOrThrow<string>(PreActionReasonKey)!,
                    step.GetOrThrow<string>(ValidatorFeedbackKey)!,
                    step.GetOrThrow<bool>(EndsTurnKey),
                    step.TryGet(StepOutcomeSummaryKey, out string? stepOutcomeSummary) ? stepOutcomeSummary : null,
                    step.TryGet(StepOutcomeStateKey, out string? stepOutcomeState) && !string.IsNullOrWhiteSpace(stepOutcomeState)
                        ? stepOutcomeState
                        : (step.GetOrThrow<bool>(EndsTurnKey) ? StepOutcomeCompleted : StepOutcomeCommittedNow)
                );
            }
        )
            .ToArray();
    }

    private static IReadOnlyList<string> ReadInteractionEffectSlots(DurableDict<string> interaction) {
        if (!interaction.TryGet(EffectSlotsKey, out DurableDict<string>? effectSlots)
            || effectSlots is null) { return [TurnEndEffectSlot]; }

        var orderedSlots = effectSlots.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(key => effectSlots.GetOrThrow<string>(key)!)
            .Where(static slot => !string.IsNullOrWhiteSpace(slot))
            .ToArray();
        return orderedSlots.Length == 0 ? [TurnEndEffectSlot] : orderedSlots;
    }
}

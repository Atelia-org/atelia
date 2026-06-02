namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// world truth 的逐 tick 执行器。
///
/// 它只消费 durable embodied state，并把“本 tick 里真的发生了哪些 authoritative 世界变化”
/// 收口为回执，交给 runtime 侧去同步 read-model / trace。
/// </summary>
internal static class WorldTurnExecutor {
    public static ActorMoveReceipt[] AdvanceOneTick(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var movementReceipts = new List<ActorMoveReceipt>();
        string[] actorIds = world.EnumerateActors()
            .Select(static actor => actor.Id)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

        foreach (string actorId in actorIds) {
            var actor = world.GetActor(actorId);

            switch (actor.EmbodiedState) {
                case IdleActorEmbodiedState:
                    break;

                case RouteFollowingActorProcessState routeFollowing:
                    if (TryAdvanceRouteFollowing(world, actor, routeFollowing, out var movementReceipt)
                        && movementReceipt is not null) {
                        movementReceipts.Add(movementReceipt);
                    }

                    break;

                case MiningActorProcessState mining:
                    AdvanceMining(actor, mining);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' uses unsupported embodied state type '{actor.EmbodiedState.GetType().Name}'."
                    );
            }
        }

        return [.. movementReceipts];
    }

    private static bool TryAdvanceRouteFollowing(
        WorldState world,
        Actor actor,
        RouteFollowingActorProcessState state,
        out ActorMoveReceipt? movementReceipt
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(state);

        movementReceipt = null;

        if (state.RemainingPassageIds.Count == 0) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
            return false;
        }

        if (state.RemainingTravelTicksOnCurrentLeg > 1) {
            actor.SetEmbodiedState(new RouteFollowingActorProcessState(
                state.DestinationLocationId,
                state.RemainingPassageIds,
                state.RemainingTravelTicksOnCurrentLeg - 1,
                state.IsInterruptible
            ));
            return false;
        }

        string currentPassageId = state.RemainingPassageIds[0];
        try {
            movementReceipt = world.MoveActorAlongPassageDuringEmbodiedProcess(actor.Id, currentPassageId);
        }
        catch (InvalidOperationException) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
            return false;
        }

        string[] remainingPassageIds = state.RemainingPassageIds.Skip(1).ToArray();
        if (remainingPassageIds.Length == 0) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
            return true;
        }

        string nextPassageId = remainingPassageIds[0];
        if (!world.TryGetPassage(nextPassageId, out var nextPassage) || nextPassage is null) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
            return true;
        }

        try {
            int nextLegTicks = WorldState.ComputeEmbodiedTravelTicks(nextPassage, movementReceipt.ToLocationId);
            actor.SetEmbodiedState(new RouteFollowingActorProcessState(
                state.DestinationLocationId,
                remainingPassageIds,
                nextLegTicks,
                state.IsInterruptible
            ));
        }
        catch (InvalidOperationException) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
        }

        return true;
    }

    private static void AdvanceMining(Actor actor, MiningActorProcessState state) {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(state);

        if (!string.Equals(actor.CurrentLocationId, state.WorksiteId, StringComparison.Ordinal)) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
            return;
        }

        int progressedTicks = state.ProgressTicksInCurrentCycle + 1;
        if (progressedTicks < state.TicksPerYield) {
            actor.SetEmbodiedState(new MiningActorProcessState(
                state.WorksiteId,
                progressedTicks,
                state.TicksPerYield,
                state.YieldItemId,
                state.ProducedYieldCount,
                state.YieldAmount,
                state.IsInterruptible
            ));
            return;
        }

        actor.AddCarriedResource(state.YieldItemId, state.YieldAmount);
        actor.SetEmbodiedState(new MiningActorProcessState(
            state.WorksiteId,
            progressTicksInCurrentCycle: 0,
            state.TicksPerYield,
            state.YieldItemId,
            producedYieldCount: checked(state.ProducedYieldCount + state.YieldAmount),
            state.YieldAmount,
            state.IsInterruptible
        ));
    }
}

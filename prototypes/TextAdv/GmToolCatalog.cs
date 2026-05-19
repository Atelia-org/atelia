using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal enum GmToolPack {
    Full,
    ImmediateSelf,
    ExploreMap,
    ExploreAudit,
    InteractionConsequence,
    InteractionAudit,
    CollectedTurnCore,
    CollectedTurnSummary,
    ImmediateSelfConsequence,
    ImmediateSelfAudit,
    InteractionEffectConsequence,
    InteractionEffectAudit,
}

internal static class GmToolCatalog {
    internal const string CreateLocationToolName = "gm_create_location";
    internal const string LinkLocationsToolName = "gm_link_locations";
    internal const string MoveActorToolName = "gm_move_actor";
    internal const string CreateItemToolName = "gm_create_item";
    internal const string CreateNpcToolName = "gm_create_npc";
    internal const string UpdateItemToolName = "gm_update_item";
    internal const string MoveItemToActorToolName = "gm_move_item_to_actor";
    internal const string PlaceItemAtLocationToolName = "gm_place_item_at_location";
    internal const string AddInteractionToolName = "gm_add_interaction";
    internal const string SetVisibilityToolName = "gm_set_visibility";
    internal const string SetInteractionVisibilityToolName = "gm_set_interaction_visibility";
    internal const string SetActorResolutionToolName = "gm_set_actor_resolution";

    private sealed record ToolRegistration(
        string Name,
        IReadOnlyList<GmToolPack> Packs,
        Func<GmWorldEditService, ITool> Factory
    );

    private static readonly ToolRegistration[] s_registrations =
    [
        new(
            CreateLocationToolName,
            [GmToolPack.Full, GmToolPack.ExploreMap, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary],
            static toolService => MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync)
        ),
        new(
            LinkLocationsToolName,
            [GmToolPack.Full, GmToolPack.ExploreMap, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary],
            static toolService => MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync)
        ),
        new(
            MoveActorToolName,
            [GmToolPack.Full, GmToolPack.ExploreMap, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.InteractionEffectConsequence],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveActorAsync)
        ),
        new(
            CreateItemToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfConsequence, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync)
        ),
        new(
            CreateNpcToolName,
            [GmToolPack.Full, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.InteractionEffectConsequence],
            static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync)
        ),
        new(
            UpdateItemToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfConsequence, GmToolPack.ImmediateSelfAudit, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string?, string?>(toolService.UpdateItemAsync)
        ),
        new(
            MoveItemToActorToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfConsequence, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveItemToActorAsync)
        ),
        new(
            PlaceItemAtLocationToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfConsequence, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.PlaceItemAtLocationAsync)
        ),
        new(
            AddInteractionToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.InteractionAudit, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfAudit, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate(toolService.AddInteractionAsync)
        ),
        new(
            SetVisibilityToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfConsequence, GmToolPack.ImmediateSelfAudit, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync)
        ),
        new(
            SetInteractionVisibilityToolName,
            [GmToolPack.Full, GmToolPack.ImmediateSelf, GmToolPack.ExploreAudit, GmToolPack.InteractionConsequence, GmToolPack.InteractionAudit, GmToolPack.CollectedTurnCore, GmToolPack.CollectedTurnSummary, GmToolPack.ImmediateSelfAudit, GmToolPack.InteractionEffectConsequence, GmToolPack.InteractionEffectAudit],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetInteractionVisibilityAsync)
        ),
        new(
            SetActorResolutionToolName,
            [GmToolPack.Full, GmToolPack.CollectedTurnSummary],
            static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetActorResolutionAsync)
        ),
    ];

    internal static ToolExecutor CreateExecutor(DurableDict<string> root, GmToolPack pack) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(CreateTools(toolService, pack));
    }

    internal static IReadOnlyList<string> GetVisibleToolNames(GmToolPack pack) {
        return EnumerateRegistrations(pack)
            .Select(static registration => registration.Name)
            .ToArray();
    }

    internal static string FormatVisibleToolNames(GmToolPack pack) {
        return string.Join("、", GetVisibleToolNames(pack));
    }

    private static IEnumerable<ITool> CreateTools(GmWorldEditService toolService, GmToolPack pack) {
        foreach (var registration in EnumerateRegistrations(pack)) {
            yield return registration.Factory(toolService);
        }
    }

    private static IEnumerable<ToolRegistration> EnumerateRegistrations(GmToolPack pack) {
        foreach (var registration in s_registrations) {
            if (!registration.Packs.Contains(pack)) { continue; }

            yield return registration;
        }
    }
}

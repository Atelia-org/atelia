using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal enum GmToolPack {
    ExploreMap,
    ExploreAudit,
    InteractionConsequence,
    InteractionAudit,
    CollectedTurnCore,
    CollectedTurnSummary,
    ImmediateSelfConsequence,
    ImmediateSelfAudit,
    ImmediateSelfSummary,
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

    private static readonly IReadOnlyDictionary<string, Func<GmWorldEditService, ITool>> s_toolFactories =
        new Dictionary<string, Func<GmWorldEditService, ITool>>(StringComparer.Ordinal) {
            [CreateLocationToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync),
            [LinkLocationsToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync),
            [MoveActorToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveActorAsync),
            [CreateItemToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync),
            [CreateNpcToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync),
            [UpdateItemToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string?, string?>(toolService.UpdateItemAsync),
            [MoveItemToActorToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveItemToActorAsync),
            [PlaceItemAtLocationToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.PlaceItemAtLocationAsync),
            [AddInteractionToolName] = static toolService => MethodToolWrapper.FromDelegate(toolService.AddInteractionAsync),
            [SetVisibilityToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync),
            [SetInteractionVisibilityToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetInteractionVisibilityAsync),
            [SetActorResolutionToolName] = static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetActorResolutionAsync),
        };

    private static readonly IReadOnlyDictionary<GmToolPack, IReadOnlyList<string>> s_packToolNames =
        new Dictionary<GmToolPack, IReadOnlyList<string>> {
            [GmToolPack.ExploreMap] =
            [
                CreateLocationToolName,
                LinkLocationsToolName,
                MoveActorToolName,
            ],
            [GmToolPack.ExploreAudit] =
            [
                CreateItemToolName,
                CreateNpcToolName,
                UpdateItemToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.InteractionConsequence] =
            [
                MoveActorToolName,
                CreateItemToolName,
                CreateNpcToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.InteractionAudit] =
            [
                AddInteractionToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.CollectedTurnCore] =
            [
                CreateLocationToolName,
                LinkLocationsToolName,
                MoveActorToolName,
                CreateItemToolName,
                CreateNpcToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.CollectedTurnSummary] =
            [
                CreateLocationToolName,
                LinkLocationsToolName,
                MoveActorToolName,
                CreateItemToolName,
                CreateNpcToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
                SetActorResolutionToolName,
            ],
            [GmToolPack.ImmediateSelfConsequence] =
            [
                CreateItemToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                SetVisibilityToolName,
            ],
            [GmToolPack.ImmediateSelfAudit] =
            [
                UpdateItemToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.ImmediateSelfSummary] =
            [
                CreateItemToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.InteractionEffectConsequence] =
            [
                MoveActorToolName,
                CreateItemToolName,
                CreateNpcToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
            [GmToolPack.InteractionEffectAudit] =
            [
                CreateItemToolName,
                UpdateItemToolName,
                MoveItemToActorToolName,
                PlaceItemAtLocationToolName,
                AddInteractionToolName,
                SetVisibilityToolName,
                SetInteractionVisibilityToolName,
            ],
        };

    internal static ToolExecutor CreateExecutor(DurableDict<string> root, GmToolPack pack) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(CreateTools(toolService, pack));
    }

    internal static IReadOnlyList<string> GetVisibleToolNames(GmToolPack pack)
        => GetPackToolNames(pack);

    internal static string FormatVisibleToolNames(GmToolPack pack)
        => string.Join("、", GetVisibleToolNames(pack));

    private static IEnumerable<ITool> CreateTools(GmWorldEditService toolService, GmToolPack pack) {
        foreach (var toolName in GetPackToolNames(pack)) {
            yield return s_toolFactories[toolName](toolService);
        }
    }

    private static IReadOnlyList<string> GetPackToolNames(GmToolPack pack) {
        if (!s_packToolNames.TryGetValue(pack, out var toolNames)) {
            throw new ArgumentOutOfRangeException(nameof(pack), pack, "Unknown GM tool pack.");
        }

        return toolNames;
    }
}

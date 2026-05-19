using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal sealed class GmToolSet {
    internal GmToolSet(string name, IReadOnlyList<string> toolNames) {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Tool set name is required.", nameof(name))
            : name;
        ToolNames = toolNames ?? throw new ArgumentNullException(nameof(toolNames));
    }

    internal string Name { get; }

    internal IReadOnlyList<string> ToolNames { get; }
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

    private static readonly IReadOnlyList<string> s_mapTools =
    [
        CreateLocationToolName,
        LinkLocationsToolName,
        MoveActorToolName,
    ];

    private static readonly IReadOnlyList<string> s_entityRevealTools =
    [
        CreateItemToolName,
        CreateNpcToolName,
        UpdateItemToolName,
    ];

    private static readonly IReadOnlyList<string> s_itemPlacementTools =
    [
        MoveItemToActorToolName,
        PlaceItemAtLocationToolName,
    ];

    private static readonly IReadOnlyList<string> s_addInteractionTools =
    [
        AddInteractionToolName,
    ];

    private static readonly IReadOnlyList<string> s_interactionVisibilityTools =
    [
        SetInteractionVisibilityToolName,
    ];

    private static readonly IReadOnlyList<string> s_visibilityTools =
    [
        SetVisibilityToolName,
    ];

    private static readonly IReadOnlyList<string> s_actorResolutionTools =
    [
        SetActorResolutionToolName,
    ];

    internal static class ToolSets {
        internal static readonly GmToolSet ExploreMap = CreateToolSet(
            "ExploreMap",
            s_mapTools
        );

        internal static readonly GmToolSet ExploreAudit = CreateToolSet(
            "ExploreAudit",
            s_entityRevealTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet InteractionConsequence = CreateToolSet(
            "InteractionConsequence",
            [MoveActorToolName],
            s_entityRevealTools,
            s_itemPlacementTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet InteractionAudit = CreateToolSet(
            "InteractionAudit",
            s_addInteractionTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet CollectedTurnCore = CreateToolSet(
            "CollectedTurnCore",
            s_mapTools,
            s_entityRevealTools,
            s_itemPlacementTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet CollectedTurnSummary = CreateToolSet(
            "CollectedTurnSummary",
            CollectedTurnCore.ToolNames,
            s_actorResolutionTools
        );

        internal static readonly GmToolSet ImmediateSelfConsequence = CreateToolSet(
            "ImmediateSelfConsequence",
            [CreateItemToolName, UpdateItemToolName],
            s_itemPlacementTools,
            s_visibilityTools
        );

        internal static readonly GmToolSet ImmediateSelfAudit = CreateToolSet(
            "ImmediateSelfAudit",
            [UpdateItemToolName],
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet ImmediateSelfSummary = CreateToolSet(
            "ImmediateSelfSummary",
            [CreateItemToolName, UpdateItemToolName],
            s_itemPlacementTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet InteractionEffectConsequence = CreateToolSet(
            "InteractionEffectConsequence",
            [MoveActorToolName],
            s_entityRevealTools,
            s_itemPlacementTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );

        internal static readonly GmToolSet InteractionEffectAudit = CreateToolSet(
            "InteractionEffectAudit",
            [CreateItemToolName, UpdateItemToolName],
            s_itemPlacementTools,
            s_addInteractionTools,
            s_visibilityTools,
            s_interactionVisibilityTools
        );
    }

    internal static IReadOnlyList<GmToolSet> AllToolSets =>
    [
        ToolSets.ExploreMap,
        ToolSets.ExploreAudit,
        ToolSets.InteractionConsequence,
        ToolSets.InteractionAudit,
        ToolSets.CollectedTurnCore,
        ToolSets.CollectedTurnSummary,
        ToolSets.ImmediateSelfConsequence,
        ToolSets.ImmediateSelfAudit,
        ToolSets.ImmediateSelfSummary,
        ToolSets.InteractionEffectConsequence,
        ToolSets.InteractionEffectAudit,
    ];

    internal static ToolExecutor CreateExecutor(DurableDict<string> root, GmToolSet toolSet) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(CreateTools(toolService, toolSet));
    }

    internal static IReadOnlyList<string> GetVisibleToolNames(GmToolSet toolSet)
        => toolSet.ToolNames;

    internal static string FormatVisibleToolNames(GmToolSet toolSet)
        => string.Join("、", GetVisibleToolNames(toolSet));

    private static IEnumerable<ITool> CreateTools(GmWorldEditService toolService, GmToolSet toolSet) {
        foreach (var toolName in toolSet.ToolNames) {
            yield return s_toolFactories[toolName](toolService);
        }
    }

    private static GmToolSet CreateToolSet(string name, params IReadOnlyList<string>[] toolGroups) {
        var toolNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in toolGroups) {
            foreach (var toolName in group) {
                if (!s_toolFactories.ContainsKey(toolName)) {
                    throw new InvalidOperationException($"Unknown GM tool '{toolName}' in tool set '{name}'.");
                }

                if (seen.Add(toolName)) {
                    toolNames.Add(toolName);
                }
            }
        }

        return new GmToolSet(name, toolNames.ToArray());
    }
}

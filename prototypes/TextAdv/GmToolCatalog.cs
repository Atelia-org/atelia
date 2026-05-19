using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

[Flags]
internal enum GmToolProfile {
    Full = 1 << 0,
    ImmediateSelf = 1 << 1,
}

[Flags]
internal enum GmToolFacet {
    None = 0,
    Map = 1 << 0,
    ActorMovement = 1 << 1,
    EntityPresentation = 1 << 2,
    ItemTransfer = 1 << 3,
    InteractionLifecycle = 1 << 4,
    ActorResolution = 1 << 5,
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
        GmToolProfile Profiles,
        GmToolFacet Facets,
        Func<GmWorldEditService, ITool> Factory
    );

    private static readonly ToolRegistration[] s_registrations =
    [
        new(CreateLocationToolName, GmToolProfile.Full, GmToolFacet.Map, static toolService => MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync)),
        new(LinkLocationsToolName, GmToolProfile.Full, GmToolFacet.Map, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync)),
        new(MoveActorToolName, GmToolProfile.Full, GmToolFacet.ActorMovement, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveActorAsync)),
        new(CreateItemToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.EntityPresentation, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync)),
        new(CreateNpcToolName, GmToolProfile.Full, GmToolFacet.EntityPresentation, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync)),
        new(UpdateItemToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.EntityPresentation, static toolService => MethodToolWrapper.FromDelegate<string, string?, string?>(toolService.UpdateItemAsync)),
        new(MoveItemToActorToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.ItemTransfer, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveItemToActorAsync)),
        new(PlaceItemAtLocationToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.ItemTransfer, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.PlaceItemAtLocationAsync)),
        new(AddInteractionToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.InteractionLifecycle, static toolService => MethodToolWrapper.FromDelegate(toolService.AddInteractionAsync)),
        new(SetVisibilityToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.EntityPresentation, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync)),
        new(SetInteractionVisibilityToolName, GmToolProfile.Full | GmToolProfile.ImmediateSelf, GmToolFacet.InteractionLifecycle, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetInteractionVisibilityAsync)),
        new(SetActorResolutionToolName, GmToolProfile.Full, GmToolFacet.ActorResolution, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetActorResolutionAsync)),
    ];

    internal static ToolExecutor CreateExecutor(DurableDict<string> root, GmToolProfile profile) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(CreateTools(toolService, profile));
    }

    internal static IReadOnlyList<string> GetVisibleToolNames(
        GmToolProfile profile,
        GmToolFacet facets = GmToolFacet.None
    ) {
        return EnumerateRegistrations(profile, facets)
            .Select(static registration => registration.Name)
            .ToArray();
    }

    internal static string FormatVisibleToolNames(
        GmToolProfile profile,
        GmToolFacet facets = GmToolFacet.None
    ) {
        return string.Join("、", GetVisibleToolNames(profile, facets));
    }

    private static IEnumerable<ITool> CreateTools(GmWorldEditService toolService, GmToolProfile profile) {
        foreach (var registration in EnumerateRegistrations(profile)) {
            yield return registration.Factory(toolService);
        }
    }

    private static IEnumerable<ToolRegistration> EnumerateRegistrations(
        GmToolProfile profile,
        GmToolFacet facets = GmToolFacet.None
    ) {
        foreach (var registration in s_registrations) {
            if ((registration.Profiles & profile) == 0) {
                continue;
            }

            if (facets != GmToolFacet.None && (registration.Facets & facets) == 0) {
                continue;
            }

            yield return registration;
        }
    }
}

using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

[Flags]
internal enum GmToolProfile {
    Full = 1 << 0,
    ImmediateSelf = 1 << 1,
}

internal static class GmToolCatalog {
    private sealed record ToolRegistration(
        GmToolProfile Profiles,
        Func<GmWorldEditService, ITool> Factory
    );

    private static readonly ToolRegistration[] s_registrations =
    [
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync)),
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync)),
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string>(toolService.MovePlayerAsync)),
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveActorAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync)),
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string?, string?>(toolService.UpdateItemAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.MoveItemToActorAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.PlaceItemAtLocationAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate(toolService.AddInteractionAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync)),
        new(GmToolProfile.Full | GmToolProfile.ImmediateSelf, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetInteractionVisibilityAsync)),
        new(GmToolProfile.Full, static toolService => MethodToolWrapper.FromDelegate<string, string>(toolService.SetActorResolutionAsync)),
    ];

    internal static ToolExecutor CreateExecutor(DurableDict<string> root, GmToolProfile profile) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(CreateTools(toolService, profile));
    }

    internal static IReadOnlyList<string> GetVisibleToolNames(DurableDict<string> root, GmToolProfile profile) {
        return CreateExecutor(root, profile)
            .GetVisibleToolDefinitions()
            .Select(static definition => definition.Name)
            .ToArray();
    }

    private static IEnumerable<ITool> CreateTools(GmWorldEditService toolService, GmToolProfile profile) {
        foreach (var registration in s_registrations) {
            if ((registration.Profiles & profile) == 0) {
                continue;
            }

            yield return registration.Factory(toolService);
        }
    }
}

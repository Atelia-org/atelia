using Atelia.Agent.Core.History;

namespace Atelia.Agent.Core;

internal static class LlmProfileExtensions {
    public static CompletionDescriptor ToCompletionDescriptor(this LlmProfile profile) {
        if (profile is null) { throw new ArgumentNullException(nameof(profile)); }

        return new CompletionDescriptor(
            profile.Client.Name,
            profile.Client.ApiSpecId,
            profile.ModelId
        );
    }
}

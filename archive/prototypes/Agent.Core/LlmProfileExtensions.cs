using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

internal static class LlmProfileExtensions {
    public static CompletionDescriptor ToCompletionDescriptor(this LlmProfile profile) {
        if (profile is null) { throw new ArgumentNullException(nameof(profile)); }

        return CompletionDescriptor.From(profile.Client, profile.ModelId);
    }
}

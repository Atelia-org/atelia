using System;
using System.Collections.Generic;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Profile;

namespace Atelia.LiveContextProto.Provider;

internal sealed class ProviderRouter {
    public const string DefaultAnthropicStrategy = "anthropic-v1";

    private readonly Dictionary<string, LlmProfile> _profiles;

    public ProviderRouter(IEnumerable<LlmProfile> profiles) {
        _profiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles) {
            _profiles[profile.Name] = profile;
        }

        DebugUtil.Print("Provider", $"ProviderRouter initialized with profiles={_profiles.Count}");
    }

    public LlmProfile Resolve(string options) {
        if (!_profiles.TryGetValue(options, out var profile)) { throw new InvalidOperationException($"Unknown provider strategy '{options}'."); }

        DebugUtil.Print("Provider", $"[Router] Strategy={options} resolved to profile={profile.Name}, client={profile.Client.Name}, model={profile.ModelId}");

        return profile;
    }

    public static ProviderRouter CreateAnthropic(IProviderClient anthropicProvider, string model, string strategyName = DefaultAnthropicStrategy)
        => new(new[] {
            new LlmProfile(
                Client: anthropicProvider,
                ModelId: model,
                Name: strategyName
            )
        });
}

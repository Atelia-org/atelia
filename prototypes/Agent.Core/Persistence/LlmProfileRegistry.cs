using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 维护一组可用于恢复持久化 checkpoint 的 <see cref="LlmProfile"/>。
/// </summary>
/// <remarks>
/// 恢复键以 invocation identity（Provider / ApiSpec / Model）为主；
/// <see cref="LlmProfileCheckpoint.Name"/> 仅作说明性元数据，不参与主键匹配。
/// <see cref="LlmProfile.SoftContextTokenCap"/> 会参与兼容性校验，避免用不同窗口预算静默恢复同一 descriptor。
/// </remarks>
public sealed class LlmProfileRegistry {
    private readonly Dictionary<CompletionDescriptor, LlmProfile> _profiles = new();

    public LlmProfileRegistry() { }

    public LlmProfileRegistry(IEnumerable<LlmProfile> profiles) {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (var profile in profiles) {
            if (profile is null) { continue; }
            Register(profile);
        }
    }

    public int Count => _profiles.Count;

    public IReadOnlyCollection<LlmProfile> Profiles => _profiles.Values;

    public void Register(LlmProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        var descriptor = profile.ToCompletionDescriptor();
        if (_profiles.TryGetValue(descriptor, out var existing)
            && existing.SoftContextTokenCap != profile.SoftContextTokenCap) {
            throw new InvalidOperationException(
                $"LlmProfileRegistry already contains descriptor {DescribeDescriptor(descriptor)} with SoftContextTokenCap={existing.SoftContextTokenCap}, " +
                $"cannot register another profile with SoftContextTokenCap={profile.SoftContextTokenCap}."
            );
        }

        if (_profiles.TryGetValue(descriptor, out existing)
            && existing.SoftContextTokenCap == profile.SoftContextTokenCap
            && !Equals(existing.EffectiveCapabilities, profile.EffectiveCapabilities)) {
            throw new InvalidOperationException(
                $"LlmProfileRegistry already contains descriptor {DescribeDescriptor(descriptor)} with a different CapabilityProfile. " +
                "Registering multiple capability variants for the same descriptor+soft-cap is not allowed."
            );
        }

        _profiles[descriptor] = profile;
    }

    public bool TryResolve(LlmProfileCheckpoint checkpoint, out LlmProfile? profile) {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!_profiles.TryGetValue(checkpoint.ToCompletionDescriptor(), out var candidate)) {
            profile = null;
            return false;
        }

        if (candidate.SoftContextTokenCap != checkpoint.SoftContextTokenCap) {
            profile = null;
            return false;
        }

        profile = candidate;
        return true;
    }

    public LlmProfile ResolveOrThrow(LlmProfileCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!TryResolve(checkpoint, out var profile) || profile is null) {
            throw new InvalidOperationException(
                "No compatible LlmProfile is registered for checkpoint " +
                $"{{Provider={checkpoint.ProviderId}, ApiSpec={checkpoint.ApiSpecId}, Model={checkpoint.ModelId}, SoftCap={checkpoint.SoftContextTokenCap}}}."
            );
        }

        return profile;
    }

    internal LlmProfile? ResolveOrNull(LlmProfileCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return TryResolve(checkpoint, out var profile) ? profile : null;
    }

    private static string DescribeDescriptor(CompletionDescriptor descriptor)
        => $"{{Provider={descriptor.ProviderId}, ApiSpec={descriptor.ApiSpecId}, Model={descriptor.Model}}}";
}

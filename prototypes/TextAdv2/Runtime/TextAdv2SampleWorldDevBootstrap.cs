using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 收口 sample-world 这类开发态 bootstrap 流程，避免宿主主路径继续直接混用
/// “只打开既有 repo” 与 “必要时创建样例世界” 两种生命周期语义。
/// </summary>
public static class TextAdv2SampleWorldDevBootstrap {
    private const string LegacyRuntimeSidecarFileName = ".textadv2-runtime-state.json";
    private static readonly TextAdv2RuntimeOptions SampleWorldRuntimeOptions = new() {
        DefaultLandmarkProfileResolver = TryResolveDefaultLandmarkProfile,
    };

    public static TextAdv2Runtime CreateTemporaryRuntime()
        => CreateFreshRuntime(Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}"));

    public static TextAdv2Runtime CreateFreshRuntime(string repoDir)
        => TextAdv2Runtime.CreateNew(repoDir, CreateSampleWorld, SampleWorldRuntimeOptions);

    public static TextAdv2Runtime OpenOrCreateRuntime(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (!Directory.Exists(repoDir)) {
            return CreateFreshRuntime(repoDir);
        }

        string[] entries = Directory.EnumerateFileSystemEntries(repoDir).ToArray();
        string legacySidecarPath = Path.Combine(repoDir, LegacyRuntimeSidecarFileName);
        bool containsOnlyLegacySidecar = entries.Length > 0
            && entries.All(entry => string.Equals(entry, legacySidecarPath, StringComparison.Ordinal));

        if (containsOnlyLegacySidecar) {
            File.Delete(legacySidecarPath);
            return CreateFreshRuntime(repoDir);
        }

        return entries.Length > 0
            ? TextAdv2Runtime.OpenExisting(repoDir, SampleWorldRuntimeOptions)
            : CreateFreshRuntime(repoDir);
    }

    public static TextAdv2Runtime ResetRuntime(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }

        return CreateFreshRuntime(repoDir);
    }

    public static TextAdv2RouteAccelerationObservation RebuildRouteAcceleration(
        TextAdv2Runtime runtime,
        string? requestedLandmarks = null
    ) {
        ArgumentNullException.ThrowIfNull(runtime);

        if (string.IsNullOrWhiteSpace(requestedLandmarks) || string.Equals(requestedLandmarks, "default", StringComparison.OrdinalIgnoreCase)) {
            var defaultProfile = runtime.ResolveDefaultLandmarkProfile();
            if (defaultProfile is null) {
                throw new InvalidOperationException(
                    "RebuildRouteAcceleration without an explicit landmark list requires a world with a known recommended landmark profile."
                );
            }

            return runtime.RebuildRouteAcceleration(defaultProfile.LandmarkLocationIds, defaultProfile.ProfileName);
        }

        return runtime.RebuildRouteAcceleration(requestedLandmarks);
    }

    private static WorldState CreateSampleWorld(Revision revision) {
        var world = TestWorldBuilder.Create(revision);
        TestWorldBuilder.PopulateSampleActors(world);
        return world;
    }

    private static TextAdv2DefaultLandmarkProfile? TryResolveDefaultLandmarkProfile(WorldState world) {
        return TestWorldBuilder.TryGetRecommendedLandmarkLocationIds(world, out var recommendedLandmarkLocationIds)
            ? new TextAdv2DefaultLandmarkProfile(
                TestWorldBuilder.RecommendedLandmarkProfileName,
                recommendedLandmarkLocationIds
            )
            : null;
    }
}

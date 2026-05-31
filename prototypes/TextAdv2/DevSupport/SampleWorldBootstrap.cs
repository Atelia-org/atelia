using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;
using Atelia.TextAdv2.Session;

namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// 收口 sample-world 这类开发态 bootstrap 流程，避免宿主主路径继续直接混用
/// “只打开既有 repo” 与 “必要时创建样例世界” 两种生命周期语义。
/// </summary>
public static class SampleWorldBootstrap {
    private const string LegacySessionSidecarFileName = ".textadv2-runtime-state.json";
    private static readonly WorldSessionOptions SampleWorldSessionOptions = new() {
        LandmarkProfileResolver = TryResolveLandmarkProfile,
    };

    public static WorldSession CreateTemporarySession()
        => CreateFreshSession(Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}"));

    public static WorldSession CreateFreshSession(string repoDir)
        => WorldSession.CreateNew(repoDir, CreateSampleWorld, SampleWorldSessionOptions);

    public static WorldSession OpenOrCreateSession(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (!Directory.Exists(repoDir)) { return CreateFreshSession(repoDir); }

        string[] entries = Directory.EnumerateFileSystemEntries(repoDir).ToArray();
        string legacySidecarPath = Path.Combine(repoDir, LegacySessionSidecarFileName);
        bool containsOnlyLegacySidecar = entries.Length > 0
            && entries.All(entry => string.Equals(entry, legacySidecarPath, StringComparison.Ordinal));

        if (containsOnlyLegacySidecar) {
            File.Delete(legacySidecarPath);
            return CreateFreshSession(repoDir);
        }

        return entries.Length > 0
            ? WorldSession.OpenExisting(repoDir, SampleWorldSessionOptions)
            : CreateFreshSession(repoDir);
    }

    public static WorldSession ResetSession(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }

        return CreateFreshSession(repoDir);
    }

    public static RouteAccelerationSnapshot RebuildRouteAcceleration(
        WorldSession session,
        string? requestedLandmarks = null
    ) {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(requestedLandmarks) || string.Equals(requestedLandmarks, "default", StringComparison.OrdinalIgnoreCase)) {
            var defaultProfile = session.ResolveLandmarkProfile();
            if (defaultProfile is null) {
                throw new InvalidOperationException(
                    "RebuildRouteAcceleration without an explicit landmark list requires a world with a known recommended landmark profile."
                );
            }

            return session.RebuildRouteAcceleration(defaultProfile.LandmarkLocationIds, defaultProfile.ProfileName);
        }

        return session.RebuildRouteAcceleration(requestedLandmarks);
    }

    private static WorldState CreateSampleWorld(Revision revision) {
        var world = TestWorldBuilder.Create(revision);
        TestWorldBuilder.PopulateSampleActors(world);
        return world;
    }

    private static LandmarkProfile? TryResolveLandmarkProfile(WorldState world) {
        return TestWorldBuilder.TryGetRecommendedLandmarkLocationIds(world, out var recommendedLandmarkLocationIds)
            ? new LandmarkProfile(
                TestWorldBuilder.RecommendedLandmarkProfileName,
                recommendedLandmarkLocationIds
            )
            : null;
    }
}

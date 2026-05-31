using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;
using Atelia.TextAdv2.Session;

namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// 收口 sample-world 这类开发态 bootstrap 流程，避免宿主主路径继续直接混用
/// “只打开既有 repo” 与 “必要时创建样例世界” 两种生命周期语义。
/// </summary>
public static class SampleWorldBootstrap {
    private sealed record RecommendedLandmarkProfile(
        string ProfileName,
        IReadOnlyList<string> LandmarkLocationIds
    );

    public static WorldSession CreateTemporarySession()
        => CreateFreshSession(Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}"));

    public static WorldSession CreateFreshSession(string repoDir)
        => WorldSession.CreateNew(repoDir, CreateSampleWorld);

    public static WorldSession OpenOrCreateSession(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (!Directory.Exists(repoDir)) { return CreateFreshSession(repoDir); }

        return Directory.EnumerateFileSystemEntries(repoDir).Any()
            ? WorldSession.OpenExisting(repoDir)
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
            var defaultProfile = TryResolveLandmarkProfile(session);
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

    private static RecommendedLandmarkProfile? TryResolveLandmarkProfile(WorldSession session) {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetRecommendedLandmarkLocationIdsForDevSupport(out var recommendedLandmarkLocationIds)
            ? new RecommendedLandmarkProfile(
                TestWorldBuilder.RecommendedLandmarkProfileName,
                recommendedLandmarkLocationIds
            )
            : null;
    }
}

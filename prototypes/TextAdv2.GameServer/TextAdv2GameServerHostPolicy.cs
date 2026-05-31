using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class TextAdv2GameServerHostPolicy {
    private const int MaxOpenRetryCount = 5;

    private TextAdv2GameServerHostPolicy(
        string configuredRepoDir,
        string resolvedRepoDir,
        string bootstrapMode,
        string runtimeOpenMode,
        bool sampleWorldResetEnabled,
        Func<TextAdv2Runtime> openRuntime,
        Func<TextAdv2Runtime>? resetRuntime,
        string[] notes
    ) {
        ConfiguredRepoDir = configuredRepoDir;
        ResolvedRepoDir = resolvedRepoDir;
        BootstrapMode = bootstrapMode;
        RuntimeOpenMode = runtimeOpenMode;
        SampleWorldResetEnabled = sampleWorldResetEnabled;
        _openRuntime = openRuntime;
        _resetRuntime = resetRuntime;
        Notes = notes;
        PlannedEndpoints = BuildPlannedEndpoints(sampleWorldResetEnabled);
    }

    private readonly Func<TextAdv2Runtime> _openRuntime;
    private readonly Func<TextAdv2Runtime>? _resetRuntime;

    public string ConfiguredRepoDir { get; }

    public string ResolvedRepoDir { get; }

    public string BootstrapMode { get; }

    public string RuntimeOpenMode { get; }

    public bool SampleWorldResetEnabled { get; }

    public bool RepositoryLockRetryEnabled => true;

    public int RepositoryLockRetryCount => MaxOpenRetryCount;

    public string[] PlannedEndpoints { get; }

    public string[] Notes { get; }

    public static TextAdv2GameServerHostPolicy Create(
        string configuredRepoDir,
        string resolvedRepoDir,
        bool autoBootstrapSampleWorld
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRepoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedRepoDir);

        return autoBootstrapSampleWorld
            ? new TextAdv2GameServerHostPolicy(
                configuredRepoDir,
                resolvedRepoDir,
                bootstrapMode: "sample-world-dev",
                runtimeOpenMode: "open-or-create-sample-world",
                sampleWorldResetEnabled: true,
                openRuntime: () => TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(resolvedRepoDir),
                resetRuntime: () => TextAdv2SampleWorldDevBootstrap.ResetRuntime(resolvedRepoDir),
                notes: [
                    "startup 通过 TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime 打开 runtime。",
                    "POST /admin/reset-sample-world 只在 sample-world-dev host policy 下映射。"
                ]
            )
            : new TextAdv2GameServerHostPolicy(
                configuredRepoDir,
                resolvedRepoDir,
                bootstrapMode: "open-existing-only",
                runtimeOpenMode: "open-existing-only",
                sampleWorldResetEnabled: false,
                openRuntime: () => TextAdv2Runtime.OpenExisting(resolvedRepoDir),
                resetRuntime: null,
                notes: [
                    "startup 只允许打开既有 repo，不会隐式创建 sample world。",
                    "POST /admin/reset-sample-world 在 open-existing-only host policy 下不会映射。"
                ]
            );
    }

    public TextAdv2Runtime OpenRuntime()
        => OpenWithRepositoryLockRetry(_openRuntime);

    public TextAdv2Runtime ResetRuntime() {
        if (_resetRuntime is null) {
            throw new InvalidOperationException("Sample-world reset is not enabled for the current GameServer host policy.");
        }

        return _resetRuntime();
    }

    private static TextAdv2Runtime OpenWithRepositoryLockRetry(Func<TextAdv2Runtime> openRuntime) {
        for (int attempt = 0; ; attempt++) {
            try {
                return openRuntime();
            }
            catch (InvalidOperationException ex) when (attempt < MaxOpenRetryCount && IsRepositoryLockFailure(ex)) {
                // Local host restarts can briefly overlap while the previous instance is still releasing the repo lock.
                Thread.Sleep((attempt + 1) * 50);
            }
        }
    }

    private static string[] BuildPlannedEndpoints(bool sampleWorldResetEnabled) {
        var endpoints = new List<string> {
            "GET /admin/world",
            "GET /admin/time",
            "POST /admin/advance-time/{ticks}",
            "GET /admin/route-acceleration",
            "POST /admin/route-acceleration/rebuild?landmarks=<locationId[,locationId...]|default>",
            "GET /admin/locations/{locationId}",
            "GET /admin/locations/{locationId}/observation",
            "GET /admin/locations/{locationId}/navigation",
            "GET /admin/routes/{fromLocationId}/{toLocationId}",
            "GET /actors/{actorId}/observation",
            "GET /actors/{actorId}/navigation",
            "POST /actors/{actorId}/moves/{passageId}",
            "GET /actors/{actorId}/route-trace",
            "GET /actors/{actorId}/plan-route/{toLocationId}",
        };

        if (sampleWorldResetEnabled) {
            endpoints.Insert(5, "POST /admin/reset-sample-world");
        }

        return [.. endpoints];
    }

    private static bool IsRepositoryLockFailure(InvalidOperationException ex)
        => ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal);
}

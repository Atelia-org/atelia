using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class GameServerHostPolicy {
    private const int MaxOpenRetryCount = 5;
    internal const string SampleWorldDevBootstrapMode = "sample-world-dev";
    internal const string OpenExistingOnlyBootstrapMode = "open-existing-only";
    private static readonly string[] SupportedBootstrapModes = [SampleWorldDevBootstrapMode, OpenExistingOnlyBootstrapMode];

    private GameServerHostPolicy(
        string configuredRepoDir,
        string resolvedRepoDir,
        string bootstrapMode,
        string runtimeOpenMode,
        bool sampleWorldResetEnabled,
        Func<SerialWorldRuntime> openRuntime,
        Func<SerialWorldRuntime>? resetRuntime,
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

    private readonly Func<SerialWorldRuntime> _openRuntime;
    private readonly Func<SerialWorldRuntime>? _resetRuntime;

    public string ConfiguredRepoDir { get; }

    public string ResolvedRepoDir { get; }

    public string BootstrapMode { get; }

    public string RuntimeOpenMode { get; }

    public bool SampleWorldResetEnabled { get; }

    public bool RepositoryLockRetryEnabled => true;

    public int RepositoryLockRetryCount => MaxOpenRetryCount;

    public string[] PlannedEndpoints { get; }

    public string[] Notes { get; }

    public static GameServerHostPolicy Create(
        string configuredRepoDir,
        string resolvedRepoDir,
        string bootstrapMode
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRepoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedRepoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapMode);

        return bootstrapMode switch {
            SampleWorldDevBootstrapMode => new GameServerHostPolicy(
                configuredRepoDir,
                resolvedRepoDir,
                bootstrapMode: SampleWorldDevBootstrapMode,
                runtimeOpenMode: "open-or-create-sample-world",
                sampleWorldResetEnabled: true,
                openRuntime: () => SampleWorldBootstrap.OpenOrCreateSession(resolvedRepoDir),
                resetRuntime: () => SampleWorldBootstrap.ResetSession(resolvedRepoDir),
                notes: [
                    "startup 通过 SampleWorldBootstrap.OpenOrCreateSession 打开 runtime。",
                    "POST /admin/reset-sample-world 只在 sample-world-dev host policy 下映射。"
                ]
            )
            ,
            OpenExistingOnlyBootstrapMode => new GameServerHostPolicy(
                configuredRepoDir,
                resolvedRepoDir,
                bootstrapMode: OpenExistingOnlyBootstrapMode,
                runtimeOpenMode: "open-existing-only",
                sampleWorldResetEnabled: false,
                openRuntime: () => SerialWorldRuntime.OpenExisting(resolvedRepoDir),
                resetRuntime: null,
                notes: [
                    "startup 只允许打开既有 repo，不会隐式创建 sample world。",
                    "POST /admin/reset-sample-world 在 open-existing-only host policy 下不会映射。"
                ]
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported GameServer bootstrap mode '{bootstrapMode}'. Allowed values: {string.Join(", ", SupportedBootstrapModes)}."
            ),
        };
    }

    public SerialWorldRuntime OpenRuntime()
        => OpenWithRepositoryLockRetry(_openRuntime);

    public SerialWorldRuntime ResetRuntime() {
        if (_resetRuntime is null) { throw new InvalidOperationException("Sample-world reset is not enabled for the current GameServer host policy."); }

        return _resetRuntime();
    }

    private static SerialWorldRuntime OpenWithRepositoryLockRetry(Func<SerialWorldRuntime> openRuntime) {
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
            "GET /",
            "GET /healthz",
            "GET /admin/runtime-status",
            "GET /admin/world",
            "GET /admin/time",
            "POST /admin/locations",
            "POST /admin/actors",
            "POST /admin/passages",
            "POST /admin/advance-time/{ticks}",
            "GET /admin/route-acceleration",
            "POST /admin/route-acceleration/rebuild?landmarks=<locationId[,locationId...]|default>",
            "GET /admin/locations/{locationId}",
            "GET /admin/locations/{locationId}/observation",
            "GET /admin/locations/{locationId}/navigation",
            "GET /admin/routes/{fromLocationId}/{toLocationId}",
            "GET /actors/{actorId}/observation",
            "GET /actors/{actorId}/context",
            "GET /actors/{actorId}/navigation",
            "POST /actors/{actorId}/moves/{passageId}",
            "GET /actors/{actorId}/runtime-route-trace",
            "GET /actors/{actorId}/runtime-route-trace/json",
            "GET /actors/{actorId}/plan-route/{toLocationId}",
        };

        if (sampleWorldResetEnabled) {
            endpoints.Insert(8, "POST /admin/reset-sample-world");
        }

        return [.. endpoints];
    }

    private static bool IsRepositoryLockFailure(InvalidOperationException ex)
        => ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal);
}

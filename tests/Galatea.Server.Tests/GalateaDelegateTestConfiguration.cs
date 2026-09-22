namespace Atelia.Galatea.Server.Tests;

internal static class GalateaDelegateTestConfiguration {
    internal static IReadOnlyList<GalateaPlayerConfig> Players { get; } = Array.AsReadOnly(new[] {
        new GalateaPlayerConfig("player-main", new Atelia.Galatea.Prompts.GalateaPlayerName("刘世超"), "pw")
    });
    internal static GalateaSenderSnapshot PlayerSender => GalateaSenderSnapshot.Player(Players[0]);

    internal static string CreateHomeDirectory(string sessionDirectory, string characterId) {
        string home = Path.IsPathFullyQualified(sessionDirectory)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)) + "-home-" + characterId
            : sessionDirectory + "-home-" + characterId;
        // Synthetic repository fixtures use /tmp or /dev/shm. Normalize first:
        // appending a suffix to a trailing '/.' would create the session itself.
        if (Path.IsPathFullyQualified(home)
            && (home.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)
                || home.StartsWith("/dev/shm/", StringComparison.Ordinal))) {
            Directory.CreateDirectory(home);
        }
        return home;
    }

    internal static GalateaDelegateConfig Create(string? allowedRoot = null) {
        string effectiveRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(allowedRoot ?? Path.GetPathRoot(Path.GetTempPath())!)
        );
        string processPath = Path.GetFullPath(
            Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The test process executable is unavailable."
                )
        );
        string executable = new FileInfo(processPath)
            .ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? processPath;
        string entryPoint = Path.GetFullPath(
            typeof(GalateaDelegateTestConfiguration).Assembly.Location
        );
        return new GalateaDelegateConfig(
            new GalateaDelegateSidecarConfig(
                executable,
                entryPoint,
                executable,
                CodexHome: effectiveRoot,
                RpcTimeoutMs: 1_000,
                ShutdownGraceMs: 100,
                MaximumFrameUtf8Bytes: 1_048_576
            ),
            [effectiveRoot],
            [new GalateaDelegateRouteConfig(
                GalateaDelegateConfigReader.CanonicalRecipient,
                GalateaDelegateConfigReader.CodexAppServerKind,
                CodexConfig: null,
                MaximumQueuedMails: 16,
                MaximumTaskUtf8Bytes: 100_000,
                MaximumReplyUtf8Bytes: 100_000,
                MaximumInboxReplies: 16,
                MaximumInboxUtf8Bytes: 1_048_576
            )]
        );
    }
}

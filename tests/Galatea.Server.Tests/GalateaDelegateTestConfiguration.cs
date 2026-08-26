namespace Atelia.Galatea.Server.Tests;

internal static class GalateaDelegateTestConfiguration {
    internal static GalateaDelegateConfig Create(string? cwd = null) {
        string effectiveCwd = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(cwd ?? Path.GetTempPath())
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
                RpcTimeoutMs: 1_000,
                TurnTimeoutMs: 1_000,
                ShutdownGraceMs: 100,
                MaximumFrameUtf8Bytes: 1_048_576
            ),
            [effectiveCwd],
            [new GalateaDelegateRouteConfig(
                GalateaDelegateConfigReader.CanonicalRecipient,
                GalateaDelegateConfigReader.CodexAppServerKind,
                effectiveCwd,
                GalateaDelegateMode.Work,
                Network: false,
                MaximumQueuedMails: 16,
                MaximumTaskUtf8Bytes: 100_000,
                MaximumReplyUtf8Bytes: 100_000,
                MaximumInboxReplies: 16,
                MaximumInboxUtf8Bytes: 1_048_576
            )]
        );
    }
}

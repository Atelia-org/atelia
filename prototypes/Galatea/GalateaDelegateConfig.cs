using System.Text.Json;

namespace Atelia.Galatea.Server;

public sealed record GalateaDelegateConfig(
    GalateaDelegateSidecarConfig Sidecar,
    IReadOnlyList<string> AllowedRoots,
    IReadOnlyList<GalateaDelegateRouteConfig> Routes
) {
    public GalateaDelegateRouteConfig CodexRoute => Routes[0];
}

/// <summary>
/// Configures the durable Codex sidecar process and its control-plane bounds.
/// A delegated Codex turn intentionally has no elapsed-time deadline and is
/// never interrupted merely because time passes.
/// </summary>
/// <param name="RpcTimeoutMs">
/// Bounds one sidecar/app-server control RPC wait; it does not bound the
/// lifetime of an accepted delegated Codex turn.
/// </param>
/// <param name="ShutdownGraceMs">
/// Bounds child-process reap waits only after sidecar shutdown has begun.
/// </param>
public sealed record GalateaDelegateSidecarConfig(
    string NodeCommand,
    string EntryPoint,
    string CodexCommand,
    int RpcTimeoutMs,
    int ShutdownGraceMs,
    int MaximumFrameUtf8Bytes
);

public sealed record GalateaDelegateRouteConfig(
    string Recipient,
    string Kind,
    IReadOnlyDictionary<string, JsonElement>? CodexConfig,
    int MaximumQueuedMails,
    int MaximumTaskUtf8Bytes,
    int MaximumReplyUtf8Bytes,
    int MaximumInboxReplies,
    int MaximumInboxUtf8Bytes
);

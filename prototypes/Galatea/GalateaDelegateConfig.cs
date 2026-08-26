using System.Text.Json.Serialization;

namespace Atelia.Galatea.Server;

public sealed record GalateaDelegateConfig(
    GalateaDelegateSidecarConfig Sidecar,
    IReadOnlyList<string> AllowedRoots,
    IReadOnlyList<GalateaDelegateRouteConfig> Routes
) {
    public GalateaDelegateRouteConfig CodexRoute => Routes[0];
}

public sealed record GalateaDelegateSidecarConfig(
    string NodeCommand,
    string EntryPoint,
    string CodexCommand,
    int RpcTimeoutMs,
    int TurnTimeoutMs,
    int ShutdownGraceMs,
    int MaximumFrameUtf8Bytes
);

public sealed record GalateaDelegateRouteConfig(
    string Recipient,
    string Kind,
    string Cwd,
    GalateaDelegateMode Mode,
    bool Network,
    int MaximumQueuedMails,
    int MaximumTaskUtf8Bytes,
    int MaximumReplyUtf8Bytes,
    int MaximumInboxReplies,
    int MaximumInboxUtf8Bytes
);

[JsonConverter(typeof(JsonStringEnumConverter<GalateaDelegateMode>))]
public enum GalateaDelegateMode {
    [JsonStringEnumMemberName("research")]
    Research,
    [JsonStringEnumMemberName("work")]
    Work
}

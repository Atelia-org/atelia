namespace Atelia.MemoPod;

internal sealed record MemoPodLifecycleTestHooks(
    Action<MemoPodDocument>? BeforeRender = null,
    Action<MemoPodDocument>? AfterRenderBeforePublish = null,
    MemoPodPublisherTestHooks? PublisherHooks = null
) {
    internal static MemoPodLifecycleTestHooks None { get; } = new();
}

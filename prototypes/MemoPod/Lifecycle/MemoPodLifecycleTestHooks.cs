namespace Atelia.MemoPod;

internal sealed record MemoPodLifecycleTestHooks(
    Action<MemoPodDocument>? BeforeRender = null,
    Action<MemoPodDocument>? AfterCaptureBeforePublish = null,
    MemoPodPublisherTestHooks? PublisherHooks = null,
    Func<MemoPodDocument, MemoPodFrozenPrompt>? RenderPrompt = null
) {
    internal static MemoPodLifecycleTestHooks None { get; } = new();
}

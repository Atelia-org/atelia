using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

/// <summary>
/// 单次完整重写版 world-understanding maintainer：一次 completion 内融入新经历并重写整份世界理解文档全文，
/// 不使用编辑工具（依赖传入的空 <see cref="ToolSession"/> 达成单轮输出）。
/// 目标 block 位于 <see cref="MemoryPackCarrier.Observation"/>，最终渲染为 Observation 的一部分——
/// 与自传版渲染到 Action 相对，利用信息在上下文不同位置的语义先验。
/// </summary>
public sealed class WorldUnderstandingRewriteMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.world-understanding.rewrite";

    private readonly CompletionMemoryBlockMaintainer _inner;

    public WorldUnderstandingRewriteMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        ToolSession toolSession,
        MemoryRewritePromptSet? prompts = null
    ) {
        prompts ??= WorldUnderstandingRewritePrompts.Default;
        _inner = new CompletionMemoryBlockMaintainer(
            DefaultId,
            RolePlayMemoryBlockPaths.WorldUnderstanding,
            completionClient,
            modelId,
            prompts.SystemPrompt,
            prompts.UserPrompt,
            toolSession,
            includeOldBlockInPrompt: false
        );
    }

    public string Id => _inner.Id;

    public MemoryPackBlockPath Target => _inner.Target;

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        var result = await _inner.MaintainAsync(request, ct).ConfigureAwait(false);
        return NormalizeResult(result);
    }

    private static MemoryBlockMaintenanceResult NormalizeResult(MemoryBlockMaintenanceResult result)
        => result with {
            NewBlock = new MemoryPackBlock(MemoryBlockTextNormalizer.NormalizeBlockText(result.NewBlock.Text))
        };
}

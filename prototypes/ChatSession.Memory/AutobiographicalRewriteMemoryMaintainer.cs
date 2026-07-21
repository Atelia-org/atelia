using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

/// <summary>
/// 单次完整重写版自传 maintainer：一次 completion 内同时融入新经历并重写整份自传全文，
/// 不使用编辑工具（依赖传入的空 <see cref="ToolSession"/> 达成单轮输出）。
/// 与两阶段编辑 Agent 版 <see cref="AutobiographicalMemoryMaintainer"/> 并存。
/// </summary>
public sealed class AutobiographicalRewriteMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography.rewrite";

    private readonly CompletionMemoryBlockMaintainer _inner;

    public AutobiographicalRewriteMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        ToolSession toolSession,
        MemoryRewritePromptSet? prompts = null
    ) {
        prompts ??= AutobiographicalRewritePrompts.Default;
        _inner = new CompletionMemoryBlockMaintainer(
            DefaultId,
            RolePlayMemoryBlockPaths.FirstPersonAutobiography,
            completionClient,
            modelId,
            prompts.SystemPrompt,
            prompts.UserPrompt,
            toolSession
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

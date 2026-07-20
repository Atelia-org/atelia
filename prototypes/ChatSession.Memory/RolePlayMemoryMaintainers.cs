using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

public sealed class WorldUnderstandingMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.world-understanding";

    private readonly CompletionMemoryBlockMaintainer _inner;

    public WorldUnderstandingMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        ToolSession toolSession,
        string? systemPrompt = null,
        string? userPrompt = null
    ) {
        _inner = new CompletionMemoryBlockMaintainer(
            DefaultId,
            RolePlayMemoryBlockPaths.WorldUnderstanding,
            completionClient,
            modelId,
            systemPrompt ?? RolePlayMemoryMaintainerPrompts.SharedSystemPrompt,
            userPrompt ?? RolePlayMemoryMaintainerPrompts.WorldUnderstandingUserPrompt,
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

public sealed class FirstPersonAutobiographyMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography";

    private readonly CompletionMemoryBlockMaintainer _inner;

    public FirstPersonAutobiographyMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        ToolSession toolSession,
        string? systemPrompt = null,
        string? userPrompt = null
    ) {
        _inner = new CompletionMemoryBlockMaintainer(
            DefaultId,
            RolePlayMemoryBlockPaths.FirstPersonAutobiography,
            completionClient,
            modelId,
            systemPrompt ?? RolePlayMemoryMaintainerPrompts.SharedSystemPrompt,
            userPrompt ?? RolePlayMemoryMaintainerPrompts.FirstPersonAutobiographyUserPrompt,
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

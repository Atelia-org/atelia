namespace Atelia.MutableContextAgentProto.Core;

public sealed class SingleUserContextRendererOptions {
    public int MaxRecentActions { get; init; } = 12;
    public int MaxMemories { get; init; } = 24;
    public string NextStepInstruction { get; init; } =
        "请为了最初的目标，继续你的思路和行动。必要时调用工具；如果信息不足，先收集信息。";
}

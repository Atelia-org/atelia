namespace Atelia.TextAdv2.Gym;

/// <summary>
/// Agent 在一个 turn 中声明的高层动作意图。
/// Host 先收集 intent，再统一裁决是否转成可执行 world effect。
/// </summary>
public abstract record AgentActionIntent;

public sealed record KeepAgentActionIntent : AgentActionIntent {
    public static KeepAgentActionIntent Instance { get; } = new();
}

public sealed record CancelCurrentProcessAgentActionIntent : AgentActionIntent {
    public static CancelCurrentProcessAgentActionIntent Instance { get; } = new();
}

public sealed record MoveAgentActionIntent : AgentActionIntent {
    public MoveAgentActionIntent(string passageId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);
        PassageId = passageId;
    }

    public string PassageId { get; }
}

public sealed record StartRouteFollowingAgentActionIntent : AgentActionIntent {
    public StartRouteFollowingAgentActionIntent(string destinationLocationId, bool isInterruptible = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationLocationId);

        DestinationLocationId = destinationLocationId;
        IsInterruptible = isInterruptible;
    }

    public string DestinationLocationId { get; }

    public bool IsInterruptible { get; }
}

public sealed record StartMiningAgentActionIntent : AgentActionIntent {
    public StartMiningAgentActionIntent(string worksiteId, bool isInterruptible = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(worksiteId);

        WorksiteId = worksiteId;
        IsInterruptible = isInterruptible;
    }

    public string WorksiteId { get; }

    public bool IsInterruptible { get; }
}

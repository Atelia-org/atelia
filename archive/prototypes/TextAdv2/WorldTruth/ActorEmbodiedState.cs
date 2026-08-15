namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// Actor 在 world truth 中的具身执行态。
///
/// 它用来钉住一个边界：
/// - 会驱动世界持续演化的 process，应进入 world truth；
/// - goal / memory / budget / prompt context 不属于这里。
/// </summary>
public abstract record ActorEmbodiedState {
    public static IdleActorEmbodiedState Idle { get; } = new();
}

public sealed record IdleActorEmbodiedState : ActorEmbodiedState;

public abstract record ActorActiveProcessState : ActorEmbodiedState {
    protected ActorActiveProcessState(string processKind, bool isInterruptible) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKind);

        ProcessKind = processKind;
        IsInterruptible = isInterruptible;
    }

    public string ProcessKind { get; }

    public bool IsInterruptible { get; }
}

/// <summary>
/// draft shape：actor 已开始一条长于单步 passage traversal 的导航执行过程。
/// </summary>
public sealed record RouteFollowingActorProcessState : ActorActiveProcessState {
    public RouteFollowingActorProcessState(
        string destinationLocationId,
        IReadOnlyList<string> remainingPassageIds,
        int remainingTravelTicksOnCurrentLeg,
        bool isInterruptible = true
    )
        : base("route-following", isInterruptible) {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationLocationId);
        ArgumentNullException.ThrowIfNull(remainingPassageIds);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingTravelTicksOnCurrentLeg);

        DestinationLocationId = destinationLocationId;
        RemainingPassageIds = remainingPassageIds;
        RemainingTravelTicksOnCurrentLeg = remainingTravelTicksOnCurrentLeg;
    }

    public string DestinationLocationId { get; }

    public IReadOnlyList<string> RemainingPassageIds { get; }

    public int RemainingTravelTicksOnCurrentLeg { get; }
}

/// <summary>
/// draft shape：actor 正在一个地点/节点上做周期性采集工作。
/// </summary>
public sealed record MiningActorProcessState : ActorActiveProcessState {
    public MiningActorProcessState(
        string worksiteId,
        int progressTicksInCurrentCycle,
        int ticksPerYield,
        string yieldItemId,
        long producedYieldCount = 0,
        int yieldAmount = 1,
        bool isInterruptible = true
    )
        : base("mining", isInterruptible) {
        ArgumentException.ThrowIfNullOrWhiteSpace(worksiteId);
        ArgumentOutOfRangeException.ThrowIfNegative(progressTicksInCurrentCycle);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerYield, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(yieldItemId);
        ArgumentOutOfRangeException.ThrowIfNegative(producedYieldCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(yieldAmount, 1);

        WorksiteId = worksiteId;
        ProgressTicksInCurrentCycle = progressTicksInCurrentCycle;
        TicksPerYield = ticksPerYield;
        YieldItemId = yieldItemId;
        ProducedYieldCount = producedYieldCount;
        YieldAmount = yieldAmount;
    }

    public string WorksiteId { get; }

    public int ProgressTicksInCurrentCycle { get; }

    public int TicksPerYield { get; }

    public string YieldItemId { get; }

    public long ProducedYieldCount { get; }

    public int YieldAmount { get; }
}

using System.Text.Json.Serialization;
using Atelia.TextAdv2.Observation;

namespace Atelia.TextAdv2.Runtime;

public sealed record BatchObserveRequest {
    public BatchObserveItem[] Items { get; init; } = [];
}

public sealed record BatchObserveItem {
    public required string RequestId { get; init; }

    public required string Kind { get; init; }

    public string? ActorId { get; init; }

    public string? LocationId { get; init; }
}

public sealed record BatchObserveResult {
    public BatchObserveResultItem[] Items { get; init; } = [];
}

public sealed record BatchObserveResultItem {
    public required string RequestId { get; init; }

    public required string Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorLocationObservation? Actor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorContextObservation? ActorContext { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorNavigationObservation? ActorNavigation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocationObservation? Location { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocationNavigationObservation? LocationNavigation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogicalTimeSnapshot? Time { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BatchRuntimeError? Error { get; init; }
}

public sealed record BatchStepRequest {
    public BatchStepCommand[] Steps { get; init; } = [];

    public long AdvanceTimeAfterBatchTicks { get; init; }

    public BatchObserveItem[]? PostObservations { get; init; }
}

public sealed record BatchStepCommand {
    public required string RequestId { get; init; }

    public required string ActorId { get; init; }

    public required string PassageId { get; init; }
}

public sealed record BatchStepResult {
    public BatchStepStepResult[] Steps { get; init; } = [];

    public required LogicalTimeSnapshot Time { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BatchObserveResult? PostObservations { get; init; }
}

public sealed record BatchStepStepResult {
    public required string RequestId { get; init; }

    public required string ActorId { get; init; }

    public required string PassageId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorMoveResult? Move { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BatchRuntimeError? Error { get; init; }
}

public sealed record BatchRuntimeError(string Message);

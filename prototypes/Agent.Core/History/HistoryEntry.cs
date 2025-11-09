using System.Collections.Immutable;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 定义历史条目的类型。此枚举与 <see cref="HistoryEntry"/> 及 <see cref="IHistoryMessage"/> 的分类保持一致，
/// 以强化学习（RL）术语为核心进行命名，具体的转换逻辑由各提供商（Provider）的适配层负责。
/// </summary>
public enum HistoryEntryKind {
    /// <summary>
    /// 表示环境提供给 Agent 的观测。
    /// </summary>
    Observation,
    /// <summary>
    /// 表示 Agent 发出的动作。
    /// </summary>
    Action,
    /// <summary>
    /// 表示工具执行后的补充观测。
    /// </summary>
    ToolResults,
    Recap
}

/// <summary>
/// 描述一次模型调用的来源信息，包括供应商、API 规范和具体的模型标识。
/// 这些信息有助于在历史回放时，将强化学习（RL）视角的序列重新映射为特定聊天（Chat）范式所需的上下文。
/// </summary>
/// <param name="ProviderId">服务提供商的内部标识符，例如 "OpenAI" 或 "Anthropic"。</param>
/// <param name="ApiSpecId">本次调用所遵循的 API 规范，例如 <c>openai-chat-v1</c>。</param>
/// <param name="Model">所使用的具体模型名称或版本号。</param>
public record CompletionDescriptor(
    string ProviderId,
    string ApiSpecId,
    string Model
);

/// <summary>
/// Agent 历史条目的抽象基类。它为强化学习（RL）序列中的所有事件提供了统一的元数据，
/// 并为派生类定义了时间戳和类型等基本属性。静态历史记录与流式回放均通过此类型与 <see cref="IHistoryMessage"/> 接口进行交互。
/// </summary>
public abstract record class HistoryEntry : ITokenEstimateSource {
    /// <summary>
    /// 派生类必须声明其在强化学习（RL）语境下的语义类型，供上层策略和存档系统使用。
    /// </summary>
    public abstract HistoryEntryKind Kind { get; }

    /// <summary>
    /// 获取或初始化历史事件发生的时间。默认为对象创建时的当前时间，但在历史回放等场景下可以被覆盖。
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    private ulong _serial;
    /// <summary>
    /// 获取当前历史条目的序列号，序列号在追加至 <see cref="AgentState"/> 时赋值。
    /// </summary>
    /// <exception cref="InvalidOperationException">在条目尚未被追加时访问。</exception>
    public ulong Serial => _serial != 0
        ? _serial
        : throw new InvalidOperationException("HistoryEntry serial has not been assigned yet. Append the entry to AgentState before reading the serial.");

    /// <summary>
    /// （内部方法）为历史条目指定序列号，仅允许赋值一次且必须大于零。
    /// </summary>
    /// <param name="serial">由 <see cref="AgentState"/> 管理的递增序列值。</param>
    /// <exception cref="ArgumentOutOfRangeException">当传入的序列号小于等于零。</exception>
    /// <exception cref="InvalidOperationException">当尝试重复赋值时。</exception>
    internal void AssignSerial(ulong serial) {
        if (serial == 0) { throw new ArgumentOutOfRangeException(nameof(serial), "HistoryEntry serial must be greater than zero."); }
        if (_serial != 0) { throw new InvalidOperationException($"HistoryEntry serial is already assigned (current={_serial})."); }

        _serial = serial;
    }


    private uint _tokenEstimate;
    /// <summary>
    /// 获取描述当前条目信息量的 token 估计值。
    /// 当返回值为 0 时表示尚未设定；若原始估计为 <c>0</c>，将返回 <c>1</c> 作为最小有效值。
    /// </summary>
    public uint TokenEstimate => _tokenEstimate;

    /// <summary>
    /// （内部方法）为历史条目指定 token 估计值，仅允许赋值一次。
    /// 当估计值为 <c>0</c> 时将其规范化为 <c>1</c>，以免与未赋值状态冲突。
    /// </summary>
    /// <param name="rawTokenEstimate">由上层评估的原始 token 估计值。</param>
    /// <exception cref="InvalidOperationException">当尝试重复赋值时。</exception>
    internal void AssignTokenEstimate(uint rawTokenEstimate) {
        if (_tokenEstimate != 0) { throw new InvalidOperationException($"HistoryEntry token estimate is already assigned (current={_tokenEstimate})."); }

        // 排除表示未赋值的0值
        _tokenEstimate = Math.Max(1, rawTokenEstimate);
    }
}

/// <summary>
/// 表示 Agent 的一个动作条目，它封装了聊天（Chat）范式中助手的回复，包括文本内容和工具调用。
/// </summary>
/// <param name="Content">模型生成的文本内容，即动作的一部分。</param>
/// <param name="ToolCalls">伴随文本内容产生的工具调用请求列表。</param>
/// <param name="Invocation">记录生成此次动作所使用的模型来源信息。</param>
public sealed record ActionEntry(
    string Content,
    IReadOnlyList<ParsedToolCall> ToolCalls,
    CompletionDescriptor Invocation
) : HistoryEntry, IActionMessage {
    /// <inheritdoc />
    public override HistoryEntryKind Kind => HistoryEntryKind.Action;
    HistoryMessageKind IHistoryMessage.Kind => HistoryMessageKind.Action;
    string IActionMessage.Content => Content;
    IReadOnlyList<ParsedToolCall> IActionMessage.ToolCalls => ToolCalls;
}


/// <summary>
/// 作为观测类历史条目的基类，用于聚合多种形式的观测数据，如系统通知、窗口渲染和工具结果等。
/// </summary>
public record class ObservationEntry : HistoryEntry {
    private LevelOfDetailContent? _notifications;
    private bool _notificationsAssigned;

    /// <summary>
    /// 初始化空的观测条目。
    /// </summary>
    public ObservationEntry() { }

    /// <inheritdoc />
    public override HistoryEntryKind Kind => HistoryEntryKind.Observation;

    /// <summary>
    /// 获取当前条目的通知内容。
    /// </summary>
    public LevelOfDetailContent? Notifications => _notifications;

    /// <summary>
    /// 将内部存储的观测数据转换为 <see cref="ObservationMessage"/>，以供补全（Completion）层或外部策略使用。
    /// 可通过 <paramref name="detailLevel"/> 参数控制内容的详细程度，以平衡信息量与成本。
    /// </summary>
    /// <param name="detailLevel">期望输出内容的细节等级。</param>
    /// <param name="windows">由外部渲染好的窗口状态描述。</param>
    public virtual ObservationMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        var notificationText = Notifications?.GetContent(detailLevel);
        var content = MergeContent(notificationText, windows);

        return new ObservationMessage(
            Content: content
        );
    }

    /// <summary>
    /// （内部方法）将通知内容设置为指定值，标记为最终状态，仅允许调用一次。
    /// </summary>
    /// <param name="notifications">新的通知内容。</param>
    /// <exception cref="ArgumentNullException">当传入的通知内容为 <c>null</c> 时抛出。</exception>
    /// <exception cref="InvalidOperationException">当通知内容已被设置过。</exception>
    internal void AssignNotifications(LevelOfDetailContent notifications) {
        if (notifications is null) { throw new ArgumentNullException(nameof(notifications)); }
        if (_notificationsAssigned) { throw new InvalidOperationException("ObservationEntry notifications have already been assigned."); }

        _notifications = notifications;
        _notificationsAssigned = true;
    }

    /// <summary>
    /// 根据通知文本与窗口内容拼接统一的观测文本。
    /// </summary>
    /// <param name="notifications">可选的通知内容。</param>
    /// <param name="windows">可选的窗口内容。</param>
    /// <returns>合并后的内容字符串；若两者均为空，则返回其中一个原值（可能为 <c>null</c>）。</returns>
    protected static string? MergeContent(string? notifications, string? windows) {
        List<string>? parts = null;

        if (!string.IsNullOrWhiteSpace(notifications)) {
            parts ??= new List<string>(capacity: 2);
            parts.Add(notifications);
        }

        if (!string.IsNullOrWhiteSpace(windows)) {
            parts ??= new List<string>(capacity: 2);
            parts.Add(windows);
        }

        if (parts is null) { return notifications ?? windows; }

        return string.Join("\n", parts);
    }
}

/// <summary>
/// 表示一个包含工具执行结果的观测条目。
/// 此类型既兼容聊天（Chat）范式中的工具输出，又能在强化学习（RL）语境下被统一解释为"环境反馈"。
/// </summary>
public sealed record class ToolResultsEntry : ObservationEntry {
    /// <summary>
    /// 初始化工具结果条目。
    /// </summary>
    /// <param name="results">按不同细节等级存储的工具调用结果列表。</param>
    /// <param name="executeError">工具执行过程中产生的错误信息。</param>
    public ToolResultsEntry(
        IReadOnlyList<LodToolCallResult> results,
        string? executeError
    ) {
        Results = results ?? throw new ArgumentNullException(nameof(results));
        ExecuteError = executeError;
    }

    /// <inheritdoc />
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResults;

    /// <summary>
    /// 获取工具调用结果列表。
    /// </summary>
    public IReadOnlyList<LodToolCallResult> Results { get; init; }

    /// <summary>
    /// 获取工具执行错误信息。
    /// </summary>
    public string? ExecuteError { get; init; }

    /// <summary>
    /// 将此条目投影为 <see cref="ToolResultsMessage"/>，并根据指定的细节等级裁剪结果内容。
    /// </summary>
    /// <param name="detailLevel">期望输出内容的细节等级。</param>
    /// <param name="windows">渲染后的窗口视图。</param>
    public override ToolResultsMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        IReadOnlyList<ToolResult> projectedResults = ProjectResults(Results, detailLevel);

        return new ToolResultsMessage(
            Content: MergeContent(Notifications?.GetContent(detailLevel), windows),
            Results: projectedResults,
            ExecuteError: ExecuteError
        );
    }

    /// <summary>
    /// 根据指定的细节等级，从原始工具调用结果列表中投影出裁剪后的版本。
    /// 此方法可避免将内部缓存或冗余信息暴露给外部策略。
    /// </summary>
    /// <param name="source">包含多细节等级内容的原始工具调用结果列表。</param>
    /// <param name="detailLevel">目标输出的细节等级。</param>
    /// <returns>一个根据细节等级裁剪后的只读工具结果列表。</returns>
    private static IReadOnlyList<ToolResult> ProjectResults(
        IReadOnlyList<LodToolCallResult> source,
        LevelOfDetail detailLevel
    ) {
        if (source.Count == 0) { return ImmutableArray<ToolResult>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolResult>(source.Count);

        for (int i = 0; i < source.Count; i++) {
            LodToolCallResult item = source[i];

            builder.Add(
                new ToolResult(
                    item.ToolName ?? string.Empty,
                    item.ToolCallId ?? string.Empty,
                    item.Status,
                    item.Result.GetContent(detailLevel)
                )
            );
        }

        return builder.MoveToImmutable();
    }

}

public sealed record class RecapEntry(
    string Content,
    ulong InsteadSerial
) : HistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.Recap;
}

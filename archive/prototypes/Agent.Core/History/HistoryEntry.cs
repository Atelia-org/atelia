using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 定义 RecentHistory 事件账本中的条目类型。
/// 命名以强化学习（RL）术语为核心；其中一部分会直接投影为 <see cref="IHistoryMessage"/>，
/// 也允许存在仅在运行时内部可见、需要在投影阶段合并掉的类型（如 <see cref="HistoryEntryKind.Injection"/>）。
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
    /// 表示 Agent-OS / runtime 注入到 Agent 当前心智流中的 actor-side 内容。
    /// </summary>
    Injection,
    /// <summary>
    /// 表示工具执行后的补充观测。
    /// </summary>
    ToolResults,
    Recap
}

/// <summary>
/// Agent 历史条目的抽象基类。
/// 它描述的是 RecentHistory 中的事件账本条目，而不是 provider 原生 message。
/// 真正发给 <see cref="ICompletionClient"/> 的 <see cref="IHistoryMessage"/> 序列，
/// 由 <see cref="AgentState.ProjectInvocationContext"/> 在调用前动态投影得到。
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

    /// <summary>
    /// 判断当前条目是否为 observation-like（Observation / ToolResults / Recap）。
    /// 用于检查历史交替不变量与上下文切分的合法性。
    /// </summary>
    public bool IsObservationLike => Kind is HistoryEntryKind.Observation or HistoryEntryKind.ToolResults or HistoryEntryKind.Recap;

    /// <summary>
    /// 判断当前条目是否为 actor-side（Action / Injection）。
    /// </summary>
    public bool IsActorLike => Kind is HistoryEntryKind.Action or HistoryEntryKind.Injection;
}

/// <summary>
/// 表示 Agent 的一个动作条目，是 History 层的 envelope：持有纯消息体 <see cref="Message"/>，
/// 并附加 Agent 历史所需的 <see cref="Invocation"/> 元信息。
/// </summary>
/// <remarks>
/// <b>分层边界</b>：本类型<b>不</b>实现 <see cref="IHistoryMessage"/>。
/// 需要纯消息体时请使用 <see cref="Message"/>（<see cref="ActionMessage"/>），
/// 例如 provider converter 的输入应使用 <see cref="AgentState.ProjectInvocationContext"/> 投影后的 <see cref="ActionMessage"/>。
/// </remarks>
/// <param name="Message">Canonical action 消息体；<see cref="ActionMessage.Blocks"/> 已在构造时冻结。</param>
/// <param name="Invocation">记录生成此次动作所使用的模型来源信息，用于 turn lock 与 thinking origin 对齐。</param>
public sealed record ActionEntry(
    ActionMessage Message,
    CompletionDescriptor Invocation
) : HistoryEntry {
    /// <inheritdoc />
    public override HistoryEntryKind Kind => HistoryEntryKind.Action;
}

public enum InjectionSourceKind {
    AgentOsTrigger,
    Wizard,
    MemoryRecall,
    Emotion,
    KnowledgeRecall,
    HostOverride,
    Other
}

public sealed record InjectionSource(
    InjectionSourceKind Kind,
    string? SourceId = null,
    string? Notes = null
);

/// <summary>
/// 表示一段由 Agent-OS / runtime 注入的 actor-side 内容。
/// </summary>
/// <remarks>
/// 与 <see cref="ActionEntry"/> 不同，本类型不表示“模型在一次 completion 中真实输出了什么”，
/// 而表示“外部运行时希望作为 Agent 当前心智流的一部分继续被模型续写的内容”。
/// RecentHistory 中只记录与 provider 无关的注入语义（文本内容 + 注入为正文还是 thinking），
/// 真正发给模型的 assistant/action message 由投影层在调用前按目标 invocation 动态构造。
/// </remarks>
public sealed record InjectionEntry : HistoryEntry {
    public InjectionEntry(
        string content,
        ActionBlockKind blockKind,
        InjectionSource source
    ) {
        if (string.IsNullOrWhiteSpace(content)) {
            throw new ArgumentException("Injection content must not be null or whitespace.", nameof(content));
        }
        if (blockKind is not (ActionBlockKind.Text or ActionBlockKind.Thinking)) {
            throw new ArgumentOutOfRangeException(nameof(blockKind), blockKind, "Injection block kind must be Text or Thinking.");
        }

        Content = content;
        BlockKind = blockKind;
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public string Content { get; }

    public ActionBlockKind BlockKind { get; }

    public InjectionSource Source { get; }

    public override HistoryEntryKind Kind => HistoryEntryKind.Injection;
}


/// <summary>
/// 作为观测类历史条目的基类，用于聚合多种形式的观测数据，如系统通知、窗口渲染和工具结果等。
/// </summary>
public record class ObservationEntry : HistoryEntry {
    private string? _notifications;
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
    public string? Notifications => _notifications;

    /// <summary>
    /// 将内部存储的观测数据转换为 <see cref="ObservationMessage"/>，以供补全（Completion）层或外部策略使用。
    /// </summary>
    /// <param name="windows">由外部渲染好的窗口状态描述。</param>
    public virtual ObservationMessage GetMessage(string? windows) {
        var content = MergeContent(Notifications, windows);

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
    internal void AssignNotifications(string notifications) {
        if (notifications is null) { throw new ArgumentNullException(nameof(notifications)); }
        if (_notificationsAssigned) { throw new InvalidOperationException("ObservationEntry notifications have already been assigned."); }

        _notifications = notifications;
        _notificationsAssigned = true;
    }

    /// <summary>
    /// （内部方法）将通知内容并入当前 observation。
    /// 若当前条目尚无通知，则等价于首次赋值；否则按行拼接追加。
    /// </summary>
    internal void MergeNotifications(string notifications) {
        if (notifications is null) { throw new ArgumentNullException(nameof(notifications)); }

        if (!_notificationsAssigned) {
            _notifications = notifications;
            _notificationsAssigned = true;
            return;
        }

        _notifications = _notifications is null
            ? notifications
            : string.Join("\n", _notifications, notifications);
    }

    /// <summary>
    /// 根据通知文本与窗口内容拼接统一的观测文本。
    /// </summary>
    /// <param name="notifications">可选的通知内容。</param>
    /// <param name="windows">可选的窗口内容。</param>
    /// <returns>合并后的内容字符串；若两者均为空，则返回其中一个原值（可能为 <c>null</c>）。</returns>
    internal static string? MergeContent(string? notifications, string? windows) {
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
    /// <param name="results">工具调用结果列表。</param>
    public ToolResultsEntry(IReadOnlyList<ToolCallExecutionResult> results) {
        Results = results ?? throw new ArgumentNullException(nameof(results));
    }

    /// <inheritdoc />
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResults;

    /// <summary>
    /// 获取工具调用结果列表。
    /// </summary>
    public IReadOnlyList<ToolCallExecutionResult> Results { get; init; }

    /// <summary>
    /// 将此条目投影为 <see cref="ToolResultsMessage"/>。
    /// </summary>
    /// <param name="windows">渲染后的窗口视图。</param>
    public override ToolResultsMessage GetMessage(string? windows) {
        IReadOnlyList<ToolResult> projectedResults = ProjectResults(Results);

        return new ToolResultsMessage(
            content: MergeContent(Notifications, windows),
            results: projectedResults
        );
    }

    /// <summary>
    /// 从原始工具调用结果列表中投影出面向外部策略的版本。
    /// </summary>
    /// <param name="source">原始工具调用结果列表。</param>
    /// <returns>投影后的只读工具结果列表。</returns>
    private static IReadOnlyList<ToolResult> ProjectResults(IReadOnlyList<ToolCallExecutionResult> source) {
        if (source.Count == 0) { return ImmutableArray<ToolResult>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolResult>(source.Count);

        for (int i = 0; i < source.Count; i++) {
            ToolCallExecutionResult item = source[i];
            builder.Add(item.ToToolResult());
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

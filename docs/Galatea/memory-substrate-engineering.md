# Galatea Memory Substrate 工程设计草案

> 状态：v0 草案。本文只讨论内容无关的软件工程 substrate，不讨论 Galatea Memory Pack 里应该有哪些具体主题、信念、关系通道或心智理论分类。

## 1. 目标

动态记忆维护、自我一致性维护、动态记忆召回、Reabsorb、主意识审议等问题彼此缠绕。当前阶段应先实现最确定、最可测试的一层：

- `IRecentHistoryAnalyzer`：接收一段 recent history 并执行分析。
- `IMemoryBlockMaintainer`：在 recent history 分析基础上，维护一个旧版文本块，产出新版文本。
- `MemoryPack`：内容无关的三载体字典容器，可渲染/投影成后续 LLM API 调用需要的连续文本。

这层不定义 Memory Pack 的业务 block key，不定义哪些内容应放 system/observation/action，也不定义 Galatea 的信念、关系、回忆录、世界档案等分类。这些留给内容设计文档和后续实验。

## 2. 非目标

本 substrate 暂不解决：

- 具体 Memory Pack block taxonomy。
- 具体 block 的写权限、冲突治理、主意识审批规则。
- 动态记忆召回策略。
- 自我一致性维护策略。
- Reabsorb 的内容判断准则。
- Galatea 的心智理论、角色理论、Belief modeling。

以上问题可以通过本文定义的容器和执行接口承载，但不应在 substrate 层硬编码。

## 3. 核心抽象

### 3.1 RecentHistorySlice

`RecentHistorySlice` 是一次分析看到的只读历史片段。它不承诺来源一定是完整会话，也不承诺一定来自某个 split point；调用方可以传入任意已投影好的 recent history。

为了支持滚动处理，它还携带一份前置上下文快照。这样用新结构复刻现有滚动压缩能力时，analyzer/maintainer 能同时看到“上一版前段上下文”和“本次 analysis window”：

```csharp
public sealed record RecentHistorySlice(
    ContextHeaderSnapshot PriorContext,
    IReadOnlyList<IHistoryMessage> Messages,
    string? SourceId = null,
    ulong? EstimatedTokens = null
);

public sealed record ContextHeaderSnapshot(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
) {
    public static ContextHeaderSnapshot Empty { get; } = new("", "", "");

    public bool IsEmpty =>
        string.IsNullOrEmpty(SystemPromptFragment)
        && string.IsNullOrEmpty(ObservationMessage)
        && string.IsNullOrEmpty(ActionMessage);
}
```

设计要点：

- `PriorContext` 表示分析窗口前段的已渲染上下文，例如上一版摘要、上一版 Memory Pack 投影，或空对象。
- `PriorContext` 推荐非 null；无前置上下文时使用 `ContextHeaderSnapshot.Empty`，避免把“确实为空”和“忘记传参”混在一起。
- `Messages` 是只读输入；analyzer 不应修改它。
- `SourceId` 用于 audit，例如 session id、epoch id、archive id。
- `EstimatedTokens` 是可选优化信息，不作为语义依据。

`RecentHistorySlice` 因此更准确地表示一个 analysis window，而不是固定意义上的“最新后缀”：

```text
PriorContext + Messages
```

当前 Galatea memory 维护的默认目标是**即将滑出上下文窗口的最旧一端**。也就是说，调用方通常应把 split point 之前的 prefix 作为 `Messages` 传入，让 analyzer/maintainer 在信息丢失前完成提炼。未来如果出现“即时分析最新上下文”的并行 analyzer，可以传入最新 suffix；这属于调用方选择的 window policy，不改变 `RecentHistorySlice` 契约。

它仍然保持内容无关；`PriorContext` 只表达三载体文本，不解释其中有哪些 Memory Pack block。

### 3.2 IRecentHistoryAnalyzer

`IRecentHistoryAnalyzer` 只定义“能接受一段 RecentHistory 进行分析”。它不规定分析产物是什么，也不规定实现是否通过工具、内部状态、外部 store 或 side effect 记录结果。

```csharp
public interface IRecentHistoryAnalyzer {
    string Id { get; }

    ValueTask AnalyzeAsync(
        RecentHistoryAnalysisContext context,
        CancellationToken ct
    );
}

public sealed record RecentHistoryAnalysisContext(
    RecentHistorySlice RecentHistory,
    IServiceProvider? Services = null
);
```

这里故意不提供泛型 `TResult`，因为 base interface 不应该替上层决定输出协议。需要结构化输出的 analyzer 可以另行实现更具体的接口；需要 side effect 的 analyzer 也可以通过 `Services` 中的 store 或自己的依赖完成。

### 3.3 IMemoryBlockMaintainer

`IMemoryBlockMaintainer` 是更具体、更可测试的 block maintainer：它接收 recent history 与一个旧版文本块，运行分析/编辑过程，返回新版文本。它自身不直接写 Memory Pack，因此对调用方是无副作用的。

它也是现有 `IMemoryMaintainerAgent` 的目标替代概念。旧名字里的 `Agent` 容易暗示它拥有会话状态或能直接提交记忆；新设计中 maintainer 应被理解为一个可插拔、可单独测试的 block transformer。它可以由 LLM + tools 实现，也可以由规则、测试 fake、外部服务或本地索引实现，但共同契约都是：输入 snapshot，输出新版 block。

```csharp
public interface IMemoryBlockMaintainer {
    string Id { get; }

    MemoryPackBlockPath Target { get; }

    ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    );
}

public sealed record MemoryBlockMaintenanceRequest(
    RecentHistorySlice RecentHistory,
    MemoryPackBlockPath Target,
    MemoryPackBlock OldBlock
);

public sealed record MemoryBlockMaintenanceResult(
    string MaintainerId,
    MemoryPackBlockPath Target,
    MemoryPackBlock NewBlock,
    IReadOnlyList<MemoryMaintenanceNotice> Notices,
    IReadOnlyList<string> Diagnostics,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? Errors = null,
    int ToolCallsExecuted = 0
);
```

约束：

- `MaintainAsync` 必须返回一个完整新版 block，而不是对旧文本的隐式增量。
- `MaintainAsync` 不提交 Memory Pack，不替换 `ContextHeader`，不删除 history。
- `Target` 同时用于 duplicate writer 检查、从 snapshot 读取旧 block、把结果写入 draft；同一 epoch 内每个 `MemoryPackBlockPath` 最多一个 maintainer 写。
- 如果 `Target` 对应的 old block 在当前 MemoryPack snapshot 中不存在，编排器应向 maintainer 传入 `new MemoryPackBlock(string.Empty)`，允许 maintainer 创建新 block。这是新建 LLM session 的正常路径，而不是错误。
- `Notices` 可承载实现层无法直接处理的问题，但 substrate 不解释这些问题的心理学含义。
- `Invocation` / `Errors` / `ToolCallsExecuted` 保留现有代码的审计能力，但不要求所有 maintainer 都来自 completion；非 LLM maintainer 可返回 `null` invocation 和 `0` tool calls。
- 工具循环是具体 maintainer 实现的内部细节，不进入统一接口。内置 LLM maintainer 可以保留 `ToolSession` 配置，使不同 maintainer 暴露不同工具集合和 scoped items；规则型或测试 fake maintainer 不需要依赖 tool abstraction。

`IRecentHistoryAnalyzer` 与 `IMemoryBlockMaintainer` 保持并列而非继承关系：前者表达“分析 recent history，可通过 side effect 或外部 store 产出结果”；后者表达“维护一个目标 block 并返回新版 block”。同一个实现类可以同时实现两者，但 substrate 不强制每个 maintainer 都提供额外 `AnalyzeAsync` 入口。

现有 `ChatSession` 中的 `IMemoryMaintainerAgent` / `MemoryMaintenanceRequest` / `MemoryMaintenanceResult` 是早期验证概念时形成的雏形，不应长期与本节类型并列成两套模型。后续实现应把已有的 recent-fragment split、并行 maintainer、tool-loop、duplicate writer 检查等能力融合到 block-level Memory substrate 中，而不是继续扩张旧 API。

融合后的推荐分层：

- `IMemoryBlockMaintainer`：统一 maintainer 概念，负责“给定 old block + recent history，产出 new block”。
- 内置 LLM maintainer 实现：承载旧 `SystemPrompt` / `UserPrompt` / `ToolSession` 风格，把 old block、target path、prior context 和 recent messages 投影成 completion 请求，并执行现有 tool-loop。`ToolSession` 属于这个实现的配置，不属于所有 maintainer 的共同接口。
- 编排器：负责选择/接收 `RecentHistorySlice`，校验 maintainer id 与 target 唯一性，并行执行 maintainer，汇总结果。它可以帮助把结果应用到 `MemoryPackDraft`，但不提交 pack store，也不调用 `SetContextHeader`。

## 4. MemoryPack 容器

### 4.1 三载体字典

Memory Pack 的容器层使用 Atelia / Agent 领域术语，而不是 provider API 的 user/assistant 术语：

- `system`：投影到 `ContextHeader.SystemPromptFragment`。
- `observation`：投影为首个 `ObservationMessage`，在 provider API 边界通常对应 user role。
- `action`：投影为首个 `ActionMessage`，在 provider API 边界通常对应 assistant role。

每个载体内部是一个有序字典。key 的业务含义由应用层决定；substrate 只保证 key、顺序、文本内容和渲染能力。载体身份由 `MemoryPack` 上的字段位置表达，不再在 block 内重复存储 role，避免冗余信息和不一致状态。

```csharp
public sealed class MemoryPack {
    public OrderedDictionary<string, MemoryPackBlock> System { get; } = [];
    public OrderedDictionary<string, MemoryPackBlock> Observation { get; } = [];
    public OrderedDictionary<string, MemoryPackBlock> Action { get; } = [];
}

public sealed record MemoryPackBlock(
    string Text
);

public sealed record MemoryPackBlockPath(
    MemoryPackCarrier Carrier,
    string BlockKey
);

public enum MemoryPackCarrier {
    System,
    Observation,
    Action
}
```

说明：

- 使用 `OrderedDictionary<string, MemoryPackBlock>` 是为了同时保留稳定顺序并避免同一 channel 内 key 重复。
- `BlockKey` 只要求在同一 channel 内唯一。
- `MemoryPackBlockPath` 是操作路径，不是 block 自身状态；它可用于 maintainer target、draft patch、audit 和 notice。
- `MemoryPackBlockPath` 的 public API 使用强类型 `MemoryPackCarrier` 表达三载体目标，获得编译期约束。StateJournal / JSON 等持久化边界仍应显式转成稳定 string token，例如 `system` / `observation` / `action`，符合现有 ChatSession schema 用 string discriminator 的风格，也避免依赖 StateJournal 对 enum 的直接支持。
- substrate 不知道 `core.beliefs`、`self.memoir` 等 key 是否存在。

### 4.2 渲染与投影

Memory Pack 需要两种能力：

1. block 级读写：给 maintainer 精确定位旧版文本块。
2. 连续文本渲染：生成后续投影所需的 system/observation/action 三段文本。

推荐渲染格式：

```markdown
## {key}

{block text}
```

同一 channel 内按 block 顺序拼接：

```csharp
public sealed record RenderedMemoryPack(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
);
```

投影到 ChatSession：

```text
SystemPrompt = BaseSystemPrompt + RenderedMemoryPack.SystemPromptFragment
Context[0] = ObservationMessage(RenderedMemoryPack.ObservationMessage)
Context[1] = ActionMessage(RenderedMemoryPack.ActionMessage)
Context[2..] = RecentHistory
```

当前 `ContextHeader` 已使用 `SystemPromptFragment` / `ObservationMessage` / `ActionMessage` 领域术语。`RenderedMemoryPack.ActionMessage` 在 substrate MVP 中先保持为 string；投影到 `ContextHeader.ActionMessage` 时，空字符串映射为 `null`，非空字符串映射为只包含一个 text block 的 `ActionMessage`。Completion 层的多模态 blocks/parts 能力暂不进入 Memory substrate MVP，直到 API 边界最终调用前再翻译为 text block 即可。

空 channel 的处理应由配置决定：

- MVP：空字符串，不生成对应 header message 或生成空内容均可，但必须稳定。
- 推荐：`ContextHeader` 允许三段中任意一段为空，由投影层决定是否跳过空 message。

### 4.3 Patch 与 Draft

容器层应支持 draft，而不是让 maintainer 直接改主 Memory Pack：

```csharp
public sealed class MemoryPackDraft {
    public MemoryPack Base { get; }

    public void ReplaceBlock(MemoryPackBlockPath path, string newText);
    public void UpsertBlock(MemoryPackBlockPath path, string text, int? order = null);
    public bool RemoveBlock(MemoryPackBlockPath path);
    public MemoryPack Build();
}
```

MVP 文件版可以直接复制 Markdown/JSON 到临时对象；StateJournal 版可使用对象级 `ForkCommittedAsMutable()` / `Repository.ReplayCommitted(..., ForceMutable)`。Revision 级 branch 不是常规路径。

## 5. 编排模型

### 5.1 单 Epoch

一次维护创建一个 epoch：

```text
RecentHistorySlice + MemoryPack snapshot
→ 为每个目标 block 创建 IMemoryBlockMaintainer
→ 并行 MaintainAsync
→ 应用结果到 MemoryPackDraft
→ render/project
→ 原子替换 ContextHeader / pack store
```

同一 epoch 内：

- 每个 `MemoryPackBlockPath` 最多一个 maintainer 写。
- maintainer 之间不共享 mutable state。
- 失败的 maintainer 不应阻止无关 block 的结果进入 audit；是否应用由上层策略决定。

推荐把 epoch 编排显式分成两个入口：

1. 纯 substrate 入口：调用方已经提供 `RecentHistorySlice` 与 `MemoryPack` snapshot，编排器只做 duplicate writer 检查、old block 解析、并行 `MaintainAsync` 和结果汇总。这是最容易单测的核心路径。
2. `ChatSessionEngine` 便捷入口：沿用现有 `FindHalfContextSplitPoint(...)` 从持久 history 切出即将滑出窗口的 prefix，并把 split metadata / token estimate 放入 epoch result。该入口是兼容现有 `RunMemoryMaintainersAsync(...)` 能力的迁移路径。

epoch result 应保留现有审计字段：是否完成、失败原因、split index、维护前 history count、维护前 token estimate、每个 maintainer 的 invocation / errors / tool calls。与此同时，block-level 结果必须以 `MemoryPackBlockPath` 表达目标，而不是旧的单字符串 `TargetBlockKey`。

当 maintainer 的 `Target` 在 snapshot 中不存在时，epoch 编排器应把 old block 视为空文本，并在应用结果时通过 draft upsert 创建该 block。

### 5.2 与现有 ChatSession substrate 的关系

当前 `RunMemoryMaintainersAsync(...)` 提供“给一组 maintainer 同一个 sliding-out analysis window，并行运行”的便捷入口。现阶段默认 window policy 是分析 split point 之前的 prefix，因为这些上下文即将从主窗口中丢失，最需要被提炼进 Memory Pack 或 rolling summary。后续如果需要分析最新后缀，应新增显式策略，而不是把 `RecentHistorySlice` 本身绑定为 suffix。

后续可以演化为：

- ChatSession 层保留通用 recent history 切片和 completion/tool-loop 编排。
- `MemoryPack`、`MemoryPackDraft`、`IMemoryBlockMaintainer` 等内容无关工程 substrate 放在 `Atelia.ChatSession`。
- Galatea 层只定义具体 Memory Pack key、维护策略、提示词和 UI 行为。
- `IMemoryBlockMaintainer` 应逐步吸收并替代现有 `IMemoryMaintainerAgent` 的职责；若短期需要过渡，也应以融合旧实现为目标，而不是长期维护两套并行 API。

### 5.3 PriorContext 的典型用途

`PriorContext` 的目标是让同一套 substrate 覆盖滚动摘要和 Memory Pack 维护，而不把具体内容分类写死。

兼容现有滚动压缩：

```text
上一版 ContextHeader 摘要 + sliding-out prefix messages
→ analyzer / maintainer
→ 新 ContextHeader 摘要
```

维护 Memory Pack block：

```text
上一版 MemoryPack 渲染结果 + sliding-out prefix messages + old block text
→ IMemoryBlockMaintainer
→ new block text
```

因此 `PriorContext` 不应被理解为“旧摘要”专用字段。它只是 analysis window 的前置上下文，可来自旧摘要、Memory Pack 投影、临时召回结果，或空对象。

## 6. MVP 验收

- 可以创建一个内容无关 `MemoryPack`，包含 system/observation/action 三个 channel。
- 每个 channel 支持按 key 读写 block，并能稳定渲染为连续文本。
- 可以把 `MemoryPack` 投影成 `ContextHeader`。
- `RecentHistorySlice` 能携带 `ContextHeaderSnapshot.Empty` 或上一版 `ContextHeader` 的三段文本。
- 可以实现一个测试 maintainer：输入 old block + recent history，输出 new block。
- 编排器能并行运行多个 maintainer，且拒绝同一 block path 的多 writer。
- maintainer 不直接提交 pack；所有改动先进入 draft。
- 不需要定义任何 Galatea 内容分类也能完成以上测试。

## 7. 延后事项

- 哪些 block 属于 system/observation/action。
- 哪些 block 受保护，哪些可自动维护。
- `mainline notice` 的语义与注入策略。
- Reabsorb 的判断标准。
- 动态召回如何选择 archive/seed。
- Galatea 主意识与 fork/snapshot 的哲学授权边界。

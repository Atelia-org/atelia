# Thinking Replay 设计方案：Lossless History + Lossy Projection

**状态**：Draft v0.3 — 设计讨论收束，可进入施工阶段，待用户拍板
**作者**：Claude Opus 4.7 + GPT-5（协同设计）
**前置**：[Turn 内 LlmProfile 锁定](./memory-notebook.md#turn-与-llmprofile-锁定硬约束) 已落地
**适用范围**：Atelia Agent.Core / Completion / Completion.Abstractions

---

## 0. TL;DR

> 把 `ActionEntry` 升级为**有序 `Blocks` 序列**（含 `TextBlock` / `ToolCallBlock` / `ThinkingBlock` 等子类型），`ThinkingBlock` 以 `OpaquePayload` 黑盒持有 provider-native bytes。通过新的 `ProjectInvocationContext(...)` 接口产出 `StablePrefix + ActiveTurnTail` 两段上下文，在**投影层而非存储层**执行当前 Turn 的 thinking replay 裁剪。

**核心哲学**：**History 层是 lossless truth，Projection 层是 policy-controlled lossy view。**

### 0.1 v1 范围边界（不承诺什么）

为了让用户能干净地判断 "先做这一版是否物有所值"，v1 **不**包含以下内容（均列为已识别的后续拓展点，见 §9）：

- **`ThinkingBlock` 持久化**：v1 仅存在于内存；StateJournal 序列化 schema 在独立 Persistence 设计中决策
- **OpenAI Responses API 接入**：接口形态预留兼容，v1 不提供实现
- **Gemini thought parts 接入**：同上
- **额外的 `ActionBlock` 子类型**（`Citation` / `ServerToolUse` / `SafetyMeta` 等）：v1 仅作扩展点声明，不实现。未来需要时各自独立 PR 引入
- **Recap 主动 GC `OpaquePayload`**：v1 不做；待真实场景出现内存压力时再设计

v1 **承诺交付**的是：`ActionEntry.Blocks` 存储重构 + invocation-specific projection 接口 + Anthropic extended thinking 端到端 replay。其他 provider 在此基础上增量接入。

---

## 1. 问题陈述

### 1.1 直接动机

主流闭源模型已普遍提供"thinking / reasoning"内容：
- **Anthropic claude-3.7+ extended thinking**：assistant content blocks 中包含 `thinking` 类型 block，需要在工具往返中**原样回灌**才能保持推理连续性
- **OpenAI o1/o3 系列**：reasoning_content 字段（Chat Completions 只读）；Responses API 中作为 `reasoning` item 类型支持读写
- **Google Gemini 2.5/3.0**：thought parts 同样支持读写

这些 thinking 内容通常被**加密 / 签名 / 不透明编码**，跨模型不可解码。要在同 Turn 内的工具往返中实现"模型保持完整推理上下文"，必须做到：

1. 模型流式返回的 thinking 块**原样保存**到 history
2. 同 Turn 的下一次模型调用时**原样回灌**到 provider request
3. 跨 Turn / 跨 model 时**自动剥离**（旧 turn 的 thinking 对新 turn 无意义且可能触发协议错误）

### 1.2 设计约束（来自现有架构）

- **Turn 锁定不变量**已落地：同一 Turn 内不允许切换 `LlmProfile`（`CompletionDescriptor` 三元组严格匹配）
- `RecentHistory` 是 Agent 状态机、Recap、调试、持久化的**唯一事实源**，不能被破坏
- `ICompletionClient` 是无状态接口，不感知 Turn 概念
- 现有 100+ 测试需保持零回归
- Recap 可能裁掉 Turn 起点边界（`HasExplicitStartBoundary` 已由 `TurnAnalyzer` 提供）

### 1.3 此前被否决的方案

讨论过程中产生但被否决的方案（决策记录见 §9）：

| 方案 | 否决理由 |
|------|----------|
| `ActionEntry.ThinkingBlocks` 独立列表 | 丢失 assistant message 内部 ordering（thinking/text/tool_use 可任意交错） |
| Turn 内 provider-native + Turn 结束转 portable | 引入"两种条目共存"的存储语义、双源同步、turn closure 触发点等新故障面 |
| `ActiveTurnReplayState` sidecar | 与 `RecentHistory` 信息守恒，但额外引入崩溃重建、持久化协议、Recap 协调等问题 |

---

## 2. 设计哲学：Lossless History + Lossy Projection

### 2.1 一句话总纲

> **History 层是 lossless truth，Projection 层是 policy-controlled lossy view。**

### 2.2 三层职责分离

| 层 | 职责 | 主要类型 |
|----|------|----------|
| **真相层** | 忠实保存 assistant 输出的 ordered blocks，不丢任何 replay-grade 信息 | `ActionEntry.Blocks` / `ActionBlock` 子类型 |
| **投影层** | 根据当前 Turn 状态 + 目标 invocation，产出本次调用可见的上下文窗口 | `ProjectInvocationContext(options)` → `ProjectedInvocationContext` |
| **协议层** | 仅负责将投影结果编码为 provider 协议；不做 Turn 裁剪、不做兼容性判断 | `IMessageConverter` 各实现 |

### 2.3 关键不变量

1. **唯一事实源**：`AgentState.RecentHistory` 中的 `ActionEntry.Blocks` 是 thinking 内容的**唯一权威存储**。不存在并行 sidecar。
2. **投影即过滤**：旧 Turn 的 thinking 对新调用不可见，是**投影规则**而非**存储规则**——thinking 物理上仍在 history 里，仅在 `ProjectInvocationContext` 输出时被过滤。
3. **OpaquePayload 黑盒**：Agent.Core / Recap / UI / StateJournal / 非对应 converter 都**不需要解释** `ThinkingBlock.OpaquePayload` 的内部结构。
4. **Turn 边界即 replay 边界**：thinking 仅在 `当前 Turn + invocation 严格匹配 + 显式起点完整` 时回灌，否则一律剥离。

---

## 3. 类型设计

### 3.1 `ActionBlock` Sum Type

```csharp
namespace Atelia.Agent.Core.History;

/// <summary>
/// Assistant message 的有序内容块基类。开放式 sum type：未来可识别的 block 类型见 §9 开放点。
/// </summary>
public abstract record ActionBlock {
    private ActionBlock() { } // 限制外部继承
    public abstract ActionBlockKind Kind { get; }

    public sealed record Text(string Content) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.Text;
    }

    public sealed record ToolCall(ParsedToolCall Call) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.ToolCall;
    }

    public sealed record Thinking(
        CompletionDescriptor Origin,
        ReadOnlyMemory<byte> OpaquePayload,
        string? PlainTextForDebug = null
    ) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.Thinking;
    }
}

public enum ActionBlockKind {
    Text,
    ToolCall,
    Thinking
    // 未来扩展见 §9：Citation / ServerToolUse / SafetyMeta
}
```

**设计决策**：
- 私有构造 + 嵌套 sealed records 实现 closed-by-default sum type；新增子类型必须显式扩展（非 `UnknownBlock` 万能逃生口，迫使新协议特性被一等公民设计）
- `Thinking.Origin` 直接复用 `CompletionDescriptor`（与 Turn lock 同构，replay 兼容性判定可直接 `block.Origin == options.TargetInvocation`）
- `OpaquePayload` 用 `ReadOnlyMemory<byte>` 而非 `string`：减少 UTF-8 编解码开销，且能容纳二进制（理论上 base64 字段也常见）
- `PlainTextForDebug` 可选：仅供日志/UI/调试，**不参与回灌**

### 3.2 `ActionEntry` 升级

```csharp
public sealed record ActionEntry(
    IReadOnlyList<ActionBlock> Blocks,
    CompletionDescriptor Invocation
) : HistoryEntry, IActionMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.Action;

    // ===== Compatibility views（lossy by design）=====
    //
    // 这些视图仅为兼容现有消费者保留，存在以下不保证：
    // (1) 不保留 Blocks 之间的 ordering——例如 [Text(a), ToolCall, Text(b)]
    //     在 Content 中变为 "ab"，丢失文本被工具调用打断的边界信息
    // (2) 不暴露 Thinking blocks——thinking 内容只能通过 Blocks 访问
    // (3) 任何依赖结构边界的下游应直接读取 Blocks，不要走这两个视图
    //
    // 选择 string.Concat 而非 string.Join('\n', ...) 的理由：
    // Anthropic / OpenAI / Gemini 的流式协议本身不保证 text block 之间有换行；
    // 在 compat view 强加 '\n' 反而会注入虚假信息。换行语义应由具体 provider
    // converter 在编码时按需添加，而非 compat view 越权决定。

    public string Content => string.Concat(
        Blocks.OfType<ActionBlock.Text>().Select(b => b.Content)
    );

    public IReadOnlyList<ParsedToolCall> ToolCalls => Blocks
        .OfType<ActionBlock.ToolCall>()
        .Select(b => b.Call)
        .ToArray();

    HistoryMessageKind IHistoryMessage.Kind => HistoryMessageKind.Action;
    string IActionMessage.Content => Content;
    IReadOnlyList<ParsedToolCall> IActionMessage.ToolCalls => ToolCalls;
}
```

**迁移策略**：
- 旧构造形式 `new ActionEntry(content, toolCalls, invocation)` 需要一个**便捷构造重载**，内部转成 `Blocks`：

```csharp
public ActionEntry(string content, IReadOnlyList<ParsedToolCall> toolCalls, CompletionDescriptor invocation)
    : this(BuildBlocks(content, toolCalls), invocation) { }

private static IReadOnlyList<ActionBlock> BuildBlocks(string content, IReadOnlyList<ParsedToolCall> toolCalls) {
    var list = new List<ActionBlock>();
    if (!string.IsNullOrEmpty(content)) { list.Add(new ActionBlock.Text(content)); }
    foreach (var call in toolCalls) { list.Add(new ActionBlock.ToolCall(call)); }
    return list;
}
```

这样所有现有调用点（测试 / Accumulator / 等）零修改。

### 3.3 富接口与投影消息

**依赖方向声明**：`IRichActionMessage` 引用 `ActionBlock` 与 `CompletionDescriptor`，而后两者位于 `Agent.Core.History`。因此 `IRichActionMessage` **必须**放在 `Agent.Core.History`（或更上层）命名空间，**不可**下沉到 `Completion.Abstractions`，否则会形成 Abstractions → Agent.Core 的反向依赖。Provider converter 项目已经引用 `Agent.Core`，所以富路径不会引入新的项目依赖。

```csharp
namespace Atelia.Completion.Abstractions;

// 老接口保持不变（位于 Completion.Abstractions，零依赖于 Agent.Core）
public interface IActionMessage : IHistoryMessage {
    string Content { get; }
    IReadOnlyList<ParsedToolCall> ToolCalls { get; }
}
```

```csharp
namespace Atelia.Agent.Core.History;

/// <summary>
/// 富接口：能识别 Blocks 的 converter 路径。位于 Agent.Core.History，避免反向依赖。
/// </summary>
public interface IRichActionMessage : IActionMessage {
    IReadOnlyList<ActionBlock> Blocks { get; }
    CompletionDescriptor Invocation { get; }
}

/// <summary>
/// 投影层产出的富 action message。Blocks 已根据投影规则过滤（旧 Turn 的 ThinkingBlock 已被剥离）。
/// </summary>
public sealed record ProjectedActionMessage(
    IReadOnlyList<ActionBlock> Blocks,
    CompletionDescriptor Invocation
) : IRichActionMessage {
    public string Content => string.Concat(
        Blocks.OfType<ActionBlock.Text>().Select(b => b.Content)
    );
    public IReadOnlyList<ParsedToolCall> ToolCalls => Blocks
        .OfType<ActionBlock.ToolCall>()
        .Select(b => b.Call)
        .ToArray();
    public HistoryMessageKind Kind => HistoryMessageKind.Action;
}
```

Converter 端的识别模式：

```csharp
// 老 converter（如 OpenAI Chat Completions）
foreach (var msg in context) {
    if (msg is IActionMessage action) {
        // 用 action.Content + action.ToolCalls，自动得到过滤后的视图
    }
}

// 新 converter（Anthropic Messages / OpenAI Responses）
foreach (var msg in context) {
    if (msg is IRichActionMessage rich) {
        EncodeBlocks(rich.Blocks); // 含 ThinkingBlock 时回灌
    } else if (msg is IActionMessage action) {
        // 兼容路径
    }
}
```

### 3.4 投影层接口

```csharp
namespace Atelia.Agent.Core.History;

public sealed record ContextProjectionOptions(
    /// <summary>
    /// 本次调用的目标模型身份；<c>null</c> 表示"非真实调用"场景
    /// （Recap 计算 / UI 展示 / debug log dump / 单元测试 等）。
    /// 当为 <c>null</c> 时 <see cref="ThinkingMode"/> 自动等价于 <see cref="ThinkingProjectionMode.None"/>
    /// ——无目标即无回灌可能。
    /// </summary>
    CompletionDescriptor? TargetInvocation = null,
    string? Windows = null,
    ThinkingProjectionMode ThinkingMode = ThinkingProjectionMode.CurrentTurnOnly
);

public enum ThinkingProjectionMode {
    /// <summary>所有 ThinkingBlock 一律剥离。</summary>
    None,
    /// <summary>仅当前 Turn 且 invocation 匹配且边界完整时保留；其他一律剥离。</summary>
    CurrentTurnOnly
}

public sealed record ProjectedInvocationContext(
    IReadOnlyList<IHistoryMessage> StablePrefix,
    IReadOnlyList<IHistoryMessage> ActiveTurnTail
) {
    /// <summary>
    /// 拼接成线性消息列表供 provider converter 消费。
    /// </summary>
    public IReadOnlyList<IHistoryMessage> ToFlat() => [.. StablePrefix, .. ActiveTurnTail];
}
```

`AgentState` 上的新方法（**唯一**投影入口，旧的 `ProjectContext(string?)` 在 PR 2 中删除）：

```csharp
public ProjectedInvocationContext ProjectInvocationContext(ContextProjectionOptions options);
```

**API 单一性决策**：考虑到本项目早期阶段 "避免兼容层" 的原则，且现有 `ProjectContext` 调用点仅 5 处（含定义与 1 个测试），不保留旧 API 入口。所有调用站点统一改用新接口：
- 真实模型调用：传入完整 `ContextProjectionOptions(TargetInvocation, ...)`
- 非调用场景（Recap / 测试）：传入 `new ContextProjectionOptions()`，自动得到等价于旧行为的 flat projection（无 thinking）
- 取扁平视图：`.ToFlat()` 一行搞定

**为什么 `TargetInvocation` 是可空的一等公民语义**：Recap 生成器、UI 展示、debug log dump 等场景**真的没有**目标 invocation——它们只想看 "假如有人现在请求，能看到什么前缀"。把 "无 invocation" 当成一等场景比当成兼容口更诚实，且实施层面统一为 "`null + 任何 ThinkingMode → 不注入 thinking`" 这一条规则。

---

## 4. 投影规则表

这张表是**整个方案的核心契约**。所有边界情况在这里钉死。

| 历史条目情况 | 是否保存到 `RecentHistory` | 投影到 `StablePrefix` | 投影到 `ActiveTurnTail` |
|--------------|----------------------------|------------------------|--------------------------|
| 普通 `ObservationEntry` | ✓ | ✓（原样） | — |
| `ToolResultsEntry` | ✓ | ✓（原样） | ✓（原样，若在当前 Turn 内） |
| `RecapEntry` | ✓ | ✓（原样） | ✓（原样，若在当前 Turn 内） |
| `ActionEntry` 中的 `TextBlock` | ✓ | ✓ | ✓ |
| `ActionEntry` 中的 `ToolCallBlock` | ✓ | ✓ | ✓ |
| 旧 Turn 的 `ThinkingBlock` | ✓ | ✗（剥离） | — |
| 当前 Turn 但 `block.Origin != TargetInvocation` 的 `ThinkingBlock` | ✓ | — | ✗（剥离） |
| 当前 Turn，`TargetInvocation == null`（非调用场景）的 `ThinkingBlock` | ✓ | — | ✗（剥离） |
| 当前 Turn，`block.Origin == TargetInvocation`，但 `HasExplicitStartBoundary == false` | ✓ | — | ✗（剥离） |
| 当前 Turn，`block.Origin == TargetInvocation`，`HasExplicitStartBoundary == true`，`ThinkingMode == None` | ✓ | — | ✗（剥离） |
| **当前 Turn，所有上述条件满足，`ThinkingMode == CurrentTurnOnly`** | ✓ | — | **✓（保留 OpaquePayload）** |

**保守原则**：
- ThinkingBlock 的默认行为是**剥离**，必须满足 4 个条件全部为真才保留
- Recap 裁掉 Turn 起点的情况下，profile lock 仍允许（"尽力推断"），但 thinking replay **拒绝**（"半截 replay 不安全"）
- 同 Turn 但不同 model 的 thinking 不可回灌（防止跨 model 加密格式不兼容触发协议错误）

---

## 5. Stream / Accumulator 改造

### 5.1 `CompletionChunk` 扩展

```csharp
namespace Atelia.Completion.Abstractions;

public enum CompletionChunkKind {
    Content,
    ToolCall,
    Thinking,    // 新增
    Error,
    TokenUsage
}

public sealed record CompletionChunk {
    public CompletionChunkKind Kind { get; init; }
    public string? Content { get; init; }
    public ParsedToolCall? ToolCall { get; init; }
    public ThinkingChunk? Thinking { get; init; }    // 新增
    public string? Error { get; init; }
    public TokenUsage? TokenUsage { get; init; }

    public static CompletionChunk FromThinking(ThinkingChunk thinking)
        => new() { Kind = CompletionChunkKind.Thinking, Thinking = thinking };

    // ... 其他工厂方法不变
}

/// <summary>
/// StreamParser 完成一个 thinking content block 的聚合后产出。OpaquePayload 由 parser 直接序列化为 provider-native bytes。
/// </summary>
public sealed record ThinkingChunk(
    ReadOnlyMemory<byte> OpaquePayload,
    string? PlainTextForDebug = null
);
```

### 5.2 关键边界（必须显式写入文档）

> **`CompletionChunk.Thinking.OpaquePayload` 由 StreamParser 直接以 provider-native bytes 形式构造，Agent.Core / CompletionAccumulator 不参与解释。**

这意味着：
- `AnthropicStreamParser` 把 `content_block_start` / `content_block_delta(thinking_delta)` / `content_block_stop` 三类 SSE 事件聚合成完整的 `{"type":"thinking","thinking":"...","signature":"..."}` JSON 对象，序列化为 bytes 写入 `OpaquePayload`
- `CompletionAccumulator` 只按到达顺序把 chunk 转成 `ActionBlock` 序列，对 OpaquePayload 完全透明
- `AnthropicMessageConverter` 回灌时 `JsonSerializer.Deserialize<AnthropicContentBlock>(payload.Span)` 即可，无需额外构造

这条边界是"为什么 Agent.Core 不会被 provider 细节污染"的真正担保。

### 5.3 `CompletionAccumulator` 改造

```csharp
internal sealed class CompletionAccumulator {
    private readonly List<ActionBlock> _blocks = new();
    private StringBuilder? _pendingTextBuffer;

    public void Append(CompletionChunk chunk, CompletionDescriptor invocation) {
        switch (chunk.Kind) {
            case CompletionChunkKind.Content:
                (_pendingTextBuffer ??= new StringBuilder()).Append(chunk.Content);
                break;

            case CompletionChunkKind.ToolCall:
                FlushPendingText();
                _blocks.Add(new ActionBlock.ToolCall(chunk.ToolCall!));
                break;

            case CompletionChunkKind.Thinking:
                FlushPendingText();
                _blocks.Add(new ActionBlock.Thinking(
                    invocation,
                    chunk.Thinking!.OpaquePayload,
                    chunk.Thinking.PlainTextForDebug
                ));
                break;

            case CompletionChunkKind.TokenUsage:
            case CompletionChunkKind.Error:
                /* 保持原有处理 */
                break;
        }
    }

    private void FlushPendingText() {
        if (_pendingTextBuffer is { Length: > 0 }) {
            _blocks.Add(new ActionBlock.Text(_pendingTextBuffer.ToString()));
            _pendingTextBuffer.Clear();
        }
    }

    public ActionEntry BuildActionEntry(CompletionDescriptor invocation) {
        FlushPendingText();
        return new ActionEntry(_blocks.ToArray(), invocation);
    }
}
```

**ordering 保真**：text content 用 buffer 累积，遇到非 text chunk 时 flush 一次，确保 `text → thinking → text → tool_use → text` 这样的交错形态被原样保留。

---

## 6. Provider 适配策略

| Provider / API | Thinking 写入支持 | 适配策略 |
|-----------------|-------------------|----------|
| **Anthropic Messages**（claude-3.7+ extended thinking） | ✓ 完整 | 第一版完整支持。StreamParser 聚合 thinking blocks，Converter 反序列化 OpaquePayload 还原为 native content block |
| **OpenAI Chat Completions**（o1/o3 reasoning_content） | ✗ 只读 | StreamParser **可选**地把 reasoning_content 写入 `ThinkingChunk`（用于审计/UI），Converter **永远丢弃** ThinkingBlock |
| **OpenAI Responses API** | ✓ 完整（reasoning items） | v1 不实现，但接口形态预留（已在 Blocks/ProjectedInvocationContext 中天然兼容） |
| **Google Gemini** | ✓ 完整（thought parts） | v1 不实现，同上 |

**Fail-fast 策略**：当 Converter 收到自身不支持的 ThinkingBlock 形态时（例如 OpaquePayload 反序列化失败），第一版选择**抛 InvalidOperationException 含明确诊断**，而非静默吞掉。原因：thinking replay 出错会触发 provider 校验失败，问题非常隐蔽，宁可显式失败。

---

## 7. 实施路线（5 步小 PR）

每步独立验证、独立 PR、独立可回滚。

### PR 1：Blocks 存储重构（不含 Thinking）

**目标**：把 `ActionEntry` 内部存储升级为 Blocks，但语义保持不变。

**改动范围**：
- 新增 `ActionBlock` sum type（仅 `Text` / `ToolCall` 两个子类型）
- `ActionEntry` 主构造参数改为 `IReadOnlyList<ActionBlock>`
- 保留旧构造重载（`string content + IReadOnlyList<ParsedToolCall> toolCalls + CompletionDescriptor`）
- `Content` / `ToolCalls` 改为派生属性
- `CompletionAccumulator` 内部改用 Blocks 累积

**验收**：
- 现有 100+ 测试零回归
- `dotnet build Atelia.sln` 0 警 0 错
- 不引入任何 thinking 相关代码

### PR 2：富接口与投影类型

**目标**：把投影层接口形态铺好，但投影规则保持现状。

**改动范围**：
- 新增 `IRichActionMessage` 接口（在 **Agent.Core.History**，非 Completion.Abstractions——避免反向依赖）
- 新增 `ProjectedActionMessage` record（在 Agent.Core.History）
- 新增 `ContextProjectionOptions` / `ThinkingProjectionMode` / `ProjectedInvocationContext`
- `AgentState.ProjectInvocationContext(options)` 实现：StablePrefix + ActiveTurnTail 用相同投影规则（即都不含 ThinkingBlock，因为还没引入）
- **删除** 旧 `AgentState.ProjectContext(string?)` 与 `AgentEngine.ProjectContext()`，所有 5 处调用站点（含 1 个测试）改用新 API
- Converter 端识别 `IRichActionMessage`（路径打通，但 Blocks 中暂无 ThinkingBlock）

**验收**：
- 现有测试零回归
- 新增投影层单元测试：StablePrefix / ActiveTurnTail 切分正确

### PR 3：CompletionChunk 扩展 + Anthropic StreamParser

**目标**：让 thinking 内容能从 Anthropic 流进入到 ActionEntry.Blocks。

**改动范围**：
- `CompletionChunkKind.Thinking` 枚举值
- `CompletionChunk.Thinking` 字段 + `ThinkingChunk` record
- `CompletionAccumulator.Append` 处理 Thinking case，构造 `ActionBlock.Thinking`
- `AnthropicStreamParser`：解析 thinking content_block 的 start/delta/stop 事件，聚合为 OpaquePayload
- 端到端流测试：Anthropic 模型返回 thinking 后，`ActionEntry.Blocks` 中能看到 `ThinkingBlock`

**验收**：
- 与真实 Anthropic API（或 mock）的端到端测试通过
- ThinkingBlock 的 OpaquePayload 反序列化能还原 `{type, thinking, signature}` 完整结构

### PR 4：投影层 Thinking 过滤 + Anthropic Converter 回灌

**目标**：闭环——thinking 能在同 Turn 内被回灌给 Anthropic。

**改动范围**：
- `ProjectInvocationContext` 实现 §4 投影规则表中的 ThinkingBlock 过滤逻辑
- `AnthropicMessageConverter` 在 `IRichActionMessage` 路径下识别 `ActionBlock.Thinking`，反序列化 OpaquePayload 为 native content block
- 完整测试矩阵（见 §8）

**验收**：
- §8 所有测试通过
- 与真实 Anthropic API 的 round-trip 测试：tool 往返后 thinking 仍被识别

### PR 5：文档与 Recap 兼容

**目标**：把方案落到 docs，并确保 Recap 不破坏 Blocks 结构。

**改动范围**：
- `docs/Agent/memory-notebook.md` 增加"Blocks 与 Thinking Replay"小节并引用本文档
- `docs/Completion/memory-notebook.md` 同步交叉引用
- Recap 生成器：处理 ActionEntry 时按 `Content` 派生视图工作（不解释 ThinkingBlock，不破坏其存在）
- 持久化原型（如 StateJournal）若涉及 ActionEntry 序列化，需为 Blocks 设计 schema

---

## 8. 测试矩阵

### 8.1 真相层（Blocks）

| # | 场景 | 期望 |
|---|------|------|
| T1 | 通过旧构造形式创建 ActionEntry | `Blocks` 含 `[Text(content), ToolCall(c1), ToolCall(c2)]` |
| T2 | 直接通过 Blocks 构造 | `Content` 为 text 块拼接，`ToolCalls` 为 toolcall 块 list |
| T3 | 含 Thinking 的 Blocks | `Content` 不含 thinking 内容，`ToolCalls` 不含 thinking |
| T4 | 空 Blocks | `Content == ""`，`ToolCalls == []` |

### 8.2 投影层

| # | 场景 | 期望 |
|---|------|------|
| P1 | 旧 Turn 的 ThinkingBlock | StablePrefix 中被剥离 |
| P2 | 当前 Turn 但 `block.Origin != TargetInvocation` | ActiveTurnTail 中被剥离 |
| P3 | 当前 Turn，`Origin == Target`，`HasExplicitStartBoundary == false`（recap 裁过） | ActiveTurnTail 中被剥离 |
| P4 | 当前 Turn，所有条件满足，`ThinkingMode == None` | ActiveTurnTail 中被剥离 |
| P5 | 当前 Turn，所有条件满足，`ThinkingMode == CurrentTurnOnly` | ActiveTurnTail 中保留 |
| P6 | StablePrefix / ActiveTurnTail 切分边界（恰好在 ObservationEntry 处） | 切分点正确 |
| P7 | 空 history | StablePrefix 与 ActiveTurnTail 均空 |
| P8 | 只有 Recap + ActionEntry（无显式 ObservationEntry） | StablePrefix 含 Recap，ActiveTurnTail 含 ActionEntry，thinking 被剥离 |

### 8.3 协议层（Anthropic）

| # | 场景 | 期望 |
|---|------|------|
| A1 | Stream parser 收到 thinking_delta SSE 事件 | 聚合为 ThinkingChunk，OpaquePayload 含完整 JSON |
| A2 | Converter 编码 ProjectedActionMessage 含 ThinkingBlock | request body 中 content 数组含原样 thinking block |
| A3 | Converter 收到 OpaquePayload 反序列化失败的 ThinkingBlock | 抛 InvalidOperationException 含诊断 |
| A4 | OpenAI Chat Converter 收到含 ThinkingBlock 的 message | ThinkingBlock 被静默丢弃，不进 request |

### 8.4 端到端

| # | 场景 | 期望 |
|---|------|------|
| E1 | Anthropic 模型返回 thinking + tool_use → 工具执行 → 第二次模型调用 | 第二次 request 中包含第一次的 thinking block |
| E2 | 同 Turn 内 profile lock 触发（已有测试） | 不受 Blocks 改动影响，继续通过 |
| E3 | 跨 Turn 切换 profile | 旧 Turn 的 thinking 不出现在新 Turn 的 request 中 |

---

## 9. 开放点（v1 不实现，但已识别）

### 9.1 已识别的 ActionBlock 扩展子类型

| 子类型 | 来源 | 说明 |
|--------|------|------|
| `Citation` | Anthropic | 引用元数据（claude 3.5+ 部分场景） |
| `ServerToolUse` | Anthropic | 服务器端工具（如 web_search）的工具调用记录 |
| `SafetyMeta` | 多家 | Refusal / safety policy 触发的元数据块 |

引入这些子类型时只需新增 `ActionBlock.Xxx` sealed record + `ActionBlockKind` 枚举值，无需修改投影/协议层框架。

### 9.2 OpenAI Responses API 接入

接口形态已天然兼容（Blocks 序列对应 Responses 的 items 数组）；具体 StreamParser / Converter 实现作为独立 PR。

### 9.3 ThinkingBlock 持久化

第一版 ThinkingBlock 仅在内存中存在；持久化到 StateJournal 时 OpaquePayload 的序列化策略（直接 base64？还是借用 binary framing？）需在 Persistence 设计文档中单独决策。

### 9.4 长会话的 ThinkingBlock GC

Recap 时是否主动删除 OpaquePayload 以节省内存？v1 暂不做，留待真实场景出现 OOM 压力时再设计。

---

## 10. 决策记录（Decision Log）

记录本方案讨论过程中的关键取舍，便于未来回顾。

### D1：为什么不用 `ActionEntry.ThinkingBlocks` 独立列表？

**否决理由**：丢失 assistant message 内部的 ordering。Anthropic extended thinking 协议事实是 `thinking / text / tool_use` 三类块可任意交错；OpenAI Responses items、Gemini parts 同理。独立列表无法回灌正确顺序。

### D2：为什么不走 "Turn 内 provider-native + Turn 结束转 portable" 重写？

**否决理由**（详见 Claude-Sendbox/CanonicalVsSidecar.md）：
- `ActionEntry.Blocks + OpaquePayload` 已能 lossless 表达所需事实
- 引入"两种条目共存"会带来 turn closure 触发点、降级转换器、Recap 双语义、序列化 schema 翻倍等成本
- 解决的是"旧 turn 不该被 provider 细节污染"的担忧，但该担忧已被 OpaquePayload 黑盒模式 + 投影层过滤化解

### D3：为什么不引入 `ActiveTurnReplayState` sidecar？

**否决理由**（详见 Claude-Sendbox/CanonicalVsSidecar.md 三个论证）：
1. 信息守恒矛盾：sidecar 丢弃后 thinking 内容去哪？三个候选（直接丢/沉淀回 ActionEntry/第三层存储）都有问题
2. 双源同步引入新故障面：崩溃重建、Recap 协调、StateJournal 协议、何时同步等问题都涌现
3. "history 被 provider 污染"的担忧已被 OpaquePayload + 投影过滤化解，sidecar 的额外工程收益不足以抵销其复杂度

### D4：为什么 `ActionBlock` 是 closed sum type 而非 open + `UnknownBlock` 兜底？

**理由**：万能逃生口会让新 provider 块类型不被强制设计为一等公民，最终成为各种边缘情况的垃圾桶。让 StreamParser 在遇到未知块时显式报错（或选择性丢弃 + 警告），迫使我们认真处理新协议特性。

### D5：为什么 `ThinkingBlock.Origin` 用 `CompletionDescriptor` 而非散字段？

**理由**：与 Turn lock 规则同构，replay 兼容性判定可直接 `block.Origin == options.TargetInvocation`，少三个散字段比较，可读性更好。

### D6：为什么 thinking replay 在 Recap 裁掉边界时拒绝，而 profile lock 容忍？

**理由**：
- profile lock 是防误用约束，"尽力推断"在断片场景仍能提供保护
- thinking replay 是"重新送回模型"的强语义操作，半截 replay 触发 provider 校验失败问题非常隐蔽
- 保守原则：宁可降级（剥离 thinking）也不做半截 replay

### D7：为什么 Recap 之前/之后的所有非 thinking 内容都进 StablePrefix，而非按 Recap 分割？

**理由**：StablePrefix / ActiveTurnTail 的边界**只**与 Turn 起点相关（即最近 ObservationEntry）。Recap 是历史压缩机制，与 Turn 边界正交。它产生的 `RecapEntry` 在投影时被原样穿过（不剥离任何东西），下游 converter 自行处理。

### D8：是否预留 OpenAI Responses API 接入？

**结论**：v1 不实现，但接口形态天然兼容。Responses items 数组（含 reasoning items）与 Blocks 序列概念同构，未来加 `OpenAIResponsesStreamParser` + `OpenAIResponsesMessageConverter` 即可，无需修改框架。

### D9：`IRichActionMessage` 为何不放在 `Completion.Abstractions`？

**理由**（GPT5 R2 提出）：`IRichActionMessage` 引用 `ActionBlock` 与 `CompletionDescriptor`，而这两者归属 `Agent.Core.History`。若把接口下沉到 `Completion.Abstractions`，则 Abstractions 必须反向引用 `Agent.Core`——这是架构边界错误。两个替代方案：

- 方案 A（**采纳**）：`IRichActionMessage` 留在 `Agent.Core.History`，与 `ActionBlock` / `CompletionDescriptor` 同层；converter 项目本来就引用 `Agent.Core`，无新增依赖。
- 方案 B（拒绝）：把 `ActionBlock` / `CompletionDescriptor` 上提到 `Completion.Abstractions`。理由：这两个类型本质是 Agent history 元信息，不是所有 completion client 都必须理解的最小公共抽象，强行上提会污染 Abstractions 边界。

### D10：`ContextProjectionOptions.TargetInvocation` 为何是可空的一等公民？

**理由**（GPT5 R2 提出 + Claude R3 提出语义动机）：项目早期阶段原则是"避免兼容层"，所以不保留旧 `ProjectContext(string?)` 入口。但完全去掉 "无 invocation" 投影也不对——Recap 生成器、UI 展示、debug log dump、单元测试**真的不需要**目标 invocation，它们只想看"假如有人现在请求，能看到什么前缀"。所以把 `TargetInvocation` 设为可空，不是兼容口而是一等公民语义场景。规则：`null + 任何 ThinkingMode → 不注入 thinking`（无目标即无回灌可能）。

### D11：`Content` compatibility view 为何用 `string.Concat` 而非 `string.Join('\n', ...)`？

**理由**（GPT5 R2 提出 + Claude R3 强化）：Anthropic / OpenAI / Gemini 的流式协议本身不保证 text block 之间存在隐含换行——块边界是 protocol-level 划分，不是排版语义。`Content` compat view 若强加 `\n` 反而会注入虚假信息，影响下游消费者（如 Recap、UI）的语义判断。换行语义应由具体 provider converter 在编码时按需添加。

---

## 11. 与现有 Turn Lock 文档的关系

本方案与 [docs/Agent/memory-notebook.md "Turn 与 LlmProfile 锁定"](./memory-notebook.md#turn-与-llmprofile-锁定硬约束) 完全互补：

- **Turn Lock**：保证同 Turn 内 profile 不切换（Provider/ApiSpec/Model 三元组严格匹配）
- **Thinking Replay**：在 Turn Lock 提供的稳定性基础上，按更严格的条件回灌 thinking（同 invocation + 显式起点）

实施 PR 4 完成后，应在 `memory-notebook.md` 中增加 "Blocks 与 Thinking Replay" 小节，引用本文档作为正式设计依据。

---

## 12. 致谢

本方案是 Claude Opus 4.7 与 GPT-5 通过多轮协同设计形成。完整讨论记录见：

- `gitignore/Claude-Sendbox/BlocksSequence.md` — Claude R1：提出 blocks 序列反提案
- `gitignore/GPT5-Sendbox/ActiveTurnReplayState.md` — GPT5 R1：提出双层 sidecar 方案
- `gitignore/Claude-Sendbox/CanonicalVsSidecar.md` — Claude R2：三论证反驳 sidecar
- `gitignore/GPT5-Sendbox/Reply-To-Claude-2026-04-25.md` — GPT5 R2：接受单层事实源，提出两个补强 + 两个小问题
- `gitignore/Claude-Sendbox/Reply-To-GPT5-2026-04-25.md` — Claude R3：采纳补强，拒绝 UnknownBlock 兜底
- `gitignore/GPT5-Sendbox/Reply-To-Claude-2026-04-25-R2.md` — GPT5 R3：指出依赖方向 + API 不对称两个硬问题
- `gitignore/Claude-Sendbox/Reply-To-GPT5-2026-04-25-R3.md` — Claude R3.5：采纳两项修正，提出“无 invocation 是一等公民”语义动机

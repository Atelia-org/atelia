# CS-5-lite-C 设计：RollingSummary Runner 输入源抽象

> 状态：Design / Ready for Implementation
> 日期：2026-07-25
> 对应 brief：[CS-5-lite-C: RollingSummary Runner 输入源抽象](cs-5-lite-C-runner-input-abstraction.md)

## 1. 结论

C 的目标是把 `RollingSummaryReplayRunner` 的核心循环从 `ChatSessionLegacyReplayEvent` 解耦，让它只消费
“某个 source step 追加了一批 history messages”这一更小合同。

第一版推荐仍放在 `prototypes/ChatSession.BacktestCli` 内，因为它服务 backtest runner，不是
`SessionJournal` 的长期库 API：

```text
prototypes/ChatSession.BacktestCli/
  RollingSummaryReplay.cs
  RollingSummaryReplaySources.cs   # 可选；若文件过大再拆
```

这样 C 可以独立完成：

- legacy `replay-rolling-summary` 行为保持。
- 新增 SessionJournal adapter 能从 `SessionJournalEngine.ReplayHistory()` 驱动同一 runner。
- 不接 `DerivedRecapStore` 写入；D 再把 produced result 写成 artifact。
- 不新增最终 CLI 命令；E 再收命令名、README 和端到端验收。

## 2. 核心输入模型

runner 核心只依赖下面两个内部模型：

```csharp
internal interface IRollingSummaryReplaySource {
    string SourceKind { get; }
    IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync(CancellationToken ct);
}

internal sealed record RollingSummaryReplayStep(
    RollingSummaryReplaySourceCursor Cursor,
    IReadOnlyList<IHistoryMessage> AppendedMessages,
    bool IsTriggerBoundary
);

internal sealed record RollingSummaryReplaySourceCursor(
    string SourceKind,
    string SourceId,
    long? EventOrdinal = null,
    string? EventCommit = null,
    EventAddress? SourceStartInclusive = null,
    EventAddress? SourceEndInclusive = null,
    EventAddress? SourceRawHead = null
);
```

语义：

- `AppendedMessages` 是本 step 追加进 `_activeHistory` 的 message。为空 step 可用于保留 source cursor，但第一版
  runner 可以直接跳过。
- `IsTriggerBoundary` 表示追加完成后是否允许 threshold/split 检查。legacy 中只在 `model-turn` 后触发；
  SessionJournal 中建议在每个 `ActionMessage` 或 `ToolResultsMessage` 后触发。
- `Cursor.SourceId` 是 human-facing 诊断 id：
  - legacy：`commit ?? ordinal.ToString(InvariantCulture)`。
  - SessionJournal：`EventAddressTextCodec.Format(SourceEndInclusive)`。
- `SourceStartInclusive` / `SourceEndInclusive` 是 step 覆盖的 raw source range。legacy 没有 raw address，填 null。
- `SourceRawHead` 是 adapter 开始 replay 时看到的 raw head；legacy 填 null。

## 3. Legacy Adapter

第一版 legacy adapter 应保持现有语义：

```csharp
internal sealed class LegacyRollingSummaryReplaySource : IRollingSummaryReplaySource {
    public LegacyRollingSummaryReplaySource(ChatSessionLegacyEventSource eventSource);
}
```

映射规则：

- `initial-state`：输出一个 `IsTriggerBoundary = false` 的 step，`AppendedMessages = messages`，用于初始化
  `_activeHistory`。
- `model-turn`：输出 `IsTriggerBoundary = true` 的 step，`AppendedMessages = appendedMessages`。
- `update-system-prompt` / `compaction` / `redundant-save`：不输出 step。
- ordinal 必须与遍历 index 一致；不一致继续抛 `InvalidDataException`。

注意：这和 `ChatSessionLegacyReplayCursor` 的完整 replay 不同。rolling summary 现有策略本来就忽略原始
compaction 和 system prompt change；C 保持这个行为，不在 legacy adapter 里引入 `ContextHeader` 或 recap。

## 4. SessionJournal Adapter

新增 adapter：

```csharp
internal sealed class SessionJournalRollingSummaryReplaySource : IRollingSummaryReplaySource {
    public static SessionJournalRollingSummaryReplaySource Open(string sessionJournalRepoPath);
}
```

实现方式：

1. `using var engine = SessionJournalEngine.Open(sessionJournalRepoPath)`。
2. 调用 A 分片提供的 `engine.ReplayHistory()`。
3. 遍历 `SessionHistoryReplay.Messages`，每个 `AddressedSessionHistoryMessage` 产出一个 step：
   - `AppendedMessages = [addressed.Message]`。
   - `SourceStartInclusive = addressed.SourceStartInclusive`。
   - `SourceEndInclusive = addressed.SourceEndInclusive`。
   - `SourceRawHead = replay.SourceRawHead`。
   - `IsTriggerBoundary = addressed.Message.Kind is Action or ToolResults`。
4. setup/session-created 不进入 history；这已经由 `ReplayHistory()` 和 `SessionReducer` 保证，adapter 不重写 reducer。

SessionJournal adapter 不负责：

- governing setup lookup。
- derived recap latest lookup。
- raw suffix after recap anchor。
- 写 artifact。

这些都留给 D/E 或后续 tail projection。

## 5. Runner 变化

`RollingSummaryReplayRunner` 构造函数改为接受 `IRollingSummaryReplaySource`：

```csharp
public RollingSummaryReplayRunner(
    IRollingSummaryReplaySource source,
    ICompletionClient client,
    CompletionConnectionConfig connection,
    ReplayMemoryMaintainerProfile profile,
    string callLogDir,
    int thresholdTokens,
    int maxEpochs);
```

内部循环：

1. 从 source 读取 step。
2. 若 `step.AppendedMessages.Count > 0`，追加到 `_activeHistory`。
3. 若 `!step.IsTriggerBoundary`，继续下一 step。
4. threshold 达标后按现有 `HistoryWindowSplitPolicy` 找 split。
5. `fragment = _activeHistory[..split]`。
6. `RecentHistorySlice.SourceId = step.Cursor.SourceId`。
7. maintainer 成功后 `_activeHistory.RemoveRange(0, split)`。
8. 输出 record。

## 6. Record 兼容与新增字段

现有 JSONL schema 可以继续使用 `atelia.chat-session.memory-maintainer-backtest.v2`，但 record 应补充
SessionJournal 所需诊断字段：

```csharp
string SourceKind,
string SourceId,
long? EventOrdinal,
string? EventCommit,
string? SourceRawHead,
string? SourceStartInclusive,
string? SourceEndInclusive
```

兼容策略：

- legacy record 保持原有 `eventOrdinal` / `eventCommit` 字段值。
- 新字段总是写出；legacy address 字段为 null。
- SessionJournal record 的 `eventOrdinal` / `eventCommit` 为 null，address 字段用
  `Atelia.SessionJournal.Derived.EventAddressTextCodec` 格式化。

`CompletionCallLogContext.EventOrdinal` 仍只能表达 legacy ordinal。第一版 SessionJournal 模式可传 null；
D/E 后续若需要 raw address 写入 call log，可扩展 `CompletionCallLogContext`。

## 7. CLI 边界

C 可以选择只改内部结构，不新增用户命令；E 再新增正式命令。

为了验证 SessionJournal adapter，C 允许新增一个内部/helper 入口或测试直接构造 source。若实现者认为手工验收需要
CLI，可增加非破坏性选项：

```bash
replay-rolling-summary --session-journal-input <repo-dir> ...
```

但不要改变现有 `--input` legacy 语义，也不要在 C 中写 derived artifact。

## 8. 测试策略

当前 `ChatSession.BacktestCli` 没有测试项目。C 推荐新增轻量测试项目：

```text
tests/ChatSession.BacktestCli.Tests/ChatSession.BacktestCli.Tests.csproj
```

测试项目引用：

- `prototypes/ChatSession.BacktestCli`
- `prototypes/Completion.Abstractions` 如需要 stub client
- xUnit / Microsoft.NET.Test.Sdk

因为 runner 和 source 类型是 `internal`，可在 CLI 项目添加：

```csharp
[assembly: InternalsVisibleTo("Atelia.ChatSession.BacktestCli.Tests")]
```

最小测试：

1. `LegacySource_PreservesExistingTriggerShape`
   - 构造 initial-state + model-turn。
   - runner 使用 scripted completion client、低 threshold、`maxEpochs = 1`。
   - 断言输出一条 record，`sourceKind = legacy-chat-session-export`，`eventOrdinal` 为 model-turn ordinal，
     address 字段为 null。

2. `SessionJournalSource_UsesAddressedReplayAndSameRunner`
   - 创建 SessionJournal repo，append observation + action。
   - runner 使用 `SessionJournalRollingSummaryReplaySource.Open(path)`。
   - 断言输出一条 record，`sourceKind = session-journal`，`sourceEndInclusive` 非 null，
     `eventOrdinal/eventCommit` 为 null。

3. `Runner_RemovesSlidingPrefixAfterSuccessfulMaintainer`
   - 使用多个 action boundary 或低阈值。
   - 断言 record 的 `remainingActiveMessageCount` 与 split 后剩余数量一致。

4. `SessionJournalSource_EmptyHistoryProducesNoRecords`
   - 只有 setup/session-created。
   - runner 不触发 maintainer。

验证命令：

```bash
dotnet test tests/ChatSession.BacktestCli.Tests/ChatSession.BacktestCli.Tests.csproj
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj
dotnet build prototypes/ChatSession.BacktestCli/ChatSession.BacktestCli.csproj
```

## 9. 完成标准

- `RollingSummaryReplayRunner` 核心不再持有 `ChatSessionLegacyEventSource`。
- legacy replay command 继续可构造 runner 并保持原 JSONL 关键字段。
- SessionJournal adapter 复用 `SessionJournalEngine.ReplayHistory()`，不复制 reducer。
- record 能表达 legacy ordinal/commit 与 SessionJournal raw address。
- 不写 `DerivedRecapStore`；D 分片只需在 record/result 上接 artifact 写入。

## 10. 残余风险

- SessionJournal adapter 第一版从 full `ReplayHistory()` 开始，不做 recap anchor tail replay；这正是 D/E 之前的
  bootstrap 行为。
- `CompletionCallLogContext` 暂不记录 raw address；输出 record 已有 address 字段，后续可再扩展 call log。
- Backtest CLI 仍处于实验工具定位，新增测试项目只覆盖 runner/source，不做真实 LLM 或端到端 CLI black-box。

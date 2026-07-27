# CS-5-lite-C 设计：RollingSummary Runner 输入源抽象

> 状态：Implemented / Ready for D Handoff
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

internal sealed record RollingSummaryReplayMessage {
    public IHistoryMessage Message { get; }
    public EventAddress? SourceStartInclusive { get; }
    public EventAddress? SourceEndInclusive { get; }
}

internal sealed record RollingSummaryReplayStep(
    RollingSummaryReplaySourceCursor TriggerCursor,
    IReadOnlyList<RollingSummaryReplayMessage> AppendedEntries,
    bool IsTriggerBoundary,
    bool ResetActiveHistory = false
);

internal sealed record RollingSummaryReplaySourceCursor(
    string SourceKind,
    string SourceId,
    long? EventOrdinal = null,
    string? EventCommit = null,
    EventAddress? SourceRawHead = null
);
```

语义：

- `AppendedEntries` 是本 step 追加进 `_activeHistory` 的 message entry。每个 entry 携带自己的 optional raw
  range；构造时强制 start/end 同时存在或同时缺失，避免产生半截 provenance。
- `IsTriggerBoundary` 表示追加完成后是否允许 threshold/split 检查。legacy 中只在 `model-turn` 后触发；
  SessionJournal 中建议在每个 `ActionMessage` 或 `ToolResultsMessage` 后触发。
- `TriggerCursor` 只描述“在哪个 source step 触发了本次 threshold 检查”，不描述被 sliding fragment
  实际吸收的范围。
- `TriggerCursor.SourceId` 是 human-facing 诊断 id：
  - legacy：`commit ?? ordinal.ToString(InvariantCulture)`。
  - SessionJournal：`EventAddressTextCodec.Format(SourceEndInclusive)`。
- message entry 的 `SourceStartInclusive` / `SourceEndInclusive` 是该 message 覆盖的 raw source range。
  legacy 没有 raw address，两者均为 null。
- `TriggerCursor.SourceRawHead` 是 adapter 开始 replay 时看到的 raw head snapshot；同一次 SessionJournal
  replay 产生的所有 step 共享它，legacy 填 null。
- runner 以 ordinal 比较要求 `_source.SourceKind == step.TriggerCursor.SourceKind`，并要求一次
  `RunAsync` 内所有 step 的 `SourceRawHead` 恒定（包括恒为 null）；违反时抛 `InvalidDataException`。
- selected fragment 必须全部 addressed 或全部 unaddressed，且 addressed 状态必须与 trigger cursor
  是否带 `SourceRawHead` 一致；违反时在调用 maintainer 前抛 `InvalidDataException`，不生成 record。

将 trigger cursor 与 message provenance 分开是 D handoff 的关键：触发 split 的 step 往往晚于 fragment
末尾，不能把 trigger step 的地址当作 recap anchor。

## 3. Legacy Adapter

第一版 legacy adapter 应保持现有语义：

```csharp
internal sealed class LegacyRollingSummaryReplaySource : IRollingSummaryReplaySource {
    public LegacyRollingSummaryReplaySource(ChatSessionLegacyEventSource eventSource);
}
```

映射规则：

- `initial-state`：输出一个 `IsTriggerBoundary = false` 的 step，`AppendedEntries = messages`，用于初始化
  `_activeHistory`。
- `model-turn`：输出 `IsTriggerBoundary = true` 的 step，`AppendedEntries = appendedMessages`。
- `update-system-prompt` / `compaction` / `redundant-save`：不输出 step。
- ordinal 必须与遍历 index 一致；不一致继续抛 `InvalidDataException`。
- legacy entry 的 raw range 始终为 null；record 的既有 `sourceId` / `eventOrdinal` / `eventCommit`
  仍来自 trigger cursor。

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
   - `AppendedEntries = [new RollingSummaryReplayMessage(addressed.Message, addressed.SourceStartInclusive,
     addressed.SourceEndInclusive)]`。
   - `TriggerCursor.SourceId = Format(addressed.SourceEndInclusive)`。
   - `TriggerCursor.SourceRawHead = replay.SourceRawHead`。
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
2. 校验 step `SourceKind` 与本次 replay 的 `SourceRawHead` snapshot invariant。
3. 若 `step.ResetActiveHistory`，先清空 `_activeHistory`；再把 `step.AppendedEntries` 追加进去。
4. 若 `!step.IsTriggerBoundary`，继续下一 step。
5. 仅将 entry 投影成 `IHistoryMessage` 后交给现有 token estimation 与 `HistoryWindowSplitPolicy`；
   trigger boundary 和 split policy 均不改变。
6. `fragmentEntries = _activeHistory[..split]`；在创建 maintainer 前验证 fragment provenance
   homogeneous，且其 addressed 状态与 trigger cursor 的 `SourceRawHead` 状态一致。
7. maintainer 输入
   `fragment = fragmentEntries.Select(entry => entry.Message)`。
8. attempted fragment 的 raw range 取
   `fragmentEntries[0].SourceStartInclusive` 到 `fragmentEntries[^1].SourceEndInclusive`。
9. `RecentHistorySlice.SourceId = step.TriggerCursor.SourceId`，继续保留 trigger 诊断语义。
10. maintainer 成功后 `_activeHistory.RemoveRange(0, split)`。
11. 对 runner catch filter 捕获并报告的 maintainer/runtime 失败，不移除 prefix、停止本次 replay，
    并在 failed record 中写入第 8 步算出的 attempted fragment range。source contract
    `InvalidDataException` 属于 fail-fast，不转成 failed record。

因此，在四条 history message
`[obs1, action1, obs2, action2]` 上由 `action2` 触发 split、`splitIndex = 2` 时：

- trigger `SourceId` 指向 `action2`；
- selected fragment 是 `[obs1, action1]`；
- record `SourceEndInclusive` 指向 `action1`，而不是 `action2`。

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
- `sourceId` / `eventOrdinal` / `eventCommit` 表示 trigger step。
- `sourceRawHead` 表示 replay snapshot，不会随 epoch 改写。
- `sourceStartInclusive` / `sourceEndInclusive` 表示 selected/attempted fragment，而不是 trigger step。
- D 的 artifact candidate 至少必须同时满足：`status == "succeeded"`、`sourceKind == "session-journal"`，
  且 `sourceRawHead` / `sourceStartInclusive` / `sourceEndInclusive` 均非 null；其
  `sourceEndInclusive` 可直接用作 `anchorRawEvent`。
- runner catch filter 捕获并报告的 failed record 只用于诊断，不得产出 artifact；此时
  `remainingActiveMessageCount` 仍包含 attempted prefix。source contract 违规直接抛异常，不产生 record。

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

已新增轻量测试项目：

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

当前回归测试覆盖：

1. `LegacySource_PreservesExistingTriggerShape`
   - 构造 initial-state + model-turn。
   - runner 使用 scripted completion client、低 threshold、`maxEpochs = 1`。
   - 断言输出一条 record，`sourceKind = legacy-chat-session-export`，`eventOrdinal` 为 model-turn ordinal，
     address 字段为 null。

2. `SessionJournalSource_UsesAddressedReplayAndSameRunner`
   - 创建两回合 SessionJournal repo，使 `action2` 触发四消息 split。
   - 断言 trigger `sourceId = action2`，fragment `sourceEndInclusive = action1`。

3. `Runner_RemovesSlidingPrefixAfterSuccessfulMaintainer`
   - 使用多个 action boundary 或低阈值。
   - 断言 record 的 `remainingActiveMessageCount` 与 split 后剩余数量一致。

4. `SessionJournalSource_EmptyHistoryProducesNoRecords`
   - 只有 setup/session-created。
   - runner 不触发 maintainer。

5. 连续两个 epoch 的 fragment range 分别来自各自首尾 entry，且 `sourceRawHead` 相同。

6. maintainer 失败时 record 仍报告 attempted fragment range，active prefix 不移除。

7. `RollingSummaryReplayMessage` 拒绝只有 start 或只有 end 的 partial raw range。

8. custom source 的 mixed provenance、fragment/raw-head 状态不一致、raw-head drift 与 source-kind
   mismatch 均 fail-fast；fragment contract 违规发生时 maintainer 尚未被调用。

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
- trigger cursor 与每条 message 的 raw range 在类型结构上分离。
- record 的 raw endpoints 来自 selected fragment 首尾 entry；成功 record 可直接作为 D 的 anchor 输入。
- source kind、raw-head snapshot、fragment provenance homogeneous 与 fragment/raw-head 状态匹配均由
  runner fail-fast enforcement。
- runner 捕获并报告的失败 record 保留 attempted provenance，且不会移除 active prefix。
- 不写 `DerivedRecapStore`；D 分片只需在 record/result 上接 artifact 写入。

## 10. 残余风险

- SessionJournal adapter 第一版从 full `ReplayHistory()` 开始，不做 recap anchor tail replay；这正是 D/E 之前的
  bootstrap 行为。
- `CompletionCallLogContext` 暂不记录 raw address；输出 record 已有 address 字段，后续可再扩展 call log。
- legacy source 缺少 raw address，因此其成功 record 仍不能直接生成带 raw anchor 的 SessionJournal recap artifact。
- runner 会拒绝 source-kind mismatch、raw-head drift、mixed provenance 以及 fragment/raw-head 状态不一致；
  内置 legacy 与 SessionJournal adapter 均满足这些 invariant。
- Backtest CLI 仍处于实验工具定位，新增测试项目只覆盖 runner/source，不做真实 LLM 或端到端 CLI black-box。

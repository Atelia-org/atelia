# SessionJournal Derived Recap Cadence：目标设计

> **状态**：Target Design / Implementation Guidance
> **日期**：2026-07-30
> **实施状态**：C0、C1、C2 已实现；本文的 HistoryUnit-count cadence是过渡期
> historical baseline。H0～H2随后已完成 breaking cutover，当前 production authority是
> [HistoryLoad设计](derived-recap-history-load-target-design.md)。
> **上位设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)
> **配置设计**：
> [Repo-owned RecapPlannerConfig](recap-planner-config-repository-design.md)
> **后续计量设计**：
> [Derived Recap History Load](derived-recap-history-load-target-design.md)

## 0. 目标

Rolling Recap 不应吞掉全部近期历史。它维护一个稳定的 recent-history reserve：

```text
Recap cold-prefix approximation
  + 至少 R 个 dependency-closed recent HistoryUnits
```

当 reserve 之外又积累 B 个 HistoryUnits 时，Planner 才建立新 Building：

```text
R = MinimumRecentHistoryUnitCount
B = RecapBuildIntervalUnitCount
G = latest Published SetAdmissionAnchor 后的 HistoryUnitCount

G < R + B  -> NoBuild
G >= R + B -> 选择 admission，Recap 至少 B 个，仍保留至少 R 个
```

例如 `R=20, B=24`，在 replay-safe boundaries足够密集的理想情况下：

```text
suffix units: 20 -> 21 -> ... -> 43 -> build -> 20
```

一般承诺是 `build -> recent >= 20`。正常运行时每新增约 24 个有效 Context messages rolling一次；
completion API request prepare、attempt、failure或 retry不推进 cadence。

## 1. 计量单位

V1 使用现有 `SessionHistoryPlanningUnit` 作为 `HistoryUnit`：

- 一个 unit 对应一个实际进入 Context 的 dependency-closed `IHistoryMessage`；
- Observation 是一个 unit；
- 最终 Agent Action 是一个 unit；
- 一组完整 tool results投影为一个 `ToolResultsMessage` unit；
- `CompletionRequestPrepared`、`CompletionAttemptStarted`、
  `CompletionAttemptFailed`、setup与其他只服务 durable protocol 的 raw events不是 unit。

这里排除的是 completion API attempt失败/retry。Tool execution即使返回 error，只要最终投影成
`ToolResultsMessage`，仍然是一个进入 Context 的 HistoryUnit。

`SessionHistoryPlanningBoundary.CompletedUnitCount` 是 replay-safe raw boundary上的累计 unit count。
Planner必须保留这项信息；不得再把 boundary降格成只有 `EventAddress` 的数组后用 Parent-lineage
距离代替。

这里的 “recent history 20 条”因此指 20 个 Context history messages，不是 20 个 raw events，
也不是 20 个 user/agent turns。若未来需要 turn-count语义，应增加另一种 estimator，而不是改变
`HistoryUnit`含义。

## 2. Authority 与不变量

Cadence只决定是否建立**新的** Building及其 `SetAdmissionAnchor`：

- raw SessionJournal仍是 event、Parent lineage与 history projection authority；
- Planner拥有 `RecapCadenceConfig`、trigger与 admission selection；
- Store不解释 cadence，不保存 active config；
- Building安装后 manifest仍是 Resume authority；
- Published set仍是 strict ordinal与 materialization authority；
- Resume/Restore不读取 active cadence config，也不重新计算 admission。

`SetAdmissionAnchor` 是 Recap prefix与 raw recent suffix的唯一分界：

```text
Maintainer input/window <= SetAdmissionAnchor
raw recent suffix        > SetAdmissionAnchor
```

所有 selected Recap contributions与 admission之后的 dependency-closed raw suffix共同进入 Context。
因此 recent reserve不需要新增 Store字段或 manifest字段。

## 3. Exact cadence algorithm

### 3.1 Growth

Existing Published：

```text
cadence baseline = latest Published SetAdmissionAnchor
```

Empty-recap baseline（尚无 Published Recap，不等同于 raw core 的 strict fresh bootstrap）：

```text
cadence baseline = core验证的 EmptyReplayStartExclusive
```

Executor可以继续为了 lagging block cursor读取一个早于 cadence baseline的 `allRelevantRaw`大
window，但 cadence count必须归一化：

```text
if cadence baseline == allRelevantRaw.StartExclusive:
    L = 0
else:
    L = exact baseline boundary.CompletedUnitCount

T = allRelevantRaw.Units.Count
G = T - L

C = candidate boundary.CompletedUnitCount - L
```

`StartExclusive`本身不出现在 `ReplaySafeBoundaries`数组中，所以必须显式处理 `L=0`。其他情况下
baseline必须是 window内唯一、exact replay-safe boundary；不在 window或非 replay-safe时返回
typed `CadenceBaselineInvalid`。baseline后的 exact raw-event count也必须用同一分支确定起点。

不得直接把大 window的 `Units.Count`当作 `G`，否则某个长期 Inherit block的旧 cursor会把已经
Published的历史重复计入 cadence。Empty-recap baseline的 normalized count为 0。

只有 exact `SessionHistoryPlanningWindow`产生最终 scheduling decision。Header-only raw distance最多
用作安全的 negative prefilter：

```text
raw distance < R + B -> 一定 NoBuild
raw distance >= R + B -> 仍须读取 exact planning window，不能直接 Build
```

因为 raw distance包含 API failure/retry、setup与 durable protocol events，它不能成为 cadence
authority。

`MaxRawGrowthEventCount`同样只统计 cadence baseline之后的 exact raw range；empty-recap baseline不得
拿整条 root lineage长度裁决，否则 setup prefix会造成 false backpressure。更老 block cursor到
baseline的 raw成本由 per-step/per-build limits独立约束。

### 3.2 Trigger

```text
if G < checked(R + B):
    NoBuild(BelowCadenceThreshold)
```

`R >= 0`，`B > 0`，构造 config时对 `R + B`做 checked validation。V1还要求：

```text
checked(R + B) <= MaxRawGrowthEventCount
```

每个 HistoryUnit至少需要一个 raw event；若不满足此式，raw safety gate会令 cadence threshold永远
不可达。

### 3.3 Admission candidates

对 window中的每个 replay-safe boundary计算：

```text
C = boundary.CompletedUnitCount - cadence baseline CompletedUnitCount
recent = G - C
```

Cadence-eligible boundary必须同时满足：

```text
C >= B
recent >= R
boundary严格晚于 latest Published admission
boundary严格晚于每个需要推进的 block cursor
```

在满足 cadence与现有 route/call/raw safety budgets的 candidates中：

1. 选择最大的 `C`，即尽量只留下 minimum recent reserve；
2. 多个 boundary具有相同 `C` 时选择 raw lineage上最新的一个，把零-unit retry/protocol events
   留在 admission之前；
3. final endpoint必须等于所选 `SetAdmissionAnchor`。

如果 `G >= R + B`，但 dependency closure尚未产生符合 `C >= B && recent >= R` 的 replay-safe
boundary，返回 `NoBuild(AwaitingReplaySafeAdmission)`。这不是损坏或 backpressure。

如果 cadence candidate存在，但全部违反 route/call/raw hard limits，继续返回现有 typed
limit defects，由 online lifecycle映射为 backpressure；不得牺牲 reserve把 admission推到 head。

### 3.4 为什么 reserve 是 minimum

一个 tool action到完整 tool results之间不可截断；某些 boundary的 `CompletedUnitCount`会跳过多个
units。因此合法 admission不一定能留下恰好 R 个 unit。

Planner承诺：

```text
recent >= R
```

不承诺：

```text
recent == R
```

### 3.5 Delayed catch-up

若维护延迟，`G`可能远大于 `R + B`。Planner按 `C`从大到小尝试 cadence-safe candidates：

- 一个 Building可用 bounded intermediate `CatchUpThrough` endpoints追赶；
- 最靠近 head的 candidate超出 budgets时，可回退到更旧但仍满足 `C >= B && G-C >= R` 的
  candidate，形成 bounded partial progress；
- partial progress发布后，下一次 Run若仍满足 threshold就继续追赶；
- intermediate endpoints不形成 Published set或 strict ordinal；
- 正常情况下不为了补齐每个遗漏 interval连续发布多个 sets；
- 一次 Run最多发布一个 set；所有 cadence-safe candidates都超限时才 typed backpressure。

### 3.6 首个 Recap 之前的 raw-history mode

当 Store尚无 Published Recap且 `G < R+B` 时，Planner返回 `NoBuild`；这不是“会话必须没有任何
operational history”的 strict fresh bootstrap。Online coordinator只有在以下 exact shape同时成立时
返回 `SessionContextLifecycleStatus.RawHistoryReady`：

```text
initial selection == EmptyLineage
planning result   == NoBuild
final selection   == EmptyLineage
```

raw core收到这个显式 outcome后，才允许把 `SessionCreated -> current boundary`的完整 raw planning
window作为当前 Context history。普通 `Ready + mature EmptyLineage`继续拒绝；声称 Published后仍
Empty、Selected消失、ordinal/invalid/store unavailable均不得降级成 raw history。

这样 `R=20, B=24` 时的前 43 个 HistoryUnits可以保持近期思路连续性，第 44 个 unit开始满足首个
Recap trigger。raw-only请求仍受 canonical request byte guard；`RawHistoryReady`不是绕过 context
容量或 topology检查的通用 fallback。

## 4. `RecapCadenceConfig`

目标 runtime shape：

```csharp
public sealed record RecapCadenceConfig {
    public RecapCadenceConfig(
        int minimumRecentHistoryUnitCount,
        int recapBuildIntervalUnitCount
    ) {
        if (minimumRecentHistoryUnitCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRecentHistoryUnitCount)
            );
        }
        if (recapBuildIntervalUnitCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(recapBuildIntervalUnitCount)
            );
        }
        _ = checked(
            minimumRecentHistoryUnitCount
            + recapBuildIntervalUnitCount
        );
        MinimumRecentHistoryUnitCount =
            minimumRecentHistoryUnitCount;
        RecapBuildIntervalUnitCount =
            recapBuildIntervalUnitCount;
    }

    public int MinimumRecentHistoryUnitCount { get; }
    public int RecapBuildIntervalUnitCount { get; }
}

public sealed class RecapPlanningInputs {
    public IReadOnlyList<RecapBlockCatalogEntry> OrderedCatalog { get; }
    public RecapCadenceConfig Cadence { get; }
    public IRecapPlanningPolicy Policy { get; }
}

public sealed record RecapPlanningLimits {
    // Repo-owned planning ceilings...
}
```

`IRecapPlanningPolicy`必须暴露稳定`Id`。code-owned resolution catalog在注册时校验
`registration key == policy.Id`（estimator同样校验 key与`Id`），重复或错配立即 fail closed；
policy抛出的可捕获普通异常映射为 typed `PolicyFailed`；`OperationCanceledException`与
`OutOfMemoryException`、`StackOverflowException`、`AccessViolationException`等 fatal异常原样传播。

Persisted JSON：

```json
{
  "cadence": {
    "minimumRecentHistoryUnitCount": 20,
    "recapBuildIntervalUnitCount": 24
  }
}
```

V1 breaking cutover删除：

```text
RawGrowthTrigger
```

不得同时保留 `RawGrowthTrigger` 与 `RecapCadenceConfig` 两套 scheduling authority。

`RawGrowthHardLimit` breaking rename为 `MaxRawGrowthEventCount`；
`MaxRawGrowthEventCount`、`MaxRawEventsPerStep`、`MaxRawEventsPerBuild`仍按 raw events计量，但只
属于 resource/safety budgets：

- 它们可以对 pathological retry storm或过大 raw traversal施加 backpressure；
- 它们不得触发正常 rolling cadence；
- 报告必须把 `HistoryUnit growth` 与 `raw event safety counts` 分开输出。

## 5. Planner facts 与 evaluator

实现时建议显式区分：

```text
RecapCadenceFacts
  GrowthUnitCount
  ReplaySafeBoundaries(Address + CompletedUnitCount)

RecapRawSafetyFacts
  RawGrowthEventCount
  per-step / per-build raw counts
```

`DerivedRecapPlannerExecutor`当前读取 `SessionHistoryPlanningWindow` 后必须保留 boundary
`CompletedUnitCount`并传给 Planner。`RecapPlanEvaluator`负责：

- config shape与 checked threshold；
- exact growth trigger；
- cadence candidate invariants；
- plan final reserve验证。

`BoundedMaintainAllRecapPlanningPolicy`负责：

- 从合法 cadence candidates中按确定性顺序试算；
- 应用 route/call/raw budgets；
- 返回 selected admission与 per-block decisions。

传给 Maintainer 的`RecentHistorySlice.SourceId`只使用
`EventAddressTextCodec.Format(startExclusive) + ".." + EventAddressTextCodec.Format(rawHead)`；
不得依赖`EventAddress.ToString()`的非 wire 文本。

Evaluator必须独立复算：

```text
absorbed unit count >= B
remaining unit count >= R
```

不能只信任 policy返回的 address。

## 6. 实施交接

唯一 canonical施工顺序维护在
[Repo-owned RecapPlannerConfig §9](recap-planner-config-repository-design.md#9-实施工作包) 的
`C0～C3`，不在本文建立第二套 package authority。

本文为其中 C0 的 cadence Shape/Rule输入；C0已完成：

- `RecapCadenceConfig`与 `RecapPlanningInputs.Cadence`；
- 删除 `RawGrowthTrigger`，将 `RawGrowthHardLimit`重命名为 `MaxRawGrowthEventCount`；
- normalized baseline、exact evaluator与 deterministic admission policy；
- header-only negative prefilter、delayed budget fallback及 focused tests。

C1也已完成 repo document/composition、runtime authority split与管理命令；C2已完成
CLI/online durable phase与 Building-first cutover。后续先按
[Derived Recap History Load](derived-recap-history-load-target-design.md)完成 H0～H2，再由 C3负责
Galatea real acceptance。

## 7. 验收矩阵

- `R=0`、`B=1`最小边界；
- negative、zero interval、`R+B` overflow拒绝；
- `G=R+B-1` NoBuild；
- `G=R+B`且存在 `C>=B && G-C>=R` 的 replay-safe boundary时 Build，否则
  `NoBuild(AwaitingReplaySafeAdmission)`；
- empty-recap baseline与 existing Published使用同一公式；
- baseline等于 `StartExclusive`时 `L=0`；否则要求 exact replay-safe boundary，缺失/非安全返回
  `CadenceBaselineInvalid`；
- Observation/Action/complete ToolResults按 Context messages计数；
- completion Prepared/Started/Failed/retry产生零 unit；error ToolResults仍计一个 unit；
- 同 `CompletedUnitCount`多个 boundary选择 raw最新者；
- dependency closure无法留下 exact R时保留大于 R；
- 无 cadence-safe boundary时 typed NoBuild，不写 Building、不调 Maintainer；
- delayed growth按 budgets回退到 older cadence-safe candidate，一次 Run只发布一个 final set，
  intermediate endpoints不占 ordinal；
- policy伪造导致 reserve不足时 evaluator拒绝；
- raw hard limit仍可 backpressure，但不改变 unit growth；
- partial Building按 frozen manifest Resume，不读取或重算 cadence；
- Published Restore不改变 admission或 recent suffix provenance。

## 8. HistoryLoad当前 authority

`HistoryUnitCount`是 C0～C2曾实现的过渡 cadence authority。breaking cutover现已按
[Derived Recap History Load](derived-recap-history-load-target-design.md)完成：

- cadence改用抽象 `HistoryLoadUnit`；
- V1 estimator固定为
  `atelia.history-load.o200k-base.history-unit-v1`；
- `o200k_base`只是内部稳定分段尺度，不表示推理模型/provider token；
- Planner/Host拥有 estimator，raw core与Store不理解 HistoryLoad；
- `IHistoryUnitLoadEstimator`按单个 HistoryUnit测量，Planner projector独立完成
  baseline-relative累加与boundary mapping；absorbed与recent load仍分别验证；
- Resume/Restore不读取 active estimator或重算 frozen admission；
- 不升级 raw event schema；H2真实 profiling决定首版不增加 bounded或persistent cache。

本文其余章节保留为 historical HistoryUnit baseline；当前运行时、配置与验收语义以新目标设计为准。

## 9. Non-goals

- 推理模型/provider exact token estimator；
- 按 user/agent turn计数；
- exact R suffix保证；
- mid-tool-loop admission；
- 每遗漏一个 interval补发一个 Published set；
- 将 cadence写入 raw `RuntimeConfigSetup`；
- Store schema或 Building/Published wire变更；
- 后台 scheduler或 persisted retry trigger。

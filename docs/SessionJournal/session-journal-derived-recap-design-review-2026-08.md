# SessionJournal + DerivedRecap 设计审阅报告（2026-08）

> **状态**：审阅完成，且已由实施方逐条复核并裁决（2026-08-04）。
> 裁决结果见 [§4 裁决台账](#4-裁决台账2026-08-04)。
> **结论摘要**：15 条 finding，其中 P1 4 条、P2 8 条、P3 3 条；**全部建议均不触碰 durable wire**。
> 实际落地 3 条，明确否决/保留 4 条，延期 8 条；另有 **2 条被证伪的事实错误**已就地标注
> （R-STORE-03 的方法行数、R-PLAN-01 的事件算术）。
> **范围**：`prototypes/SessionJournal`、`prototypes/SessionJournal.Offline`、
> `prototypes/SessionJournal.DerivedRecap.Store`、`prototypes/SessionJournal.DerivedRecap.Planner`、
> `prototypes/SessionJournal.DerivedRecap.Maintainers`，以及作为 composition root 的
> `prototypes/SessionJournal.Cli`。
> **目标**：寻找**设计层面**需要修正的问题与化简机会，而不是逐行 code review。
> **与既有审阅的关系**：
> - [contract normalization 审阅报告](session-journal-semantic-preserving-contract-normalization-review-report.md)
>   已关闭 public surface 收口类问题（N-A..N-E）；
> - [V4 化简候选](event-addressed-derived-recap-v4-simplification-candidate.md)
>   已关闭 durable model 层的化简；
> - 本轮聚焦**尚未被上述两轮覆盖的维度**：代码体量/职责分配、同一语义的多份实现、
>   概念数量、跨层重复的 proof、以及可以在不动 durable wire 的前提下删除的结构。

---

## 0. 阅读顺序与本文约定

- 每个 finding 用 `R-<区域>-<序号>` 标识，区域取值：
  `CORE`（raw SessionJournal）、`STORE`、`PLAN`（Planner）、`MNT`（Maintainers）、
  `HOST`（CLI/Galatea 组合层）、`XCUT`（跨层）。
- 每个 finding 标注：**证据**（文件/行）、**问题**、**建议**、**风险/代价**、**是否触碰 durable wire**。
- 严重度：`P1`（设计缺陷，建议修正）、`P2`（明确的化简机会）、`P3`（观察/待验证）。
- 本文不修改代码，也不声称已实施；实施应另开 plan 文档。

---

## 1. 规模基线（2026-08-04 实测）

| 项目 | 文件数 | 行数 | 最大单文件 |
|---|---:|---:|---|
| `SessionJournal` | 35 | 13,689 | `SessionJournalEngine.cs` 4,821 |
| `SessionJournal.Offline` | 3 | 1,018 | `SessionJournalOfflineForwardFold.cs` 833 |
| `DerivedRecap.Store` | 11 | 10,997 | `DerivedRecapStore.cs` 6,575 |
| `DerivedRecap.Planner` | 25 | 12,986 | `DerivedRecapPlannerExecutor.cs` 2,487 |
| `DerivedRecap.Maintainers` | 7 | 841 | `RecapMaintainerProfileCatalog.cs` 365 |
| `SessionJournal.Cli` | 17 | 6,583 | `SessionJournalLegacyImporter.cs` 1,201 |
| **合计** | 98 | **46,114** | — |

文档侧 `docs/SessionJournal/*.md` 另有 11,454 行（不含 `done/`、`superseded/`）。

> 观察：为「把旧 history 截断成常驻 Recap，并继续拼接 exact raw suffix」这一件事，
> 当前投入约 46k 行实现 + 11k 行规范。这个比例本身是本轮审阅的首要怀疑对象。

---

## 2. Findings

### R-PLAN-01 `P1` cadence 用 token 计量、raw safety 用 event 计数，两者可互锁成不可恢复的永久 backpressure

> **裁决（2026-08-04，实施方复核）**：liveness 风险**成立**；但下文的事件算术与
> 生产路径描述**不准确**，已按复核结果修正（见「问题」小节的修正说明）。
> 修复不采用本文的「最小修正」，而是单独设计 pressure trigger / proof cap /
> tool-call bound / emergency scheduling。**本条仍未修复。**

**证据**

- cadence 取值（`prototypes/SessionJournal.DerivedRecap.Planner/README.md` L131-L149 的示例配置，
  与 Galatea 实际部署的 `recap-planner-config.json` 一致；**注意这不是库内置默认值**，
  库本身不提供 cadence 默认）：
  `minimumRecentHistoryLoad = 18000`、`recapBuildIntervalHistoryLoad = 21000`，
  触发条件是 `G >= R + B`，即**自 cadence baseline 起累计 ≥ 39,000 HistoryLoad token**。
- raw safety 门（[RecapPlanEvaluator.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlanEvaluator.cs#L66-L80)）：
  `rawGrowthEventCount` = cadence baseline 在 lineage 中的**下标**（raw event 个数），
  `> limits.MaxRawGrowthEventCount` 即返回 `Unavailable(MaxRawGrowthEventCountExceeded)`。
- 硬上限 `RecapProtocolHardCaps.V4.MaxRawGrowthEventCount = 512`
  （[RecapPlannerContracts.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerContracts.cs#L231-L243)），
  且 `ValidatePlanningLimits` 用 `RequireAtMost` 只允许 config **收紧**
  （[RecapPlannerContracts.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerContracts.cs#L292-L318)）。
- 该 defect 在 online 路径被映射为 `Backpressure`
  （[DerivedRecapOnlineLifecycleCoordinator.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapOnlineLifecycleCoordinator.cs#L680-L688)），
  即**拒绝本次 completion**。
- raw safety 在 cadence/estimator **之前**执行，因此一旦越界，无论 cadence 配置如何都不再进入 build。

**问题**

两个门用不同单位度量同一段 raw 区间，且没有任何相容性校验：

```text
必须在 <= 512 个 raw event 之内累计 >= 39,000 token 的 HistoryLoad
=> 要求平均每个 raw event >= 76 token
```

一个无 tool 的普通 turn 至少产生 4 个 raw event（`ObservationAccepted` +
`CompletionRequestPrepared` + `CompletionAttemptStarted` + `AgentActionProduced`）；
每个 tool call 追加 `ToolExecutionStarted` + `ToolResultObserved`，
每轮 tool 续跑再追加一组 `Prepared` + `Started` + `Action`。于是：

| turn 形态 | raw events/turn | 512 事件可容纳 turn 数 | 触发首次 build 所需的平均 turn 长度 |
|---|---:|---:|---:|
| 无 tool | 4 | 128 | ≥ 305 token |
| 1 次 tool call（单轮） | 9 | 56 | ≥ 697 token |
| 2 次 tool call（同一 action 内并行） | 11 | 46 | ≥ 848 token |
| 2 轮串行 tool | 14 | 36 | ≥ 1,084 token |

> **修正说明**：本表的初版把 1 次/2 次 tool call 分别记为 10/16 事件，未计入
> 「同一 action 内的并行 tool call 只产生一组续跑事件」，高估了事件密度。
> 上表为重新推导的结果，但**仍是解析估算，未经真实会话 trace 验证**；
> 实施方复核认为生产路径与此不完全一致。**结论的方向成立，具体数字不应直接用于定阈值。**

也就是说：**短 turn 的会话（闲聊、确认、小步 tool 循环）会先撞到 512 事件上限，
而 HistoryLoad 还远未达到 39,000**。此时：

1. raw safety 拒绝建 Recap；
2. online lifecycle 把它翻译成 Backpressure，拒绝新 completion；
3. 512 是 code-owned 硬顶，改 config 无法放宽；
4. 调小 cadence 也无用 —— raw safety 在 cadence 之前判定，已越界就直接 `Unavailable`；
5. `recap reset` 后 baseline 退回 `SessionCreated`，距离只会**更大**。

结论：**这是一个可达且不可通过配置恢复的死锁**，唯一出路是改代码、rewind 掉真实历史、
或换 branch/重新导入。对一个以「长期自主运行」为目标的系统，这是首要设计缺陷。

**建议（按侵入性递增）**

1. **最小修正**：把 raw growth 从「拒绝」改为「强制触发」。语义应是
   *"raw 事件数超过 N 时，即使 HistoryLoad 未达阈值也必须建 Recap"*，
   而不是 *"超过 N 就不许建"*。真正需要 fail-closed 的是
   `maxRawEventsPerBuild` / `maxRawEventsPerStep`（单次 build 的工作量上限），
   而 `maxRawGrowthEventCount` 语义上是 **cadence 的第二触发条件**，不是安全阀。
2. **配置相容性校验**：在 `RecapPlannerConfigResolver` 中加入静态可达性检查，
   拒绝 `minimumRecentHistoryLoad + recapBuildIntervalHistoryLoad` 在
   `maxRawGrowthEventCount` 个事件内不可能达成的配置组合（需要一个保守的
   per-event token 下界假设，或直接要求两者之一为 0/无穷）。
3. **统一计量单位**：cadence 与 safety 都以 `SessionHistoryPlanningUnit`（HistoryUnit）
   计数，而不是一个用 token、一个用 raw event。当前 README 已明确
   "Planner不得用 raw event distance冒充 context/history长度"，但 raw safety 门恰恰
   在用 raw event distance 做 gating。

**是否触碰 durable wire**：否。仅改 Planner 判定逻辑与 config 校验。

---

### R-XCUT-01 `P1` bounded prefix 上限 513 有三处彼此独立的来源，跨程序集无任何链接

**证据**

| 位置 | 表达式 | 所属程序集 |
|---|---|---|
| [DerivedRecapLineageView.cs](../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapLineageView.cs#L12) | `internal const int MaxHeaderCount = 513;` | Store |
| [RecapFrozenPlanBarrier.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/RecapFrozenPlanBarrier.cs#L41) | `internal const int MaxHeaderCount = 513;` | Planner |
| [DerivedRecapOnlineLifecycleCoordinator.cs](../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapOnlineLifecycleCoordinator.cs#L302) | `RecapProtocolHardCaps.V4.MaxRawGrowthEventCount + 1` | Planner |

**问题**

真正的不变量是：

```text
bounded prefix header 上限 >= MaxRawGrowthEventCount + 1
```

（+1 用于证明 exact `startExclusive`）。但：

- Store **不引用** Planner（依赖方向是 Planner → Store），因此 Store 里的 `513`
  在物理上无法从 `RecapProtocolHardCaps` 推导；
- 三处常量没有任何编译期或运行期一致性检查；
- 若将来把 `MaxRawGrowthEventCount` 调到 1024，Store 的 lineage view 仍只取 513 个 header，
  结果是**在正常运行区间内稳定返回 `BeyondPrefix`**——即 R-PLAN-01 的死锁提前发生，
  且症状表现为「bounded 证据不足」而不是「超限」，极难定位。

**建议**

把该上限提升为 Store 拥有的 public 协议常量（例如
`DerivedRecapProtocol.MaxLineagePrefixHeaderCount`），Planner 的 `RecapProtocolHardCaps.V4`
在静态构造时断言 `MaxRawGrowthEventCount + 1 <= DerivedRecapProtocol.MaxLineagePrefixHeaderCount`。
`RecapFrozenPlanBarrier` 直接引用同一常量，删除第二份字面量。

**是否触碰 durable wire**：否。

---

### R-XCUT-02 `P2` 同一个「bounded 证据不足」概念被声明成 17 个互不相通的类型

**证据**

`BeyondPrefix` 变体声明（`grep` 实测）：

| 程序集 | 数量 | 代表 |
|---|---:|---|
| `SessionJournal` | 5 | `SessionCurrentLineageAnchorLookup.BeyondPrefix`、`SessionHistoryPlanningWindowReadResult.BeyondPrefix`、`SessionCreatedPlanningSeedReadResult.BeyondPrefix`、`SessionHistoryPlanningWindowProofResult.BeyondPrefix`、`SessionGoverningSetupProofResult.BeyondPrefix` |
| `DerivedRecap.Store` | 8 | `DerivedRecapContracts.cs` 中 6 个 + Store/LineageView 内部 2 个 |
| `DerivedRecap.Planner` | 4 | execution / preparer(×2) / restore |

另有 `DerivedRecapBeyondPrefixStage`、`BeyondPrefixStageToken`、
`DerivedRecapBeyondPrefixException`、`RecapExecutionBeyondPrefixReport`、
`FindBeyondPrefix`/`FindRestoreBeyondPrefix`/`FormatBeyondPrefix` 等配套设施。

**问题**

所有变体承载的**载荷完全相同**：`RequiredAnchor?`、`CapturedHead`、`HeaderCount`、
`NextAddress`（即 `SessionCurrentLineageBeyondPrefix`）。差别只在于「它出现在哪一步」，
而这个信息已经由 `DerivedRecapBeyondPrefixStage` 单独表达。当前设计把
「证据」×「阶段」做成了类型笛卡尔积，代价是：

- 每新增一个 bounded 读入口就要新增一个 result union；
- 每一层都要写一遍 `switch` 把内层 evidence 搬到外层类型；
- Host 侧必须对每个 union 单独 exhaustive match（Galatea 已因此专门加了
  typed `recap-beyond-prefix` 分支）。

**建议**

保留 `SessionCurrentLineageBeyondPrefix` 作为**唯一** evidence 类型（已存在），
各层结果统一为 `Result<T> = Available(T) | BeyondPrefix(evidence, stage)`，
其中 `stage` 是既有的 `DerivedRecapBeyondPrefixStage`。若不想引入泛型 union，
至少让 Store/Planner 的 6+4 个变体共享同一个 `record RecapBeyondPrefix(evidence, stage)`
载荷类型，删掉逐层重新声明的字段。

注：若 R-PLAN-01 按建议 1 修正、且 R-XCUT-01 把上限提升为「充裕的协议常量」，
则 `BeyondPrefix` 会从**常规控制流**退化为**运维异常**，本项的收益会进一步放大
（多数 union 可直接坍缩成异常或单一 not-ready 状态）。

**是否触碰 durable wire**：否。纯 in-memory result shape。

---

### R-XCUT-03 `P2` 概念预算：458 个 public 类型 / 46k 行实现，用于「截断旧历史 + 拼接 exact raw 后缀」

**证据（实测）**

| 程序集 | public 类型数 | 行数 |
|---|---:|---:|
| `SessionJournal` | 123 | 13,689 |
| `SessionJournal.Offline` | 3 | 1,018 |
| `DerivedRecap.Store` | 175 | 10,997 |
| `DerivedRecap.Planner` | 147 | 12,986 |
| `DerivedRecap.Maintainers` | 10 | 841 |
| **合计** | **458** | **39,531**（+ CLI 6,583） |

**问题**

`DerivedRecap.Store` 的 public 类型数（175）超过 raw core（123）。Store 的职责按
[V4 化简候选 §7](event-addressed-derived-recap-v4-simplification-candidate.md) 只有
「point read/write + shape/size/checksum/commitment 校验 + atomic replace/publication +
返回 structural defects」，却产出了比整个 event-sourced 核心还多的对外概念。
这个比例本身提示 Store 的 result/authority 类型存在**同构复制**（与 R-XCUT-02 同源）。

**建议**：不作为独立行动项，而是把它当作 R-XCUT-02、R-STORE-* 修正后的**验收指标**：
若一轮化简后 Store public 类型数没有明显下降，说明化简停留在实现层而未触及概念层。

---

### R-CORE-01 `P3` Offline forward fold 是 tail 状态机的第二份独立实现，靠运行期比对互证

**证据**

- [SessionJournalOfflineValidator.cs](../../prototypes/SessionJournal.Offline/SessionJournalOfflineValidator.cs#L44-L80)：
  用 `SessionJournalOfflineForwardFold`（833 行，前向）折叠一遍，再与
  `scan.ExecutionStateAtCapturedHead`（由 `SessionExecutionTailResolver`，916 行，后向解析）
  比较，不一致即 `throw new InvalidDataException("... disagree at captured head ...")`；
- governing setup 同样做 fold vs `SessionAuthoritativeGoverningSetupResolver` 的双实现比对。

**评价**

这是刻意的 N-version programming：两份独立实现互为 oracle。它确实能捕获单侧实现 bug，
但代价是**任何 tail 语义变更都必须同时改两处**，且两处的失配只在 offline validate 时暴露。

**建议**：保留，但明确记录为「刻意冗余」并加一条约束：
tail 语义的规范变更必须先改
[tail-execution-recovery-design.md](tail-execution-recovery-design.md)，
再同时改两份实现，且必须有一个 test 证明**故意引入的单侧偏差会被 validator 捕获**
（当前是否有这样的 mutation test 待确认，见台账 B1）。

---

### R-STORE-01 `P1` Building 与 Published 被实现成两套平行世界，V4 设计承诺的共用 primitive 未落地

**证据 —— Store 层的平行 API 对**

| 语义 | Building 版 | Published 版 |
|---|---|---|
| 检查单个 block 并签发写权限 | `InspectBuildingBlockAsync` | `InspectPublishedForRestoreAsync`（批量） |
| 推进 rolling checkpoint | `AdvanceRollingCheckpointAsync` | `AdvancePublishedCheckpointAsync` |
| 安装 final block | `EnsureFinalBlockAsync` | `InstallPublishedReplacementAsync` |
| 写权限 token | `BuildingBlockWriteAuthority` | `PublishedBlockWriteAuthority` |
| plan handle | `BuildingPlanHandle` | `PublishedRestoreHandle` / `PublishedRestorePlanAuthority` |
| checkpoint 写结果 union | `CheckpointWriteResult`（4 变体） | `PublishedCheckpointWriteResult`（4 变体） |
| final 写结果 union | `FinalBlockWriteResult`（6 变体） | `PublishedFinalWriteResult`（7 变体） |

**证据 —— Planner 层因此也分叉**

- Building Resume：`DerivedRecapPlannerExecutor.cs` 的 `DerivedRecapBuildingExecutor.EnsureFinalAsync`
  （约 L2301-L2353）→ `_store.EnsureFinalBlockAsync`；
- Published Restore：`DerivedRecapRestoreExecutor.cs` 的 `InstallFinalAsync`
  （约 L927-L970）→ `_store.InstallPublishedReplacementAsync`。

**问题**

[V4 化简候选 §7](event-addressed-derived-recap-v4-simplification-candidate.md) 明确承诺：

> runner 复用一个内部 primitive：`EnsureFinalRecapBlock(plan, frozen input, rolling checkpoint?)`
> —— Building Resume 与 Published Restore 共用该 primitive；两者的外层 authority 不合并。

实际落地的是：**外层 authority 没合并，内层 primitive 也没合并**。两条路径从
Store API、write authority、result union 到 Planner 的 executor 全线各写一份。

这在概念上是不必要的：Building 与 Published 目录**结构完全相同**
（`manifest.json` + `inputs/` + `blocks/` + `work/`），需要做的事也相同
（按 frozen plan 让每个 block 走完 route 并落 final）。真正的差异只有两处：

1. Building 以 **directory rename** 结束并取得 membership；
   Published 已有 membership，以 **envelope commit** 结束；
2. Published 的 frozen plan authority 来自 `publication.FrozenPlanSnapshot`，
   Building 来自 `manifest.json`——但这一点在设计上已经由「runner 不自行选择 authority winner，
   由外层传入」解决了。

**建议**

引入一个 Store 内部概念 `RecapSetContainer { Phase(Building|Published), Root, FrozenPlan }`，
让 block 层的 inspect / checkpoint / final 三组操作只有**一套**实现与**一套** result union，
phase 只影响：

- 终态提交方式（rename vs envelope commit）；
- 是否允许 `HealthyConflict`（Published 多出的那个变体）。

预期收益：Store 删掉 3 个 result union（约 15 个变体）、1 个 write authority 类型、
3 个 `*Published*` 方法族；Planner 的 `EnsureFinalAsync` / `InstallFinalAsync` 合并为一处。
这也是 R-XCUT-03 概念预算下降的主要来源。

**是否触碰 durable wire**：否。目录布局与文件 schema 完全不变，只改 in-memory API 形状。

---

### R-STORE-02 `P2` 20+ 个 result union 的变体集合高度同构，其中 3 个 health union 完全同构

**证据**（`DerivedRecapContracts.cs` 1,134 行，83+ public 类型，20+ result union）

三个 health union 变体集合完全一致：

| Union | 变体 |
|---|---|
| `FinalRecapBlockHealth` | Missing / Healthy / Damaged / Unavailable（+ abstract `StateToken`） |
| `RollingRecapCheckpointHealth` | Missing / Healthy / Unusable / Unavailable（+ abstract `StateToken`） |
| `FrozenRecapInputHealth` | NotRequired / Missing / Healthy / Damaged / Unavailable（+ abstract `StateToken`） |

read/selection 族的公共骨架是 `Available / Missing / Invalid / Changed / Unavailable`，
write 族的公共骨架是 `Updated|Installed / AlreadyCurrent|AlreadyHealthy / Stale / Unavailable`，
再叠加 R-XCUT-02 的 `BeyondPrefix` 与 `StoreUnavailable`。

**问题**

变体名相同、载荷相同、消费方式相同，只因为「作用于不同 component」就复制一遍类型。
这类同构复制的代价不在行数，而在**每个消费点都要写一遍 exhaustive switch**，
且新增一个 component 就要再复制一整套。

**建议**

1. 三个 health union 合并为 `RecapComponentHealth`，用一个 `RecapComponentKind`
   区分 Final / Checkpoint / FrozenInput；`NotRequired` 与 `Unusable` 作为该 union 的
   合法变体（对不适用的 kind 由 Store 保证不产生）。
2. 与 R-STORE-01 合并后，write 结果族从 4 个降到 2 个。
3. 保留 `RecapStructuralDefect` 作为唯一的 defect 载荷（已存在），
   不再在每个 union 里重复声明 defect 列表字段。

**是否触碰 durable wire**：否。

---

### R-STORE-03 `P2` ~~`DerivedRecapStore.cs` 单文件 6,575 行，含约 900 行的单个方法~~（已部分证伪）

> **裁决（2026-08-04，实施方复核）：本条的核心数字是错的，不予实施。**
> 「`InspectPublishedForRestoreCoreAsync` 约 900 行」系测量失误
> （用正则匹配方法声明起止，漏掉了跨行的返回类型声明）。
> 大括号配对实测：`InspectPublishedForRestoreCoreAsync` = **206 行**；
> 全文件最长方法是 `CreateBuildingCoreAsync` = **315 行**。
> 6,575 行的文件体量属实，但既然没有失控的巨型方法，
> 纯粹的文件搬移无收益，应只作为将来真实重构的伴随步骤。
> 下文保留原始记录以备追溯。

**证据**

| 方法 | 约行数 | 职责数 |
|---|---:|---:|
| `InspectPublishedForRestoreCoreAsync` | ~900 | 5（admission 解析 / roster 构建 / health 检查 / capability 分类 / 结果打包） |
| `CreateBuildingCoreAsync` | 311 | 4（source 获取 / staging / frozen input+manifest 安装 / lineage 校验） |
| `CommitPublishedEnvelopeTrustedAsync` | ~250 | 3 |
| `InstallPublishedReplacementCoreAsync` | ~200 | 3 |
| `PublishTrustedAsync` | ~190 | 3 |

同一文件内混合：路径/目录管理、读写锁、root 生命周期、Building CRUD、
publication 密封与提升、Published restore 检查、materialize、strict ordinal selection、
lineage 证明、state token 管理。而项目内已经存在
`DerivedRecapCodec.cs`、`RecapDurableFileSystem.cs`、`DerivedRecapLineageView.cs`、
`DerivedRecapPublisher.cs` 四个本应承载其中一部分的文件。

**建议**（按收益排序）

1. `SelectNthPreviousAsync` + `InventoryCurrentLineageBuildings` → `DerivedRecapLineageView.cs`
   （它们本质是 lineage 查询，且已持有 bounded prefix）；
2. `ValidatePlanLineage` / `ValidateInputsAndBlocks` / `ValidateBlockAgainstPlan`
   / `ValidateExistingMaintainRoute` / `ValidateFrozenSourceCursor` → 独立
   `DerivedRecapStructuralValidation.cs`（纯静态、无 IO，易单测）；
3. `InspectPublishedForRestoreCoreAsync` 按 5 个职责拆分为 5 个方法；
   若先做 R-STORE-01，其中的 health 检查与 capability 分类将与 Building 侧合并。

**注意（反向建议 / 不要做）**：`PublishTrustedAsync` 中三次
`CanPublishCoreAsync`（preflight / 取锁后 initial / rename 前 final）**不是冗余**，
而是 [V4 化简候选 §2.2](event-addressed-derived-recap-v4-simplification-candidate.md)
明确要求的 crash-safety fence。任何「缓存诊断快照 + quick-check」的优化都会破坏
「rename 前必须完整重验 + latest-anchor revalidation」这条不变量，**不应采纳**。

**是否触碰 durable wire**：否。

---

### R-PLAN-02 `P2` `DerivedRecapPlannerExecutor.cs` 中并置两个 Executor，`BuildingExecutor` 混合 evaluation/execution/publication

**证据**

| 类型 | 行范围 | 承担职责 |
|---|---|---|
| `DerivedRecapPlannerExecutor` | ~L15-L1283 | planning 意图捕获、diagnostics、BuildingExecutor 生命周期、baseline/config 校验 |
| `DerivedRecapBuildingExecutor` | ~L1296-L2453 | Resume 执行、lineage 证明、publication 准备、block 执行、final 安装、publish |

后者同时是 evaluator（`PrepareBuildingAsync`）、executor（`EnsureBlockAsync`）
与 publisher 调用方（`ExecuteAndPublishAsync`）。

**建议**

拆为两个文件；`DerivedRecapBuildingExecutor` 内部再分离
「准备（纯判定，返回 plan-of-actions）」与「执行（消费 plan-of-actions）」两阶段——
这正好与 `DerivedRecapRestoreExecutor` 已有的 `Prepare()`（非 async，L520）+
`ExecuteBlockAsync()` 两段式结构对齐，做完后两条路径的形状会自然收敛，
为 R-STORE-01 的 primitive 合并铺路。

**是否触碰 durable wire**：否。

---

### R-PLAN-03 `P3` defect-kind → code 映射与 BeyondPrefix 传播在多处逐字重写

**证据**

| 位置 | 内容 |
|---|---|
| `DerivedRecapPlannerExecutor.cs` ~L1463-L1480 | `defect.Kind switch { ExecutionLimit→…, StoreUnavailable→…, _→BuildingInvalid }` |
| `DerivedRecapRestoreExecutor.cs` ~L170-L185 | 同一 switch，仅 default 分支为 `FrozenPlanInvalid` |
| `DerivedRecapPlannerExecutor.cs` ~L1455-L1459 | `if (barrier.BeyondPrefix is { } b) return new …BeyondPrefix(stage, b);` |
| `DerivedRecapRestoreExecutor.cs` ~L155-L160、~L240-L246 | 同一模式，共 2 处 |

**建议**：属于 R-XCUT-02 的下游症状。若采纳 R-XCUT-02 的统一 evidence + stage 方案，
这些重写点会自然消失；单独修补收益有限，不建议作为独立行动项。

---

### R-STORE-04 `P3` 7 个 opaque authority token 中至少两对可合并

**证据**

| Token | 绑定内容 |
|---|---|
| `BuildingPlanHandle` | `ownerPath` + `BuildingDescriptor` |
| `BuildingBlockWriteAuthority` | `ownerPath` + `BuildingDescriptor` + `blockId` + 两个 state token |
| `PublishedRestoreHandle` | `RefId` + anchor + authorityKind + authorityStateToken + manifestSha + blockRoster |
| `PublishedRestorePlanAuthority` | `ownerPath` + `RefId` + anchor + authorityKind + manifest/block roster |
| `PublishedBlockWriteAuthority` | `ownerPath` + handle + blockId + state tokens |
| `PublishedEnvelopeCommitAuthority` | `ownerPath` + handle + `Dict<blockId, finalStateToken>` |
| `PreparedRecapPublication` | publisher ref + handle + lineage + expectedRawHead |

`BuildingPlanHandle` 与 `BuildingBlockWriteAuthority` 的差异只是「多了 blockId + state token」；
`PublishedRestoreHandle` 与 `PublishedRestorePlanAuthority` 承载几乎相同的字段集合。

**建议**：低优先级。真正的收敛应在 R-STORE-01 之后进行——届时
Building/Published 的 handle 与 block write authority 各自只剩一个，
再评估是否需要 handle 与 block-authority 分离（当前分离是有价值的：
handle 证明「读到了 plan」，block authority 证明「读到了该 block 的当前状态」）。

**是否触碰 durable wire**：否。

---

### R-CORE-02 `P2` 同一套「事件 kind → 执行状态」规则有三份独立实现，其中两份是同向的前向折叠

**证据**

| 实现 | 位置 | 方向 | 输入 | 产出 |
|---|---|---|---|---|
| `SessionExecutionTailResolver` | 核心，916 行 | **后向**（head 沿 Parent 上溯） | header + 必要 payload | `SessionExecutionRecovery` / `SessionExecutionState` |
| `SessionTailContextProjection.FoldSuffix` | 核心，512 行 | **前向** | `DecodedSessionEvent[]` | phase + context messages + planning units |
| `SessionJournalOfflineForwardFold` | Offline，833 行 | **前向** | `SessionJournalAuditEvent` | `SessionExecutionState` + 统计 |

后两者的 `switch (kind)` 覆盖**完全相同的 11 个 case**：

```text
RuntimeConfigSetup / SystemPromptSetup / SessionCreated / ObservationAccepted
CompletionRequestPrepared / CompletionAttemptStarted / CompletionAttemptFailed
AgentActionProduced / ImportedAgentAction
ToolExecutionStarted / ToolResultObserved
```

（`SessionTailContextProjection.cs` L78-L352 vs
`SessionJournalOfflineForwardFold.cs` L50-L105。）

**问题**

后向 resolver 与前向 fold 互为 oracle，是**有价值的刻意冗余**（且已有 mutation test：
`tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs` 的
`DurableHeadMatrix_MatchesTailResolverAndFoldContracts` 与 8 个 `[InlineData]` mutation
场景 `wrong-parent` / `started-without-prepared` / `wrong-correlation` /
`missing-runtime` / `extra-runtime` 等）。

但**两份前向 fold**不是冗余，是**重复**：它们方向相同、规则相同、产出的
`SessionExecutionState` 相同，唯一差别是输入类型
（`DecodedSessionEvent` vs `SessionJournalAuditEvent`）。这个差别是偶然的，
不是本质的——两者都是「已解码的事件 + 地址」。

**建议**

1. 把前向折叠的**状态机部分**提取为核心内的单一实现，输入抽象为一个窄接口
   （地址 + kind + 已解码 body 的最小视图）；
2. `SessionJournalOfflineForwardFold` 保留其**统计与 semantic commitment** 职责，
   状态机部分改为调用共享实现；
3. 保留后向 `SessionExecutionTailResolver` 作为独立 oracle，并在
   [tail-execution-recovery-design.md](tail-execution-recovery-design.md) 中
   显式记录「前向 1 份 + 后向 1 份 = 刻意的 2-version 冗余」，
   避免将来又长出第三、第四份。

**风险提示**：若合并后 Offline validator 的 `folded.ExecutionState != scan.ExecutionStateAtCapturedHead`
比对变成自我比对，该断言就失去价值——合并时必须确认比对的另一侧仍是**后向** resolver
（当前 `scan.ExecutionStateAtCapturedHead` 正是来自 `SessionExecutionTailResolver`，
所以合并前向两份后互证仍然成立）。

**是否触碰 durable wire**：否。

---

### R-CORE-03 `P2` `SessionJournalEngine.cs` 4,821 行中约 25% 是散落的静态 `Validate*`

**证据**（行号区段为估算）

| 职责 | 约行数 | 占比 |
|---|---:|---:|
| Validation & utility（散落的静态 `Validate*`、私有 record） | ~1,230 | 25.5% |
| Completion dispatch & provider loop | ~570 | 11.9% |
| Governing setup 解析与校验 | ~590 | 12.2% |
| History planning window 物化 | ~460 | 9.6% |
| Lineage 读取/遍历 | ~310 | 6.5% |
| Manifest & payload 读取 | ~310 | 6.5% |
| Context candidate 校验与物化 | ~310 | 6.4% |
| Request canonicalization & context 选择 | ~250 | 5.2% |
| Send/Resume 驱动 | ~290 | 6.0% |
| 其余（open/create、append、tool、CAS、cursor） | ~450 | 9.3% |

最长方法：`CompleteArtifactTailAsync`（~245 行，串联 context lifecycle → candidate 选择
→ manifest 构建 → dispatch）。

**建议**

已有的 3 个 partial（`.CompletedTurns` / `.DesiredSetup` / `.RuntimeRecovery`）证明
partial 拆分是可行且已被接受的做法。建议继续按同一模式拆出：

- `SessionJournalEngine.Validation.cs`（纯静态，无状态）；
- `SessionJournalEngine.Setup.cs`（governing setup 解析/校验/cursor）；
- `SessionJournalEngine.Planning.cs`（lineage 读取 + planning window 物化 + seed）；
- `SessionJournalEngine.Completion.cs`（dispatch / tool loop / canonicalization）。

这是纯机械拆分，零语义风险，但会显著降低后续所有审阅的成本。

**是否触碰 durable wire**：否。

---

### R-HOST-01 `P1` 关键并发不变量由文档要求、由 Host 实现，但库既不提供也不校验

**证据**

- `prototypes/SessionJournal/README.md` L258：
  > 产品Host应让send/resume/abandon/rewind共享同一个per-session writer lock。
- `prototypes/SessionJournal.DerivedRecap.Store/README.md`：
  > Neither lock serializes two callers sharing the same engine instance:
  > such callers must serialize raw mutation against Building install, Publish, and Restore themselves.
- 实测：`SessionJournalEngine.cs` 中**没有任何** `SemaphoreSlim` / `lock (` /
  `Monitor.` / `Interlocked.`（grep 零命中）。

**问题**

整套 exact-head CAS + Recap publication fence 的正确性依赖一个**库不提供、
不校验、也无法观测**的 Host 级锁。违反它的后果不是抛异常，而是
「两个 caller 交错执行 inspect → compose → mutate」，最终由更下游的 CAS 拒绝
——但此时 Observation 可能已经 append、provider 可能已经被调用。

同类的「文档要求但库不强制」的不变量还有（来自 CLI/Galatea 装配对比）：

| 不变量 | 强制方 |
|---|---|
| per-session writer lock | ✗ 仅文档 |
| phase-first 检查顺序（先 inspect 再决定是否开 Store） | ✗ Host 自行实现 |
| Store 必须先显式 `CreateAsync` | ✓ 库拒绝 auto-create，但 Host 必须自己调用 |
| setup reconcile 只在 `Idle` 且在 Recap preparation 之前 | ✗ Host 自行安排顺序 |
| `expectedHead` 必须来自本轮 inspection | ~ 库能检测不匹配，但无法阻止 Host 传入陈旧值 |

**建议**

1. **最小且高收益**：在 `SessionJournalEngine` 内加一个**fail-closed 重入守卫**
   （`Interlocked.CompareExchange` 标志位），任何 mutation 入口
   （`SendAsync`/`ResumeAsync`/`AbandonFailedTurn`/`RewindLatestCompletedTurn`/setup reconcile）
   在守卫已被占用时立刻抛 typed 异常。它不能替代 Host 的「inspect→compose→mutate」
   跨调用锁，但能把「同一 engine 实例上的并发 mutation」从**静默交错**变成**确定性失败**。
2. 把 phase-first 顺序固化为一个库提供的 façade（见 R-HOST-02），
   而不是让每个 Host 用注释和文档去复述顺序。

**是否触碰 durable wire**：否。

---

### R-HOST-02 `P2` 跑通一次 online turn 需要 Host exhaustive match 13 个 closed union / ~62 个分支

**证据**（CLI `OnlineTurnCommand.cs` + `RecapPlannerComposition.cs` +
`RecapCliComposition.cs` + `RecapOperationReadiness.cs` 合计约 1,288 行装配代码）

| # | Union | 变体数 |
|---:|---|---:|
| 1 | `SessionRuntimeRecoveryRequirements` | 7 |
| 2 | `OnlineExecutionMode`（CLI 自定义中间层） | 4 |
| 3 | `SessionDesiredSetupReconciliationResult` | 3 |
| 4 | `CompletionDispatchBindingResult` | 2 |
| 5 | `RecapPlannerCompositionLoadResult`（CLI 自定义） | 4 |
| 6 | `RecapCliCompositionResolveResult`（CLI 自定义） | 3 |
| 7 | `RecapOperationReadinessResult`（CLI 自定义） | 2 |
| 8 | `DerivedRecapOperationPreparationResult` | 4 |
| 9 | `DerivedRecapSelection` | 6（online 路径调用两次） |
| 10 | `DerivedRecapExecutionResult` | 5 |
| 11 | `DerivedRecapRestoreResult` | 5 |
| 12 | `TurnResult` | 2 |
| 13 | `ResumeOutcome` | 3 |

注意其中 **4 个（#2/#5/#6/#7）是 CLI 自己为了驯服前面那些 union 而新造的中间 union**
——这是「库的 result shape 粒度过细，Host 必须先做一次归约」的直接症状。

CLI 与 Galatea 的装配还存在直接重复：

| 重复块 | CLI | Galatea | 重复度 |
|---|---|---|---|
| capability catalog → planning snapshot 投影 | `RecapPlannerComposition.cs` ~L158-L168 | `GalateaRecapComposition.cs` ~L151-L159 | 100% |
| completion target identity 构造 | `OnlineTurnCommand.cs` ~L237-L242 | `GalateaRecapComposition.cs` ~L127-L137 | 100% |
| maintainer registry composition | `RecapCliComposition.cs` ~L20-L50 | `GalateaRecapComposition.cs` ~L99-L123 | ~80% |
| readiness/preparation adapter | `RecapOperationReadiness.cs` ~L85-L118 | `GalateaRecapComposition.cs` ~L23-L80 | ~60% |

**建议**

1. `RecapMaintainerProfileCatalog.ToCapabilitySnapshot()`：把 100% 重复的投影收进库
   （当前两个 Host 各写一遍，且 descriptor shape 变化时必须同步改两处）。
2. 提供一个**库级 online-turn façade**，把
   「inspect phase → 决定是否需要 Recap → 打开 Store → preparer → 组 lifecycle →
   Send/Resume」这条固定顺序封装成一个入口，Host 只提供
   `(connections resolver, maintainer catalog, desired setup)` 三个策略回调。
   当前 CLI 的 `OnlineExecutionMode` / `RecapOperationReadinessResult` 等中间 union
   实际上就是这个 façade 的雏形，只是长在 CLI 里而不是库里。
3. 该 façade 同时是 R-HOST-01 中「phase-first 顺序」与「writer lock」的自然归属地。

**是否触碰 durable wire**：否。

---

## 3. 优先级与建议行动顺序

| 序 | Finding | 严重度 | 触碰 durable wire | 预估收益 |
|---:|---|---|---|---|
| 1 | **R-PLAN-01** cadence/raw-safety 单位互锁死锁 | P1 | 否 | 消除一个可达的不可恢复故障 |
| 2 | **R-XCUT-01** 513 三处独立常量 | P1 | 否 | 消除一类极难定位的配置陷阱 |
| 3 | **R-HOST-01** 并发不变量无强制 | P1 | 否 | 把静默交错变确定性失败 |
| 4 | **R-STORE-01** Building/Published 双世界 | P1 | 否 | Store/Planner 概念与代码双降 |
| 5 | **R-XCUT-02** 17 个 BeyondPrefix 类型 | P2 | 否 | 依赖 1、2 完成后收益最大 |
| 6 | **R-STORE-02** 同构 result union | P2 | 否 | 与 4 一并做 |
| 7 | **R-CORE-02** 两份同向前向折叠 | P2 | 否 | 删除约 400-600 行重复状态机 |
| 8 | **R-HOST-02** Host façade | P2 | 否 | 让第三个 Host 的接入成本可控 |
| 9 | **R-STORE-03 / R-PLAN-02 / R-CORE-03** 文件与方法切分 | P2 | 否 | 纯机械，零风险，降低后续审阅成本 |
| 10 | **R-PLAN-03 / R-STORE-04 / R-CORE-01** | P3 | 否 | 作为上述项的副产品处理 |

> **R-XCUT-03 不列为行动项**：458 个 public 类型是**验收指标**，不是待办。
> 建议在第二组完成后重新实测，若 public 类型数没有实质下降，说明合并只是搬家而非化简。

**建议的实施分组**

- **第一组（正确性）**：1 + 2 + 3。三项互不依赖，都不动 durable wire，
  都能独立验收。建议先做，因为 1 是当前唯一已识别的**可达故障**。
- **第二组（结构）**：4 + 6 + 5。顺序不可颠倒：先合并 Building/Published
  双世界（4），同构 union 才会自然坍缩（6），BeyondPrefix 的层数才会下降（5）。
- **第三组（去重）**：7 + 8 + 9。可与第二组并行。

**一个总体判断**

本子系统的 durable model（`event-addressed-derived-recap-v4-simplification-candidate.md`
定义的那 4 个文件 + 2 个 phase + 1 个 rolling checkpoint）本身是**克制且合理**的。
当前的复杂度不来自 durable model，而来自**in-memory contract 层的同构复制**：
同一份证据/健康状态/写结果，因为「作用于 Building 还是 Published」「出现在哪个 stage」
「由哪一层返回」被复制成了几十个类型。因此本轮所有建议都**不需要动 durable wire**，
这也是它们值得做的主要理由。

---

## 4. 裁决台账（2026-08-04）

实施方对 15 条 finding 逐条复核后的处置。**§3 的优先级排序是审阅方的原始建议，
本节是实际裁决；两者不一致处以本节为准。**

### 4.1 已实施（3 条，提交 `c76f0f45`..`c235d44d`）

| Finding | 实际做法 | 与本文建议的差异 |
|---|---|---|
| **R-HOST-01** | 在 `SessionJournalEngine` 上加 mutation gate（`Interlocked.CompareExchange` + 租约），覆盖 Send/Resume/appends/UseRuntime/reconcile/abandon/rewind/Dispose 共 12 个入口；Galatea 侧让 disposal 与 turn lock 串行 | 与本文「fail-closed 重入守卫」建议一致。实现见 [SessionJournalMutationContracts.cs](../../prototypes/SessionJournal/SessionJournalMutationContracts.cs) |
| **R-XCUT-01** | 集中 lineage prefix ceiling，并规避跨程序集 `const` 内联；**保留** Store ceiling / Frozen horizon / Online raw-growth horizon 三者的语义区分 | 比本文建议更精细：本文把三处当作同一常量的复制，实施方判定它们语义不同，只统一来源不统一含义 |
| **R-STORE-01** | **未**合并 Building/Published 两套 authority world；改为让 checkpoint 成功结果携带 Store 签发的 refreshed `BuildingBlockWriteAuthority`，删除 Planner happy path 的重复 inspection | 与本文建议方向不同。本文主张引入 `RecapSetContainer` 统一 phase；实施方选择了侵入性小得多的局部改进 |

### 4.2 明确否决 / 保留现状（4 条）

| Finding | 否决理由 |
|---|---|
| **R-CORE-01** | forward/backward 双 oracle 是有价值的 proof redundancy —— 与本文结论一致，本条本就是「建议保留」 |
| **R-CORE-02** | 否决「大一统状态机」：两个 fold 的输入、输出、**证明义务**并不相同。本文只比较了 `switch` 的 case 集合，未比较证明义务，论证不充分 |
| **R-STORE-02** | 否决合并 health/result union：各阶段的合法状态集合确实不同，同构只是表面 |
| **R-STORE-04** | 保留 authority token：它们证明的是不同阶段的事实，不是同一事实的重复包装 |

### 4.3 延期（8 条）

| Finding | 延期条件 |
|---|---|
| **R-PLAN-01** | liveness 风险确认存在，但需单独设计 pressure trigger / proof cap / tool-call bound / emergency scheduling。**风险仍然敞口** |
| **R-XCUT-02** | 17 个 `BeyondPrefix` 名称属实，但 payload 与阶段语义不同；仅考虑未来共享 evidence propagation |
| **R-XCUT-03** | 仅作监控指标，不以 public type / LOC 数量驱动重构 —— 与本文「作为验收指标而非行动项」一致 |
| **R-STORE-03** | 核心数字被证伪（见该条裁决说明），不做纯文件搬移 |
| **R-PLAN-02** | 等 action-plan boundary 明确后再拆 executor |
| **R-PLAN-03** | 现有 mapping 并非完全等价，等 evidence shape 收口后再处理 |
| **R-CORE-03** | 否决「机械拆文件、零风险」的判断；只能作为未来真实重构的伴随步骤 |
| **R-HOST-02** | 投影重复属实，但 Maintainers/Planner ownership 与 CLI/Galatea failure policy 不等价，暂不抽 façade |

### 4.4 审阅方法的教训

1. **方法体行数必须用大括号配对测量**，不能用正则匹配声明行起止 —— R-STORE-03 因此
   把 206 行误报为 900 行，是本轮唯一的严重事实错误。
2. **「switch case 集合相同」不等于「可以合并」**：R-CORE-02 / R-STORE-02 / R-STORE-04
   三条都栽在同一处——只比较了结构形状，没有比较**证明义务**与**合法状态集合**。
   同构的类型未必表达同构的语义。
3. **区分「库默认值」与「某个部署的配置」**：R-PLAN-01 把 Galatea 的
   `recap-planner-config.json` 当成了库的 canonical default。
4. 反过来，**基于「库是否提供某个机制」的判断是可靠的**：R-HOST-01 的
   「`SessionJournalEngine` 内零同步原语」是 grep 可证的事实，该条得以原样落地。

---

## 附录 A. 审阅进度台账

| 批次 | 范围 | 状态 |
|---|---|---|
| B0 | 规模基线、既有审阅盘点 | 完成 |
| B1 | raw core：Engine/TailResolver/OperationalSemantics/Offline 的状态机重复度 | 完成（R-CORE-01/02/03） |
| B2 | Store：durable model 与 6.5k 行实现的对应关系 | 完成（R-STORE-01..04） |
| B3 | Planner：Prepare/Execute/Resume/Restore 四条路径的重复度 | 完成（R-PLAN-02/03，主结论并入 R-STORE-01） |
| B4 | 跨层：BeyondPrefix / bounded prefix / authority token 的传播成本 | 完成（R-XCUT-01/02/03） |
| B5 | Host/CLI：composition 复杂度 | 完成（R-HOST-01/02） |
| B6 | 汇总与优先级建议 | 完成（§3） |

## 附录 B. 已关闭的线索

1. **[已确认]** `RecapPlanEvaluator.EvaluateRawSafety` 在 cadence 之前执行，越界即
   `Unavailable`，无强制建 Recap 分支 → R-PLAN-01。
2. **[已确认]** 存在有效的 mutation test：
   `tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs` 的
   `DurableHeadMatrix_MatchesTailResolverAndFoldContracts` + 8 个 mutation `[InlineData]`
   （`wrong-parent` / `wrong-attempt` / `started-without-prepared` / `wrong-correlation` /
   `wrong-checkpoint` / `setup-pending-prepared` / `missing-runtime` / `extra-runtime`），
   证明后向 resolver 与前向 fold 的互证是有效的 → R-CORE-01 保留结论「刻意冗余」。
3. **[已确认]** V4 §7 的 `EnsureFinalRecapBlock` 共用 primitive **未落地** → R-STORE-01。
4. **[已确认]** `DerivedRecapStore.cs` 仍在单文件内混合路径/锁/root/Building/publication/
   Published/selection/lineage 证明/state token → R-STORE-03。
5. **[已确认]** `SessionJournalEngine.cs` 职责清单与最长方法 → R-CORE-03。
6. **[已确认]** online `PrepareAsync` 中 latest / configured 两轮各含一次
   restore-then-reselect，共 4-5 次 `CaptureFreshLineage` → 归入 R-STORE-01 的下游症状，
   合并 Building/Published 后可一并评估是否能归约为单轮。

## 附录 C. 明确**不建议**采纳的候选

| 候选 | 理由 |
|---|---|
| 缓存 `PublishTrustedAsync` 的三次 `CanPublishCoreAsync` 诊断结果 | 三次分别是 preflight / 取锁后 initial / rename 前 final，是 V4 §2.2 要求的 crash-safety fence，缓存会破坏「rename 前必须完整重验 + latest-anchor revalidation」不变量 |
| 删除 Offline 与 core tail resolver 的互证比对 | 有 mutation test 支撑，是有效的 2-version 冗余；应删除的是**两份同向**前向折叠（R-CORE-02），不是跨方向互证 |
| 放宽 `NthPrevious` strict ordinal、坏 slot 时 fallback 到更旧 set | 违反 V4 不变量 5，会让「上下文突然回退到更早的 Recap」变成静默行为 |
| 把 `BeyondPrefix` 降级为字符串或吞掉 | 已由上一轮 contract normalization（N-E-02）明确否决 |

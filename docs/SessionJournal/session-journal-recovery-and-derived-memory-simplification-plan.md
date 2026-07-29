# SessionJournal 恢复与 DerivedMemory 化简：阶段情况与后续计划

> **状态**：Implemented Shape/Plan；P1～P6 已实施
> **日期**：2026-07-29
> **适用基线**：current Prepared v5 + DM-0～DM-8
> **相关文档**：
> [事件源会话与长期上下文架构路线图](event-sourced-session-architecture-roadmap.md)、
> [DerivedMemory 已实施方案](done/derived-memory-subsystem-implementation-plan.md)、
> [Tail Execution Recovery 后续化简候选](tail-execution-recovery-simplification-study.md)

## 0. 文档目的

current implementation 已从最初的 “tail-only recovery + derived context anchor” 扩展为完整的
request exact-reopen、derived epoch planning、multi-role maintenance、ArtifactSet publication 和
online lifecycle。实现具备较强的 correctness，但生产代码、测试和概念数量已明显超过最初预期，
人类维护者难以快速建立完整心智模型。

本文不立即推翻 current implementation，也不把已经实现等同于已经确认长期保留。它重新固定近期目标：

1. SessionJournal 可以针对调用方指定的 existing active named branch 做 tail-only runtime recovery；
2. `RuntimeConfigSetup` 持久化“使用第 n 个最近 DerivedArtifactSet”的 Agent 自主策略；
3. DerivedMemory 保持 logical event-anchor binding，但不规定必须逐 raw event 查询；
4. shared epoch planner 继续让多个 MemoryMaintainer 使用同一 history interval；
5. online selection 只保留一个 `NthPrevious(n)` 语义，`Latest` 等价于 `n = 0`；
6. 删除自动 `Budgeted` candidate search；必要的硬上限只做 fail-fast safety，不再成为另一种选择策略；
7. full replay/reducer 不再作为 SessionJournal online/runtime 主表面；若仍需要，迁移到明确的
   offline audit、recovery/fix tooling 或 test oracle 边界；
8. 把 DerivedMemory 中其余能力翻译成可理解的职责，再分别决定保留、隔离或删除。

本文是阶段性 Shape/Plan 文档。具体 wire shape、项目拆分和迁移提交仍需逐工作包设计与审阅。

## 1. 原始目标的最小模型

在一个由调用方按名称选中的 existing active branch raw head 上，online recovery 最终只需要构造：

```text
RecoveredSessionState {
  governing runtime config
  governing system prompt
  execution tail state
  selected derived context header
  dependency-closed recent raw suffix
}
```

逻辑流程是：

```text
selected active branch head（Engine 内绑定稳定 RefId）
  -> 恢复 execution tail
  -> 恢复最新 paired setup
  -> 读取 RuntimeConfigSetup 中的 nthPrevious
  -> 选择该 branch published coherent set lineage 上精确位置为 n 的 ArtifactSet
  -> 验证 set anchor 属于真实 Parent lineage
  -> 从 anchor 之后 fold dependency-closed raw suffix
  -> derived header + raw suffix
```

“event 绑定 ArtifactSet”只要求逻辑关系可验证。允许 current 的反向实现：

```text
ArtifactSet -> CommonAnchor(raw EventAddress)
ArtifactSet -> PreviousSetId
```

不要求 SessionJournal 对父链上的每个 event 发起一次 DerivedMemory point lookup。DerivedMemory 先沿
set lineage 找到第 n 个 candidate，SessionJournal 再以 raw Parent chain 验证 anchor，仍能保持
raw correctness authority 与 derived/raw 单向依赖。

## 2. 已确认继续保留的架构约束

### 2.1 Raw 与 derived 的 authority 方向

- raw SessionJournal events 是 execution/history correctness source；
- DerivedMemory 只单向引用 raw address/range/setup；
- raw event 不引用 artifact、epoch 或 ArtifactSet identity；
- missing/deleted DerivedMemory 不应破坏 raw audit 或已 Prepared request 的恢复；
- provider 给出的 anchor/setup/source assertions 必须由 SessionJournal 重新验证。

这组约束与最初目标不冲突，而且避免 derived pointer/index 被意外升级成事实源。

### 2.2 Self-contained Prepared

`CompletionRequestPrepared` 不只承担 setup lookup checkpoint。它还表示一个已经确定、即将产生外部
provider side effect 的 exact request。Prepared/Started crash window、tool identity 和 uncertain
outcome 都要求请求能够精确恢复。

因此近期不删除：

- exact context snapshots；
- raw range/hash；
- paired setup refs；
- tool/runtime/dispatch identity；
- canonical request commitment；
- execution checkpoint。

Prepared 后恢复继续 raw-only，不重新选择当前 DerivedArtifactSet，也不要求 DerivedMemory 仍存在。

### 2.3 Shared epoch

多个 MemoryMaintainer 必须读取同一个 immutable history interval。否则各 role 可能在不同 raw head、
不同 suffix split 或不同 setup 下生成内容，随后只能靠更多兼容规则勉强组成 ArtifactSet。

因此继续保留：

- immutable epoch plan；
- exact source interval；
- shared input set；
- epoch 使用的 setup/config provenance；
- 多 role 对同一 epoch 的一致性验证。

是否需要 current config/epoch 文件的每个 fingerprint 和 index，是后续实现化简问题；shared epoch
本身不是候选删除项。

### 2.4 Atomic ArtifactSet publication

多个 required role 中任何一个未完成时，都不能把半套 memory 暴露给 online request。ArtifactSet
必须在 required members 闭合后一次成为 usable candidate；旧 set 在新 set 完成前继续可用。

这一约束是 shared epoch 的自然下游，也继续保留。

### 2.5 Strict fresh bootstrap

真正首次启动时，DerivedMemory lineage 可能尚无任何真实 ArtifactSet。这个状态若一律按“第 n 个
不存在即失败”处理，Agent 将无法产生首个 completion request。因此保留唯一的 bootstrap 例外。
strict bootstrap 不证明“这个 branch 从未发布过 DerivedArtifactSet”，而是识别 first-online-request
仍处于 fresh-genesis topology。bootstrap eligibility 必须同时满足：

- provider 报告 selected branch 的 published coherent set lineage 为空，而不是 pointer 损坏、
  lineage 断裂或暂时查找失败；
- durable `SessionCreated` origin 必须是 `native`；`legacy-import` 不进入 online bootstrap；
- raw Parent ancestry 上不存在任何 `CompletionRequestPrepared`；
- raw tail 精确满足以下两个等价 bootstrap boundary 之一：
  - **A — pre-append**：从 `SessionCreated` 到 selected head 只有 governing
    `RuntimeConfigSetup` / `SystemPromptSetup` 更新；首次 `Send` 的 pending observation 尚未持久化；
  - **B — exact/recovery**：存在一个满足 A 的 fresh predecessor，其后恰有一个由 tail execution
    projection 认定为 active 的 first `ObservationAccepted`；该 observation 之后没有 Prepared、
    `AgentActionProduced`、`ImportedAgentAction`、tool execution/result、completion attempt/failure
    或第二个 observation 等 history/execution-bearing fact。

为证明该 topology，首次 online path 允许沿 headers 走到 `SessionCreated`；这不是 normal
non-empty-lineage request 的性能基线。B 覆盖 `Send` 已 append observation、尚未写 Prepared 的正常
第二阶段，以及在该窗口 crash 后的 reopen；“历史中曾出现某个 observation”不等于 active first
observation，不能把任意 observation history 重新解释成 fresh genesis。未知或无法安全分类的
post-genesis kind 也不得被当成 fresh topology。

满足上述资格后：

- 使用 `SessionCreated` 作为 raw suffix 的逻辑起点，以空 derived context header 加
  dependency-closed raw suffix 构造请求；
- 不为了走通接口而持久化一个内容为空的伪 ArtifactSet；
- bootstrap request 仍受最终 exact request hard guard 约束；
- 一旦该 branch 写入首个 `CompletionRequestPrepared`，bootstrap 由 raw history 永久关闭；Prepared
  crash reopen 走 self-contained raw-only 路径，之后即使 DerivedMemory 被删空也不得误入 bootstrap；
- 非空 lineage 上 `nthPrevious` 越界必须显式 not-ready，不得退回 bootstrap。

fresh genesis 上可能曾由 offline maintenance 发布过尚未被任何 Prepared 使用的 derived set；若它
随后被删除、provider 再次报告 healthy empty lineage，重新 bootstrap 是允许的，不改变任何 raw
correctness。反之，imported/legacy raw history 已包含 history-bearing events，不满足 fresh topology，
不能 bootstrap；但 import 本身不受阻，offline maintainer 与 rebuild 可以在首个 Prepared 前自由
发布真实 set，online request 随后按 exact ordinal 使用它，不形成死锁。

这不是第二种长期 selection mode，只是 derived lineage 为空时构造首个 completion request 的
初始化规则。

## 3. 已确认需要修正的方向

### 3.1 Branch/ref 成为正式目标

pre-P1 baseline 中 `SessionJournalEngine` 固定打开 `main`，DerivedArtifactEpochPlanner 也只接受
current main。这不满足“从指定 active branch head 沿 Parent chain 恢复”的目标。P1 已完成 raw
Engine，P2 已完成 DerivedMemory 与 CLI composition：调用方可选择 existing active branch，
Engine lifetime 与 durable derived lineage 都绑定 exact `RefId`。

目标 contract 应允许 composition root 显式选择 branch，例如：

```text
SessionJournalEngine.Open(path, branchSelector, runtime?)
```

branch name 只适合作为人类输入的 selector；DerivedMemory 的 durable lineage identity 应绑定
EventJournal 的稳定 `RefId`（或由它构造的 opaque identity）。active branch name 在 archive 后可被
新 ref 复用，不能直接作为持久 lineage key。至少必须区分：

- **选择并运行一个已有 branch**：近期目标；
- **创建/fork/archive/rename 的产品 UX**：不与 loader 泛化捆绑；
- **跨 branch 复用 DerivedArtifactSet**：后续目标，第一版可以明确不做。

P1 已证实 raw Engine 改造不需要改变父链算法：Open 时解析 branch name，随后 head/read/append/CAS
都使用 lifetime-bound `RefId`。P2 进一步把 latest pointer、epoch/config key、ArtifactSet、
transaction、repository validation、rewind stale-future 与 fork ownership 统一到 stable
`RefId`。branch name 只在 composition root 选择 Engine；`DerivedMemoryBranchScope` 由 repository
从 Engine 创建，调用方不能自由伪造 durable identity。

### 3.2 Context selection 成为 durable RuntimeConfigSetup policy

current `NthPreviousOrdinal` 位于进程注入的 `SessionRuntime.ContextSelection`，重启后不能只从 journal
恢复。新的目标是让 Agent 通过 durable setup 决定下一次请求聚焦多近或回顾多远。

建议的最小语义形状：

```text
RuntimeConfigSetup {
  ...
  derivedContext {
    nthPrevious: nonnegative integer
  }
}
```

关键语义：

- `0` 表示 published coherent set lineage 的最新 ArtifactSet；
- `n` 表示沿同 branch、同 coherence policy 的 set lineage 向前第 n 个；
- ordinal 是 selection 发生时的相对位置，不是 durable set identity；若 lifecycle 在 selection 前发布了
  新 set，ordinal 相对新 lineage tip 解释；
- ordinal 按 lineage 的精确位置解释；所需 link 或目标 set 损坏、不完整、不可验证时必须失败，
  不允许跳过坏 set 后把更旧 set 重新编号为第 n 个；
- setup 仍遵守 sticky/latest-on-Parent-lineage 规则；
- Agent 可以在合法 idle/failed boundary 通过系统能力追加新的 RuntimeConfigSetup；
- Prepared 前按 governing setup 选择；Prepared 后 exact reopen 不重新解释 `nthPrevious`；
- 非空 lineage 上找不到第 n 个 set 时显式 not-ready，不静默改用更近或更远的 set；严格空 lineage
  仅适用 §2.5 的 fresh bootstrap。

这会触发 RuntimeConfigSetup body schema 的直接 cutover。项目尚未发布，不保留旧实验 wire 的
compatibility decoder。

近期先冻结“每个 branch 一个 active coherence group + durable ordinal”。如果 Agent 还需要在同一
branch 的多个 coherence groups 之间切换，则 group/policy identity 也必须进入 durable setup，否则
“第 n 个 set”没有唯一 lineage。该扩展不应通过 host 重引入第二个易漂移选择真源。

“Agent 自己决定”在 core 层只表示：上层把 Agent 已做出的决定在下一个 safe idle/failed boundary
追加为 RuntimeConfigSetup。是否暴露 self-service tool、如何把 tool-loop 中间的请求延迟到安全边界，
属于独立 composition/UX 切片。

### 3.3 Selection mode 收成一个 ordinal

目标 API 不再暴露：

- `Latest` mode；
- `Budgeted` mode；
- “尝试多个 candidate，自动找一个能装下的” fallback；
- ordinal 与自动 cost policy 的组合状态空间。

provider 可以直接沿 DerivedMemory `PreviousSetId` 走 n 步，只返回 ordinal `n` 的 descriptor；
SessionJournal 验证其 anchor/setup/raw ancestry 后再 materialize exact text。保留
descriptor/materialization 两阶段只服务 bounded IO 与 raw authority validation，不再返回 `n + 1`
个 descriptors，也不再表达多种 planner mode 或公开 `MaxCandidateCount`。实现内部仍应有合理的
ordinal 上限，防止无界 lineage walk，但它不是 session policy。

### 3.4 区分“选择策略”和“硬安全上限”

删除 `Budgeted` 不等于允许生成超过 provider/model 上限的请求。

需要区分：

1. **epoch scheduling thresholds**：决定何时让 maintainers 生成新 set；
2. **durable nthPrevious**：Agent 主动决定选哪个已有 set；
3. **request hard limit**：选中的 exact request 若物理上不可接受，必须 fail-fast。

`Budgeted` 与 epoch planner 并不真正功能重叠：planner 只决定何时生成 shared epoch，Budgeted
则在 request time 自动比较多个已发布 sets。删除 Budgeted 的理由是后者违背 Agent 明确选择 ordinal
的控制语义，并引入多候选测量/materialization/fallback，而不是 planner 已替代它。

第 1 项保留在 shared epoch planner；第 2 项进入 RuntimeConfigSetup；第 3 项保留与选择解耦的最小
hard-limit 集合，不得自动改选另一个 set。目标是删除 raw-suffix 与 bootstrap selection budgets，
并先盘点 model、host、bootstrap、planner 等现有上限；只有能证明语义相同的字段才合并。面向最终
completion request 的 total guard 可以收成例如 `MaximumCanonicalRequestEstimate` 的单一
deterministic fail-fast。current estimator 只是 canonical JSON byte length 的粗略换算，不是真实
model tokenizer；在 completion surface 提供真实 capability 前，文档和异常不得宣称它等于模型
context-window hard limit。

### 3.5 Full reducer 退出 online/runtime 主表面

P5 已确认 online recovery 不需要 full reducer，并已从 public/runtime core 删除 full
projection/replay surface 与 production reducer。

目标边界：

```text
SessionJournal online/runtime
  -> tail execution recovery
  -> governing setup recovery
  -> bounded suffix fold

SessionJournal offline/test companion
  -> full raw audit
  -> migration/recovery/fix inspection
  -> differential oracle（若仍证明有净价值）
```

不能直接删文件后让 untrusted import、offline validation 或 maintainer provenance 失去检查。当前
production caller 已经很少：online path 和 DerivedMemory/Maintainers 均不调用 full projection。
P5 已完成：legacy importer 使用 Offline report + exact lineage/setup 验证；public full
projection surface 与 production reducer 已删除。head/phase inspection 使用
`InspectExecutionBoundary()`，完整 pending/checkpoint recovery 使用 internal
`ResolveExecutionTail()`，setup 使用 exact head `ResolveGoverningSetup()`，history/provenance
使用 bounded `ReadHistoryPlanningWindow[At]()`。

验收重点不是“测试还引用 reducer”，而是 online/reopen 路径在没有 reducer 的 production dependency
时仍覆盖全部 legal/illegal tail 状态。

推荐目标是独立 `SessionJournal.Offline` / `SessionJournal.Recovery` 边界，而不是完全删除审计能力。
它仍可检查冷前缀、历史上的每个 Prepared 和 import 后的全链语义，但不再是 runtime fallback。项目
拆分时不得把普通 companion assembly 加入 `InternalsVisibleTo`；应通过移动 audit 所需实现、抽取窄
protocol/audit substrate 或其他正常程序集边界解决访问问题。

## 4. 其余 DerivedMemory 术语的白话说明与阶段判断

| Current 名称 | 实际解决的问题 | 阶段判断 |
| --- | --- | --- |
| orchestration transaction | 标识“一次让所有 role 维护同一 epoch”的工作 | 不属于 tail loader；启用自动维护时保留满足 missing-role-only resume 的最小 durable identity |
| role settlement | 记录某个 maintainer 已成功，重启后不重复昂贵 LLM 调用 | 保留最小 durable 形状；已 settlement role 在 resume 时不得重跑 |
| finalization intent | required roles 完成后，先冻结将发布的 exact members，再原子发布 set | 保留最小 durable 形状；finalization 后只允许续发布/验证，不再调用 maintainer |
| online lifecycle coordinator | 在 safe boundary 自动 plan epoch、运行/恢复 maintainer、发布 set | composition 能力；应与 core recovery 分层，可选启用 |
| backpressure | maintainer 落后且 raw suffix 接近硬上限时，拒绝继续增长历史 | 保留 fail-fast safety，但不要与 candidate auto-selection 混为一谈 |
| strict repository validation | 离线检查 derived 文件、hash、lineage、raw anchor 和 pointer 是否自洽 | 保留在 offline/ops；不得成为每次 online request 的全库扫描 |
| latest pointer rebuild | index 丢失时从 immutable sets 恢复 tip | 派生 index 的必要修复能力；branch-aware 改造后重新定义 |
| producer/policy/job fingerprints | 判断一次重跑是否真的是同一输入、prompt、model 和 policy | **P6 KEEP**：transaction/job/producer/policy/topology/candidate/attempt identity 保留；跨库 generation 的 JobFingerprint/TransactionId 与 Candidate/Attempt 合并延后 |

近期不尝试一次性删除这一整组能力。P6 已确认 maintenance crash resume 必须只补失败/缺失
role；专项审查只决定满足该 contract 所需的最小 identity/fingerprint 集合，不再重新讨论是否保留
settlement/finalization。

## 5. P0 Contract Inventory

本节是 P1～P6 的实施入口。表中的 **current** 描述 2026-07-29 基线事实，**target** 描述本轮已经
冻结的边界；`done/` 下的历史计划只保留当时事实，不反向覆盖这里的 active target。

### 5.1 四类 ownership map

| Ownership | Current symbols / files | Current 职责 | Target 边界 |
| --- | --- | --- | --- |
| **online core** | `SessionJournalEngine`、`SessionExecutionTailResolver`、`SessionAuthoritativeGoverningSetupResolver`、`SessionTailContextProjection`、`SessionContextCandidateContracts`、Prepared v5 codec/manifest，位于 `prototypes/SessionJournal/` | raw append/CAS、tail execution recovery、governing setup、bounded suffix fold、store-neutral candidate validation、request preparation/dispatch 与 exact reopen | **P5 已完成**：绑定 selected active branch 的稳定 `RefId`；只保留 tail/bounded online path 与 neutral contracts；不引用 concrete DerivedMemory/Maintainers；无 public full projection/replay surface |
| **offline audit** | `SessionJournalOfflineValidator`、`SessionJournal.Cli validate`、legacy importer verification 与 corruption tests | checked root-to-head scan、全链 codec/状态审计、import smoke verification、与 exact-head tail/setup 结果交叉验证 | **P5 已完成**：位于明确的 `SessionJournal.Offline` companion；允许付出 full scan 成本，但不是 online fallback，也不向 core 反向泄漏依赖 |
| **derived maintenance** | `DerivedArtifactEpochPlanner`、`DerivedMemoryArtifactStore`、`DerivedArtifactSetStore`、`DerivedArtifactSetContextCandidateSource`、`DerivedMemoryOrchestrator` / `DerivedMemoryOrchestrationStore`、`DerivedMemoryMaintainerRunner`，位于 `prototypes/SessionJournal.DerivedMemory/`；concrete profiles 位于 `SessionJournal.Maintainers` | shared epoch、candidate production、durable settlements/finalization、atomic ArtifactSet publication、latest/index rebuild、strict repository validation 与 online lifecycle | **P6 已完成**：RefId-derived lineage、shared epoch、backpressure、per-role settlement、bounded finalization v2 与 atomic publication；selection 只按 exact `nthPrevious`；transaction/job/provisioning identity 保留 |
| **composition** | `SessionJournal.Cli` / Agent Host、`SessionRuntime`、connection/tool/role provisioning、`DerivedMemoryOnlineLifecycleCoordinator` 装配 | 选择 completion/tool/provider、注入 candidate source/lifecycle/runtime selection、配置 planner/policy、运行 online turn 与 ops commands | 按 active branch name 选择并先打开 Engine，再把其 lifetime-bound lineage identity 绑定到 DerivedMemory；host 固定每 branch 的单 active memory domain；Agent ordinal 来自 durable setup，不再由 runtime 注入第二份 |

ownership 描述能力归属，不要求每类必须对应一个程序集。P5 可以先建立 companion boundary，再决定
最终 project name；不得用 `InternalsVisibleTo` 把普通 companion 伪装成 test assembly。

### 5.2 P1～P6 touch matrix

| 包 | 关键 symbols / files | 冻结目标 | Persistence / wire effect | Public API effect | CLI / composition effect | Focused tests / docs | First changing package |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **P1** | `SessionJournalEngine.cs`、`SessionHistoryPlanning.cs`、`SessionJournalOfflineValidator.cs`；EventJournal `RefId` / `OpenBranch` / `CommitToRef` | existing active branch name 只在 Open 时解析；Engine lifetime 绑定 exact `RefId`，所有 head/read/append/CAS 使用该 ref；pre-P1 name-only `CommitToRef("main", ...)` 退出 append path | 无 raw event wire 变化 | 新增 branch-scoped `Open`/inspection；`main` overload 仅为 default；必要时给 EventJournal 增加 RefId-bound commit | P1 不改 CLI；branch selector 延后至 P2，与 DerivedMemory lineage authority 和 composition 联合 cutover，避免暴露只能驱动 raw Engine 的半成品入口 | `SessionJournalEngineTests`、tail/recovery/planning tests；增加 archive/rebind race 覆盖；更新 active API docs | `Atelia.SessionJournal`，必要时先扩 `Atelia.EventJournal` |
| **P2** | `DerivedMemoryRepository.cs`、`DerivedArtifactEpochPlanner.cs`、`DerivedArtifactSetStore.cs`、`DerivedArtifactSetContextCandidateSource.cs`、`DerivedMemoryOnlineLifecycleCoordinator.cs`、DerivedMemory contracts | planner/config/epoch/set/latest 均绑定 Engine 暴露的 RefId-derived identity；path-only authority check 退役；每 branch 独立 lineage | **新的 DerivedMemory storage/schema generation**；旧 branch-name key 与新 RefId key 不混读、不 fallback | derived planning/publication/rebuild/validation 接受或验证 exact engine lineage | `configure/plan/run/publish/rebuild/validate/run-online-turn` 以 `--branch` 为人类 selector，不再接受可与 Engine 冲突的自由 durable identity | epoch/set/integration/orchestration tests、CLI E2E；更新 DerivedMemory current docs | `SessionJournal.DerivedMemory`，随后 `SessionJournal.Cli` |
| **P3** | `SessionJournalContracts.cs`、`SessionEventCodec.cs`、`SessionContextCandidateContracts.cs`、`SessionJournalEngine.cs`、DerivedMemory candidate source | durable `nthPrevious` 成为唯一 Agent-controlled ordinal；exact nth 不跳坏 set；zero-input bootstrap exception 保留，fresh-topology eligibility 由 P4 实现 | `RuntimeConfigSetup` body **v1 → v2**；只写/读 v2，不提供 v1 compatibility decoder | 删除 `SessionRuntime.ContextSelection`、`Latest` / `Budgeted` modes、candidate-list fallback 与 public `MaxCandidateCount` | host 只 provision 单 active domain/provider；不再解析或注入 runtime selection flags | codec/golden、governing setup、candidate route、Prepared reopen、CLI tests；更新 wire/current docs | `Atelia.SessionJournal`，同一 cutover 内跟进 DerivedMemory/CLI |
| **P4** | `SessionJournalEngine` selection/cost helpers、`SessionHistoryTokenEstimator`、planner hard-limit/backpressure config、online coordinator | **已完成**：删除 raw/bootstrap/total token budgets；保留 exact canonical request UTF-8 byte guard 和 planner backpressure；以 raw header walk 判定 fresh-genesis 的 pre-append 与 exact/recovery bootstrap boundaries | `SessionCreated` body **v1 → v2**，required `origin=native|legacy-import`；无 v1 fallback。planner persisted shape/id/schema v2 不变 | 删除 `SessionContextBudgetOptions`；新增单一 `MaximumCanonicalRequestBytes`，不宣称 tokenizer | 删除三个 budget flags，新增 `--maximum-canonical-request-bytes`；planner threshold/backpressure flags 保留 | byte guard/bootstrap pre-append、post-observation crash/reopen、import/performance/backpressure tests；更新 estimator 与 CLI docs | `Atelia.SessionJournal` + composition（完成） |
| **P5** | `SessionJournalOfflineValidator`、checked audit scan、legacy importer、CLI `validate`、tail/bounded tests | **已完成**：full reducer 退出 online/public core surface，完整 raw/import audit 不降级 | 无 raw/derived wire 变化 | 已删除 full projection/replay public surface；保留窄 inspection、tail recovery、setup 与 bounded planning API | importer/validate 已切到 companion audit；online composition 不引用 full reducer | offline validator、import safety、corruption、tail/fold legality；usage/roadmap 已更新 | P5-A～D（完成） |
| **P6** | `DerivedMemoryOrchestrationContracts.cs`、`DerivedMemoryOrchestrationStore.cs`、`DerivedMemoryOrchestrator.cs`、`DerivedArtifactSetStore.cs` | **已完成**：settled role 重启不重跑；per-role settlement + bounded finalization v2 + required-role atomic publication | finalization **v1 → v2**，无 compatibility read；transaction/artifact/set schemas/ids 不变 | 新增窄 `DerivedMemoryFinalizedRole`；finalization 删除 transaction 可推导字段 | 无 CLI/raw wire 变化；调用方继续复用固定 job provisioning 才能恢复同一 transaction | wire golden/v1 reject、partial resume、finalization-before-set、tamper/strict validation | `Atelia.SessionJournal.DerivedMemory`（完成） |

### 5.3 Persistence / wire ledger

| Generation / record | Current | Frozen cutover rule | Compatibility / rebuild rule | First package |
| --- | --- | --- | --- | --- |
| Raw `CompletionRequestPrepared` | body v5，self-contained exact context/request manifest | P1～P6 均保持 v5 exact reopen 语义；不得重新引入 derived id 或 reopen-time selection | 已 Prepared recovery 永不访问 DerivedMemory；任何字段变化需独立 wire decision，不在本轮顺手修改 | 无 |
| Raw `RuntimeConfigSetup` | **P3 已完成**：body v2，required `derivedContext.nthPrevious >= 0`；ordinal 只来自 governing setup | 已删除 runtime/CLI ordinal source，只写/读 v2 | **不提供 v1 decoder/fallback**；codec goldens、setup hash/provenance 与 import/create callers 已更新 | P3（完成） |
| Raw `SessionCreated` | **P4 已完成**：body v2，required `origin=native|legacy-import` | public create 默认/显式写 native；legacy importer 写 legacy-import；strict bootstrap 只接受 native | **不提供 v1 decoder/fallback**；origin 是区分 native first-observation crash 与 imported pending observation 所需的 durable fact | P4（完成） |
| DerivedMemory lineage/storage | **P2 已完成**：`derived/memory/v2/`；durable identity 使用 canonical lowercase `RefId`；config/epoch/transaction 为 v2，set/latest 为 v3 | P2 已按 RefId-derived identity 完成 generation cutover；planner、set/latest、epoch、orchestration 使用同一 opaque identity | v1 generation inert；不混读、不自动迁移、不设 compatibility branch；derived 数据可从 raw 重建 | P2（完成） |
| Planner config / epoch thresholds | P2 后 config/epoch/pointers 均为 v2，包含 scheduling headroom、hard limit/backpressure policy | P4 已保留全部 epoch scheduling/backpressure 字段、estimator id 与 identity bytes | persisted shape 未变化，继续使用 v2；pointer/index 仍可从 immutable generation 重建 | P4（完成，无 schema 变化） |
| Orchestration transaction / settlement / finalization | **P6 已完成**：transaction v2、settlement v1 保持；finalization v2 仅含 transaction id、anchor setups、included roles、omitted optional roles、expected set id | finalization 不重复 job/epoch/policy/previous-set 或 nested settlement transaction id；联表 immutable transaction 与 durable settlements 重建 authority | finalization v1 不兼容读取；transaction/artifact/set schema/id 不变。JobFingerprint/TransactionId 与 Candidate/Attempt 跨库合并延后到独立 generation | P6（完成） |
| Bootstrap eligibility | **P4 已完成**：healthy empty lineage + native creation origin + raw ancestry 无 Prepared；接受 fresh setup-only predecessor 的 pre-append boundary，或其后恰有一个 active first observation 的 exact/recovery boundary | lifecycle 前先做 read-only empty-lineage/topology/byte preflight，之后重新 exact selection | 只限制 online bootstrap，不限制 import/offline maintenance/rebuild 发布真实 set；曾发布但未被 Prepared 使用的 set 删除后仍可 bootstrap | P4（完成） |

P2 与 P6 的 derived generation 没有合并：P2 改变 lineage authority；P6 只创建独立
finalization v2，保持 transaction/artifact/set generation 与 ids。未来跨库 identity 合并仍必须
作为新的独立 generation decision。

### 5.4 双真源与 atomic gates

pre-P1 baseline 曾包含以下 raw authority split，P1 已解决并留下 focused evidence：

- Engine head/read 通过缓存 `_mainRef`，append 最后却重新调用 name-only
  `CommitToRef(SessionJournalDefaults.MainBranchName, ...)`；archive 后同名 rebind 会使两条路径指向
  不同 ref。现在 Engine 在 Open 时保存 exact `BranchRefId`，所有 current head/read/append/CAS
  都使用它；测试覆盖 old ref 在 archive + same-name rebind 后不会跳转到 replacement ref。

截至 P3 完成后，current authority 状态与后续边界包括：

- P2 已消除 DerivedMemory 的自由 string identity：composition 先按 branch name 打开 Engine，
  再用 `DerivedMemoryRepository.Bind(engine)` 取得 lifetime-bound exact `RefId` scope；
  branch-local planning/publication/rebuild/validation 与 raw authority 已闭合；
- P3 已消除 ordinal authority split：Agent ordinal 只来自 governing
  `RuntimeConfigSetup.derivedContext.nthPrevious`，runtime/CLI 不再提供第二入口；
- full reducer、tail resolver 与 suffix fold 是三条不同 traversal。P5 可以把 full audit 移出
  online core，但在 companion audit 落地前不能把“重复”当作删除 corruption/import checks 的理由。

1. **P1 ref lifetime gate（已满足）**：active branch name 只在 `Open` 时解析一次。Engine 保存 exact `RefId`；
   append 必须针对该 lifetime-bound ref 做 expected-head CAS，不能每次 append 重新按 branch name
   lookup 后误写 archive/rebind 后的另一 ref。P1 的 branch isolation、closed/rebound ref 与
   selected-branch Send/Resume tests 已提供 evidence。
2. **P1 + P2 release gate（已满足）**：composition 先打开 selected Engine 再绑定 exact scope；
   A/B branch 的 config、epoch、set、latest/provider 与 exact/global validation 已有 focused evidence，
   archive + same-name recreate、foreign scope、rewind stale-future 也会 fail fast。
3. **P3 single-source gate**：`RuntimeConfigSetup` v2 与 Engine/provider ordinal contract 必须同包
   cutover；不得同时保留 `SessionRuntime.ContextSelection`、CLI runtime selection flags 或另一份
   host ordinal fallback。host 只拥有固定 selection domain 的 provisioning，不拥有 Agent ordinal。
4. **Prepared atomic gate**：Prepared 前可以按 governing setup 和 current set tip 解释 ordinal；
   Prepared 写入后 exact request 只由 Prepared v5 恢复，setup、tip、provider 或 DerivedMemory 变化
   均不得重选。
5. **P4 backpressure gate**：删除 `Budgeted` 和 selection budgets 不得删除 shared epoch planner 的
   scheduling thresholds、hard limit 或 explicit backpressure。request guard 与 maintenance
   backpressure 是两个 authority。
6. **P4 bootstrap topology gate**：zero-input bootstrap 只在 healthy empty lineage 与 raw
   fresh-genesis topology 同时成立时开放；topology 只接受 setup-only pre-append，或该 predecessor
   后恰有一个 active first observation 的 exact/recovery boundary。imported/legacy history 明确不
   满足，但 offline maintenance/rebuild 不受该 online gate 限制。
7. **P5 audit gate**：移除 public/full reducer residency 前，CLI validate、untrusted import、
   malformed/corrupt history 与 historical Prepared 检查必须已有等强或更强的 companion audit；
   tests 变绿但审计覆盖减少不算完成。
8. **P6 resume/publication gate**：每个 role 的成功 settlement 必须先 durable，resume 只运行
   missing/failed roles；required roles 闭合后 durable finalization v2 冻结窄 included roles、
   omitted optional roles 与 expected set identity，expected previous 来自 immutable transaction。
   finalization 后不得再次调用 maintainer，只能 idempotent 续发布/验证；ArtifactSet 仍须一次原子成为 usable。

### 5.5 CLI、tests 与 current docs ledger

| Surface | Current fact | P0 标记 | 后续改动包 |
| --- | --- | --- | --- |
| `SessionJournal.Cli run-online-turn` | **P4 后**要求 `--branch`，ordinal 来自 governing setup；只保留可选 `--maximum-canonical-request-bytes`，三个旧 budget flags 已拒绝 | branch、ordinal、fresh bootstrap 与 request-size authority 已闭合 | P4（完成） |
| DerivedMemory ops CLI | branch-local `configure/plan/run/publish/rebuild` 要求 `--branch`；`validate` 不带 branch 验证所有 active refs、带 branch 验证 exact selected ref；list 是 global inventory | P2 已闭合 branch name selector、durable RefId identity 与 Engine authority | P2（完成） |
| raw Engine tests | P5 前大量普通断言把 full projection 当作 head/state getter；tail/performance tests 另设 invocation counter | 便捷调用不构成保留 public full projection 的理由 | **P5 已完成**：head/phase、tail state、setup、history/provenance 分别迁到窄 API；删除 invocation counter 与复制的 full reducer oracle |
| candidate route tests | 已覆盖 durable exact ordinal、setup update/reopen、selection-time lifecycle、canonical-byte guard、native fresh bootstrap、observation crash/reopen 与 imported rejection | P3/P4 已删除 mode/list/fallback 与临时 budget coverage | P3/P4（完成） |
| DerivedMemory planner/set tests | 已覆盖 A/B branch config/epoch/set/latest/provider 隔离、exact/global validation、archive + same-name recreate、foreign scope、rewind stale-future、pointer rebuild 与 raw authority | P2 branch-aware matrix 已形成；P3～P6 继续复用这些边界 | P2（完成） |
| orchestration tests | 已覆盖 partial settlement、cancellation、optional omission、finalization 后续发布、pointer rebuild 与 corruption | P6 必须保留的 contract tests；字段化简不得降低 missing-role-only resume/atomic publication 覆盖 | P6 |
| active roadmap | 同时记录 historical evolution、current DM-8 和未来方向 | 已标记 superseded 段落并链接本文作为 P1～P6 implemented plan | P0～P6（完成） |
| DerivedMemory / CLI README | 已同步 P3 exact nth、strict lineage、durable ordinal 与无 runtime selection flag；settlement/finalization 仍记录 current behavior | current behavior 文档；后续不得提前写成未实施 target | P3（完成）；P4～P6 随实现同步 |
| `docs/SessionJournal/done/**` 与 historical trunk baseline | 记录当时已实施设计和验收 | 保持 append-only 历史，不以新 target 回写旧结论 | 不修改 |
| tail recovery research/study | 记录 D0/D1 与 DM-8 current facts，部分候选结论已被本文后续决策取代 | 作为研究背景；active 决策冲突时以本文为准 | 后续仅补 supersession note |

### 5.6 P0 验收

P0 只改变文档 authority，不改变 production behavior。完成必须同时满足：

- 本文明确成为 P1～P6 的 active Shape/Plan，且 current roadmap 链接本文；
- current implementation、active target 与 `done/` historical record 三种状态不混写；
- online core、offline audit、derived maintenance、composition 四类 ownership 均有 current/target
  边界；
- P1～P6 均列出关键 symbols/files、persistence/wire、public API、CLI/composition、tests/docs 与
  first changing package；
- 明确记录 P2 RefId-derived DerivedMemory generation、P3 `RuntimeConfigSetup` v1 → v2，以及
  P6 任意 persisted/hash identity 删除所需的独立 schema decision；
- P3 不留下 runtime ordinal fallback，P4 不把 planner backpressure 与 request guard 合并，P5
  不因移出 reducer 而削弱 import/offline audit；
- strict bootstrap 明确定义为 P4 的 raw fresh-genesis topology 判定，不声称证明“从未发布 set”，
  也不限制 import/offline maintenance/rebuild；
- P6 明确保留 role-level settlement、durable finalization、missing-role-only resume 与 atomic
  ArtifactSet publication，不再把它们列为待回答的产品问题；
- active roadmap 不再把 automatic Budgeted selection 或 full reducer 驻留 online core 写成长期
  target，并把 current Prepared 版本写为 v5；
- 不修改 production code、tests、`done/**` 或描述 current CLI/README behavior 的正文；
- `git diff --check` 通过，本文与 roadmap 新增/修改的相对 Markdown links 均存在。

## 6. 后续工作包

### P0：阶段文档与 contract inventory

目标：

- 以本文固定重新收束后的目标；
- 为 current APIs 标注 online core、offline audit、derived maintenance、composition 四类 ownership；
- 列出受影响的 wire、public API、CLI、tests 和 current docs；
- 不改 production behavior。

完成标准：

- 满足 §5.6；
- 所有后续工作包都能引用一组一致的目标/非目标。

### P1：Branch-scoped raw Engine

目标：

- Engine 按调用方给出的名称打开 existing active branch，并在内部绑定其稳定 `RefId`；
- append/CAS、tail resolver、governing setup、planning window 都使用选中的 ref；
- current `main` 只作为 convenience default，不再是内部 invariant。

第一阶段非目标：

- branch create/fork/archive UI；
- 跨 branch DerivedArtifactSet reuse；
- merge/multi-parent history；
- archived/closed ref；
- `MoveRef` 后 DerivedMemory 自动重基。

验收（本包使用 branch-neutral fake/in-memory candidate provider；不能单独宣称真实 DerivedMemory
已经支持 branch-aware recovery）：

- 同一 repository 两个 branch 可独立 Open/Send/Resume；
- 两个 branch 的 head、setup cursor、execution checkpoint 不串线；
- 只沿 selected head 的真实 Parent lineage 读取；
- branch 不存在时在任何写入前失败；
- archived/closed ref 在第一阶段明确不支持并在任何写入前失败；
- existing main behavior 保持。

实施状态（2026-07-29）：**P1 raw Engine boundary 已完成**。

- `SessionJournalEngine.Open(path, branchName[, runtime])` 在 Open 时把 existing active branch name
  解析为一个 exact `RefId`；Engine 暴露只读 `BranchName` / `BranchRefId`，其 lifetime 内所有
  current-head、tail recovery、setup/planning、append/CAS 都使用该 `RefId`。原 `Open(path[, runtime])`
  保留为 `main` convenience。
- EventJournal 新增 `CommitToRef(RefId, ...)`；name overload 只负责一次解析并委托。default、
  missing、closed ref 在 EventFrame append 前失败；expected-head CAS mismatch 仍保持既有
  “event 已追加、ref 未移动并报告 orphan address”的语义。
- focused evidence 覆盖 main/feature 的 projection、header lineage、planning seed 与 head 隔离，
  branch-scoped Send → Prepared → reopen Resume，仅 selected ref 前移；missing/archived branch
  零 event mutation；dispose owning Engine 后外部 `MoveRef`，再次 Open 能观察新 head；archive 后
  同名重建不会使持有旧 `RefId` 的提交跳到 replacement ref。
- 当前仍是 process-local ref cache、single-driver、非线程安全模型；P1 不承诺 owning Engine 存活时
  由另一个 EventJournal instance live move/archive 后自动 refresh，也不宣称跨 instance CAS。
  需要外部 ref 操作时先 dispose owner，完成操作后 reopen。
- P1 没有修改 raw event wire，也没有增加 branch create/fork/archive UI、CLI `--branch`、
  offline validator branch surface 或 concrete DerivedMemory branch authority；这些分别留在
  composition/P2/后续明确工作包。故当前只可宣称 raw Engine branch-scoped，不可宣称真实
  DerivedMemory E2E 已 branch-aware。

### P2：Branch-aware DerivedMemory lineage

实施状态（2026-07-29）：**已完成**。

- 新 generation 固定为 `derived/memory/v2/`；planner config/epoch 使用 v2 schema，
  ArtifactSet/latest 使用 v3 schema，transaction 使用 v2 schema。旧 v1 目录 inert，不混读、
  不 fallback。
- durable identity 全部使用 canonical lowercase `RefId`；fork 不继承 set/config lineage，archive
  后同名重建得到新 ref，也不会继承旧 derived state。
- branch-local API 由 `DerivedMemoryRepository.Bind(engine)` 提供 scope；wrong repository、
  wrong ref 与 stale-future 在 derived/LLM side effect 前 fail-fast。
- branch validation 只验证 selected ref；global validation 枚举全部 active refs，并拒绝 stored
  non-active/archive ref；两者都通过 `SessionJournalEngine.OpenReadOnly` 读取 raw，malformed
  active tail 不会被 validation recovery/truncate。
- provider 的 missing-pointer fallback 只做 immutable unique-tip discovery；持久 pointer rebuild
  必须先通过 selected Engine 的 raw-authority gate。orchestration raw mutations 不再是 public
  surface，composition 通过 engine-bound Prepare → finalization → Publish API 完成发布。
- CLI branch-local 命令统一使用 required `--branch <name>`；validation 不带 branch 时是 global，
  带 branch 时是 exact-ref。
- evidence 覆盖 A/B config/epoch/set/latest 独立、fork/同名重建、scoped/global validation、
  pointer rebuild、wrong scope/engine 零写入、rewind 后 Send 零 LLM + validation/rebuild 失败且
  derived files 不变，以及 CLI 两 branch E2E 与 missing/wrong branch 零副作用。

目标：

- planner key、ArtifactSet latest pointer、epoch lineage 与 selected branch 使用同一稳定 identity；
- fork/rewind 后不会选择不在 current Parent lineage 的 future set；
- missing/stale pointer 可重建，但重建不能跨 branch 误选 global tip。

第一阶段允许每个 branch 独立生成 DerivedMemory，不做跨 branch reuse；rewind 后若原 derived tip
超出新 head，允许明确 fail-fast，不要求自动回滚 pointer 或生成新 derived generation。无 engine 的
global validation 必须改成按 lineage/ref 分组验证，不能打开一个默认 main engine 检查所有 derived
keys。

验收：

- branch A/B 各有独立 latest set；
- rewind 后较新的 abandoned-future set 不再 usable，且未实现 reconciliation 时明确失败；
- wrong-branch anchor 在 materialization 前 fail-fast；
- 删除 derived indexes 后能按 branch 重建；
- P1 + P2 联合验收后，才可宣称真实 DerivedMemory 参与的两个 active branches 均可独立
  Open/Send/Resume。

### P3：Durable NthPrevious cutover（已完成）

实施结果（2026-07-29）：

- `RuntimeConfigSetup` 已直接切到 strict body v2，required
  `derivedContext.nthPrevious >= 0`；v1 不兼容读取；
- provider contract 已收成 `SelectAsync(boundary, nthPrevious)` 的单 descriptor/status，
  区分 `Selected`、`EmptyLineage`、`OrdinalUnavailable`；
- `Latest` / `Budgeted` modes、candidate list/bound、automatic fallback、
  `SessionRuntime.ContextSelection` 与 CLI `--selection` 已删除；
- 未 Prepared request 在 lifecycle 之后按 exact governing setup 选择；Prepared v5 exact reopen
  不访问 DerivedMemory，未改变 wire；
- P4 已删除临时 `SessionRuntime.ContextBudgets`，改用 exact canonical request byte guard。

目标：

- `nthPrevious` 进入 RuntimeConfigSetup；
- governing setup 是 Agent-controlled ordinal 的唯一 durable source；
- provider contract 只表达 ordinal selection；
- `Latest` 作为 `n = 0` 的语义别名从 production contract 删除；
- `Budgeted` mode 和 automatic candidate fallback 删除；
- current 单 active coherence group 假设显式化；
- host 固定并绑定每个 branch 的单一 active memory selection domain/policy；若未来允许 Agent 在多个
  domain 间切换，domain identity 也必须进入 RuntimeConfigSetup；
- `SessionRuntime.ContextSelection` 删除，Engine 只读取 authoritative governing setup。

验收：

- runtime config 更新后，下一次未 Prepared request 使用新 ordinal；
- dispose/reopen 后不重新注入 selection options 也得到同一选择；
- lifecycle 若先发布新 set，ordinal 按 selection-time tip 解释；
- Prepared 后再次修改 setup 或 derived latest，不改变 exact reopen；
- 非空 lineage 上 ordinal 不存在、所需 lineage link 损坏、anchor 不合法或 exact candidate
  不可用时显式失败，不跳过、不重编号、不进入 bootstrap。

### P4：Budget 与 estimator 化简

> **实施状态：完成。** Commits：`d492b080`（SessionCreated v2 origin）、
> `af46044e`（canonical request byte guard）、`234a37db`（fresh bootstrap topology）。
> 最终 metric 是 canonical request JSON 的精确 UTF-8 byte length，不是 token estimate。
> DerivedMemory planner estimator、thresholds、hard-limit/backpressure 与 v2 persisted schema 均未改变。

目标：

- 删除用于自动 candidate search 的 cost state；
- 删除 raw suffix/bootstrap selection budgets；
- 盘点并收成最小的 non-selection hard-limit 集合；面向最终 request 使用 canonical JSON UTF-8
  byte length 的 deterministic guard；
- planner thresholds 只负责 maintenance scheduling，不暗中改写 Agent 的 nth choice。

验收：

- 没有按成本自动改选 candidate 的 production branch；
- 选中的第 n 个 request 超限时不追加 Prepared、不调用 completion client；candidate provider
  可以为估算 materialize exact candidate，但不得执行外部 completion side effect；
- fresh bootstrap 同时验证 healthy empty derived lineage、raw ancestry 无
  `CompletionRequestPrepared`，并覆盖 setup-only pre-append 与其后恰有一个 active first
  observation 的 exact/recovery 两种 boundary；
- `Send` append first observation 后、Prepared 前 crash/reopen 仍能进入同一个 bootstrap request
  planning；第二个 observation、已完成/历史 observation 或任一后续 action/import/tool/attempt/failure
  fact 均使 topology 不合法；
- imported/legacy non-genesis history 在 empty lineage 时明确 not-ready，但 offline maintenance 可在
  首个 Prepared 前发布 set；fresh genesis 上未被 Prepared 使用的 set 删除后仍允许 bootstrap；
- bootstrap 使用同一个最终 request guard，不再有专用 bootstrap selection budget；
- 10k cold prefix 仍不增加 selected anchor 之前的 payload reads。

### P5：Full reducer 去生产化

> **已完成**：P5-A 已在 core 提供 branch/exact-head checked normalized audit scan；
> P5-B 已将 offline validator 迁入 `Atelia.SessionJournal.Offline` companion，并让 CLI
> `validate --branch` 使用无 context 物化的 forward audit fold；公开 report 只保留最小
> phase/setup/counts 与版本化 semantic history/system-prompt hashes，不输出完整 execution
> state、明文 prompt 或 tool execution/correlation 细节。P5-C 已让 legacy importer
> 独立计算 source semantic commitment，并用 Offline report + exact branch/ref/head、
> lineage/mapping 与 governing setup 双重验证 staging/published target；production
> legacy full projection/replay caller 已归零。P5-D 已删除 core public full projection/replay
> surface、production reducer、相关 diagnostics，并把 tests 迁到 tail recovery、exact setup、
> bounded planning window 与 Offline validation。

目标：

- 删除 online/runtime 对 `SessionReducer`、`Project()`、`ReplayHistory()` 的依赖和叙事；
- 将仍有价值的 full audit/recovery/import checks 迁入明确 companion boundary；
- 决定 differential oracle 是保留在 tests、缩成 pure operational fixture，还是删除。

当前分包：

1. **P5-A/B 已完成**：建立正常依赖方向的 checked scan 与 Offline forward audit fold；
2. **P5-C 已完成**：替换 importer 与 CLI tests 的 full projection 便捷 caller，并以
   source semantic commitment 保持 import 内容真实性；
3. **P5-D 已完成**：迁移剩余 test callers，删除 public full projection/replay surface 与
   无生产 caller 的 reducer；durable head matrix 以明确 phase/head 预期验证 tail resolver，
   并与 bounded `FoldSuffix` 合法性对照，不再复制第二套正向 reducer oracle。

验收：

- Send/Resume/Prepared reopen/tail planning 不链接 full reducer；
- CLI import/validate 的真实性检查有明确替代，不因化简静默减弱；
- maintainer input 只走 addressed bounded planning window；
- public API 不再让调用方误把 full projection 当作 normal recovery。

### P6：Maintenance orchestration 专项化简（已完成）

已确认 contract：

- 多个长耗时 MemoryMaintainer 中任一 role 成功并 durable settlement 后，进程崩溃/reopen 不得重跑
  该 role 的昂贵 LLM 调用；
- resume 只补 missing/failed roles，并验证既有 settlement 仍属于 exact epoch、role target 与
  provisioning；
- required roles 闭合后必须 durable finalization，冻结窄 included roles、omitted optional roles
  与 expected set identity；expected previous set 从 immutable transaction 取得；
- finalization 后的 reopen 不再运行 maintainer；只允许完成或验证同一个 atomic ArtifactSet
  publication；
- 旧 usable set 在新 set 原子发布前继续可用，partial settlements 永不直接暴露给 online selection。

实施结论：

- **KEEP** durable per-role settlement、finalization-before-publication、missing/failed-only resume、
  optional omission、old latest until atomic publish，以及 finalized reopen no-producer；
- finalization 切到独立 v2，只冻结 transaction id、anchor setups、窄 included roles、omitted
  optional roles 与 expected set id；job/epoch/policy/expected previous 统一由 immutable
  transaction 提供；
- **KEEP** transaction/job/producer/policy/topology/candidate/attempt identities；它们仍参与 exact
  retry、artifact/set identity 与 provisioning 验证；
- **MERGE later** JobFingerprint/TransactionId 与 CandidateId/AttemptId 的跨库 generation
  合并。当前 Candidate/Attempt 是固定 job provisioning，调用方必须复用它们才能恢复同一
  transaction；本轮不修改 transaction/artifact/set schemas 或 ids。

本包不得反向修改 raw SessionJournal wire，也不得把 concrete maintainer policy 放回 raw core。

## 7. 推荐顺序

```text
P0 contract inventory
  -> P1 branch-scoped raw Engine [done]
  -> P2 branch-aware DerivedMemory [done]
  -> P3 durable NthPrevious [done]
  -> P4 budget/estimator cleanup [done]
  -> P5 full reducer de-production [done]
  -> P6 orchestration simplification [done]
```

P1/P2 已把 raw Engine、DerivedMemory 与 composition 绑定到同一 lifetime/durable `RefId`
authority。P3/P4 已连续完成，避免同时保留旧 runtime selection 与新 durable selection；P5
也已在 bounded planning caller 稳定后完成。P6 随后独立完成，并保留“成功 role 不重跑”的
crash-resume contract。

## 8. 共同非目标与验收闸门

非目标：

- 不恢复 raw `ArtifactSetCommitted` activation event；
- 不让 SessionJournal 引用 concrete DerivedMemory；
- 不用 full raw fallback 掩盖 missing candidate；
- 不把多个 maintainer 各自独立 split history；
- 不为旧实验 wire 增加 compatibility layer；
- 不在本轮设计 branch merge 或跨 branch artifact reuse。

每个实施包都必须保持：

- Prepared/Started exact reopen 不访问 DerivedMemory；
- selected anchor 前的 cold prefix 不产生 payload replay；
- exact Parent/head CAS 与 tool execution recovery 不退化；
- raw/derived mutation ownership 不混淆；
- 文档明确区分 current behavior、target behavior 和 historical implementation。

## 9. 当前阶段结论

用户反馈中的主要化简方向成立：

- P1/P2 已让 raw Engine、DerivedMemory 与 CLI 支持 existing active named branch，并把 durable
  lineage 与 rewind authority 绑定稳定 `RefId`；
- selection policy 应 durable，`NthPrevious` 足以表达 Agent 的近期/远期聚焦；
- `Latest` 可删除为 `n = 0`；
- automatic `Budgeted` candidate search 可以删除；它与 epoch planner 职责不同，但不符合 Agent
  明确 ordinal 的控制语义；
- raw/bootstrap selection budgets 可删；保留最小 non-selection hard limits，其中最终 exact
  request 使用 canonical JSON UTF-8 byte guard；
- shared epoch 与 atomic ArtifactSet publication 是必要能力；
- full reducer 对 online recovery 没有直接价值，应退出 production 主表面；
- orchestration/settlement/finalization 服务 maintenance crash resume；P6 已保留成功 role
  不重跑、durable finalization 与 atomic publication，并把 finalization 收窄为 v2；其余 identity
  合并延后到独立跨库 generation。

P1 raw Engine boundary、P2 branch-aware DerivedMemory、P3 durable exact ordinal 与 P4 strict
fresh bootstrap/canonical-byte guard、P5 full reducer 去生产化与 P6 bounded finalization
均已是现行 contract。

# DerivedMemory 可替换子系统与 Shared Epoch 实施方案

> **状态**：Approved / 待逐片实施
> **日期**：2026-07-27
> **最新代码对齐**：2026-07-28；已纳入
> `ChatSession.LegacyExportCli` / `SessionJournal.Cli` 拆分与
> `SessionJournal.Maintainers` companion assembly
> **目标基线**：CS-3D7 current trunk；后续以 breaking wire / derived rebuild 方式演进
> **上游决策**：
> [Tail Execution Recovery 化简调研 §4](tail-execution-recovery-simplification-study.md)、
> [MemoryMaintainer Provisioning / Planner 功能缺口](memory-maintainer-provisioning-planner-gap.md)、
> [SessionJournal 事件源会话与长期上下文架构路线图](event-sourced-session-architecture-roadmap.md)、
> [Legacy ChatSession Export / SessionJournal CLI 拆分](../ChatSession/legacy-export-and-sessionjournal-cli-split.md)
> **用途**：供后续 Coding Agent 按依赖顺序领取单一分片，完成设计复核、实现、测试、审阅、修复和提交。

## 0. 执行结论

DerivedMemory 重构不能简化成“定义几个接口，然后把 `DerivedRecapStore.cs` 移到新项目”。current
SessionJournal 仍在三个层面依赖 concrete derived shape：

1. request materializer 直接接收 `DerivedRecapArtifact`；
2. `CompletionRequestPrepared` 仍保存 exact raw `ActiveArtifactSet` reference 和 artifact ids；
3. reconstructor 仍读取 raw `ArtifactSetCommitted` 取得 coverage seed 并验证 latest activation。

若此时先物理搬文件，`SessionJournal.csproj` 就会被迫引用 concrete DerivedMemory 项目，依赖方向仍然
错误。

采用以下顺序：

```text
DM-0  Cross-assembly contracts
  ↓
DM-1  Neutral request materialization
  ↓
DM-2  Self-contained Prepared v4
  ↓
DM-3  DerivedMemory assembly + provider cutover
  ↓
DM-4  Remove raw ArtifactSetCommitted
  ↓
DM-5  Shared DerivedArtifactEpochPlanner
  ↓
DM-6  Epoch-bound independent maintainer runner
  ↓
DM-7  Parallel orchestration + ArtifactSet publication
  ↓
DM-8  Online lifecycle + budgeted set selection
```

核心排序理由：

- 先让 crash recovery 不依赖 derived subsystem，再移动 concrete implementation；
- 先建立正确程序集边界，再让 epoch planner 依赖它；
- 先持久化 shared epoch，再运行任何 maintainer；
- 先证明单 role 可按 epoch 独立重跑，再增加多 role 并行与自动 lifecycle；
- raw wire、derived store 和 online orchestration 不在一个分片里同时切换。

## 1. Current baseline 与待拆耦合

### 1.1 Current implementation facts

当前主要入口：

- `prototypes/SessionJournal/SessionRequestManifest.cs`
  - `SessionContextPlan` 保存 `ArtifactInputs + ActiveArtifactSet`；
  - artifact input 仍包含 `ArtifactId / ArtifactKind`。
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`
  - exact reopen 会读取 `ArtifactSetCommitted`；
  - coverage setup seed 来自 activation；
  - 会验证 referenced activation 位于 authoritative raw range。
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
  - `Materialize()` 直接接收 `ImmutableArray<DerivedRecapArtifact>`；
  - concrete artifact validation、raw suffix fold 和 request rendering 混在一起。
- `prototypes/SessionJournal/SessionJournalEngine.cs`
  - 直接 `DerivedRecapStore.Open(Path)`；
  - 负责 raw activation resolution、sidecar readiness、materialization、Prepared 与 dispatch。
- `prototypes/SessionJournal/DerivedRecapStore.cs`
  - concrete sidecar persistence、artifact DTO、indexes、identity 与 rebuild logic 位于 raw core
    程序集。
- `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs`
  - 每个 maintainer runner 独立解释 threshold 并计算 split；
  - SessionJournal 模式仍从 raw root + empty `MemoryPack` full replay。
- `prototypes/SessionJournal.Cli/MemoryMaintainerHistorySplitPolicy.cs`
  - 当前 synthetic sliding-prefix 切分 policy 已与旧 ChatSession 实现隔离；
  - 它是 maintainer 开发入口的临时 policy，不是未来 shared epoch authority。
- `prototypes/SessionJournal.Cli/MemoryMaintainerArtifactWriting.cs`
  - 当前 artifact writer 与 producer fingerprint 仍直接使用 core 内的 `DerivedRecapStore`。
- `prototypes/SessionJournal.Maintainers/`
  - 已作为只依赖 SessionJournal contracts 的 companion assembly；
  - 当前拥有 autobiography / world-understanding 的 profiles、embedded prompts 和 target paths；
  - generic `RewriteMemoryBlockMaintainer`、`MemoryMaintenanceOrchestrator` 与 mutable `MemoryPack`
    仍暂留在 `SessionJournal/SessionMemoryContracts.cs`。

### 1.2 已完成的 CLI / Maintainers 边界

原 `ChatSession.BacktestCli` 已拆分并退役，不再作为本计划的实施入口：

```text
legacy ChatSession repo
  -> ChatSession.LegacyExportCli export-json
  -> atelia.chat-session.legacy-upgrade-export.v1
  -> SessionJournal.Cli import-legacy-json
  -> current SessionJournal repo
```

- `ChatSession.LegacyExportCli` 只依赖旧 `ChatSession`，只负责 JSON/Markdown export；
- `SessionJournal.Cli` product project 不依赖 `ChatSession`，负责 import、validate、maintainer
  开发运行和当前 artifact/set 离线命令；
- `SessionJournal.Maintainers` 只依赖 `SessionJournal`，承载 application-specific maintainer
  policy；
- producer/consumer compatibility 由版本化 JSON schema 与两侧测试锁定，不建立 shared legacy
  contracts assembly；
- `SessionJournal.Cli.Tests` 可以为 exchange-schema compatibility 同时引用两侧 product
  assembly；这一 test-only edge 不得进入 `SessionJournal.Cli`；
- `ChatSession.LegacyExportCli` 不参与未来 DerivedMemory composition、epoch planning 或 online
  lifecycle。

这次拆分已经完成了 composition root 和 concrete policy 的初步分层，但没有提前完成
DerivedMemory：store、planner、runner substrate 和 raw/derived 解耦仍是 DM-0～DM-8 的工作。

### 1.3 已确定的长期边界

- raw SessionJournal event sequence 是 execution/history correctness source；
- derived config、epochs、artifacts、sets 和 indexes 不进入 raw Parent sequence；
- raw 不引用 derived plan/artifact/set id；
- derived records 可以单向引用 raw addresses/ranges/setup refs；
- Prepared 将实际进入 provider request 的 derived text 提升为 exact execution fact；
- Prepared/Started exact reopen 不打开 DerivedMemory、不重新运行 planner；
- `SessionExecutionTailResolver` 始终 raw-only；
- derived subsystem 缺失只阻止未 Prepared 的 request planning，不污染 raw recovery；
- 同一 coherence group 的 ArtifactSet members 必须共享 exact epoch，而不只是偶然具有 common anchor。

## 2. 目标程序集与 authority 图

```text
Agent Host / SessionJournal.Cli
├── Atelia.SessionJournal
│   ├── raw event codec / reducer / tail resolver
│   ├── candidate contracts
│   ├── raw-facing candidate validation
│   ├── dependency-closed suffix materialization
│   ├── Prepared commit / exact reopen
│   └── provider/tool execution driver
│
├── Atelia.SessionJournal.DerivedMemory            # 暂名
│   ├── DerivedMemoryRepository
│   ├── planner config snapshots / epoch ledger
│   ├── artifact / ArtifactSet stores
│   ├── indexes / usage records
│   ├── MemoryMaintainer provisioning / runner
│   └── coherent context candidate provider
│        └── 单向引用 Atelia.SessionJournal contracts
│
└── Atelia.SessionJournal.Maintainers
    └── concrete maintainer implementations、profiles、prompts、targets 与应用 role policy

ChatSession.LegacyExportCli
└── Atelia.ChatSession                           # frozen migration island
```

约束：

- `SessionJournal.csproj` 不引用 `SessionJournal.DerivedMemory.csproj`；
- composition root 负责构造 session-bound provider 并注入；
- 第一阶段 contracts 继续定义在 SessionJournal，不提前增加
  `SessionJournal.Abstractions` 第三程序集；
- DerivedMemory 内部可以有 repository/store interfaces，但不能把具体存储 API 作为 raw core contract；
- `SessionJournal.Maintainers` 是只依赖 SessionJournal contracts 的 concrete
  MemoryMaintainer companion assembly；SessionJournal raw core 不反向依赖它；
- `SessionJournal.Cli` 是当前离线 composition root；长期 Agent Host 也遵循相同依赖方向；
- `ChatSession.LegacyExportCli` 不得引用 SessionJournal、Maintainers 或 DerivedMemory；
- `SessionJournal` 不依赖 `Agent.Core`。

## 3. 跨边界 contract shape

本计划冻结语义，不锁死最终 C# record 名称。推荐能力合同：

```csharp
public interface ICoherentContextCandidateSource {
    ValueTask<SessionContextCandidate?> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken);
}
```

DM-0 已实现单候选 `Latest` contract。将来如果 branch-aware `NthPrevious(n)` 确实需要 bounded
enumeration，才增加有序 candidate batch；不得因此把 derived repository、artifact ids 或 concrete
EventJournal ownership 暴露给 core。

### 3.1 Selection request

至少表达：

- exact completion boundary；
- selection mode（DM-0/DM-3 第一版只需 Latest）；
- 可选 raw suffix token budget hint；
- coherence group / application policy token；

单候选 `SelectAsync` 不提前携带 `maxCandidates`；它在没有 batch 语义时只是伪配置。诊断与 cost
单位也不进入 DM-0 contract，避免让无消费、无上界的字段成为跨程序集负担。

### 3.2 Candidate

至少表达：

```text
SessionContextCandidate {
  rawStartExclusive,
  anchorSetups { runtimeConfig, systemPrompt },  // address + body schema + payload sha256
  contributions: [{
    carrier,
    blockKey,
    exactText,
    contentCodecId,
    contentSha256,
    sourceRawHead
  }]
}
```

`contentSha256` 的 domain/codec 固定为
`atelia.session-journal.context-contribution-text-sha256.v1`：对 UTF-8 exact text 以前缀化 domain
分隔后计算 SHA-256。它不复用含 artifact identity 的旧 hash，故可直接作为 DM-1 neutral renderer 的
输入验证。`sourceRawHead` 是每个 contribution 的 raw provenance；core 要求其落在
`rawStartExclusive..completionBoundary` 的真实 Parent 区间内。

不得成为 cross-boundary contract 的字段：

- artifact/set 文件路径；
- concrete artifact/set DTO；
- latest/default index shape；
- profile/maintainer implementation；
- store schema；
- 要写入 raw Prepared 的 artifact/set/epoch id；
- ready-made `CompletionRequest`。

### 3.3 Correctness responsibility

DerivedMemory 负责：

- required/optional roles 与 coherence group；
- epoch/member/set compatibility；
- producer/profile/model fingerprint；
- derived lineage、store integrity、index rebuild；
- derived candidate discovery/ordering。

SessionJournal 仍负责：

- selected anchor 是 current completion boundary 的严格祖先；
- raw source/setup refs 位于真实 Parent lineage；
- replay-safe/dependency-safe boundary；
- contribution target 合法、唯一且有界；
- 对合法 unordered contributions 以 `carrier rank + blockKey` 作 canonical normalization；
- dependency-closed suffix fold；
- current setup/tool/runtime/target validation；
- canonical request 与 exact-head Prepared CAS。

core 必须重新验证 provider 返回的 raw-facing assertions，不能把 raw correctness 外包给可替换实现。

## 4. 分片依赖图

```text
DM-0 Contracts
  └── DM-1 Neutral Materializer
        └── DM-2 Prepared v4
              └── DM-3 DerivedMemory Assembly + Provider
                    └── DM-4 Raw Activation Removal
                          └── DM-5 Shared Epoch Planner
                                └── DM-6 Epoch-bound Runner
                                      └── DM-7 Parallel Publication
                                            └── DM-8 Online Lifecycle
```

DM-2 与 DM-3 可以并行做只读研究/原型，但推荐按图中顺序合入。DM-3 与 DM-4 应连续推进，不允许
raw/derived 双 writer 变成长期兼容层。

## 5. DM-0：Cross-assembly Contracts

### 目标

冻结 SessionJournal 与可替换 DerivedMemory 之间的最小语义接口，用 fake provider 证明 raw core 不需要
concrete store 类型。

### 实施状态（2026-07-28）

已完成并以独立 commit 落地：

- `SessionContextCandidateContracts.cs` 提供 public single-candidate source/request/setup/contribution
  contracts；
- `SessionContextCandidateValidator.cs` 以 caller 已 authoritative resolve 的
  `SessionGoverningSetup` 为 seed，验证它的 `Head == rawStartExclusive`，而不复制一条冷历史 setup
  resolver；
- validator 以 header-only 回溯验证 strict anchor 与 source heads，按需读取 anchor setup payload
  验证 kind/schema/hash，并 canonicalize contributions；
- fake source fixtures 覆盖 unordered legal input 与所有 raw-facing negative cases；
- `SessionJournal.csproj` architecture guard 锁定其不引用 Maintainers、DerivedMemory 或 Agent.Core。

### 主要落点

- 新增 `prototypes/SessionJournal/SessionContextCandidateContracts.cs`（暂名）；
- 新增 fake-provider contract tests；
- 决定 `SelectAsync` 与 bounded ordered candidates 的最小第一版形状；
- 决定 provider 如何取得 branch-aware 只读信息：
  - 优先返回 provenance 后由 core authoritative validation；
  - 若确需 lineage capability，只暴露窄 read-only abstraction，不暴露 Engine ownership。
- 增加 project-reference architecture test 或等价静态检查。

### 非目标

- 不移动文件；
- 不改 Prepared wire；
- 不改 online writer；
- 不实现 DerivedArtifactSetStore；
- 不调用真实 LLM。

### 验收

- fake provider 可表达一个合法 candidate；
- divergent/equal anchor、setup ref mismatch、source head 越界、duplicate target、invalid carrier、
  hash/text/size fail-fast；
- provider 缺失不影响 raw `Open`、`Project()`、`ReplayHistory()`、tail recovery；
- `SessionExecutionTailResolver` API/reads 不变化；
- current production request path 尚未切换。

## 6. DM-1：Neutral Request Materialization

### 目标

让 SessionJournal request materializer 只消费 normalized candidate，不再认识
`DerivedRecapArtifact` / `DerivedRecapStore`。

### 主要落点

- 拆分 `SessionTailContextProjection`：
  - concrete artifact -> contribution 的转换留在临时 adapter；
  - raw ancestry/setup/suffix fold/rendering 留在 core；
- `SessionTailContextProjection.Materialize()` 改为接收 normalized candidate；
- `RequestContextMaterialization` 不再保存 concrete artifact objects；
- current activation/store path 通过同程序集临时 adapter 产生 candidate；
- 明确 adapter 只服务 DM-1～DM-3 cutover，不扩展成第二套长期 policy。

### 非目标

- 不改 raw kind 12；
- 不改 Prepared schema；
- 不移动 store；
- 不加入 latest/Nth/budget selection。

### 验收

- current fixtures 的 provider-facing request/canonical bytes 不变；
- full-vs-tail execution oracle 不变；
- 1-vs-10001 header/payload/decoded-byte diagnostics 不回归；
- materializer 源码不再引用 `DerivedRecapArtifact`；
- concrete adapter 有 focused equivalence tests。

## 7. DM-2：Self-contained Prepared v4

### 目标

Prepared exact reopen 完全脱离 raw ArtifactSet activation 与 derived ids，为程序集切换建立
crash-recovery 安全边界。

### 目标 manifest

```text
CompletionRequestPrepared v4 {
  origin,
  executionCheckpoint,
  plan {
    rawStartExclusive,
    rawRangeSha256,
    rawStartSetups,
    exactContextInputs[]
  },
  currentSetups,
  requestParameters,
  tool/runtime identity,
  target,
  recipe/codec identity,
  canonical commitment
}
```

`exactContextInputs` 保存 store-neutral contribution snapshots/hashes，不保存：

- `ActiveArtifactSet`；
- artifact/set/epoch id；
- derived path/index；
- 可重新规划的 selection policy。

### 主要落点

- `SessionRequestManifest.cs`；
- `SessionRequestManifestCodec.cs`；
- `SessionPreparedRequestReconstructor.cs`；
- `SessionJournalEngine.BuildRequestManifest()`；
- offline validator、goldens、mutation tests；
- governing setup seed 改为 Prepared 自带 `RawStartSetups`。

### Migration

- 明确 per-kind breaking wire upgrade；
- 不增加 v3 compatibility decoder、缺省字段推断或 root replay fallback；
- 旧实验 SessionJournal 通过 legacy export 重新 import，随后重建 derived memory；
- 盘点非幂等外部副作用；不声称可安全重发未知历史 attempt。

### 验收

- Prepared 后删除整个 derived repository，exact request/canonical bytes 不变；
- Prepared/Started resume 不调用 candidate provider；
- reconstructor 不读取 `ArtifactSetCommitted`；
- manifest/raw corruption 逐字段 fail-fast；
- online `FullProjectionInvocationCount == 0`；
- current failpoint/attempt/tool sequence 行为不变。

## 8. DM-3：DerivedMemory Assembly 与 Provider Cutover

### 目标

建立 concrete 可替换子系统，使未 Prepared 的 context planning 只通过注入 provider；停止新增 raw
ArtifactSet activation。

### 新项目

```text
prototypes/SessionJournal.DerivedMemory/
tests/SessionJournal.DerivedMemory.Tests/
```

第一阶段迁入/新增：

- `DerivedRecapStore` 与 artifact schema/DTO/identity/indexes；
- `DerivedMemoryRepository`；
- `DerivedArtifactSetStore`；
- derived usage/audit index；
- current artifact -> normalized contribution renderer；
- `ICoherentContextCandidateSource` concrete implementation；
- derived-only exact set publication；
- repository-bound atomic write/lock/path-hardening。

DM-3 明确保留现有 producer surfaces，不与 store/provider cutover 同时搬迁：

- `RewriteMemoryBlockMaintainer`、`MemoryMaintenanceOrchestrator` 与 mutable `MemoryPack`
  暂留 SessionJournal；
- application profiles、prompts 与 target paths 留在 `SessionJournal.Maintainers`；
- current runner/composition 留在 `SessionJournal.Cli`。

generic producer substrate 的最终归属在 DM-6/DM-7 复核；application-specific policy 不迁入
DerivedMemory。这样避免物理搬家与 Prepared wire cut 同时扩大。

### Composition

- `SessionJournal.Cli` 增加对 DerivedMemory 的引用，并继续组合 SessionJournal、
  SessionJournal.Maintainers 与 Completion；
- 长期 Agent Host 同样只在 composition root 同时引用这些程序集；
- `SessionJournal.Maintainers` 继续只依赖 SessionJournal contracts，不因 store/provider
  cutover 反向依赖 DerivedMemory；
- `ChatSession.LegacyExportCli` 的引用图和命令保持不变；
- provider instance 与一个 SessionJournal repo/session 绑定；
- Engine/request coordinator 只保存 interface；
- `SessionJournal.Open(path)` 无 provider 仍支持 raw-only surfaces；
- online planning 入口明确要求 provider。

### CLI transition

- 在 `SessionJournal.Cli` 增加 derived-only set publish/inventory 能力；
- current `checkpoint-artifact-set` 不再作为长期 writer；
- 新 writer 不追加 raw kind 12；
- raw kind 12 reader 可只为 DM-3 这一过渡分片暂存，DM-4 立即删除；
- 不保留双 writer 或 silent import。

### 验收

- `SessionJournal.csproj` 不引用 DerivedMemory；
- DerivedMemory 单向引用 SessionJournal；
- SessionJournal.Tests 用 fake provider 覆盖 core；
- concrete persistence/provider tests 位于 DerivedMemory.Tests；
- 手工 exact autobiography + world-understanding artifacts 可发布 derived set；
- provider 返回 candidate 后可完成 Observation/ToolResult completion；
- provider 缺失时 pre-Prepared 明确 not-ready；
- Prepared 后 provider/sidecar 删除仍 exact reopen。

## 9. DM-4：删除 raw ArtifactSetCommitted

### 目标

移除 raw/derived 反向引用和 current activation 兼容表面，完成候选 C 的 semantic cutover。

### 删除或收缩

- `SessionEventKind.ArtifactSetCommitted`；
- `ArtifactSetCommittedBody` / `SessionActiveArtifactSet`；
- event codec/schema/goldens；
- reducer/tail-resolver idle-boundary 分支；
- `CommitArtifactSetAsync()`；
- `ResolveActiveArtifactSet()` / `EnsureActiveArtifactSetReadyAsync()`；
- latest-equals-selected/raw activation validators；
- offline readiness report 的 active raw set；
- `SessionJournal.Cli checkpoint-artifact-set` raw checkpoint command；
- activation setup checkpoint 逻辑。

### Governing setup hint

- near-head Prepared 继续提供 current setup hint；
- pre-first-Prepared derived candidate/epoch 可以提供可重建 hint；
- raw header parent walk 始终 authoritative fallback；
- hint 缺失只能增加 reads，不能改变答案；
- 不引入 dedicated config ref 或 full root projection cache。

### Migration

- 旧实验 repo 不保留 kind 12 compatibility decode；
- 从 legacy export 重新 import，或使用显式 offline raw migration；
- 删除/rebuild derived repository；
- 用 derived-only set publication 恢复 online readiness。

### 验收

- raw event inventory 不包含 derived activation；
- raw validator 不打开 DerivedMemory；
- 删除全部 derived files 后 raw audit/Prepared reopen 通过；
- tail resolver 与 full reducer execution state differential 通过；
- real legacy export 可导入全新 repo；
- no compatibility alias/decoder/silent full replay；
- code search 不再出现 raw `ArtifactSetCommitted` production surface。

## 10. DM-5：Shared DerivedArtifactEpochPlanner

### 目标

在任何 LLM maintainer 调用前，统一决定同一 coherence group 的 history coverage，解决 daily parallel
maintenance 与 independent prompt-tuning 的同步问题。

### Repository shape

概念布局：

```text
derived/memory/v1/
  planner-config.json
  planner-configs/<config-hash>.json
  epochs/<epoch-id>.json
  artifacts/...
  sets/...
  indexes/...
```

`planner-config.json` 是 current pointer；旧 immutable config snapshot 和 epoch plan append-only。
config 不进入 raw event sequence。

### Planner config

至少包含：

- token estimator id/version；
- `minimumRecentTokens`；
- `epochTriggerTokens`；
- dependency-safe/replay-safe boundary policy；
- coherence group / topology version；
- scheduling headroom；
- hard-limit/backpressure policy；
- planner schema/fingerprint。

### Epoch plan

```text
DerivedArtifactEpochPlan {
  epochId,
  coherenceGroup,
  topologyVersion,
  previousEpoch?,
  plannedAtRawHead,
  sourceStartExclusive,
  sourceEndInclusive,
  rawStartSetups,
  inputSet?,
  configFingerprint,
  measuredCost,
  planningDiagnostics
}
```

`epochId` 由 immutable identity 决定，不使用 wall-clock/run id。config 回答“未来怎样切”，ledger
回答“历史实际上怎样切了”。

### Trigger rule

```text
保留 newest dependency-closed suffix >= minimumRecentTokens
计算更旧的 eligible dependency-safe prefix

if Cost(eligible prefix) >= epochTriggerTokens:
    atomic compare-and-publish one epoch plan
```

boundary alignment 可以使 epoch 大小不同。同步要求是共享 exact epoch，不是 token 数相等。

### 主要落点

- DerivedMemory planner/config/epoch codecs；
- deterministic identity/hash；
- branch-aware previous epoch/index；
- atomic compare-and-publish；
- header-first addressed raw range planner；
- CLI `plan-derived-artifact-epochs` / `list-derived-artifact-epochs`（命名可在本片设计复核）；
- diagnostics：headers、payloads、decoded bytes、selected boundary/cost。

### 非目标

- 不运行 maintainer；
- 不调用 LLM；
- 不发布 ArtifactSet；
- 不自动接入 Send/Resume；
- 不实现 retrieval/ranking。

### 验收

- 同 raw/config 得到同 exact ranges/ids；
- config 更新只影响未来 epoch；
- restart 不重新规划已 durable epoch；
- concurrent planners 对同 previous epoch 原子收口；
- branch/rewind 拒绝 divergent epoch；
- 允许 boundary-aligned 非等大 epochs；
- 10k+ cold prefix 不增加增量 planning payload reads；
- 无 artifact genesis 行为有显式 policy/test。

## 11. DM-6：Epoch-bound Independent Maintainer Runner

### 目标

让单个 maintainer 只消费 exact epoch plan；prompt/model tuning 可以独立重跑，但不能重新切分 history
或推进 shared cursor。

### 主要变化

- `SessionJournal.Cli run-memory-maintainer` 的正式模式从 `--threshold-tokens` 驱动改为
  `--epoch <id>`；
- threshold/split 只存在于 DM-5 planner；
- runner 从 previous set/role artifact 恢复 old block；
- 只读取 epoch 的 exact raw range；
- artifact schema 增加：
  - epoch id/plan fingerprint；
  - exact source range/setup；
  - previous role artifact；
  - input set/artifacts；
  - producer/prompt/model fingerprint；
  - candidate/attempt identity；
- 同一 role/epoch 可以保存多个 alternative candidates；
- current root + empty MemoryPack full replay 路径退役。

### CLI intent

```text
run-memory-maintainer
  --input <repo>
  --epoch <epoch-id>
  --profile <role>
  [--system-prompt ... --prompt ... --connection ...]
```

拆分后 legacy backtest runner 已退役；当前 `SessionJournal.Cli` 的 threshold/full-replay
模式只是过渡性 maintainer 开发入口。DerivedMemory production mode 不再允许 role-local split。
`--profile` 解析与 concrete factory/descriptor 来自 `SessionJournal.Maintainers`，epoch lookup、
range materialization 与 artifact persistence 来自 DerivedMemory。

### 验收

- autobiography 与 world-understanding 在不同进程运行仍读取同一 exact epoch range；
- prompt override 只改变 producer candidate/fingerprint；
- prompt-tuning 不移动 epoch/default set cursor；
- previous role artifact lineage 正确；
- rerun 不覆盖旧 candidate；
- writer failure 不推进任何 derived set index；
- addressed provenance 的 end/anchor 来自实际 epoch fragment，不来自 trigger head。

## 12. DM-7：Parallel Orchestration 与 ArtifactSet Publication

### 目标

把 shared epoch、role provisioning、parallel producer、partial settlement 和 atomic set publication
闭合成日常 maintenance transaction。

### 主要能力

- typed role catalog / maintainer factory registry；
- coherence group required/optional role policy；
- one immutable epoch/input snapshot；
- 无依赖 maintainers `Task.WhenAll` 并行；
- producer success、artifact persistence、role settlement、set publication 分层状态；
- crash 后只重开未结算 roles；
- alternative candidate evaluation/selection；
- explicit no-change/identity result；
- required roles 全部结算后 atomic publish one DerivedArtifactSet；
- previous usable set 在失败期间保持可选。

### Set invariants

- exact epoch id/plan fingerprint；
- exact required role membership；
- role/target/artifact id 唯一；
- current raw lineage / common anchor / setup coherence；
- contribution hash 可重算；
- topology/policy/producer compatibility；
- previous set lineage 明确；
- partial set 永不进入 candidate index。

### Ownership migration

本片完成 producer substrate 的最终归属复核。已经形成的程序集边界应作为默认方向：

- `SessionJournal.Maintainers` 保留/接收 concrete maintainer implementations、
  profiles、embedded prompts、target paths、factories 与窄职责 producer helpers；
- DerivedMemory 接收 epoch-bound runner、multi-role orchestration、settlement、artifact/set
  publication 与 repository lifecycle；
- SessionJournal 只保留跨边界所需的 store-neutral request candidate、raw history/provenance
  以及最小 maintainer input/output contracts；
- `RewriteMemoryBlockMaintainer`、`MemoryRewriteProfile`、
  `MemoryMaintenanceOrchestrator`、mutable `MemoryPack` / drafts 与
  `RecentHistorySlice` 逐一按上述规则复核，不因当前同处
  `SessionMemoryContracts.cs` 就整体搬入同一目标程序集；
- `SessionJournal.Cli` 只保留参数解析、composition、路径安全、reporting 和显式运维命令。

目标是 raw SessionJournal 不拥有 concrete producer policy 或 derived orchestration。具体 rewrite
实现优先属于 Maintainers，跨 maintainer 的 durable orchestration 属于 DerivedMemory；若 DM-7
复核发现两者确需共享 neutral producer model，再引入更窄 substrate，而不是让 Maintainers 与
DerivedMemory 互相引用或保留两份实现。

### 验收

- 两个 required roles 确实并行读取同一 immutable snapshot；
- required role 失败时不发布半套 set；
- 成功的 partial artifacts 可留作 retry/tuning；
- restart 只执行缺失 role；
- no-change 仍显式结算 epoch；
- set publication 原子且并发安全；
- raw event count/head 不因 maintenance/publication 变化；
- 新增第三个测试 role 不修改 SessionJournal core。

## 13. DM-8：Online Lifecycle 与 Budgeted Selection

### 目标

把 planner/maintenance/provider 接入 Session 日常运行，并支持 recent-history 长短的可解释选择。

### Lifecycle

```text
safe online boundary
  -> inspect latest usable set + raw suffix
  -> DM-5 maybe plan next epoch
  -> DM-7 run/resume maintenance
  -> publish new set when complete
  -> context candidate provider selects usable set
  -> SessionJournal validates/materializes
  -> Prepared exact commit
```

旧 set 在新 set 完成前继续可选。threshold 必须保留 maintenance headroom；若 suffix 到 hard limit 而
required set 未完成，进入 explicit backpressure/not-ready，不允许 silent full raw fallback。

### Selection progression

1. `Latest`：最新 current-lineage usable set；
2. `NthPrevious(n)`：沿同一 set lineage 的第 n 个 usable set；
3. exact candidate comparison：
   - common anchor；
   - raw suffix token/byte cost；
   - required topology；
   - freshness/staleness；
   - total request budget；
4. 多 coherence groups / retrieval candidates 的组合。

ordinal 不等价于 cost；第一版 `NthPrevious` 是可解释控制面，budgeted comparison 是长期主路径。

### Restart phases

- raw tail recovery：不访问 DerivedMemory；
- Prepared/Started：只从 raw manifest exact reopen；
- unprepared `AwaitingAgentAction`：调用 candidate provider；
- idle `Open`：raw-only 可成功；
- `SendAsync`：在 raw mutation/provider call 前完成 memory readiness preflight；append Observation 后按
  exact boundary 重新 materialize；
- provider 缺失/损坏：derived-not-ready，不是 raw corruption。

### 验收

- Observation 与多轮 ToolResult continuation 不调用 `Project()`；
- latest/Nth/budget selection 可解释且 branch-aware；
- 选择更旧 set 时 reads 只随 selected anchor 到 boundary 增长；
- selected anchor 前增加 10k+ cold prefix 不增加 payload reads；
- Prepared 后删除 DerivedMemory 仍 exact reopen；
- hard-limit/backpressure 行为有 failpoint/restart tests；
- 使用真实 `dsv4p` 完成 autobiography + world-understanding shared epoch、parallel production、set
  publication、online completion 与 restart；
- 源码/报告不泄露 connection secrets 或完整敏感请求内容。

## 14. Migration 与 schema 边界

| 变化 | 权威迁移方式 | 明确禁止 |
| --- | --- | --- |
| Prepared v3 -> v4 | `ChatSession.LegacyExportCli export-json` 后由 `SessionJournal.Cli import-legacy-json` 导入新 repo；盘点非幂等外部副作用 | compatibility decoder、缺省字段、自证 fallback |
| 删除 raw kind 12 | 新 repo/import 或显式 offline raw migration | 保留 retired kind writer、把 derived id 改名后继续写 raw |
| DerivedRecapStore -> DerivedMemory schema | 删除/rebuild derived repository，或显式 derived migrator | 让 raw validity 依赖旧 sidecar |
| artifact 增加 epoch identity | 按 raw + config 重跑 planner/maintainers | 从 common anchor 猜 epoch id |
| planner config 更新 | 新 immutable config snapshot，只影响未来 epoch | 用 current config 重解释历史 epoch |
| CLI threshold -> epoch runner | 删除过渡 threshold/full-replay mode；`SessionJournal.Cli` 显式 `--epoch` | 把旧 Backtest runner 复活为 compatibility mode、每个 role 独立 threshold 后偶然拼 set |

实验项目尚未发布，不为 retired wire 保留长期兼容面。每次 breaking cut 必须更新：

- codec schema/version；
- goldens/mutation fixtures；
- offline validator；
- import/rebuild 文档；
- real acceptance evidence；
- current trunk/roadmap 状态。

## 15. 测试与观测策略

### 15.1 Test ownership

- `tests/SessionJournal.Tests`
  - contracts/fake provider；
  - raw candidate validation；
  - Prepared/reconstructor；
  - tail resolver/full reducer；
  - Engine/attempt/tool failpoints。
- `tests/SessionJournal.DerivedMemory.Tests`
  - config/epoch/artifact/set codecs；
  - repository atomicity/index rebuild；
  - candidate provider；
  - epoch planner；
  - maintainer settlement/publication。
- `tests/SessionJournal.Maintainers.Tests`
  - stable maintainer/profile/target identity；
  - embedded prompt/profile loading；
  - concrete producer behavior 与 prompt override；
  - 不承担 epoch/store/selection tests。
- `tests/SessionJournal.Cli.Tests`
  - composition；
  - CLI parsing/path safety/atomic reports；
  - plan/run/publish workflow；
  - legacy import/rebuild E2E 与 producer/consumer exchange-schema compatibility。
- `tests/ChatSession.LegacyExportCli.Tests`
  - legacy export command、schema、只读/path-safety 与 atomic publish 行为；
  - 不新增 DerivedMemory workflow tests。

### 15.2 Metrics

不以 wall-clock 作为唯一闸门。至少记录：

- header visits；
- payload reads；
- logical/decoded bytes；
- chronological full-chain reads；
- `FullProjectionInvocationCount`；
- peak live payload/materialized context；
- epoch planned range/cost；
- maintainer/provider invocation count；
- artifacts/set publications。

### 15.3 Standard verification

在受限 .NET host 下优先：

```bash
dotnet test <project> -m:1 -nr:false --no-restore
```

每个分片先跑 focused tests，再跑：

- `SessionJournal.Tests`；
- `SessionJournal.DerivedMemory.Tests`（DM-3 起）；
- `SessionJournal.Maintainers.Tests`（涉及 concrete producer/profile 起）；
- `SessionJournal.Cli.Tests`（涉及 CLI 起）；
- `ChatSession.LegacyExportCli.Tests`（仅当 legacy exchange schema/export surface 被触及）；
- relevant Completion/EventJournal tests；
- zero-warning build。

真实 LLM 只在需要 producer/online acceptance 的 DM-6～DM-8 使用；DM-0～DM-5 应完全可用 deterministic
fakes 完成验收。

## 16. Review 与提交纪律

每个 DM 分片采用同一闭环：

1. package-local 重新核实现状与 contracts；
2. 必要时补窄设计说明；
3. 实现；
4. focused tests；
5. 独立 review，重点查 authority/branch/crash/read-boundary；
6. 顺手修复 review 疏漏；
7. full relevant verification；
8. 更新本文状态、current trunk docs 和 CLI README；
9. 一个语义清晰的 commit。

不得把多个尚未稳定的变化打成一次整体提交，例如：

- Prepared wire + raw kind removal + epoch planner；
- physical assembly move + maintainer prompt 重写；
- epoch planning + retrieval/ranking；
- provider cutover + tool runtime protocol。

DM-3/DM-4 应连续调度，但仍保持独立 review/commit，以便确认新 provider path 先可用，再删除旧 raw
surface。

## 17. 下一步：领取 DM-0

下一次 Coding Agent 应只实施 **DM-0：Cross-assembly Contracts**。开始前重点阅读：

- `prototypes/SessionJournal/SessionRequestManifest.cs`
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `prototypes/SessionJournal/SessionExecutionTailResolver.cs`
- `prototypes/SessionJournal/DerivedRecapStore.cs`
- `prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj`
- `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs`
- `prototypes/SessionJournal.Maintainers/SessionJournal.Maintainers.csproj`
- `prototypes/SessionJournal.Maintainers/README.md`
- `tests/SessionJournal.Tests/`
- 本文 §2、§3、§5

DM-0 完成标志不是“新建了 interface 文件”，而是：

- 合同没有泄漏 concrete derived storage；
- fake candidate 足以表达合法/非法 raw-facing cases；
- execution resolver 和 Prepared reopen 仍不依赖 provider；
- 下一片 DM-1 可以在不重新设计跨程序集边界的前提下，把 materializer 切到 normalized candidate。

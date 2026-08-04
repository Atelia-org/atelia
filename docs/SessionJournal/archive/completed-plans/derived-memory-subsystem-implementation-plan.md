# DerivedMemory 可替换子系统与 Shared Epoch 实施方案

> **状态**：Final Audited / Implemented / Committed
> **主实施提交**：`db2c8df6 feat(session-journal): integrate online derived memory lifecycle`
> **Closeout addendum**：restart/backpressure oracle、最终 real gate 与文档归档已独立审阅通过并提交
> **日期**：2026-07-27
> **最新代码对齐**：2026-07-28；已纳入
> `ChatSession.LegacyExportCli` / `SessionJournal.Cli` 拆分与
> `SessionJournal.Maintainers` companion assembly
> **目标基线**：current Prepared v5 + DM-0～DM-8；后续以 breaking wire / derived rebuild 方式演进
> **上游决策**：
> [Tail Execution Recovery 化简调研](../studies/tail-execution-recovery-simplification-study.md)、
> [MemoryMaintainer Provisioning / Planner 历史缺口](memory-maintainer-provisioning-planner-gap.md)、
> [SessionJournal 事件源会话与长期上下文架构路线图](../studies/event-sourced-session-architecture-roadmap.md)、
> [Legacy ChatSession Export / SessionJournal CLI 拆分](../../../ChatSession/legacy-export-and-sessionjournal-cli-split.md)
> **用途**：记录已实施的 authority、contracts、迁移、验收与后续非目标；不再作为待领取分片计划。

## 0. 执行结论

本计划最初不能简化成“定义几个接口，然后把旧 store 移到新项目”；必须先解除 raw
SessionJournal 对 concrete derived shape 的依赖。DM-0～DM-4 已完成这项切割，当前状态为：

1. request materializer 只消费 normalized `SessionContextCandidate`，不认识 artifact/store；
2. `CompletionRequestPrepared` v5 已保存 self-contained exact context snapshots 与两端 setup refs；
3. reconstructor 以 `RawStartSetups` 取得 suffix fold seed；raw event inventory 不再包含
   derived-set definition/activation，Prepared reopen 与 raw audit 均不读取 DerivedMemory。

因此 `SessionJournal.csproj` 不引用 concrete DerivedMemory；DerivedMemory 单向引用 core
contracts，composition root 同时组合 provider/maintainer/Completion。

采用以下顺序：

```text
DM-0  Cross-assembly contracts  ✓
  ↓
DM-1  Neutral request materialization  ✓
  ↓
DM-2  Self-contained Prepared v4（后由 DM-8 升级 v5） ✓
  ↓
DM-3  DerivedMemory assembly + provider cutover  ✓
  ↓
DM-4  Remove raw derived-set activation  ✓
  ↓
DM-5  Shared DerivedArtifactEpochPlanner  ✓
  ↓
DM-6  Epoch-bound independent maintainer runner  ✓
  ↓
DM-7  Parallel orchestration + ArtifactSet publication  ✓
  ↓
DM-8  Online lifecycle + budgeted set selection  ✓
```

核心排序理由：

- 先让 crash recovery 不依赖 derived subsystem，再移动 concrete implementation；
- 先建立正确程序集边界，再让 epoch planner 依赖它；
- 先持久化 shared epoch，再运行任何 maintainer；
- 先证明单 role 可按 epoch 独立重跑，再增加多 role 并行与自动 lifecycle；
- raw wire、derived store 和 online orchestration 不在一个分片里同时切换。

## 1. Current baseline 与待拆耦合

### 1.1 Current implementation facts

DM-4 后的当前主要入口：

- `prototypes/SessionJournal/SessionRequestManifest.cs`
  - `SessionContextPlan` 保存 `RawStartSetups + ExactContextInputs`；current Prepared v5
    允许严格的零输入 bootstrap snapshot；
  - Prepared exact input 只保存 rendered snapshot/hash，不包含 artifact identity。
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`
  - exact reopen 只读取 Prepared 所钉死的 raw range、两端 setup refs 与 exact context inputs；
  - fold seed 来自 `plan.rawStartSetups`；
  - 不解析 derived artifact/set identity；raw range 只包含现行 SessionJournal events。
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
  - `Materialize()` 只接收 normalized candidate；
  - raw suffix fold 和 request rendering 留在 core，不引用 concrete derived types。
- `prototypes/SessionJournal/SessionJournalEngine.cs`
  - pre-Prepared planning 只消费注入的 `ICoherentContextCandidateSource`；
  - 不打开 DerivedMemory，也不再提供 raw ArtifactSet writer；Prepared 与 dispatch 仍属 core。
- `prototypes/SessionJournal.DerivedMemory/`
  - 拥有 `DerivedMemoryRepository`、`DerivedMemoryArtifactStore`、
    `DerivedArtifactSetStore`、`DerivedArtifactEpochPlanner`、
    `DerivedMemoryMaintainerRunner` 与 concrete candidate provider；
  - 只引用 SessionJournal；artifact/set/epoch/pointer 全部位于
    `derived/memory/v1/`。
- `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs`
  - `--epoch` 是唯一正式模式；旧 threshold/split/full replay 已删除；
  - CLI 只组合 descriptor、Completion client、runner、call log 与 atomic report。
- `prototypes/SessionJournal.Maintainers/`
  - 已作为依赖 SessionJournal contracts 与 Completion.Abstractions 的 companion assembly；
  - 当前拥有 autobiography / world-understanding 的 stable role/profile descriptor、
    embedded prompts、target paths 与 concrete factory；
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
- `SessionJournal.Cli` product project 不依赖 `ChatSession`，负责 import、raw/derived validate、
  maintainer 开发运行，并已在 DM-3C 接收 derived set publish/list/rebuild composition command；
- `SessionJournal.Maintainers` 只依赖 `SessionJournal`，承载 application-specific maintainer
  policy；
- producer/consumer compatibility 由版本化 JSON schema 与两侧测试锁定，不建立 shared legacy
  contracts assembly；
- `SessionJournal.Cli.Tests` 可以为 exchange-schema compatibility 同时引用两侧 product
  assembly；这一 test-only edge 不得进入 `SessionJournal.Cli`；
- `ChatSession.LegacyExportCli` 不参与 current 或后续 DerivedMemory composition、epoch planning 或 online
  lifecycle。

这次拆分先完成了 composition root 和 concrete policy 的初步分层；随后 DM-0～DM-8 已完成
DerivedMemory store、planner、runner substrate 和 raw/derived 解耦。

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

DM-8 已把最初的单候选合同升级为 bounded two-phase contract。discovery 只返回
content-free descriptor；core 完成 raw authority/cost 筛选后才逐个 materialize exact
derived text，从而避免枚举候选时同时持有多个完整 ArtifactSet：

```csharp
public interface ICoherentContextCandidateSource {
    ValueTask<SessionContextCandidateDiscovery> DiscoverAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken);
}
```

provider 的 `Handle` 是 opaque exact identity；core 不解析 artifact/set id。`EmptyLineage`
只表示经 strict store lookup / latest rebuild 后确认的真实空 lineage，missing/stale pointer
不能伪装为 bootstrap。

### 3.1 Selection request

至少表达：

- exact completion boundary；
- selection mode：`Latest`、`NthPrevious`、`Budgeted`；
- 可选 raw suffix 与 total canonical request token budget；
- coherence group / application policy token；
- bounded candidate count 与 zero-based nth ordinal。

`Latest` discovery 只返回 1 个 descriptor；`NthPrevious(n)` 只返回 `n + 1`；
`Budgeted` 最多返回显式 `MaxCandidateCount`（current default 32、hard maximum 64）。
ordinal 只表示 lineage 次序，绝不被当作 suffix cost。

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

discovery descriptor 只包含 `Handle + Ordinal + RawStartExclusive + AnchorSetups`；
`contributions` 只在 materialization 阶段出现。

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
- provider supplied `IReadOnlyList` 在 validator 入口以单次、最多 128 项的 immutable snapshot 冻结；
  Count 不作为 trust input，后续 lineage、hash、target 与排序验证只消费同一 snapshot，避免 lazy/mutable
  provider 在多次枚举之间注入未验证 contribution；
- fake source fixtures 覆盖 unordered legal input 与所有 raw-facing negative cases；
- `SessionJournal.csproj` architecture guard 锁定其不引用 Maintainers、DerivedMemory 或 Agent.Core。

### 主要落点

- 新增 `prototypes/SessionJournal/SessionContextCandidateContracts.cs`（暂名）；
- 新增 fake-provider contract tests；
- 历史第一版采用 `SelectAsync`；DM-8 已按实际 budgeted selection 需求升级为 bounded
  `DiscoverAsync + MaterializeAsync`；
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

### 实施状态（2026-07-28；历史落点）

已完成：

- DM-1 实施当时由 `ValidatedSessionContextCandidate` 冻结 completion boundary、canonical contributions、
  anchor setup、一次 lineage walk 的 chronological suffix addresses（exclusive anchor / inclusive
  boundary）及 header diagnostics；materializer 直接消费它，不重复 Parent walk；
- `SessionTailContextProjection` 不再引用 DerivedRecap 类型；raw block text 由 core 的
  `CreateOneHotSnapshot()` 通过既有 singleton `MemoryPack.Render()` recipe 变为 request snapshot；
- `LegacyArtifactContextCandidateAdapter` 是唯一 kind-12/store → candidate bridge，明确标注 DM-3
  删除；它继续验证 Produced、artifact/source coverage、governing setup、member identity、target block
  与 legacy snapshot hash，并保留 per-member not-ready artifact id；
- `LegacyArtifactContextSnapshotFactory` 则只服务 legacy kind-12 commit 与 offline validation；它不属于
  planning adapter，故在 DM-3 后继续存在，并随 DM-4 删除 raw kind-12 一并删除；
- Prepared v4 直接保存 core-rendered `ExactContextInputs`，adapter 不再传递 legacy artifact identity。
  DM-0 text hash 约束 raw block text；v4 snapshot hash 约束最终 request snapshot，二者刻意独立。

后续 current-state 修订：tail operational simplification D0a（`b79d67f8`）确认上述
single-candidate internal path 已无 production caller，因而删除了
`ValidatedSessionContextCandidate`、`SessionContextCandidateValidator.Validate(...)` 与
`SessionTailContextProjection.Materialize(...)`。current online route 使用 batch planning window +
selected-context materialization；本节保留的是 DM-1 迁移历史，不再是 current internal API 清单。

DM-1 当时的性能说明：validator 向 materializer 交付同一次 Parent walk 冻结的 suffix addresses，故未新增
cold-prefix walk，header/decoded suffix 复杂度保持原有量级；legacy bridge 当时仍让 validator 对 activation coverage 的
两条 setup exact refs 各重读一次 payload，故不宣称 payload-read count 绝对不变。DM-3 provider cutover
把 anchor proof 与 provider result 合并时再收掉该 legacy-only recheck。

DM-1/DM-2 当时的 Prepared v4 reopen 仍是 bounded：anchor 之前若存在可信的 earlier Prepared
checkpoint，shared resolver 只读该近头 checkpoint 与两条 setup payload；它绝不把当前正被重建的
manifest 当作证明。反之，在 DM-2→DM-3 的 legacy-only pre-Prepared planning 窗口，首次 anchor
若只有远处 setup 而无 earlier Prepared checkpoint，authoritative proof 可能 header-only 回扫冷前缀；
这是显式过渡成本，DM-3 provider cutover 不得通过把 raw activation reference 回塞 Prepared 来掩盖它。

### 主要落点（DM-1 历史计划）

- 当时计划拆分 `SessionTailContextProjection`：
  - concrete artifact -> contribution 的转换留在临时 adapter；
  - raw ancestry/setup/suffix fold/rendering 留在 core；
- 当时的 `SessionTailContextProjection.Materialize()` 改为接收 normalized candidate；该
  single-candidate surface 后由 D0a 退役；
- `RequestContextMaterialization` 不再保存 concrete artifact objects；
- 当时的 activation/store path 通过同程序集临时 adapter 产生 candidate；
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

### 实施状态（2026-07-28）

已完成 v4 breaking wire cutover：`plan.rawStartSetups` 提供 anchor fold seed，
`exactContextInputs` 仅保存已进入 request 的 one-hot snapshot/hash；Prepared 不再保存或反查
activation、artifact/set/epoch id。旧 v3 明确因 body schema mismatch 被拒绝，不提供 compatibility
decode、默认字段或 root replay fallback。raw kind 12 仍只服务 DM-2 至 DM-3 的 pre-Prepared legacy
planning bridge；在这个短过渡期，跨过历史 Prepared 寻找它可能增加 header scan，DM-3 provider cutover
必须紧接收口，不能把 activation hint 回塞进 v4。

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

### DM-3A 实施状态（2026-07-28）

已先完成 core-only 的 provider cutover，尚未搬迁 concrete store 或命令：

- `SessionRuntime` 可注入 `ICoherentContextCandidateSource` 与最小的 selection options；
- 仅 `AwaitingAgentAction` 的 pre-Prepared planning 调用 provider；它返回的候选仍由 core
  用 authoritative Parent-chain setup resolver（明确禁用 kind 12 checkpoint）和 candidate validator
  验证，再交给既有 materializer；
- `SendAsync()` 在 Observation append 前检查 provider 配置；选择结果为空会保留已追加的
  `AwaitingAgentAction`，供之后 `ResumeAsync()` 重试；
- Prepared/Started reopen、`Open()`、`Project()`、`ReplayHistory()` 与 tail resolver 均不需要
  provider，也不会调用它；
- 旧 kind-12/store/adapter 已离开 online route，但显式 legacy writer/store 仍保留到 DM-3B；
  本阶段不以 compatibility fallback 重新接回 online route，也不把尚可显式调用的过渡 surface
  误称为已删除或不可达。

### 新项目

```text
prototypes/SessionJournal.DerivedMemory/
tests/SessionJournal.DerivedMemory.Tests/
```

DM-3B 当时迁入/新增（artifact 部分已在 DM-6 再次 breaking cut）：

- artifact store 与 schema/DTO/identity/indexes；
- `DerivedMemoryRepository`；
- `DerivedArtifactSetStore`；
- current artifact -> normalized contribution renderer；
- `ICoherentContextCandidateSource` concrete implementation；
- derived-only exact set publication；
- repository-bound atomic write/lock/path-hardening。

DM-3 明确保留现有 producer surfaces，不与 store/provider cutover 同时搬迁：

- `RewriteMemoryBlockMaintainer`、`MemoryMaintenanceOrchestrator` 与 mutable `MemoryPack`
  暂留 SessionJournal；
- application profiles、prompts 与 target paths 留在 `SessionJournal.Maintainers`；
- current runner/composition 留在 `SessionJournal.Cli`。

DM-3B 已删除 `LegacyArtifactContextCandidateAdapter` 以及 online raw activation writer/store coupling：
DerivedMemory provider 直接产出 neutral candidate，不再需要 raw activation/store 到 candidate 的同程序集桥接。
原计划认为 `LegacyArtifactContextSnapshotFactory` 必须保留到 DM-4；实施复核发现它只被已删除的
writer/offline sidecar readiness 消费，因此与 writer 一并删除。kind-12 codec、reducer/tail
read-only legality 与 offline raw provenance 验证仍保留到 DM-4。

DM-3B 没有伪造 epoch，也没有加入尚无消费者的 usage/audit index。shared epoch authority 仍从 DM-5
开始；不能为了让 set 看似“同步”而把当前 anchor 包装成假的 epoch。

generic producer substrate 的最终归属在 DM-6/DM-7 复核；application-specific policy 不迁入
DerivedMemory。这样避免物理搬家与 Prepared wire cut 同时扩大。

### Composition

- `SessionJournal.Cli` 增加对 DerivedMemory 的引用，并继续组合 SessionJournal、
  SessionJournal.Maintainers 与 Completion；
- 长期 Agent Host 同样只在 composition root 同时引用这些程序集；
- `SessionJournal.Maintainers` 依赖 SessionJournal contracts 与
  Completion.Abstractions，但不因 store/provider cutover 反向依赖 DerivedMemory；
- `ChatSession.LegacyExportCli` 的引用图和命令保持不变；
- provider instance 与一个 SessionJournal repo/session 绑定；
- Engine/request coordinator 只保存 interface；
- `SessionJournal.Open(path)` 无 provider 仍支持 raw-only surfaces；
- online planning 入口明确要求 provider。

### DM-3B 实施状态（2026-07-28）

- 新程序集与测试项目已经进入 solution，core 与 core tests 均无反向 project reference；
- artifact store 完整搬出 core；当时保留的 v1 schema/path 已由 DM-6 的
  `derived/memory/v1/artifacts` v2 取代；repository 继续统一负责 path/reparse
  hardening、bounded lock 与 same-directory atomic replace；
- derived-only set 使用 deterministic full hash id、strict schema、exact previous CAS、immutable set
  与 atomic latest pointer；pointer 丢失只在完整无环 lineage 的 unique tip 下重建，fork/cycle
  fail-fast；
- set identity 持久化 canonical role requirements（role/target/required）并纳入 hash，读取时 caller
  policy 必须 exact match；set/pointer 在 JSON decode 前分别受 1 MiB / 64 KiB stream-length 上限；
- set policy 的 required/optional roles 是上层数据，不硬编码 autobiography/world-understanding；
- concrete provider 只读 DerivedMemory，不打开 Engine/EventJournal；core 用 authoritative raw
  validator 复核其 anchor/setup/source assertions；
- DM-3B `Latest` provider 把 `RawSuffixTokenBudget` 仅视为 non-binding hint，不承诺 budgeted
  selection 或 older-set search；
- public strict anchor helper 返回 exact setup address/schema/payload hash，供发布前在仍持有 raw
  Engine 的 composition code 调用；
- CLI maintainer writer 当时迁入 DerivedMemory repository；DM-6 已进一步改为
  `DerivedMemoryRepository.Artifacts + DerivedMemoryMaintainerRunner`；raw
  `checkpoint-artifact-set` command、parser/help/tests 和 offline sidecar readiness/active fields 已删除；
- Observation integration 已证明 publish 不改变 raw head/event count、online 不写 kind 12；
  Prepared failpoint 后删除整个 `derived/` 并在不注入 provider 的情况下仍按 canonical bytes exact
  reopen。

### CLI transition（DM-3C）

- 已增加 `publish-derived-artifact-set`、`list-derived-artifact-sets`、
  `validate-derived-memory` 与 `rebuild-derived-artifact-set-latest`；
- publish 要求 explicit expected-previous CAS，members 必须共享 common anchor；CLI
  从该 raw anchor 调用 strict setup helper，用户不能提交 setup refs；
- public inventory 只暴露 content-free stable records，不暴露正文/路径，也不要求 caller
  提供 policy 才能 self-validate persisted role snapshot；
- inventory 严格读取 set/pointer/member derived consistency，但允许 missing/stale pointer、
  fork/cycle 留给诊断；repository validation 进一步要求每个 exact key role snapshot
  一致、完整无环单 tip 且 pointer exact 指向 tip；
- validation 是纯读取：空 derived 合法，不 rebuild、不创建 derived 目录/lock；orphan
  artifacts 合法；
- artifact strict inventory 与 writer 共享 8 MiB file/UTF-8 wire byte cap；strict reader
  在 deserialize 前拒绝超限文件，writer 在任何 artifact/index/directory mutation 前拒绝
  超限 candidate。derived 是可重建数据，因此这是 v1 direct cutover，不保留超限
  compatibility writer；
- 普通 artifact tolerant read/latest-index rebuild 的既有容错语义保持不变；8 MiB cap
  属于 repository strict validation 与新 writer contract；
- report 使用版本化 stable CLI DTO，位于 repo 外并 atomic publish；commands 拒绝未知
  option 与重复 scalar；
- 命令只写 derived repository，不追加 raw kind 12；E2E 锁定 publish 前后 raw
  head/event count/logical payload bytes/raw file hash 不变；
- 不把 usage index、online host composition 或 fake epoch 偷渡进第一版 command；
- raw kind 12 reader 只为 DM-3C/DM-4 之间的只读过渡暂存，DM-4 删除；
- 不保留双 writer 或 silent import。

### 验收

- `SessionJournal.csproj` 不引用 DerivedMemory；
- DerivedMemory 单向引用 SessionJournal；
- SessionJournal.Tests 用 fake provider 覆盖 core；
- concrete persistence/provider tests 位于 DerivedMemory.Tests；
- 手工 exact autobiography + world-understanding artifacts 可发布 derived set；
- provider 返回 candidate 后可完成 Observation completion；dependency-closed ToolResult
  continuation 继续由 core fake-provider suite 覆盖，concrete provider integration 可在 DM-3C
  command/fixture 成熟后补强；
- provider 缺失时 pre-Prepared 明确 not-ready；
- Prepared 后 provider/sidecar 删除仍 exact reopen。

## 9. DM-4：删除 raw derived-set activation

> **实施状态（2026-07-28）**：完成。以下为已落地的 cutover 合同与验收结果。

### 目标

移除 raw/derived 反向引用和 current activation 兼容表面，完成候选 C 的 semantic cutover。

### 已删除或收缩

- raw derived-set event kind、body、member/reference contracts；
- event codec/schema/goldens；
- reducer/tail-resolver idle-boundary 分支；
- latest-equals-selected/raw activation validators；
- offline readiness report 的 active raw set；
- activation setup checkpoint 逻辑。

### Governing setup hint

- near-head Prepared 继续提供 current setup hint；
- pre-first-Prepared derived candidate/epoch 可以提供可重建 hint；
- online 沿真实 Parent lineage 回溯；命中受控 writer 产生的 Prepared 后，重验 referenced setup
  payload 的 kind/schema/hash，但不再 O(N) 证明它们是该 Prepared ancestry 上最新的 setup；
- Prepared append 前必须完成 request reconstruction、canonical exact check、bound setup cursor
  validation 与 head CAS；这是 bounded checkpoint 的 writer trust boundary；
- 不可信 import 必须先通过 full offline validation，不能直接把任意 schema-valid Prepared 当作
  online hint；
- 未命中 Prepared 时，raw header parent walk 是 authoritative fallback；
- hint 缺失只能增加 reads，不能改变答案；
- 不引入 dedicated config ref 或 full root projection cache。

### Migration

- 旧实验 repo 不保留 retired wire compatibility decode；
- 从 legacy export 重新 import 到 fresh repository；DM-4 不提供 in-place raw wire migration；
- 删除/rebuild derived repository；
- 用 derived-only set publication 恢复 online readiness。

### 验收

- raw event inventory 不包含 derived activation；
- raw validator 不打开 DerivedMemory；
- 删除全部 derived files 后 raw audit/Prepared reopen 通过；
- tail resolver 与 full reducer execution state differential 通过；
- real legacy export 可导入全新 repo；
- no compatibility alias/decoder/silent full replay；
- production/test code search 不再出现 retired raw activation surface；
- CLI 与 DerivedMemory E2E 通过“raw opaque kind 全部属于当前
  `SessionEventKind` inventory”检查守住 breaking-wire 边界。

## 10. DM-5：Shared DerivedArtifactEpochPlanner

> **实施状态（2026-07-28）**：已完成。实现位于
> `SessionJournal/SessionHistoryPlanning.cs`、
> `SessionJournal.DerivedMemory/DerivedArtifactEpochPlanner.cs` 与
> `DerivedArtifactEpochContracts.cs`；CLI 使用 singular
> `configure-derived-artifact-planner` / `plan-derived-artifact-epoch` /
> `list-derived-artifact-epochs`。

### 目标

在任何 LLM maintainer 调用前，统一决定同一 coherence group 的 history coverage，解决 daily parallel
maintenance 与 independent prompt-tuning 的同步问题。

### Repository shape

概念布局：

```text
derived/memory/v1/
  planner-configs/<config-hash>.json
  epochs/<epoch-id>.json
  sets/...
  indexes/
    current-planner-configs/<key-hash>.json
    latest-epochs/<key-hash>.json
    latest-sets/...
```

planner key 的长期形状是 exact `lineageKey + coherenceGroup`；但 v1 authority 明确只接受
`lineageKey == SessionJournalDefaults.MainBranchName`，不能把任意 token 当作已经具备 branch-aware
能力。未来支持多 branch 时必须先提供 exact ref/lineage authority，再放宽该约束。config snapshot 自带
`previousConfigId`，因此 config history 与 epoch ledger 都是 append-only、可验证的单 tip
lineage；current/latest 文件只是可重建 pointer。config 不进入 raw event sequence。

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

v1 config 要求
`hardLimit > minimumRecent + epochTrigger + schedulingHeadroom`；否则正常 epoch trigger
在 backpressure 前永远不可达，configure 必须 fail fast（包括 checked cost overflow）。

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
回答“历史实际上怎样切了”。planning diagnostics 随 epoch 持久化用于观测，但不进入
`epochId` identity，避免 cache/hint 命中差异改变 coverage identity。

genesis 的 `previousEpochId` 与 `inputSetId` 必须同时为空，对应显式
`empty-memory-pack` policy。非 genesis 二者必须同时存在；input set 需要通过 exact
self-validation，并满足同 lineage/coherence group 且
`CommonAnchor == previousEpoch.SourceEndInclusive`。repository strict validation 会再次
交叉验证该约束，不能只依赖 plan-time 检查。

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
- current-main-bound previous epoch/index；多 branch authority 后续显式扩展；
- atomic compare-and-publish；
- header-first addressed raw range planner；
- CLI configure/plan/list 与 content-free atomic reports；
- diagnostics：headers、payloads、decoded bytes、selected boundary/cost。

planning 采用两阶段协议：先在 derived write lock 外捕获 immutable config、读取 raw planning
window 并计算 candidate；随后所有 `Planned`、`AlreadyPlanned`、`BelowTrigger` 与
backpressure 终态都进入同一个短锁线性化点，重读 exact current config/latest pointer。
并发 direct-child 只有在 previous/input identity 与 captured raw head 都相同时才作为幂等重试；
pointer 已推进到 grandchild 或无关 successor 必须报 concurrency。raw scan 不进入 derived
repository 长锁。

`SessionJournal.ReadCurrentLineageHeaders()` 提供一次 store-neutral、header-only 的 current-main
snapshot（captured head、head-to-root address/parent/kind、read diagnostics），不 decode payload、
不调用 `Project()`。`ReadHistoryPlanningSeeds()` 用同一次 current-lineage pass 从 root 向 head
只解 setup payload，为多个 epoch starts 生成 core-owned verified seeds；historical planning
oracle 随后按 exact `plannedAt` + seed 增量重放各 epoch window，不会为 stable-root legacy
setup 产生 E 次全链回溯。

repository strict validation 与 latest-epoch pointer rebuild 会：

- 验证每个 epoch 的 `start < end <= plannedAt` 且三者仍位于 current lineage；
- 强制 genesis start 是该 lineage 的 `SessionCreated`；
- exact 比较 core-produced start setup refs；
- 按 epoch immutable config 重跑 token estimator 与 `SelectBoundary`，要求 selected
  replay-safe boundary、measured cost 与 deterministic semantic diagnostics exact 一致；
- rebuild pointer 前执行与 strict validation 等价的 config/topology/non-genesis exact
  ArtifactSet dependency closure。

因此 derived JSON/hash 自洽但 genesis 跳过历史、source end 落在 multi-tool 中间、
选择了另一个合法 boundary、篡改 cost，或已因 rewind/divergence 脱离 current main 的 epoch，
都不能通过 validate，也不能被 rebuild 成 current latest pointer。historical oracle 允许读取
对应增量 window payload，但不调用 `Project()` 或物化 start 之前的完整 conversation。

epoch file 与 latest pointer 的发布仍是 crash-safe 两步。由于 observational diagnostics 不进入
`epochId`，pointer 写前发现同 ID orphan file 时会 strict decode 并采用 first-writer durable
observations；但 decoded event/unit/boundary counts 与 total/eligible/retained costs 等 semantic
diagnostics 必须和当前重算一致，否则拒绝，不以 full-DTO collision 或覆盖方式处理。

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
- 非 `main` key fail fast，rewind/divergence 拒绝脱离 current-main 的 epoch；
- 允许 boundary-aligned 非等大 epochs；
- 10k+ cold prefix 不增加增量 planning payload reads；
- 无 artifact genesis 行为有显式 policy/test。

10k 验收使用 1 vs 10,001 个真实 imported cold turns、真实 EventFrame parent chain 和单次
ref move，对比相同 recent suffix 的 payload reads、decoded bytes 与 decoded event count。
在没有近头 Prepared/setup checkpoint 的 legacy lineage 上，governing setup proof 仍可能
产生随 cold prefix 增长的 **header-only** visits；DM-5 不声称消除了这项旧链 header scan。
Linux 测试 fixture 优先放在 `/dev/shm`，只避免 durable fixture 构造退化成物理盘 fsync
benchmark，不改变 EventJournal durability API。

## 11. DM-6：Epoch-bound Independent Maintainer Runner

> **实施状态（2026-07-28）**：完成。CLI、runner、artifact v2 与测试已切到
> exact durable epoch；旧 threshold/full replay 和 recap latest-by-profile surface
> 已直接退役。

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

拆分后 legacy backtest runner 与 threshold/full-replay 模式均已退役。
DerivedMemory production mode 不再允许 role-local split。
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

### DM-6 落地决策

- `DerivedMemoryMaintainerRunner` 属于 DerivedMemory；它只读取 exact
  epoch/config/input set dependency closure，并从 durable `RawStartSetups`
  构造 repository-bound planning seed，再调用
  `ReadHistoryPlanningWindowAt(sourceEndInclusive, seed)`。core seed 只重读两条 setup
  payload 与 bounded execution recovery；window 从已解码 suffix/fold 直接回传
  `EndSetups`，不从 end 回扫 root。global
  repository validation 仍是运维命令，不成为每个 role run 的隐藏全量扫描。
- `MemoryMaintainerProfileCatalog` 属于 Maintainers，拥有 stable role/profile、embedded
  prompt 与 concrete factory；CLI 只负责 Completion client、fingerprint、路径与 report
  composition。
- artifact 直接切为唯一 v2 `DerivedMemoryArtifactStore`，物理位置
  `derived/memory/v1/artifacts/`。identity 包含 epoch/plan、role/profile、exact
  range/setup、input set/structured members、previous role artifact、
  producer/prompt/model 与 candidate/attempt。store 是 append-only，没有 role-local
  latest pointer 或 linear CAS；ArtifactSet publication 是唯一 selection boundary。
- setup provenance 不压成模糊的一组地址：`RawStartSetups` 保存 exact epoch 起点的
  fold seed，`AnchorSetups` 保存
  `ResolveContextAnchorSetupReferences(SourceEndInclusive)` 的 exact 末端事实；两组均含
  address/schema/payload hash。runner 复核 input set anchor 等于前者，set
  publication/strict read 只把 member artifact 的后者与 set anchor exact 比较。
- genesis 明确 empty `MemoryPack`。non-genesis 从 exact input set 恢复全部 blocks；若
  topology 新增 role 而 input set 尚无该 role，则 old block/previous role artifact
  显式为空，其他 blocks 仍进入 PriorContext。
- exact retry 复用同一 artifact identity；改变 candidate/attempt 或 prompt/model
  fingerprint 会追加 alternative，不覆盖旧 candidate。writer failure 不会触碰 set
  pointer。
- artifact id 使用完整 canonical identity hash，不存在 collision suffix；同路径的
  strict same identity 是 durable retry，其他情况一律 corruption/hash-collision
  fail-fast。writer 在 lock/directory 前完成 input member shape/uniqueness/hash/previous
  relation 校验；exact point read 在 existence probe 前拒绝 symlink/reparse point。
- global validation 交叉 artifact 与 durable epoch/config、raw range/setup、input set/member
  snapshot/previous role，并按 unique raw end 缓存 anchor authority。未被 set 选择但
  dependency closure 完整的 alternative candidate 仍是合法 orphan；无 epoch/closure 的
  detached artifact 非法。runner 不调用 global audit。
- prompt fingerprint 使用 schema-tagged canonical structured JSON，避免 delimiter
  collision；CLI 在任何 Completion/目录/LLM side effect 前拒绝 output 与 call-log
  exact 或双向 ancestor 冲突。
- 这是 derived-rebuildable breaking cut：`derived/recaps/v1/`、
  `DerivedRecapStore`、latest-by-profile、threshold replay tests/CLI options 均已删除，
  不保留 compatibility path。

## 12. DM-7：Parallel Orchestration 与 ArtifactSet Publication

> **实施状态（2026-07-28）**：已完成。DerivedMemory 现拥有 deterministic
> transaction/job identity、typed required/optional provisioning、共享一次
> exact-epoch snapshot 的并行 runner、immutable per-role settlement、missing-role
> resume、`changed` / `unchanged` / `identity` outcome，以及只在 required roles
> 闭合后先写的 immutable finalization intent。intent 冻结 included settlements、
> omitted optional roles 与 exact expected set；crash/reopen 不会重试已冻结的
> optional omission，并可从 intent-before-set 或 set-before-return 两侧幂等收口。
> reopen 只有在 latest 已指向 exact set、或已沿同 exact policy lineage 前进到其后代时
> 才报告完成；missing pointer 先按 unique tip 严格重建，绝不把 descendant pointer
> 回退到旧 transaction 的 set，divergent pointer 明确 fail-fast。
> ArtifactSet v2 直接绑定 exact epoch、transaction、
> topology、完整 provisioning 和 settlements；发布前后均重新验证 current raw
> lineage authority。`SessionJournal.Cli run-derived-memory-orchestration` 提供显式
> run/resume composition；只有 `produce` role 需要 Completion connection，
> `identity` 与 exact `select-existing` 不创建 LLM client。旧 set schema 与绕过
> transaction 的 publication 参数没有 compatibility path。
> 两个 maintainer CLI 在读取 connections/prompt、创建目录/client 或调用 LLM 前，统一
> 拒绝 readonly inputs 与 output/call-log 的 exact/ancestor/descendant 冲突及
> symlink/reparse path；orchestration role/policy 结构也在任何 writable side effect
> 前完成验证。

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

> **实施状态（2026-07-28）**：完成。core/DerivedMemory/CLI 已切到 bounded two-phase
> discovery/materialization、Prepared v5 strict bootstrap、shared estimator 与 online lifecycle；
> hard-limit partial failure + dispose/reopen 组合 oracle 已覆盖 durable pending resume。

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

current `DerivedMemoryOnlineLifecycleCoordinator` 同时实现 neutral lifecycle 与 candidate source：

- 构造函数接收 exact policy、lineage 与 host-bound role executions，不读取 Maintainers catalog
  或 connections；
- 若存在 pending epoch，先 resume/run DM-7，再考虑 successor，避免越过未完成 epoch；
- maintainer 失败但旧 usable set 尚未触及 hard limit 时，旧 set 继续服务；超过 scheduling
  headroom/hard limit 则返回显式 backpressure；
- callback 只能写 derived state；core 在 callback 前后验证 exact raw head，禁止偷偷追加 raw event。

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

DM-8 current evaluator 在 core 中完成真实成本判断：同一 planning window 只 decode/fold 一次；
suffix cost 使用 `SessionHistoryTokenEstimator`，total budget 则对最终形状
`CompletionRequest` 的 canonical bytes 估算。`Budgeted` 先试满足 raw budget 的最旧 candidate，
再按 total budget 向较新 set 收缩；derived text 每次只 materialize 一个。最终 candidate 会再次
验证 exact descriptor/content 与 total budget。

### Restart phases

- raw tail recovery：不访问 DerivedMemory；
- Prepared/Started：只从 raw manifest exact reopen；
- unprepared `AwaitingAgentAction`：调用 candidate provider；
- idle `Open`：raw-only 可成功；
- `SendAsync`：在 raw mutation/provider call 前完成 memory readiness preflight；append Observation 后按
  exact boundary 重新 materialize；
- provider 缺失/损坏：derived-not-ready，不是 raw corruption。

`SendAsync` 在 append Observation 前执行 lifecycle + candidate/total-budget preflight；失败时
raw head/event count 不变。append 成功后必须按新 exact boundary 再执行 lifecycle 与选择，不能
复用旧 candidate。ToolResult continuation 在 dependency-closed
`AwaitingAgentAction` boundary 走同一路径。Prepared/Started reopen 永远不调用 lifecycle/provider。

真实空 lineage 的 bootstrap 采用 Prepared v5 的零个 `ExactContextInputs`，raw suffix 必须落在
显式 `BootstrapRawSuffixTokenBudget` 内；不创建伪 artifact/空 contribution。第一个真实 set
发布后 provider 返回 candidates，bootstrap 自动失效。

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

### Real acceptance evidence（2026-07-28）

第一阶段在全新临时 repo `gitignore/dm8-real-whc5bJ/session` 上，从
`cyber-copy-upgraded/chat-session-legacy-upgrade-export.json` 导入 71 对
observation/action；raw import 验证通过。online lifecycle 规划 epoch
`dae_b5fd9314…cc630`，两个真实 `dsv4p` maintainer 在相差约 41 ms 的时间点并行启动，
分别约 402 s / 588 s 完成；2 artifacts、2 durable settlements 与 finalization 完整发布为
set `das_b8b88e36…19f83b`。最终 raw/derived offline validation 均通过，Prepared v5 数量为 1。

agent completion 当时未闭合：provider 连续返回 `503 model_not_found / no available channel`。根因
不是 DerivedMemory，而是 legacy import 保留了已下线的 `unsloth/qwen3.6` runtime setup。首次失败后
raw 正确停在 `AwaitingCompletion`；修复后的 CLI restart 从同一 Prepared exact request 进入
`ResumeAsync`，显式 `restart-new-attempt` 只新增 agent attempt，未重跑 maintainer。外部成功结果
不能伪造；这次失败揭示并修复了 CLI Send/Resume routing 与 explicit uncertain policy 缺口。

最终 real gate 已由 fresh import 闭合：在 raw chain 追加与 `dsv4p` 匹配的
`deepseek-v4-pro` + `openai-chat/deepseek-v4` runtime setup，复用同一 raw provenance 上已经 strict
验证的 autobiography + world-understanding coherent set。第一轮真实 online turn 完成后 phase 为
`Idle`；dispose/reopen 后第二轮同样成功回到 `Idle`。两个 content-free report 的
`errorCount = 0`，未记录 response 正文、完整 request 或 connection secret。第二轮结束后的 strict
validation 再次通过：raw head phase 为 `Idle`、inventory 为 157 events；derived inventory 为
2 artifacts / 1 set / 1 latest pointer / 1 planner config / 1 epoch / 1 transaction /
2 settlements / 1 finalization。

deterministic tests 另证明：provider failure 后 CLI 默认 refuse 不重发，显式 restart 只增加一个
attempt；hard-limit 下一个 required role 已 durable settlement、另一个 role 失败时返回
backpressure，dispose/reopen 后只补缺失 role、不重复 epoch/transaction/已完成 role，发布 set 后若
suffix 仍超限则继续显式 backpressure，raw head/event count 全程不变。10k cold-prefix performance
test 继续证明 online `FullProjectionInvocationCount` 不变且 payload reads 不随 selected anchor
之前的历史增长。

## 14. Migration 与 schema 边界

| 变化 | 权威迁移方式 | 明确禁止 |
| --- | --- | --- |
| Prepared v3 -> v4 | `ChatSession.LegacyExportCli export-json` 后由 `SessionJournal.Cli import-legacy-json` 导入新 repo；盘点非幂等外部副作用 | compatibility decoder、缺省字段、自证 fallback |
| Prepared v4 -> v5 | 同上；这是为 strict zero-input bootstrap 所做的 direct wire cut，新 repo/import 后重建 derived | v4 compatibility reader、把空 artifact 伪装成 bootstrap |
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
  - maintainer settlement/publication；
  - lifecycle pending-before-successor、旧 set 可用性、backpressure 与 bounded discovery。
  - hard-limit partial settlement + dispose/reopen + pending resume 组合 oracle。
- `tests/SessionJournal.Maintainers.Tests`
  - stable maintainer/profile/target identity；
  - embedded prompt/profile loading；
  - concrete producer behavior 与 prompt override；
  - 不承担 epoch/store/selection tests。
- `tests/SessionJournal.Cli.Tests`
  - composition；
  - CLI parsing/path safety/atomic reports；
  - plan/run/publish workflow；
  - deterministic online maintenance + agent completion；
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
- discovered/materialized candidate count 与 selected ordinal；
- raw-suffix / total-request estimated tokens；
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

## 17. 实施收口

DM-0～DM-8 的计划能力均已落地。current online composition root 是
`SessionJournal.Cli run-online-turn`：它把 concrete maintainer role bindings、Completion
connection、`DerivedMemoryOnlineLifecycleCoordinator` 和 raw engine 组合起来；core 项目仍不
引用 DerivedMemory/Maintainers。命令按 boundary 选择 `SendAsync` 或 `ResumeAsync`；uncertain
attempt 缺省拒绝，只有 operator 显式选择 `restart-new-attempt` 才会发起新 provider attempt。

已完成收口闸门：

1. independent review 已检查 authority、crash/reopen、bounded read 与 pre-append side effect；
2. relevant full tests、hostile provider tests 与 zero-warning solution build 已通过；
3. fresh legacy import、shared epoch、两个真实 `dsv4p` maintainer、ArtifactSet publication、
   matching runtime setup、两轮 online completion 与 process reopen 已验收；
4. real reports 只记录 content-free ids/status/metrics，没有提交 connection secret、call log 或完整
   request。

后续 retrieval、多 coherence group 组合、动态 maintainer provisioning UI 属于新计划，不应继续
塞入 DM-8。

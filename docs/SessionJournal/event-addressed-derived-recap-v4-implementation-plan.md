# SessionJournal EADR V4：实现与替换计划

> **状态**：Closed Completion Record / Mixed Historical
> **日期**：2026-07-30
> **核心概念**：
> [EADR 核心概念](event-addressed-derived-recap-concepts.md)
> **目标设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)
> **Post-R3 配置设计**：
> [Repo-owned RecapPlannerConfig](recap-planner-config-repository-design.md)
> **Post-R3 Cadence 设计**：
> [Derived Recap Cadence](derived-recap-cadence-target-design.md)
> **Post-C2 HistoryLoad 设计**：
> [Derived Recap History Load](derived-recap-history-load-target-design.md)
> **兼容策略**：不迁移、不双写、不读取 historical DerivedMemory v2/v3
> **关闭边界**：R0～R3、Post-R3 C0～C3与 H0～H2均已完成。
> 2026-07-31 production cadence已唯一切换到 HistoryLoad config V2，并以当时的
> Galatea legacy export完成 deterministic real-repo acceptance。
>
> 本文保留工作分解、交付顺序与 commit/evidence map，既包含 closed completion record，也包含实施时的
> historical plan 叙事；它不再承担 current API、wire 或 implementation-status authority。核对 current
> 实现应读取对应 component README、code 与 focused tests。

## 0. 原则

V4 的 durable workflow 从新 projects/tests 开始，不在 current
`SessionJournal.DerivedMemory` 内原地演化。R0a 的一次性公共 contract cutover 是唯一机械例外：
current DerivedMemory/CLI/tests 只迁移到新 neutral/Recap 类型名以保持同一 solution 可构建，不在旧
assembly 中实现 EADR Store、Planner、catch-up 或 Restore。四个纵向包分别形成 contract →
implementation → focused tests → independent review → tail-fix 闭环。

目标 projects：

```text
prototypes/SessionJournal.DerivedRecap.Store/
prototypes/SessionJournal.DerivedRecap.Planner/
prototypes/SessionJournal.DerivedRecap.Maintainers/
tests/SessionJournal.DerivedRecap.Store.Tests/
tests/SessionJournal.DerivedRecap.Planner.Tests/
```

最终删除 current：

```text
prototypes/SessionJournal.DerivedMemory/
tests/SessionJournal.DerivedMemory.Tests/
```

全程不变量：

- raw Parent lineage 是 correctness authority；
- Recap 是 resident cold-prefix approximation；Memory 保留给未来 dynamic retrieval；
- `SetAdmissionAnchor` 与 `AbsorbedThrough` 分离；
- Building 不计 ordinal，Published directory 才计；
- exact invalid Published set不 fallback；
- source bytes、route 与 prior context在第一次 Maintainer 调用前冻结；
- healthy final block不重做；
- rolling checkpoint不进入 ordinal；
- Store不调 Planner/Maintainer；
- Prepared exact reopen不访问 Recap Store；
- 不保留 V4 Memory/Artifact 命名兼容层。

### 0.1 设计冻结与逐包反馈纪律

EADR V4 的 Shape/Rule 主干在 R0 启动时暂时冻结。后续不再先做多轮纯文档层面的全局
“方案自洽性 → 方案化简机会”循环；实现证据成为发现具体设计问题的主要输入。

每个 `R0～R3` 仍必须独立完成：

```text
package-local plan lock
  -> implementation + focused tests
  -> fidelity review：实现是否满足 canonical Shape/Rule
  -> simplification review：真实实现是否暴露可删除的状态、协议或抽象
  -> tail-fix + canonical docs回写
  -> next package
```

不得因为后续 package 已有设计，就在当前 package 提前铺设尚未使用的接口、状态或兼容层。review
finding 默认在当前 package 内闭合；只有既不削弱当前 package 主张、也不会制造跨包双真源的问题，
才可以明确延后。

R0 只在下列实现证据出现时退回整体 Shape/Rule，而不是做 package-local tail-fix：

- backend 无法兑现 required atomic rename / durability contract；
- neutral Context contracts 无法避免 raw core 反向依赖 concrete Recap implementation；
- canonical manifest/block/publication encoding 无法形成唯一、稳定的 hash projection；
- strict ordinal 与 bounded Restore authority 在实际 Store API 中产生不可消除的冲突。

除上述情况外，filesystem API、codec shape、validator composition、test seam 与命名摩擦都先作为
Craft-tier 问题在当前 package 内解决。R0 gate 关闭前不启动 Planner、Maintainer catch-up 或
Published Restore。

### 0.2 Post-R3 follow-up plan lock

R0～R3以下章节是已经完成的 historical baseline与实施证据，不回写成尚未发生的新行为。
Post-R3新 planning authority由下列文档取代对应 baseline：

- cadence Shape/Rule：
  [Derived Recap Cadence](derived-recap-cadence-target-design.md)；
- 唯一施工顺序与 repo config：
  [Repo-owned RecapPlannerConfig §9](recap-planner-config-repository-design.md#9-实施工作包)。

Post-C3的public Host integration与Galatea `ChatSessionEngine` cutover分别由
[DerivedRecap Host Integration](derived-recap-host-integration-target-design.md)和
[Galatea → SessionJournal + DerivedRecap](galatea-session-journal-cutover-plan.md)维护；不得让
Galatea引用CLI executable或复制CLI internal resolver/readiness。

后续严格按：

```text
C0 cadence contracts/evaluator + policy/executor
  -> C1 repo document/loader + single composition
  -> C2 CLI/online authority cutover
  -> H0 unit estimator + window projector + Galatea calibration
  -> H1a inactive V2 contracts/codec/registry
  -> H1b Planner evaluator/policy/executor integration vertical
  -> H1c single production authority cutover
  -> H2 cache profiling decision
  -> C3 Galatea real acceptance
```

不得并行保留 `RawGrowthTrigger`与 `RecapCadenceConfig`，也不得让 historical
`RecapPlannerConfig`再次成为 Resume/Restore authority。

C0已完成 breaking cutover：new-plan scheduling只接受 exact、content-free planning-window
facts；header路径只能作 negative prefilter。

C1已完成 strict repo document/loader/atomic init、policy/profile typed resolution、
single immutable Host composition、完整 capability catalog、`planner-config init/inspect`
与 runtime authority split。C2进一步让 run/online在 durable phase与 Building-first discovery
之后条件加载一次 repo composition snapshot；public `DerivedRecapPreparedExecutor.ExecuteAsync`
只消费 preparer签发的 authority，并在内部路由 exact Building Resume或 new planning；两个
low-level executor均为 assembly-internal。Restore只接受 frozen durable state、完整 capability
registry与 code-owned V4 hard caps，
Prepared/Started recovery也保持 Store/config zero-touch。实现验收还
关闭了 pre-first-recap语义缺口：`EmptyLineage + NoBuild + EmptyLineage`通过显式
`RawHistoryAuthorized`使用完整 raw recent history，不再被误判成 strict fresh bootstrap。

H0～H2的唯一 Shape/Rule与施工边界由
[Derived Recap History Load](derived-recap-history-load-target-design.md)维护。该 cutover把
HistoryUnitCount scheduling authority替换为抽象 HistoryLoad；V1内部使用 `o200k_base`，但
HistoryLoad不表示推理模型/provider token。不得同时保留两套 cadence authority，也不得为此升级
raw event或Store schema。

HistoryLoad内部 package gate：

- H0：`HistoryLoadUnit`、`IHistoryUnitLoadEstimator`、
  `O200kBaseHistoryUnitLoadEstimator`、`RecapHistoryLoadProjector`、exact framing/golden vectors、
  baseline-relative measurement limits与Galatea load distribution/threshold calibration；不改变
  active scheduling；projector从 exact baseline address自行解析 unit offset、传入 per-unit cap并
  聚合 load/bytes/boundaries；
- H1a：增加尚未 active的 config V2 DTO/strict codec/estimator registry，V1仍是唯一 production
  authority；
- H1b：把既有 projector与baseline-relative facts接入 evaluator/policy/executor focused
  vertical，但不得提供 production selector让 V1/V2并行；
- H1c：一次性切换 repo init/inspect、CLI/online/report与built-in composition，拒绝 V1 config，
  删除 HistoryUnit scheduling comparisons、header cadence negative与cross-unit validation；
- H2：只根据 H0 calibration与针对性 pre-C3 profiling决定 bounded in-memory cache；
  persistent sidecar需另立设计。

完成记录（2026-07-31）：

| Gate | 状态 | 实施证据/结论 |
|---|---|---|
| H0 | 完成 | `e07ff1af` estimator/projector；`0dbf9d6d` calibration CLI与 Galatea evidence |
| H1a | 完成 | `2eb7188a` strict inactive config V2/registry |
| H1b/H1c | 完成 | `84a37cab` baseline ordering收口；`e47b635c` production/CLI/online原子 cutover |
| H2 | 完成 | 20～40-unit warm projection p50约 6～13 ms；不增加 bounded/persistent cache |
| C3 | 完成 | current Galatea export fresh import；实际 selection 为 growth 116,458 / absorbed 98,082 / recent 18,376；failure/resume、exact损坏恢复、online、Prepared recovery全部通过 |

Recent reserve使 new Building的 `SetAdmissionAnchor`通常早于 raw head；因此 C0同时把
`DerivedRecapExecutionResult.BlockFailed`补为携带 exact admission，CLI resume/report不得再从
raw head推断 Building slot。

## 1. R0：Contracts + Publish/Read vertical

### Intent

一次性锁定 Recap vocabulary、neutral context contract 与最小 Store，从 empty Building 走通 atomic
Publish、strict select 与 materialize。

R0 在同一个关闭闸门内按依赖顺序分成：

```text
R0a contract cutover
  -> R0b new Store + Publish/Read
  -> joint fidelity/simplification review
```

R0a 可以机械迁移 current 调用方，但不得改变 current DerivedMemory 的 transaction/planning
行为；R0b 不得反向引用 current DerivedMemory。

### In scope

#### Contract cutover

- `DerivedArtifactSet` → `DerivedRecapSet`；
- `MemoryBlockId` → `RecapBlockId`；
- `IMemoryBlockMaintainer` → `IRecapBlockMaintainer`；
- maintenance request/result/profile/rewriter 改为 Recap naming；
- `MemoryPack*` context-header projection 改为：
  - `ContextHeaderCarrier`；
  - `ContextHeaderBlock`；
  - `ContextHeaderBlockPath`；
  - `ContextHeaderPack` / Draft；
  - `RenderedContextHeader` 或直接 `ContextHeaderSnapshot`；
- `ISessionMemoryLifecycleCoordinator` 评估并 cut over 为
  `ISessionContextLifecycleCoordinator`；
- `SessionContextCandidate` / descriptor 使用 `SetAdmissionAnchor`，per-contribution provenance
  使用 `AbsorbedThrough`；
- exact selection descriptor显式携带 optimistic snapshot token；
- selection reason覆盖 `EmptyLineage / OrdinalUnavailable /
  ExactPublishedSetInvalid / StoreUnavailable`；
- carrier/target、contribution count、UTF-8 size、content codec/hash 与 stable ordering 下沉为
  public neutral validator；raw core 与 Store publication gate共用，raw ancestry仍由
  SessionJournal authority验证；
- 保持 persisted MaintainerId、block keys、prompt bytes/logical names不变。

这是单次 breaking cutover，不增加 obsolete wrapper 或双 contract。

#### Store vertical

- `derived/recap/v4/refs/<ref>/store.json + building/ + published/`；
- crash-safe CreateStore/reset；
- RefId/EventAddress/RecapBlockId path codecs 与 safe-descendant guard；
- canonical manifest/block/publication codecs 与 checksums；
- manifest checksum 覆盖排除 hash 字段的 canonical manifest bytes；
- Building manifest authority；
- `CanPublish` shared validator；
- publication candidate seal：

```text
flush/close/fsync manifest, inputs and every final block
  -> containing-directory durability barriers
  -> temp envelope
  -> file fsync
  -> rename to publication.json
  -> directory barrier
  -> final eligibility/latest-anchor validation
  -> building directory atomic create-new rename to published
```

- Published publication authority；
- envelope ETag descriptor；
- Parent-chain point lookup strict ordinal；
- exact materialization + per-block `AbsorbedThrough`；
- typed selection reasons。

R0 `CreateBuilding` 只接受 `Maintain { Source = Empty }` 的显式 fake frozen plan。完整
`Inherit / Maintain { Source = Existing }` union 在 R0 已有 canonical codec 与 shape validation，
但在 R1 的 exact source envelope double-read/copy 协议落地前不得创建可发布 Building。R0 的
“resume”仅指已经 sealed 的 `publication.json` 可以继续完成 directory promotion；按 frozen source
补 final blocks 的语义性 Building Resume 属于 R1。

`DerivedRecapContextCandidateSource` 绑定同一 `SessionJournalEngine + DerivedRecapStore` lifetime：

- Source 用 engine capture 的 header-only current lineage驱动 Store point lookup，Store 不另开 raw
  repository，也不从 directory/name排序推断 lineage；
- selection 前后复核 exact completion boundary，拒绝 stale snapshot；
- Store descriptor 只包含 `RefId + SetAdmissionAnchor + EnvelopeSha256`；
- Source 在 selected admission anchor 上从 raw authority解析
  `SessionContextAnchorSetupReferences`，组装 neutral descriptor；setup refs 不写入 Recap
  manifest/publication；
- materialization 前后复核 envelope token，随后仍由 raw core验证 anchor、setup refs、ancestry
  与 shared contribution shape。

### Out of scope

- Planner trigger/Maintainer calls；
- Existing/Inherit source freezing 与语义性 Building Resume；
- rolling checkpoint；
- Published semantic Restore；
- full scrub；
- old data migration。

### Tests

- codecs/path traversal/symlink/reparse isolation；
- Linux child-process failpoint + forced termination/reopen 覆盖 root Create/reset、publication seal
  与 Building→Published promotion关键边界；
- missing required root directory不是 EmptyLineage；
- valid-JSON manifest mutation触发 checksum failure；
- manifest/blocks Complete 但未 promoted不计 ordinal；
- envelope temp/final seal crash matrix；
- manifest/input/final-block file flush与 directory barrier前后的 power-loss matrix；
- directory rename old-or-new visibility，destination exists fail-fast；
- final gate拒绝 retroactive insertion；
- `n = 0 / n > 0 / ordinal short / empty lineage`；
- invalid exact Published directory仍计数、不 fallback；
- off-lineage/ref rewind不可见；
- descriptor/materialization ETag double-read；
- shared limits 与 raw ancestry；
- Prepared snapshot/reopen不访问 Store；
- 10k cold prefix只做 raw header walk + point lookup。

### Done when

fake Recap blocks 可以完成：

```text
Building -> CanPublish -> atomic Published
         -> strict NthPrevious -> exact Context contributions
```

且无需 Planner、Maintainer、derived lineage 或 latest pointer。

### R0 完成记录（2026-07-30）

R0 已按 `contract cutover → Store implementation → independent review → tail-fix` 关闭：

| Commit | 内容 |
|---|---|
| `44e535a7` | `ContextHeader* / IRecapBlockMaintainer / Context lifecycle` 一次性 contract cutover；concrete Maintainers assembly迁移；candidate anchor/cursor/token与 public neutral validator收口 |
| `763fdada` | pre-cutover ArtifactId/SetId/wire golden、SnapshotToken负向、exact-invalid/store-unavailable lifecycle矩阵 |
| `046667ed` | canonical codecs、Linux durable backend、Store root、Building/Published、strict ordinal、ETag materialization与 raw-bound source |
| `9d5d7e3d` | engine-bound publisher、rename前 authoritative raw-head final gate、`renameat2(RENAME_NOREPLACE)`、跨实例锁竞争与 child-process crash matrix |

最终验收：

- `SessionJournal.DerivedRecap.Store.Tests`：40/40；
- `SessionJournal.Tests`：309/309；
- R0a regression：current DerivedMemory 109/109、DerivedRecap.Maintainers 18/18、CLI 71/71；
- `Atelia.sln` build：0 warnings / 0 errors；
- 独立最终 review：P0=0、P1=0。

durability 证据是 Linux production `fsync`/directory barrier 与独立子进程
`Environment.FailFast` 后父进程 reopen 的 8 点矩阵，覆盖 root commit、publication seal、
Building→Published promotion 与 reset 的关键前后边界。它证明当前 OS/filesystem contract 下真实
进程终止不会暴露半 membership；不宣称模拟物理断电、device volatile cache、network filesystem
或未验证平台。

R0 public Building 入口只支持 `Maintain { Source = Empty }`。R1 必须先实现 exact source envelope
double-read/copy，之后才能启用 Existing/Inherit、语义性 Building Resume、rolling checkpoint 与
Maintainer execution；不得绕过当前 stable rejection。

## 2. R1：Planner + Build/Resume vertical

### Intent

实现 frozen source、Maintain/Inherit、endpoint-only catch-up 与 rolling checkpoint，并证明 partial
failure/reopen只补当前缺失工作。

### In scope

- `RecapPlannerConfig`（R1 historical baseline；new planning已由 Post-R3 cadence target取代）：
  - raw growth trigger/hard-limit；
  - admission selection；
  - active recap catalog；
  - NoBuild/Maintain/Inherit policy；
  - content/route/call limits；
- exact source envelope read：
  - read token；
  - copy by commitments；
  - validate payloads；
  - reread same token；
  - changed 时隔离 pre-manifest work；
- discriminated `RecapBlockPlan`；
- manifest self-check；
- Existing/Empty source；
- ordered `CatchUpThrough[]`，start 由 previous endpoint推导；
- one frozen prior context per Maintain block；
- single `work/<block>.json` rolling checkpoint；
- `BlockPlanSha256`；
- phase-specific final-block execution；
- valid final block skip；
- Building Resume；
- Galatea/老王 fixture。

### Out of scope

- Published Restore；
- recursive source repair；
- step checkpoint chain；
- per-step distinct prior context；
- relevance Tag/LLM policy implementation；首版使用 injectable deterministic policy。

### Tests

- trigger未达到返回 NoBuild；
- Inherit零 Maintainer调用且 cursor不变；
- Maintain正文不变仍推进到 admission；
- source envelope在多 block copy中变化时整个 pre-manifest build重试；
- source repair在 manifest 后不改变 frozen input；
- endpoint start唯一推导、strict increase、final=admission；
- prior context Inline/Empty shape与 ancestry；
- partial Building output不作为其他 block prior context；
- rolling(A5/A11) crash 后只补 suffix；
- rolling replace只看见旧或新 healthy version；
- rolling missing/damaged只重跑当前 block完整 route；
- final endpoint checkpoint后、final install前 crash不再调用 LLM；
- alpha final healthy、zeta failed；reopen不重跑 alpha；
- old BlockPlanSha256 checkpoint被拒绝；
- structurally valid但绑定错误 BlockPlanSha256 的 final block被拒绝；
- route/call limit产生稳定 backpressure；
- 已有较新 Published anchor时拒绝旧 target。

### Galatea acceptance

```text
A1  client AbsorbedThrough
A8  weekend Inherit
A12 weekend Inherit
A20 work target

source container A12
frozen old cursor A1
route A5 -> A11 -> A20
```

- A5/A11 可以早于 SourceSetAnchor A12；
- A5/A11不成为 set、ordinal或其他 block source；
- 只有 A20 atomically Published。

### Done when

真实进程 reopen 证明：正常 crash保留 block-local missing-suffix resume；checkpoint corruption最多重跑
该 block，其他 final blocks不重跑。

### R1 package-local plan lock（2026-07-30）

R1 不移植 current DerivedMemory 的 epoch/job/orchestration workflow，也不增加新的 durable
transaction state。最小 vertical 只保留以下 authority：

```text
Published source publication.json + committed blocks
  -> hidden pre-manifest staging
  -> Building manifest + immutable frozen inputs
  -> one replaceable work/<block>.json
  -> one final blocks/<block>.json
  -> existing R0 atomic publication
```

按依赖顺序实施：

| 包 | 范围 | 独立验收 |
|---|---|---|
| **R1A Store substrate** | exact Published source read；Store-owned source-by-commitment copy；所有 distinct source envelope统一 double-read；manifest-last Building install；Building-local snapshot；rolling/final typed health；same-directory atomic replace与短临界区 state-token CAS | source在 multi-block/multi-source copy中改变时没有可见 Building；manifest后 source变化不影响 frozen input；rolling replace reopen只见 old/new healthy version |
| **R1B Planner + executor** | runtime `RecapPlannerConfig`、ordered active catalog、pure injectable deterministic policy、`NoBuild / PlanReady / Unavailable`；exact-head admission/route/prior validation；phase-specific final-block execution；Maintain/Inherit；plan-or-resume→R0 Publisher | below-trigger 0 copy/0 call；limit backpressure 0 call；Inherit 0 call；Maintain unchanged content仍推进；healthy final skip；checkpoint只补 suffix |
| **R1C acceptance + review** | Galatea fixture、真实进程 reopen、fidelity/simplification review与尾部修正 | A1 source cursor经 A5/A11到 A20；A8/A12 Inherit不推进；A5/A11不成为 set/ordinal/source；alpha healthy、zeta失败后 reopen不重跑 alpha |

R1A 的 source read返回 snapshot只帮助 Planner构造 plan 中的 exact token/hash；它不授权 caller把
任意 bytes写入 Building。创建 Building 时 Store必须重新按 manifest source commitments复制：

```text
group by (SourceSetAnchor, EnvelopeSha256)
  -> read and validate each source envelope
  -> copy all required committed blocks into hidden staging inputs
  -> after every copy, reread every distinct source token
  -> validate derived frozen-input hashes against block plans
  -> write manifest last
  -> atomic staging-directory create-new promotion
```

同一 source anchor声明不同 token、source missing/invalid/changed均返回 typed failure；Store不在内部
无限 retry。caller不再向 public Building API提供 frozen input bytes。hidden pre-manifest staging
不是 Resume authority。

Building Resume只信 build-local manifest与 inputs。单 block inspection将 authoritative plan、
optional input、final health和rolling health绑定在同一 Building descriptor下。checkpoint损坏、旧
`BlockPlanSha256` 或 off-route cursor只使该 block从 frozen source重跑；unsafe path/symlink仍是
安全失败。Store不持锁跨 Maintainer调用；rolling/final写入用观察到的 component state token做
短临界区 compare-and-swap，拒绝覆盖另一 coordinator的进度。healthy but different final永不覆盖。

Planner policy是同步、纯 deterministic seam，只决定 NoBuild/Maintain/Inherit、admission候选与
bounded endpoints；它不做 IO、不调用 Maintainer，也不成为 lineage authority。Planner必须重新
验证：

- 所有 raw读取绑定同一 captured head，manifest安装前 head改变则整次重新规划；
- admission在 captured lineage上 replay-safe，且严格晚于 latest Published；
- route从真实 frozen `AbsorbedThrough` 或 Empty replay seed开始，strict increase，final等于
  admission；
- 每个 step使用 `ReadHistoryPlanningWindowAt(endpoint, start)`产生 dependency-closed slice；
- Inline prior-context anchor是 first replay start的同-lineage ancestor/equal，整条 route只用同一
  frozen snapshot；
- route/call/content/raw-event hard limits在首个 Maintainer调用前全部通过。

R1 executor顺序执行 blocks：Inherit exact-copy local input并保持 cursor；Maintain从
source cursor/previous endpoint唯一推导下一 start。每步即使正文不变也先以 endpoint更新并
durable replace rolling；final endpoint checkpoint成功后才安装 final。因此 checkpoint与 final
之间 crash，reopen可以 0 次 Maintainer调用安装 final。

R1 明确不实现 Published Restore、exact-invalid online self-heal、recursive source repair、
per-step prior context、checkpoint chain、Tag/LLM relevance policy、background scrub、CLI/R3
cutover或 current DerivedMemory compatibility bridge。

### R1 完成记录（2026-07-30）

R1 已按 `Store substrate → pure planning authority → executor → independent review →
crash acceptance → tail-fix` 关闭：

| Commit | 内容 |
|---|---|
| `354d9a60` | exact Published source snapshot、Store-owned multi-source freeze、Building-local authority、rolling/final component CAS |
| `fe458721` / `581f50fb` | pure planning contracts，以及 Schedule → Intent → exact preflight authority seal |
| `b2a82c06` | engine/store-bound Planner executor、Maintain/Inherit、Building Resume与R0 Publisher集成 |
| `f70387af` | earliest real block cursor boundary、Resume pre-LLM raw semantics、typed Store failures、engine-bound manifest前raw-head gate |
| `91a64026` | Galatea、rolling/final checkpoint、alpha/zeta、multi-source与manifest-last真实进程 crash acceptance |
| `dddd71bd` | 被更新 Published超越的旧 Building 在 Maintainer调用前稳定拒绝 |

最终 acceptance证明：

- Galatea client block在 A8/A12 两次 Inherit 后仍保持真实 `AbsorbedThrough=A1`，随后按
  A5 → A11 → A20 catch up；A5/A11不成为 Published set、ordinal或 source；
- child process在第一个 rolling checkpoint 后终止，reopen只补两个 suffix；final checkpoint
  后、final install前终止，reopen新增 Maintainer调用为 0；
- alpha final healthy、zeta失败时，reopen不重跑 alpha；
- 两个 distinct Published sources全部复制后统一复核 envelope；任一变化都不安装 Building；
- hidden staging manifest写入与 Building directory promotion两侧的真实进程终止分别只暴露
  Missing或完整 Building；
- Resume只信 Building-local manifest/inputs；source后续变化不改变 frozen plan；
- Published Restore、online self-heal、recursive repair、CLI与旧 DerivedMemory删除仍保留给
  R2/R3。

## 3. R2：Exact-slot Restore + Online lifecycle

### Intent

把 Published structural defects转成一组 bounded ephemeral restore actions，并闭合 exact invalid-slot
not-ready → Restore → reselect，同时不建立全库 self-heal system。

### In scope

- Store `StructuralDefects[]`；
- Planner 内部 ephemeral workflow：

```text
Prepare exact restore actions
  -> bounded actions
  | RestoreUnavailable(reason)
```

- healthy exact publication存在时，唯一 authority来自其 `FrozenPlanSnapshot`；仅在下述
  envelope-loss winner规则成立时，co-located exact manifest可作为一次性 restore witness；
- Resume/Restore只共用 frozen raw validator、pending-window preparation 与单步
  Maintainer runner；外层 Store/CAS/envelope workflow不合并；
- frozen plan exact不变；
- component atomic replace + publication envelope last；
- pending replacement复用；
- envelope-loss restore：
  healthy manifest cache + frozen inputs + final blocks → full revalidation → new envelope；
- inputs/work missing只影响 Restore capability；
- exact selected-slot on-demand Restore；
- lifecycle：

```text
metadata select
  -> Selected(descriptor): exact content inspection/materialize
       payload invalid -> same-slot bounded restore actions -> execute
                         -> reselect same slot -> materialize
  -> ExactPublishedSetInvalid(metadata defects):
       same-slot bounded restore actions -> execute -> reselect
       RestoreUnavailable -> stable not-ready
```

- no blind retry；只有显式 operation或外部状态变化后重试。

### Out of scope

- background/full-generation scrub；
- periodic/proactive heal；
- dependency scheduler；
- recursive source chain repair；
- backup/replication；
- terminal/transient三态 persisted taxonomy。

### Tests

- final block损坏但 rolling final健康：0次 LLM restore；
- earlier rolling endpoint健康：只跑 suffix；
- rolling/source均损坏：RestoreUnavailable或仅该 block完整重跑；
- healthy Published target在 inputs/work丢失后仍 materialize；
- block replace后、envelope前 crash保持 exact not-ready；
- pending replacement匹配 BlockPlanSha256时不重复调用；
- envelope replace后旧 descriptor fail-fast；
- byte-identical restore ETag可不变；
- publication missing但 directory membership保留；
- healthy manifest cache可重建 envelope；
- publication/manifest均不可用时ordinal保留、stable not-ready；
- Restore不得改变 roster/mode/source/route/prior/Maintainer/limits；
- latest/middle invalid slot均不 fallback、不重编号；
- Store不调用 Planner/Maintainer；
- selector不边读边修；
- Prepared后删除整个 Store仍 exact reopen。

### E2E

```text
historical raw growth trigger
  -> NoBuild 或 plan
  -> Maintain/Inherit
  -> rolling catch-up
  -> partial failure + reopen
  -> atomic Published
  -> strict NthPrevious
  -> completion Prepared
  -> exact block/envelope damage
  -> exact-slot not-ready
  -> bounded Restore same Published directory
  -> reselect/materialize
  -> Prepared reopen
```

### Done when

Galatea 可以多 epoch运行，Published damage不改变 ordinal，Restore没有演化成 transaction、scrub
或 recursive dependency workflow。

### R2 package-local plan lock（2026-07-30）

R2 不把 Building Resume 与 Published Restore 合并成一个 phase-neutral public workflow。代码级
只共享 `RecapFrozenPlanRawValidator`、`RecapPendingWindowPreparer`、
`RecapMaintainerStepRunner`；phase-specific Store API可复用底层 durable atomic-replace
primitive：

```text
Building Resume
  authority = building/manifest.json
  membership 尚未建立

Published Restore
  authority = published/publication.json
  membership 始终保留
  component replace 后、envelope replace 前 exact slot仍 not-ready
```

按依赖顺序实施：

| 包 | 范围 | 独立验收 |
|---|---|---|
| **R2A Published restore substrate** | exact authority inspection、manifest envelope-loss witness、per-block capability/pending replacement、Published checkpoint/final CAS、full revalidation与 envelope-last commit | block-first/envelope-last crash、old/new descriptor、strict ordinal不变、coherent authority conflict不 fallback |
| **R2B bounded Restore executor** | ephemeral actions / `RestoreUnavailable`、Keep/AdoptPending/InstallCheckpoint/ResumeSuffix/ReplayBlock、pending-only raw windows、engine-bound final raw-head gate | healthy/pending 0 call、earlier checkpoint只补 suffix、缺 source稳定 unavailable、head race留下可复用 pending但不安装 envelope |
| **R2C online lifecycle** | authoritative configured ordinal、latest prerequisite restore、R1 Run、configured exact-slot restore、single reselect、candidate-source facade | latest/middle invalid exact repair、不 fallback/不重编号/不循环、public concrete composition |
| **R2D acceptance + review** | process crash、envelope-loss、descriptor ETag、Prepared Store deletion、fidelity/simplification review | Prepared deletion exact reopen、P0/P1关闭，未出现 scrub/job/scheduler/recursive repair |

#### Restore authority winner

`publication.json` 分为三类，winner规则不可由 Planner猜测：

1. **Healthy exact authority**：envelope能 canonical decode、自校验，且 `RefId +
   SetAdmissionAnchor` 与 exact directory一致。它始终是唯一 frozen-plan authority；即使 blocks、
   inputs、work损坏或 manifest cache冲突，也不得 fallback manifest。
2. **Missing or non-authoritative damaged envelope**：文件缺失，或已在 bounded read内完整捕获的
   bytes因 shape/checksum/canonical validation无法形成一个自校验 publication authority。此时
   self-hashed、shape/identity均健康的 co-located manifest可作为一次性的
   **envelope-loss restore witness**。
3. **Coherent authority conflict**：publication自校验健康但 identity/anchor与目录冲突。必须
   `RestoreUnavailable(AuthorityConflict)`，不得用 manifest掩盖。

manifest witness不是 normal-read authority，也不与 publication形成双真源。它只授权按 exact
manifest恢复；全部 required frozen inputs和 final blocks必须在 envelope commit 前重新验证。
publication与 manifest均不可用时 ordinal仍由 directory membership保留，但 RestoreUnavailable。
publication authority file的真实 I/O/permission fault或在完整读取前超过资源上限属于
unobservable/unavailable，不得 fallback manifest。final/input/work component的同类 fault只令
对应 block capability unavailable/unusable，不发可写 component CAS token；它不重新裁决
publication/manifest winner。Restore不会为了判断损坏而无界读取大文件。

#### 最小 Published Store API

Store只提供 Published 专用薄操作，不回调 Planner/Maintainer：

```text
InspectPublishedForRestore(exact anchor + raw lineage)
  -> handle(authority kind + authority state token + exact frozen plan)
     + per-block health/capability
  | RestoreAuthorityUnavailable(defects)

AdvancePublishedCheckpoint(handle + component CAS)
InstallPublishedReplacement(handle + component CAS)
CommitPublishedEnvelope(handle + expected component tokens + raw-head gate)
```

commit在 per-Ref lock 内重新验证 authority token、exact plan、全部 final blocks与 witness所需inputs，
再从 exact plan和当前 finals生成 publication并 atomic replace envelope last。byte-identical
publication保持 token；否则旧 descriptor fail-fast。不得移动、删除或重新 promotion
`published/<anchor>/`。

per-block capability只允许：

- healthy old commitment：Keep；
- self-check healthy且匹配 exact plan/mode-final的 replacement：AdoptPending；
- healthy final-endpoint checkpoint：0-call install；
- healthy earlier checkpoint：ResumeSuffix；
- checkpoint不可用时，Empty source可 ReplayBlock，Existing需要 frozen input；
- Inherit final损坏需要 frozen input exact-copy；
- 缺失 dependency只令该 exact block/plan RestoreUnavailable，不递归修 source。

#### Online lifecycle bounded order

configured `NthPrevious` 只来自 boundary 上 authoritative governing
`RuntimeConfigSetup.DerivedContext.NthPrevious`。Engine把同一个
`SessionContextSelectionRequest`传给 lifecycle；Host/Planner不得保存第二份 ordinal。

一次 `PrepareAsync` 最多执行：

```text
inspect slot 0
  -> invalid: restore latest once + reselect latest once
  -> RunAsync once
  -> inspect configured nth against the new tip
  -> invalid: restore that exact anchor once + reselect configured nth once
  -> Ready | stable Backpressure/Unavailable
```

无循环、无 fallback、无邻居扫描。slot 0 restore只为解除 R1 Run的 latest-source prerequisite；
configured slot restore只为即将生成的 online request。outer lifecycle与 engine-bound envelope commit
均复核 raw head。Prepared之后的 recovery不进入 lifecycle，继续完全不读取 Recap Store。

### R2 completion record（2026-07-30）

R2 已按 R2A～R2D 完成实现、独立审阅、P1 tail-fix 与真实性验收：

| 包 | 实施证据 | 关键结果 |
|---|---|---|
| R2 authority/read | `d4972638`、`06421fd5` | exact publication / manifest-witness winner、canonical bounded read、六种 block capability |
| R2 component/envelope writes | `4e317752`、`a7662018` | Published checkpoint/final CAS、unobservable component不可写、所有成功 envelope路径均经过最终 raw-head gate |
| R2 shared seams + executor | `d1bd5543`、`02b4ceee`、`64ce4f9c` | frozen-only bounded Restore、全局 0-call preflight、pending suffix、R1/R2 raw-head drift均为 Retryable |
| R2 online lifecycle | `6a4f49ca`、`9d911d2c`、`8e3268e0` | governing setup唯一 ordinal、最多 4 select / 2 restore / 1 Run、public concrete composition |
| R2 fidelity/recovery | `22d37593`、`dd984285`、`8a5ea29f` | byte-identical envelope ETag、frozen plan不变、Prepared删 Store reopen、Published Restore进程崩溃恢复 |

最终验收保持了以下简化边界：

- `DerivedRecapRestoreExecutor.RestoreAsync(anchor, expectedRawHead)`返回独立 typed result；不存在
  persisted RestorePlan、后台 repair job 或 phase-neutral万能 workflow；
- R2 historical实现中，`RecapPlannerConfig`在 Restore只提供 route/call/raw execution
  ceilings，不提供 roster、catalog、trigger或 policy authority；Post-R3 cutover进一步要求
  Restore完全不接收 active config，只使用 frozen plan + `RecapProtocolHardCaps`；
- block replace 后、envelope前 crash只留下可复用 pending；exact Published membership和 strict
  ordinal不变，重试不重复 Maintainer调用；
- public coordinator同时实现 lifecycle与 candidate facade，但 neutral candidate contract不暴露
  repair-private anchor，也不承担 repair；
- R2没有引入 scrub、scheduler、recursive source repair、migration、CLI或旧 DerivedMemory cutover。
  CLI、Host cutover与旧 DerivedMemory删除进入 R3；migration、full scrub、scheduler与 recursive
  repair继续 deferred，且不属于 R3。

## 4. R3：Cutover、CLI 与真实验收

### Intent

把 `SessionJournal.Cli` 的 production composition切到 DerivedRecap，删除 current
DerivedMemory，并用真实 legacy export 导入得到的 current-wire 隔离 repo证明替换完成。

这里的 Host 只指当前真正组合 `SessionJournal` 的 executable
`SessionJournal.Cli`。仍使用 `ChatSessionEngine` 的 Galatea/FamilyChat Server迁移不属于
R3。

### R3 plan lock

R3按以下顺序交付；前一工作包的 focused gate通过后才进入后一工作包：

1. **R3A Policy facts + bounded baseline policy**
   - `RecapPolicyFacts`显式携带 first-build 的
     `EmptyReplayStartExclusive`，以及每个 available block source 的 exact
     `SourceIntent + AbsorbedThrough`；
   - policy仍只读取 header/cursor facts，不读取 raw payload；
   - `RecapPlanningPolicyDecision`增加 typed `Unavailable`，用于表达“已触发，但不存在预算内
     合法 admission/route”，不得伪装成 `NoBuild`；
   - 首个 production policy为 deterministic `MaintainAll`：所有 catalog block都更新，
     prior context固定 empty；按 lineage顺序和 replay-safe boundaries生成最短 greedy
     route，选择最新的预算内 admission；
   - 不猜 relevance，不自动 `Inherit`，不引入 Tag/LLM policy。
2. **R3B Exact Building quarantine**
   - Store只增加 exact unpublished Building quarantine；
   - 在 per-ref writer lock内把一个 Building原子 rename到 Store-owned quarantine目录；
   - Published同 anchor存在时拒绝，missing幂等返回；不得借用 whole-Store `ResetAsync`。
3. **R3C Recap operator CLI**
   - 增加 `recap create/inspect/run/resume/restore/abandon-building/reset`命令族；
   - `run`就是 bounded plan-or-resume-and-publish；不增加没有 durable authority的
     dry-plan命令；
   - `create/reset`只表达 Store初始化/整根隔离重置；真正 catch-up由一次或多次显式
     `run`完成，不提供会在崩溃重试时再次 reset的“一键 rebuild”；
   - inspect/report只输出 address、state、typed result/defect，不输出 recap正文、
     frozen prompt或 provider secret；
   - report输出继续使用同目录临时文件、symlink/reparse ancestor拒绝和 atomic
     publication。
4. **R3D Online cutover**
   - `run-online-turn`先打开 raw SessionJournal并 inspect phase；
   - `Prepared/Started` recovery只组合 agent completion runtime，不要求 message，不打开、
     创建或修复 Recap Store；
   - 只有需要新 request的 phase才组合 Store + Planner + Maintainers + candidate/lifecycle；
   - Store缺失不得被 online路径静默 create/reset。
5. **R3E Old subsystem deletion**
   - 删除 current `SessionJournal.DerivedMemory` production/test projects和旧 CLI命令；
   - 更新 solution、project references、active docs、InternalsVisibleTo与 architecture guards；
   - persisted maintainer identity/resource logical name若仍是 canonical wire identity，不因
     assembly删除而改名。
6. **R3F Real acceptance**
   - mandatory gate使用真实 legacy export导入 current-wire 隔离 repo，并使用 scripted
     completion/maintainer，不依赖网络；
   - optional real LLM smoke独立运行，不作为 deterministic release gate。

### R3 完成记录（2026-07-30）

| 包 | 状态 | 实施证据 | 结果 |
|---|---|---|---|
| R3A Policy facts + bounded baseline | 完成 | `5dcdb142`、`bed40990` | production `MaintainAll` policy、typed unavailable 与 first-build replay seed 收口 |
| R3B Exact Building quarantine | 完成 | `e8c62e4d` | exact unpublished Building quarantine，不借 whole-Store reset |
| R3C Recap operator CLI | 完成 | `8804f96b`、`6fa52e6a`、`3e7c7666`、`f7b0e39f`、`ac15ff49` | create/inspect/run/resume/restore/abandon/reset、content-free report 与 path/readiness preflight |
| R3D Online cutover | 完成 | `8ee323b3`、`235be95c` | phase-first composition；lifecycle保持 engine-owned；Prepared/Started recovery不打开 Recap Store |
| R3E Old subsystem deletion | 完成 | `df8e3044`、`4f518f6b` | 删除旧 DerivedMemory production/tests/CLI surface，更新 solution/refs、active docs 与 architecture guard |
| R3F Real acceptance | 完成 | `637e1c6d`、`d95e2594`、`b2f17a32` | 真实 legacy export隔离导入；中断续跑、exact损坏恢复、online suffix与 Prepared删 v4 recovery |

R3 tail review发现并关闭了三项会削弱结论的问题：

- lifecycle最初被 CLI 与 engine各调用一次；最终只允许 engine拥有一次 bounded lifecycle；
- real-data test未配置 source时最初会空跑成 Passed；最终通过 conditional Fact明确报告 Skipped，
  release runner则在 shell层强制 source/report参数；
- partial failure最初发生在失败 block首个 endpoint；最终改为已有 rolling checkpoint后失败，
  并断言 resume request的 canonical hash等于失败 suffix request。

现存 historical SessionJournal directories使用已退休 wire，不能由 current codec直接 reopen。
R3F没有伪造兼容层，而是选择仓库内真实
`family-chat-legacy-upgrade/cyber.json`，先走 production `import-legacy-json`形成 124-event
current-wire baseline，再开始 Recap gate。existing current-wire repo copy模式可在出现此类 fixture后
增补；它不是 V4 cutover correctness的前置条件。

R3A对既有 ceilings的解释保持保守：

- 以下是 R3 historical baseline；Post-R3由
  [Derived Recap Cadence](derived-recap-cadence-target-design.md)取代其 scheduling语义：
- `RawGrowthHardLimit`是 policy前的总 backlog admission gate；fresh bootstrap必须显式配置到
  足以覆盖当前 raw lineage，否则返回 typed backpressure，不自动 reset；
- `MaxRawEventsPerBuild`按每个 maintained block 的 replay window累加；
- greedy route只能在 replay-safe boundary上切分；单段 boundary gap超过
  `MaxRawEventsPerStep`时返回 typed unavailable；
- event-count ceiling不等于 provider token/byte ceiling。R3不伪称解决通用 token packing；
  production配置应使用保守 step limit，real LLM smoke记录实际 request规模，后续单独设计
  provider-aware request budget。

### In scope

- production `MaintainAll` policy与其所需的 exact cursor facts；
- `SessionJournal.Cli`只组合 Store + Planner + Maintainers；
- exact Building/Published inspect、bounded run/exact resume、exact restore、exact unpublished
  Building quarantine、explicit Store create/reset、online turn；
- 删除 current DerivedMemory production/test projects；
- 更新 solution、active docs 与 architecture guards；
- 删除 active target/runtime 的 ArtifactSet/DerivedMemory V4 naming；
- 保留 done/superseded/current baseline 的历史名称。

### Out of scope

- v2/v3 migration、fallback、dual read/write；
- full scrub CLI；
- broad repair console；
- future dynamic Memory；
- multi-process writer。
- Galatea/FamilyChat Server从 ChatSession迁移到 SessionJournal；
- generic dry-plan/exact-plan authoring CLI；
- one-shot reset-and-rebuild命令；
- provider-aware token packing与自动 profile relevance判断。

### Structure scan

```text
rg "MaintenanceJob|RoleAttempt|RoleSettlement|Finalization|JobReprovision"
rg "PreviousSetId|latest pointer|PolicyFingerprint"
rg "DerivedArtifactSet|DerivedMemoryBlock|SourceRawHead"
rg "SessionJournal\\.DerivedMemory"
rg "MemoryPack|IMemoryBlockMaintainer|ISessionMemoryLifecycleCoordinator"
rg "derived/memory/v2|derived/memory/v3"
```

允许：

- done/superseded/current baseline 文档；
- v2/v3 historical paths；
- migration/export-only references。

不允许：

- EADR V4 current/runtime 继续使用宽泛 Memory 或 ArtifactSet作为 Recap 领域名；
- current/target contract 双表面并存。

### Final validation

- Store/Planner/Maintainers/SessionJournal/CLI focused tests；
- solution build；
- relative Markdown link scan；
- `git diff --check`；
- 一份真实 legacy-upgrade export，经 production importer形成隔离 current-wire repo：

```text
hash real export
  -> import isolated current-wire repo
  -> record raw full-file hash + semantic fingerprint
  -> create Store
  -> bounded run
  -> partial rolling progress
  -> reopen
  -> publish
  -> strict select
  -> corrupt exact component
  -> not-ready
  -> restore
  -> online completion
  -> Prepared reopen after Store deletion
```

Mandatory scripted acceptance还必须证明：

- source fixture从不原地修改；旧 `derived/memory/v1`不读取也不删除；
- partial failure后 healthy block不重复调用，失败 block只补 missing suffix；
- exact selected block损坏后仍返回同一 `Selected(descriptor)`，随后 production
  `materialize-inspect`返回 `Invalid/MaterializationInvalid`；same-slot `Restore`后再次
  materialize必须成功；neighbor non-fallback由多 ordinal Store focused test独立证明；
- Store/build/corrupt/restore期间 raw full-file hash、head、semantic fingerprint不变；
- online append保留旧 lineage prefix，且只新增预期的 SessionJournal suffix；
- `CompletionRequestPrepared`后保存 canonical request bytes/hash，删除
  `derived/recap/v4`再 reopen仍得到同一 request，且不触碰 Recap lifecycle；
- acceptance report记录 source identity、policy/config、admission/route/call counts、
  corruption target、restore result、Prepared request hash与最终 raw prefix hash。

real fixture位于 `gitignore`时，runner通过
`ATELIA_REAL_LEGACY_UPGRADE_EXPORT`接受显式 source path；未配置时该 external test明确
Skipped，不能显示为 Passed。release gate在运行 test前还必须要求
`ATELIA_DERIVED_RECAP_ACCEPTANCE_REPORT`存在。真实 provider smoke为 opt-in，只 gate结构合法与
流程成功，不 gate生成文本。

R3F pre-B2 historical record（以下选择结果保留为当时语义，不代表当前契约）：

- source：`cyber.json`，1,112,223 bytes，SHA-256
  `98375378f32239eb3aafdf60d40a650c3c2a96fc3e4140698e0dfd934d9920ea`；
- imported baseline：124 addresses；2 maintained blocks / 4 frozen route endpoints；
- call trace：run第 4 次故障，resume 1 次且 canonical request hash与失败 suffix一致；
- exact block corruption得到 `ExactPublishedSetInvalid`；Restore复用 checkpoint，0 provider call；
- online + Prepared recovery精确追加
  `ObservationAccepted → CompletionRequestPrepared → CompletionAttemptStarted →
  AgentActionProduced` 两组共 8 events，原 124-address prefix保持；
- 隔离副本内 invalid historical v1 sentinel保持 byte-identical；source与
  build/restore前 raw full-tree fingerprint保持不变；Prepared删除整个 v4后恢复仍不重建 v4。

当前 B2 语义与验收证据改为：strict ordinal metadata selection在 block payload损坏后仍返回
原 `Selected(descriptor)`；production `materialize-inspect`随后返回
`Invalid/MaterializationInvalid`；same-slot `Restore`以 0 provider call恢复损坏 block且不改
healthy sibling；再次 materialize返回 `Selected`。current acceptance report从 v2直接升级为
v3，不提供compat reader/writer。

release gate不能只依赖 test内条件，runner先强制外部参数，再执行 exact test：

```bash
: "${ATELIA_REAL_LEGACY_UPGRADE_EXPORT:?required}"
: "${ATELIA_DERIVED_RECAP_ACCEPTANCE_REPORT:?required}"
test -f "$ATELIA_REAL_LEGACY_UPGRADE_EXPORT"

dotnet test \
  tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj \
  -m:1 -nr:false --no-restore \
  --filter 'FullyQualifiedName~DerivedRecapRealDataAcceptanceTests'
```

最终 gates：

- Store 89/89、Planner 87/87、Maintainers 18/18、SessionJournal 313/313；
- CLI normal suite 44 passed + 1 external real-data gate Skipped；带显式 source的 real-data gate
  1/1；
- SessionJournal.Offline 5/5；
- `Atelia.sln` build 0 warnings / 0 errors；
- active SessionJournal Markdown relative links、retired project absence、architecture guards与
  `git diff --check`均通过。

本次 report写入
`gitignore/session-journal/derived-recap-r3-acceptance-20260730.json`（不进 git），report
SHA-256 为 `4f33e9fb3cc63accaecd9d6300a68f54e41fdf7d4660cdcc69ae5f4ca4c79c8c`。

## 5. 完成定义

- Recap/Memory/Context边界进入 canonical docs和target symbols；
- 四个 assemblies职责单向；
- Building/Published只有一个原子 boundary；
- source/admission/cursor如实；
- endpoint-only route与rolling checkpoint可用；
- strict exact ordinal可用；
- Published bounded Restore可用；
- Prepared exact reopen不依赖 Recap Store；
- historical DerivedMemory transaction workflow从 production tree删除；
- future dynamic Memory、full scrub和advanced self-heal保持 deferred。

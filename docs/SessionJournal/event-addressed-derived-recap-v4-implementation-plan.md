# SessionJournal EADR V4：实现与替换计划

> **状态**：Implementation Plan
> **日期**：2026-07-30
> **核心概念**：
> [EADR 核心概念](event-addressed-derived-recap-concepts.md)
> **目标设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)
> **兼容策略**：不迁移、不双写、不读取 current DerivedMemory v2/v3
> **当前推进点**：R0 Contracts + Publish/Read vertical 已完成；R1 Planner +
> Build/Resume vertical 已完成 package-local plan lock，正在实施 R1A Store substrate

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

- `RecapPlannerConfig`：
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
- `EnsureFinalRecapBlock`；
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
| **R1B Planner + executor** | runtime `RecapPlannerConfig`、ordered active catalog、pure injectable deterministic policy、`NoBuild / PlanReady / Unavailable`；exact-head admission/route/prior validation；`EnsureFinalRecapBlock`；Maintain/Inherit；plan-or-resume→R0 Publisher | below-trigger 0 copy/0 call；limit backpressure 0 call；Inherit 0 call；Maintain unchanged content仍推进；healthy final skip；checkpoint只补 suffix |
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

`EnsureFinalRecapBlock` 顺序执行 blocks：Inherit exact-copy local input并保持 cursor；Maintain从
source cursor/previous endpoint唯一推导下一 start。每步即使正文不变也先以 endpoint更新并
durable replace rolling；final endpoint checkpoint成功后才安装 final。因此 checkpoint与 final
之间 crash，reopen可以 0 次 Maintainer调用安装 final。

R1 明确不实现 Published Restore、exact-invalid online self-heal、recursive source repair、
per-step prior context、checkpoint chain、Tag/LLM relevance policy、background scrub、CLI/R3
cutover或 current DerivedMemory compatibility bridge。

## 3. R2：Exact-slot Restore + Online lifecycle

### Intent

把 Published structural defects转成一个 bounded RestorePlan，并闭合 exact invalid-slot
not-ready → Restore → reselect，同时不建立全库 self-heal system。

### In scope

- Store `StructuralDefects[]`；
- Planner：

```text
TryCreateRestorePlan
  -> RestorePlan
  | RestoreUnavailable(reason)
```

- Published authority只来自 `publication.FrozenPlanSnapshot`；
- Resume/Restore共用 `EnsureFinalRecapBlock`，外层 phase不合并；
- frozen plan exact不变；
- component atomic replace + publication envelope last；
- pending replacement复用；
- envelope-loss restore：
  healthy manifest cache + frozen inputs + final blocks → full revalidation → new envelope；
- inputs/work missing只影响 Restore capability；
- exact selected-slot on-demand Restore；
- lifecycle：

```text
inspect/select
  -> Selected: materialize
  -> ExactPublishedSetInvalid:
       bounded RestorePlan -> execute -> reselect
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
raw growth
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

## 4. R3：Cutover、CLI 与真实验收

### Intent

把 production composition切到 DerivedRecap，删除 current DerivedMemory，并用真实 repo证明替换完成。

### In scope

- Host/CLI只组合 Store + Planner + Maintainers；
- 最小命令：
  - inspect exact Building/Published；
  - plan/run/resume Building；
  - restore exact Published set；
  - quarantine unpublished Building；
  - explicit Store rebuild；
  - run online turn；
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

- EADR V4 target/runtime 继续使用宽泛 Memory 或 ArtifactSet作为 Recap 领域名；
- current/target contract 双表面并存。

### Final validation

- Store/Planner/Maintainers/SessionJournal/CLI focused tests；
- solution build；
- relative Markdown link scan；
- `git diff --check`；
- 一个真实 SessionJournal repo：

```text
Create/rebuild Store
  -> plan
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

## 5. 完成定义

- Recap/Memory/Context边界进入 canonical docs和target symbols；
- 四个 assemblies职责单向；
- Building/Published只有一个原子 boundary；
- source/admission/cursor如实；
- endpoint-only route与rolling checkpoint可用；
- strict exact ordinal可用；
- Published bounded Restore可用；
- Prepared exact reopen不依赖 Recap Store；
- current DerivedMemory transaction workflow从 production tree删除；
- future dynamic Memory、full scrub和advanced self-heal保持 deferred。
